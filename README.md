## Music classification utility
Windows GUI for music genre and style detection using locally deployed AI model.  
Powered by [MAEST](https://github.com/palonso/MAEST) project and their pre-trained model targeting 519 Discogs music styles.

### Demonstration
![alt text](https://raw.githubusercontent.com/Mugnum/MusicClassificationUtility/refs/heads/master/media/screenshot-1.png)
Click on image to enlarge.

### Prerequisites
- [.NET 10.0 Runtime - Windows x64](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)
- [Python 3.12 and Astral uv](https://docs.astral.sh/uv/getting-started/installation/#__tabbed_1_2)
- [FFmpeg](https://ffmpeg.org/download.html)
- At least 3 Gb of available disk space (7 Gb via hardlinks)

### Installation
- Download [latest release](https://github.com/Mugnum/MusicClassificationUtility/releases) and extract files anywhere.
- Launch `MusicClassification.GUI.exe`.
- On first launch: a settings window will show up, requiring to perform first setup.
- Optionally update "Runtime folder" path, otherwise 3 Gb files will end up in your %localappdata%.
- Press "Initialize..." and then "Save".

### License
Music Classification Utility's application code and Python adapter are licensed under GNU Affero General Public License v3.0, see [LICENSE](https://github.com/Mugnum/MusicClassificationUtility/blob/master/LICENSE).  
Copyright © 2026 Sergey Syrtsev.

### Third-party notices
This project utilizes [MAEST](https://github.com/palonso/MAEST) (Music Audio Efficient Spectrogram Transformer) by Pablo Alonso-Jiménez, Xavier Serra and Dmitry Bogdanov. First setup of this application downloads the upstream codebase (linked in [runtime-manifest.json](https://github.com/Mugnum/MusicClassificationUtility/blob/master/python/runtime-manifest.json)) into user's `runtime\vendor` directory. MAEST's source code is licensed under AGPL-3.0.

Analysis uses `discogs-maest-30s-pw-129e-519l-swa.ckpt`, which is downloaded during initialization from MAEST's release. The checkpoint is supplied unchanged and is not included in this repository or release.

Model weights retain their applicable upstream terms and are not re-licensed under this application's AGPL-3.0 license.
