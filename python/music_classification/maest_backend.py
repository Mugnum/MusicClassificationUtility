from __future__ import annotations

import importlib.util
import sys
import time
import warnings
from pathlib import Path
from typing import Callable

import numpy as np

from .runtime import AnalysisError, file_sha256, model_directory, source_directory, read_manifest


class MaestBackend:
    def __init__(self, threads: int = 8, progress: Callable[[str], None] = lambda _: None):
        started = time.perf_counter()
        manifest = read_manifest()
        checkpoint = model_directory() / manifest["model"]["filename"]
        package = source_directory() / "models"
        if not checkpoint.is_file() or not (package / "maest.py").is_file():
            raise AnalysisError("MAEST source or checkpoint is missing. Initialize the runtime from the application's Settings first.")
        progress("Verifying the local MAEST checkpoint...")
        digest = file_sha256(checkpoint)
        if digest != manifest["model"]["sha256"]:
            raise AnalysisError("The MAEST checkpoint does not match the pinned SHA-256 in runtime-manifest.json.")

        import torch

        torch.set_num_threads(threads)
        self.torch = torch
        existing = sys.modules.get("maest")
        if existing is None:
            spec = importlib.util.spec_from_file_location(
                "maest", package / "__init__.py", submodule_search_locations=[str(package)]
            )
            if spec is None or spec.loader is None:
                raise AnalysisError("Could not load the pinned MAEST package.")
            upstream = importlib.util.module_from_spec(spec)
            sys.modules["maest"] = upstream
            try:
                # This specific upstream warning is already addressed by the
                # explicit setuptools pin; keep other warnings visible.
                with warnings.catch_warnings():
                    warnings.filterwarnings("ignore", message="pkg_resources is deprecated as an API.*", category=UserWarning)
                    spec.loader.exec_module(upstream)
            except BaseException:
                sys.modules.pop("maest", None)
                raise
        else:
            if Path(existing.__file__).resolve() != (package / "__init__.py").resolve():
                raise AnalysisError("A different MAEST package was already imported into this process.")
            upstream = existing

        progress("Loading MAEST on CPU...")
        # Load only the verified release's tensor state, without general pickle
        # execution, downloads, or timm's automatic checkpoint adaptation.
        state = torch.load(checkpoint, map_location="cpu", weights_only=True)
        if not isinstance(state, dict) or "time_new_pos_embed" not in state:
            raise AnalysisError("The pinned checkpoint has an unexpected state-dict layout.")
        time_positions = int(state["time_new_pos_embed"].shape[-1])
        frequency_positions = int(state["freq_new_pos_embed"].shape[-2])
        self.input_frames = 16 + (time_positions - 1) * 10
        self.mel_bands = 16 + (frequency_positions - 1) * 10
        if self.input_frames != 1876 or self.mel_bands != 96:
            raise AnalysisError("Unexpected input geometry for the pinned 30-second MAEST checkpoint.")

        # Upstream's default input_t=1875 would allocate 186 temporal positions;
        # this checkpoint contains 187. Using 1876 preserves all trained
        # positions and matches the published TensorFlow/ONNX input geometry.
        self.model = upstream.get_maest(
            arch=manifest["model"]["architecture"], pretrained=False,
            n_classes=519, input_f=self.mel_bands, input_t=self.input_frames,
        )
        self.model.load_state_dict(state, strict=True)
        self.model.eval()
        self.model.init_melspectrogram()
        self.model.melspectrogram.eval()
        self.labels = list(self.model.labels)
        if len(self.labels) != 519 or len(set(self.labels)) != 519:
            raise AnalysisError("The checkpoint's label vocabulary is inconsistent.")
        self.info = {
            "architecture": manifest["model"]["architecture"],
            "checkpoint_sha256": digest,
            "source_revision": manifest["source"]["commit"],
            "torch_version": torch.__version__,
            "input_frames": self.input_frames,
            "mel_bands": self.mel_bands,
            "label_count": len(self.labels),
            "weight_loading": "strict-unmodified-state-dict",
            "preprocessing": "upstream-torchaudio-melspectrogram",
            "device": "cpu",
            "load_seconds": time.perf_counter() - started,
        }
        progress(f"Model ready: 519 labels, {self.info['load_seconds']:.2f}s to load.")

    def predict(self, samples: np.ndarray) -> np.ndarray:
        if samples.shape != (480000,) or samples.dtype != np.float32:
            raise AnalysisError("MAEST expects exactly 30 seconds of mono float32 audio at 16 kHz.")
        with self.torch.inference_mode():
            audio = self.torch.from_numpy(np.ascontiguousarray(samples))
            features = self.model.melspectrogram(audio)
            if tuple(features.shape) != (self.mel_bands, self.input_frames):
                raise AnalysisError(f"Unexpected spectrogram shape: {tuple(features.shape)}")
            # Bypass the convenience method's frame trimming and implicit
            # averaging so each explicit window retains its own predictions.
            logits, _ = self.model(features[None, None], melspectrogram_input=True)
            return self.torch.sigmoid(logits)[0].cpu().numpy().copy()
