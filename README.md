# GraphicsEncDecLib

A pure-managed C# image codec library — **PNG, JPEG, BMP, QOI and GIF, encode and
decode, plus an image resampler**. No GDI+, no `System.Drawing`, no P/Invoke, no
NuGet, and no native dependency of any kind: the library references `System.dll`
and nothing else, so it runs under .NET Framework on Windows and under Mono on
Linux from the same source.

**Licence:** MIT OR GPL-2.0-or-later, at your choice — see [`LICENSE`](LICENSE)
for both texts and `GPL-2.0.txt` for the GPL in full.

## What it does

| Format | Decode | Encode |
|---|---|---|
| **PNG** | every bit depth (1/2/4/8/16) and colour type, `tRNS`, Adam7 interlace | the same set, palettised and interlaced |
| **JPEG** | baseline **and progressive**; grayscale, 4:4:4 / 4:2:2 / 4:2:0; CMYK / YCCK | baseline; grayscale, 4:4:4 / 4:2:2 / 4:2:0 |
| **BMP** | header sizes 12–124; 1/4/8-bit palette and 16/24/32-bit direct; `BI_RGB`, `BI_BITFIELDS`, `BI_RLE4/8` | 4/8-bit (optionally RLE), 24-bit, 32-bit `BI_BITFIELDS` |
| **QOI** | full specification | full specification, byte-identical to the reference encoder |
| **GIF** | 87a / 89a, LZW, interlace, transparency, **animation** (all four disposal methods) | stills and animation, exact palette when it fits, adaptive quantiser otherwise |
| **Resample** | — | five filters, any ratio, all channel counts, both sample depths |

A format-agnostic **dispatcher** sniffs by magic bytes and decodes whatever it
finds. Out of scope by design: TGA / PSD / HDR / PIC, metadata (EXIF / ICC / text
chunks), and streaming or incremental decode — every entry point takes and
returns a `byte[]`.

## Requirements

C# 5.0 / .NET Framework 4.x, buildable by `csc.exe` alone — no MSBuild, no
`.csproj`, no package manager. Runs unchanged under Mono on Linux.

## Building

- **Windows:** `compile-lib.bat`
- **Linux / Mono:** `./compile-lib.sh`

Both compile the `src\` tree into the library assembly with only the `System.dll`
reference, so a stray `System.Drawing` call is a build error rather than a runtime
surprise. The library is just as easily consumed by copying the `src\` files into
your own project and building them with your tree.

## API at a glance

- **`ImageBuffer`** — the pixel type every call speaks: width, height, channel
  count (1–4) and a `byte[]` of pixels.
- **`ImageFrameSet`** — a sequence of frames with timing, for animated GIF.
- **Decoding** — one decoder per format (`PngCodec`, `JpegCodec`, `BmpCodec`,
  `QoiCodec`, `GifCodec`), cheap header-only readers, and the
  `ImageDecoderDispatch` magic-byte dispatcher.
- **Encoding** — a per-format encoder with its own options object (bit depth,
  chroma subsampling, quality, palette, RLE, interlace …); passing `null` options
  takes sensible defaults.
- **Resampling** — `ImageResampler`: five filters, any ratio, straight or
  premultiplied alpha.
- **Core primitives** — CRC-32, Adler-32, a zlib (DEFLATE) stream codec, and
  byte readers / writers, each usable on its own.

Malformed input is reported through a consistent failure contract (reference
§4.1); the library throws only for genuinely impossible states.

## Full reference

The complete API — every type, method, option, accuracy note and worked example —
is in **[`API_Reference.md`](API_Reference.md)**, with a rendered
**`API_Reference.html`** beside it.

## Provenance

Several source files are hand transcodings of permissive and public-domain C
reference implementations (stb, qoi, lodepng and others); each names its exact
source in a lineage comment at the top, and `LICENSE` lists them in full. No
third-party code is shipped verbatim and nothing is bundled.
