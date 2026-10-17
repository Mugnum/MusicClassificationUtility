"""Versioned JSON-lines transport. All classifier diagnostics go to stderr."""

import contextlib
import json
import sys
from pathlib import Path

from .analysis import analyze_file
from .maest_backend import MaestBackend


def emit(message):
    print(json.dumps(message, ensure_ascii=False, allow_nan=False), flush=True)


def serve(ffmpeg, ffprobe, threads):
    emit({"type": "stage", "message": "Loading model"})
    with contextlib.redirect_stdout(sys.stderr):
        backend = MaestBackend(threads)
    emit({"type": "ready", "protocol_version": 1, "model": {**backend.info, "labels": backend.labels}})
    for line in sys.stdin:
        request_id = None
        try:
            request = json.loads(line)
            request_id = request.get("id")
            if not isinstance(request_id, str) or request.get("type") != "analyze":
                raise ValueError("Expected an analyze request with a string id.")
            path = Path(request["path"]).resolve(strict=True)
            if not path.is_file():
                raise ValueError("The input is not a regular file.")
            # Redirect noisy upstream output, but write protocol events through
            # the original stdout so no diagnostic text corrupts the transport.
            protocol_stdout = sys.stdout
            def event(value):
                value = {**value, "id": request_id}
                print(json.dumps(value, ensure_ascii=False, allow_nan=False), file=protocol_stdout, flush=True)
            with contextlib.redirect_stdout(sys.stderr):
                result = analyze_file(path, backend, ffmpeg, ffprobe, top=10, events=event)
            emit({"type": "result", "id": request_id, "result": result})
        except Exception as exc:
            emit({"type": "error", "id": request_id, "message": str(exc)})
    return 0
