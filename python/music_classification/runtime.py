from __future__ import annotations

import hashlib
import json
import os
import shutil
from pathlib import Path

PROJECT_ROOT = Path(__file__).resolve().parent.parent


def runtime_directory() -> Path:
    configured = os.environ.get("MCU_RUNTIME_DIR")
    if configured:
        return Path(configured).expanduser().resolve()
    return Path(os.environ.get("LOCALAPPDATA", Path.home())) / "MusicClassificationUtility" / "runtime"


def model_directory() -> Path:
    configured = os.environ.get("MCU_MODEL_DIR")
    return Path(configured).expanduser().resolve() if configured else runtime_directory() / "models"


def source_directory() -> Path:
    configured = os.environ.get("MCU_SOURCE_DIR")
    return Path(configured).expanduser().resolve() if configured else runtime_directory() / "vendor" / read_manifest()["source"]["directory"]


class AnalysisError(RuntimeError):
    pass


def read_manifest() -> dict:
    return json.loads((PROJECT_ROOT / "runtime-manifest.json").read_text(encoding="utf-8"))


def file_sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def executable(name: str, override: str | None = None) -> str:
    value = override or name
    found = shutil.which(value)
    if not found:
        raise AnalysisError(f"{name} was not found: {value}. Install FFmpeg or supply its executable path.")
    return str(Path(found).resolve())
