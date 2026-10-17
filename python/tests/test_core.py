import tempfile
import unittest
from unittest.mock import patch
from pathlib import Path

import numpy as np

from music_classification.analysis import aggregate, analyze_file, discover, top_genres
from music_classification.audio import AudioWindow, WINDOW_SAMPLES, model_audio
from music_classification.runtime import AnalysisError
from music_classification.runtime import PROJECT_ROOT
from music_classification.runtime import model_directory, source_directory, read_manifest


class CoreTests(unittest.TestCase):
    def analyze_windows(self, durations):
        windows = []
        start = 0
        for duration in durations:
            count = round(duration * 16000)
            windows.append(AudioWindow(start, np.full(count, 0.1, dtype=np.float32)))
            start += count
        backend = unittest.mock.Mock()
        backend.labels = ["First", "Second"]
        backend.predict.side_effect = [np.array([0.9, 0.1]), np.array([0.1, 0.9])]
        with patch("music_classification.analysis.probe", return_value={"reported_duration_seconds": sum(durations)}), patch("music_classification.analysis.decode_windows", return_value=iter(windows)):
            result = analyze_file(Path("song.wav"), backend, "ffmpeg", "ffprobe")
        return result, backend.predict.call_count

    def test_tail_under_ten_seconds_is_never_inferred_or_averaged(self):
        result, calls = self.analyze_windows([30, 1.5])
        self.assertEqual(calls, 1)
        self.assertEqual(len(result["segments"]), 1)
        self.assertEqual(result["duration_seconds"], 31.5)
        self.assertEqual(result["analyzed_duration_seconds"], 30)
        self.assertEqual(result["ignored_tail_seconds"], 1.5)
        np.testing.assert_allclose(result["overall_scores"], [0.9, 0.1])

    def test_fifteen_second_tail_has_half_full_window_weight(self):
        result, calls = self.analyze_windows([30, 15])
        self.assertEqual(calls, 2)
        np.testing.assert_allclose(result["overall_scores"], [(0.9 * 30 + 0.1 * 15) / 45, (0.1 * 30 + 0.9 * 15) / 45])
        self.assertEqual(result["ignored_tail_seconds"], 0)

    def test_exact_ten_second_tail_is_retained(self):
        result, calls = self.analyze_windows([30, 10])
        self.assertEqual(calls, 2)
        self.assertEqual(result["segments"][-1]["valid_duration_seconds"], 10)

    def test_whole_input_under_ten_seconds_has_no_eligible_segment(self):
        with self.assertRaisesRegex(AnalysisError, "at least 10 seconds"):
            self.analyze_windows([9.999])

    def test_blank_optional_directories_use_runtime_defaults(self):
        with patch.dict("os.environ", {"MCU_RUNTIME_DIR": str(PROJECT_ROOT), "MCU_MODEL_DIR": "", "MCU_SOURCE_DIR": ""}):
            self.assertEqual(model_directory(), PROJECT_ROOT / "models")
            self.assertEqual(source_directory(), PROJECT_ROOT / "vendor" / read_manifest()["source"]["directory"])

    def test_explicit_runtime_dependency_directories_are_respected(self):
        with patch.dict("os.environ", {"MCU_MODEL_DIR": str(PROJECT_ROOT / "custom-models"), "MCU_SOURCE_DIR": str(PROJECT_ROOT / "custom-source")}):
            self.assertEqual(model_directory(), PROJECT_ROOT / "custom-models")
            self.assertEqual(source_directory(), PROJECT_ROOT / "custom-source")

    def temporary_directory(self):
        parent = PROJECT_ROOT / "test-artifacts"
        parent.mkdir(exist_ok=True)
        return tempfile.TemporaryDirectory(dir=parent)

    def test_tail_has_original_coverage_and_explicit_repeated_padding(self):
        window = AudioWindow(480000, np.array([0.1, -0.2, 0.3], dtype=np.float32))
        expanded = model_audio(window)
        self.assertEqual(window.end_sample, 480003)
        self.assertEqual(len(expanded), WINDOW_SAMPLES)
        np.testing.assert_array_equal(expanded[:6], np.tile(window.samples, 2))

    def test_complete_window_is_not_padded(self):
        samples = np.zeros(WINDOW_SAMPLES, dtype=np.float32)
        self.assertIs(model_audio(AudioWindow(0, samples)), samples)

    def test_empty_window_is_rejected(self):
        with self.assertRaises(AnalysisError):
            model_audio(AudioWindow(0, np.array([], dtype=np.float32)))

    def test_short_tail_does_not_receive_full_window_weight(self):
        result = aggregate([np.array([0.9, 0.1]), np.array([0.0, 1.0])], [30, 1])
        np.testing.assert_allclose(result, [27 / 31, 4 / 31])

    def test_empty_aggregate_is_rejected(self):
        with self.assertRaises(AnalysisError):
            aggregate([], [])

    def test_top_results_keep_label_alignment_and_ties_stable(self):
        ranked = top_genres(np.array([0.1, 0.8, 0.8]), ["low", "first", "second"], 2)
        self.assertEqual([item["label"] for item in ranked], ["first", "second"])

    def test_discovery_preserves_literal_paths_and_deduplicates(self):
        with self.temporary_directory() as temporary:
            root = Path(temporary)
            audio = root / "трек [one].flac"
            audio.touch()
            (root / "notes.txt").touch()
            nested = root / "nested"
            nested.mkdir()
            (nested / "two.mp3").touch()
            files, errors = discover([str(root), str(audio)])
            self.assertEqual(files, [audio.resolve()])
            self.assertEqual(errors, [])
            files, _ = discover([str(root)], recursive=True)
            self.assertEqual(len(files), 2)

    def test_missing_path_is_a_per_input_error(self):
        with self.temporary_directory() as temporary:
            files, errors = discover([str(Path(temporary) / "missing.wav")])
            self.assertEqual(files, [])
            self.assertEqual(len(errors), 1)


if __name__ == "__main__":
    unittest.main()
