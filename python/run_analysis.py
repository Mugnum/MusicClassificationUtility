"""Unsupported internal CLI entry point; no public API compatibility is promised."""
from music_classification.cli import main

if __name__ == "__main__":
    raise SystemExit(main())
