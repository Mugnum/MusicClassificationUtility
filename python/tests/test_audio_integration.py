import shutil
import subprocess
import unittest
import wave

import numpy as np

from music_classification.audio import CREATE_NO_WINDOW, SAMPLE_RATE, decode_windows, probe
from music_classification.runtime import AnalysisError, PROJECT_ROOT


def write_fixture(path, seconds):
    rate = 44100
    t = np.arange(round(seconds * rate), dtype=np.float64) / rate
    envelope = 0.65 + 0.35 * np.sin(2 * np.pi * 2 * t)
    left = envelope * (0.2 * np.sin(2 * np.pi * 110 * t) + 0.1 * np.sin(2 * np.pi * 440 * t))
    right = envelope * (0.2 * np.sin(2 * np.pi * 165 * t) + 0.1 * np.sin(2 * np.pi * 660 * t))
    samples = (np.stack([left, right], axis=1) * 32767).astype("<i2")
    with wave.open(str(path), "wb") as stream:
        stream.setnchannels(2)
        stream.setsampwidth(2)
        stream.setframerate(rate)
        stream.writeframes(samples.tobytes())


@unittest.skipUnless(shutil.which("ffmpeg") and shutil.which("ffprobe"), "FFmpeg and FFprobe are required")
class AudioIntegrationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.folder = PROJECT_ROOT / "test-artifacts" / "audio [test]"
        cls.folder.mkdir(parents=True, exist_ok=True)
        cls.ffmpeg = shutil.which("ffmpeg")
        cls.ffprobe = shutil.which("ffprobe")
        cls.wav = cls.folder / "generated short.wav"
        write_fixture(cls.wav, 2.25)
        cls.formats = {
            "wav": cls.wav,
            "flac": cls.folder / "generated short.flac",
            "alac": cls.folder / "generated alac.m4a",
            "mp3": cls.folder / "generated short.mp3",
            "aac": cls.folder / "generated aac.m4a",
            "opus": cls.folder / "generated short.opus",
            "ogg": cls.folder / "generated short.ogg",
            "wma": cls.folder / "generated short.wma",
        }
        codecs = {"flac": "flac", "alac": "alac", "mp3": "libmp3lame", "aac": "aac",
                  "opus": "libopus", "ogg": "libvorbis", "wma": "wmav2"}
        for kind, codec in codecs.items():
            subprocess.run(
                [cls.ffmpeg, "-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-i", str(cls.wav),
                 "-c:a", codec, str(cls.formats[kind])], check=True, capture_output=True,
                creationflags=CREATE_NO_WINDOW,
            )

    def test_common_formats_decode_from_paths_with_spaces_and_brackets(self):
        for kind, path in self.formats.items():
            with self.subTest(format=kind):
                info = probe(path, self.ffprobe)
                self.assertEqual(info["channels"], 2)
                windows = list(decode_windows(path, self.ffmpeg))
                self.assertEqual(len(windows), 1)
                duration = windows[0].valid_samples / SAMPLE_RATE
                self.assertGreater(duration, 2.0)
                self.assertLess(duration, 2.6)
                self.assertTrue(np.isfinite(windows[0].samples).all())

    def test_lossless_formats_produce_identical_model_audio(self):
        reference = next(decode_windows(self.wav, self.ffmpeg)).samples
        for kind in ("flac", "alac"):
            with self.subTest(format=kind):
                decoded = next(decode_windows(self.formats[kind], self.ffmpeg)).samples
                np.testing.assert_array_equal(decoded, reference)

    def test_long_file_includes_every_sample_including_tail(self):
        path = self.folder / "generated 32 seconds.wav"
        write_fixture(path, 32.25)
        windows = list(decode_windows(path, self.ffmpeg))
        self.assertEqual([window.valid_samples for window in windows], [30 * SAMPLE_RATE, round(2.25 * SAMPLE_RATE)])
        self.assertEqual(windows[1].start_sample, windows[0].end_sample)
        self.assertEqual(windows[-1].end_sample, round(32.25 * SAMPLE_RATE))

    def test_corrupt_input_fails_instead_of_returning_predictions(self):
        broken = self.folder / "broken.flac"
        broken.write_bytes(b"this is not an audio file")
        # FFprobe can accept a damaged FLAC container and expose an incomplete
        # stream. Actual decoding must still fail rather than yield a result.
        with self.assertRaises(AnalysisError):
            list(decode_windows(broken, self.ffmpeg))
