/*----------------------------------------------------------------------------*/
/* SPDX-License-Identifier: MIT OR GPL-2.0-or-later                           */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* Dual-licensed at the recipient's choice -- see LICENSE in this folder for  */
/* the full terms of both licences.  GPL-2.0 full text in GPL-2.0.txt.        */
/*----------------------------------------------------------------------------*/
/* Transcoded from stb_image.h v2.30 and stb_image_write.h v1.16              */
/* (nothings/stb, commit 2c980bb5): the decoder from stbi__bmp_parse_header,  */
/* stbi__bmp_load and their bit helpers stbi__high_bit, stbi__bitcount and    */
/* stbi__shiftsigned; the encoder from stbi_write_bmp_core.  Structure and    */
/* variable roles are kept -- the mask/shift/count triples, the "easy" fast   */
/* paths, the all-alpha-zero fixup, the bottom-up flip -- so this file can be */
/* diffed against its source when it misbehaves.  stb is dual-licensed MIT or */
/* public domain, satisfying both arms of this file's dual licence.           */
/*----------------------------------------------------------------------------*/
/* BmpCodec.cs -- Windows and OS/2 device-independent bitmaps         (BMP_)  */
/*----------------------------------------------------------------------------*/
/* BMP looks like the simplest format here and is in fact the messiest, less  */
/* because it is hard than because it accumulated thirty years of variants    */
/* that all still occur in the wild.  What this codec reads:                  */
/*                                                                            */
/*   header sizes   12 (BITMAPCOREHEADER, OS/2), 40 (BITMAPINFOHEADER),       */
/*                  56 (undocumented Adobe variant), 108 (V4), 124 (V5)       */
/*   bit depths     1, 4, 8 palettised; 16, 24, 32 direct                     */
/*   compression    BI_RGB, BI_BITFIELDS (explicit channel masks), and        */
/*                  BI_RLE8 / BI_RLE4 run-length compression                  */
/*   row order      bottom-up (positive height) and top-down (negative)       */
/*                                                                            */
/* WHAT IT DOES NOT READ: the BI_JPEG and BI_PNG modes, which embed a whole   */
/* JPEG or PNG file inside the BMP container.  Both are refused by name with  */
/* a reason string saying what was found, rather than failing obscurely.      */
/*                                                                            */
/* THAT OMISSION IS DELIBERATE AND PERMANENT, not a gap waiting to be filled. */
/* Those two modes were added to Windows GDI for the DEVICE path -- so a      */
/* printer driver could hand already-compressed data to hardware that         */
/* decompresses it itself -- and not as an on-disk BMP variant.  Measured     */
/* rather than assumed: GDI+ itself REFUSES to load a well-formed file in     */
/* either mode, and its own BMP output is always BI_RGB.  No ordinary encoder */
/* produces one, so refusing them costs a reader nothing real.                */
/*                                                                            */
/* The refusal therefore names a CAPABILITY this codec does not have, never a */
/* piece of work that will arrive later, and it fires during the header parse */
/* before any buffer is allocated.                                            */
/*                                                                            */
/* RLE USED TO BE ON THAT LIST AND IS NOT ANY MORE.  BI_RLE8 and BI_RLE4 are  */
/* read AND written, including the delta and absolute modes -- see            */
/* BMP_TryDecodeRle and BMP_WriteRle8Row.  The paragraph that used to sit     */
/* here said adding RLE "would be new work rather than transcription",        */
/* which was the argument for leaving it out; it was wrong, and the work      */
/* was done.                                                                  */
/*                                                                            */
/* THREE THINGS ABOUT BMP THAT CATCH EVERY FIRST IMPLEMENTATION:              */
/*                                                                            */
/*   1. ROWS ARE STORED BOTTOM-UP by default.  A positive height in the       */
/*      header means the FIRST row in the file is the BOTTOM row of the       */
/*      image.  A negative height means top-down.  Getting this backwards     */
/*      produces a vertically mirrored image that still decodes "correctly"   */
/*      by every other measure.                                               */
/*                                                                            */
/*   2. EVERY ROW IS PADDED TO A FOUR-BYTE BOUNDARY.  Not every row of        */
/*      pixels -- every row of BYTES, after the pixels.  A 3-pixel 24-bit     */
/*      row is 9 bytes of colour and 3 bytes of padding.  Ignoring the        */
/*      padding shears the image progressively, which looks like a stride     */
/*      bug and is one.                                                       */
/*                                                                            */
/*   3. CHANNELS ARE STORED B, G, R -- not R, G, B.  The format predates the  */
/*      convention and never changed.                                         */
/*                                                                            */
/* THE ALL-ALPHA-ZERO FIXUP is stb's, and it is a compatibility hack rather   */
/* than a spec behaviour: a great many 32-bit BMPs in the wild set every      */
/* alpha byte to zero while plainly intending an opaque image, because the    */
/* fourth byte was historically ignored.  Honouring that literally produces a */
/* completely transparent picture.  So if EVERY alpha in the image is zero,   */
/* the whole channel is forced to 255.  An image genuinely meant to be        */
/* invisible is indistinguishable from the broken case, and the useful        */
/* interpretation wins.                                                       */
/*                                                                            */
/* PORTABILITY: pure array and integer work, no platform surface.             */
/*----------------------------------------------------------------------------*/

using System;
using System.Collections.Generic;

namespace GraphicsEncDec
{
    /*------------------------------------------------------------------------*/
    /* BmpEncodeOptions:                                                      */
    /* Encoder settings for BMP.  The format is uncompressed, so there is no  */
    /* quality or effort setting -- only the pixel layout to write.           */
    /*------------------------------------------------------------------------*/

    public sealed class BmpEncodeOptions
    {
        /*--------------------------------------------------------------------*/
        /* 4 and 8 write PALETTISED files with a 40-byte BITMAPINFOHEADER and */
        /* a colour table. The source must have at most 16 or 256 distinct    */
        /* colours respectively; see BMP_TryBuildPalette for why more is      */
        /* refused rather than quantised.                                     */
        /*                                                                    */
        /* 24 writes BI_RGB with a 40-byte BITMAPINFOHEADER and no alpha.     */
        /* 32 writes BI_BITFIELDS with a 108-byte V4 header carrying explicit */
        /* channel masks -- which is what stb does, and for a good reason     */
        /* recorded at BMP_TryEncode: plain BI_RGB at 32 bits with an alpha   */
        /* mask is ignored by most readers.                                   */
        /*--------------------------------------------------------------------*/

        public int BMPOPT_iBitsPerPixel;                 // 4, 8, 24 or 32

        /*--------------------------------------------------------------------*/
        /* true run-length compresses a palettised file: BI_RLE8 at 8 bits,   */
        /* BI_RLE4 at 4. Ignored at 24 and 32 bits, where the format defines  */
        /* no compression.                                                    */
        /*                                                                    */
        /* Off by default. RLE is smaller on flat synthetic images and LARGER */
        /* on photographic ones -- a row with no repeats costs a two-byte     */
        /* absolute-run header on top of its pixels -- so it is a choice the  */
        /* caller makes about their own data, not one to make for them.       */
        /*--------------------------------------------------------------------*/

        public bool BMPOPT_bRleCompress;                 // false writes uncompressed BI_RGB

        /*--------------------------------------------------------------------*/
        /* true writes a negative height, meaning the first row in the file   */
        /* is the TOP row.  Legal, widely supported, and not what stb does;   */
        /* the default stays bottom-up because that is what every BMP writer  */
        /* has emitted since 1990 and some old readers accept nothing else.   */
        /*--------------------------------------------------------------------*/

        public bool BMPOPT_bTopDown;                     // false writes the conventional bottom-up form
    }

    /*------------------------------------------------------------------------*/
    /* BmpCodec:                                                              */
    /* Static-only.  Decodes and encodes Windows and OS/2 bitmaps.            */
    /*------------------------------------------------------------------------*/

    public static class BmpCodec
    {
        /*--------------------------------------------------------------------*/
        /* Header sizes, which are how a BMP declares its own dialect.  The   */
        /* DIB header's first field is its own length, and that length is the */
        /* only way to tell the variants apart.                               */
        /*--------------------------------------------------------------------*/

        private const int BMP_HDR_CORE = 12;             // BITMAPCOREHEADER, OS/2 1.x
        private const int BMP_HDR_INFO = 40;             // BITMAPINFOHEADER, the common case
        private const int BMP_HDR_A56  = 56;             // undocumented Adobe variant with masks
        private const int BMP_HDR_V4   = 108;            // BITMAPV4HEADER
        private const int BMP_HDR_V5   = 124;            // BITMAPV5HEADER

        private const int BMP_FILE_HEADER_SIZE = 14;     // "BM", size, two reserved words, pixel offset

        /*--------------------------------------------------------------------*/
        /* Compression identifiers from the DIB header.                       */
        /*--------------------------------------------------------------------*/

        private const int BMP_BI_RGB       = 0;          // no compression
        private const int BMP_BI_RLE8      = 1;          // 8-bit run length, read and written
        private const int BMP_BI_RLE4      = 2;          // 4-bit run length, read and written
        private const int BMP_BI_BITFIELDS = 3;          // explicit channel masks follow
        private const int BMP_BI_JPEG      = 4;          // an entire JPEG embedded -- refused
        private const int BMP_BI_PNG       = 5;          // an entire PNG embedded -- refused

        /*--------------------------------------------------------------------*/
        /* Limits.  The palette cap is the format's own -- an 8-bit index     */
        /* cannot name more than 256 entries.  The extra-data cap is stb's    */
        /* guard against a pixel offset that points implausibly far into the  */
        /* file, which would otherwise be a licence to skip over arbitrary    */
        /* amounts of data on a corrupt header.                               */
        /*--------------------------------------------------------------------*/

        private const int BMP_MAX_PALETTE    = 256;      // entries an index can address
        private const int BMP_MAX_EXTRA_DATA = 256 * 4;  // slack allowed between header and pixels

        /*--------------------------------------------------------------------*/
        /* Words the V4 header carries after its channel mask quad: the       */
        /* colour space type, the nine words of CIEXYZTRIPLE endpoints, and   */
        /* the three gamma words. Thirteen in all.                            */
        /*                                                                    */
        /* NAMED, AND DERIVED HERE, BECAUSE GETTING IT WRONG IS SILENT.       */
        /* A BITMAPV4HEADER is 108 bytes: 40 for the BITMAPINFOHEADER fields, */
        /* 16 for the four masks, and 52 -- thirteen words -- for this tail.  */
        /* An encoder that writes the wrong number produces a header whose    */
        /* real length disagrees with the 108 it declares, so the pixel       */
        /* offset points into the wrong place and the decoder reads header    */
        /* bytes as pixels. Nothing rejects the file; it simply decodes to    */
        /* rubbish.                                                           */
        /*                                                                    */
        /* This was written as 17 in the first draft and testing caught it:   */
        /* 24-bit output was perfect while every 32-bit image came            */
        /* back wrong from the first pixel, and GDI+ disagreed with us on     */
        /* exactly the files we had written ourselves.                        */
        /*--------------------------------------------------------------------*/

        private const int BMP_V4_TAIL_WORDS = 13;        // (108 - 40 - 16) / 4

        /*---------------------------------------------------------------------*/
        /* BMP_DefaultEncodeOptions:                                           */
        /*                                                                     */
        /* Returns the settings used when a caller passes null options: 24-bit */
        /* bottom-up, which is the most widely readable BMP there is.  A       */
        /* static factory rather than a second constructor, because no two     */
        /* callables in this library share a name.                             */
        /*                                                                     */
        /* Note that 24 is the default even for a source WITH alpha.  That is  */
        /* deliberate: silently promoting to 32-bit would change the file's    */
        /* shape based on its content, and a caller who wants alpha preserved  */
        /* should say so.  BMP_TryEncode refuses rather than discards -- see   */
        /* there.                                                              */
        /*                                                                     */
        /* Arguments:                                                          */
        /*     None.                                                           */
        /*                                                                     */
        /* Returns:                                                            */
        /*     BmpEncodeOptions  : A new options object with defaults set.     */
        /*---------------------------------------------------------------------*/

        public static BmpEncodeOptions BMP_DefaultEncodeOptions()
        {
            BmpEncodeOptions optNew;

            optNew = new BmpEncodeOptions();
            optNew.BMPOPT_iBitsPerPixel = 24;
            optNew.BMPOPT_bTopDown      = false;
            optNew.BMPOPT_bRleCompress  = false;

            /* The most compatible BMP shape. */
            return(optNew);
        }

        /*--------------------------------------------------------------------*/
        /* BMP_TryDecode:                                                     */
        /*                                                                    */
        /* Decodes a BMP into an 8-bit buffer with 3 or 4 channels, choosing  */
        /* 4 only when the file actually carries an alpha mask.               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     arrData  : Buffer holding the BMP file.  Must not be null.     */
        /*     iOffset  : Index of the first byte of the image.               */
        /*     iLength  : Byte count of the whole file.                       */
        /*     imgOut   : Receives the decoded image; null on failure.        */
        /*     sReason  : Receives "" on success, or why the file was         */
        /*                rejected.                                           */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool     : true when imgOut holds the decoded image.           */
        /*--------------------------------------------------------------------*/

        public static bool BMP_TryDecode(byte[] arrData, int iOffset, int iLength,
                                         out ImageBuffer imgOut, out string sReason)
        {
            ByteReader  brIn;
            ImageBuffer imgNew;
            byte[]      arrPalette;
            uint        uMaskR;
            uint        uMaskG;
            uint        uMaskB;
            uint        uMaskA;
            uint        uAllAlpha;
            int         iPixelOffset;
            int         iHeaderSize;
            int         iWidth;
            int         iHeightRaw;
            int         iHeight;
            int         iBitsPerPixel;
            int         iCompression;
            int         iPaletteSize;
            int         iChannels;
            bool        bBottomUp;

            imgOut  = null;
            sReason = "";

            if (arrData == null)
            {   /* Missing argument, not malformed image data. */
                throw new ArgumentNullException("arrData");
            }

            if (!ImageDecoderDispatch.IMGDEC_TryValidateWindow(arrData, iOffset, iLength, out sReason))
            {   /* The window itself is unusable; pass its reason up. */
                return(false);
            }

            brIn = new ByteReader(arrData, iOffset, iLength);

            if (!BMP_TryParseHeader(brIn, out iPixelOffset, out iHeaderSize, out iWidth,
                                    out iHeightRaw, out iBitsPerPixel, out iCompression,
                                    out uMaskR, out uMaskG, out uMaskB, out uMaskA, out sReason))
            {   /* The header named its own problem. */
                return(false);
            }

            /* A POSITIVE height means bottom-up, which is the surprising   */
            /* default; negative means top-down.  See the file header.      */
            bBottomUp = (iHeightRaw > 0);

            if (iHeightRaw < 0)
            {   /* Top-down: the magnitude is the real height. */
                iHeight = -iHeightRaw;
            }
            else
            {   /* Bottom-up: the value is the height as it stands. */
                iHeight = iHeightRaw;
            }

            if (iWidth < 1 || iHeight < 1)
            {   /* A zero or negative width, or a zero height, has no pixels. */
                sReason = "BMP: dimensions " + iWidth.ToString() + "x" + iHeight.ToString() + " are not positive";
                return(false);
            }

            if (iWidth > ImageBuffer.IMG_MAX_DIMENSION || iHeight > ImageBuffer.IMG_MAX_DIMENSION)
            {   /* Absurd dimensions -- treat as corruption rather than attempting the allocation. */
                sReason = "BMP: dimensions " + iWidth.ToString() + "x" + iHeight.ToString() + " exceed the limit";
                return(false);
            }

            /* stb's channel rule, kept exactly: an alpha MASK is what makes    */
            /* the image four-channel, not the bit depth.  The 24-bit special   */
            /* case exists because a V4 header can declare an alpha mask        */
            /* alongside 24-bit pixels that have nowhere to put alpha.          */
            if (iBitsPerPixel == 24 && uMaskA == 0xFF000000u)
            {   /* Alpha mask present but unstorable at this depth; ignore it. */
                iChannels = 3;
            }
            else if (uMaskA != 0u)
            {   /* A real alpha mask, so the image has four channels. */
                iChannels = 4;
            }
            else
            {   /* No alpha anywhere. */
                iChannels = 3;
            }

            if (!ImageBuffer.IMG_TryAllocate(iWidth, iHeight, iChannels,
                                             ImageBuffer.IMG_BITS_8, out imgNew, out sReason))
            {   /* Geometry rejected by the allocator. */
                return(false);
            }

            /* Palette, for the sub-16-bit depths.  Absent otherwise. */
            arrPalette   = null;
            iPaletteSize = 0;

            if (iBitsPerPixel < 16)
            {   /* Palettised: the table sits between the DIB header and the pixels. */
                if (!BMP_TryReadPalette(brIn, iHeaderSize, iPixelOffset,
                                        out arrPalette, out iPaletteSize, out sReason))
                {   /* The palette could not be read; the reader says why. */
                    return(false);
                }
            }

            /* Seek to the pixel data.  stb tracks a running "extra_read"      */
            /* count and skips relative to it; using the file's own absolute   */
            /* offset instead is equivalent and removes a whole class of       */
            /* off-by-one, because the offset is exactly what the field        */
            /* means.                                                          */
            if (!brIn.BYTER_TrySeek(iPixelOffset))
            {   /* The declared pixel offset is outside the file. */
                sReason = "BMP: pixel data offset " + iPixelOffset.ToString() + " is outside the file";
                return(false);
            }

            uAllAlpha = 0u;

            if (iCompression == BMP_BI_RLE8 || iCompression == BMP_BI_RLE4)
            {   /* Run-length encoded palettised data.  A different shape     */
                /* entirely from the packed-row form below: the stream is a   */
                /* sequence of operations rather than rows of indices.        */
                if (!BMP_TryDecodeRle(brIn, imgNew, arrPalette, iPaletteSize,
                                      iBitsPerPixel, iChannels, out sReason))
                {   /* The RLE stream was malformed; the decoder says how. */
                    return(false);
                }
            }
            else if (iBitsPerPixel < 16)
            {   /* Palettised path: indices into the table just read. */
                if (!BMP_TryDecodePalettised(brIn, imgNew, arrPalette, iPaletteSize,
                                             iBitsPerPixel, iChannels, out sReason))
                {   /* Pixel data ran out or an index was out of range. */
                    return(false);
                }
            }
            else
            {   /* Direct-colour path: 16, 24 or 32 bits per pixel. */
                if (!BMP_TryDecodeDirect(brIn, imgNew, iBitsPerPixel, iChannels,
                                         uMaskR, uMaskG, uMaskB, uMaskA,
                                         out uAllAlpha, out sReason))
                {   /* Pixel data ran out, or the masks were unusable. */
                    return(false);
                }
            }

            /* THE COMPATIBILITY FIXUP, from stb.  See the file header: an      */
            /* image whose every alpha byte is zero almost certainly meant to   */
            /* be opaque, because the fourth byte was ignored for years.        */
            if (iChannels == 4 && uAllAlpha == 0u)
            {   /* Every alpha was zero; treat the whole channel as opaque. */
                BMP_ForceOpaque(imgNew);
            }

            if (bBottomUp)
            {   /* The file stored the bottom row first, so put it back. */
                BMP_FlipVertically(imgNew);
            }

            imgOut = imgNew;

            /* Header understood, pixels read, row order corrected. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* BMP_TryParseHeader:                                                */
        /*                                                                    */
        /* Reads the 14-byte file header and the DIB header that follows it,  */
        /* resolving the channel masks for whichever dialect this is.         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     brIo           : Reader positioned at the file's first byte;   */
        /*                      left positioned after the DIB header.         */
        /*     iOffsetOut     : Receives the pixel-data offset.               */
        /*     iHeaderSizeOut : Receives the DIB header size.                 */
        /*     iWidthOut      : Receives the width.                           */
        /*     iHeightOut     : Receives the RAW height, sign included.       */
        /*     iBppOut        : Receives the bits per pixel.                  */
        /*     iCompressOut   : Receives the compression identifier.          */
        /*     uMaskROut      : Receives the red channel mask.                */
        /*     uMaskGOut      : Receives the green channel mask.              */
        /*     uMaskBOut      : Receives the blue channel mask.               */
        /*     uMaskAOut      : Receives the alpha channel mask, 0 for none.  */
        /*     sReason        : Receives "" or the first problem found.       */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool           : true when the header is a dialect we read.    */
        /*--------------------------------------------------------------------*/

        private static bool BMP_TryParseHeader(ByteReader brIo, out int iOffsetOut,
                                               out int iHeaderSizeOut, out int iWidthOut,
                                               out int iHeightOut, out int iBppOut,
                                               out int iCompressOut, out uint uMaskROut,
                                               out uint uMaskGOut, out uint uMaskBOut,
                                               out uint uMaskAOut, out string sReason)
        {
            uint uOffset;
            uint uHeaderSize;
            uint uWidth;
            uint uHeight;
            int  iPlanes;
            int  i;

            iOffsetOut     = 0;
            iHeaderSizeOut = 0;
            iWidthOut      = 0;
            iHeightOut     = 0;
            iBppOut        = 0;
            iCompressOut   = BMP_BI_RGB;
            uMaskROut      = 0u;
            uMaskGOut      = 0u;
            uMaskBOut      = 0u;
            uMaskAOut      = 0u;
            sReason        = "";

            if (brIo.BYTER_Remaining() < BMP_FILE_HEADER_SIZE)
            {   /* Not even a file header's worth of bytes. */
                sReason = "BMP: file is too short to hold a header";
                return(false);
            }

            if (!BMP_TryExpectByte(brIo, (int)'B', out sReason) ||
                !BMP_TryExpectByte(brIo, (int)'M', out sReason))
            {   /* Missing the "BM" signature; not a BMP at all. */
                sReason = "BMP: missing 'BM' signature";
                return(false);
            }

            brIo.BYTER_TrySkip(4);                       // file size, unreliable in the wild
            brIo.BYTER_TrySkip(2);                       // reserved
            brIo.BYTER_TrySkip(2);                       // reserved

            if (!brIo.BYTER_TryReadU32LE(out uOffset))
            {   /* Truncated before the pixel offset. */
                sReason = "BMP: truncated file header";
                return(false);
            }

            if (!brIo.BYTER_TryReadU32LE(out uHeaderSize))
            {   /* Truncated before the DIB header's own length field. */
                sReason = "BMP: truncated DIB header";
                return(false);
            }

            /* Read as unsigned then range-checked before narrowing, so a   */
            /* hostile 0xFFFFFFFF cannot arrive as -1 and pass a later      */
            /* "is it positive" test.                                       */
            if (uOffset > (uint)int.MaxValue || uHeaderSize > (uint)int.MaxValue)
            {   /* Absurd values; corrupt header. */
                sReason = "BMP: header offset or size is implausibly large";
                return(false);
            }

            iOffsetOut     = (int)uOffset;
            iHeaderSizeOut = (int)uHeaderSize;

            if (iHeaderSizeOut != BMP_HDR_CORE && iHeaderSizeOut != BMP_HDR_INFO &&
                iHeaderSizeOut != BMP_HDR_A56 && iHeaderSizeOut != BMP_HDR_V4 &&
                iHeaderSizeOut != BMP_HDR_V5)
            {   /* An unknown dialect.  Refusing beats guessing at field positions. */
                sReason = "BMP: DIB header size " + iHeaderSizeOut.ToString() +
                          " is not 12, 40, 56, 108 or 124";
                return(false);
            }

            if (iHeaderSizeOut == BMP_HDR_CORE)
            {   /* OS/2 core header stores the dimensions as 16-bit values. */
                if (!brIo.BYTER_TryReadU16LE(out iWidthOut) ||
                    !brIo.BYTER_TryReadU16LE(out iHeightOut))
                {   /* Truncated inside the core header. */
                    sReason = "BMP: truncated BITMAPCOREHEADER";
                    return(false);
                }
            }
            else
            {   /* Every later header stores them as signed 32-bit values,     */
                /* and the SIGN of the height is what selects the row order.   */
                if (!brIo.BYTER_TryReadU32LE(out uWidth) || !brIo.BYTER_TryReadU32LE(out uHeight))
                {   /* Truncated inside the DIB header. */
                    sReason = "BMP: truncated DIB header dimensions";
                    return(false);
                }

                iWidthOut  = unchecked((int)uWidth);
                iHeightOut = unchecked((int)uHeight);
            }

            if (!brIo.BYTER_TryReadU16LE(out iPlanes))
            {   /* Truncated before the plane count. */
                sReason = "BMP: truncated before the plane count";
                return(false);
            }

            if (iPlanes != 1)
            {   /* Multi-plane bitmaps are a DOS-era concept and no writer emits them. */
                sReason = "BMP: plane count " + iPlanes.ToString() + " is not 1";
                return(false);
            }

            if (!brIo.BYTER_TryReadU16LE(out iBppOut))
            {   /* Truncated before the bit depth. */
                sReason = "BMP: truncated before the bit depth";
                return(false);
            }

            if (iHeaderSizeOut == BMP_HDR_CORE)
            {   /* The core header ends here; it has no compression field and   */
                /* no masks, so the defaults stand.                             */
                BMP_SetMaskDefaults(iBppOut, BMP_BI_RGB, ref uMaskROut, ref uMaskGOut,
                                    ref uMaskBOut, ref uMaskAOut);
                return(true);
            }

            if (!brIo.BYTER_TryReadU32LE(out uWidth))
            {   /* Reusing uWidth as a scratch read for the compression field. */
                sReason = "BMP: truncated before the compression field";
                return(false);
            }

            iCompressOut = unchecked((int)uWidth);

            if (iCompressOut == BMP_BI_RLE8 && iBppOut != 8)
            {   /* BI_RLE8 is defined only for 8-bit images. */
                sReason = "BMP: BI_RLE8 requires 8 bits per pixel, not " + iBppOut.ToString();
                return(false);
            }

            if (iCompressOut == BMP_BI_RLE4 && iBppOut != 4)
            {   /* BI_RLE4 is defined only for 4-bit images. */
                sReason = "BMP: BI_RLE4 requires 4 bits per pixel, not " + iBppOut.ToString();
                return(false);
            }

            if (iCompressOut == BMP_BI_JPEG || iCompressOut == BMP_BI_PNG)
            {   /* A whole other file embedded in the BMP container. */
                sReason = "BMP: embedded JPEG/PNG compression is not supported";
                return(false);
            }

            if (iCompressOut != BMP_BI_RGB && iCompressOut != BMP_BI_BITFIELDS &&
                iCompressOut != BMP_BI_RLE8 && iCompressOut != BMP_BI_RLE4)
            {   /* Some value the format never defined. */
                sReason = "BMP: compression " + iCompressOut.ToString() + " is not recognised";
                return(false);
            }

            if ((iCompressOut == BMP_BI_RLE8 || iCompressOut == BMP_BI_RLE4) && iHeightOut < 0)
            {   /* Microsoft's own rule: a top-down DIB cannot be compressed.   */
                /* Refusing it is not strictness for its own sake -- the        */
                /* end-of-line and delta operations are defined in terms of     */
                /* advancing through stored rows, and a top-down RLE file has   */
                /* no agreed meaning for them.                                  */
                sReason = "BMP: a top-down DIB cannot be RLE compressed";
                return(false);
            }

            if (iCompressOut == BMP_BI_BITFIELDS && iBppOut != 16 && iBppOut != 32)
            {   /* Masks only mean anything at the two depths that have spare bits. */
                sReason = "BMP: BI_BITFIELDS requires 16 or 32 bits per pixel, not " + iBppOut.ToString();
                return(false);
            }

            brIo.BYTER_TrySkip(4);                       // image byte size, unreliable
            brIo.BYTER_TrySkip(4);                       // horizontal resolution
            brIo.BYTER_TrySkip(4);                       // vertical resolution
            brIo.BYTER_TrySkip(4);                       // colours used
            brIo.BYTER_TrySkip(4);                       // important colours

            if (iHeaderSizeOut == BMP_HDR_INFO || iHeaderSizeOut == BMP_HDR_A56)
            {   /* BITMAPINFOHEADER, and the Adobe 56-byte variant that adds   */
                /* four mask words to it.                                      */
                if (iHeaderSizeOut == BMP_HDR_A56)
                {   /* The extra four words sit here in the Adobe variant. */
                    brIo.BYTER_TrySkip(16);
                }

                if (iBppOut == 16 || iBppOut == 32)
                {   /* These depths need masks, from the header or by default. */
                    if (iCompressOut == BMP_BI_BITFIELDS)
                    {   /* Three explicit masks follow the header proper. */
                        if (!brIo.BYTER_TryReadU32LE(out uMaskROut) ||
                            !brIo.BYTER_TryReadU32LE(out uMaskGOut) ||
                            !brIo.BYTER_TryReadU32LE(out uMaskBOut))
                        {   /* Truncated inside the mask triple. */
                            sReason = "BMP: truncated BI_BITFIELDS masks";
                            return(false);
                        }

                        if (uMaskROut == uMaskGOut && uMaskGOut == uMaskBOut)
                        {   /* Three identical masks cannot separate three      */
                            /* channels.  stb refuses this outright and notes   */
                            /* it is undocumented but produced by Photoshop.    */
                            sReason = "BMP: BI_BITFIELDS masks are all identical";
                            return(false);
                        }
                    }
                    else
                    {   /* BI_RGB at these depths uses the standard layouts. */
                        BMP_SetMaskDefaults(iBppOut, iCompressOut, ref uMaskROut, ref uMaskGOut,
                                            ref uMaskBOut, ref uMaskAOut);
                    }
                }
            }
            else
            {   /* V4 or V5: four masks always present, in the header proper. */
                if (!brIo.BYTER_TryReadU32LE(out uMaskROut) ||
                    !brIo.BYTER_TryReadU32LE(out uMaskGOut) ||
                    !brIo.BYTER_TryReadU32LE(out uMaskBOut) ||
                    !brIo.BYTER_TryReadU32LE(out uMaskAOut))
                {   /* Truncated inside the V4 mask quad. */
                    sReason = "BMP: truncated V4/V5 masks";
                    return(false);
                }

                if (iCompressOut != BMP_BI_BITFIELDS)
                {   /* Per the documentation the masks are only authoritative   */
                    /* in BI_BITFIELDS mode; otherwise the defaults win.        */
                    BMP_SetMaskDefaults(iBppOut, iCompressOut, ref uMaskROut, ref uMaskGOut,
                                        ref uMaskBOut, ref uMaskAOut);
                }

                brIo.BYTER_TrySkip(4);                   // colour space type

                for (i = 0; i < 12; i++)
                {   /* Colour space endpoints and gamma; none of it affects pixels. */
                    brIo.BYTER_TrySkip(4);
                }

                if (iHeaderSizeOut == BMP_HDR_V5)
                {   /* V5 adds rendering intent and an ICC profile pointer. */
                    brIo.BYTER_TrySkip(4);               // rendering intent
                    brIo.BYTER_TrySkip(4);               // profile data offset
                    brIo.BYTER_TrySkip(4);               // profile data size
                    brIo.BYTER_TrySkip(4);               // reserved
                }
            }

            /* Header understood and masks resolved. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* BMP_SetMaskDefaults:                                               */
        /*                                                                    */
        /* Fills in the channel masks a BI_RGB file does not state.           */
        /*                                                                    */
        /* 16-bit BI_RGB is X1R5G5B5 -- five bits each with one wasted, NOT   */
        /* the R5G6B5 that most other formats use at this depth.  Assuming    */
        /* 565 produces an image with a green cast and everything one bit     */
        /* out of place, which is exactly the kind of "nearly right" bug that */
        /* survives casual inspection.                                        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iBpp        : Bits per pixel.                                  */
        /*     iCompress   : Compression identifier.                          */
        /*     uMaskRIo    : Red mask, written when a default applies.        */
        /*     uMaskGIo    : Green mask.                                      */
        /*     uMaskBIo    : Blue mask.                                       */
        /*     uMaskAIo    : Alpha mask.                                      */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void        : Masks are written through the ref parameters.    */
        /*--------------------------------------------------------------------*/

        private static void BMP_SetMaskDefaults(int iBpp, int iCompress, ref uint uMaskRIo,
                                                ref uint uMaskGIo, ref uint uMaskBIo,
                                                ref uint uMaskAIo)
        {
            if (iCompress == BMP_BI_BITFIELDS)
            {   /* The file stated its own masks; never override them. */
                return;
            }

            if (iBpp == 16)
            {   /* X1R5G5B5, the format's actual 16-bit layout. */
                uMaskRIo = 31u << 10;
                uMaskGIo = 31u << 5;
                uMaskBIo = 31u;
            }
            else if (iBpp == 32)
            {   /* B, G, R, A in memory order, so A occupies the top byte.       */
                /* The alpha mask being set here is what makes a 32-bit BI_RGB   */
                /* file decode as four channels, and is why the                  */
                /* all-alpha-zero fixup exists at all.                           */
                uMaskRIo = 0xFFu << 16;
                uMaskGIo = 0xFFu << 8;
                uMaskBIo = 0xFFu;
                uMaskAIo = 0xFFu << 24;
            }
        }

        /*--------------------------------------------------------------------*/
        /* BMP_TryReadPalette:                                                */
        /*                                                                    */
        /* Reads the colour table that sits between the DIB header and the    */
        /* pixel data, for the 1, 4 and 8 bit depths.                         */
        /*                                                                    */
        /* Entries are 4 bytes (B, G, R, unused) in every dialect except the  */
        /* 12-byte OS/2 core header, where they are 3 bytes with no padding.  */
        /*                                                                    */
        /* THE ENTRY COUNT IS DERIVED FROM THE PIXEL OFFSET rather than from  */
        /* the header's "colours used" field, which is routinely zero even    */
        /* when a palette is present.  The space between the end of the       */
        /* header and the start of the pixels is the palette, so its size     */
        /* divided by the entry size is the count.  stb computes this with a  */
        /* running "extra_read" counter; the absolute arithmetic here is      */
        /* equivalent and easier to check against the file layout.            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     brIo          : Reader; repositioned to the palette and left   */
        /*                     after it.                                      */
        /*     iHeaderSize   : DIB header size, which selects the entry size. */
        /*     iPixelOffset  : Where the pixel data begins.                   */
        /*     arrOut        : Receives 4 bytes per entry as R, G, B, A.      */
        /*     iCountOut     : Receives the entry count.                      */
        /*     sReason       : Receives "" or the problem found.              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool          : true when a usable palette was read.           */
        /*--------------------------------------------------------------------*/

        private static bool BMP_TryReadPalette(ByteReader brIo, int iHeaderSize, int iPixelOffset,
                                               out byte[] arrOut, out int iCountOut, out string sReason)
        {
            byte[] arrPalette;
            int    iEntrySize;
            int    iPaletteStart;
            int    iPaletteBytes;
            int    iCount;
            int    iRed;
            int    iGreen;
            int    iBlue;
            int    i;

            arrOut    = null;
            iCountOut = 0;
            sReason   = "";

            if (iHeaderSize == BMP_HDR_CORE)
            {   /* OS/2 entries are three bytes with no padding byte. */
                iEntrySize = 3;
            }
            else
            {   /* Every later dialect pads each entry to four bytes. */
                iEntrySize = 4;
            }

            iPaletteStart = BMP_FILE_HEADER_SIZE + iHeaderSize;
            iPaletteBytes = iPixelOffset - iPaletteStart;

            if (iPaletteBytes < 0)
            {   /* The pixel data claims to start inside the header. */
                sReason = "BMP: pixel offset " + iPixelOffset.ToString() +
                          " points inside the header";
                return(false);
            }

            iCount = iPaletteBytes / iEntrySize;

            if (iCount < 1)
            {   /* A sub-16-bit image with no palette cannot be decoded at all. */
                sReason = "BMP: " + iEntrySize.ToString() +
                          "-byte palette expected before the pixels, but none is present";
                return(false);
            }

            if (iCount > BMP_MAX_PALETTE)
            {   /* More entries than an index can name; the gap is not a palette. */
                if (iPaletteBytes > (BMP_MAX_PALETTE * iEntrySize) + BMP_MAX_EXTRA_DATA)
                {   /* And far too large to be slack, so the header is corrupt. */
                    sReason = "BMP: " + iPaletteBytes.ToString() +
                              " bytes between header and pixels is not a plausible palette";
                    return(false);
                }

                iCount = BMP_MAX_PALETTE;
            }

            if (!brIo.BYTER_TrySeek(iPaletteStart))
            {   /* The palette would start outside the file. */
                sReason = "BMP: palette offset is outside the file";
                return(false);
            }

            arrPalette = new byte[BMP_MAX_PALETTE * 4];

            for (i = 0; i < iCount; i++)
            {   /* Stored B, G, R -- reversed into R, G, B on the way in, so   */
                /* the rest of this codec never thinks about BMP's order.      */
                if (!brIo.BYTER_TryReadU8(out iBlue) ||
                    !brIo.BYTER_TryReadU8(out iGreen) ||
                    !brIo.BYTER_TryReadU8(out iRed))
                {   /* Ran out of file inside the palette. */
                    sReason = "BMP: truncated palette at entry " + i.ToString();
                    return(false);
                }

                if (iEntrySize == 4)
                {   /* Discard the padding byte, which is not an alpha value. */
                    brIo.BYTER_TrySkip(1);
                }

                arrPalette[(i * 4)]     = (byte)iRed;
                arrPalette[(i * 4) + 1] = (byte)iGreen;
                arrPalette[(i * 4) + 2] = (byte)iBlue;
                arrPalette[(i * 4) + 3] = 255;
            }

            arrOut    = arrPalette;
            iCountOut = iCount;

            /* A palette of iCount entries, in R, G, B, A order. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* BMP_TryDecodePalettised:                                           */
        /*                                                                    */
        /* Decodes the 1, 4 and 8 bit depths, expanding each index through    */
        /* the colour table.                                                  */
        /*                                                                    */
        /* Rows are written top-to-bottom into the buffer regardless of the   */
        /* file's row order; the caller flips afterwards if the file was      */
        /* bottom-up.  Doing it in one pass at the end is simpler than        */
        /* writing rows backwards here and getting the padding wrong.         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     brIo          : Reader positioned at the pixel data.           */
        /*     imgIo         : Destination buffer, written in place.          */
        /*     arrPalette    : Colour table, R, G, B, A per entry.            */
        /*     iPaletteSize  : Number of valid entries.                       */
        /*     iBpp          : 1, 4 or 8.                                     */
        /*     iChannels     : 3 or 4, matching the destination.              */
        /*     sReason       : Receives "" or the problem found.              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool          : true when every row was decoded.               */
        /*--------------------------------------------------------------------*/

        private static bool BMP_TryDecodePalettised(ByteReader brIo, ImageBuffer imgIo,
                                                    byte[] arrPalette, int iPaletteSize,
                                                    int iBpp, int iChannels, out string sReason)
        {
            int iRowBytes;
            int iPad;
            int iDest;
            int iIndex;
            int iByte;
            int iBitOffset;
            int iX;
            int iY;

            sReason = "";

            if (iBpp == 1)
            {   /* One bit per pixel, eight pixels per byte. */
                iRowBytes = (imgIo.IMG_iWidth + 7) >> 3;
            }
            else if (iBpp == 4)
            {   /* Two pixels per byte, high nibble first. */
                iRowBytes = (imgIo.IMG_iWidth + 1) >> 1;
            }
            else if (iBpp == 8)
            {   /* One byte per pixel. */
                iRowBytes = imgIo.IMG_iWidth;
            }
            else
            {   /* 2 bpp exists in some OS/2 files and is not handled here, matching stb. */
                sReason = "BMP: " + iBpp.ToString() + " bits per pixel is not supported";
                return(false);
            }

            /* Every row is padded up to a four-byte boundary.  The negation   */
            /* trick is stb's and reads oddly: (-n) & 3 is the number of       */
            /* bytes needed to round n up to a multiple of four.               */
            iPad  = (-iRowBytes) & 3;
            iDest = 0;

            for (iY = 0; iY < imgIo.IMG_iHeight; iY++)
            {   /* One stored row per iteration. */
                iByte      = 0;
                iBitOffset = 0;

                for (iX = 0; iX < imgIo.IMG_iWidth; iX++)
                {   /* Pull the next index out of the packed row. */
                    if (iBpp == 8)
                    {   /* A whole byte per pixel; no unpacking needed. */
                        if (!brIo.BYTER_TryReadU8(out iIndex))
                        {   /* Pixel data ran out mid-row. */
                            sReason = "BMP: pixel data ran out at row " + iY.ToString();
                            return(false);
                        }
                    }
                    else if (iBpp == 4)
                    {   /* Two pixels per byte, most significant nibble first. */
                        if ((iX & 1) == 0)
                        {   /* Even column: fetch a fresh byte and take its high nibble. */
                            if (!brIo.BYTER_TryReadU8(out iByte))
                            {   /* Pixel data ran out mid-row. */
                                sReason = "BMP: pixel data ran out at row " + iY.ToString();
                                return(false);
                            }

                            iIndex = (iByte >> 4) & 0x0F;
                        }
                        else
                        {   /* Odd column: the low nibble of the byte already read. */
                            iIndex = iByte & 0x0F;
                        }
                    }
                    else
                    {   /* One bit per pixel, most significant bit first. */
                        if ((iX & 7) == 0)
                        {   /* Start of a new byte's worth of pixels. */
                            if (!brIo.BYTER_TryReadU8(out iByte))
                            {   /* Pixel data ran out mid-row. */
                                sReason = "BMP: pixel data ran out at row " + iY.ToString();
                                return(false);
                            }

                            iBitOffset = 7;
                        }

                        iIndex     = (iByte >> iBitOffset) & 0x01;
                        iBitOffset = iBitOffset - 1;
                    }

                    if (iIndex >= iPaletteSize)
                    {   /* An index past the end of the table.  Refusing beats   */
                        /* reading whatever happens to be in the array.          */
                        sReason = "BMP: palette index " + iIndex.ToString() +
                                  " is beyond the " + iPaletteSize.ToString() + " entry table";
                        return(false);
                    }

                    imgIo.IMG_arrSamples8[iDest]     = arrPalette[(iIndex * 4)];
                    imgIo.IMG_arrSamples8[iDest + 1] = arrPalette[(iIndex * 4) + 1];
                    imgIo.IMG_arrSamples8[iDest + 2] = arrPalette[(iIndex * 4) + 2];
                    iDest = iDest + 3;

                    if (iChannels == 4)
                    {   /* Palettised images have no alpha, so every pixel is opaque. */
                        imgIo.IMG_arrSamples8[iDest] = 255;
                        iDest = iDest + 1;
                    }
                }

                brIo.BYTER_TrySkip(iPad);
            }

            /* Every row expanded through the palette. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* BMP_TryDecodeRle:                                                  */
        /*                                                                    */
        /* Decodes BI_RLE8 and BI_RLE4 run-length compressed palettised data. */
        /*                                                                    */
        /* WRITTEN FROM MICROSOFT'S SPECIFICATION, NOT TRANSCODED, because no */
        /* archived source implements it -- stb declares its own scope as     */
        /* "BMP non-1bpp, non-RLE" and refuses these two compressions         */
        /* outright.  Where a capability is needed and no usable source       */
        /* exists, this library writes its own from the specification rather  */
        /* than narrowing what it supports -- the bar for not doing so is     */
        /* that the effort would be extreme, and this was not.                */
        /*                                                                    */
        /* THE STREAM IS OPERATIONS, NOT ROWS.  Everywhere else in BMP the    */
        /* pixel data is rows of packed samples with a known length; here it  */
        /* is a sequence of byte pairs, each an instruction:                  */
        /*                                                                    */
        /*   n>0, v      ENCODED MODE.  Emit n pixels.  For RLE8 all of them  */
        /*               are index v; for RLE4 they alternate between v's     */
        /*               high and low nibble, starting high.                  */
        /*   0, 0        END OF LINE.  Move to the start of the next row.     */
        /*   0, 1        END OF BITMAP.  Stop.                                */
        /*   0, 2, dx,dy DELTA.  Move right dx and down dy, LEAVING THE       */
        /*               SKIPPED PIXELS ALONE.                                */
        /*   0, n>=3     ABSOLUTE MODE.  n literal pixels follow, packed as   */
        /*               for the depth, then padded so the run occupies an    */
        /*               even number of bytes.                                */
        /*                                                                    */
        /* THE DELTA IS THE OPERATION THAT MAKES THIS MORE THAN A RUN CODEC,  */
        /* and it is the one worth being careful about. It skips forward      */
        /* without writing anything, so the pixels it passes over keep        */
        /* whatever the image already held -- and the specification does not  */
        /* say what that should be.                                           */
        /*                                                                    */
        /* SKIPPED PIXELS ARE LEFT BLACK, AND THAT WAS MEASURED RATHER THAN   */
        /* ASSUMED. The first draft pre-filled the image with palette entry 0 */
        /* on the reasoning that a skipped pixel "is" index 0. GDI+ disagrees */
        /* -- it leaves them opaque black regardless of what entry 0 holds,   */
        /* which was confirmed by decoding a file whose entry 0 is orange and */
        /* watching GDI+ return (0,0,0) for the skipped pixels while getting  */
        /* every written pixel right.                                         */
        /*                                                                    */
        /* Since the format is silent, the platform's behaviour is the right  */
        /* tie-breaker, and it happens to be the simpler implementation too:  */
        /* the buffer arrives zero-filled, so this decoder does nothing at    */
        /* all and the pixels stay black. The test palette deliberately keeps */
        /* a non-black entry 0 so the two interpretations remain              */
        /* distinguishable if anyone revisits this.                           */
        /*                                                                    */
        /* "Down" here means down in STORED row order. The rows go into the   */
        /* buffer in stored order and the caller flips afterwards, exactly as */
        /* the uncompressed palettised path does, so nothing in this function */
        /* has to know the file is bottom-up.                                 */
        /*                                                                    */
        /* TRUNCATION POLICY: running out of data in the MIDDLE of an         */
        /* operation is a malformed file and is refused. Running out exactly  */
        /* at an operation boundary is accepted as an implicit end of bitmap, */
        /* because a missing final (0,1) is common in real files and the      */
        /* image is complete without it. Refusing that would reject files     */
        /* every other reader accepts.                                        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     brIo          : Reader positioned at the pixel data.           */
        /*     imgIo         : Destination buffer, written in place.          */
        /*     arrPalette    : Colour table, R, G, B, A per entry.            */
        /*     iPaletteSize  : Number of valid entries.                       */
        /*     iBpp          : 8 for BI_RLE8, 4 for BI_RLE4.                  */
        /*     iChannels     : 3 or 4, matching the destination.              */
        /*     sReason       : Receives "" or the problem found.              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool          : true when the stream decoded cleanly.          */
        /*--------------------------------------------------------------------*/

        private static bool BMP_TryDecodeRle(ByteReader brIo, ImageBuffer imgIo, byte[] arrPalette,
                                             int iPaletteSize, int iBpp, int iChannels,
                                             out string sReason)
        {
            bool bDone;
            int  iCount;
            int  iValue;
            int  iDeltaX;
            int  iDeltaY;
            int  iRunBytes;
            int  iPackedByte;
            int  iIndex;
            int  iX;
            int  iY;
            int  i;

            sReason     = "";
            iPackedByte = 0;

            /* No pre-fill. Pixels the stream never covers -- because of a    */
            /* delta, a short row, or an early end of bitmap -- stay at the   */
            /* buffer's zero initialisation, which is black. That matches     */
            /* GDI+, measured rather than assumed; see the file header.       */

            iX    = 0;
            iY    = 0;
            bDone = false;

            while (!bDone)
            {   /* One operation per iteration. */
                if (brIo.BYTER_Remaining() < 2)
                {   /* Out of data at an operation boundary: treat a missing   */
                    /* end-of-bitmap marker as an implicit one.                */
                    bDone = true;
                    continue;
                }

                brIo.BYTER_TryReadU8(out iCount);
                brIo.BYTER_TryReadU8(out iValue);

                if (iCount > 0)
                {   /* ENCODED MODE: iCount pixels from the single value byte. */
                    for (i = 0; i < iCount; i++)
                    {   /* RLE4 alternates nibbles; RLE8 repeats the whole byte. */
                        if (iBpp == 4)
                        {   /* Even positions in the run take the high nibble. */
                            if ((i & 1) == 0)
                            {   /* High nibble first, per the specification. */
                                iIndex = (iValue >> 4) & 0x0F;
                            }
                            else
                            {   /* Then the low nibble. */
                                iIndex = iValue & 0x0F;
                            }
                        }
                        else
                        {   /* RLE8: every pixel of the run is the same index. */
                            iIndex = iValue;
                        }

                        if (!BMP_TryPutRlePixel(imgIo, arrPalette, iPaletteSize, iChannels,
                                                iX, iY, iIndex, out sReason))
                        {   /* Out of bounds or a bad index; the writer says which. */
                            return(false);
                        }

                        iX = iX + 1;
                    }
                }
                else if (iValue == 0)
                {   /* END OF LINE: back to column zero on the next stored row. */
                    iX = 0;
                    iY = iY + 1;
                }
                else if (iValue == 1)
                {   /* END OF BITMAP: the stream is finished. */
                    bDone = true;
                }
                else if (iValue == 2)
                {   /* DELTA: jump forward, leaving the skipped pixels as they are. */
                    if (brIo.BYTER_Remaining() < 2)
                    {   /* The two offset bytes are missing: truncated mid-operation. */
                        sReason = "BMP: RLE delta is missing its offset bytes";
                        return(false);
                    }

                    brIo.BYTER_TryReadU8(out iDeltaX);
                    brIo.BYTER_TryReadU8(out iDeltaY);

                    iX = iX + iDeltaX;
                    iY = iY + iDeltaY;
                }
                else
                {   /* ABSOLUTE MODE: iValue literal pixels follow. */
                    if (iBpp == 4)
                    {   /* Two pixels per byte, rounded up. */
                        iRunBytes = (iValue + 1) >> 1;
                    }
                    else
                    {   /* One byte per pixel. */
                        iRunBytes = iValue;
                    }

                    if (brIo.BYTER_Remaining() < iRunBytes)
                    {   /* The literal run is cut short: truncated mid-operation. */
                        sReason = "BMP: RLE absolute run of " + iValue.ToString() +
                                  " pixels is truncated";
                        return(false);
                    }

                    for (i = 0; i < iValue; i++)
                    {   /* Pull each literal index out of the packed run. */
                        if (iBpp == 4)
                        {   /* A fresh byte on even pixels, its low nibble on odd. */
                            if ((i & 1) == 0)
                            {   /* Read the byte and take the high nibble. */
                                brIo.BYTER_TryReadU8(out iPackedByte);
                                iIndex = (iPackedByte >> 4) & 0x0F;
                            }
                            else
                            {   /* Low nibble of the byte already read. */
                                iIndex = iPackedByte & 0x0F;
                            }
                        }
                        else
                        {   /* One index per byte. */
                            brIo.BYTER_TryReadU8(out iIndex);
                        }

                        if (!BMP_TryPutRlePixel(imgIo, arrPalette, iPaletteSize, iChannels,
                                                iX, iY, iIndex, out sReason))
                        {   /* Out of bounds or a bad index. */
                            return(false);
                        }

                        iX = iX + 1;
                    }

                    if ((iRunBytes & 1) != 0)
                    {   /* An absolute run is padded so it occupies an even     */
                        /* number of bytes.  Skipping this is the classic way   */
                        /* to desynchronise from the stream one operation       */
                        /* later, which then decodes as plausible nonsense.     */
                        brIo.BYTER_TrySkip(1);
                    }
                }
            }

            /* The stream ended, whether by an explicit end-of-bitmap marker   */
            /* or by running out at an operation boundary.                     */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* BMP_TryPutRlePixel:                                                */
        /*                                                                    */
        /* Writes one palette-indexed pixel at a stored-row coordinate,       */
        /* checking both the coordinate and the index.                        */
        /*                                                                    */
        /* A COORDINATE PAST THE RIGHT EDGE IS TOLERATED, NOT REFUSED, and    */
        /* that is a deliberate softening. Encoders in the wild routinely     */
        /* emit a run that overshoots the last column by a pixel or two --    */
        /* the row is padded conceptually and the decoder is expected to      */
        /* discard the surplus. Refusing would reject files every other       */
        /* reader accepts. A coordinate past the BOTTOM is different: it      */
        /* means the row count itself is wrong, so it is refused.             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     imgIo         : Destination buffer.                            */
        /*     arrPalette    : Colour table, R, G, B, A per entry.            */
        /*     iPaletteSize  : Number of valid entries.                       */
        /*     iChannels     : 3 or 4.                                        */
        /*     iX            : Column, may exceed the width.                  */
        /*     iY            : Stored row.                                    */
        /*     iIndex        : Palette index to write.                        */
        /*     sReason       : Receives "" or the problem found.              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool          : true when the pixel was written or harmlessly  */
        /*                     discarded; false when the file is malformed.   */
        /*--------------------------------------------------------------------*/

        private static bool BMP_TryPutRlePixel(ImageBuffer imgIo, byte[] arrPalette,
                                               int iPaletteSize, int iChannels,
                                               int iX, int iY, int iIndex, out string sReason)
        {
            int iDest;

            sReason = "";

            if (iIndex >= iPaletteSize || iIndex < 0)
            {   /* An index outside the table cannot be resolved to a colour. */
                sReason = "BMP: RLE palette index " + iIndex.ToString() +
                          " is beyond the " + iPaletteSize.ToString() + " entry table";
                return(false);
            }

            if (iY < 0 || iY >= imgIo.IMG_iHeight)
            {   /* Past the bottom means the stream disagrees with the header. */
                sReason = "BMP: RLE data runs past row " + (imgIo.IMG_iHeight - 1).ToString();
                return(false);
            }

            if (iX < 0 || iX >= imgIo.IMG_iWidth)
            {   /* Past the right edge: discard silently, per the header note. */
                return(true);
            }

            iDest = ((iY * imgIo.IMG_iWidth) + iX) * iChannels;

            imgIo.IMG_arrSamples8[iDest]     = arrPalette[(iIndex * 4)];
            imgIo.IMG_arrSamples8[iDest + 1] = arrPalette[(iIndex * 4) + 1];
            imgIo.IMG_arrSamples8[iDest + 2] = arrPalette[(iIndex * 4) + 2];

            if (iChannels == 4)
            {   /* Palettised images carry no alpha, so opaque. */
                imgIo.IMG_arrSamples8[iDest + 3] = 255;
            }

            /* Written, or discarded as an overshoot past the right edge. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* BMP_TryDecodeDirect:                                               */
        /*                                                                    */
        /* Decodes the 16, 24 and 32 bit depths, where the colour is in the   */
        /* pixel rather than in a table.                                      */
        /*                                                                    */
        /* Two fast paths are recognised, exactly as stb does: 24-bit is      */
        /* always plain B, G, R, and 32-bit with the standard masks is plain  */
        /* B, G, R, A.  Everything else goes through the general              */
        /* mask-and-shift path, which is correct for any layout but does real */
        /* work per channel per pixel.                                        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     brIo          : Reader positioned at the pixel data.           */
        /*     imgIo         : Destination buffer, written in place.          */
        /*     iBpp          : 16, 24 or 32.                                  */
        /*     iChannels     : 3 or 4, matching the destination.              */
        /*     uMaskR        : Red channel mask.                              */
        /*     uMaskG        : Green channel mask.                            */
        /*     uMaskB        : Blue channel mask.                             */
        /*     uMaskA        : Alpha channel mask, 0 for none.                */
        /*     uAllAlphaOut  : Receives the OR of every alpha value written,  */
        /*                     which the caller uses to detect the            */
        /*                     all-zero-alpha case.                           */
        /*     sReason       : Receives "" or the problem found.              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool          : true when every row was decoded.               */
        /*--------------------------------------------------------------------*/

        private static bool BMP_TryDecodeDirect(ByteReader brIo, ImageBuffer imgIo, int iBpp,
                                                int iChannels, uint uMaskR, uint uMaskG,
                                                uint uMaskB, uint uMaskA, out uint uAllAlphaOut,
                                                out string sReason)
        {
            uint uPixel;
            uint uAlpha;
            int  iRowBytes;
            int  iPad;
            int  iDest;
            int  iEasy;
            int  iShiftR;
            int  iShiftG;
            int  iShiftB;
            int  iShiftA;
            int  iCountR;
            int  iCountG;
            int  iCountB;
            int  iCountA;
            int  iRed;
            int  iGreen;
            int  iBlue;
            int  iValue;
            int  iX;
            int  iY;

            uAllAlphaOut = 0u;
            sReason      = "";

            if (iBpp == 24)
            {   /* Three bytes per pixel; rows need padding. */
                iRowBytes = 3 * imgIo.IMG_iWidth;
            }
            else if (iBpp == 16)
            {   /* Two bytes per pixel; rows need padding. */
                iRowBytes = 2 * imgIo.IMG_iWidth;
            }
            else if (iBpp == 32)
            {   /* Four bytes per pixel, so rows are always aligned already. */
                iRowBytes = 0;
            }
            else
            {   /* Any other depth at or above 16 is not a BMP layout. */
                sReason = "BMP: " + iBpp.ToString() + " bits per pixel is not supported";
                return(false);
            }

            iPad = (-iRowBytes) & 3;

            /* The two shortcuts stb recognises, kept by the same names. */
            iEasy = 0;

            if (iBpp == 24)
            {   /* Always plain B, G, R with no masks involved. */
                iEasy = 1;
            }
            else if (iBpp == 32)
            {   /* Only when the masks are the conventional ones. */
                if (uMaskB == 0xFFu && uMaskG == 0xFF00u &&
                    uMaskR == 0x00FF0000u && uMaskA == 0xFF000000u)
                {   /* Standard 32-bit layout: B, G, R, A straight through. */
                    iEasy = 2;
                }
            }

            iShiftR = 0;
            iShiftG = 0;
            iShiftB = 0;
            iShiftA = 0;
            iCountR = 0;
            iCountG = 0;
            iCountB = 0;
            iCountA = 0;

            if (iEasy == 0)
            {   /* General path: derive a shift and a bit count per channel   */
                /* from its mask, once, before the pixel loop.                */
                if (uMaskR == 0u || uMaskG == 0u || uMaskB == 0u)
                {   /* A zero mask means that channel cannot be extracted at all. */
                    sReason = "BMP: channel masks are missing or zero";
                    return(false);
                }

                iShiftR = BMP_HighBit(uMaskR) - 7;
                iCountR = BMP_BitCount(uMaskR);
                iShiftG = BMP_HighBit(uMaskG) - 7;
                iCountG = BMP_BitCount(uMaskG);
                iShiftB = BMP_HighBit(uMaskB) - 7;
                iCountB = BMP_BitCount(uMaskB);
                iShiftA = BMP_HighBit(uMaskA) - 7;
                iCountA = BMP_BitCount(uMaskA);

                if (iCountR > 8 || iCountG > 8 || iCountB > 8 || iCountA > 8)
                {   /* More than eight bits in a channel cannot be narrowed      */
                    /* by the replication table below, which only covers 0..8.   */
                    sReason = "BMP: a channel mask covers more than 8 bits";
                    return(false);
                }
            }

            iDest = 0;

            for (iY = 0; iY < imgIo.IMG_iHeight; iY++)
            {   /* One stored row per iteration. */
                for (iX = 0; iX < imgIo.IMG_iWidth; iX++)
                {   /* One pixel per iteration. */
                    if (iEasy != 0)
                    {   /* Fast path: the bytes are already channel values,   */
                        /* just in B, G, R order.                             */
                        if (!brIo.BYTER_TryReadU8(out iBlue) ||
                            !brIo.BYTER_TryReadU8(out iGreen) ||
                            !brIo.BYTER_TryReadU8(out iRed))
                        {   /* Pixel data ran out mid-row. */
                            sReason = "BMP: pixel data ran out at row " + iY.ToString();
                            return(false);
                        }

                        imgIo.IMG_arrSamples8[iDest]     = (byte)iRed;
                        imgIo.IMG_arrSamples8[iDest + 1] = (byte)iGreen;
                        imgIo.IMG_arrSamples8[iDest + 2] = (byte)iBlue;
                        iDest = iDest + 3;

                        if (iEasy == 2)
                        {   /* 32-bit standard layout carries a real alpha byte. */
                            if (!brIo.BYTER_TryReadU8(out iValue))
                            {   /* Pixel data ran out before the alpha byte. */
                                sReason = "BMP: pixel data ran out at row " + iY.ToString();
                                return(false);
                            }

                            uAlpha = (uint)iValue;
                        }
                        else
                        {   /* 24-bit has no alpha; treat the pixel as opaque. */
                            uAlpha = 255u;
                        }

                        uAllAlphaOut = uAllAlphaOut | uAlpha;

                        if (iChannels == 4)
                        {   /* Destination has an alpha channel to fill. */
                            imgIo.IMG_arrSamples8[iDest] = (byte)uAlpha;
                            iDest = iDest + 1;
                        }
                    }
                    else
                    {   /* General path: read the whole pixel as one value   */
                        /* and pull each channel out with its own mask.      */
                        if (iBpp == 16)
                        {   /* Two-byte pixel. */
                            if (!brIo.BYTER_TryReadU16LE(out iValue))
                            {   /* Pixel data ran out mid-row. */
                                sReason = "BMP: pixel data ran out at row " + iY.ToString();
                                return(false);
                            }

                            uPixel = (uint)iValue;
                        }
                        else
                        {   /* Four-byte pixel. */
                            if (!brIo.BYTER_TryReadU32LE(out uPixel))
                            {   /* Pixel data ran out mid-row. */
                                sReason = "BMP: pixel data ran out at row " + iY.ToString();
                                return(false);
                            }
                        }

                        imgIo.IMG_arrSamples8[iDest] =
                            (byte)BMP_ShiftSigned(uPixel & uMaskR, iShiftR, iCountR);
                        imgIo.IMG_arrSamples8[iDest + 1] =
                            (byte)BMP_ShiftSigned(uPixel & uMaskG, iShiftG, iCountG);
                        imgIo.IMG_arrSamples8[iDest + 2] =
                            (byte)BMP_ShiftSigned(uPixel & uMaskB, iShiftB, iCountB);
                        iDest = iDest + 3;

                        if (uMaskA != 0u)
                        {   /* An alpha mask was declared, so extract it too. */
                            uAlpha = (uint)BMP_ShiftSigned(uPixel & uMaskA, iShiftA, iCountA);
                        }
                        else
                        {   /* No alpha mask; the pixel is opaque. */
                            uAlpha = 255u;
                        }

                        uAllAlphaOut = uAllAlphaOut | uAlpha;

                        if (iChannels == 4)
                        {   /* Destination has an alpha channel to fill. */
                            imgIo.IMG_arrSamples8[iDest] = (byte)uAlpha;
                            iDest = iDest + 1;
                        }
                    }
                }

                brIo.BYTER_TrySkip(iPad);
            }

            /* Every row decoded, and uAllAlphaOut carries the OR of every   */
            /* alpha seen so the caller can spot the all-zero case.          */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* BMP_TryEncode:                                                     */
        /*                                                                    */
        /* Writes a BMP: 24-bit BI_RGB with a 40-byte header, or 32-bit       */
        /* BI_BITFIELDS with a 108-byte V4 header.                            */
        /*                                                                    */
        /* WHY 32-BIT NEEDS THE V4 HEADER, which is stb's finding and worth   */
        /* keeping: a plain 40-byte BI_RGB header at 32 bits per pixel has no */
        /* way to say that the fourth byte is alpha, and most readers treat   */
        /* it as padding and show the image opaque.  BI_BITFIELDS with an     */
        /* explicit alpha mask is what actually gets honoured, and the mask   */
        /* quad only exists from the V4 header onward.                        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     imgIn    : Source image, 8-bit, 1 to 4 channels.               */
        /*     optIn    : Encoder settings; null means the defaults.          */
        /*     arrOut   : Receives the complete BMP file; null on failure.    */
        /*     sReason  : Receives "" on success, or why it was refused.      */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool     : true when arrOut holds a complete BMP file.         */
        /*--------------------------------------------------------------------*/

        public static bool BMP_TryEncode(ImageBuffer imgIn, BmpEncodeOptions optIn,
                                         out byte[] arrOut, out string sReason)
        {
            BmpEncodeOptions optUse;
            ImageBuffer      imgUse;
            ByteWriter       bwOut;
            int              iWantChannels;
            int              iRowBytes;
            int              iPad;
            int              iHeaderSize;
            int              iFileSize;
            int              iSrc;
            int              iStride;
            int              iHeightField;
            int              iX;
            int              iY;
            int              i;

            arrOut  = null;
            sReason = "";

            if (!ImageBuffer.IMG_TryValidate(imgIn, out sReason))
            {   /* Source is malformed; the validator's reason is the right one. */
                return(false);
            }

            if (imgIn.IMG_iBitsPerSample != ImageBuffer.IMG_BITS_8)
            {   /* BMP has no 16-bit-per-channel form, and narrowing is lossy,   */
                /* so the caller must decide -- same rule as the QOI encoder.    */
                sReason = "BMP: the format stores only 8-bit samples; convert the 16-bit source first";
                return(false);
            }

            if (optIn == null)
            {   /* No options supplied, which is legal and means the defaults. */
                optUse = BMP_DefaultEncodeOptions();
            }
            else
            {   /* Caller supplied settings. */
                optUse = optIn;
            }

            if (optUse.BMPOPT_iBitsPerPixel != 4 && optUse.BMPOPT_iBitsPerPixel != 8 &&
                optUse.BMPOPT_iBitsPerPixel != 24 && optUse.BMPOPT_iBitsPerPixel != 32)
            {   /* Only the four depths this encoder writes. */
                sReason = "BMP: encode bit depth " + optUse.BMPOPT_iBitsPerPixel.ToString() +
                          " is not 4, 8, 24 or 32";
                return(false);
            }

            if (optUse.BMPOPT_iBitsPerPixel == 4 || optUse.BMPOPT_iBitsPerPixel == 8)
            {   /* Palettised output is a different enough shape -- colour      */
                /* table, packed indices, optional run-length compression --    */
                /* that it gets its own function rather than more branches in   */
                /* this one.                                                    */
                return(BMP_TryEncodePalettised(imgIn, optUse, out arrOut, out sReason));
            }

            if (optUse.BMPOPT_bRleCompress)
            {   /* RLE exists only for the palettised depths; the format has   */
                /* no compression at 24 or 32 bits. Refusing beats silently    */
                /* ignoring the request, which would hand back a file the      */
                /* caller believes is compressed.                              */
                sReason = "BMP: RLE compression requires a bit depth of 4 or 8, not " +
                          optUse.BMPOPT_iBitsPerPixel.ToString();
                return(false);
            }

            if (optUse.BMPOPT_iBitsPerPixel == 32)
            {   /* 32-bit output needs a four-channel source. */
                iWantChannels = ImageBuffer.IMG_CHANNELS_RGBA;
            }
            else
            {   /* 24-bit output needs three. */
                iWantChannels = ImageBuffer.IMG_CHANNELS_RGB;
            }

            if (imgIn.IMG_iChannels == ImageBuffer.IMG_CHANNELS_RGBA &&
                optUse.BMPOPT_iBitsPerPixel == 24)
            {   /* REFUSE rather than silently drop the alpha channel.  This     */
                /* is the same line the QOI encoder draws: a lossless widening   */
                /* is done for the caller, a LOSSY narrowing is theirs to ask    */
                /* for.  Converting to RGB first, or asking for 32 bits, both    */
                /* make the intent explicit.                                     */
                sReason = "BMP: a 24-bit file cannot carry the source's alpha channel; " +
                          "set BMPOPT_iBitsPerPixel to 32, or convert to RGB first";
                return(false);
            }

            if (imgIn.IMG_iChannels != iWantChannels)
            {   /* Widening only: gray to RGB, gray+alpha to RGBA, RGB to   */
                /* RGBA.  All lossless, so they are done without asking.    */
                if (!ImageBuffer.IMG_TryConvertChannels(imgIn, iWantChannels, out imgUse, out sReason))
                {   /* The conversion failed; pass its reason up. */
                    return(false);
                }
            }
            else
            {   /* Already the right shape. */
                imgUse = imgIn;
            }

            if (optUse.BMPOPT_iBitsPerPixel == 24)
            {   /* Three bytes per pixel, rows padded to four. */
                iRowBytes   = imgUse.IMG_iWidth * 3;
                iHeaderSize = BMP_HDR_INFO;
            }
            else
            {   /* Four bytes per pixel; rows are inherently aligned. */
                iRowBytes   = imgUse.IMG_iWidth * 4;
                iHeaderSize = BMP_HDR_V4;
            }

            iPad      = (-iRowBytes) & 3;
            iFileSize = BMP_FILE_HEADER_SIZE + iHeaderSize +
                        ((iRowBytes + iPad) * imgUse.IMG_iHeight);

            bwOut = new ByteWriter(iFileSize);

            /* File header, identical for both shapes apart from the sizes. */
            bwOut.BYTEW_WriteU8((int)'B');
            bwOut.BYTEW_WriteU8((int)'M');
            bwOut.BYTEW_WriteU32LE((uint)iFileSize);
            bwOut.BYTEW_WriteU16LE(0);                   // reserved
            bwOut.BYTEW_WriteU16LE(0);                   // reserved
            bwOut.BYTEW_WriteU32LE((uint)(BMP_FILE_HEADER_SIZE + iHeaderSize));

            /* A NEGATIVE height is what declares a top-down file.  Written    */
            /* unchecked so the two's complement bit pattern goes out as the   */
            /* format expects.                                                 */
            if (optUse.BMPOPT_bTopDown)
            {   /* First row in the file is the top row. */
                iHeightField = -imgUse.IMG_iHeight;
            }
            else
            {   /* Conventional bottom-up. */
                iHeightField = imgUse.IMG_iHeight;
            }

            bwOut.BYTEW_WriteU32LE((uint)iHeaderSize);
            bwOut.BYTEW_WriteU32LE((uint)imgUse.IMG_iWidth);
            bwOut.BYTEW_WriteU32LE(unchecked((uint)iHeightField));
            bwOut.BYTEW_WriteU16LE(1);                   // planes, always 1
            bwOut.BYTEW_WriteU16LE(optUse.BMPOPT_iBitsPerPixel);

            if (optUse.BMPOPT_iBitsPerPixel == 24)
            {   /* BI_RGB, and the rest of BITMAPINFOHEADER is zeroes. */
                bwOut.BYTEW_WriteU32LE((uint)BMP_BI_RGB);
                bwOut.BYTEW_WriteU32LE(0);               // image byte size, 0 is legal for BI_RGB
                bwOut.BYTEW_WriteU32LE(0);               // horizontal resolution
                bwOut.BYTEW_WriteU32LE(0);               // vertical resolution
                bwOut.BYTEW_WriteU32LE(0);               // colours used
                bwOut.BYTEW_WriteU32LE(0);               // important colours
            }
            else
            {   /* BI_BITFIELDS with the standard 32-bit masks, then the V4   */
                /* colour space fields, all zero.                             */
                bwOut.BYTEW_WriteU32LE((uint)BMP_BI_BITFIELDS);
                bwOut.BYTEW_WriteU32LE(0);               // image byte size
                bwOut.BYTEW_WriteU32LE(0);               // horizontal resolution
                bwOut.BYTEW_WriteU32LE(0);               // vertical resolution
                bwOut.BYTEW_WriteU32LE(0);               // colours used
                bwOut.BYTEW_WriteU32LE(0);               // important colours
                bwOut.BYTEW_WriteU32LE(0x00FF0000u);     // red mask
                bwOut.BYTEW_WriteU32LE(0x0000FF00u);     // green mask
                bwOut.BYTEW_WriteU32LE(0x000000FFu);     // blue mask
                bwOut.BYTEW_WriteU32LE(0xFF000000u);     // alpha mask

                for (i = 0; i < BMP_V4_TAIL_WORDS; i++)
                {   /* Colour space type, the nine endpoint words and the      */
                    /* three gamma words.  The count is a named constant       */
                    /* because getting it wrong writes a header whose real     */
                    /* length disagrees with the length it declares, and the   */
                    /* symptom is decoded garbage rather than a rejected       */
                    /* file -- see BMP_V4_TAIL_WORDS.                          */
                    bwOut.BYTEW_WriteU32LE(0);
                }
            }

            iStride = imgUse.IMG_iWidth * imgUse.IMG_iChannels;

            for (iY = 0; iY < imgUse.IMG_iHeight; iY++)
            {   /* Rows go out in file order, which is bottom-up unless the   */
                /* caller asked otherwise.                                    */
                if (optUse.BMPOPT_bTopDown)
                {   /* Top-down: write rows in their natural order. */
                    iSrc = iY * iStride;
                }
                else
                {   /* Bottom-up: the last row of the image goes out first. */
                    iSrc = (imgUse.IMG_iHeight - 1 - iY) * iStride;
                }

                for (iX = 0; iX < imgUse.IMG_iWidth; iX++)
                {   /* B, G, R order on the wire, reversed from ours. */
                    bwOut.BYTEW_WriteU8((int)imgUse.IMG_arrSamples8[iSrc + 2]);
                    bwOut.BYTEW_WriteU8((int)imgUse.IMG_arrSamples8[iSrc + 1]);
                    bwOut.BYTEW_WriteU8((int)imgUse.IMG_arrSamples8[iSrc]);

                    if (optUse.BMPOPT_iBitsPerPixel == 32)
                    {   /* The fourth byte is a real alpha value here. */
                        bwOut.BYTEW_WriteU8((int)imgUse.IMG_arrSamples8[iSrc + 3]);
                    }

                    iSrc = iSrc + imgUse.IMG_iChannels;
                }

                for (i = 0; i < iPad; i++)
                {   /* Pad the row up to a four-byte boundary. */
                    bwOut.BYTEW_WriteU8(0);
                }
            }

            arrOut = bwOut.BYTEW_ToArray();

            /* A complete BMP file of the requested shape. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* BMP_TryEncodePalettised:                                           */
        /*                                                                    */
        /* Writes a 4- or 8-bit palettised BMP, optionally run-length         */
        /* compressed.                                                        */
        /*                                                                    */
        /* THE PALETTE IS EXACT, NEVER QUANTISED. An image with more distinct */
        /* colours than the depth can name is REFUSED rather than reduced.    */
        /* That is the same line every encoder in this library draws:         */
        /* lossless conversions are done for the caller, lossy ones are       */
        /* theirs to ask for. Colour quantisation is real work with real      */
        /* choices in it -- palette selection, dithering -- and it belongs in */
        /* the GIF encoder, which needs it and which has options to control   */
        /* it. Doing a hidden, undithered, first-256-colours                  */
        /* reduction here would be the worst of both worlds.                  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     imgIn    : Source image, 8-bit, 1 or 3 channels.               */
        /*     optIn    : Encoder settings; never null by the time this runs. */
        /*     arrOut   : Receives the complete BMP file; null on failure.    */
        /*     sReason  : Receives "" on success, or why it was refused.      */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool     : true when arrOut holds a complete BMP file.         */
        /*--------------------------------------------------------------------*/

        private static bool BMP_TryEncodePalettised(ImageBuffer imgIn, BmpEncodeOptions optIn,
                                                    out byte[] arrOut, out string sReason)
        {
            ImageBuffer imgUse;
            ByteWriter  bwData;
            ByteWriter  bwOut;
            byte[]      arrPalette;
            byte[]      arrIndices;
            byte[]      arrData;
            int         iMaxColours;
            int         iPaletteCount;
            int         iCompression;
            int         iRowBytes;
            int         iPad;
            int         iOffset;
            int         iRow;
            int         iHeightField;
            int         iY;
            int         i;

            arrOut  = null;
            sReason = "";

            if (imgIn.IMG_iChannels == ImageBuffer.IMG_CHANNELS_GRAY_ALPHA ||
                imgIn.IMG_iChannels == ImageBuffer.IMG_CHANNELS_RGBA)
            {   /* A palettised BMP has no alpha channel at all, so encoding   */
                /* one would silently discard it. Refuse, as the 24-bit path   */
                /* does for the same reason.                                   */
                sReason = "BMP: a palettised file cannot carry the source's alpha channel; " +
                          "convert to RGB first, or use 32 bits";
                return(false);
            }

            if (optIn.BMPOPT_bRleCompress && optIn.BMPOPT_bTopDown)
            {   /* Microsoft's rule, enforced on the way out as well as in:   */
                /* a top-down DIB cannot be compressed.                       */
                sReason = "BMP: a top-down DIB cannot be RLE compressed";
                return(false);
            }

            if (imgIn.IMG_iChannels != ImageBuffer.IMG_CHANNELS_RGB)
            {   /* Grayscale widens losslessly, so it is done without asking. */
                if (!ImageBuffer.IMG_TryConvertChannels(imgIn, ImageBuffer.IMG_CHANNELS_RGB,
                                                        out imgUse, out sReason))
                {   /* The conversion failed; pass its reason up. */
                    return(false);
                }
            }
            else
            {   /* Already three channels. */
                imgUse = imgIn;
            }

            if (optIn.BMPOPT_iBitsPerPixel == 4)
            {   /* A four-bit index can name sixteen colours. */
                iMaxColours = 16;
            }
            else
            {   /* An eight-bit index can name two hundred and fifty-six. */
                iMaxColours = 256;
            }

            if (!BMP_TryBuildPalette(imgUse, iMaxColours, out arrPalette, out iPaletteCount,
                                     out arrIndices, out sReason))
            {   /* Too many distinct colours, or the image is unusable. */
                return(false);
            }

            /* Build the pixel data first, because an RLE stream's length is   */
            /* not known until it has been produced and the header has to      */
            /* state it.                                                       */
            bwData    = new ByteWriter(imgUse.IMG_iWidth * imgUse.IMG_iHeight);
            iRowBytes = 0;
            iPad      = 0;

            if (!optIn.BMPOPT_bRleCompress)
            {   /* Uncompressed: packed indices, each row padded to four bytes. */
                if (optIn.BMPOPT_iBitsPerPixel == 4)
                {   /* Two indices per byte, rounded up. */
                    iRowBytes = (imgUse.IMG_iWidth + 1) >> 1;
                }
                else
                {   /* One index per byte. */
                    iRowBytes = imgUse.IMG_iWidth;
                }

                iPad = (-iRowBytes) & 3;
            }

            for (iY = 0; iY < imgUse.IMG_iHeight; iY++)
            {   /* Rows go out in file order: bottom-up unless asked otherwise. */
                if (optIn.BMPOPT_bTopDown)
                {   /* Top-down: natural order. */
                    iRow = iY;
                }
                else
                {   /* Bottom-up: the image's last row is stored first. */
                    iRow = imgUse.IMG_iHeight - 1 - iY;
                }

                if (optIn.BMPOPT_bRleCompress)
                {   /* One RLE operation stream per row, terminated by an   */
                    /* end-of-line marker.                                  */
                    if (optIn.BMPOPT_iBitsPerPixel == 4)
                    {   /* Nibble-packed runs. */
                        BMP_WriteRle4Row(bwData, arrIndices, iRow * imgUse.IMG_iWidth,
                                         imgUse.IMG_iWidth);
                    }
                    else
                    {   /* Byte runs. */
                        BMP_WriteRle8Row(bwData, arrIndices, iRow * imgUse.IMG_iWidth,
                                         imgUse.IMG_iWidth);
                    }

                    bwData.BYTEW_WriteU8(0);             // end of line
                    bwData.BYTEW_WriteU8(0);
                }
                else
                {   /* Uncompressed: pack the row's indices and pad it. */
                    BMP_WritePackedRow(bwData, arrIndices, iRow * imgUse.IMG_iWidth,
                                       imgUse.IMG_iWidth, optIn.BMPOPT_iBitsPerPixel);

                    for (i = 0; i < iPad; i++)
                    {   /* Pad the row to a four-byte boundary. */
                        bwData.BYTEW_WriteU8(0);
                    }
                }
            }

            if (optIn.BMPOPT_bRleCompress)
            {   /* The stream ends with an explicit end-of-bitmap marker.    */
                /* An end-of-line was already written after the final row;   */
                /* that is harmless and is what most encoders emit.          */
                bwData.BYTEW_WriteU8(0);
                bwData.BYTEW_WriteU8(1);

                iCompression = (optIn.BMPOPT_iBitsPerPixel == 4) ? BMP_BI_RLE4 : BMP_BI_RLE8;
            }
            else
            {   /* Uncompressed. */
                iCompression = BMP_BI_RGB;
            }

            arrData = bwData.BYTEW_ToArray();
            iOffset = BMP_FILE_HEADER_SIZE + BMP_HDR_INFO + (iPaletteCount * 4);

            bwOut = new ByteWriter(iOffset + arrData.Length);

            /* File header. */
            bwOut.BYTEW_WriteU8((int)'B');
            bwOut.BYTEW_WriteU8((int)'M');
            bwOut.BYTEW_WriteU32LE((uint)(iOffset + arrData.Length));
            bwOut.BYTEW_WriteU16LE(0);
            bwOut.BYTEW_WriteU16LE(0);
            bwOut.BYTEW_WriteU32LE((uint)iOffset);

            if (optIn.BMPOPT_bTopDown)
            {   /* A negative height declares top-down storage. */
                iHeightField = -imgUse.IMG_iHeight;
            }
            else
            {   /* Conventional bottom-up. */
                iHeightField = imgUse.IMG_iHeight;
            }

            /* BITMAPINFOHEADER. Unlike the uncompressed direct-colour case,   */
            /* an RLE file must carry a real image-size field, so it is        */
            /* written for both here rather than left at zero.                 */
            bwOut.BYTEW_WriteU32LE((uint)BMP_HDR_INFO);
            bwOut.BYTEW_WriteU32LE((uint)imgUse.IMG_iWidth);
            bwOut.BYTEW_WriteU32LE(unchecked((uint)iHeightField));
            bwOut.BYTEW_WriteU16LE(1);
            bwOut.BYTEW_WriteU16LE(optIn.BMPOPT_iBitsPerPixel);
            bwOut.BYTEW_WriteU32LE((uint)iCompression);
            bwOut.BYTEW_WriteU32LE((uint)arrData.Length);
            bwOut.BYTEW_WriteU32LE(0);                   // horizontal resolution
            bwOut.BYTEW_WriteU32LE(0);                   // vertical resolution
            bwOut.BYTEW_WriteU32LE((uint)iPaletteCount); // colours used
            bwOut.BYTEW_WriteU32LE(0);                   // important colours

            for (i = 0; i < iPaletteCount; i++)
            {   /* The colour table, in the format's own B, G, R, unused order. */
                bwOut.BYTEW_WriteU8((int)arrPalette[(i * 4) + 2]);
                bwOut.BYTEW_WriteU8((int)arrPalette[(i * 4) + 1]);
                bwOut.BYTEW_WriteU8((int)arrPalette[(i * 4)]);
                bwOut.BYTEW_WriteU8(0);
            }

            bwOut.BYTEW_WriteBytes(arrData, 0, arrData.Length);

            arrOut = bwOut.BYTEW_ToArray();

            /* A complete palettised BMP, compressed or not as asked. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* BMP_TryBuildPalette:                                               */
        /*                                                                    */
        /* Collects the distinct colours of an RGB image into a palette and   */
        /* produces the per-pixel index array that names them.                */
        /*                                                                    */
        /* Colours are numbered in the order they are first ENCOUNTERED,      */
        /* scanning top-left to bottom-right. That is arbitrary but it is     */
        /* deterministic, which is what matters: the same image always        */
        /* produces the same palette and therefore the same file.             */
        /*                                                                    */
        /* Refuses rather than quantises when the image has more colours than */
        /* the depth allows -- see BMP_TryEncodePalettised for why.           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     imgIn          : Source image, 8-bit, exactly 3 channels.      */
        /*     iMaxColours    : 16 for a 4-bit file, 256 for an 8-bit one.    */
        /*     arrPaletteOut  : Receives 4 bytes per entry as R, G, B, A.     */
        /*     iCountOut      : Receives the number of distinct colours.      */
        /*     arrIndicesOut  : Receives one palette index per pixel.         */
        /*     sReason        : Receives "" or why the image was refused.     */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool           : true when the image fits the palette.         */
        /*--------------------------------------------------------------------*/

        private static bool BMP_TryBuildPalette(ImageBuffer imgIn, int iMaxColours,
                                                out byte[] arrPaletteOut, out int iCountOut,
                                                out byte[] arrIndicesOut, out string sReason)
        {
            Dictionary<int, int> dictSeen;
            byte[]               arrPalette;
            byte[]               arrIndices;
            int                  iPixels;
            int                  iCount;
            int                  iKey;
            int                  iIndex;
            int                  iSrc;
            int                  i;

            arrPaletteOut = null;
            iCountOut     = 0;
            arrIndicesOut = null;
            sReason       = "";

            iPixels    = imgIn.IMG_iWidth * imgIn.IMG_iHeight;
            arrPalette = new byte[iMaxColours * 4];
            arrIndices = new byte[iPixels];
            dictSeen   = new Dictionary<int, int>();
            iCount     = 0;

            for (i = 0; i < iPixels; i++)
            {   /* One pixel per iteration, in reading order. */
                iSrc = i * 3;

                /* Pack the three channels into one int so the dictionary has   */
                /* a cheap key. Alpha plays no part: a palettised BMP has       */
                /* none, and a source carrying one was refused before this.     */
                iKey = ((int)imgIn.IMG_arrSamples8[iSrc] << 16) |
                       ((int)imgIn.IMG_arrSamples8[iSrc + 1] << 8) |
                        (int)imgIn.IMG_arrSamples8[iSrc + 2];

                if (!dictSeen.TryGetValue(iKey, out iIndex))
                {   /* A colour not seen before; give it the next slot. */
                    if (iCount >= iMaxColours)
                    {   /* Out of palette entries. Refuse rather than reduce.   */
                        /* THE REFUSAL IS PERMANENT, and the wording says so.   */
                        /* It used to say quantisation was "not implemented     */
                        /* yet", which stopped being true: the                  */
                        /* quantiser exists, in GifCodec. It is not reached     */
                        /* from here because BMP has no option asking for it,   */
                        /* and a hidden undithered reduction is the worst       */
                        /* available answer.  That is deliberate, not an        */
                        /* omission: a caller who wants a lossy reduction       */
                        /* asks GIF for one, where the palette size and the     */
                        /* dither ARE options.                                  */
                        sReason = "BMP: the image has more than " + iMaxColours.ToString() +
                                  " distinct colours, which a " +
                                  ((iMaxColours == 16) ? "4" : "8") +
                                  "-bit palette cannot hold; this encoder takes an EXACT " +
                                  "palette and never quantises";
                        return(false);
                    }

                    iIndex = iCount;
                    dictSeen.Add(iKey, iIndex);

                    arrPalette[(iIndex * 4)]     = imgIn.IMG_arrSamples8[iSrc];
                    arrPalette[(iIndex * 4) + 1] = imgIn.IMG_arrSamples8[iSrc + 1];
                    arrPalette[(iIndex * 4) + 2] = imgIn.IMG_arrSamples8[iSrc + 2];
                    arrPalette[(iIndex * 4) + 3] = 255;

                    iCount = iCount + 1;
                }

                arrIndices[i] = (byte)iIndex;
            }

            if (iCount < 1)
            {   /* An image with no pixels cannot have been allocated, so   */
                /* this is unreachable -- stated rather than assumed.       */
                sReason = "BMP: the image yielded no colours";
                return(false);
            }

            arrPaletteOut = arrPalette;
            iCountOut     = iCount;
            arrIndicesOut = arrIndices;

            /* Every pixel now has an index, and every index a colour. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* BMP_WritePackedRow:                                                */
        /*                                                                    */
        /* Writes one uncompressed row of palette indices, packed for the     */
        /* depth. The caller adds the row padding.                            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     bwIo         : Writer to append to.                            */
        /*     arrIndices   : Per-pixel palette indices for the whole image.  */
        /*     iRowStart    : Index of this row's first pixel.                */
        /*     iWidth       : Pixels in the row.                              */
        /*     iBpp         : 4 or 8.                                         */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void         : The packed row is appended.                     */
        /*--------------------------------------------------------------------*/

        private static void BMP_WritePackedRow(ByteWriter bwIo, byte[] arrIndices,
                                               int iRowStart, int iWidth, int iBpp)
        {
            int iLow;
            int i;

            if (iBpp == 8)
            {   /* One index per byte. */
                for (i = 0; i < iWidth; i++)
                {   /* Straight copy. */
                    bwIo.BYTEW_WriteU8((int)arrIndices[iRowStart + i]);
                }

                return;
            }

            for (i = 0; i < iWidth; i += 2)
            {   /* Two indices per byte, the left pixel in the HIGH nibble. */
                if ((i + 1) < iWidth)
                {   /* A full pair. */
                    iLow = (int)arrIndices[iRowStart + i + 1];
                }
                else
                {   /* Odd width: the final byte's low nibble is unused. */
                    iLow = 0;
                }

                bwIo.BYTEW_WriteU8((((int)arrIndices[iRowStart + i] & 0x0F) << 4) | (iLow & 0x0F));
            }
        }

        /*--------------------------------------------------------------------*/
        /* BMP_WriteRle8Row:                                                  */
        /*                                                                    */
        /* Run-length encodes one row at 8 bits per pixel.                    */
        /*                                                                    */
        /* THE HEURISTIC, and why it is where it is: an encoded run costs two */
        /* bytes whatever its length, while the same pixels inside an         */
        /* absolute run cost one byte each plus a two-byte header for the run */
        /* as a whole. So a repeat of three or more is always worth encoding, */
        /* a repeat of two breaks even, and a repeat of one is worse. This    */
        /* encoder therefore takes runs of three or more and gathers          */
        /* everything else into absolute runs -- falling back to one- and     */
        /* two-pixel encoded runs when there are too few literals to reach    */
        /* absolute mode's three-pixel minimum.                               */
        /*                                                                    */
        /* The result is valid rather than optimal, and that distinction is   */
        /* deliberate: the format lets any valid encoding stand, an optimal   */
        /* one would need dynamic programming across the row, and the         */
        /* difference on real images is a few percent.                        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     bwIo         : Writer to append to.                            */
        /*     arrIndices   : Per-pixel palette indices for the whole image.  */
        /*     iRowStart    : Index of this row's first pixel.                */
        /*     iWidth       : Pixels in the row.                              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void         : The row's operations are appended, without a    */
        /*                    terminating end-of-line marker.                 */
        /*--------------------------------------------------------------------*/

        private static void BMP_WriteRle8Row(ByteWriter bwIo, byte[] arrIndices,
                                             int iRowStart, int iWidth)
        {
            int iRunLength;
            int iLiteralStart;
            int iX;

            iX = 0;

            while (iX < iWidth)
            {   /* One operation per iteration. */
                iRunLength = BMP_CountRun8(arrIndices, iRowStart, iWidth, iX);

                if (iRunLength >= 3)
                {   /* Worth an encoded run on its own. */
                    bwIo.BYTEW_WriteU8(iRunLength);
                    bwIo.BYTEW_WriteU8((int)arrIndices[iRowStart + iX]);
                    iX = iX + iRunLength;
                }
                else
                {   /* Gather literals until a run worth encoding appears. */
                    iLiteralStart = iX;

                    while (iX < iWidth && (iX - iLiteralStart) < 255)
                    {   /* Stop as soon as a three-or-longer run begins. */
                        if (BMP_CountRun8(arrIndices, iRowStart, iWidth, iX) >= 3)
                        {   /* Leave that run for the next iteration. */
                            break;
                        }

                        iX = iX + 1;
                    }

                    BMP_FlushRle8Literals(bwIo, arrIndices, iRowStart + iLiteralStart,
                                          iX - iLiteralStart);
                }
            }
        }

        /*--------------------------------------------------------------------*/
        /* BMP_CountRun8:                                                     */
        /*                                                                    */
        /* Counts how many consecutive pixels from a position share the same  */
        /* index, capped at 255 and at the end of the row.                    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     arrIndices  : Per-pixel palette indices.                       */
        /*     iRowStart   : Index of the row's first pixel.                  */
        /*     iWidth      : Pixels in the row.                               */
        /*     iX          : Column to start counting from.                   */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int         : Run length, at least 1.                          */
        /*--------------------------------------------------------------------*/

        private static int BMP_CountRun8(byte[] arrIndices, int iRowStart, int iWidth, int iX)
        {
            byte byValue;
            int  iRun;

            byValue = arrIndices[iRowStart + iX];
            iRun    = 1;

            while ((iX + iRun) < iWidth && iRun < 255 &&
                   arrIndices[iRowStart + iX + iRun] == byValue)
            {   /* Same index, still inside the row, still inside the count. */
                iRun = iRun + 1;
            }

            /* At least the pixel we started on. */
            return(iRun);
        }

        /*--------------------------------------------------------------------*/
        /* BMP_FlushRle8Literals:                                             */
        /*                                                                    */
        /* Emits a gathered run of non-repeating pixels, choosing absolute    */
        /* mode when there are enough of them and short encoded runs when     */
        /* there are not.                                                     */
        /*                                                                    */
        /* Absolute mode needs at least three pixels -- counts of 0, 1 and 2  */
        /* are the escape codes for end-of-line, end-of-bitmap and delta --   */
        /* so one or two leftover literals must go out as encoded runs of     */
        /* length one or two instead. Forgetting that produces a stream that  */
        /* reads as an escape and derails the whole row.                      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     bwIo        : Writer to append to.                             */
        /*     arrIndices  : Per-pixel palette indices.                       */
        /*     iStart      : Absolute index of the first literal.             */
        /*     iCount      : Number of literals.                              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void        : The literals are appended in some valid form.    */
        /*--------------------------------------------------------------------*/

        private static void BMP_FlushRle8Literals(ByteWriter bwIo, byte[] arrIndices,
                                                  int iStart, int iCount)
        {
            int i;

            if (iCount < 1)
            {   /* Nothing gathered; emit nothing. */
                return;
            }

            if (iCount < 3)
            {   /* Too few for absolute mode, whose count byte would collide   */
                /* with an escape code. Emit them as short encoded runs.       */
                for (i = 0; i < iCount; i++)
                {   /* One pixel per two-byte operation; wasteful but valid. */
                    bwIo.BYTEW_WriteU8(1);
                    bwIo.BYTEW_WriteU8((int)arrIndices[iStart + i]);
                }

                return;
            }

            bwIo.BYTEW_WriteU8(0);
            bwIo.BYTEW_WriteU8(iCount);

            for (i = 0; i < iCount; i++)
            {   /* The literal indices, one byte each. */
                bwIo.BYTEW_WriteU8((int)arrIndices[iStart + i]);
            }

            if ((iCount & 1) != 0)
            {   /* An absolute run occupies an even number of bytes. */
                bwIo.BYTEW_WriteU8(0);
            }
        }

        /*--------------------------------------------------------------------*/
        /* BMP_WriteRle4Row:                                                  */
        /*                                                                    */
        /* Run-length encodes one row at 4 bits per pixel.                    */
        /*                                                                    */
        /* RLE4'S ENCODED MODE IS NOT A RUN OF ONE COLOUR -- it is a run of   */
        /* an ALTERNATING PAIR. The value byte holds two indices and the      */
        /* decoder emits them alternately, high nibble first, for the whole   */
        /* count. A solid run is just the case where both nibbles are equal,  */
        /* so writing the general form costs nothing extra and additionally   */
        /* compresses two-colour dither and hatch patterns, which are exactly */
        /* what 16-colour images are full of.                                 */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     bwIo         : Writer to append to.                            */
        /*     arrIndices   : Per-pixel palette indices for the whole image.  */
        /*     iRowStart    : Index of this row's first pixel.                */
        /*     iWidth       : Pixels in the row.                              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void         : The row's operations are appended, without a    */
        /*                    terminating end-of-line marker.                 */
        /*--------------------------------------------------------------------*/

        private static void BMP_WriteRle4Row(ByteWriter bwIo, byte[] arrIndices,
                                             int iRowStart, int iWidth)
        {
            int iRunLength;
            int iLiteralStart;
            int iFirst;
            int iSecond;
            int iX;

            iX = 0;

            while (iX < iWidth)
            {   /* One operation per iteration. */
                iRunLength = BMP_CountRun4(arrIndices, iRowStart, iWidth, iX);

                if (iRunLength >= 3)
                {   /* Worth an encoded run. The two nibbles are this pixel     */
                    /* and the next; for a solid run they are the same value.   */
                    iFirst = (int)arrIndices[iRowStart + iX];

                    if ((iX + 1) < iWidth)
                    {   /* The alternating partner. */
                        iSecond = (int)arrIndices[iRowStart + iX + 1];
                    }
                    else
                    {   /* A one-pixel row end; repeat the value harmlessly. */
                        iSecond = iFirst;
                    }

                    bwIo.BYTEW_WriteU8(iRunLength);
                    bwIo.BYTEW_WriteU8(((iFirst & 0x0F) << 4) | (iSecond & 0x0F));
                    iX = iX + iRunLength;
                }
                else
                {   /* Gather literals until a run worth encoding appears. */
                    iLiteralStart = iX;

                    while (iX < iWidth && (iX - iLiteralStart) < 255)
                    {   /* Stop as soon as a three-or-longer run begins. */
                        if (BMP_CountRun4(arrIndices, iRowStart, iWidth, iX) >= 3)
                        {   /* Leave that run for the next iteration. */
                            break;
                        }

                        iX = iX + 1;
                    }

                    BMP_FlushRle4Literals(bwIo, arrIndices, iRowStart + iLiteralStart,
                                          iX - iLiteralStart);
                }
            }
        }

        /*--------------------------------------------------------------------*/
        /* BMP_CountRun4:                                                     */
        /*                                                                    */
        /* Counts how far an ALTERNATING PAIR pattern continues from a        */
        /* position -- a, b, a, b, ... -- capped at 255 and the row end.      */
        /*                                                                    */
        /* A solid run of one colour is the case a == b, so this single       */
        /* function serves both and there is no separate solid-run path to    */
        /* keep in step with it.                                              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     arrIndices  : Per-pixel palette indices.                       */
        /*     iRowStart   : Index of the row's first pixel.                  */
        /*     iWidth      : Pixels in the row.                               */
        /*     iX          : Column to start counting from.                   */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int         : Run length, at least 1.                          */
        /*--------------------------------------------------------------------*/

        private static int BMP_CountRun4(byte[] arrIndices, int iRowStart, int iWidth, int iX)
        {
            byte byFirst;
            byte bySecond;
            byte byWanted;
            int  iRun;

            byFirst = arrIndices[iRowStart + iX];

            if ((iX + 1) >= iWidth)
            {   /* Only one pixel left in the row. */
                return(1);
            }

            bySecond = arrIndices[iRowStart + iX + 1];
            iRun     = 2;

            while ((iX + iRun) < iWidth && iRun < 255)
            {   /* Even offsets from the start must match the first value,   */
                /* odd offsets the second.                                   */
                if ((iRun & 1) == 0)
                {   /* An even step back to the first of the pair. */
                    byWanted = byFirst;
                }
                else
                {   /* An odd step, so the second. */
                    byWanted = bySecond;
                }

                if (arrIndices[iRowStart + iX + iRun] != byWanted)
                {   /* The pattern broke. */
                    break;
                }

                iRun = iRun + 1;
            }

            /* The length of the alternating pattern, at least two here. */
            return(iRun);
        }

        /*--------------------------------------------------------------------*/
        /* BMP_FlushRle4Literals:                                             */
        /*                                                                    */
        /* Emits gathered non-repeating pixels at 4 bits, in absolute mode    */
        /* when there are at least three and as short encoded runs otherwise. */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     bwIo        : Writer to append to.                             */
        /*     arrIndices  : Per-pixel palette indices.                       */
        /*     iStart      : Absolute index of the first literal.             */
        /*     iCount      : Number of literals.                              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void        : The literals are appended in some valid form.    */
        /*--------------------------------------------------------------------*/

        private static void BMP_FlushRle4Literals(ByteWriter bwIo, byte[] arrIndices,
                                                  int iStart, int iCount)
        {
            int iRunBytes;
            int iLow;
            int i;

            if (iCount < 1)
            {   /* Nothing gathered. */
                return;
            }

            if (iCount < 3)
            {   /* Too few for absolute mode; emit as encoded runs of one. */
                for (i = 0; i < iCount; i++)
                {   /* Both nibbles the same, so the run reads as one pixel. */
                    bwIo.BYTEW_WriteU8(1);
                    bwIo.BYTEW_WriteU8((((int)arrIndices[iStart + i] & 0x0F) << 4) |
                                        ((int)arrIndices[iStart + i] & 0x0F));
                }

                return;
            }

            bwIo.BYTEW_WriteU8(0);
            bwIo.BYTEW_WriteU8(iCount);

            for (i = 0; i < iCount; i += 2)
            {   /* Two indices per byte, first in the high nibble. */
                if ((i + 1) < iCount)
                {   /* A full pair. */
                    iLow = (int)arrIndices[iStart + i + 1];
                }
                else
                {   /* Odd count: the final low nibble is unused. */
                    iLow = 0;
                }

                bwIo.BYTEW_WriteU8((((int)arrIndices[iStart + i] & 0x0F) << 4) | (iLow & 0x0F));
            }

            iRunBytes = (iCount + 1) >> 1;

            if ((iRunBytes & 1) != 0)
            {   /* An absolute run occupies an even number of BYTES -- note   */
                /* that is bytes, not pixels, which is why the count is       */
                /* halved before the parity test.                             */
                bwIo.BYTEW_WriteU8(0);
            }
        }

        /*--------------------------------------------------------------------*/
        /* BMP_FlipVertically:                                                */
        /*                                                                    */
        /* Reverses the row order of a decoded image in place, to turn a      */
        /* bottom-up file into a top-down buffer.                             */
        /*                                                                    */
        /* Done as a separate pass after decoding rather than by writing rows */
        /* backwards during it.  The decode loops already juggle bit          */
        /* unpacking and row padding, and adding a reversed destination index */
        /* to that is where off-by-one errors live.  One extra pass over the  */
        /* buffer is cheap by comparison.                                     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     imgIo  : Buffer to flip, modified in place.                    */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void   : The rows are reversed.                                */
        /*--------------------------------------------------------------------*/

        private static void BMP_FlipVertically(ImageBuffer imgIo)
        {
            byte[] arrSamples;
            byte   bySwap;
            int    iStride;
            int    iTop;
            int    iBottom;
            int    iY;
            int    i;

            arrSamples = imgIo.IMG_arrSamples8;
            iStride    = imgIo.IMG_iWidth * imgIo.IMG_iChannels;

            for (iY = 0; iY < (imgIo.IMG_iHeight >> 1); iY++)
            {   /* Walk only half the rows, swapping each with its mirror. */
                iTop    = iY * iStride;
                iBottom = (imgIo.IMG_iHeight - 1 - iY) * iStride;

                for (i = 0; i < iStride; i++)
                {   /* Swap the two rows a sample at a time. */
                    bySwap                  = arrSamples[iTop + i];
                    arrSamples[iTop + i]    = arrSamples[iBottom + i];
                    arrSamples[iBottom + i] = bySwap;
                }
            }
        }

        /*--------------------------------------------------------------------*/
        /* BMP_ForceOpaque:                                                   */
        /*                                                                    */
        /* Sets every alpha sample to 255.  Applied only when the decoded     */
        /* image's alpha was zero everywhere -- see the compatibility note in */
        /* the file header for why that case is treated as "opaque" rather    */
        /* than "invisible".                                                  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     imgIo  : A four-channel buffer, modified in place.             */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void   : Every alpha sample becomes 255.                       */
        /*--------------------------------------------------------------------*/

        private static void BMP_ForceOpaque(ImageBuffer imgIo)
        {
            int i;

            for (i = 3; i < imgIo.IMG_arrSamples8.Length; i += 4)
            {   /* Every fourth sample is an alpha, starting at index 3. */
                imgIo.IMG_arrSamples8[i] = 255;
            }
        }

        /*--------------------------------------------------------------------*/
        /* BMP_TryExpectByte:                                                 */
        /*                                                                    */
        /* Reads one byte and checks it against an expected value, for the    */
        /* two signature bytes.                                               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     brIo       : Reader to take the byte from.                     */
        /*     iExpected  : The value it must have.                           */
        /*     sReason    : Receives "" or a short description.               */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool       : true when a byte was available and matched.       */
        /*--------------------------------------------------------------------*/

        private static bool BMP_TryExpectByte(ByteReader brIo, int iExpected, out string sReason)
        {
            int iGot;

            sReason = "";

            if (!brIo.BYTER_TryReadU8(out iGot))
            {   /* Ran out of bytes. */
                sReason = "BMP: truncated signature";
                return(false);
            }

            if (iGot != iExpected)
            {   /* Wrong byte; the caller phrases the message. */
                sReason = "BMP: unexpected signature byte";
                return(false);
            }

            /* The expected byte was present. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* BMP_HighBit:                                                       */
        /*                                                                    */
        /* Returns the index of the highest set bit, or -1 for zero.          */
        /* Transcoded from stbi__high_bit, including its binary-search shape. */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     uValue  : The value to examine.                                */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int     : Bit index 0..31, or -1 when uValue is zero.          */
        /*--------------------------------------------------------------------*/

        private static int BMP_HighBit(uint uValue)
        {
            uint uWork;
            int  iBit;

            if (uValue == 0u)
            {   /* No bits set at all. */
                return(-1);
            }

            uWork = uValue;
            iBit  = 0;

            if (uWork >= 0x10000u)
            {   /* Top half occupied; discard the low sixteen bits. */
                iBit  = iBit + 16;
                uWork = uWork >> 16;
            }

            if (uWork >= 0x00100u)
            {   /* Discard the low eight of what remains. */
                iBit  = iBit + 8;
                uWork = uWork >> 8;
            }

            if (uWork >= 0x00010u)
            {   /* Discard the low four. */
                iBit  = iBit + 4;
                uWork = uWork >> 4;
            }

            if (uWork >= 0x00004u)
            {   /* Discard the low two. */
                iBit  = iBit + 2;
                uWork = uWork >> 2;
            }

            if (uWork >= 0x00002u)
            {   /* One more bit above the base. */
                iBit = iBit + 1;
            }

            /* The index of the highest set bit. */
            return(iBit);
        }

        /*--------------------------------------------------------------------*/
        /* BMP_BitCount:                                                      */
        /*                                                                    */
        /* Counts the set bits in a value, by the classic parallel-sum trick. */
        /* Transcoded from stbi__bitcount.                                    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     uValue  : The value to examine.                                */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int     : Number of set bits, 0..32.                           */
        /*--------------------------------------------------------------------*/

        private static int BMP_BitCount(uint uValue)
        {
            uint uWork;

            uWork = uValue;

            /* Each step folds pairs of counts together: bits, then nibbles,   */
            /* then bytes, then halves.  No branches and no loop.              */
            uWork = (uWork & 0x55555555u) + ((uWork >> 1) & 0x55555555u);
            uWork = (uWork & 0x33333333u) + ((uWork >> 2) & 0x33333333u);
            uWork = (uWork + (uWork >> 4)) & 0x0F0F0F0Fu;
            uWork = uWork + (uWork >> 8);
            uWork = uWork + (uWork >> 16);

            /* The low byte now holds the total. */
            return((int)(uWork & 0xFFu));
        }

        /*--------------------------------------------------------------------*/
        /* BMP_ShiftSigned:                                                   */
        /*                                                                    */
        /* Extracts a channel of arbitrary width and scales it to a full      */
        /* 8-bit range.  Transcoded from stbi__shiftsigned, tables included.  */
        /*                                                                    */
        /* THE TABLES ARE BIT REPLICATION, NOT ARITHMETIC, and that is the    */
        /* part worth understanding.  A five-bit channel holding 31 must      */
        /* become 255, not 31 and not 248 -- so the value is not scaled by    */
        /* multiplying and dividing but by REPEATING its bit pattern up the   */
        /* byte: 11111 becomes 11111111.  The multiplier for a width of n     */
        /* is the number whose set bits sit exactly n apart, and the shift    */
        /* discards the surplus.  A naive value * 255 / 31 gives the same     */
        /* answer for the endpoints and differs in the middle, which is       */
        /* exactly the sort of one-unit disagreement that would show up       */
        /* against the GDI+ oracle and be hard to place.                      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     uValue  : The masked-off channel bits, still in position.      */
        /*     iShift  : Right shift to bring the high bit to position 7;     */
        /*               negative means shift left instead.                   */
        /*     iBits   : Width of the channel, 0..8.                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int     : The channel scaled to 0..255.                        */
        /*--------------------------------------------------------------------*/

        private static int BMP_ShiftSigned(uint uValue, int iShift, int iBits)
        {
            uint uWork;

            if (iBits < 0 || iBits > 8)
            {   /* Outside the tables.  The caller already refuses masks wider   */
                /* than eight bits, so reaching here means a logic error         */
                /* rather than bad data; return black instead of indexing off    */
                /* the end of the table.                                         */
                return(0);
            }

            uWork = uValue;

            if (iShift < 0)
            {   /* The channel sits below bit 7 and must move up. */
                uWork = uWork << (-iShift);
            }
            else
            {   /* The channel sits above bit 7 and must move down. */
                uWork = uWork >> iShift;
            }

            /* Drop the bits below the channel's own width, leaving the value   */
            /* right-aligned in iBits bits.                                     */
            uWork = uWork >> (8 - iBits);

            /* Replicate the pattern up to eight bits.  See the header. */
            return((int)((uWork * BMP_arrMulTable[iBits]) >> BMP_arrShiftTable[iBits]));
        }

        /*--------------------------------------------------------------------*/
        /* The replication tables from stbi__shiftsigned, indexed by channel  */
        /* width in bits.  Entry n turns an n-bit value into an 8-bit one by  */
        /* repeating its bit pattern; the shift table then discards the       */
        /* surplus bits the multiply produced above the byte.                 */
        /*--------------------------------------------------------------------*/

        private static readonly uint[] BMP_arrMulTable = new uint[9]
        {
            0u,                                          // width 0: unused
            0xFFu,                                       // 1 bit  -> 11111111
            0x55u,                                       // 2 bits -> 01010101
            0x49u,                                       // 3 bits -> 01001001
            0x11u,                                       // 4 bits -> 00010001
            0x21u,                                       // 5 bits -> 00100001
            0x41u,                                       // 6 bits -> 01000001
            0x81u,                                       // 7 bits -> 10000001
            0x01u                                        // 8 bits -> already full width
        };

        private static readonly int[] BMP_arrShiftTable = new int[9]
        {
            0,                                           // width 0: unused
            0,                                           // 1 bit
            0,                                           // 2 bits
            1,                                           // 3 bits
            0,                                           // 4 bits
            2,                                           // 5 bits
            4,                                           // 6 bits
            6,                                           // 7 bits
            0                                            // 8 bits
        };
    }
}
