# ManyCam CVC1 decoder

Convert old **ManyCam** webcam recordings that use the **CVC1** video codec into normal MP4 files.

[![Buy Me a Coffee](https://img.shields.io/badge/Buy%20me%20a%20coffee-FFDD00?style=for-the-badge&logo=buymeacoffee&logoColor=black)](https://buymeacoffee.com/MawdleCode)

---

## Is this your problem?

Around 2009–2010, **ManyCam Virtual Webcam 2.5.x** recorded video as AVI files using its own codec, `CVC1`.
The codec was never released, ManyCam dropped recording in version 2.6, and nothing can play these files today:

- **VLC:** *"VLC does not support the audio or video format 'CVC1'"*
- **Windows Media Player / Films & TV:** won't open them, or shows a codec error
- **ffmpeg / ffprobe:** `Video: none (CVC1 / 0x31435643) ... unknown codec`
- Installing codec packs (K-Lite, CCCP, …) doesn't help

These recordings were often made with a USB webcam (Logitech, Dell/Creative, …) running through ManyCam —
for example home CCTV or motion-detection setups. The default file name was `Untitled.avi`, `Untitled1.avi`, …

This tool decodes CVC1 directly and converts the video to H.264 MP4. It also recovers
**recordings that were never closed properly** (ManyCam writes the AVI header last, so a crash leaves a
file whose header is zeroed — those are handled automatically).

## Download

1. Download **`cvc1conv.zip`** from the [latest release](https://github.com/MawdleCode/Manycam-cvc1-decoder/releases/latest) and unzip it.
2. Get **ffmpeg** (used to write the MP4):
   - `winget install ffmpeg` in a terminal, **or**
   - download a build from <https://www.gyan.dev/ffmpeg/builds/> and put `ffmpeg.exe` next to `cvc1conv.exe`.

Requirements: Windows 7 or later with .NET Framework 4 (already included in Windows 8, 10 and 11).

> `cvc1conv.exe` is not code-signed, so Windows SmartScreen may warn the first time you run it.
> If you'd rather not run a downloaded exe, [build it yourself](#building-from-source) — it takes a few seconds and needs nothing extra.

## Usage

**Easiest:** drag one or more `.avi` files (or a folder) onto `cvc1conv.exe`. Each `video.avi` becomes `video.mp4` beside it.

**From a terminal:**

```
cvc1conv Untitled.avi
cvc1conv "D:\Old webcam\" --overwrite
cvc1conv Untitled1.avi -o "D:\Converted\garden.mp4" --crf 16
cvc1conv Untitled.avi --y4m          (uncompressed .y4m, no ffmpeg needed)
```

| Option | Meaning |
|---|---|
| `-o <file>` | Output file (single input). A `.y4m` name writes uncompressed video. |
| `--y4m` | Write uncompressed `.y4m` instead of MP4 (no ffmpeg needed; files are large). |
| `--fps <n>` | Frame rate for the output. Default: from the AVI header (usually 15). |
| `--crf <n>` | x264 quality, lower = better/larger. Default 18 (visually lossless). |
| `--ffmpeg <path>` | Path to `ffmpeg.exe` if it isn't next to the tool or on PATH. |
| `--overwrite` | Replace existing output files. |
| `--md5` | Decode only and print a checksum of all frames (for testing). |

### Why does my video play too fast?

ManyCam stamped its AVIs as 15 fps regardless of how many frames per second it actually captured
(often 5–8 fps for a webcam on an older PC), so recordings play back faster than real time.
If your video has an on-screen clock, compare it against the video length and use `--fps` to slow it down,
e.g. `cvc1conv Untitled.avi --fps 5`.

## How it works

ManyCam never shipped a CVC1 decoder — only the encoder, `MCVEnc.dll`. This project reconstructs the format
from that encoder. CVC1 turned out to be a variant of the Chinese **AVS1-P2 (Jizhun profile)** video standard
— AVS 8×8 integer transform, AVS intra prediction, context-adaptive 2D-VLC entropy coding, quarter-pel motion
compensation with I/P/B frames and the AVS loop filter — wrapped in ManyCam's own frame/slice headers and
macroblock syntax.

The decoder deliberately reproduces several quirks of ManyCam's encoder (for example a transposed chroma
prediction mode and a motion-vector bookkeeping bug), because the encoder built its residuals around them;
matching them is what gives a clean picture without drift. On real recordings every frame's data is consumed
to the exact bit, and the last frame of each group of pictures matches the following keyframe closely
(35–47 dB PSNR), confirming that errors don't accumulate.

The bitstream is documented in [FORMAT.md](FORMAT.md) for anyone who wants to add CVC1 support elsewhere
(e.g. FFmpeg). The constant tables the format needs are embedded in `src/Tables.cs`, so **no ManyCam files
are needed**.

## Building from source

Windows already contains the C# compiler, so no SDK or Visual Studio is required:

```
git clone https://github.com/MawdleCode/Manycam-cvc1-decoder.git
cd Manycam-cvc1-decoder
build.bat
```

This produces `cvc1conv.exe`. The code is plain C# 5 targeting .NET Framework 4, with no dependencies.

## Limitations

- Only CVC1 (ManyCam 2.5.x). Other ManyCam versions recorded WMV/MPEG, which play normally.
- Tested on 320×240 recordings. Other sizes that are multiples of 16 should work; please open an issue if not.
- CVC1 AVIs from ManyCam have no audio track.

If a file doesn't convert, please [open an issue](https://github.com/MawdleCode/Manycam-cvc1-decoder/issues)
with the console output (and, if you're able to share it, a short sample).

## Support

If this rescued your old recordings, you can say thanks here:

<a href="https://buymeacoffee.com/MawdleCode"><img src="https://img.shields.io/badge/Buy%20me%20a%20coffee-FFDD00?style=for-the-badge&logo=buymeacoffee&logoColor=black" alt="Buy Me a Coffee"></a>

## Legal

MIT licensed — see [LICENSE](LICENSE). This is an independent project for interoperability with users' own
recordings; it is not affiliated with or endorsed by ManyCam or Visicom Media. ManyCam is a trademark of its owner.
