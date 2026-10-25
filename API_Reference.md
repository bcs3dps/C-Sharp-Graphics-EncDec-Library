# GraphicsEncDecLib — API Reference

A pure-managed C# image codec library. Five formats, both directions, plus a
resampler. No GDI+, no `System.Drawing`, no P/Invoke, no NuGet, no native
dependency of any kind.

**Licence: MIT OR GPL-2.0-or-later, at the recipient's choice.** See `LICENSE`
in this folder for both texts and `GPL-2.0.txt` for the GPL in full. Copyright
(c) 2026 B. C. Services.

---

## 1. What it does

| Format | Decode | Encode |
|---|---|---|
| **PNG** | every bit depth (1/2/4/8/16), every colour type (0/2/3/4/6), `tRNS` in both forms, Adam7 interlacing | the same set, including palettised and interlaced output |
| **JPEG** | baseline and **progressive**; grayscale, 4:4:4, 4:2:2, 4:2:0; CMYK, YCCK and raw-RGB four-component corners | baseline; grayscale, 4:4:4, 4:2:2, 4:2:0 |
| **BMP** | header sizes 12/40/56/108/124; 1/4/8-bit palettised and 16/24/32-bit direct; `BI_RGB`, `BI_BITFIELDS`, `BI_RLE8`, `BI_RLE4`; both row orders | 4/8-bit palettised (optionally RLE), 24-bit `BI_RGB`, 32-bit `BI_BITFIELDS` |
| **QOI** | full specification | full specification, byte-identical to the reference encoder on all eight reference images |
| **GIF** | 87a and 89a, LZW, interlacing, global and local tables, transparency, and **animation** with all four disposal methods | stills and **animation**, with an exact palette when the image fits one and an adaptive quantiser when it does not |
| **Resample** | — | five filters, both sample depths, all four channel counts, any ratio |

There is also a **format-agnostic dispatcher** that sniffs by magic bytes and
decodes whatever it finds.

> **Out of scope, deliberately:** TGA, PSD, HDR and PIC. Metadata (EXIF, ICC,
> text chunks) is skipped on read and not written. There is no streaming or
> incremental decode — every entry point takes and returns a `byte[]`.

---

## 2. Requirements and portability

- **C# 5.0 / .NET Framework 4.x**, buildable by `csc.exe` alone. No MSBuild, no
  `.csproj`, no package manager.
- **The library references `System.dll` and nothing else.** This is enforced by
  the build rather than asserted: the library compiles as its own assembly with
  only that reference, so a stray `System.Drawing` call is a compile error.
- **Runs under Mono on Linux.** That is the reason the library exists. It is
  built and its full test suite is run under Mono on every change.
- `/unsafe` is enabled in the supplied build scripts so a `fixed` pointer in an
  inner loop needs no build change. No current code uses one.

---

## 3. Consuming the library

**Copy the source in.** This library is distributed as C# source, not as a
binary, and the intended use is to mirror the files into your own tree and own
that copy. Nothing needs to be installed and nothing is loaded at run time.

What you need:

```
src\Core\Adler32.cs              src\Codecs\BmpCodec.cs
src\Core\ByteReader.cs           src\Codecs\GifCodec.cs
src\Core\ByteWriter.cs           src\Codecs\JpegCodec.cs
src\Core\Crc32.cs                src\Codecs\PngCodec.cs
src\Core\ImageBuffer.cs          src\Codecs\QoiCodec.cs
src\Core\ImageDecoderDispatch.cs
src\Core\ImageFrameSet.cs        src\Resample\ImageResampler.cs
src\Core\ZlibStreamCodec.cs
```

**Fourteen files. They are self-contained**: the only dependency any of them has
is on the others and on `System.dll`. Each codec is one file carrying both
directions, so a consumer needing only PNG can take `PngCodec.cs` plus `src\Core\`
and drop the rest.

Everything lives in namespace **`GraphicsEncDec`**.

`compile-lib.bat` (Windows, `csc.exe`) and `compile-lib.sh` (Linux, `mcs`) are
supplied as worked examples. Each builds the library and nothing else, from the
fourteen files above and no others. Neither globs: every source file is named
explicitly, so a file added to the tree and not to the script is not compiled.

---

## 4. Conventions you need before reading anything else

### 4.1 The failure contract

**Every fallible call returns `bool`, writes its result to an `out` parameter,
and writes a human-readable explanation to `out string sReason`.**

```csharp
ImageBuffer img;
string       sReason;

if (!PngCodec.PNG_TryDecode(arrFile, 0, arrFile.Length, out img, out sReason))
{
    Console.Error.WriteLine("decode failed: " + sReason);
    return;
}
```

- **A malformed image is data, not an error condition.** Corrupt input returns
  `false` with a reason; it never throws.
- **Exceptions are reserved for caller bugs** — a null buffer, an offset outside
  the array. Those are your fault, not the file's.
- **On `false`, the `out` value is undefined.** Do not read it.
- `sReason` is always non-empty on failure and is meant to be shown or logged.
  It names the specific field, offset or constraint that failed.

### 4.2 Naming

Every public symbol is globally unique — there is no overloading anywhere in
the library, and no two methods share a name. A search for a symbol returns its
definition and its real uses, nothing else.

Names are `GROUP_PascalName`. The group prefixes are:

| Prefix | Owner |
|---|---|
| `IMG_` | `ImageBuffer` |
| `FRM_` | `ImageFrameSet` |
| `IMGDEC_` | the format dispatcher |
| `PNG_`, `JPEG_`, `BMP_`, `QOI_`, `GIF_` | the five codecs |
| `RESAMP_` | the resampler |
| `CRC32_`, `ADLER_`, `ZLIB_` | the checksum and compression primitives |
| `BYTER_`, `BYTEW_` | `ByteReader` and `ByteWriter` |

Variables carry Hungarian type prefixes: `i` int, `b` bool, `s` string, `u`
unsigned, `f` float, `arr` array, `img` an `ImageBuffer`, `opt` an options
object.

### 4.3 Options objects and `null`

Each encoder takes an options object. **Passing `null` is legal and means "use
the defaults"**, which keeps the common call short without an overload:

```csharp
QoiCodec.QOI_TryEncode(img, null, out arrOut, out sReason);
```

To change one field, start from the factory rather than a bare constructor —
some fields have no valid zero:

```csharp
GifEncodeOptions opt = GifCodec.GIF_DefaultEncodeOptions();
opt.GIFOPT_iPaletteSize = 16;
GifCodec.GIF_TryEncode(img, opt, out arrOut, out sReason);
```

> **A default-constructed `GifEncodeOptions` is REFUSED.** `GIFOPT_iPaletteSize`
> has no "0 means default" sentinel — its documented range is 2 to 256 — so a
> zeroed object is rejected rather than silently read as 256. Use the factory or
> pass `null`.

### 4.4 The lossless / lossy line

**This is the single most important behavioural rule in the library, and it is
the same in every codec.**

- **A conversion that loses nothing is performed for you, silently.** Grayscale
  widened to RGB, an opaque alpha channel appended, 8-bit widened to 16-bit.
- **A conversion that loses something is REFUSED, with a reason.** Dropping an
  alpha channel, narrowing 16-bit to 8-bit, reducing more colours than a palette
  can hold, rounding a value that is not exactly representable.

The caller who wants a lossy conversion asks for it explicitly — by converting
the buffer first with `IMG_TryConvertChannels`, or by using an option that says
so. The encoder never decides on your behalf.

The one deliberate exception is GIF's quantiser, and it is only reachable
because `GIFOPT_iPaletteSize` and `GIFOPT_bDither` exist for you to ask with.

---

## 5. `ImageBuffer` — the pixel type

Every decode produces one; every encode and resize consumes one.

```csharp
public sealed class ImageBuffer
{
    public int      IMG_iWidth;           // pixels across, >= 1
    public int      IMG_iHeight;          // pixel rows, >= 1
    public int      IMG_iChannels;        // 1, 2, 3 or 4
    public int      IMG_iBitsPerSample;   // 8 or 16
    public byte[]   IMG_arrSamples8;      // live when 8-bit, else null
    public ushort[] IMG_arrSamples16;     // live when 16-bit, else null
}
```

**Samples are interleaved, row-major, top row first, no padding between rows.**
A pixel's channels are adjacent. There is no stride to account for: a row is
exactly `IMG_iWidth * IMG_iChannels` samples.

**Exactly one sample array is non-null.** That is the type's central invariant.
Check `IMG_iBitsPerSample` and use the matching array; never assume 8-bit.

### 5.1 Channel counts

| Constant | Value | Layout |
|---|---|---|
| `IMG_CHANNELS_GRAY` | 1 | luminance |
| `IMG_CHANNELS_GRAY_ALPHA` | 2 | luminance, alpha |
| `IMG_CHANNELS_RGB` | 3 | red, green, blue |
| `IMG_CHANNELS_RGBA` | 4 | red, green, blue, alpha |

**Alpha is STRAIGHT, not premultiplied**, everywhere in this library. A decoded
image's colour channels are the real colour, whatever the alpha says.

| Constant | Value |
|---|---|
| `IMG_BITS_8` | 8 |
| `IMG_BITS_16` | 16 |
| `IMG_OPAQUE_8` | 255 |
| `IMG_OPAQUE_16` | 65535 |
| `IMG_MAX_DIMENSION` | 16777216 |

### 5.2 Methods

```csharp
bool   IMG_TryAllocate(int iWidth, int iHeight, int iChannels,
                       int iBitsPerSample,
                       out ImageBuffer imgOut, out string sReason);
bool   IMG_TryValidate(ImageBuffer imgIn, out string sReason);
int    IMG_SampleCount(ImageBuffer imgIn);
int    IMG_RowStride(ImageBuffer imgIn);
int    IMG_SampleIndex(ImageBuffer imgIn, int iX, int iY, int iChannel);
bool   IMG_TryConvertChannels(ImageBuffer imgIn, int iNewChannels,
                              out ImageBuffer imgOut, out string sReason);
```

**`IMG_TryAllocate`** allocates a zero-filled buffer. It refuses a dimension
below 1 or above `IMG_MAX_DIMENSION`, a channel count outside 1 to 4, a bit
depth other than 8 or 16, and any geometry whose total sample count would
overflow a 32-bit int. That overflow check is the reason to use it rather than
building the object by hand.

**`IMG_TryValidate`** checks a buffer you built or modified yourself: the
dimensions, the channel count, the depth, that exactly one sample array is live,
and that its length matches the geometry exactly.

**`IMG_SampleCount`** is `width * height * channels`. **`IMG_RowStride`** is
`width * channels` — both in SAMPLES, not bytes; at 16 bits a sample is two
bytes.

**`IMG_SampleIndex`** computes the index of one channel of one pixel. It does no
bounds checking, so it is safe to use in a loop you have already bounded.

**`IMG_TryConvertChannels`** is the lossy-conversion escape hatch. It performs
all twelve conversions between the four channel counts, at either depth:

| From → to | Behaviour |
|---|---|
| gray → gray+alpha, RGB → RGBA | appends an opaque alpha channel |
| gray → RGB / RGBA | replicates luminance into all three colour channels |
| gray+alpha → RGBA | replicates, alpha preserved |
| RGB / RGBA → gray | luminance-weighted, `(77 R + 150 G + 29 B) >> 8` |
| RGBA → RGB, gray+alpha → gray | **drops the alpha channel** |

This is the one place in the library where information is discarded without
complaint, because calling it is the caller saying so.

---

## 6. `ImageFrameSet` — animation

```csharp
public sealed class ImageFrameSet
{
    public ImageBuffer[] FRM_arrFrames;      // fully composited frames
    public int[]         FRM_arrDelaysMs;    // delay BEFORE showing frame i
    public int           FRM_iLoopCount;     // FRM_LOOP_FOREVER, or a play count
}

public const int FRM_LOOP_FOREVER = 0;
public const int FRM_MAX_FRAMES   = 20000;

bool FRM_TryValidate(ImageFrameSet frmIn, out string sReason);
int  FRM_FrameCount(ImageFrameSet frmIn);
```

**Frames are fully composited, not deltas.** Every frame is a complete image of
the same size, with disposal already applied and transparency resolved. You can
display `FRM_arrFrames[n]` directly without knowing anything about the frame
before it. **Every frame owns its own pixel array** — they are not views onto a
shared canvas.

**`FRM_arrDelaysMs[i]` is the delay before frame `i` is shown**, in milliseconds.
`FRM_arrFrames` and `FRM_arrDelaysMs` always have the same length.

`FRM_TryValidate` requires at least one frame, matching array lengths, every
frame the same dimensions and channel count, and a frame count within
`FRM_MAX_FRAMES`. That cap exists because a small file can declare an enormous
canvas and repeat a tiny frame, so the memory cost is frames times canvas and
nothing in the file's own size bounds it.

---

## 7. Decoding

### 7.1 The five decoders

All five share one shape:

```csharp
bool PNG_TryDecode (byte[] arrData, int iOffset, int iLength,
                    out ImageBuffer imgOut, out string sReason);
bool JPEG_TryDecode(byte[] arrData, int iOffset, int iLength,
                    out ImageBuffer imgOut, out string sReason);
bool BMP_TryDecode (byte[] arrData, int iOffset, int iLength,
                    out ImageBuffer imgOut, out string sReason);
bool QOI_TryDecode (byte[] arrData, int iOffset, int iLength,
                    out ImageBuffer imgOut, out string sReason);
bool GIF_TryDecode (byte[] arrData, int iOffset, int iLength,
                    out ImageBuffer imgOut, out string sReason);
```

`iOffset` and `iLength` describe a **window** into `arrData`. The image need not
start at index 0 and need not run to the end — which is the case that matters
when a thumbnail is embedded inside a larger file.

**The decoded buffer carries the FILE's channel count and depth, not a
convenient one.** A grayscale PNG decodes to one channel; a 16-bit PNG decodes to
16-bit. Nothing is widened for you. If you want a specific shape, ask for it
with `IMG_TryConvertChannels` afterwards — which keeps the conversion visible at
your call site instead of hidden in five codecs.

**`GIF_TryDecode` returns frame 0** of an animation, composited. For every frame,
use `GIF_TryDecodeFrames`.

### 7.2 The cheap header readers

```csharp
bool PNG_TryReadHeader (byte[] arrData, int iOffset, int iLength,
                        out PngHeader hdrOut,  out string sReason);
bool JPEG_TryReadHeader(byte[] arrData, int iOffset, int iLength,
                        out JpegHeader hdrOut, out string sReason);
```

These read the header and stop. **Use them when you are choosing an image by
size before committing to decoding it** — picking the right thumbnail out of a
set, or rejecting something too large before allocating for it.

They are not merely fast; they are also strict. A file whose Huffman or
quantisation tables are corrupt is refused by `JPEG_TryReadHeader` even though
it never decodes a pixel, because reporting a size for an image that cannot be
decoded is worse than useless to a caller deciding whether to fetch it.

`PngHeader` carries width, height, bit depth, colour type, interlace method, and
the derived `PNGH_iSamplesPerPixel`, `PNGH_bIsPalettised` and `PNGH_bHasAlpha`.

`JpegHeader` carries width, height, precision, component count, the progressive
flag, per-component sampling factors, the derived MCU geometry, the restart
interval, and the JFIF and Adobe-transform flags.

BMP and QOI have no header-only reader; their full decode is cheap.

### 7.3 Animation

```csharp
bool GIF_TryDecodeFrames(byte[] arrData, int iOffset, int iLength,
                         out ImageFrameSet frmOut, out string sReason);
```

Returns every frame, composited, with delays and the loop count. Frame 0 equals
what `GIF_TryDecode` returns for the same file.

### 7.4 The dispatcher

```csharp
bool   IMGDEC_TrySniffFormat  (byte[] arrData, int iOffset, int iLength,
                               out int iFormatOut, out string sReason);
bool   IMGDEC_TryDecodeAny    (byte[] arrData, int iOffset, int iLength,
                               out ImageBuffer imgOut, out string sReason);
bool   IMGDEC_TryValidateWindow(byte[] arrData, int iOffset, int iLength,
                               out string sReason);
string IMGDEC_FormatName      (int iFormat);
```

| Constant | Value |
|---|---|
| `IMGDEC_FORMAT_UNKNOWN` | 0 |
| `IMGDEC_FORMAT_PNG` | 1 |
| `IMGDEC_FORMAT_JPEG` | 2 |
| `IMGDEC_FORMAT_BMP` | 3 |
| `IMGDEC_FORMAT_QOI` | 4 |
| `IMGDEC_FORMAT_GIF` | 5 |

**Sniffing is by magic bytes only, never by file extension** — an extension does
not exist inside a base64 blob and is not trustworthy where it does exist.

`IMGDEC_TryDecodeAny` is what to call when you hold "some image" and do not care
which format it is. All five formats both sniff and decode.

---

## 8. Encoding

All five encoders share one shape:

```csharp
bool PNG_TryEncode (ImageBuffer imgIn, PngEncodeOptions  optIn,
                    out byte[] arrOut, out string sReason);
bool JPEG_TryEncode(ImageBuffer imgIn, JpegEncodeOptions optIn,
                    out byte[] arrOut, out string sReason);
bool BMP_TryEncode (ImageBuffer imgIn, BmpEncodeOptions  optIn,
                    out byte[] arrOut, out string sReason);
bool QOI_TryEncode (ImageBuffer imgIn, QoiEncodeOptions  optIn,
                    out byte[] arrOut, out string sReason);
bool GIF_TryEncode (ImageBuffer imgIn, GifEncodeOptions  optIn,
                    out byte[] arrOut, out string sReason);

bool GIF_TryEncodeFrames(ImageFrameSet frmIn, GifEncodeOptions optIn,
                         out byte[] arrOut, out string sReason);
```

And each has a factory:

```csharp
PngEncodeOptions  PNG_DefaultEncodeOptions();
JpegEncodeOptions JPEG_DefaultEncodeOptions();
BmpEncodeOptions  BMP_DefaultEncodeOptions();
QoiEncodeOptions  QOI_DefaultEncodeOptions();
GifEncodeOptions  GIF_DefaultEncodeOptions();
```

### 8.1 PNG

```csharp
public sealed class PngEncodeOptions
{
    public int  PNGOPT_iCompressionLevel;   // 0 none, 1 fastest, 6 default, 9 smallest
    public bool PNGOPT_bInterlace;          // true writes Adam7
    public int  PNGOPT_iBitDepth;           // 1..16; 0 matches the source buffer
    public int  PNGOPT_iColorType;          // PNG_COLOR_*; 0 matches the source buffer
}
```

Defaults: level **6**, no interlace, depth and colour type matched to the source.

| Colour type | Constant | Depths accepted |
|---|---|---|
| 0 grayscale | `PNG_COLOR_GRAY` | 1, 2, 4, 8, 16 |
| 2 truecolour | `PNG_COLOR_RGB` | 8, 16 |
| 3 palette | `PNG_COLOR_PALETTE` | 1, 2, 4, 8 |
| 4 gray+alpha | `PNG_COLOR_GRAY_ALPHA` | 8, 16 |
| 6 RGBA | `PNG_COLOR_RGBA` | 8, 16 |

**The output shape follows the INPUT shape, never the pixel values.** A
four-channel buffer whose alpha happens to be 255 everywhere is still written as
RGBA. That means you can predict the output without inspecting the pixels, and
an encode-decode round trip returns the same channel count you started with. If
you want the smaller file, convert the buffer down first.

**`PNGOPT_iColorType = 0` means "match the source"**, which collides with
`PNG_COLOR_GRAY = 0` only in appearance: the request it would otherwise express —
grayscale from a buffer that is not already grayscale — is a lossy narrowing this
encoder refuses anyway.

**Palette output picks the narrowest index depth that fits** when
`PNGOPT_iBitDepth` is 0, because the palette is exact and a narrow index is
exactly as lossless as a wide one. An explicit depth is honoured when it fits and
refused when it is too narrow.

**Sub-byte grayscale output requires every sample to be exactly representable**
at that depth. It is not rounded for you. In practice this is less strict than it
sounds: an image decoded from a sub-byte PNG has samples that are multiples of
the replication step by construction, so decode-then-re-encode is the identity.

**More distinct colours than the palette can name is refused**, not quantised.

### 8.2 JPEG

```csharp
public sealed class JpegEncodeOptions
{
    public int JPEGOPT_iQuality;       // 1..100
    public int JPEGOPT_iSubsampling;   // JPEG_SUBSAMPLE_444 / _422 / _420
}
```

Defaults: quality **90**, **4:4:4**.

| Constant | Value |
|---|---|
| `JPEG_QUALITY_MIN` | 1 |
| `JPEG_QUALITY_MAX` | 100 |
| `JPEG_QUALITY_DEFAULT` | 90 |
| `JPEG_SUBSAMPLE_444` | 0 |
| `JPEG_SUBSAMPLE_422` | 1 |
| `JPEG_SUBSAMPLE_420` | 2 |

Quality scales the Annex K quantisation tables. Output is always baseline
(SOF0) — this library decodes progressive JPEG but does not write it.

**A one- or three-channel buffer encodes. Two or four channels are REFUSED**,
naming the alpha: JPEG has no transparency and dropping it silently is exactly
the narrowing described in §4.4. Convert with `IMG_TryConvertChannels` if that is
what you want.

**16-bit input is refused permanently** — baseline JPEG is an 8-bit format.

**Subsampling a grayscale image is refused** rather than ignored; there is no
chroma to reduce.

Chroma is **box-averaged** on the way down, not point-sampled.

### 8.3 BMP

```csharp
public sealed class BmpEncodeOptions
{
    public int  BMPOPT_iBitsPerPixel;   // 4, 8, 24 or 32
    public bool BMPOPT_bRleCompress;    // true writes BI_RLE8 / BI_RLE4
    public bool BMPOPT_bTopDown;        // true writes a negative-height DIB
}
```

Defaults: **24**-bit, no RLE, bottom-up.

- 24-bit writes `BI_RGB`; 32-bit writes `BI_BITFIELDS` with a `BITMAPV4HEADER`.
- 4- and 8-bit write a palettised file, optionally RLE-compressed.
  `BMPOPT_bRleCompress` requires 4 or 8 bits.
- **The palette is exact.** More distinct colours than the depth can name is
  refused, not quantised.
- **An RGBA source asked for 24-bit is refused**, because that drops the alpha.
- **16-bit input is refused.**
- Top-down RLE is refused — the format itself forbids the combination.

### 8.4 QOI

```csharp
public sealed class QoiEncodeOptions
{
    public int QOIOPT_iColorspace;   // QOI_SRGB_LINEAR_ALPHA (0) or QOI_LINEAR (1)
}
```

Default: `QOI_SRGB_LINEAR_ALPHA`.

QOI is lossless. The format itself stores only RGB or RGBA, so **a grayscale
buffer is WIDENED for you** — 1 channel becomes RGB, 2 becomes RGBA — which is
lossless and therefore silent, per §4.4. **16-bit input is refused**; QOI is an
8-bit format.

The colourspace byte is metadata: it is written to the header and returned on
decode, and changes no pixel.

### 8.5 GIF

```csharp
public sealed class GifEncodeOptions
{
    public int  GIFOPT_iPaletteSize;   // 2..256, rounded up to a power of two
    public bool GIFOPT_bDither;        // Floyd-Steinberg, quantised path only
    public int  GIFOPT_iLoopCount;     // 0..65535, or GIFOPT_LOOP_FROM_FRAMES
    public int  GIFOPT_iTransparency;  // GIF_TRANSPARENCY_*
}
```

Defaults: **256** entries, no dither, `GIFOPT_LOOP_FROM_FRAMES`, and
`GIF_TRANSPARENCY_AUTO`.

**`GIFOPT_iPaletteSize` counts TABLE ENTRIES, not usable colours.** A reserved
transparent index comes out of that budget, so 16 entries with an index reserved
gives fifteen colours. Entries is what the format's own size field expresses and
what a caller managing file size cares about.

**The exact palette is always tried first.** An image whose colours fit the table
is encoded losslessly, byte for byte, and `GIFOPT_bDither` has no effect on it —
an exact palette approximates nothing, so there is no error to diffuse. Only when
the image does not fit does the adaptive quantiser run.

| `GIFOPT_iTransparency` | Value | Meaning |
|---|---|---|
| `GIF_TRANSPARENCY_AUTO` | 0 | **Default.** Reserve an index only when the source needs one, or when animating and delta encoding would pay for it. |
| `GIF_TRANSPARENCY_NONE` | 1 | Never declare one, so every entry is a colour. **Refused** when the source carries transparent pixels. Also disables delta encoding on an animation. |
| `GIF_TRANSPARENCY_RESERVE` | 2 | Always reserve index 0, even for a fully opaque still. |

`GIF_TRANSPARENCY_NONE` asked of an image that genuinely has holes is a refusal
rather than an override, for the reason in §4.4.

**Partial alpha is refused.** GIF has exactly one bit of transparency, so an
alpha of 128 cannot be written without deciding on your behalf whether that pixel
vanishes or becomes solid. The reason string **names the offending pixel's
coordinates**, so you do not have to search the image for the one bad sample.

> **The one exception to "lossless with an exact palette":** the colour beneath a
> fully transparent pixel is not preserved and comes back as zero. Such a pixel is
> never drawn, so its RGB carries no information — and a real RGBA source can hold
> hundreds of different colours under alpha 0, which would exhaust the palette on
> pixels nobody can see. **The guarantee is exact in every VISIBLE sample:** alpha
> everywhere, and colour wherever alpha is not zero.

#### Animation

`GIF_TryEncodeFrames` writes all frames with one shared global colour table.

**`GIFOPT_iLoopCount` is read literally for any value from 0 to 65535, including
0, which means "loop forever".** The sentinel `GIFOPT_LOOP_FROM_FRAMES` (−1) is
the default and takes the count from `ImageFrameSet.FRM_iLoopCount` instead —
which is what makes a decode-then-re-encode round trip preserve the original
count. Any other negative value is refused, as is anything above 65535.

**Delays must be a multiple of 10 ms.** GIF counts hundredths of a second, so
105 ms cannot be written, and rounding it would mean silently returning a
different animation from the one you handed in. The reason string names the
multiple to use.

Two encoding forms are chosen automatically. **Delta**: later frames are cropped
to the changed rectangle and unchanged pixels marked transparent, with disposal 1.
**Full canvas**: every frame covers the screen with disposal 2. The test is
whether a transparent hole ever *opens* between consecutive frames — if one does,
delta encoding cannot express it and the whole animation falls back to full
canvas.

---

## 9. Resampling

```csharp
bool RESAMP_TryResize(ImageBuffer imgIn, int iNewWidth, int iNewHeight,
                      int iFilter, int iAlphaMode,
                      out ImageBuffer imgOut, out string sReason);
```

| Filter | Value | Character |
|---|---|---|
| `RESAMP_FILTER_BOX` | 1 | a trapezoid with one-pixel ramps; a true box at integer ratios |
| `RESAMP_FILTER_TRIANGLE` | 2 | linear; bilinear when upsampling |
| `RESAMP_FILTER_CUBIC` | 3 | the cubic B-spline; smooth and deliberately blurring |
| `RESAMP_FILTER_CATMULLROM` | 4 | an interpolating cubic; sharp, and **exact at scale 1** |
| `RESAMP_FILTER_MITCHELL` | 5 | Mitchell-Netravali B=C=1/3; the usual compromise |

**There is no filter 0.** An uninitialised filter has no correct meaning, so it is
refused rather than quietly treated as box.

**For a photographic downscale, Catmull-Rom or Mitchell. For an upscale,
Triangle or Catmull-Rom. Box is for exact integer reductions.**

`TRIANGLE` and `CATMULLROM` reproduce the source exactly at scale 1; `CUBIC` and
`MITCHELL` do not, and must not — they are blurring kernels by design.

### 9.1 Alpha mode

| Constant | Value |
|---|---|
| `RESAMP_ALPHA_PREMULTIPLIED` | 0 — **the default** |
| `RESAMP_ALPHA_STRAIGHT` | 1 |

**Premultiplied is the default and is the right answer for coverage alpha.**
Colour is multiplied by coverage on the way in and divided by it on the way out,
so an invisible pixel contributes no colour to its visible neighbours.

Take an opaque white pixel beside a fully transparent black one, averaged 50/50:

| Mode | Result |
|---|---|
| `_STRAIGHT` | `(128,128,128,128)` — **grey**, a 127-count halo |
| `_PREMULTIPLIED` | `(255,255,255,128)` — **still white** |

**Use `_STRAIGHT` only when alpha is a MASK rather than coverage** — a selection
channel over real data, where the RGB beneath alpha 0 is meaningful and
premultiplying would destroy it.

Zero being a legal value here, where it is refused for `iFilter`, is deliberate:
an uninitialised alpha mode *does* have a correct meaning — the caller has not
thought about alpha, and the behaviour that is right when you have not thought
about it is premultiplied.

The mode is accepted and has no effect on 1- and 3-channel images, so a caller
resizing a mixed set need not vary the argument per image.

> **What premultiplying costs, so it is not rediscovered as a bug:** colour
> channels become dependent on the alpha channel, so **"resizing RGBA equals
> resizing each channel alone" holds in `_STRAIGHT` mode and NOT in
> `_PREMULTIPLIED`.** What it does not cost is precision — the intermediate is
> float, so un-premultiplying recovers the original exactly rather than
> amplifying a quantisation error.

### 9.2 Behaviour

- **The result keeps the source's depth and channel count.** Resizing is a
  geometry operation; narrowing 16 bits to 8 on the way through would be a
  second, invisible decision.
- **A sample is rounded exactly once**, at the final write. The intermediate is
  float, which is what prevents two-pass banding on a gradient.
- **Both passes handle edges by folding** the out-of-range kernel weight back
  onto the clamped edge pixel, so a border is not darkened.
- Every degenerate geometry works: one pixel in, one pixel out, a single row or
  column, a row resized into a column, and extreme ratios in both directions.

---

## 10. Core primitives

These are public because they are genuinely reusable, and because the codecs
needed them built correctly anyway.

### 10.1 Checksums

```csharp
uint CRC32_Compute(byte[] arrData, int iOffset, int iLength);
uint CRC32_Begin();
uint CRC32_Update(uint uRunning, byte[] arrData, int iOffset, int iLength);
uint CRC32_Finish(uint uRunning);

uint ADLER_Compute(byte[] arrData, int iOffset, int iLength);
uint ADLER_Begin();
uint ADLER_Update(uint uRunning, byte[] arrData, int iOffset, int iLength);
```

CRC-32/ISO-HDLC, as PNG and zip use it: `CRC32_Compute("123456789")` is
`0xCBF43926`. Adler-32 is RFC 1950's.

The incremental trio exists because a PNG chunk's CRC covers the type bytes and
the data bytes but **not** the length field, and those are two separate spans.
`CRC32_Finish` applies the final inversion; do not skip it.

### 10.2 zlib

```csharp
bool ZLIB_TryDeflate(byte[] arrData, int iOffset, int iLength, int iLevel,
                     out byte[] arrOut, out string sReason);
bool ZLIB_TryInflate(byte[] arrData, int iOffset, int iLength, int iMaxOutput,
                     out byte[] arrOut, out string sReason);
```

RFC 1950 framing over the framework's `DeflateStream`. `iLevel` is 0 to 9.

**`iMaxOutput` is enforced DURING expansion, not after** — that is what makes it
a decompression-bomb guard rather than a post-hoc check. Pass the exact expected
size when you know it, as the PNG decoder does; the inflate then doubles as a
length check.

The Adler-32 trailer **is verified**. `DeflateStream` alone will happily inflate
a damaged payload without complaining.

> **Known platform difference:** Mono 6.8 ignores
> `CompressionLevel.NoCompression` and deflates anyway. The output is still a
> valid zlib stream and still decodes; only the size differs. Do not rely on
> "store" actually storing.

### 10.3 Byte readers and writers

```csharp
ByteReader(byte[] arrData, int iOffset, int iLength);

int  BYTER_Remaining();               int  BYTER_Position();
bool BYTER_TrySeek(int iNewPos);      bool BYTER_TrySkip(int iCount);
bool BYTER_TryPeekU8 (out int iValueOut);
bool BYTER_TryReadU8 (out int iValueOut);
bool BYTER_TryReadU16BE(out int iValueOut);
bool BYTER_TryReadU16LE(out int iValueOut);
bool BYTER_TryReadU32BE(out uint uValueOut);
bool BYTER_TryReadU32LE(out uint uValueOut);
bool BYTER_TryReadBytes(int iCount, out byte[] arrValueOut);
bool BYTER_TryReadInto(byte[] arrDestIo, int iDestOffset, int iCount);
```

**A failed read leaves the cursor untouched**, so a caller can report exactly
where parsing stopped.

```csharp
ByteWriter(int iInitialCapacity);

int    BYTEW_Length();    byte[] BYTEW_Buffer();    byte[] BYTEW_ToArray();
void   BYTEW_WriteU8(int iValue);
void   BYTEW_WriteU16BE(int iValue);   void BYTEW_WriteU16LE(int iValue);
void   BYTEW_WriteU32BE(uint uValue);  void BYTEW_WriteU32LE(uint uValue);
void   BYTEW_WriteBytes(byte[] arrSrc, int iOffset, int iLength);
```

**`BYTEW_Buffer` exposes the live array** — it is not a copy, and its length is
the capacity, not the content length. Use `BYTEW_Length` for the latter and
`BYTEW_ToArray` when you want an owned copy. The live accessor exists so a PNG
encoder can checksum what it just wrote without copying the whole output per
chunk.

---

## 11. Accuracy and what "lossless" means per format

| Format | Guarantee |
|---|---|
| **PNG** | Lossless both ways. |
| **QOI** | Lossless both ways; the encoder reproduces the reference encoder byte for byte. |
| **BMP** | Lossless both ways. |
| **GIF** | Lossless **when the image fits the palette**, in every visible sample (see §8.5). Lossy when the quantiser runs — that is what you asked for. |
| **JPEG** | Lossy by construction, in both directions. |
| **Resample** | Lossy by construction; exact at scale 1 for the two interpolating filters. |

Every tolerance in this library's own test suite was **measured**, not chosen —
each constant was set to zero and the reported worst case read back. For context,
the decoder agrees with the platform decoder it was validated against to within
1 sample count on grayscale JPEG, 3 on 4:4:4 colour, and 3 on 4:2:2 and 4:2:0
photographs. Progressive JPEG decodes **identically** to its baseline twin, at
tolerance zero.

---

## 12. Deviations from the reference sources

Each file names its own upstream in a lineage comment. Where this library
deliberately departs from that upstream, the file header records it as a
numbered DEVIATION. They are listed here because a reader diffing against the
original will find them and should find them explained.

| File | Deviation |
|---|---|
| `QoiCodec.cs` | Refuses a truncated file (the reference fills the remainder and returns success); verifies the 8-byte end marker (the reference ignores it); indexes a run's pixel on the encode side, which makes all eight reference images re-encode byte-identically. |
| `PngCodec.cs` | Refuses a palette index beyond the palette (the reference cannot — its palette is a fixed stack array, so an out-of-range index builds a pixel out of whatever was in memory). Treats the IHDR-derived size as an exact inflate cap rather than a backstop. |
| `JpegCodec.cs` | **Two**, both matching libjpeg-turbo 3.1.4's `h2v1_fancy_upsample` against a defect in the transcription source: the last interpolated output of the horizontal chroma upsampler weights the *nearer* sample by three (the source swaps the operands, which turns a monotonic input into a non-monotonic output), and the interior rounding bias alternates +1 / +2 by output parity, which is libjpeg's ordered dither. |
| `GifCodec.cs` | Walks the LZW prefix chain with an explicit stack rather than recursion — a C# stack overflow cannot be caught, so a crafted file would otherwise be fatal. Stores palette entries RGBA rather than BGRA, which removes a background-fill colour swap present upstream. Implements "restore to previous" from the specification rather than the source's out-of-bounds frame arithmetic. Clears disposal 2 to **transparent** rather than to the background colour, because that is what real readers do. Fixes an alpha read in the quantiser's pixel swap. |

---

## 13. Public-for-testability

Beyond the consumer API above, the library exposes a second, larger group of
symbols: chunk and marker walkers, the block decoder, the IDCT, the five chroma
resamplers, both halves of GIF's LZW, the quantiser's internals, and the
resampler's kernel and coefficient machinery.

**These are public so the test suite can drive them directly against
independently computed expectations** — the IDCT against the mathematical
definition of the transform, the chroma filters against hand-worked arithmetic,
the Huffman tables against a canonical reconstruction, the palette search against
an exhaustive scan. None of that is reachable through the top-level entry points.
The alternative, `InternalsVisibleTo`, would couple the library to the name of a
test assembly, which is a worse trade for code whose whole point is to be lifted
into another project.

**They are not part of the stable surface.** Treat everything in §5 through §10
as the API; treat this group as implementation that happens to be reachable. It
may change without notice.

---

## 14. Index of the consumer API

**Types:** `ImageBuffer`, `ImageFrameSet`, `PngHeader`, `JpegHeader`,
`JpegComponent`, `PngEncodeOptions`, `JpegEncodeOptions`, `BmpEncodeOptions`,
`QoiEncodeOptions`, `GifEncodeOptions`, `ByteReader`, `ByteWriter`.

**Static classes:** `PngCodec`, `JpegCodec`, `BmpCodec`, `QoiCodec`, `GifCodec`,
`ImageResampler`, `ImageDecoderDispatch`, `Crc32`, `Adler32`, `ZlibStreamCodec`.

| Call | Section |
|---|---|
| `ADLER_Begin`, `ADLER_Compute`, `ADLER_Update` | 10.1 |
| `BMP_DefaultEncodeOptions`, `BMP_TryDecode`, `BMP_TryEncode` | 7.1, 8.3 |
| `BYTER_Position`, `BYTER_Remaining`, `BYTER_TryPeekU8`, `BYTER_TryReadBytes`, `BYTER_TryReadInto`, `BYTER_TryReadU8`, `BYTER_TryReadU16BE`, `BYTER_TryReadU16LE`, `BYTER_TryReadU32BE`, `BYTER_TryReadU32LE`, `BYTER_TrySeek`, `BYTER_TrySkip` | 10.3 |
| `BYTEW_Buffer`, `BYTEW_Length`, `BYTEW_ToArray`, `BYTEW_WriteBytes`, `BYTEW_WriteU8`, `BYTEW_WriteU16BE`, `BYTEW_WriteU16LE`, `BYTEW_WriteU32BE`, `BYTEW_WriteU32LE` | 10.3 |
| `CRC32_Begin`, `CRC32_Compute`, `CRC32_Finish`, `CRC32_Update` | 10.1 |
| `FRM_FrameCount`, `FRM_TryValidate` | 6 |
| `GIF_DefaultEncodeOptions`, `GIF_TryDecode`, `GIF_TryDecodeFrames`, `GIF_TryEncode`, `GIF_TryEncodeFrames` | 7.1, 7.3, 8.5 |
| `IMG_RowStride`, `IMG_SampleCount`, `IMG_SampleIndex`, `IMG_TryAllocate`, `IMG_TryConvertChannels`, `IMG_TryValidate` | 5.2 |
| `IMGDEC_FormatName`, `IMGDEC_TryDecodeAny`, `IMGDEC_TrySniffFormat`, `IMGDEC_TryValidateWindow` | 7.4 |
| `JPEG_DefaultEncodeOptions`, `JPEG_TryDecode`, `JPEG_TryEncode`, `JPEG_TryReadHeader` | 7.1, 7.2, 8.2 |
| `PNG_DefaultEncodeOptions`, `PNG_TryDecode`, `PNG_TryEncode`, `PNG_TryReadHeader` | 7.1, 7.2, 8.1 |
| `QOI_DefaultEncodeOptions`, `QOI_TryDecode`, `QOI_TryEncode` | 7.1, 8.4 |
| `RESAMP_FilterName`, `RESAMP_IsKnownFilter`, `RESAMP_TryResize` | 9 |
| `ZLIB_TryDeflate`, `ZLIB_TryInflate` | 10.2 |

---

## 15. Worked examples

**Decode anything, resize it to a thumbnail, write a PNG:**

```csharp
ImageBuffer imgFull;
ImageBuffer imgSmall;
byte[]      arrPng;
string      sReason;

if (!ImageDecoderDispatch.IMGDEC_TryDecodeAny(arrInput, 0, arrInput.Length,
                                              out imgFull, out sReason))
{
    return(false);
}

if (!ImageResampler.RESAMP_TryResize(imgFull, 128, 128,
                                     ImageResampler.RESAMP_FILTER_CATMULLROM,
                                     ImageResampler.RESAMP_ALPHA_PREMULTIPLIED,
                                     out imgSmall, out sReason))
{
    return(false);
}

if (!PngCodec.PNG_TryEncode(imgSmall, null, out arrPng, out sReason))
{
    return(false);
}
```

**Pick an image by size without decoding it:**

```csharp
PngHeader hdr;
string    sReason;

if (PngCodec.PNG_TryReadHeader(arrCandidate, 0, arrCandidate.Length,
                               out hdr, out sReason))
{
    if (hdr.PNGH_iWidth <= 512 && hdr.PNGH_iHeight <= 512)
    {
        /* small enough to be worth decoding */
    }
}
```

**Write a 16-colour dithered GIF:**

```csharp
GifEncodeOptions opt;
byte[]           arrGif;
string           sReason;

opt = GifCodec.GIF_DefaultEncodeOptions();
opt.GIFOPT_iPaletteSize = 16;
opt.GIFOPT_bDither      = true;

if (!GifCodec.GIF_TryEncode(img, opt, out arrGif, out sReason))
{
    return(false);
}
```

**Encode a JPEG at 4:2:0, handling an RGBA source:**

```csharp
JpegEncodeOptions opt;
ImageBuffer       imgRgb;
byte[]            arrJpeg;
string            sReason;

/* JPEG has no alpha and will not drop it silently, so ask explicitly. */
if (img.IMG_iChannels == ImageBuffer.IMG_CHANNELS_RGBA)
{
    if (!ImageBuffer.IMG_TryConvertChannels(img, ImageBuffer.IMG_CHANNELS_RGB,
                                            out imgRgb, out sReason))
    {
        return(false);
    }
}
else
{
    imgRgb = img;
}

opt = JpegCodec.JPEG_DefaultEncodeOptions();
opt.JPEGOPT_iQuality     = 85;
opt.JPEGOPT_iSubsampling = JpegCodec.JPEG_SUBSAMPLE_420;

if (!JpegCodec.JPEG_TryEncode(imgRgb, opt, out arrJpeg, out sReason))
{
    return(false);
}
```
