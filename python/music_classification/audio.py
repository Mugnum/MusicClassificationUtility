from __future__ import annotations

import json
import subprocess
import tempfile
from dataclasses import dataclass
from pathlib import Path
from typing import Iterator

import numpy as np

from .runtime import AnalysisError, runtime_directory

SAMPLE_RATE = 16000
WINDOW_SECONDS = 30
WINDOW_SAMPLES = SAMPLE_RATE * WINDOW_SECONDS
CREATE_NO_WINDOW = getattr(subprocess, "CREATE_NO_WINDOW", 0)


@dataclass(frozen=True)
class AudioWindow:
    start_sample: int
    samples: np.ndarray

    @property
    def valid_samples(self) -> int:
        return len(self.samples)

    @property
    def end_sample(self) -> int:
        return self.start_sample + self.valid_samples


def probe(path: Path, ffprobe: str) -> dict:
    try:
        result = subprocess.run(
            [ffprobe, "-v", "error", "-protocol_whitelist", "file,pipe,data", "-select_streams", "a:0", "-show_entries",
             "stream=codec_name,sample_rate,channels,duration:format=duration", "-of", "json", str(path)],
            capture_output=True, timeout=30, creationflags=CREATE_NO_WINDOW,
        )
    except (OSError, subprocess.TimeoutExpired) as exc:
        raise AnalysisError(f"Could not inspect audio: {exc}") from exc
    if result.returncode:
        raise AnalysisError(result.stderr.decode("utf-8", errors="replace").strip()[-2000:] or "FFprobe failed.")
    try:
        data = json.loads(result.stdout)
        streams = data.get("streams", [])
        if not streams:
            raise AnalysisError("The file contains no decodable audio stream.")
        stream = streams[0]
        duration = None
        for value in (stream.get("duration"), data.get("format", {}).get("duration")):
            try:
                candidate = float(value)
                if np.isfinite(candidate) and candidate > 0:
                    duration = candidate
                    break
            except (TypeError, ValueError):
                pass
        return {
            "codec": stream.get("codec_name"),
            "sample_rate": int(stream["sample_rate"]) if stream.get("sample_rate") else None,
            "channels": stream.get("channels"),
            "reported_duration_seconds": duration,
        }
    except (ValueError, KeyError, TypeError) as exc:
        raise AnalysisError(f"Invalid FFprobe response: {exc}") from exc


def decode_windows(path: Path, ffmpeg: str) -> Iterator[AudioWindow]:
    """Stream bounded chunks of mono float32 audio; stderr cannot fill a pipe."""
    scratch = runtime_directory() / "cache"
    scratch.mkdir(parents=True, exist_ok=True)
    command = [
        ffmpeg, "-nostdin", "-hide_banner", "-loglevel", "error",
        "-protocol_whitelist", "file,pipe,data", "-i", str(path),
        "-map", "0:a:0", "-vn", "-sn", "-dn", "-ac", "1", "-ar", str(SAMPLE_RATE),
        "-c:a", "pcm_f32le", "-f", "f32le", "pipe:1",
    ]
    process = None
    start_sample = 0
    try:
        with tempfile.TemporaryFile(dir=scratch) as errors:
            process = subprocess.Popen(
                command, stdout=subprocess.PIPE, stderr=errors, stdin=subprocess.DEVNULL,
                creationflags=CREATE_NO_WINDOW,
            )
            assert process.stdout is not None
            while True:
                blocks = bytearray()
                while len(blocks) < WINDOW_SAMPLES * 4:
                    block = process.stdout.read(WINDOW_SAMPLES * 4 - len(blocks))
                    if not block:
                        break
                    blocks.extend(block)
                if not blocks:
                    break
                if len(blocks) % 4:
                    raise AnalysisError("FFmpeg returned a truncated float32 audio sample.")
                samples = np.frombuffer(blocks, dtype="<f4").copy()
                if not np.isfinite(samples).all():
                    raise AnalysisError("Decoded audio contains non-finite samples.")
                yield AudioWindow(start_sample, samples)
                start_sample += len(samples)
            code = process.wait(timeout=30)
            if code:
                errors.seek(0, 2)
                errors.seek(max(0, errors.tell() - 4000))
                message = errors.read(4000).decode("utf-8", errors="replace").strip()[-2000:]
                raise AnalysisError(message or f"FFmpeg failed with exit code {code}.")
            if start_sample == 0:
                raise AnalysisError("The file decoded to zero audio samples.")
    except OSError as exc:
        raise AnalysisError(f"Audio decoding failed: {exc}") from exc
    finally:
        if process is not None:
            if process.poll() is None:
                process.terminate()
                try:
                    process.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait(timeout=5)
            if process.stdout is not None:
                process.stdout.close()


def model_audio(window: AudioWindow) -> np.ndarray:
    if window.valid_samples <= 0 or window.valid_samples > WINDOW_SAMPLES:
        raise AnalysisError("An analysis window must contain between 1 sample and 30 seconds.")
    if window.valid_samples == WINDOW_SAMPLES:
        return window.samples
    # Keep all trailing audio. Padding is disclosed and its length is excluded
    # from the duration weights used for the track-level aggregate.
    return np.resize(window.samples, WINDOW_SAMPLES).astype(np.float32, copy=False)
