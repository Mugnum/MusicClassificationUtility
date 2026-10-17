from __future__ import annotations

import argparse
import contextlib
import datetime as dt
import json
import os
import platform
import sys
import time
import traceback
import uuid
from pathlib import Path

from . import __version__
from .analysis import analyze_file, discover
from .runtime import AnalysisError, PROJECT_ROOT, executable, read_manifest, runtime_directory, source_directory, model_directory


def progress(message: str) -> None:
    print(message, file=sys.stderr, flush=True)


def parser() -> argparse.ArgumentParser:
    result = argparse.ArgumentParser(description="Offline native-Windows MAEST genre analysis.")
    commands = result.add_subparsers(dest="command", required=True)
    check = commands.add_parser("check", help="Inspect the local runtime; optionally load the model.")
    check.add_argument("--load-model", action="store_true")
    check.add_argument("--threads", type=int, default=8)
    check.add_argument("--ffmpeg")
    check.add_argument("--ffprobe")
    worker = commands.add_parser("worker", help="Serve sequential JSON-lines analysis requests for the GUI.")
    worker.add_argument("--threads", type=int, default=8)
    worker.add_argument("--ffmpeg")
    worker.add_argument("--ffprobe")
    analyze = commands.add_parser("analyze", help="Analyze files or folders and save JSON/text results.")
    analyze.add_argument("paths", nargs="+")
    analyze.add_argument("--recursive", action="store_true")
    analyze.add_argument("--top", type=int, default=10)
    analyze.add_argument("--segments", action="store_true", help="Also print section results in the text output.")
    analyze.add_argument("--json", action="store_true", help="Emit one JSON document to stdout; progress stays on stderr.")
    analyze.add_argument("--output-dir", type=Path, default=runtime_directory() / "results")
    analyze.add_argument("--threads", type=int, default=8)
    analyze.add_argument("--ffmpeg")
    analyze.add_argument("--ffprobe")
    analyze.add_argument("--debug", action="store_true")
    return result


def text_report(report: dict, show_segments: bool) -> str:
    lines = [
        "MAEST music genre analysis",
        f"Run: {report['run_id']}",
        "Scores are multi-label model outputs, not calibrated confidence percentages.",
        "Overall scores use an average weighted by each window's original duration.",
        "",
    ]
    lines += ["SONG SUMMARY"]
    for item in report["files"]:
        styles = list(dict.fromkeys(genre["label"].split("---", 1)[-1] for genre in item["overall"]))[:3]
        lines.append(f"{Path(item['file']).name} | {' · '.join(styles)} | Done")
    lines += ["", "DETAILED RESULTS", ""]
    for item in report["files"]:
        lines += [f"File: {item['file']}", f"Duration: {item['duration_seconds']:.3f}s | "
                  f"Windows: {len(item['segments'])} | Analysis: {item['analysis_seconds']:.2f}s", "Overall:"]
        for genre in item["overall"]:
            lines.append(f"  {genre['score']:.4f}  {genre['label']}")
        for warning in item["warnings"]:
            lines.append(f"Warning: {warning}")
        if show_segments:
            lines.append("Sections:")
            for segment in item["segments"]:
                lines.append(f"  {segment['start_seconds']:.3f}-{segment['end_seconds']:.3f}s "
                             f"(repeated padding: {segment['repeated_padding_seconds']:.3f}s)")
                for genre in segment["top_genres"]:
                    lines.append(f"    {genre['score']:.4f}  {genre['label']}")
        lines.append("")
    for failure in report["errors"]:
        lines += [f"ERROR: {failure['file']}", f"  {failure['message']}", ""]
    lines.append(f"Succeeded: {len(report['files'])} | Failed inputs: {len(report['errors'])}")
    if report.get("output_files"):
        lines += [f"JSON: {report['output_files']['json']}", f"Text: {report['output_files']['text']}"]
    return "\n".join(lines) + "\n"


def save_report(report: dict, directory: Path, show_segments: bool) -> tuple[str, str]:
    directory = directory.expanduser().resolve()
    directory.mkdir(parents=True, exist_ok=True)
    stem = directory / f"analysis-{report['run_id']}"
    json_path = stem.with_suffix(".json")
    text_path = stem.with_suffix(".txt")
    report["output_files"] = {"json": str(json_path), "text": str(text_path)}
    json_text = json.dumps(report, ensure_ascii=False, indent=2, allow_nan=False)
    human_text = text_report(report, show_segments)
    for path, content in ((json_path, json_text + "\n"), (text_path, human_text)):
        temporary = path.with_suffix(path.suffix + ".tmp")
        try:
            temporary.write_text(content, encoding="utf-8")
            os.replace(temporary, path)
        finally:
            temporary.unlink(missing_ok=True)
    return json_text, human_text


def main(argv: list[str] | None = None) -> int:
    args_parser = parser()
    args = args_parser.parse_args(argv)
    if not 1 <= args.threads <= 64:
        args_parser.error("--threads must be between 1 and 64")
    if args.command == "analyze" and not 1 <= args.top <= 519:
        args_parser.error("--top must be between 1 and 519")
    try:
        ffmpeg = executable("ffmpeg", args.ffmpeg)
        ffprobe = executable("ffprobe", args.ffprobe)
        if args.command == "worker":
            from .worker import serve
            return serve(ffmpeg, ffprobe, args.threads)
        if args.command == "check":
            import importlib.metadata

            manifest = read_manifest()
            source = source_directory()
            model = model_directory() / manifest["model"]["filename"]
            status = {
                "python": sys.version,
                "python_executable": sys.executable,
                "platform": platform.platform(),
                "ffmpeg": ffmpeg,
                "ffprobe": ffprobe,
                "packages": {name: importlib.metadata.version(name) for name in ("torch", "torchaudio", "timm", "numpy", "sacred")},
                "source_present": (source / "models" / "maest.py").is_file(),
                "checkpoint_present": model.is_file(),
            }
            if args.load_model:
                from .maest_backend import MaestBackend

                with contextlib.redirect_stdout(sys.stderr):
                    backend = MaestBackend(args.threads, progress)
                status["model"] = backend.info
            print(json.dumps(status, ensure_ascii=False, indent=2))
            return 0 if status["source_present"] and status["checkpoint_present"] else 1

        files, input_errors = discover(args.paths, args.recursive)
        now = dt.datetime.now(dt.timezone.utc)
        report = {
            "schema_version": 1,
            "run_id": now.strftime("%Y%m%d-%H%M%S") + "-" + uuid.uuid4().hex[:8],
            "created_at_utc": now.isoformat(),
            "application_version": __version__,
            "settings": {
                "sample_rate": 16000,
                "window_seconds": 30,
                "tail_policy": "ignore-under-10s-otherwise-repeat-to-full-window",
                "minimum_segment_seconds": 10,
                "aggregation": "mean-weighted-by-original-window-duration",
                "top": args.top,
                "threads": args.threads,
                "device": "cpu",
            },
            "model": None,
            "files": [],
            "errors": input_errors,
        }
        started = time.perf_counter()
        if files:
            from .maest_backend import MaestBackend

            with contextlib.redirect_stdout(sys.stderr):
                backend = MaestBackend(args.threads, progress)
            report["model"] = {**backend.info, "labels": backend.labels}
            for index, path in enumerate(files, 1):
                progress(f"[{index}/{len(files)}] {path}")
                try:
                    with contextlib.redirect_stdout(sys.stderr):
                        item = analyze_file(path, backend, ffmpeg, ffprobe, args.top, progress)
                    report["files"].append(item)
                    progress(f"  Done in {item['analysis_seconds']:.2f}s")
                except Exception as exc:
                    report["errors"].append({"file": str(path), "message": str(exc)})
                    progress(f"  ERROR: {exc}")
                    if args.debug:
                        traceback.print_exc(file=sys.stderr)
        report["elapsed_seconds"] = time.perf_counter() - started
        json_text, human_text = save_report(report, args.output_dir, args.segments)
        print(json_text if args.json else human_text, end="\n" if args.json else "", flush=True)
        return 1 if report["errors"] else 0
    except KeyboardInterrupt:
        progress("Analysis cancelled.")
        return 130
    except Exception as exc:
        progress(f"ERROR: {exc}")
        if getattr(args, "debug", False):
            traceback.print_exc(file=sys.stderr)
        return 2
