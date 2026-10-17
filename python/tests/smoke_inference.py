"""Explicit local model smoke test; synthetic audio does not assess genre accuracy."""

import contextlib
import json
import sys
import time

import numpy as np

from music_classification.analysis import analyze_file
from music_classification.audio import decode_windows, model_audio
from music_classification.maest_backend import MaestBackend
from music_classification.runtime import PROJECT_ROOT, executable
from test_audio_integration import write_fixture


def main():
    folder = PROJECT_ROOT / "test-artifacts" / "audio [test]"
    folder.mkdir(parents=True, exist_ok=True)
    source = folder / "generated 32 seconds.wav"
    write_fixture(source, 32.25)
    ffmpeg, ffprobe = executable("ffmpeg"), executable("ffprobe")
    started = time.perf_counter()
    with contextlib.redirect_stdout(sys.stderr):
        backend = MaestBackend(progress=lambda message: print(message, file=sys.stderr))
        result = analyze_file(source, backend, ffmpeg, ffprobe, top=5)
        assert len(result["segments"]) == 1
        assert result["duration_seconds"] == 32.25
        assert result["ignored_tail_seconds"] == 2.25
        assert result["analyzed_duration_seconds"] == 30
        window = next(decode_windows(source, ffmpeg))
        repeated = backend.predict(model_audio(window))
        np.testing.assert_array_equal(repeated, np.array(result["segments"][0]["scores"], dtype=np.float32))
    summary = {
        "passed": True,
        "purpose": "synthetic-audio pipeline smoke test; not an accuracy evaluation",
        "source_duration_seconds": result["duration_seconds"],
        "segments": len(result["segments"]),
        "labels": len(backend.labels),
        "deterministic_repeat": True,
        "analysis_seconds": result["analysis_seconds"],
        "total_seconds": time.perf_counter() - started,
    }
    output = PROJECT_ROOT / "test-artifacts" / "inference-smoke.json"
    output.write_text(json.dumps(summary, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(summary, indent=2))


if __name__ == "__main__":
    main()
