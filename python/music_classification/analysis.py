from __future__ import annotations

import time
from pathlib import Path
from typing import Callable, Protocol

import numpy as np

from .audio import SAMPLE_RATE, WINDOW_SAMPLES, decode_windows, model_audio, probe
from .runtime import AnalysisError

AUDIO_SUFFIXES = frozenset({
    ".aac", ".aif", ".aiff", ".alac", ".ape", ".au", ".dff", ".dsf", ".flac",
    ".m4a", ".m4b", ".mka", ".mp2", ".mp3", ".mp4", ".ogg", ".opus", ".tak",
    ".tta", ".wav", ".wave", ".webm", ".wma", ".wv",
})

MINIMUM_WINDOW_SECONDS = 10


class Predictor(Protocol):
    labels: list[str]

    def predict(self, samples: np.ndarray) -> np.ndarray: ...


def discover(paths: list[str], recursive: bool = False) -> tuple[list[Path], list[dict]]:
    import os

    files: list[Path] = []
    errors: list[dict] = []
    seen: set[str] = set()
    for item in paths:
        path = Path(item).expanduser().resolve()
        if path.is_file():
            candidates = [path]  # Explicit files are probed regardless of extension.
        elif path.is_dir():
            entries = path.rglob("*") if recursive else path.iterdir()
            candidates = sorted(
                (entry for entry in entries if entry.is_file() and entry.suffix.lower() in AUDIO_SUFFIXES),
                key=lambda entry: str(entry).casefold(),
            )
            if not candidates:
                errors.append({"file": str(path), "message": "No supported audio files found in this folder."})
        else:
            errors.append({"file": str(path), "message": "Path does not exist or is not a regular file/folder."})
            continue
        for candidate in candidates:
            key = os.path.normcase(str(candidate.resolve()))
            if key not in seen:
                files.append(candidate)
                seen.add(key)
    return files, errors


def top_genres(scores: np.ndarray, labels: list[str], count: int) -> list[dict]:
    order = np.argsort(-scores, kind="stable")[:count]
    return [{"label": labels[int(index)], "score": float(scores[index])} for index in order]


def aggregate(scores: list[np.ndarray], durations: list[int]) -> np.ndarray:
    if not scores or len(scores) != len(durations) or any(value <= 0 for value in durations):
        raise AnalysisError("Cannot aggregate empty or inconsistent analysis windows.")
    return np.average(np.stack(scores).astype(np.float64), axis=0, weights=durations)


def analyze_file(
    path: Path, backend: Predictor, ffmpeg: str, ffprobe: str, top: int = 10,
    progress: Callable[[str], None] = lambda _: None,
    events: Callable[[dict], None] = lambda _: None,
) -> dict:
    started = time.perf_counter()
    media = probe(path, ffprobe)
    events({"type": "metadata", "duration_seconds": media["reported_duration_seconds"]})
    segments: list[dict] = []
    scores: list[np.ndarray] = []
    durations: list[int] = []
    warnings: list[str] = []
    decoded_samples = 0
    ignored_tail_seconds = 0.0
    for window in decode_windows(path, ffmpeg):
        start = window.start_sample / SAMPLE_RATE
        end = window.end_sample / SAMPLE_RATE
        decoded_samples += window.valid_samples
        if window.valid_samples < MINIMUM_WINDOW_SECONDS * SAMPLE_RATE:
            ignored_tail_seconds = window.valid_samples / SAMPLE_RATE
            progress(f"  Ignored final {ignored_tail_seconds:.3f}s (less than {MINIMUM_WINDOW_SECONDS}s).")
            events({"type": "progress", "completed_seconds": end,
                    "total_seconds": media["reported_duration_seconds"], "percent": 99.0,
                    "windows_completed": len(segments), "message": "Ignoring short final segment"})
            continue
        progress(f"  {start:7.2f}-{end:7.2f}s")
        prediction = np.asarray(backend.predict(model_audio(window)), dtype=np.float64)
        if prediction.shape != (len(backend.labels),) or not np.isfinite(prediction).all():
            raise AnalysisError("The model returned an invalid prediction vector.")
        if np.any(prediction < 0) or np.any(prediction > 1):
            raise AnalysisError("The model returned scores outside the sigmoid range [0, 1].")
        padding = (WINDOW_SAMPLES - window.valid_samples) / SAMPLE_RATE
        rms = float(np.sqrt(np.mean(window.samples.astype(np.float64) ** 2)))
        segments.append({
            "start_seconds": start,
            "end_seconds": end,
            "valid_duration_seconds": window.valid_samples / SAMPLE_RATE,
            "repeated_padding_seconds": padding,
            "rms": rms,
            "limited_source_context": window.valid_samples < 5 * SAMPLE_RATE,
            "top_genres": top_genres(prediction, backend.labels, top),
            "scores": prediction.tolist(),
        })
        scores.append(prediction)
        durations.append(window.valid_samples)
        expected = media["reported_duration_seconds"]
        events({"type": "progress", "completed_seconds": end,
                "total_seconds": expected,
                "percent": min(99.0, end / expected * 100) if expected else None,
                "windows_completed": len(segments)})
    if not scores:
        raise AnalysisError(f"No eligible audio segment: at least {MINIMUM_WINDOW_SECONDS} seconds of decoded audio is required.")
    combined = aggregate(scores, durations)
    analyzed_duration = sum(durations) / SAMPLE_RATE
    duration = decoded_samples / SAMPLE_RATE
    if segments[-1]["repeated_padding_seconds"]:
        warnings.append(
            f"The final {segments[-1]['valid_duration_seconds']:.3f}s window was repeated to fill 30s; "
            "only its original duration contributes to the overall average."
        )
        if segments[-1]["limited_source_context"] and len(segments) > 1:
            warnings.append("The final window has less than 5 seconds of original audio; its section labels have limited context.")
    if duration < 5:
        warnings.append("Less than 5 seconds of source audio: genre results may be uninformative.")
    if all(segment["rms"] < 1e-5 for segment in segments):
        warnings.append("The decoded audio is silent or nearly silent; genre scores are not meaningful.")
    return {
        "file": str(path),
        "source": media,
        "duration_seconds": duration,
        "analyzed_duration_seconds": analyzed_duration,
        "ignored_tail_seconds": ignored_tail_seconds,
        "analysis_seconds": time.perf_counter() - started,
        "overall": top_genres(combined, backend.labels, top),
        "overall_scores": combined.tolist(),
        "segments": segments,
        "warnings": warnings,
    }
