/*----------------------------------------------------------------------------*/
/* SPDX-License-Identifier: MIT OR GPL-2.0-or-later                           */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* Dual-licensed at the recipient's choice -- see LICENSE in this folder for  */
/* the full terms of both licences.  GPL-2.0 full text in GPL-2.0.txt.        */
/*----------------------------------------------------------------------------*/
/* The chunk layer in this file is written from the PNG specification         */
/* (W3C/ISO 15948) rather than transcoded, because the two candidate sources  */
/* both fold chunk handling into their own I/O abstractions and neither       */
/* transcodes cleanly on its own. The PIXEL paths that follow ARE             */
/* transcoded, and each names its own source:                                 */
/*                                                                            */
/*   DECODE  stb_image.h v2.30 (nothings/stb, commit 2c980bb5)                */
/*   ENCODE  lodepng.cpp (lvandeve/lodepng, version 20260119, ed6fe582)       */
/*                                                                            */
/* THE SOURCE SPLIT IS DELIBERATE AND WAS DECIDED ON THE CODE rather than on  */
/* either project's description of itself. stb's DECODER is complete -- every */
/* bit depth, every colour type, tRNS, Adam7 -- and it is the lineage this    */
/* library already uses for BMP. But stb_image_write's PNG ENCODER writes     */
/* only 8-bit non-interlaced truecolour and grayscale: its colour-type table  */
/* is literally `int ctype[5] = { -1, 0, 4, 2, 6 }`, indexed by channel       */
/* count, with no palette entry and no depth choice. It cannot write the      */
/* shapes this codec writes. lodepng is spec-complete in both directions AND  */
/* its zlib half is separable by design (LODEPNG_NO_COMPILE_ZLIB, and a       */
/* custom_zlib hook), which matters because this library already has          */
/* ZlibStreamCodec and does not want a second deflate implementation.         */
/*                                                                            */
/* Both licences -- stb dual MIT/public-domain, lodepng zlib -- satisfy both  */
/* arms of this file's dual licence.                                          */
/*----------------------------------------------------------------------------*/
/* PngCodec.cs -- Portable Network Graphics                           (PNG_)  */
/*----------------------------------------------------------------------------*/
/* WHAT IS BUILT: PNG IN BOTH DIRECTIONS, THE WHOLE SPECIFICATION.            */
/*                                                                            */
/*   THE CHUNK LAYER  signature, chunk framing with CRC verification, IHDR    */
/*                    parse and validation, IEND, and the                     */
/*                    critical-versus-ancillary rule.                         */
/*   DECODE           depths 1, 2, 4, 8 and 16; colour types 0, 2, 3, 4 and   */
/*                    6; all five scanline filters; palette and sub-byte      */
/*                    grayscale; tRNS in both of its forms -- per-entry       */
/*                    palette alpha, and the single transparent colour that   */
/*                    types 0 and 2 name; Adam7 interlacing.                  */
/*   ENCODE           every shape decode can read, which is the same list:    */
/*                    every depth and colour type, palettised output with an  */
/*                    exact palette, tRNS, Adam7, and a per-scanline filter   */
/*                    heuristic.                                              */
/*                                                                            */
/* Every legal PNG this library is handed is decoded rather than refused, and */
/* PNG_TryEncode writes every shape PNG_TryDecode reads.                      */
/*                                                                            */
/* The refusals that survive are all statements about the FILE rather than    */
/* about this library -- a chunk combination the specification forbids, a     */
/* length that contradicts the header, a CRC that does not match. Each says   */
/* what is wrong rather than deferring to an unwritten capability, because    */
/* there is no longer one to defer to. While such refusals existed they were  */
/* deliberately worded to be told apart from these at a glance: a decoder     */
/* that quietly ignored a tRNS chunk would have handed back an image missing  */
/* its transparency and said nothing, which is the confident-but-wrong        */
/* output this codec is written to avoid.                                     */
/*                                                                            */
/* THE FILE STRUCTURE:                                                        */
/*                                                                            */
/*   8-byte signature   89 50 4E 47 0D 0A 1A 0A                               */
/*   then chunks, each  length (4 bytes, big-endian, of the DATA only)        */
/*                      type   (4 bytes, ASCII)                               */
/*                      data   (length bytes)                                 */
/*                      CRC    (4 bytes, over TYPE AND DATA, not the length)  */
/*                                                                            */
/* THE CRC DOES NOT COVER THE LENGTH FIELD, and that is why Crc32 grew an     */
/* incremental Begin/Update/Finish trio: the type and the data are            */
/* separate spans and concatenating them just to checksum would copy every    */
/* chunk.                                                                     */
/*                                                                            */
/* THE SIGNATURE IS DESIGNED, NOT ARBITRARY. The high bit in 0x89 detects a   */
/* transport that stripped it; "PNG" is readable in a hex dump; the CR-LF     */
/* pair catches a transfer that converted line endings; 0x1A stops MS-DOS     */
/* TYPE from dumping the rest of the file; and the trailing LF catches the    */
/* reverse line-ending conversion. Every byte of it is a different failure    */
/* mode being tested for.                                                     */
/*                                                                            */
/* CRITICAL VERSUS ANCILLARY, which is the rule that makes PNG extensible:    */
/* bit 5 of a chunk type's FIRST byte -- equivalently, whether that letter is */
/* lower case -- says whether a decoder may ignore the chunk. An ancillary    */
/* chunk it does not understand is skipped; a CRITICAL chunk it does not      */
/* understand must cause it to REFUSE THE FILE, because that chunk may change */
/* how the pixels are to be interpreted. Guessing there is how a decoder      */
/* produces confident, wrong output.                                          */
/*                                                                            */
/* PORTABILITY: pure array and integer work, no platform surface.             */
/*----------------------------------------------------------------------------*/

using System;
using System.Collections.Generic;

namespace GraphicsEncDec
{
    /*------------------------------------------------------------------------*/
    /* PngHeader:                                                             */
    /* Everything IHDR says about an image, plus two things derived from it   */
    /* that every later stage needs and that are easy to get wrong when       */
    /* recomputed at each use.                                                */
    /*                                                                        */
    /* This type exists as public API in its own right, not only as decoder   */
    /* scaffolding: a consumer often needs an image's dimensions WITHOUT      */
    /* decoding it. The case this was written for is a client picking a       */
    /* thumbnail by size before ever fetching the pixels, which is common     */
    /* enough that hand-rolled IHDR readers get written for it repeatedly.    */
    /* PNG_TryReadHeader replaces them.                                       */
    /*------------------------------------------------------------------------*/

    public sealed class PngHeader
    {
        /*--------------------------------------------------------------------*/
        /* Straight from IHDR.                                                */
        /*--------------------------------------------------------------------*/

        public int PNGH_iWidth;                          // pixels across, at least 1
        public int PNGH_iHeight;                         // pixel rows, at least 1
        public int PNGH_iBitDepth;                       // 1, 2, 4, 8 or 16 -- bits per SAMPLE
        public int PNGH_iColorType;                      // 0, 2, 3, 4 or 6; see PNG_COLOR_*
        public int PNGH_iInterlace;                      // 0 none, 1 Adam7

        /*--------------------------------------------------------------------*/
        /* Derived, because every later stage needs them and the derivation   */
        /* is a small table that is easy to misremember.                      */
        /*--------------------------------------------------------------------*/

        public int  PNGH_iSamplesPerPixel;               // 1, 2, 3 or 4 -- see PNG_SamplesForColorType
        public bool PNGH_bIsPalettised;                  // colour type 3: samples are palette indices
        public bool PNGH_bHasAlpha;                      // colour type 4 or 6 carry an alpha SAMPLE
    }

    /*------------------------------------------------------------------------*/
    /* PngEncodeOptions:                                                      */
    /* What a caller may ask of the encoder. This shape is part of the        */
    /* library's public contract: fields are added, never repurposed, and a   */
    /* null options argument means the defaults.                              */
    /*                                                                        */
    /* A null options argument is legal everywhere in this library and means  */
    /* the defaults; see PNG_DefaultEncodeOptions.                            */
    /*                                                                        */
    /* THE ZERO SENTINEL ON PNGOPT_iColorType LOOKS LIKE A COLLISION AND IS   */
    /* NOT ONE, which is worth writing down because the next reader will      */
    /* notice it and wonder. Zero means "match the source buffer", and zero   */
    /* is also PNG_COLOR_GRAY, so a caller cannot explicitly ask for          */
    /* grayscale output. The request that collides is "grayscale from a       */
    /* buffer that is not already grayscale" -- and that is a LOSSY           */
    /* narrowing, three channels collapsing into one, which this encoder      */
    /* refuses on principle exactly as BMP refuses to drop an alpha channel   */
    /* and as every palettised encoder here refuses to quantise. A caller     */
    /* who wants it says so by converting the buffer first with               */
    /* ImageBuffer.IMG_TryConvertChannels, which makes the loss explicit at   */
    /* the call site. So the only meaning the sentinel steals is one the      */
    /* encoder would have refused anyway, and the frozen surface stands.      */
    /*------------------------------------------------------------------------*/

    public sealed class PngEncodeOptions
    {
        public int  PNGOPT_iCompressionLevel;            // 0 none, 1 fastest, 6 default, 9 smallest
        public bool PNGOPT_bInterlace;                   // true writes Adam7
        public int  PNGOPT_iBitDepth;                    // 1 to 16; 0 matches the source buffer
        public int  PNGOPT_iColorType;                   // PNG_COLOR_*; 0 matches the source buffer
    }

    /*------------------------------------------------------------------------*/
    /* PngCodec:                                                              */
    /* Static-only. The chunk layer, the header reader, and every pixel path  */
    /* above them: unfiltering, unpacking, palette expansion and encoding.    */
    /*------------------------------------------------------------------------*/

    public static class PngCodec
    {
        /*--------------------------------------------------------------------*/
        /* Colour types. The values are the specification's own and the gaps  */
        /* are real -- 1 and 5 are not defined -- because the number is a bit */
        /* field: bit 0 means palette used, bit 1 means colour used, bit 2    */
        /* means alpha used. That is why 3 is "palette + colour" and 6 is     */
        /* "colour + alpha", and why 1 (palette without colour) and 5         */
        /* (palette with alpha) do not exist.                                 */
        /*--------------------------------------------------------------------*/

        public const int PNG_COLOR_GRAY       = 0;       // one grayscale sample
        public const int PNG_COLOR_RGB        = 2;       // three samples
        public const int PNG_COLOR_PALETTE    = 3;       // one sample, an index into PLTE
        public const int PNG_COLOR_GRAY_ALPHA = 4;       // two samples
        public const int PNG_COLOR_RGBA       = 6;       // four samples

        /*--------------------------------------------------------------------*/
        /* Interlace methods. Only these two are defined.                     */
        /*--------------------------------------------------------------------*/

        public const int PNG_INTERLACE_NONE  = 0;        // rows in order, top to bottom
        public const int PNG_INTERLACE_ADAM7 = 1;        // seven interleaved passes

        /*--------------------------------------------------------------------*/
        /* Scanline filter types. EVERY scanline carries one of these in a    */
        /* byte of its own, ahead of its data, and the encoder picks it per   */
        /* row to make that row compress better.                              */
        /*                                                                    */
        /* THE FILTER IS NOT A PROPERTY OF THE IMAGE. Two encoders given the  */
        /* same pixels routinely choose different filters, the same encoder   */
        /* usually chooses differently for different rows of one image, and a */
        /* decoder must therefore handle all five on any row. That is why     */
        /* testing builds files that force each one rather than trusting a    */
        /* real encoder to have exercised them.                               */
        /*                                                                    */
        /* Each filter subtracts a PREDICTION from the true sample; decoding  */
        /* adds the same prediction back. The predictions use the byte to the */
        /* left (a), the byte above (b) and the byte above-left (c), where    */
        /* "to the left" means one whole PIXEL back, not one byte back.       */
        /*--------------------------------------------------------------------*/

        public const int PNG_FILTER_NONE    = 0;         // prediction 0: the bytes stand as they are
        public const int PNG_FILTER_SUB     = 1;         // prediction a: the byte one pixel to the left
        public const int PNG_FILTER_UP      = 2;         // prediction b: the byte in the row above
        public const int PNG_FILTER_AVERAGE = 3;         // prediction (a + b) >> 1
        public const int PNG_FILTER_PAETH   = 4;         // prediction: whichever of a, b, c is nearest a+b-c

        /*--------------------------------------------------------------------*/
        /* Chunk types, packed into a uint so a comparison is one integer     */
        /* test rather than four byte tests or a string allocation per chunk. */
        /* The ASCII is spelled out beside each so the constants stay         */
        /* readable.                                                          */
        /*--------------------------------------------------------------------*/

        public const uint PNG_TYPE_IHDR = 0x49484452u;   // "IHDR" image header, always first
        public const uint PNG_TYPE_PLTE = 0x504C5445u;   // "PLTE" palette
        public const uint PNG_TYPE_IDAT = 0x49444154u;   // "IDAT" compressed pixel data
        public const uint PNG_TYPE_IEND = 0x49454E44u;   // "IEND" end marker, always last
        public const uint PNG_TYPE_TRNS = 0x74524E53u;   // "tRNS" transparency

        /*--------------------------------------------------------------------*/
        /* Structural sizes.                                                  */
        /*--------------------------------------------------------------------*/

        private const int PNG_SIGNATURE_SIZE = 8;        // the eight designed bytes
        private const int PNG_CHUNK_OVERHEAD = 12;       // 4 length + 4 type + 4 CRC
        private const int PNG_IHDR_DATA_SIZE = 13;       // IHDR's data is exactly this long

        /*--------------------------------------------------------------------*/
        /* Where the IDAT accumulator starts. A PNG may split its compressed  */
        /* stream across any number of IDAT chunks and the decoder has to     */
        /* join them before inflating, so the pieces are appended to a        */
        /* ByteWriter. That grows by doubling, so this figure only decides    */
        /* how many doublings a large image costs -- it is a starting point,  */
        /* not a limit, and nothing breaks if an image needs more.            */
        /*--------------------------------------------------------------------*/

        private const int PNG_IDAT_START_CAPACITY = 8192;

        /*--------------------------------------------------------------------*/
        /* Encoder constants.                                                 */
        /*--------------------------------------------------------------------*/

        private const int PNG_DEFAULT_DEFLATE_LEVEL = 6;  // the conventional zlib default effort
        private const int PNG_ENCODE_OVERHEAD       = 96; // signature plus IHDR and IEND, with room

        /*--------------------------------------------------------------------*/
        /* Palette limits. A PLTE chunk holds three bytes per entry -- red,   */
        /* green, blue, each 8 bits -- and no more than 256 entries, because  */
        /* 8 is the deepest an index may be.                                  */
        /*--------------------------------------------------------------------*/

        private const int PNG_PALETTE_ENTRY_BYTES = 3;   // red, green, blue; alpha comes from tRNS
        private const int PNG_PALETTE_MAX_ENTRIES = 256; // an 8-bit index cannot reach further
        private const int PNG_RGBA_ENTRY_BYTES    = 4;   // the working palette's stride, alpha included

        /*---------------------------------------------------------------------*/
        /* SUB-BYTE SAMPLE SCALING, indexed by bit depth.                      */
        /*                                                                     */
        /* A grayscale sample narrower than a byte has to be widened to fill   */
        /* 0 to 255, and the right widening is REPLICATION, not division. A    */
        /* 4-bit value of 15 must become 255, not 240; a 2-bit value of 3 must */
        /* become 255, not 192. Multiplying by these figures does exactly      */
        /* that, because each one is the bit pattern repeated: 0x11 is 0001    */
        /* 0001, 0x55 is 01010101, 0xFF is 11111111. So value times multiplier */
        /* copies the value into every field of the byte, which is the same    */
        /* answer as value divided by the maximum, times 255, and needs no     */
        /* division.                                                           */
        /*                                                                     */
        /* This is the same replication rule BmpCodec's 16-bit path uses, and  */
        /* it is worth recognising as one rule rather than two: a 5-bit BMP    */
        /* channel of 31 becomes 255 for the same reason.                      */
        /*                                                                     */
        /* THE ZERO ENTRIES ARE UNREACHABLE, not defaults. Depths 3, 5, 6 and  */
        /* 7 do not exist in PNG and IHDR validation refuses them long before  */
        /* this table is indexed. Index 0 is likewise unreachable.             */
        /*                                                                     */
        /* PALETTE INDICES ARE NEVER SCALED. An index is a number that names   */
        /* an entry, not a light level, so scaling one would look up the       */
        /* wrong colour. That distinction is why the scale is chosen from the  */
        /* COLOUR TYPE rather than from the depth alone.                       */
        /*---------------------------------------------------------------------*/

        private static readonly byte[] PNG_arrDepthScale =
            new byte[] { 0, 0xFF, 0x55, 0, 0x11, 0, 0, 0, 0x01 };

        /*--------------------------------------------------------------------*/
        /* ADAM7 INTERLACING -- the seven-pass layout, as four tables.        */
        /*                                                                    */
        /* An interlaced PNG is not one image with its rows shuffled. It is   */
        /* SEVEN COMPLETE SUB-IMAGES, each a sparse sample of the whole on    */
        /* its own grid, stored one after another in the same zlib stream.    */
        /* Each pass is filtered and packed entirely independently: its own   */
        /* width, its own scanline padding, its own filter byte on every row. */
        /*                                                                    */
        /* Pass p takes the pixels whose coordinates are                      */
        /*                                                                    */
        /*     x = PNG_arrAdam7Xorig[p] + (i * PNG_arrAdam7Xspacing[p])       */
        /*     y = PNG_arrAdam7Yorig[p] + (j * PNG_arrAdam7Yspacing[p])       */
        /*                                                                    */
        /* which is the familiar 8x8 tile pattern: pass 0 is one pixel in     */
        /* every 8x8 block, and each later pass fills in half the gaps left   */
        /* by its predecessors until pass 6 supplies every remaining row.     */
        /* The point of the arrangement is that a decoder reading the stream  */
        /* progressively can display a whole coarse image after pass 0 rather */
        /* than a correct top third, which mattered a great deal over a       */
        /* modem and is why the format carries it at all.                     */
        /*                                                                    */
        /* A PASS WHOSE WIDTH OR HEIGHT WORKS OUT TO ZERO IS ABSENT FROM THE  */
        /* STREAM ENTIRELY -- no rows, and not even a filter byte. This is    */
        /* the detail that breaks implementations, because narrow or short    */
        /* images have several such passes: a one-pixel-wide image has five,  */
        /* and consuming so much as one byte for any of them puts every       */
        /* later pass out of step.                                            */
        /*--------------------------------------------------------------------*/

        private static readonly int[] PNG_arrAdam7Xorig     = new int[] { 0, 4, 0, 2, 0, 1, 0 };
        private static readonly int[] PNG_arrAdam7Yorig     = new int[] { 0, 0, 4, 0, 2, 0, 1 };
        private static readonly int[] PNG_arrAdam7Xspacing  = new int[] { 8, 8, 4, 4, 2, 2, 1 };
        private static readonly int[] PNG_arrAdam7Yspacing  = new int[] { 8, 8, 8, 4, 4, 2, 2 };

        private const int PNG_ADAM7_PASSES = 7;          // fixed by the format; never varies

        /*--------------------------------------------------------------------*/
        /* Bit 5 of a chunk type's first byte. Set means the letter is lower  */
        /* case, which means the chunk is ANCILLARY and may be ignored by a   */
        /* decoder that does not understand it. Clear means CRITICAL.         */
        /*--------------------------------------------------------------------*/

        private const uint PNG_ANCILLARY_BIT = 0x20000000u;

        /*--------------------------------------------------------------------*/
        /* The signature. See the file header for what each byte is for.      */
        /*--------------------------------------------------------------------*/

        private static readonly byte[] PNG_arrSignature =
            new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        /*--------------------------------------------------------------------*/
        /* PNG_TryReadHeader:                                                 */
        /*                                                                    */
        /* Reads and validates a PNG's signature and IHDR chunk, without      */
        /* decoding any pixels.                                               */
        /*                                                                    */
        /* Cheap by construction: it touches the first 33 bytes of the file   */
        /* and stops. A consumer choosing between several thumbnails by size  */
        /* can call this on each without paying for a decode, which is        */
        /* exactly what the first consumer does.                              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     arrData  : Buffer holding the PNG file. Must not be null.      */
        /*     iOffset  : Index of the first byte of the image.               */
        /*     iLength  : Bytes available from iOffset.                       */
        /*     hdrOut   : Receives the parsed header; null on failure.        */
        /*     sReason  : Receives "" on success, or why it was rejected.     */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool     : true when hdrOut holds a valid, self-consistent     */
        /*                IHDR.                                               */
        /*--------------------------------------------------------------------*/

        public static bool PNG_TryReadHeader(byte[] arrData, int iOffset, int iLength,
                                             out PngHeader hdrOut, out string sReason)
        {
            ByteReader brIn;
            byte[]     arrIhdr;
            uint       uLength;
            uint       uType;

            hdrOut  = null;
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

            if (!PNG_TryReadSignature(brIn, out sReason))
            {   /* Not a PNG at all. */
                return(false);
            }

            if (!PNG_TryReadChunkHeader(brIn, out uLength, out uType, out sReason))
            {   /* No chunk header where IHDR must be. */
                return(false);
            }

            if (uType != PNG_TYPE_IHDR)
            {   /* The specification requires IHDR to be the very first         */
                /* chunk. A file that starts with anything else is malformed,   */
                /* not merely unusual.                                          */
                sReason = "PNG: first chunk is " + PNG_TypeToText(uType) + ", not IHDR";
                return(false);
            }

            if (uLength != (uint)PNG_IHDR_DATA_SIZE)
            {   /* IHDR is a fixed thirteen bytes; any other length means the   */
                /* file is damaged or was written by something confused.        */
                sReason = "PNG: IHDR is " + uLength.ToString() + " bytes, not 13";
                return(false);
            }

            if (!PNG_TryReadChunkBody(brIn, arrData, iOffset, uLength, uType,
                                      out arrIhdr, out sReason))
            {   /* Truncated, or the CRC did not match. */
                return(false);
            }

            /* Parse and validate the thirteen bytes. */
            return(PNG_TryParseIhdr(arrIhdr, out hdrOut, out sReason));
        }

        /*--------------------------------------------------------------------*/
        /* PNG_TryReadSignature:                                              */
        /*                                                                    */
        /* Consumes and checks the eight-byte signature.                      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     brIo     : Reader positioned at the file's first byte; left    */
        /*                positioned after the signature on success.          */
        /*     sReason  : Receives "" or what was wrong.                      */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool     : true when the signature is present and correct.     */
        /*--------------------------------------------------------------------*/

        private static bool PNG_TryReadSignature(ByteReader brIo, out string sReason)
        {
            int iByte;
            int i;

            sReason = "";

            for (i = 0; i < PNG_SIGNATURE_SIZE; i++)
            {   /* Compare byte by byte so the failure can name the position,   */
                /* which distinguishes "not a PNG" from "a PNG damaged in       */
                /* transit" -- byte 4 or 7 wrong specifically suggests a line   */
                /* ending conversion, which is what those bytes are for.        */
                if (!brIo.BYTER_TryReadU8(out iByte))
                {   /* Fewer than eight bytes in the whole window. */
                    sReason = "PNG: file is too short to hold a signature";
                    return(false);
                }

                if (iByte != (int)PNG_arrSignature[i])
                {   /* Wrong byte; say which, for the reason above. */
                    sReason = "PNG: signature byte " + i.ToString() + " is 0x" +
                              iByte.ToString("X2") + ", expected 0x" +
                              PNG_arrSignature[i].ToString("X2");
                    return(false);
                }
            }

            /* All eight bytes matched. */
            return(true);
        }

        /*---------------------------------------------------------------------*/
        /* PNG_TryReadChunkHeader:                                             */
        /*                                                                     */
        /* Reads a chunk's length and type -- the eight bytes that precede     */
        /* its data.                                                           */
        /*                                                                     */
        /* THE LENGTH IS RANGE-CHECKED HERE rather than at the point of use.   */
        /* The field is an unsigned 32-bit value and the specification caps    */
        /* it at 2^31 - 1, but a corrupt file can hold 0xFFFFFFFF. Read into   */
        /* an int that becomes -1, and every later "is it too big" test        */
        /* passes. Keeping it unsigned to the check, and refusing anything     */
        /* above the cap, is what stops that.                                  */
        /*                                                                     */
        /* Arguments:                                                          */
        /*     brIo        : Reader positioned at the chunk's first byte.      */
        /*     uLengthOut  : Receives the DATA length, excluding type and CRC. */
        /*     uTypeOut    : Receives the type as four packed ASCII bytes.     */
        /*     sReason     : Receives "" or what was wrong.                    */
        /*                                                                     */
        /* Returns:                                                            */
        /*     bool        : true when a plausible chunk header was read.      */
        /*---------------------------------------------------------------------*/

        public static bool PNG_TryReadChunkHeader(ByteReader brIo, out uint uLengthOut,
                                                  out uint uTypeOut, out string sReason)
        {
            uLengthOut = 0u;
            uTypeOut   = 0u;
            sReason    = "";

            if (!brIo.BYTER_TryReadU32BE(out uLengthOut))
            {   /* Fewer than four bytes left; there is no chunk here. */
                sReason = "PNG: truncated before a chunk length";
                return(false);
            }

            if (uLengthOut > 0x7FFFFFFFu)
            {   /* Beyond the specification's own cap, and beyond anything an   */
                /* int index could address. Refuse before it is narrowed.       */
                sReason = "PNG: chunk length " + uLengthOut.ToString() +
                          " exceeds the 2^31-1 maximum";
                return(false);
            }

            if (!brIo.BYTER_TryReadU32BE(out uTypeOut))
            {   /* Length present but no type after it. */
                sReason = "PNG: truncated before a chunk type";
                return(false);
            }

            /* Length and type both read; the caller decides what to do next. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* PNG_TryReadChunkBody:                                              */
        /*                                                                    */
        /* Reads a chunk's data and verifies its CRC, having already consumed */
        /* the length and type.                                               */
        /*                                                                    */
        /* THE CRC COVERS THE TYPE AND THE DATA, NOT THE LENGTH. That is the  */
        /* detail worth stating, because the length sits immediately before   */
        /* the type and including it produces a checksum that is wrong for    */
        /* every chunk in every file -- a mistake that fails loudly and       */
        /* immediately, which is the good kind, but only if you know to look  */
        /* for it.                                                            */
        /*                                                                    */
        /* The type bytes are re-derived from the packed value rather than    */
        /* re-read, so the reader never has to seek backwards.                */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     brIo       : Reader positioned at the chunk's data.            */
        /*     arrData    : The underlying buffer, for the CRC span.          */
        /*     iOffset    : Where the reader's window starts in arrData.      */
        /*     uLength    : Data length from the chunk header.                */
        /*     uType      : Chunk type from the chunk header.                 */
        /*     arrBodyOut : Receives the chunk's data; null on failure. An    */
        /*                  empty chunk yields a zero-length array, not null. */
        /*     sReason    : Receives "" or what was wrong.                    */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool       : true when the data was read and its CRC matched.  */
        /*--------------------------------------------------------------------*/

        public static bool PNG_TryReadChunkBody(ByteReader brIo, byte[] arrData, int iOffset,
                                                uint uLength, uint uType,
                                                out byte[] arrBodyOut, out string sReason)
        {
            byte[] arrTypeBytes;
            byte[] arrBody;
            uint   uStoredCrc;
            uint   uActualCrc;
            uint   uRunning;
            int    iDataStart;

            arrBodyOut = null;
            sReason    = "";

            /* Where the data begins in the underlying array, for the CRC. */
            iDataStart = iOffset + brIo.BYTER_Position();

            if (!brIo.BYTER_TryReadBytes((int)uLength, out arrBody))
            {   /* The chunk claims more data than the file holds. */
                sReason = "PNG: " + PNG_TypeToText(uType) + " chunk claims " +
                          uLength.ToString() + " bytes but the file is shorter";
                return(false);
            }

            if (!brIo.BYTER_TryReadU32BE(out uStoredCrc))
            {   /* Data present but the four CRC bytes are missing. */
                sReason = "PNG: truncated before the " + PNG_TypeToText(uType) + " chunk CRC";
                return(false);
            }

            /* Rebuild the four type bytes from the packed value. */
            arrTypeBytes = new byte[4];
            arrTypeBytes[0] = (byte)((uType >> 24) & 0xFFu);
            arrTypeBytes[1] = (byte)((uType >> 16) & 0xFFu);
            arrTypeBytes[2] = (byte)((uType >> 8) & 0xFFu);
            arrTypeBytes[3] = (byte)(uType & 0xFFu);

            /* Type first, then data, in one running CRC. This is exactly the   */
            /* case Crc32's incremental form was built for.                     */
            uRunning   = Crc32.CRC32_Begin();
            uRunning   = Crc32.CRC32_Update(uRunning, arrTypeBytes, 0, 4);
            uRunning   = Crc32.CRC32_Update(uRunning, arrData, iDataStart, (int)uLength);
            uActualCrc = Crc32.CRC32_Finish(uRunning);

            if (uActualCrc != uStoredCrc)
            {   /* The chunk is damaged. Refusing is the whole point of the     */
                /* CRC being there; accepting it would mean decoding data the   */
                /* file itself says is wrong.                                   */
                sReason = "PNG: " + PNG_TypeToText(uType) + " chunk CRC is " +
                          uStoredCrc.ToString("X8") + " but the data gives " +
                          uActualCrc.ToString("X8");
                return(false);
            }

            arrBodyOut = arrBody;

            /* Data read and verified against its own checksum. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* PNG_TrySkipChunkBody:                                              */
        /*                                                                    */
        /* Steps over a chunk's data and CRC without reading or verifying     */
        /* either, for ancillary chunks this decoder does not use.            */
        /*                                                                    */
        /* THE CRC IS DELIBERATELY NOT CHECKED HERE. A damaged comment or     */
        /* timestamp chunk says nothing about whether the PIXELS are sound,   */
        /* and refusing a whole image because its tEXt chunk was corrupted in */
        /* transit would reject files every other decoder displays perfectly. */
        /* The chunks that matter -- IHDR, PLTE, IDAT, tRNS -- all go through */
        /* PNG_TryReadChunkBody and are checked.                              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     brIo     : Reader positioned at the chunk's data.              */
        /*     uLength  : Data length from the chunk header.                  */
        /*     uType    : Chunk type, for the failure text.                   */
        /*     sReason  : Receives "" or what was wrong.                      */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool     : true when the data and CRC were skipped.            */
        /*--------------------------------------------------------------------*/

        public static bool PNG_TrySkipChunkBody(ByteReader brIo, uint uLength, uint uType,
                                                out string sReason)
        {
            sReason = "";

            if (!brIo.BYTER_TrySkip((int)uLength))
            {   /* The chunk claims more data than the file holds. */
                sReason = "PNG: " + PNG_TypeToText(uType) + " chunk claims " +
                          uLength.ToString() + " bytes but the file is shorter";
                return(false);
            }

            if (!brIo.BYTER_TrySkip(4))
            {   /* Data skipped but the CRC bytes are missing. */
                sReason = "PNG: truncated before the " + PNG_TypeToText(uType) + " chunk CRC";
                return(false);
            }

            /* Positioned at the next chunk's length field. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* PNG_IsAncillary:                                                   */
        /*                                                                    */
        /* Whether a chunk type may be ignored by a decoder that does not     */
        /* understand it.                                                     */
        /*                                                                    */
        /* The answer is one bit: bit 5 of the type's FIRST byte, which is    */
        /* the difference between an upper-case and a lower-case ASCII        */
        /* letter. Lower case means ancillary. The specification encodes      */
        /* three more properties in the other bytes' case bits -- private,    */
        /* reserved, and safe-to-copy -- none of which a decoder needs.       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     uType  : Packed four-byte chunk type.                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool   : true when the chunk is ancillary and skippable.       */
        /*--------------------------------------------------------------------*/

        public static bool PNG_IsAncillary(uint uType)
        {
            /* Set bit means lower case means ancillary. */
            return((uType & PNG_ANCILLARY_BIT) != 0u);
        }

        /*--------------------------------------------------------------------*/
        /* PNG_TryParseIhdr:                                                  */
        /*                                                                    */
        /* Turns IHDR's thirteen bytes into a validated PngHeader.            */
        /*                                                                    */
        /* THE DEPTH AND COLOUR TYPE ARE NOT INDEPENDENT, and that is the     */
        /* validation worth doing properly. Each colour type permits only     */
        /* certain depths:                                                    */
        /*                                                                    */
        /*   0  grayscale         1, 2, 4, 8, 16                              */
        /*   2  truecolour              8, 16                                 */
        /*   3  palette          1, 2, 4, 8                                   */
        /*   4  gray + alpha           8, 16                                  */
        /*   6  truecolour+alpha       8, 16                                  */
        /*                                                                    */
        /* A 16-bit palette is meaningless because PLTE entries are 8-bit; a  */
        /* 1-bit truecolour image is meaningless because three channels       */
        /* cannot share one bit. Accepting either would put the scanline      */
        /* arithmetic into a state no later stage is written for.             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     arrIhdr  : IHDR's thirteen data bytes.                         */
        /*     hdrOut   : Receives the parsed header; null on failure.        */
        /*     sReason  : Receives "" or which field was rejected.            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool     : true when every field is valid and consistent.      */
        /*--------------------------------------------------------------------*/

        private static bool PNG_TryParseIhdr(byte[] arrIhdr, out PngHeader hdrOut, out string sReason)
        {
            PngHeader hdrNew;
            uint      uWidth;
            uint      uHeight;
            int       iBitDepth;
            int       iColorType;
            int       iCompression;
            int       iFilter;
            int       iInterlace;

            hdrOut  = null;
            sReason = "";

            uWidth = ((uint)arrIhdr[0] << 24) | ((uint)arrIhdr[1] << 16) |
                     ((uint)arrIhdr[2] << 8) | (uint)arrIhdr[3];

            uHeight = ((uint)arrIhdr[4] << 24) | ((uint)arrIhdr[5] << 16) |
                      ((uint)arrIhdr[6] << 8) | (uint)arrIhdr[7];

            iBitDepth    = (int)arrIhdr[8];
            iColorType   = (int)arrIhdr[9];
            iCompression = (int)arrIhdr[10];
            iFilter      = (int)arrIhdr[11];
            iInterlace   = (int)arrIhdr[12];

            if (uWidth == 0u || uHeight == 0u)
            {   /* The specification forbids a zero dimension outright. */
                sReason = "PNG: IHDR declares " + uWidth.ToString() + "x" + uHeight.ToString();
                return(false);
            }

            if (uWidth > (uint)ImageBuffer.IMG_MAX_DIMENSION ||
                uHeight > (uint)ImageBuffer.IMG_MAX_DIMENSION)
            {   /* Beyond what this library will allocate; treat as corruption. */
                sReason = "PNG: " + uWidth.ToString() + "x" + uHeight.ToString() +
                          " exceeds the maximum dimension";
                return(false);
            }

            if (iBitDepth != 1 && iBitDepth != 2 && iBitDepth != 4 &&
                iBitDepth != 8 && iBitDepth != 16)
            {   /* Only these five depths exist. */
                sReason = "PNG: bit depth " + iBitDepth.ToString() + " is not 1, 2, 4, 8 or 16";
                return(false);
            }

            if (iColorType != PNG_COLOR_GRAY && iColorType != PNG_COLOR_RGB &&
                iColorType != PNG_COLOR_PALETTE && iColorType != PNG_COLOR_GRAY_ALPHA &&
                iColorType != PNG_COLOR_RGBA)
            {   /* 1 and 5 are the gaps in the bit-field encoding; see the   */
                /* constants above.                                          */
                sReason = "PNG: colour type " + iColorType.ToString() + " is not 0, 2, 3, 4 or 6";
                return(false);
            }

            if (!PNG_IsDepthValidForColorType(iBitDepth, iColorType))
            {   /* A legal depth and a legal colour type that cannot go   */
                /* together; the table is in this function's header.      */
                sReason = "PNG: bit depth " + iBitDepth.ToString() +
                          " is not allowed with colour type " + iColorType.ToString();
                return(false);
            }

            if (iCompression != 0)
            {   /* Deflate is the only method the format has ever defined. */
                sReason = "PNG: compression method " + iCompression.ToString() + " is not 0";
                return(false);
            }

            if (iFilter != 0)
            {   /* Likewise the only filter method defined. Note this is the   */
                /* filter METHOD, not the per-scanline filter TYPE -- method   */
                /* 0 is what says each scanline carries one of five filter     */
                /* type bytes, which the unfilter stage implements.            */
                sReason = "PNG: filter method " + iFilter.ToString() + " is not 0";
                return(false);
            }

            if (iInterlace != PNG_INTERLACE_NONE && iInterlace != PNG_INTERLACE_ADAM7)
            {   /* Only none and Adam7 exist. */
                sReason = "PNG: interlace method " + iInterlace.ToString() + " is not 0 or 1";
                return(false);
            }

            hdrNew                       = new PngHeader();
            hdrNew.PNGH_iWidth           = (int)uWidth;
            hdrNew.PNGH_iHeight          = (int)uHeight;
            hdrNew.PNGH_iBitDepth        = iBitDepth;
            hdrNew.PNGH_iColorType       = iColorType;
            hdrNew.PNGH_iInterlace       = iInterlace;
            hdrNew.PNGH_iSamplesPerPixel = PNG_SamplesForColorType(iColorType);
            hdrNew.PNGH_bIsPalettised    = (iColorType == PNG_COLOR_PALETTE);
            hdrNew.PNGH_bHasAlpha        = (iColorType == PNG_COLOR_GRAY_ALPHA ||
                                            iColorType == PNG_COLOR_RGBA);

            hdrOut = hdrNew;

            /* Every field valid, and the derived values filled in. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* PNG_IsDepthValidForColorType:                                      */
        /*                                                                    */
        /* Whether a bit depth is permitted with a colour type. The table is  */
        /* written out in PNG_TryParseIhdr's header.                          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iBitDepth   : 1, 2, 4, 8 or 16.                                */
        /*     iColorType  : 0, 2, 3, 4 or 6.                                 */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool        : true when the combination is legal.              */
        /*--------------------------------------------------------------------*/

        private static bool PNG_IsDepthValidForColorType(int iBitDepth, int iColorType)
        {
            if (iColorType == PNG_COLOR_GRAY)
            {   /* Grayscale is the only type that permits every depth,       */
                /* because one channel can be packed as finely as you like.   */
                return(true);
            }

            if (iColorType == PNG_COLOR_PALETTE)
            {   /* Palette indices are packed sub-byte, but PLTE entries are   */
                /* 8-bit triples, so a 16-bit index has nothing to index.      */
                return(iBitDepth == 1 || iBitDepth == 2 || iBitDepth == 4 || iBitDepth == 8);
            }

            /* Truecolour, gray+alpha and truecolour+alpha all carry two or   */
            /* more channels, which cannot be packed below a byte apiece.     */
            return(iBitDepth == 8 || iBitDepth == 16);
        }

        /*--------------------------------------------------------------------*/
        /* PNG_SamplesForColorType:                                           */
        /*                                                                    */
        /* How many samples each pixel carries in the FILE, which is not the  */
        /* same as how many channels the decoded image will have.             */
        /*                                                                    */
        /* The distinction matters and is a standing source of confusion: a   */
        /* palettised image has ONE sample per pixel in the file -- an index  */
        /* -- and three or four channels after the palette is applied. This   */
        /* function answers the file question, which is what the scanline     */
        /* arithmetic needs.                                                  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iColorType  : 0, 2, 3, 4 or 6.                                 */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int         : Samples per pixel in the file: 1, 2, 3 or 4.     */
        /*--------------------------------------------------------------------*/

        public static int PNG_SamplesForColorType(int iColorType)
        {
            if (iColorType == PNG_COLOR_RGB)
            {   /* Red, green, blue. */
                return(3);
            }

            if (iColorType == PNG_COLOR_GRAY_ALPHA)
            {   /* Grey and alpha. */
                return(2);
            }

            if (iColorType == PNG_COLOR_RGBA)
            {   /* Red, green, blue, alpha. */
                return(4);
            }

            /* Grayscale and palette both store a single sample per pixel --   */
            /* a grey level in one case, an index in the other.                */
            return(1);
        }

        /*--------------------------------------------------------------------*/
        /* PNG_TypeToText:                                                    */
        /*                                                                    */
        /* Renders a packed chunk type as its four ASCII characters, for      */
        /* failure messages.                                                  */
        /*                                                                    */
        /* Non-printable bytes become a dot rather than being emitted raw, so */
        /* a corrupt type in a log line cannot carry control characters into  */
        /* whatever reads it.                                                 */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     uType  : Packed four-byte chunk type.                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     string : Four characters. Never null.                          */
        /*--------------------------------------------------------------------*/

        public static string PNG_TypeToText(uint uType)
        {
            char[] arrChars;
            int    iByte;
            int    i;

            arrChars = new char[4];

            for (i = 0; i < 4; i++)
            {   /* Most significant byte is the first character. */
                iByte = (int)((uType >> (24 - (i * 8))) & 0xFFu);

                if (iByte < 0x20 || iByte > 0x7E)
                {   /* Not printable ASCII; show a placeholder instead. */
                    arrChars[i] = '.';
                }
                else
                {   /* An ordinary printable character. */
                    arrChars[i] = (char)iByte;
                }
            }

            /* A safe four-character rendering. */
            return(new string(arrChars));
        }

        /*--------------------------------------------------------------------*/
        /* PNG_TryWalkChunks:                                                 */
        /*                                                                    */
        /* Walks a whole PNG's chunk sequence, verifying its structure        */
        /* without decoding pixels: IHDR first, IEND last, every critical     */
        /* chunk recognised, every ancillary chunk skipped.                   */
        /*                                                                    */
        /* THIS IS USEFUL ON ITS OWN -- it answers "is this file              */
        /* structurally sound" for a fraction of the cost of a decode. The    */
        /* decoder follows the same shape, reading the bodies it cares about  */
        /* instead of counting them.                                          */
        /*                                                                    */
        /* AN UNRECOGNISED CRITICAL CHUNK IS FATAL. That is the specification */
        /* being explicit that extensibility runs one way: an ancillary chunk */
        /* a decoder skips cannot change the pixels, and a critical chunk it  */
        /* does not know might.                                               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     arrData        : Buffer holding the PNG file.                  */
        /*     iOffset        : Index of the first byte of the image.         */
        /*     iLength        : Bytes available from iOffset.                 */
        /*     hdrOut         : Receives the parsed IHDR; null on failure.    */
        /*     iIdatCountOut  : Receives how many IDAT chunks were found.     */
        /*     iIdatBytesOut  : Receives their total data length.             */
        /*     sReason        : Receives "" or the first structural problem.  */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool           : true when the file is structurally sound.     */
        /*--------------------------------------------------------------------*/

        public static bool PNG_TryWalkChunks(byte[] arrData, int iOffset, int iLength,
                                             out PngHeader hdrOut, out int iIdatCountOut,
                                             out long iIdatBytesOut, out string sReason)
        {
            ByteReader brIn;
            byte[]     arrBody;
            uint       uLength;
            uint       uType;
            bool       bSeenIhdr;
            bool       bSeenIend;
            bool       bSeenIdat;

            hdrOut        = null;
            iIdatCountOut = 0;
            iIdatBytesOut = 0;
            sReason       = "";

            if (arrData == null)
            {   /* Missing argument, not malformed image data. */
                throw new ArgumentNullException("arrData");
            }

            if (!ImageDecoderDispatch.IMGDEC_TryValidateWindow(arrData, iOffset, iLength, out sReason))
            {   /* The window itself is unusable. */
                return(false);
            }

            brIn = new ByteReader(arrData, iOffset, iLength);

            if (!PNG_TryReadSignature(brIn, out sReason))
            {   /* Not a PNG at all. */
                return(false);
            }

            bSeenIhdr = false;
            bSeenIend = false;
            bSeenIdat = false;

            while (!bSeenIend)
            {   /* One chunk per iteration, until IEND or a failure. */
                if (brIn.BYTER_Remaining() < 1)
                {   /* Ran out before IEND, so the file is truncated. A PNG   */
                    /* without its end marker is incomplete by definition.    */
                    sReason = "PNG: file ended without an IEND chunk";
                    return(false);
                }

                if (!PNG_TryReadChunkHeader(brIn, out uLength, out uType, out sReason))
                {   /* Malformed chunk header; the reader says how. */
                    return(false);
                }

                if (!bSeenIhdr && uType != PNG_TYPE_IHDR)
                {   /* IHDR must come first. */
                    sReason = "PNG: first chunk is " + PNG_TypeToText(uType) + ", not IHDR";
                    return(false);
                }

                if (uType == PNG_TYPE_IHDR)
                {   /* The image header. Exactly one, and it must be first. */
                    if (bSeenIhdr)
                    {   /* A second IHDR is malformed. */
                        sReason = "PNG: more than one IHDR chunk";
                        return(false);
                    }

                    if (uLength != (uint)PNG_IHDR_DATA_SIZE)
                    {   /* Fixed length; anything else is damage. */
                        sReason = "PNG: IHDR is " + uLength.ToString() + " bytes, not 13";
                        return(false);
                    }

                    if (!PNG_TryReadChunkBody(brIn, arrData, iOffset, uLength, uType,
                                              out arrBody, out sReason))
                    {   /* Truncated or CRC mismatch. */
                        return(false);
                    }

                    if (!PNG_TryParseIhdr(arrBody, out hdrOut, out sReason))
                    {   /* A field was invalid; the parser says which. */
                        return(false);
                    }

                    bSeenIhdr = true;
                }
                else if (uType == PNG_TYPE_IEND)
                {   /* The end marker. Its data length must be zero. */
                    if (uLength != 0u)
                    {   /* IEND carries no data. */
                        sReason = "PNG: IEND carries " + uLength.ToString() + " bytes of data";
                        return(false);
                    }

                    if (!PNG_TryReadChunkBody(brIn, arrData, iOffset, uLength, uType,
                                              out arrBody, out sReason))
                    {   /* Even an empty chunk has a CRC to verify. */
                        return(false);
                    }

                    bSeenIend = true;
                }
                else if (uType == PNG_TYPE_IDAT)
                {   /* Pixel data. This walk counts and measures it rather   */
                    /* than decompressing it; the decoder inflates.          */
                    if (!PNG_TryReadChunkBody(brIn, arrData, iOffset, uLength, uType,
                                              out arrBody, out sReason))
                    {   /* Truncated or CRC mismatch. */
                        return(false);
                    }

                    iIdatCountOut = iIdatCountOut + 1;
                    iIdatBytesOut = iIdatBytesOut + (long)uLength;
                    bSeenIdat     = true;
                }
                else if (uType == PNG_TYPE_PLTE || uType == PNG_TYPE_TRNS)
                {   /* Chunks this decoder KNOWS, so their bodies are read    */
                    /* and their CRCs verified. A corrupt palette or          */
                    /* transparency table is caught rather than passed on.    */
                    /*                                                        */
                    /* THIS TEST MUST COME BEFORE THE ANCILLARY TEST BELOW,   */
                    /* and the order being wrong is a real bug this file      */
                    /* once shipped with. tRNS begins with a LOWER-CASE       */
                    /* letter, so PNG_IsAncillary says yes about it -- and    */
                    /* that is correct: the ancillary bit answers "may a      */
                    /* decoder that does NOT understand this chunk skip       */
                    /* it?", and for tRNS the answer is yes. But a decoder    */
                    /* that DOES understand it must not, and with the two     */
                    /* branches the other way round every tRNS chunk fell     */
                    /* into the skip path and was never CRC-checked.          */
                    if (!PNG_TryReadChunkBody(brIn, arrData, iOffset, uLength, uType,
                                              out arrBody, out sReason))
                    {   /* Truncated or CRC mismatch. */
                        return(false);
                    }
                }
                else if (PNG_IsAncillary(uType))
                {   /* Something optional this decoder does not use -- a      */
                    /* comment, a timestamp, a gamma value. Skip it without   */
                    /* checking its CRC; see PNG_TrySkipChunkBody.            */
                    if (!PNG_TrySkipChunkBody(brIn, uLength, uType, out sReason))
                    {   /* The chunk runs past the end of the file. */
                        return(false);
                    }
                }
                else
                {   /* A CRITICAL chunk this decoder does not recognise. The   */
                    /* specification requires refusal: it may change how the   */
                    /* pixels are to be read, and guessing would produce       */
                    /* confident wrong output.                                 */
                    sReason = "PNG: unrecognised critical chunk " + PNG_TypeToText(uType);
                    return(false);
                }
            }

            if (!bSeenIdat)
            {   /* A PNG with no pixel data at all is not a usable image. */
                sReason = "PNG: no IDAT chunk";
                return(false);
            }

            /* Signature, IHDR, at least one IDAT, IEND, and every chunk   */
            /* between them either understood or safely skipped.           */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* PNG_TryDecode:                                                     */
        /*                                                                    */
        /* Decodes a PNG into an ImageBuffer: every depth from 1 to 16, every */
        /* colour type including palette, tRNS in both of its forms, and      */
        /* Adam7 interlacing.  Nothing legal is refused, and every refusal    */
        /* this can produce is a statement about the FILE.                    */
        /*                                                                    */
        /* THE DECODED CHANNEL COUNT IS WHAT THE FILE MEANS, NOT WHAT IT      */
        /* STORES, and the two are not the same thing. A                      */
        /* palettised file stores ONE sample per pixel -- an index -- and     */
        /* means three channels, or four when tRNS gives its entries alpha. A */
        /* grayscale file with a tRNS chunk stores one sample and means two.  */
        /* PNG_DecodedChannelCount is the single place that mapping lives.    */
        /*                                                                    */
        /* Nothing beyond that is widened and nothing is dropped. A caller    */
        /* wanting a particular shape converts afterwards with                */
        /* ImageBuffer.IMG_TryConvertChannels, which exists and is tested;    */
        /* folding that into the decoder would give every codec in the        */
        /* library its own copy of the same conversion.                       */
        /*                                                                    */
        /* THE STAGES, and why they are separate functions: gather the        */
        /* chunks, decide whether the shape is in scope and legal, inflate,   */
        /* unfilter and unpack, then resolve the palette or the transparent   */
        /* colour. Each fails for a different reason, each is separately      */
        /* testable, and the unfilter stage is the one that 16-bit samples    */
        /* and interlacing extend rather than replace.                        */
        /*                                                                    */
        /* THE LAST STAGE NEEDS A STAGING BUFFER, AND ONLY SOMETIMES. When    */
        /* the file's samples ARE the finished channels -- truecolour or      */
        /* grayscale with no tRNS -- the unfilter writes straight into the    */
        /* image and there is no second pass at all. When a palette or a      */
        /* transparent colour has to be resolved, the file samples are one    */
        /* thing and the output channels are another, so they cannot share a  */
        /* buffer. Allocating the staging array only in that case keeps the   */
        /* common path free of a whole-image copy.                            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     arrData  : Buffer holding the PNG file. Must not be null.      */
        /*     iOffset  : Index of the first byte of the image.               */
        /*     iLength  : Bytes available from iOffset.                       */
        /*     imgOut   : Receives the decoded image; null on failure.        */
        /*     sReason  : Receives "" on success, or why it was refused.      */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool     : true when imgOut holds the decoded pixels.          */
        /*--------------------------------------------------------------------*/

        public static bool PNG_TryDecode(byte[] arrData, int iOffset, int iLength,
                                         out ImageBuffer imgOut, out string sReason)
        {
            PngHeader   hdrIn;
            ImageBuffer imgNew;
            byte[]      arrIdat;
            byte[]      arrPlte;
            byte[]      arrTrns;
            byte[]      arrRaw;
            byte[]      arrStage;
            ushort[]    arrStage16;
            byte[]      arrPaletteRgba;
            int[]       arrTransColour;
            long        lExpected;
            int         iPaletteCount;
            int         iChannels;
            int         iBitsPerSample;
            int         iPackedRowBytes;

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

            if (!PNG_TryGatherChunks(arrData, iOffset, iLength, out hdrIn, out arrIdat,
                                     out arrPlte, out arrTrns, out sReason))
            {   /* Bad signature, bad structure, or a chunk failed its CRC. */
                return(false);
            }

            if (!PNG_TryCheckDecodeScope(hdrIn, arrPlte, arrTrns, out sReason))
            {   /* A legal PNG whose shape this decoder does not handle, or   */
                /* an illegal chunk combination. The reason says which.       */
                return(false);
            }

            /*----------------------------------------------------------------*/
            /* PACKED SCANLINE WIDTH, which below a depth of 8 is not the     */
            /* pixel count times anything obvious.                            */
            /*                                                                */
            /* Samples are packed continuously across the row and each        */
            /* scanline restarts on a byte boundary, so the row's byte count  */
            /* is the total bit count ROUNDED UP. A 5-pixel 1-bit grayscale   */
            /* row is five bits, which is one byte with three bits of padding */
            /* -- and that padding is real, present in the file, and covered  */
            /* by the filters like any other byte. Computing this as anything */
            /* other than a round-up shears every sub-byte image whose width  */
            /* is not a multiple of the samples per byte.                     */
            /*----------------------------------------------------------------*/

            iPackedRowBytes = ((hdrIn.PNGH_iWidth * hdrIn.PNGH_iSamplesPerPixel *
                                hdrIn.PNGH_iBitDepth) + 7) / 8;

            /*-----------------------------------------------------------------*/
            /* HOW MANY BYTES THE STREAM MUST HOLD.                            */
            /*                                                                 */
            /* Each scanline is preceded by ONE filter byte, so the inflated   */
            /* stream is longer than the packed pixel data by exactly the row  */
            /* count. Computed in long: two dimensions times a channel count   */
            /* overflows an int a long way below the dimension cap that IHDR   */
            /* validation allows, and the overflow would give a small positive */
            /* expectation that every later check then passed.                 */
            /*                                                                 */
            /* AN INTERLACED FILE IS A SUM OVER ITS SEVEN PASSES, not one      */
            /* row-times-height product, because each pass has its own row     */
            /* width, its own padding and its own filter byte per row -- and   */
            /* because empty passes contribute nothing at all. Since this      */
            /* figure is the inflate CAP rather than a hint, computing         */
            /* it the non-interlaced way for an interlaced file would reject   */
            /* every such file outright.                                       */
            /*-----------------------------------------------------------------*/

            if (hdrIn.PNGH_iInterlace == PNG_INTERLACE_ADAM7)
            {   /* Seven passes, some of which may be absent. */
                lExpected = PNG_Adam7RawLength(hdrIn);
            }
            else
            {   /* One image, one row width, one filter byte per row. */
                lExpected = ((long)iPackedRowBytes + 1L) * (long)hdrIn.PNGH_iHeight;
            }

            if (lExpected > (long)int.MaxValue)
            {   /* Legal by IHDR but larger than one array can hold. */
                sReason = "PNG: " + hdrIn.PNGH_iWidth.ToString() + "x" +
                          hdrIn.PNGH_iHeight.ToString() + " at depth " +
                          hdrIn.PNGH_iBitDepth.ToString() + " needs " +
                          lExpected.ToString() + " bytes of scanline data, which will not fit";
                return(false);
            }

            /*----------------------------------------------------------------*/
            /* THE EXPECTED SIZE IS PASSED AS THE INFLATE CAP, which turns    */
            /* ZlibStreamCodec's decompression-bomb backstop into a real      */
            /* check. A PNG decoder is in the unusual position of knowing     */
            /* EXACTLY how many bytes the stream must produce -- IHDR says    */
            /* so -- and anything larger is malformed rather than merely      */
            /* suspicious.                                                    */
            /*                                                                */
            /* This IS stricter than the declared source. stb accepts a       */
            /* longer stream, having found a real file in the wild with       */
            /* trailing zeroes (its issue 276), and checks only that there is */
            /* enough data rather than exactly enough. The strict form was    */
            /* chosen here and then MEASURED rather than assumed: all eight   */
            /* reference PNGs inflate to exactly this length, so the check    */
            /* rejects no file a real encoder produced. It is the same line   */
            /* QoiCodec draws when it refuses a truncated file the reference  */
            /* would have padded -- deviate toward strictness, state it, and  */
            /* prove it costs nothing.                                        */
            /*----------------------------------------------------------------*/

            if (!ZlibStreamCodec.ZLIB_TryInflate(arrIdat, 0, arrIdat.Length, (int)lExpected,
                                                 out arrRaw, out sReason))
            {   /* Not a zlib stream, a wrong Adler trailer, or more output   */
                /* than the declared geometry can account for.                */
                sReason = "PNG: IDAT " + sReason;
                return(false);
            }

            if ((long)arrRaw.Length < lExpected)
            {   /* Inflated cleanly but produced fewer bytes than the           */
                /* geometry needs, so at least one scanline is simply absent.   */
                sReason = "PNG: IDAT inflated to " + arrRaw.Length.ToString() +
                          " bytes but " + lExpected.ToString() + " are needed for " +
                          hdrIn.PNGH_iWidth.ToString() + "x" + hdrIn.PNGH_iHeight.ToString();
                return(false);
            }

            iChannels = PNG_DecodedChannelCount(hdrIn, arrTrns != null);

            /*----------------------------------------------------------------*/
            /* WHICH OF ImageBuffer's TWO SAMPLE ARRAYS THIS IMAGE LIVES IN.  */
            /*                                                                */
            /* PNG'S 16-BIT PATH WAS THE FIRST CODE IN THIS LIBRARY to choose */
            /* the 16-bit array. The buffer type carried both arrays and both */
            /* opaque constants from the start, on the strength of a decision */
            /* made before any codec existed, and until then the second array */
            /* was allocated by tests and used by nothing.                    */
            /*----------------------------------------------------------------*/

            if (hdrIn.PNGH_iBitDepth == 16)
            {   /* Two bytes per sample on the wire, one ushort in memory. */
                iBitsPerSample = ImageBuffer.IMG_BITS_16;
            }
            else
            {   /* Every depth from 1 to 8 widens into a byte. */
                iBitsPerSample = ImageBuffer.IMG_BITS_8;
            }

            if (!ImageBuffer.IMG_TryAllocate(hdrIn.PNGH_iWidth, hdrIn.PNGH_iHeight,
                                             iChannels, iBitsPerSample,
                                             out imgNew, out sReason))
            {   /* Geometry the buffer type declines to allocate. */
                return(false);
            }

            /*----------------------------------------------------------------*/
            /* Staging, as the sub-byte path does, but in whichever width the */
            /* samples are. Exactly one of the two staging references is      */
            /* non-null, and the unfilter pass is told both so it can write   */
            /* into the right one without the caller having to split into two */
            /* loops.                                                         */
            /*----------------------------------------------------------------*/

            arrStage   = null;
            arrStage16 = null;

            if (iBitsPerSample == ImageBuffer.IMG_BITS_16)
            {   /* Sixteen-bit. A palette cannot reach here -- IHDR validation   */
                /* refuses colour type 3 at depth 16 -- so the only reason to    */
                /* stage is a tRNS transparent colour.                           */
                if (iChannels == hdrIn.PNGH_iSamplesPerPixel)
                {   /* Nothing left to resolve; write straight into the image. */
                    arrStage16 = imgNew.IMG_arrSamples16;
                }
                else
                {   /* tRNS adds a channel, so the file samples need their own   */
                    /* buffer to be compared against before being copied.        */
                    arrStage16 = new ushort[hdrIn.PNGH_iWidth * hdrIn.PNGH_iHeight *
                                            hdrIn.PNGH_iSamplesPerPixel];
                }
            }
            else if (iChannels == hdrIn.PNGH_iSamplesPerPixel)
            {   /* The file's samples ARE the finished channels, so unfilter    */
                /* straight into the image and skip the second pass entirely.   */
                arrStage = imgNew.IMG_arrSamples8;
            }
            else
            {   /* A palette or a transparent colour still has to be           */
                /* resolved, so the file samples need a buffer of their own.   */
                arrStage = new byte[hdrIn.PNGH_iWidth * hdrIn.PNGH_iHeight *
                                    hdrIn.PNGH_iSamplesPerPixel];
            }

            if (hdrIn.PNGH_iInterlace == PNG_INTERLACE_ADAM7)
            {   /* Seven sparse sub-images, each unfiltered on its own   */
                /* geometry and then scattered into place.               */
                if (!PNG_TryDeinterlace(arrRaw, hdrIn, arrStage, arrStage16, out sReason))
                {   /* A bad filter byte, or a pass that ran off the stream. */
                    return(false);
                }
            }
            else if (!PNG_TryUnfilterImage(arrRaw, 0, hdrIn, hdrIn.PNGH_iWidth,
                                           hdrIn.PNGH_iHeight, iPackedRowBytes,
                                           arrStage, arrStage16, out sReason))
            {   /* Some scanline declared a filter type that does not exist. */
                return(false);
            }

            if (hdrIn.PNGH_bIsPalettised)
            {   /* Turn indices into real colours. The palette carries alpha    */
                /* whether or not tRNS supplied any, so one expansion handles   */
                /* both the three- and four-channel cases.                      */
                arrPaletteRgba = PNG_BuildPaletteRgba(arrPlte, arrTrns, out iPaletteCount);

                if (!PNG_TryExpandPalette(arrStage, arrPaletteRgba, iPaletteCount,
                                          iChannels, imgNew.IMG_arrSamples8, out sReason))
                {   /* An index naming an entry the palette does not have. */
                    return(false);
                }
            }
            else if (arrTrns != null)
            {   /* Colour type 0 or 2 naming a single fully transparent       */
                /* colour. Every pixel matching it becomes transparent and    */
                /* every other pixel opaque, which is why the output gained   */
                /* an alpha channel the file does not store.                  */
                arrTransColour = PNG_ReadTransparentColour(hdrIn, arrTrns);

                if (iBitsPerSample == ImageBuffer.IMG_BITS_16)
                {   /* Same rule, wider samples, and an opaque alpha of   */
                    /* 0xFFFF rather than 0xFF.                           */
                    PNG_ApplyTransparentColour16(arrStage16, hdrIn.PNGH_iSamplesPerPixel,
                                                 arrTransColour, imgNew.IMG_arrSamples16);
                }
                else
                {   /* Eight bits or fewer, already widened into bytes. */
                    PNG_ApplyTransparentColour(arrStage, hdrIn.PNGH_iSamplesPerPixel,
                                               arrTransColour, imgNew.IMG_arrSamples8);
                }
            }

            imgOut = imgNew;

            /* Every scanline reconstructed, unpacked, and resolved through   */
            /* the palette or the transparent colour where one applied.       */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* PNG_TryGatherChunks:                                               */
        /*                                                                    */
        /* Walks the chunk sequence the way PNG_TryWalkChunks does, but       */
        /* KEEPS what it finds: the parsed IHDR, every IDAT body joined into  */
        /* one buffer, and whether a PLTE or tRNS chunk was present.          */
        /*                                                                    */
        /* THE IDAT BODIES ARE ONE ZLIB STREAM, NOT SEVERAL. A PNG may split  */
        /* its compressed data across any number of IDAT chunks and the split */
        /* points are arbitrary -- they can fall in the middle of a deflate   */
        /* symbol. So the pieces must be concatenated and inflated once;      */
        /* inflating them individually cannot work and would fail in a way    */
        /* that looks like data corruption rather than like a decoder bug.    */
        /*                                                                    */
        /* This is deliberately a SECOND walk rather than an extension of     */
        /* PNG_TryWalkChunks. That function answers "is this file             */
        /* structurally sound" for callers who want the question answered     */
        /* cheaply and without a decode, and it is public API and tested as   */
        /* such; growing it output parameters only a decoder wants would      */
        /* make every other caller pay for them.                              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     arrData       : Buffer holding the PNG file.                   */
        /*     iOffset       : Index of the first byte of the image.          */
        /*     iLength       : Bytes available from iOffset.                  */
        /*     hdrOut        : Receives the parsed IHDR; null on failure.     */
        /*     arrIdatOut    : Receives the joined IDAT bodies; null on       */
        /*                     failure.                                       */
        /*     arrPlteOut    : Receives the PLTE body, or null when absent.   */
        /*     arrTrnsOut    : Receives the tRNS body, or null when absent.   */
        /*     sReason       : Receives "" or the first structural problem.   */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool          : true when the file is sound and arrIdatOut     */
        /*                     holds its complete compressed stream.          */
        /*--------------------------------------------------------------------*/

        private static bool PNG_TryGatherChunks(byte[] arrData, int iOffset, int iLength,
                                                out PngHeader hdrOut, out byte[] arrIdatOut,
                                                out byte[] arrPlteOut, out byte[] arrTrnsOut,
                                                out string sReason)
        {
            ByteReader brIn;
            ByteWriter bwIdat;
            byte[]     arrBody;
            uint       uLength;
            uint       uType;
            bool       bSeenIhdr;
            bool       bSeenIend;
            bool       bSeenIdat;

            hdrOut      = null;
            arrIdatOut  = null;
            arrPlteOut  = null;
            arrTrnsOut  = null;
            sReason     = "";

            brIn = new ByteReader(arrData, iOffset, iLength);

            if (!PNG_TryReadSignature(brIn, out sReason))
            {   /* Not a PNG at all. */
                return(false);
            }

            bwIdat    = new ByteWriter(PNG_IDAT_START_CAPACITY);
            bSeenIhdr = false;
            bSeenIend = false;
            bSeenIdat = false;

            while (!bSeenIend)
            {   /* One chunk per iteration, until IEND or a failure. */
                if (brIn.BYTER_Remaining() < 1)
                {   /* Ran out before IEND; the file is incomplete. */
                    sReason = "PNG: file ended without an IEND chunk";
                    return(false);
                }

                if (!PNG_TryReadChunkHeader(brIn, out uLength, out uType, out sReason))
                {   /* Malformed chunk header; the reader says how. */
                    return(false);
                }

                if (!bSeenIhdr && uType != PNG_TYPE_IHDR)
                {   /* IHDR must come first. */
                    sReason = "PNG: first chunk is " + PNG_TypeToText(uType) + ", not IHDR";
                    return(false);
                }

                if (uType == PNG_TYPE_IHDR)
                {   /* The image header. Exactly one, and it must be first. */
                    if (bSeenIhdr)
                    {   /* A second IHDR is malformed. */
                        sReason = "PNG: more than one IHDR chunk";
                        return(false);
                    }

                    if (uLength != (uint)PNG_IHDR_DATA_SIZE)
                    {   /* Fixed length; anything else is damage. */
                        sReason = "PNG: IHDR is " + uLength.ToString() + " bytes, not 13";
                        return(false);
                    }

                    if (!PNG_TryReadChunkBody(brIn, arrData, iOffset, uLength, uType,
                                              out arrBody, out sReason))
                    {   /* Truncated or CRC mismatch. */
                        return(false);
                    }

                    if (!PNG_TryParseIhdr(arrBody, out hdrOut, out sReason))
                    {   /* A field was invalid; the parser says which. */
                        return(false);
                    }

                    bSeenIhdr = true;
                }
                else if (uType == PNG_TYPE_IEND)
                {   /* The end marker. Its data length must be zero. */
                    if (uLength != 0u)
                    {   /* IEND carries no data. */
                        sReason = "PNG: IEND carries " + uLength.ToString() + " bytes of data";
                        return(false);
                    }

                    if (!PNG_TryReadChunkBody(brIn, arrData, iOffset, uLength, uType,
                                              out arrBody, out sReason))
                    {   /* Even an empty chunk has a CRC to verify. */
                        return(false);
                    }

                    bSeenIend = true;
                }
                else if (uType == PNG_TYPE_IDAT)
                {   /* Pixel data. Append it to the accumulator; see this   */
                    /* function's header for why the pieces cannot be       */
                    /* inflated one at a time.                              */
                    if (!PNG_TryReadChunkBody(brIn, arrData, iOffset, uLength, uType,
                                              out arrBody, out sReason))
                    {   /* Truncated or CRC mismatch. */
                        return(false);
                    }

                    bwIdat.BYTEW_WriteBytes(arrBody, 0, arrBody.Length);
                    bSeenIdat = true;
                }
                else if (uType == PNG_TYPE_PLTE || uType == PNG_TYPE_TRNS)
                {   /* The palette and transparency chunks. Their bodies are   */
                    /* verified here so a corrupt palette is caught, and       */
                    /* their PRESENCE is recorded, because that alone          */
                    /* decides whether the decode may proceed at all.          */
                    /*                                                         */
                    /* THE ORDER OF THIS TEST AGAINST THE ANCILLARY ONE        */
                    /* BELOW IS LOAD-BEARING, and having it backwards was a    */
                    /* real bug caught by the tRNS refusal test. tRNS is       */
                    /* ANCILLARY by the case rule -- lower-case 't' -- so      */
                    /* with the branches the other way round it was skipped    */
                    /* before ever reaching here, bSeenTrnsOut stayed false,   */
                    /* and a truecolour image carrying a transparent colour    */
                    /* decoded as fully opaque with no complaint. Exactly      */
                    /* the confident-wrong-output failure the scope check      */
                    /* exists to prevent, arriving through the back door.      */
                    if (!PNG_TryReadChunkBody(brIn, arrData, iOffset, uLength, uType,
                                              out arrBody, out sReason))
                    {   /* Truncated or CRC mismatch. */
                        return(false);
                    }

                    if (uType == PNG_TYPE_PLTE)
                    {   /* A palette, whether or not the colour type uses one. */
                        if (arrPlteOut != null)
                        {   /* The specification allows at most one PLTE, and   */
                            /* two would leave which one wins undefined.        */
                            sReason = "PNG: more than one PLTE chunk";
                            return(false);
                        }

                        arrPlteOut = arrBody;
                    }
                    else
                    {   /* tRNS, whose two placement rules both matter. */
                        if (arrTrnsOut != null)
                        {   /* At most one, for the same reason as PLTE. */
                            sReason = "PNG: more than one tRNS chunk";
                            return(false);
                        }

                        if (bSeenIdat)
                        {   /* tRNS must precede the pixel data. A decoder     */
                            /* that streams IDAT as it arrives would already   */
                            /* have committed the pixels before learning       */
                            /* which colour was meant to be transparent, so    */
                            /* the specification puts the chunk first and a    */
                            /* file that does not is malformed.                */
                            sReason = "PNG: tRNS appears after IDAT";
                            return(false);
                        }

                        if (hdrOut.PNGH_bIsPalettised && arrPlteOut == null)
                        {   /* For a palettised image tRNS lists one alpha     */
                            /* per PALETTE ENTRY, so it cannot be read -- or   */
                            /* even length-checked -- before the palette it    */
                            /* annotates has been seen. The specification      */
                            /* requires that order and this enforces it,       */
                            /* rather than deferring and guessing later.       */
                            sReason = "PNG: tRNS appears before PLTE on a palettised image";
                            return(false);
                        }

                        arrTrnsOut = arrBody;
                    }
                }
                else if (PNG_IsAncillary(uType))
                {   /* Something optional this decoder does not use -- a      */
                    /* comment, a timestamp, a gamma value. Skipped without   */
                    /* checking its CRC; see PNG_TrySkipChunkBody.            */
                    if (!PNG_TrySkipChunkBody(brIn, uLength, uType, out sReason))
                    {   /* The chunk runs past the end of the file. */
                        return(false);
                    }
                }
                else
                {   /* A CRITICAL chunk this decoder does not recognise. The   */
                    /* specification requires refusal: it may change how the   */
                    /* pixels are to be read.                                  */
                    sReason = "PNG: unrecognised critical chunk " + PNG_TypeToText(uType);
                    return(false);
                }
            }

            if (!bSeenIdat)
            {   /* No compressed data at all, so there is nothing to decode. */
                sReason = "PNG: no IDAT chunk";
                return(false);
            }

            arrIdatOut = bwIdat.BYTEW_ToArray();

            /* A sound container, with its whole compressed stream in hand. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* PNG_TryCheckDecodeScope:                                           */
        /*                                                                    */
        /* Decides whether an image's shape is one the decoder handles, and   */
        /* separates "legal PNG, not built yet" from "not a legal PNG".       */
        /*                                                                    */
        /* THE TWO OUTCOMES READ DIFFERENTLY ON PURPOSE. A reason naming an   */
        /* unfinished capability tells a caller the file is fine and the      */
        /* library is not; a reason naming a rule ("a tRNS chunk is not       */
        /* allowed with colour type 6") tells them the file is wrong.         */
        /* Collapsing the two into one message would send somebody looking    */
        /* in the wrong place.                                                */
        /*                                                                    */
        /* THERE ARE NO UNFINISHED-CAPABILITY REFUSALS LEFT HERE. Every check */
        /* that remains says the FILE is wrong, which is the half of the      */
        /* split this function was shaped around and the half that is         */
        /* permanent.                                                         */
        /*                                                                    */
        /* The function keeps its name and its shape rather than being        */
        /* renamed to something like PNG_TryCheckLegal, because a later       */
        /* format extension might add a refusal of the other kind, and the    */
        /* two-outcome distinction is worth keeping ready.                    */
        /*                                                                    */
        /* THE LEGALITY RULES, and what each one is actually about:           */
        /*                                                                    */
        /*   tRNS with colour type 4 or 6   Those carry a real alpha sample   */
        /*                                  per pixel already, so a second,   */
        /*                                  contradictory transparency        */
        /*                                  statement has no defined meaning. */
        /*   PLTE with colour type 0 or 4   Grayscale has no indices, so      */
        /*                                  there is nothing to look up.      */
        /*   PLTE with colour type 2 or 6   LEGAL, and ignored. It is a       */
        /*                                  suggested palette for a display   */
        /*                                  that cannot manage truecolour and */
        /*                                  says nothing about the pixels.    */
        /*   No PLTE with colour type 3     Every sample is an index into a   */
        /*                                  table that is not there.          */
        /*                                                                    */
        /* THE LENGTH RULES ARE CHECKED HERE TOO, before anything is decoded, */
        /* because a tRNS of the wrong length is not a transparency the       */
        /* decoder can partly honour -- it means the file disagrees with      */
        /* itself about its own colour type, and going on would apply         */
        /* whichever interpretation the code happened to be written for.      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     hdrIn    : The parsed IHDR.                                    */
        /*     arrPlte  : The PLTE body, or null when absent.                 */
        /*     arrTrns  : The tRNS body, or null when absent.                 */
        /*     sReason  : Receives "" when in scope, else why not.            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool     : true when the decoder can proceed.                  */
        /*--------------------------------------------------------------------*/

        private static bool PNG_TryCheckDecodeScope(PngHeader hdrIn, byte[] arrPlte,
                                                    byte[] arrTrns, out string sReason)
        {
            int iPaletteCount;

            sReason = "";

            /*----------------------------------------------------------------*/
            /* PLTE rules.                                                    */
            /*----------------------------------------------------------------*/

            if (arrPlte != null && (hdrIn.PNGH_iColorType == PNG_COLOR_GRAY ||
                                    hdrIn.PNGH_iColorType == PNG_COLOR_GRAY_ALPHA))
            {   /* Grayscale has no indices, so a palette cannot mean          */
                /* anything; the specification forbids the pairing outright.   */
                sReason = "PNG: a PLTE chunk is not allowed with grayscale colour type " +
                          hdrIn.PNGH_iColorType.ToString();
                return(false);
            }

            if (hdrIn.PNGH_bIsPalettised && arrPlte == null)
            {   /* Colour type 3 stores nothing but indices, so without the   */
                /* table there is no image at all -- not a degraded one.      */
                sReason = "PNG: colour type 3 has no PLTE chunk to index";
                return(false);
            }

            if (arrPlte != null)
            {   /* Validate the palette's own shape whether or not this      */
                /* colour type will use it, because a malformed chunk is a   */
                /* malformed file either way.                                */
                if ((arrPlte.Length % PNG_PALETTE_ENTRY_BYTES) != 0 || arrPlte.Length == 0)
                {   /* Entries are exactly three bytes and there is at least   */
                    /* one, so any other length means the chunk is damaged     */
                    /* or was written by something confused.                   */
                    sReason = "PNG: PLTE is " + arrPlte.Length.ToString() +
                              " bytes, which is not a positive multiple of 3";
                    return(false);
                }

                iPaletteCount = arrPlte.Length / PNG_PALETTE_ENTRY_BYTES;

                if (iPaletteCount > PNG_PALETTE_MAX_ENTRIES)
                {   /* An index is at most 8 bits, so entries past 256 can   */
                    /* never be named by anything.                           */
                    sReason = "PNG: PLTE declares " + iPaletteCount.ToString() +
                              " entries, more than the 256 an index can reach";
                    return(false);
                }
            }
            else
            {   /* No palette, so nothing to size the tRNS against below. */
                iPaletteCount = 0;
            }

            /*----------------------------------------------------------------*/
            /* tRNS rules. Its length is determined by the colour type, and   */
            /* the three cases are genuinely different chunks wearing one     */
            /* name.                                                          */
            /*----------------------------------------------------------------*/

            if (arrTrns != null && hdrIn.PNGH_bHasAlpha)
            {   /* Colour types 4 and 6 already carry alpha per pixel. */
                sReason = "PNG: a tRNS chunk is not allowed with colour type " +
                          hdrIn.PNGH_iColorType.ToString() + ", which already carries alpha";
                return(false);
            }

            if (arrTrns != null && hdrIn.PNGH_bIsPalettised &&
                arrTrns.Length > iPaletteCount)
            {   /* One alpha per palette entry, and no more. FEWER is legal    */
                /* and common -- entries the chunk does not reach stay fully   */
                /* opaque -- which is why this is a maximum rather than an     */
                /* equality.                                                   */
                sReason = "PNG: tRNS gives " + arrTrns.Length.ToString() +
                          " alphas for a palette of " + iPaletteCount.ToString() + " entries";
                return(false);
            }

            if (arrTrns != null && !hdrIn.PNGH_bIsPalettised &&
                arrTrns.Length != (hdrIn.PNGH_iSamplesPerPixel * 2))
            {   /* One 16-BIT value per channel, so two bytes for grayscale   */
                /* and six for truecolour -- and the width is 16 bits even    */
                /* when the image's samples are narrower, which is the part   */
                /* that surprises people. Any other length means the file     */
                /* disagrees with its own colour type.                        */
                sReason = "PNG: tRNS is " + arrTrns.Length.ToString() + " bytes, but colour type " +
                          hdrIn.PNGH_iColorType.ToString() + " requires " +
                          (hdrIn.PNGH_iSamplesPerPixel * 2).ToString();
                return(false);
            }

            /* A non-interlaced image at a depth of 8 or below, with a         */
            /* palette if it needs one and a well-formed tRNS if it has one.   */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* PNG_TryUnfilterImage:                                              */
        /*                                                                    */
        /* Reverses the per-scanline filters and unpacks sub-byte samples,    */
        /* turning the inflated stream into one byte per FILE SAMPLE.         */
        /*                                                                    */
        /* Transcoded from stb_image.h v2.30 (nothings/stb, commit 2c980bb5), */
        /* stbi__create_png_image_raw, taking its depth-1-to-8 paths where    */
        /* the output channel count equals the file's. The alternating        */
        /* two-row workspace, the split of each filter into a first-pixel     */
        /* loop and a rest-of-row loop, and the Paeth formulation are all     */
        /* kept as they are there, so this function can be diffed against its */
        /* source.                                                            */
        /*                                                                    */
        /* THE FILTER STEP IS ONE BYTE BELOW A DEPTH OF 8, NOT ONE PIXEL, and */
        /* that single line is what makes sub-byte images work. The filters   */
        /* predict from "the pixel to the left", but when several samples     */
        /* share a byte that pixel is a fraction of a byte away and there is  */
        /* nothing to index. The specification's answer is that the step is   */
        /* the pixel size in bytes ROUNDED UP TO ONE, so below depth 8 every  */
        /* filter works byte by byte over the packed row and knows nothing    */
        /* about where the sample boundaries fall. Unpacking happens after,   */
        /* never before -- filtering the unpacked bytes would give different  */
        /* and wrong answers.                                                 */
        /*                                                                    */
        /* THE PREVIOUS ROW IS THE RECONSTRUCTED ONE, NOT THE FILTERED ONE.   */
        /* That is the single detail this whole function turns on. Every      */
        /* prediction refers to bytes as they will be AFTER decoding, both    */
        /* above and to the left, so the workspace has to hold the finished   */
        /* previous scanline while the current one is still being built. Two  */
        /* buffers, used alternately by row parity: row j writes into half    */
        /* (j & 1) and reads half (~j & 1), which is where row j-1 landed.    */
        /*                                                                    */
        /* WHY THERE IS NO SPECIAL FIRST-ROW HANDLING, and why that is not a  */
        /* deviation. Row 0 has no row above it, so every prediction must     */
        /* treat the bytes above as zero. stb cannot simply do that: its      */
        /* workspace comes from malloc and holds whatever was there before,   */
        /* so it carries a first_row_filter table that rewrites the five      */
        /* filters into equivalents that never read the row above -- Up       */
        /* becomes None, Paeth becomes Sub, Average becomes a synthetic sixth */
        /* filter. In C# the array arrives zero-filled, so the row above      */
        /* genuinely IS all zeroes and the ordinary five filters already      */
        /* produce the right answer. The equivalences stb's table encodes are */
        /* the proof: with b = 0, Up gives raw + 0 = None; with b = c = 0,    */
        /* Paeth returns a, which is Sub; with b = 0, Average's (a + 0) >> 1  */
        /* is stb's avg_first. Same output, one fewer table, and no synthetic */
        /* filter constant that does not exist in the specification.          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     arrRaw           : The inflated stream: for each scanline, one */
        /*                        filter byte then iPackedRowBytes filtered   */
        /*                        bytes. Must hold at least that much; the    */
        /*                        caller checks.                              */
        /*     iRawOffset       : Where in arrRaw this image's first filter   */
        /*                        byte sits. Zero for a whole image; for an   */
        /*                        Adam7 pass, wherever the preceding passes   */
        /*                        ended.                                      */
        /*     hdrIn            : The parsed IHDR, for the depth, the colour  */
        /*                        type and the samples per pixel. NOT for the */
        /*                        geometry -- see iWidth and iHeight.         */
        /*     iWidth           : Width IN THIS CALL, which is the image's    */
        /*                        width for a whole image and the pass's own  */
        /*                        width for an Adam7 pass.                    */
        /*     iHeight          : Likewise the row count for this call.       */
        /*     iPackedRowBytes  : Bytes per scanline in the FILE, which is    */
        /*                        the row's bit count rounded up. Computed by */
        /*                        the caller, which needs it anyway to size   */
        /*                        the inflate cap.                            */
        /*     arrDest8Io       : Receives one byte per FILE SAMPLE at every  */
        /*                        depth from 1 to 8, row major with no        */
        /*                        padding -- grey levels or palette indices   */
        /*                        or colour channels, not necessarily         */
        /*                        finished pixels. Null at depth 16.          */
        /*     arrDest16Io      : The same, one ushort per sample, at depth   */
        /*                        16. Null at every other depth. Exactly one  */
        /*                        of the two destinations is non-null.        */
        /*     sReason          : Receives "" or which scanline was bad.      */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool             : true when every scanline was reconstructed. */
        /*--------------------------------------------------------------------*/

        private static bool PNG_TryUnfilterImage(byte[] arrRaw, int iRawOffset, PngHeader hdrIn,
                                                 int iWidth, int iHeight, int iPackedRowBytes,
                                                 byte[] arrDest8Io, ushort[] arrDest16Io,
                                                 out string sReason)
        {
            byte[] arrWork;
            int    iFilterBytes;
            int    iSamplesPerRow;
            int    iCur;
            int    iPrior;
            int    iDest;
            int    iRawPos;
            int    iFilter;
            int    j;
            int    k;

            sReason = "";

            iSamplesPerRow = iWidth * hdrIn.PNGH_iSamplesPerPixel;

            if (hdrIn.PNGH_iBitDepth < 8)
            {   /* Several samples share a byte, so the pixel to the left is    */
                /* not a whole number of bytes away; the specification rounds   */
                /* the step up to one byte and the filters then work over       */
                /* packed bytes without knowing where samples begin.            */
                iFilterBytes = 1;
            }
            else if (hdrIn.PNGH_iBitDepth == 16)
            {   /* TWO bytes per sample, so a pixel is twice its sample       */
                /* count. The filters themselves are unchanged -- they work   */
                /* on BYTES and always have, and a 16-bit sample is simply    */
                /* two neighbouring bytes that get filtered independently     */
                /* against the two bytes one pixel to the left. Only the      */
                /* STEP changes, so nothing below this line depends on the    */
                /* sample depth at all.                                       */
                iFilterBytes = hdrIn.PNGH_iSamplesPerPixel * 2;
            }
            else
            {   /* One byte per sample, so a pixel is its sample count. */
                iFilterBytes = hdrIn.PNGH_iSamplesPerPixel;
            }

            /* Two scanlines of workspace, zero-filled, which is what makes   */
            /* row 0's "row above" correct without a special case.            */
            arrWork = new byte[iPackedRowBytes * 2];
            iRawPos = iRawOffset;

            for (j = 0; j < iHeight; j++)
            {   /* One scanline per iteration. The halves swap roles every   */
                /* row so the finished previous row is always readable.      */
                iCur   = (j & 1) * iPackedRowBytes;
                iPrior = ((~j) & 1) * iPackedRowBytes;
                iDest  = j * iSamplesPerRow;

                iFilter = (int)arrRaw[iRawPos];
                iRawPos = iRawPos + 1;

                if (iFilter > PNG_FILTER_PAETH)
                {   /* Only five filter types are defined, and a sixth value   */
                    /* means the stream is not what IHDR said it was. Refuse   */
                    /* rather than guessing which filter was meant.            */
                    sReason = "PNG: scanline " + j.ToString() + " declares filter type " +
                              iFilter.ToString() + ", which is not 0 to 4";
                    return(false);
                }

                if (iFilter == PNG_FILTER_NONE)
                {   /* No prediction at all: the bytes are already the          */
                    /* samples, so this is a straight copy into the             */
                    /* workspace, which the next row may need as its "above".   */
                    Array.Copy(arrRaw, iRawPos, arrWork, iCur, iPackedRowBytes);
                }
                else if (iFilter == PNG_FILTER_SUB)
                {   /* Predict from the pixel to the left. The first pixel of   */
                    /* the row has nothing to its left, so its bytes stand as   */
                    /* they are -- that is the first-pixel copy, and it is      */
                    /* why the second loop starts at iFilterBytes.              */
                    Array.Copy(arrRaw, iRawPos, arrWork, iCur, iFilterBytes);

                    for (k = iFilterBytes; k < iPackedRowBytes; k++)
                    {   /* Add back the already-reconstructed byte one whole   */
                        /* pixel to the left, within this same row.            */
                        arrWork[iCur + k] = (byte)((arrRaw[iRawPos + k] +
                            arrWork[iCur + k - iFilterBytes]) & 0xFF);
                    }
                }
                else if (iFilter == PNG_FILTER_UP)
                {   /* Predict from the byte directly above. No first-pixel     */
                    /* special case: every byte in the row has one above it,    */
                    /* and on row 0 that is the zeroed half of the workspace.   */
                    for (k = 0; k < iPackedRowBytes; k++)
                    {   /* Add back the finished byte from the previous row. */
                        arrWork[iCur + k] = (byte)((arrRaw[iRawPos + k] +
                            arrWork[iPrior + k]) & 0xFF);
                    }
                }
                else if (iFilter == PNG_FILTER_AVERAGE)
                {   /* Predict from the mean of left and above, rounded DOWN.   */
                    /* The floor matters: the encoder used the same floor, so   */
                    /* rounding differently here would drift by a unit per      */
                    /* byte and produce an image that looks almost right.       */
                    for (k = 0; k < iFilterBytes; k++)
                    {   /* First pixel: nothing to the left, so the mean is   */
                        /* of the above byte and zero.                        */
                        arrWork[iCur + k] = (byte)((arrRaw[iRawPos + k] +
                            (arrWork[iPrior + k] >> 1)) & 0xFF);
                    }

                    for (k = iFilterBytes; k < iPackedRowBytes; k++)
                    {   /* Both neighbours present. The sum is taken in int     */
                        /* before the shift: two bytes can sum past 255 and a   */
                        /* byte-width add would wrap before the halving.        */
                        arrWork[iCur + k] = (byte)((arrRaw[iRawPos + k] +
                            (((int)arrWork[iPrior + k] +
                              (int)arrWork[iCur + k - iFilterBytes]) >> 1)) & 0xFF);
                    }
                }
                else
                {   /* Paeth: predict with whichever of left, above and      */
                    /* above-left is closest to left + above - above-left.   */
                    for (k = 0; k < iFilterBytes; k++)
                    {   /* First pixel: left and above-left are both zero,   */
                        /* and Paeth(0, b, 0) is b, so this reduces to Up.   */
                        arrWork[iCur + k] = (byte)((arrRaw[iRawPos + k] +
                            arrWork[iPrior + k]) & 0xFF);
                    }

                    for (k = iFilterBytes; k < iPackedRowBytes; k++)
                    {   /* All three neighbours present. */
                        arrWork[iCur + k] = (byte)((arrRaw[iRawPos + k] +
                            PNG_PaethPredictor((int)arrWork[iCur + k - iFilterBytes],
                                               (int)arrWork[iPrior + k],
                                               (int)arrWork[iPrior + k - iFilterBytes])) & 0xFF);
                    }
                }

                /* Step past this scanline's filtered bytes. The filter byte   */
                /* was consumed separately above.                              */
                iRawPos = iRawPos + iPackedRowBytes;

                if (hdrIn.PNGH_iBitDepth == 8)
                {   /* One byte per sample already, so the reconstructed row   */
                    /* IS the output row and nothing needs unpacking.          */
                    Array.Copy(arrWork, iCur, arrDest8Io, iDest, iSamplesPerRow);
                }
                else if (hdrIn.PNGH_iBitDepth == 16)
                {   /* Two bytes per sample, big-endian on the wire, joined   */
                    /* into native ushorts here.                              */
                    PNG_CombineWideRow(arrWork, iCur, arrDest16Io, iDest, iSamplesPerRow);
                }
                else
                {   /* Samples narrower than a byte, packed several to a   */
                    /* byte, most significant first.                       */
                    PNG_ExpandPackedRow(arrWork, iCur, arrDest8Io, iDest, iSamplesPerRow,
                                        hdrIn.PNGH_iBitDepth, hdrIn.PNGH_iColorType);
                }
            }

            /* Every scanline reconstructed, and unpacked where it needed it. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* PNG_Adam7PassWidth:                                                */
        /*                                                                    */
        /* How many pixels across one interlace pass is, for a given image    */
        /* width.                                                             */
        /*                                                                    */
        /* The arithmetic is a ceiling divide of the pixels the pass's grid   */
        /* reaches. It can legitimately come out ZERO -- pass 1 starts at     */
        /* column 4, so an image three pixels wide has no pass-1 columns at   */
        /* all -- and a zero here means the pass is absent from the stream    */
        /* entirely rather than present and empty.                            */
        /*                                                                    */
        /* THE SUBTRACTION CANNOT GO NEGATIVE, which is worth knowing because */
        /* C# integer division truncates toward zero and would give the wrong */
        /* answer if it could. The largest origin is 4 and it pairs with a    */
        /* spacing of 8, so the smallest the numerator reaches is             */
        /* 1 - 4 + 8 - 1, which is 4. Every other origin-spacing pairing is   */
        /* further from zero still.                                           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iWidth  : The full image width in pixels, at least 1.          */
        /*     iPass   : 0 to 6.                                              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int     : Pixels across in this pass; 0 when the pass is       */
        /*               absent.                                              */
        /*--------------------------------------------------------------------*/

        private static int PNG_Adam7PassWidth(int iWidth, int iPass)
        {
            /* Ceiling divide over the columns this pass's grid lands on. */
            return((iWidth - PNG_arrAdam7Xorig[iPass] + PNG_arrAdam7Xspacing[iPass] - 1) /
                   PNG_arrAdam7Xspacing[iPass]);
        }

        /*--------------------------------------------------------------------*/
        /* PNG_Adam7PassHeight:                                               */
        /*                                                                    */
        /* How many rows one interlace pass has, for a given image height.    */
        /* The same ceiling divide as PNG_Adam7PassWidth, on the other axis,  */
        /* and it can be zero for the same reason.                            */
        /*                                                                    */
        /* Written as its own function rather than folded into a shared       */
        /* helper taking four table references, because two three-line        */
        /* functions read better at the call site than one that needs its     */
        /* arguments decoded.                                                 */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iHeight  : The full image height in pixels, at least 1.        */
        /*     iPass    : 0 to 6.                                             */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int      : Rows in this pass; 0 when the pass is absent.       */
        /*--------------------------------------------------------------------*/

        private static int PNG_Adam7PassHeight(int iHeight, int iPass)
        {
            /* Ceiling divide over the rows this pass's grid lands on. */
            return((iHeight - PNG_arrAdam7Yorig[iPass] + PNG_arrAdam7Yspacing[iPass] - 1) /
                   PNG_arrAdam7Yspacing[iPass]);
        }

        /*--------------------------------------------------------------------*/
        /* PNG_Adam7RawLength:                                                */
        /*                                                                    */
        /* How many bytes an interlaced image's inflated stream must hold:    */
        /* the sum over the seven passes of each one's packed rows plus its   */
        /* filter bytes.                                                      */
        /*                                                                    */
        /* EMPTY PASSES CONTRIBUTE NOTHING, not even the one filter byte a    */
        /* zero-width row might seem to need. Counting them would make this   */
        /* figure too large, and since the decoder passes it to               */
        /* ZlibStreamCodec as the inflate CAP rather than as a hint, too      */
        /* large means a real file is refused for being too short.            */
        /*                                                                    */
        /* Returns long for the reason PNG_TryDecode computes its own figure  */
        /* in long: the products involved overflow an int well below the      */
        /* dimension cap IHDR validation allows.                              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     hdrIn  : The parsed IHDR.                                      */
        /*                                                                    */
        /* Returns:                                                           */
        /*     long   : Total bytes across all seven passes.                  */
        /*--------------------------------------------------------------------*/

        private static long PNG_Adam7RawLength(PngHeader hdrIn)
        {
            long lTotal;
            long lPassRowBytes;
            int  iPassWidth;
            int  iPassHeight;
            int  p;

            lTotal = 0L;

            for (p = 0; p < PNG_ADAM7_PASSES; p++)
            {   /* One pass per iteration. */
                iPassWidth  = PNG_Adam7PassWidth(hdrIn.PNGH_iWidth, p);
                iPassHeight = PNG_Adam7PassHeight(hdrIn.PNGH_iHeight, p);

                if (iPassWidth <= 0 || iPassHeight <= 0)
                {   /* Absent from the stream; contributes not one byte. */
                    continue;
                }

                /* The pass's own packed row width, padded to a byte on its   */
                /* own account rather than on the image's.                    */
                lPassRowBytes = (((long)iPassWidth * (long)hdrIn.PNGH_iSamplesPerPixel *
                                  (long)hdrIn.PNGH_iBitDepth) + 7L) / 8L;

                lTotal = lTotal + ((lPassRowBytes + 1L) * (long)iPassHeight);
            }

            /* What the whole seven-pass stream must inflate to, exactly. */
            return(lTotal);
        }

        /*--------------------------------------------------------------------*/
        /* PNG_TryDeinterlace:                                                */
        /*                                                                    */
        /* Decodes the seven Adam7 passes and scatters them into one dense    */
        /* image.                                                             */
        /*                                                                    */
        /* Transcoded from stb_image.h v2.30 (nothings/stb, commit 2c980bb5), */
        /* the de-interlacing half of stbi__create_png_image.                 */
        /*                                                                    */
        /* THE UNFILTERING IS NOT REIMPLEMENTED HERE. Each pass is a small    */
        /* complete image in its own right -- its own width, its own padding, */
        /* its own filter byte per row -- so it goes through exactly the same */
        /* PNG_TryUnfilterImage every non-interlaced file uses, told the      */
        /* pass's geometry instead of the image's. That is why sub-byte       */
        /* packing and 16-bit joining need no interlace-specific work at all: */
        /* both already happen inside that function and are therefore         */
        /* per-pass automatically.                                            */
        /*                                                                    */
        /* THE SCATTER IS SEPARATE FROM THE UNFILTER, DELIBERATELY. It would  */
        /* be possible to write each reconstructed row straight into its      */
        /* scattered positions and skip the pass buffer. It would also mean   */
        /* the filter loop knowing about interlacing, which is precisely the  */
        /* coupling that makes interlaced decoders hard to read -- and the    */
        /* filters must see the pass's rows CONTIGUOUSLY anyway, because each */
        /* row predicts from the reconstructed row above it IN THE PASS, not  */
        /* in the finished image.                                             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     arrRaw       : The whole inflated stream, all passes.          */
        /*     hdrIn        : The parsed IHDR.                                */
        /*     arrDest8Io   : Receives one byte per file sample at depths 1   */
        /*                    to 8; null at depth 16.                         */
        /*     arrDest16Io  : Receives one ushort per file sample at depth    */
        /*                    16; null otherwise. Exactly one is non-null.    */
        /*     sReason      : Receives "" or what went wrong, naming the      */
        /*                    pass.                                           */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool         : true when all seven passes decoded and landed.  */
        /*--------------------------------------------------------------------*/

        private static bool PNG_TryDeinterlace(byte[] arrRaw, PngHeader hdrIn,
                                               byte[] arrDest8Io, ushort[] arrDest16Io,
                                               out string sReason)
        {
            byte[]   arrPass8;
            ushort[] arrPass16;
            int      iSamples;
            int      iPassWidth;
            int      iPassHeight;
            int      iPassRowBytes;
            int      iRawPos;
            int      iOutX;
            int      iOutY;
            int      iSrcBase;
            int      iDstBase;
            int      p;
            int      i;
            int      j;
            int      k;

            sReason  = "";
            iSamples = hdrIn.PNGH_iSamplesPerPixel;
            iRawPos  = 0;

            for (p = 0; p < PNG_ADAM7_PASSES; p++)
            {   /* One pass per iteration, in stream order. */
                iPassWidth  = PNG_Adam7PassWidth(hdrIn.PNGH_iWidth, p);
                iPassHeight = PNG_Adam7PassHeight(hdrIn.PNGH_iHeight, p);

                if (iPassWidth <= 0 || iPassHeight <= 0)
                {   /* Absent from the stream. Skipping WITHOUT advancing      */
                    /* iRawPos is the whole of the correctness here: consume   */
                    /* so much as a filter byte for a pass that was never      */
                    /* written and every later pass reads shifted data.        */
                    continue;
                }

                /* The pass's own packed row width. Padding is per pass, not   */
                /* per image, which is what makes sub-byte interlacing         */
                /* different from sub-byte anything else.                      */
                iPassRowBytes = ((iPassWidth * iSamples * hdrIn.PNGH_iBitDepth) + 7) / 8;

                arrPass8  = null;
                arrPass16 = null;

                if (hdrIn.PNGH_iBitDepth == 16)
                {   /* Sixteen-bit samples land in the wide buffer. */
                    arrPass16 = new ushort[iPassWidth * iPassHeight * iSamples];
                }
                else
                {   /* Everything from 1 to 8 bits widens into bytes. */
                    arrPass8 = new byte[iPassWidth * iPassHeight * iSamples];
                }

                if (!PNG_TryUnfilterImage(arrRaw, iRawPos, hdrIn, iPassWidth, iPassHeight,
                                          iPassRowBytes, arrPass8, arrPass16, out sReason))
                {   /* Name the pass: "scanline 3" means something different   */
                    /* in each of seven sub-images, and without the pass       */
                    /* number the message sends the reader to the wrong row.   */
                    sReason = "PNG: interlace pass " + p.ToString() + ": " + sReason;
                    return(false);
                }

                /*------------------------------------------------------------*/
                /* Scatter this pass's dense pixels to the positions its grid */
                /* names in the finished image.                               */
                /*------------------------------------------------------------*/

                for (j = 0; j < iPassHeight; j++)
                {   /* One pass row per iteration. */
                    iOutY = (j * PNG_arrAdam7Yspacing[p]) + PNG_arrAdam7Yorig[p];

                    for (i = 0; i < iPassWidth; i++)
                    {   /* One pass pixel per iteration. */
                        iOutX = (i * PNG_arrAdam7Xspacing[p]) + PNG_arrAdam7Xorig[p];

                        iSrcBase = ((j * iPassWidth) + i) * iSamples;
                        iDstBase = ((iOutY * hdrIn.PNGH_iWidth) + iOutX) * iSamples;

                        for (k = 0; k < iSamples; k++)
                        {   /* Whole pixels move, one sample at a time. */
                            if (arrPass16 != null)
                            {   /* Sixteen-bit. */
                                arrDest16Io[iDstBase + k] = arrPass16[iSrcBase + k];
                            }
                            else
                            {   /* Eight bits or fewer, already widened. */
                                arrDest8Io[iDstBase + k] = arrPass8[iSrcBase + k];
                            }
                        }
                    }
                }

                /* Step over exactly what this pass occupied: its rows and   */
                /* their filter bytes, and nothing else.                     */
                iRawPos = iRawPos + ((iPassRowBytes + 1) * iPassHeight);
            }

            /* Seven passes read in order and every pixel placed. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* PNG_CombineWideRow:                                                */
        /*                                                                    */
        /* Joins one reconstructed scanline of 16-bit samples from pairs of   */
        /* big-endian bytes into native ushorts.                              */
        /*                                                                    */
        /* Transcoded from the depth-16 branch of stb_image.h's               */
        /* stbi__create_png_image_raw, which does the same shift-and-or.      */
        /*                                                                    */
        /* THE BYTE ORDER IS THE FILE'S, NOT THE MACHINE'S. PNG is big-endian */
        /* throughout, so the high byte comes first, and the join has to be   */
        /* written out rather than done by reinterpreting memory. A cast of   */
        /* the byte array to ushorts would give the right answer on a         */
        /* big-endian machine and a byte-swapped image on every machine this  */
        /* library actually runs on -- and the swap is invisible on grey      */
        /* ramps and glaring on photographs, which is the worst way round for */
        /* noticing it.                                                       */
        /*                                                                    */
        /* NO SCALING HAPPENS HERE. A 16-bit sample already fills the range   */
        /* the ushort array carries, so unlike the sub-byte path there is     */
        /* nothing to widen -- which is also why the depth-scale table has no */
        /* entry at index 16 and must not be consulted for this depth.        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     arrWork       : The workspace holding the reconstructed row.   */
        /*     iWorkOffset   : Index of that row's first byte in arrWork.     */
        /*     arrDestIo     : Receives the joined samples.                   */
        /*     iDestOffset   : Index of the row's first sample in arrDestIo.  */
        /*     iSampleCount  : Samples in the row: width times samples per    */
        /*                     pixel. The row occupies twice this many bytes. */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void          : The row is written into arrDestIo.             */
        /*--------------------------------------------------------------------*/

        private static void PNG_CombineWideRow(byte[] arrWork, int iWorkOffset,
                                               ushort[] arrDestIo, int iDestOffset,
                                               int iSampleCount)
        {
            int iIn;
            int i;

            iIn = iWorkOffset;

            for (i = 0; i < iSampleCount; i++)
            {   /* High byte first, low byte second, as PNG stores every   */
                /* multi-byte value.                                       */
                arrDestIo[iDestOffset + i] = (ushort)((((int)arrWork[iIn]) << 8) |
                                                       ((int)arrWork[iIn + 1]));
                iIn = iIn + 2;
            }
        }

        /*--------------------------------------------------------------------*/
        /* PNG_ExpandPackedRow:                                               */
        /*                                                                    */
        /* Unpacks one reconstructed scanline of sub-byte samples into one    */
        /* byte per sample, scaling grayscale levels to fill 0 to 255 and     */
        /* leaving palette indices alone.                                     */
        /*                                                                    */
        /* Transcoded from stb_image.h's depth-4, depth-2 and depth-1         */
        /* branches inside stbi__create_png_image_raw, keeping its            */
        /* shift-and-consume shape: take a byte when the sample index says a  */
        /* new one is due, use its top bits, then shift the byte left so the  */
        /* next sample is on top again.                                       */
        /*                                                                    */
        /* SAMPLES ARE PACKED MOST SIGNIFICANT FIRST, which is why the value  */
        /* is taken from the TOP of the byte and the byte is shifted LEFT.    */
        /* The leftmost pixel of a row lives in the high bits of the first    */
        /* byte, not the low ones, and a decoder that read from the bottom    */
        /* would return every group of pixels in reverse order -- an image    */
        /* that looks almost plausible, which is the worst kind of wrong.     */
        /*                                                                    */
        /* THE TRAILING BITS OF THE LAST BYTE ARE SIMPLY NOT VISITED. A row   */
        /* of five 1-bit pixels occupies one byte with three bits of padding; */
        /* the loop runs to the sample count, not to the byte count, so the   */
        /* padding is read into inb and then shifted away unused. It still    */
        /* had to be filtered, which is why the packed width and not the      */
        /* sample count governs everything above this point.                  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     arrWork      : The workspace holding the reconstructed row.    */
        /*     iWorkOffset  : Index of that row's first byte in arrWork.      */
        /*     arrDestIo    : Receives the unpacked samples.                  */
        /*     iDestOffset  : Index of the row's first sample in arrDestIo.   */
        /*     iSampleCount : Samples in the row: width times samples per     */
        /*                    pixel.                                          */
        /*     iBitDepth    : 1, 2 or 4. Depth 8 never reaches here.          */
        /*     iColorType   : Decides whether the values are light levels to  */
        /*                    be scaled or palette indices to be left alone.  */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void         : The row is written into arrDestIo.              */
        /*--------------------------------------------------------------------*/

        private static void PNG_ExpandPackedRow(byte[] arrWork, int iWorkOffset,
                                                byte[] arrDestIo, int iDestOffset,
                                                int iSampleCount, int iBitDepth,
                                                int iColorType)
        {
            int iScale;
            int iIn;
            int iByte;
            int i;

            if (iColorType == PNG_COLOR_PALETTE)
            {   /* An index names an entry; scaling it would look up an         */
                /* entirely different colour, so it is carried through as-is.   */
                iScale = 1;
            }
            else
            {   /* A grayscale level, widened by bit replication so that the   */
                /* depth's maximum becomes 255 exactly.                        */
                iScale = (int)PNG_arrDepthScale[iBitDepth];
            }

            iIn    = iWorkOffset;
            iByte  = 0;

            if (iBitDepth == 4)
            {   /* Two samples per byte. */
                for (i = 0; i < iSampleCount; i++)
                {   /* Take a fresh byte on every even sample. */
                    if ((i & 1) == 0)
                    {   /* Start of a new packed byte. */
                        iByte = (int)arrWork[iIn];
                        iIn   = iIn + 1;
                    }

                    arrDestIo[iDestOffset + i] = (byte)((iScale * (iByte >> 4)) & 0xFF);
                    iByte = (iByte << 4) & 0xFF;
                }
            }
            else if (iBitDepth == 2)
            {   /* Four samples per byte. */
                for (i = 0; i < iSampleCount; i++)
                {   /* Take a fresh byte every fourth sample. */
                    if ((i & 3) == 0)
                    {   /* Start of a new packed byte. */
                        iByte = (int)arrWork[iIn];
                        iIn   = iIn + 1;
                    }

                    arrDestIo[iDestOffset + i] = (byte)((iScale * (iByte >> 6)) & 0xFF);
                    iByte = (iByte << 2) & 0xFF;
                }
            }
            else
            {   /* Depth 1: eight samples per byte. The caller never sends   */
                /* any other depth here, depth 8 having been handled by a    */
                /* straight copy and 16 refused long before.                 */
                for (i = 0; i < iSampleCount; i++)
                {   /* Take a fresh byte every eighth sample. */
                    if ((i & 7) == 0)
                    {   /* Start of a new packed byte. */
                        iByte = (int)arrWork[iIn];
                        iIn   = iIn + 1;
                    }

                    arrDestIo[iDestOffset + i] = (byte)((iScale * (iByte >> 7)) & 0xFF);
                    iByte = (iByte << 1) & 0xFF;
                }
            }
        }

        /*--------------------------------------------------------------------*/
        /* PNG_PaethPredictor:                                                */
        /*                                                                    */
        /* The Paeth prediction: of the three neighbouring bytes, returns     */
        /* whichever is closest to the initial estimate a + b - c.            */
        /*                                                                    */
        /* Transcoded from stb_image.h's stbi__paeth, and it deliberately     */
        /* keeps stb's formulation rather than the specification's. THE TWO   */
        /* LOOK NOTHING ALIKE AND ARE THE SAME FUNCTION. The specification    */
        /* computes p = a + b - c, then the three absolute distances          */
        /* |p - a|, |p - b| and |p - c|, and picks the smallest with ties     */
        /* going to a then b. stb rearranges that into two comparisons        */
        /* against a threshold, which has better data dependencies and admits */
        /* branch-free code.                                                  */
        /*                                                                    */
        /* Keeping stb's version follows this library's transcribe-rather-    */
        /* than-paraphrase rule, so a misbehaving function can be diffed      */
        /* against its source.  The risk that carries is that a clever        */
        /* rearrangement is exactly the kind of thing that is wrong in one    */
        /* corner and right everywhere else, so testing answers it directly:  */
        /* the forward filter it compares against implements the              */
        /* SPECIFICATION'S form instead, and every Paeth round trip therefore */
        /* compares two independent formulations against each other rather    */
        /* than comparing this one to itself.                                 */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iA  : The byte one pixel to the left, already reconstructed.   */
        /*     iB  : The byte directly above, already reconstructed.          */
        /*     iC  : The byte above and one pixel to the left.                */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int : Whichever of iA, iB or iC the rule selects, 0 to 255.    */
        /*--------------------------------------------------------------------*/

        private static int PNG_PaethPredictor(int iA, int iB, int iC)
        {
            int iThresh;
            int iLo;
            int iHi;
            int iT0;
            int iT1;

            iThresh = (iC * 3) - (iA + iB);

            if (iA < iB)
            {   /* a is the smaller of the two outer neighbours. */
                iLo = iA;
                iHi = iB;
            }
            else
            {   /* b is the smaller, or they are equal. */
                iLo = iB;
                iHi = iA;
            }

            if (iHi <= iThresh)
            {   /* The threshold is above both, so the far neighbour wins. */
                iT0 = iLo;
            }
            else
            {   /* Otherwise the diagonal is the candidate at this stage. */
                iT0 = iC;
            }

            if (iThresh <= iLo)
            {   /* The threshold is below both, so the near neighbour wins. */
                iT1 = iHi;
            }
            else
            {   /* Neither extreme applies; keep what the first test chose. */
                iT1 = iT0;
            }

            /* The selected neighbour, which the caller adds to the stored   */
            /* residual to recover the true sample.                          */
            return(iT1);
        }

        /*--------------------------------------------------------------------*/
        /* PNG_DecodedChannelCount:                                           */
        /*                                                                    */
        /* How many channels the DECODED image has, which is not always the   */
        /* same as how many samples the file stores per pixel.                */
        /*                                                                    */
        /* The two diverge in exactly two ways and this is the one place that */
        /* knows about either. A palettised file stores one sample -- an      */
        /* index -- and means three channels, or four when tRNS has given the */
        /* palette entries alpha. A grayscale or truecolour file with a tRNS  */
        /* chunk stores what it always stored and means one channel more,     */
        /* because naming a transparent colour is the same as carrying an     */
        /* alpha channel that happens to be either 0 or 255 everywhere.       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     hdrIn      : The parsed IHDR.                                  */
        /*     bHasTrns   : Whether a tRNS chunk was present.                 */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int        : Channels in the decoded buffer: 1, 2, 3 or 4.     */
        /*--------------------------------------------------------------------*/

        private static int PNG_DecodedChannelCount(PngHeader hdrIn, bool bHasTrns)
        {
            if (hdrIn.PNGH_bIsPalettised)
            {   /* Indices resolve to colours; whether alpha comes with them   */
                /* is the only question.                                       */
                if (bHasTrns)
                {   /* tRNS gave at least one entry a non-opaque alpha. */
                    return(ImageBuffer.IMG_CHANNELS_RGBA);
                }

                /* No tRNS, so every entry is opaque and alpha would carry no   */
                /* information at all.                                          */
                return(ImageBuffer.IMG_CHANNELS_RGB);
            }

            if (bHasTrns)
            {   /* Colour type 0 or 2 -- types 4 and 6 cannot reach here,     */
                /* the scope check having refused tRNS alongside them -- so   */
                /* the file gains exactly one alpha channel.                  */
                return(hdrIn.PNGH_iSamplesPerPixel + 1);
            }

            /* Everything else: what the file stores is what it means. */
            return(hdrIn.PNGH_iSamplesPerPixel);
        }

        /*--------------------------------------------------------------------*/
        /* PNG_BuildPaletteRgba:                                              */
        /*                                                                    */
        /* Turns a PLTE chunk, and a tRNS chunk when there is one, into one   */
        /* four-byte-per-entry table the expansion can read without caring    */
        /* whether the image has transparency.                                */
        /*                                                                    */
        /* ENTRIES tRNS DOES NOT REACH ARE OPAQUE, and that is a rule rather  */
        /* than a convenience. A tRNS chunk for a palette may be SHORTER than */
        /* the palette -- an image whose first three entries are transparent  */
        /* and whose remaining two hundred are not carries three bytes, not   */
        /* two hundred and three -- and every entry past the end is fully     */
        /* opaque by definition. Filling alpha with 255 first and overwriting */
        /* only what the chunk covers is how that falls out with no special   */
        /* case, and it is what the declared source does.                     */
        /*                                                                    */
        /* Not a Try function because it cannot fail: every length rule this  */
        /* depends on was checked by PNG_TryCheckDecodeScope before the       */
        /* decode began, and a function that always returns true would only   */
        /* invite a caller to write a branch that never runs.                 */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     arrPlte         : The PLTE body, a positive multiple of three  */
        /*                       bytes long. Must not be null.                */
        /*     arrTrns         : The tRNS body, no longer than the entry      */
        /*                       count, or null when there is none.           */
        /*     iEntryCountOut  : Receives how many entries the table holds.   */
        /*                                                                    */
        /* Returns:                                                           */
        /*     byte[]          : Red, green, blue, alpha per entry.           */
        /*--------------------------------------------------------------------*/

        private static byte[] PNG_BuildPaletteRgba(byte[] arrPlte, byte[] arrTrns,
                                                   out int iEntryCountOut)
        {
            byte[] arrPalette;
            int    iEntries;
            int    i;

            iEntries   = arrPlte.Length / PNG_PALETTE_ENTRY_BYTES;
            arrPalette = new byte[iEntries * PNG_RGBA_ENTRY_BYTES];

            for (i = 0; i < iEntries; i++)
            {   /* Three bytes in, four bytes out, alpha fully opaque until   */
                /* and unless tRNS says otherwise below.                      */
                arrPalette[(i * PNG_RGBA_ENTRY_BYTES)]     = arrPlte[(i * PNG_PALETTE_ENTRY_BYTES)];
                arrPalette[(i * PNG_RGBA_ENTRY_BYTES) + 1] = arrPlte[(i * PNG_PALETTE_ENTRY_BYTES) + 1];
                arrPalette[(i * PNG_RGBA_ENTRY_BYTES) + 2] = arrPlte[(i * PNG_PALETTE_ENTRY_BYTES) + 2];
                arrPalette[(i * PNG_RGBA_ENTRY_BYTES) + 3] = 255;
            }

            if (arrTrns != null)
            {   /* One alpha per entry, for as many entries as the chunk       */
                /* covers. Its length was checked against the entry count by   */
                /* the scope check, so this cannot run off the end.            */
                for (i = 0; i < arrTrns.Length; i++)
                {   /* Replace the opaque default for this entry. */
                    arrPalette[(i * PNG_RGBA_ENTRY_BYTES) + 3] = arrTrns[i];
                }
            }

            iEntryCountOut = iEntries;

            /* A table the expansion can index without further checks, other   */
            /* than that the index is in range.                                */
            return(arrPalette);
        }

        /*--------------------------------------------------------------------*/
        /* PNG_TryExpandPalette:                                              */
        /*                                                                    */
        /* Replaces every palette index with the colour it names.             */
        /*                                                                    */
        /* Transcoded from stb_image.h's stbi__expand_png_palette, with one   */
        /* deliberate difference: AN INDEX BEYOND THE PALETTE IS REFUSED.     */
        /*                                                                    */
        /* stb cannot refuse it. Its palette is a 1024-byte array on the      */
        /* stack, always big enough for any 8-bit index, so an out-of-range   */
        /* index reads whatever happened to be in that memory and produces a  */
        /* plausible-looking image built partly from stack contents. That is  */
        /* a reasonable trade for a library whose job is to load anything,    */
        /* and it is the wrong one here: the index is out of range because    */
        /* the FILE is wrong, and this library's contract is to say so rather */
        /* than to invent pixels. The specification is explicit that indices  */
        /* must be less than the palette length, so no correct encoder can    */
        /* produce a file this rejects.                                       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     arrIndices      : One index per pixel, row major.              */
        /*     arrPaletteRgba  : Four bytes per entry from                    */
        /*                       PNG_BuildPaletteRgba.                        */
        /*     iEntryCount     : How many entries that table holds.           */
        /*     iChannels       : 3 or 4, from PNG_DecodedChannelCount.        */
        /*     arrDestIo       : Receives iChannels bytes per pixel.          */
        /*     sReason         : Receives "" or which pixel was out of range. */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool            : true when every index named a real entry.    */
        /*--------------------------------------------------------------------*/

        private static bool PNG_TryExpandPalette(byte[] arrIndices, byte[] arrPaletteRgba,
                                                 int iEntryCount, int iChannels,
                                                 byte[] arrDestIo, out string sReason)
        {
            int iIndex;
            int iSrc;
            int iDst;
            int i;

            sReason = "";
            iDst    = 0;

            for (i = 0; i < arrIndices.Length; i++)
            {   /* One pixel per iteration; a palettised file stores exactly   */
                /* one sample per pixel, so the index array's length IS the    */
                /* pixel count.                                                */
                iIndex = (int)arrIndices[i];

                if (iIndex >= iEntryCount)
                {   /* The file names an entry its own palette does not have. */
                    sReason = "PNG: pixel " + i.ToString() + " uses palette index " +
                              iIndex.ToString() + " but PLTE holds only " +
                              iEntryCount.ToString() + " entries";
                    return(false);
                }

                iSrc = iIndex * PNG_RGBA_ENTRY_BYTES;

                arrDestIo[iDst]     = arrPaletteRgba[iSrc];
                arrDestIo[iDst + 1] = arrPaletteRgba[iSrc + 1];
                arrDestIo[iDst + 2] = arrPaletteRgba[iSrc + 2];

                if (iChannels == ImageBuffer.IMG_CHANNELS_RGBA)
                {   /* tRNS gave the palette alpha, so carry it through. */
                    arrDestIo[iDst + 3] = arrPaletteRgba[iSrc + 3];
                }

                iDst = iDst + iChannels;
            }

            /* Every index resolved to a real entry. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* PNG_ReadTransparentColour:                                         */
        /*                                                                    */
        /* Reads the single fully-transparent colour a tRNS chunk names for   */
        /* colour type 0 or 2, scaled into the same range the image's samples */
        /* were scaled into.                                                  */
        /*                                                                    */
        /* EVERY VALUE IS STORED AS SIXTEEN BITS, WHATEVER THE IMAGE'S DEPTH, */
        /* which is the part of this chunk that catches people out. A 4-bit   */
        /* grayscale image still writes its transparent level as a two-byte   */
        /* big-endian field, with the level in the low byte.                  */
        /*                                                                    */
        /* AT DEPTH 16 THE VALUE IS USED WHOLE AND UNSCALED, which is what    */
        /* the second branch below is for. Below 16 the samples were widened  */
        /* on unpacking, so the transparent level has to be widened by the    */
        /* SAME replication before it can be compared -- a 4-bit level of 5   */
        /* has to become 5 times 0x11 here exactly as it did there. At depth  */
        /* 16 no widening happened, both sides are already sixteen bits, and  */
        /* scaling would be wrong twice over: the multiplier table does not   */
        /* even have an entry at index 16, and the low-byte mask that suits   */
        /* the narrow depths would throw away half the value.                 */
        /*                                                                    */
        /* Getting this wrong in either direction is SILENT: the transparent  */
        /* colour simply matches nothing, the image comes back fully opaque,  */
        /* and no error is raised anywhere.                                   */
        /*                                                                    */
        /* Not a Try function because it cannot fail: the chunk's length was  */
        /* checked against the colour type before the decode began.           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     hdrIn    : The parsed IHDR.                                    */
        /*     arrTrns  : The tRNS body, two bytes per channel.               */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int[]    : One value per channel, in the widened sample range. */
        /*--------------------------------------------------------------------*/

        private static int[] PNG_ReadTransparentColour(PngHeader hdrIn, byte[] arrTrns)
        {
            int[] arrColour;
            int   iSamples;
            int   iScale;
            int   iValue;
            int   k;

            iSamples  = hdrIn.PNGH_iSamplesPerPixel;
            arrColour = new int[iSamples];

            if (hdrIn.PNGH_iBitDepth == 16)
            {   /* Whole sixteen-bit values, compared against samples that   */
                /* were never widened. No mask and no scale.                 */
                for (k = 0; k < iSamples; k++)
                {   /* Big-endian on the wire, as everywhere in PNG. */
                    arrColour[k] = ((int)arrTrns[k * 2] << 8) | (int)arrTrns[(k * 2) + 1];
                }

                /* The colour to compare unpacked samples against. */
                return(arrColour);
            }

            iScale = (int)PNG_arrDepthScale[hdrIn.PNGH_iBitDepth];

            for (k = 0; k < iSamples; k++)
            {   /* Sixteen bits big-endian per channel, of which only the low   */
                /* byte can carry a value at these depths.                      */
                iValue = ((int)arrTrns[k * 2] << 8) | (int)arrTrns[(k * 2) + 1];
                iValue = iValue & 0xFF;

                arrColour[k] = (iValue * iScale) & 0xFF;
            }

            /* The colour to compare unpacked samples against. */
            return(arrColour);
        }

        /*--------------------------------------------------------------------*/
        /* PNG_ApplyTransparentColour:                                        */
        /*                                                                    */
        /* Copies the file's samples into the output and appends an alpha     */
        /* channel that is 0 where the pixel is exactly the transparent       */
        /* colour and 255 everywhere else.                                    */
        /*                                                                    */
        /* Transcoded from stb_image.h's stbi__compute_transparency, which    */
        /* does the same test in place on an image already widened with an    */
        /* opaque alpha; here the copy and the test happen in one pass        */
        /* because the widening has not been done yet.                        */
        /*                                                                    */
        /* THE ALPHA IS BINARY -- 0 OR 255, NEVER ANYTHING BETWEEN. tRNS on a */
        /* non-palettised image names one colour and says it is invisible; it */
        /* has no way to express partial transparency, and a decoder that     */
        /* produced a soft edge here would be inventing information. The      */
        /* match must be exact across EVERY channel, which is why one         */
        /* differing channel is enough to make the pixel opaque.              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     arrSamples        : The unpacked file samples, row major.      */
        /*     iSamplesPerPixel  : 1 for grayscale, 3 for truecolour.         */
        /*     arrColour         : The transparent colour, one value per      */
        /*                         channel, already scaled.                   */
        /*     arrDestIo         : Receives iSamplesPerPixel + 1 bytes per    */
        /*                         pixel.                                     */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void              : The image is written into arrDestIo.       */
        /*--------------------------------------------------------------------*/

        private static void PNG_ApplyTransparentColour(byte[] arrSamples, int iSamplesPerPixel,
                                                       int[] arrColour, byte[] arrDestIo)
        {
            bool bMatches;
            int  iPixels;
            int  iSrc;
            int  iDst;
            int  i;
            int  k;

            iPixels = arrSamples.Length / iSamplesPerPixel;
            iSrc    = 0;
            iDst    = 0;

            for (i = 0; i < iPixels; i++)
            {   /* One pixel per iteration. */
                bMatches = true;

                for (k = 0; k < iSamplesPerPixel; k++)
                {   /* Copy the channel out and test it in the same pass, so   */
                    /* the samples are read once rather than twice.            */
                    arrDestIo[iDst + k] = arrSamples[iSrc + k];

                    if ((int)arrSamples[iSrc + k] != arrColour[k])
                    {   /* One differing channel is enough to make the pixel   */
                        /* opaque; the match has to be exact.                  */
                        bMatches = false;
                    }
                }

                if (bMatches)
                {   /* Exactly the colour tRNS named: fully transparent. */
                    arrDestIo[iDst + iSamplesPerPixel] = 0;
                }
                else
                {   /* Any other colour: fully opaque. */
                    arrDestIo[iDst + iSamplesPerPixel] = 255;
                }

                iSrc = iSrc + iSamplesPerPixel;
                iDst = iDst + iSamplesPerPixel + 1;
            }
        }

        /*--------------------------------------------------------------------*/
        /* PNG_ApplyTransparentColour16:                                      */
        /*                                                                    */
        /* The 16-bit form of PNG_ApplyTransparentColour: copies the file's   */
        /* samples into the output and appends an alpha channel that is 0     */
        /* where the pixel is exactly the transparent colour and 0xFFFF       */
        /* everywhere else.                                                   */
        /*                                                                    */
        /* Transcoded from stb_image.h's stbi__compute_transparency16.        */
        /*                                                                    */
        /* A SEPARATE FUNCTION RATHER THAN A WIDTH ARGUMENT, and deliberately */
        /* so. The two differ in the array they write, the type they compare  */
        /* and the value that means opaque, which is essentially all of them; */
        /* a single function taking both arrays would be two function bodies  */
        /* sharing a brace and a loop counter. Keeping them apart also keeps  */
        /* each one's indexing readable, which is where this kind of code     */
        /* goes wrong.                                                        */
        /*                                                                    */
        /* THE OPAQUE VALUE IS 0xFFFF, NOT 0xFF. Writing 255 into a 16-bit    */
        /* alpha channel produces a pixel that is very nearly invisible       */
        /* rather than opaque -- an error that looks like a decode failure    */
        /* of the COLOUR channels when it is nothing of the kind. That is     */
        /* what ImageBuffer.IMG_OPAQUE_16 exists for.                         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     arrSamples        : The unpacked file samples, row major.      */
        /*     iSamplesPerPixel  : 1 for grayscale, 3 for truecolour.         */
        /*     arrColour         : The transparent colour, one whole 16-bit   */
        /*                         value per channel, unscaled.               */
        /*     arrDestIo         : Receives iSamplesPerPixel + 1 samples per  */
        /*                         pixel.                                     */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void              : The image is written into arrDestIo.       */
        /*--------------------------------------------------------------------*/

        private static void PNG_ApplyTransparentColour16(ushort[] arrSamples,
                                                         int iSamplesPerPixel,
                                                         int[] arrColour, ushort[] arrDestIo)
        {
            bool bMatches;
            int  iPixels;
            int  iSrc;
            int  iDst;
            int  i;
            int  k;

            iPixels = arrSamples.Length / iSamplesPerPixel;
            iSrc    = 0;
            iDst    = 0;

            for (i = 0; i < iPixels; i++)
            {   /* One pixel per iteration. */
                bMatches = true;

                for (k = 0; k < iSamplesPerPixel; k++)
                {   /* Copy the channel out and test it in the same pass, so   */
                    /* the samples are read once rather than twice.            */
                    arrDestIo[iDst + k] = arrSamples[iSrc + k];

                    if ((int)arrSamples[iSrc + k] != arrColour[k])
                    {   /* One differing channel is enough to make the pixel   */
                        /* opaque; the match has to be exact.                  */
                        bMatches = false;
                    }
                }

                if (bMatches)
                {   /* Exactly the colour tRNS named: fully transparent. */
                    arrDestIo[iDst + iSamplesPerPixel] = 0;
                }
                else
                {   /* Any other colour: fully opaque, at SIXTEEN bits. */
                    arrDestIo[iDst + iSamplesPerPixel] = (ushort)ImageBuffer.IMG_OPAQUE_16;
                }

                iSrc = iSrc + iSamplesPerPixel;
                iDst = iDst + iSamplesPerPixel + 1;
            }
        }

        /*--------------------------------------------------------------------*/
        /*                                                                    */
        /*  ENCODE                                                            */
        /*                                                                    */
        /*  Everything above this line reads a PNG. Everything below writes   */
        /*  one, and the lineage changes here: the decoder is transcoded from */
        /*  stb_image.h, the encoder from lodepng. The reason is in this      */
        /*  file's header -- in short, stb_image_write cannot express a       */
        /*  palette or a depth choice, so it could never have written every   */
        /*  shape this decoder reads.                                         */
        /*                                                                    */
        /*  WHAT IS DELIBERATELY NOT SHARED WITH THE DECODER: the five        */
        /*  forward filters. They are near-mirrors of the reconstruction in   */
        /*  PNG_TryUnfilterImage and it is tempting to derive one from the    */
        /*  other, but an independently written forward filter is what makes  */
        /*  every decode test meaningful, and a codec whose                   */
        /*  encoder and decoder shared a predictor would round-trip perfectly */
        /*  while agreeing on a wrong prediction. They stay separate.         */
        /*                                                                    */
        /*--------------------------------------------------------------------*/

        /*--------------------------------------------------------------------*/
        /* PNG_DefaultEncodeOptions:                                          */
        /*                                                                    */
        /* The options a caller gets by passing null: deflate at the          */
        /* conventional default effort, no interlacing, and depth and colour  */
        /* type matched to whatever buffer is handed in.                      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     PngEncodeOptions : A fresh instance, never shared, so a caller */
        /*                        may modify it without affecting anyone      */
        /*                        else's defaults.                            */
        /*--------------------------------------------------------------------*/

        public static PngEncodeOptions PNG_DefaultEncodeOptions()
        {
            PngEncodeOptions optNew;

            optNew = new PngEncodeOptions();

            optNew.PNGOPT_iCompressionLevel = PNG_DEFAULT_DEFLATE_LEVEL;
            optNew.PNGOPT_bInterlace        = false;
            optNew.PNGOPT_iBitDepth         = 0;
            optNew.PNGOPT_iColorType        = 0;

            /* Defaults that produce a plain, widely readable file. */
            return(optNew);
        }

        /*--------------------------------------------------------------------*/
        /* PNG_TryEncode:                                                     */
        /*                                                                    */
        /* Writes an ImageBuffer as a PNG: any depth from 1 to 16, any of the */
        /* five colour types, interlaced or not, with a filter chosen per     */
        /* scanline.                                                          */
        /*                                                                    */
        /* THIS WRITES EVERY SHAPE PNG_TryDecode CAN READ, so no refusal      */
        /* below defers to unfinished work. Every remaining refusal is a      */
        /* statement about the REQUEST -- a lossy narrowing, an illegal       */
        /* depth-and-colour-type pairing, or more colours than a palette can  */
        /* name -- and none of them becomes acceptable later.                 */
        /*                                                                    */
        /* THE OUTPUT SHAPE IS A FUNCTION OF THE INPUT SHAPE, NEVER OF THE    */
        /* PIXEL VALUES, and that is a decision rather than an accident.      */
        /* A four-channel buffer whose alpha happens to be 255 everywhere     */
        /* could be written as colour type 2, losing nothing and saving a     */
        /* quarter of the file -- the encoder that produced this project's    */
        /* own reference images does exactly that, which is why edgecase.qoi  */
        /* declares four channels and its PNG twin is truecolour (11.7).      */
        /* This encoder does NOT, for three reasons: a caller can predict     */
        /* what comes out without knowing what is in the pixels; encoding     */
        /* and decoding round-trip to the same channel count, which is the    */
        /* property tests and callers both expect; and the saving is          */
        /* available to anyone who wants it by converting the buffer down     */
        /* first, which says so at the call site. It is the same line the     */
        /* decoder draws when it hands back the file's own channel count      */
        /* rather than a convenient one.                                      */
        /*                                                                    */
        /* A LOSSY conversion is refused rather than performed -- asking for  */
        /* fewer channels than the buffer has, or for a colour type that      */
        /* would drop its alpha, names IMG_TryConvertChannels instead.        */
        /* Lossless WIDENING is done without asking, matching what BmpCodec   */
        /* does and what the buffer type's own converter is for.              */
        /*                                                                    */
        /* PALETTISED OUTPUT IS BUILT IN A DIFFERENT ORDER FROM EVERYTHING    */
        /* ELSE and the ordering is forced rather than chosen. For every      */
        /* other colour type the depth can be settled from the request and    */
        /* the buffer alone; for a palette the depth depends on how many      */
        /* DISTINCT COLOURS the image turns out to have, which is not known   */
        /* until the palette has been extracted. So the palette branch        */
        /* extracts first, resolves the depth second, and hands the filter    */
        /* stage a one-sample-per-pixel index image rather than the caller's  */
        /* buffer.                                                            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     imgIn    : The image to write. Must not be null.               */
        /*     optIn    : Encode options, or null for the defaults.           */
        /*     arrOut   : Receives the PNG file; null on failure.             */
        /*     sReason  : Receives "" on success, or why it was refused.      */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool     : true when arrOut holds a complete PNG.              */
        /*--------------------------------------------------------------------*/

        public static bool PNG_TryEncode(ImageBuffer imgIn, PngEncodeOptions optIn,
                                         out byte[] arrOut, out string sReason)
        {
            PngEncodeOptions optUse;
            ImageBuffer      imgUse;
            ImageBuffer      imgData;
            ByteWriter       bwOut;
            byte[]           arrFiltered;
            byte[]           arrZlib;
            byte[]           arrIhdr;
            byte[]           arrPaletteRgba;
            byte[]           arrIndices;
            byte[]           arrPlte;
            byte[]           arrTrns;
            int              iColorType;
            int              iWantChannels;
            int              iBitDepth;
            int              iEntryCount;
            int              iInterlace;
            int              iCapacity;

            arrOut  = null;
            sReason = "";

            if (imgIn == null)
            {   /* Missing argument, not bad data. */
                throw new ArgumentNullException("imgIn");
            }

            if (!ImageBuffer.IMG_TryValidate(imgIn, out sReason))
            {   /* A malformed buffer; the validator says which field. */
                sReason = "PNG encode: " + sReason;
                return(false);
            }

            if (optIn == null)
            {   /* Null options mean the defaults, everywhere in this library. */
                optUse = PNG_DefaultEncodeOptions();
            }
            else
            {   /* The caller's own settings. */
                optUse = optIn;
            }

            if (!PNG_TryResolveColorType(imgIn, optUse, out iColorType, out iWantChannels,
                                         out sReason))
            {   /* An impossible or lossy request; the resolver says which. */
                return(false);
            }

            if (iWantChannels == imgIn.IMG_iChannels)
            {   /* The buffer is already the shape being written. */
                imgUse = imgIn;
            }
            else if (!ImageBuffer.IMG_TryConvertChannels(imgIn, iWantChannels, out imgUse,
                                                         out sReason))
            {   /* A lossless widening the converter nonetheless declined. */
                sReason = "PNG encode: " + sReason;
                return(false);
            }

            arrPaletteRgba = null;
            arrPlte        = null;
            arrTrns        = null;

            if (iColorType == PNG_COLOR_PALETTE)
            {   /* The palette order described in this function's header:     */
                /* extract the colours, size the index from how many there    */
                /* are, then filter an index image rather than the caller's   */
                /* pixels.                                                    */
                if (!PNG_TryBuildPalette(imgUse, out arrPaletteRgba, out iEntryCount,
                                         out arrIndices, out sReason))
                {   /* More colours than a palette can name, and this library   */
                    /* does not quantise on the caller's behalf.                */
                    return(false);
                }

                if (!PNG_TryResolvePaletteDepth(optUse, iEntryCount, out iBitDepth,
                                                out sReason))
                {   /* A depth that is illegal for a palette, or too narrow   */
                    /* to number the entries this image needs.                */
                    return(false);
                }

                if (!PNG_TryMakeIndexImage(imgUse.IMG_iWidth, imgUse.IMG_iHeight, arrIndices,
                                           out imgData, out sReason))
                {   /* The allocator declined a geometry it had already         */
                    /* accepted for the source, which cannot normally happen.   */
                    sReason = "PNG encode: " + sReason;
                    return(false);
                }

                arrPlte = PNG_BuildPlteData(arrPaletteRgba, iEntryCount);

                if (imgUse.IMG_iChannels == ImageBuffer.IMG_CHANNELS_RGBA)
                {   /* THE INPUT SHAPE DECIDES, NOT THE PIXEL VALUES, which     */
                    /* is the same rule the colour-type resolver follows. A     */
                    /* buffer that carries alpha gets a tRNS chunk even when    */
                    /* every entry turns out opaque, so that decoding the       */
                    /* file returns the four channels that went in. Omitting    */
                    /* it in the all-opaque case would make the output depend   */
                    /* on the pixels and would silently change a round trip's   */
                    /* channel count.                                           */
                    arrTrns = PNG_BuildPaletteTrnsData(arrPaletteRgba, iEntryCount);
                }
            }
            else
            {   /* Every other colour type: the depth is settled from the     */
                /* request and the buffer, and the caller's pixels are what   */
                /* gets filtered.                                             */
                if (!PNG_TryResolveBitDepth(imgIn, optUse, iColorType, out iBitDepth,
                                            out sReason))
                {   /* A depth that is illegal for the colour type or lossy   */
                    /* for the buffer.                                        */
                    return(false);
                }

                imgData = imgUse;
            }

            if (optUse.PNGOPT_bInterlace)
            {   /* Seven passes, each a complete little image of its own. */
                iInterlace = PNG_INTERLACE_ADAM7;

                if (!PNG_TryBuildInterlacedStream(imgData, iBitDepth, iColorType,
                                                  out arrFiltered, out sReason))
                {   /* A pass could not be gathered. */
                    return(false);
                }
            }
            else
            {   /* One filter byte plus one packed row per scanline. */
                iInterlace  = PNG_INTERLACE_NONE;
                arrFiltered = PNG_BuildFilteredStream(imgData, iBitDepth, iColorType);
            }

            if (!ZlibStreamCodec.ZLIB_TryDeflate(arrFiltered, 0, arrFiltered.Length,
                                                 optUse.PNGOPT_iCompressionLevel,
                                                 out arrZlib, out sReason))
            {   /* The compressor failed; report rather than raise. */
                sReason = "PNG encode: " + sReason;
                return(false);
            }

            /*----------------------------------------------------------------*/
            /* Assemble. The chunk order here is the minimum a valid PNG      */
            /* needs and the one every decoder expects: signature, IHDR,      */
            /* PLTE and tRNS when the image is palettised, IDAT, IEND.        */
            /* Ancillary chunks are deliberately not written -- no gamma, no  */
            /* timestamp, no text -- because this library has no metadata to  */
            /* put in them and inventing values would be worse than their     */
            /* absence.                                                       */
            /*                                                                */
            /* PLTE BEFORE tRNS BEFORE IDAT IS NOT A STYLE CHOICE. The        */
            /* specification fixes that order, and our own decoder enforces   */
            /* it -- PNG_TryGatherChunks refuses a tRNS that arrives before   */
            /* its PLTE or after the first IDAT -- so writing them in any     */
            /* other order would produce a file this library rejects.         */
            /*----------------------------------------------------------------*/

            iCapacity = arrZlib.Length + PNG_ENCODE_OVERHEAD;

            if (arrPlte != null)
            {   /* Room for the palette and, when present, its alphas. */
                iCapacity = iCapacity + arrPlte.Length + PNG_CHUNK_OVERHEAD;

                if (arrTrns != null)
                {   /* One byte per entry the chunk reaches. */
                    iCapacity = iCapacity + arrTrns.Length + PNG_CHUNK_OVERHEAD;
                }
            }

            bwOut = new ByteWriter(iCapacity);

            bwOut.BYTEW_WriteBytes(PNG_arrSignature, 0, PNG_SIGNATURE_SIZE);

            arrIhdr = PNG_BuildIhdrData(imgUse.IMG_iWidth, imgUse.IMG_iHeight, iBitDepth,
                                        iColorType, iInterlace);
            PNG_WriteChunk(bwOut, PNG_TYPE_IHDR, arrIhdr, 0, arrIhdr.Length);

            if (arrPlte != null)
            {   /* Palettised: the colours the indices name. */
                PNG_WriteChunk(bwOut, PNG_TYPE_PLTE, arrPlte, 0, arrPlte.Length);

                if (arrTrns != null)
                {   /* And their alphas, for as many entries as it reaches. */
                    PNG_WriteChunk(bwOut, PNG_TYPE_TRNS, arrTrns, 0, arrTrns.Length);
                }
            }

            PNG_WriteChunk(bwOut, PNG_TYPE_IDAT, arrZlib, 0, arrZlib.Length);
            PNG_WriteChunk(bwOut, PNG_TYPE_IEND, PNG_arrSignature, 0, 0);

            arrOut = bwOut.BYTEW_ToArray();

            /* A complete file any PNG decoder will read. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* PNG_TryResolveColorType:                                           */
        /*                                                                    */
        /* Works out which colour type to write and how many channels the     */
        /* buffer must be converted to, honouring an explicit request when it */
        /* is lossless and refusing it when it is not.                        */
        /*                                                                    */
        /* THE LOSSLESS/LOSSY LINE IS THE WHOLE OF THIS FUNCTION, and it is   */
        /* the same line every other encoder in this library draws. Widening  */
        /* is free and is done silently -- grey to colour, or an opaque alpha */
        /* appended -- because nothing is lost and the caller's pixels come   */
        /* back unchanged. Narrowing is refused with a reason naming          */
        /* IMG_TryConvertChannels, because collapsing colour to luminance or  */
        /* discarding an alpha channel is a decision with a visible result    */
        /* and belongs to the caller.                                         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     imgIn            : The buffer being written.                   */
        /*     optIn            : Resolved options, never null.               */
        /*     iColorTypeOut    : Receives the PNG colour type to write.      */
        /*     iWantChannelsOut : Receives the channel count the buffer must  */
        /*                        have; equal to the buffer's own when no     */
        /*                        conversion is needed.                       */
        /*     sReason          : Receives "" or why the request was refused. */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool             : true when a colour type was resolved.       */
        /*--------------------------------------------------------------------*/

        private static bool PNG_TryResolveColorType(ImageBuffer imgIn, PngEncodeOptions optIn,
                                                    out int iColorTypeOut,
                                                    out int iWantChannelsOut, out string sReason)
        {
            int  iNaturalType;
            int  iRequested;
            int  iRequestedChannels;
            bool bSourceHasAlpha;
            bool bRequestHasAlpha;

            iColorTypeOut    = 0;
            iWantChannelsOut = 0;
            sReason          = "";

            iNaturalType = PNG_ColorTypeForChannels(imgIn.IMG_iChannels);
            iRequested   = optIn.PNGOPT_iColorType;

            if (iRequested == 0)
            {   /* The sentinel: match the buffer. See PngEncodeOptions for   */
                /* why zero can mean this without stealing colour type 0.     */
                iColorTypeOut    = iNaturalType;
                iWantChannelsOut = imgIn.IMG_iChannels;
                return(true);
            }

            if (iRequested == PNG_COLOR_PALETTE)
            {   /* PALETTE INVERTS THIS FUNCTION'S ARITHMETIC, which is why     */
                /* it is a branch rather than another row in the comparison     */
                /* below. Everywhere else iWantChannels is the file's samples   */
                /* per pixel, so the two numbers are the same thing seen from   */
                /* two sides. A palettised file stores ONE sample per pixel     */
                /* -- an index -- and means three or four channels, so the      */
                /* sample count carries no information about the conversion     */
                /* the buffer needs and the usual subtraction would compare     */
                /* 1 against 3 and call a widening a loss.                      */
                /*                                                              */
                /* What iWantChannels means here is the width of the PALETTE    */
                /* ENTRY, and the lossless/lossy line is unchanged: a grey      */
                /* buffer widens to RGB entries, a grey+alpha buffer to RGBA    */
                /* ones, and nothing is ever dropped.                           */
                if (imgIn.IMG_iBitsPerSample != ImageBuffer.IMG_BITS_8)
                {   /* A PLTE entry is three 8-bit bytes and the format has     */
                    /* no wider form, so palettising 16-bit samples would       */
                    /* discard half of every one. Refused as the narrowing it   */
                    /* is, permanently, not as something unbuilt.               */
                    sReason = "PNG encode: the buffer holds 16-bit samples and a PLTE entry " +
                              "is 8 bits per channel, so a palette would discard half of " +
                              "every sample";
                    return(false);
                }

                if (imgIn.IMG_iChannels == ImageBuffer.IMG_CHANNELS_GRAY_ALPHA ||
                    imgIn.IMG_iChannels == ImageBuffer.IMG_CHANNELS_RGBA)
                {   /* The buffer carries alpha, so the entries must too and   */
                    /* a tRNS chunk will be written alongside PLTE.            */
                    iWantChannelsOut = ImageBuffer.IMG_CHANNELS_RGBA;
                }
                else
                {   /* No alpha anywhere, so PLTE alone says everything. */
                    iWantChannelsOut = ImageBuffer.IMG_CHANNELS_RGB;
                }

                iColorTypeOut = PNG_COLOR_PALETTE;

                /* An exact palette will be extracted; whether the image has   */
                /* few enough colours to fit one is PNG_TryBuildPalette's      */
                /* question, not this function's.                              */
                return(true);
            }

            if (iRequested != PNG_COLOR_RGB && iRequested != PNG_COLOR_GRAY_ALPHA &&
                iRequested != PNG_COLOR_RGBA)
            {   /* Not a colour type at all. Note that 0 was consumed above   */
                /* as the sentinel, so it cannot reach here.                  */
                sReason = "PNG encode: colour type " + iRequested.ToString() +
                          " is not 2, 4 or 6";
                return(false);
            }

            iRequestedChannels = PNG_SamplesForColorType(iRequested);
            bSourceHasAlpha    = (imgIn.IMG_iChannels == ImageBuffer.IMG_CHANNELS_GRAY_ALPHA ||
                                  imgIn.IMG_iChannels == ImageBuffer.IMG_CHANNELS_RGBA);
            bRequestHasAlpha   = (iRequested == PNG_COLOR_GRAY_ALPHA ||
                                  iRequested == PNG_COLOR_RGBA);

            if (iRequestedChannels < imgIn.IMG_iChannels)
            {   /* Fewer channels out than in: colour collapsing to grey, or   */
                /* an alpha channel being discarded. Either way the caller     */
                /* should say so explicitly.                                   */
                sReason = "PNG encode: colour type " + iRequested.ToString() + " has " +
                          iRequestedChannels.ToString() + " channels but the buffer has " +
                          imgIn.IMG_iChannels.ToString() +
                          "; convert it first with IMG_TryConvertChannels if that loss is intended";
                return(false);
            }

            if (bSourceHasAlpha && !bRequestHasAlpha)
            {   /* Wider overall but still dropping the alpha -- gray+alpha    */
                /* asked to become truecolour is the case that reaches here.   */
                sReason = "PNG encode: colour type " + iRequested.ToString() +
                          " carries no alpha but the buffer does" +
                          "; convert it first with IMG_TryConvertChannels if that loss is intended";
                return(false);
            }

            iColorTypeOut    = iRequested;
            iWantChannelsOut = iRequestedChannels;

            /* A request that widens losslessly, or asks for what is already   */
            /* there.                                                          */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* PNG_TryResolveBitDepth:                                            */
        /*                                                                    */
        /* Works out which bit depth to write, honouring an explicit request  */
        /* when it is lossless and refusing it when it is not.                */
        /*                                                                    */
        /* THIS IS THE SAME LOSSLESS/LOSSY LINE PNG_TryResolveColorType DRAWS */
        /* FOR CHANNELS, applied to sample width. Widening is free and is     */
        /* done silently; narrowing is refused and the caller is told why.    */
        /*                                                                    */
        /*   source  request      what happens                                */
        /*   ------  -----------  ---------------------------------------     */
        /*    8      0 or 8       write 8                                     */
        /*    8      16           WIDEN by 257 and write 16 -- lossless       */
        /*    8      1, 2 or 4    write sub-byte IF every sample is exactly   */
        /*                        representable, else refuse                  */
        /*   16      0 or 16      write 16                                    */
        /*   16      8 or below   REFUSE: narrowing                           */
        /*                                                                    */
        /* WHY 257 IS THE LOSSLESS WIDENING and not 256. Multiplying by 257   */
        /* copies the byte into both halves of the ushort -- 0xAB becomes     */
        /* 0xABAB -- so 0 maps to 0, 255 maps to 65535, and taking the high   */
        /* byte back recovers the original exactly. It is the same bit-       */
        /* replication rule PNG_arrDepthScale encodes for the narrow depths,  */
        /* one octave up. Multiplying by 256 would map 255 to 0xFF00 and lose */
        /* the top of the range.                                              */
        /*                                                                    */
        /* WHY SUB-BYTE OUTPUT DEMANDS EXACT REPRESENTABILITY rather than     */
        /* quantising. An 8-bit buffer holds 0 to 255 and a 4-bit file holds  */
        /* sixteen levels, so writing one as the other is a lossy reduction   */
        /* with more than one defensible answer -- round, truncate, dither.   */
        /* This library refuses that class of decision on the caller's        */
        /* behalf everywhere else: BmpCodec refuses an image with more        */
        /* colours than its palette can name rather than quantising, and the  */
        /* PNG palette encoder below does the same. So the encoder writes     */
        /* sub-byte only when it can do so EXACTLY, which is precisely        */
        /* the case that matters in practice -- an image that came from       */
        /* decoding a sub-byte PNG has samples that are multiples of the      */
        /* depth's replication step and round-trips perfectly.                */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     imgIn         : The buffer being written.                      */
        /*     optIn         : Resolved options, never null.                  */
        /*     iColorType    : The colour type already resolved.              */
        /*     iBitDepthOut  : Receives the depth to write.                   */
        /*     sReason       : Receives "" or why the request was refused.    */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool          : true when a depth was resolved.                */
        /*--------------------------------------------------------------------*/

        private static bool PNG_TryResolveBitDepth(ImageBuffer imgIn, PngEncodeOptions optIn,
                                                   int iColorType, out int iBitDepthOut,
                                                   out string sReason)
        {
            int iSourceDepth;
            int iRequested;

            iBitDepthOut = 0;
            sReason      = "";

            iSourceDepth = imgIn.IMG_iBitsPerSample;
            iRequested   = optIn.PNGOPT_iBitDepth;

            if (iRequested == 0)
            {   /* The sentinel: match the buffer. Zero is not a legal PNG   */
                /* depth, so unlike the colour-type sentinel it steals       */
                /* nothing at all.                                           */
                iRequested = iSourceDepth;
            }

            if (iRequested != 1 && iRequested != 2 && iRequested != 4 &&
                iRequested != 8 && iRequested != 16)
            {   /* Not a depth the format has. */
                sReason = "PNG encode: a bit depth of " + iRequested.ToString() +
                          " is not 1, 2, 4, 8 or 16";
                return(false);
            }

            if (!PNG_IsDepthValidForColorType(iRequested, iColorType))
            {   /* A legal depth and a legal colour type that cannot go     */
                /* together -- three channels cannot share four bits. The   */
                /* same table the decoder validates IHDR against, reused    */
                /* rather than restated.                                    */
                sReason = "PNG encode: bit depth " + iRequested.ToString() +
                          " is not allowed with colour type " + iColorType.ToString();
                return(false);
            }

            if (iRequested < iSourceDepth)
            {   /* Narrowing: 16-bit samples asked to become 8 or fewer, or    */
                /* 8-bit samples asked to become sub-byte. The sub-byte case   */
                /* has an exact path and is taken below; anything narrowing    */
                /* from 16 has none, because this library has no depth         */
                /* converter for a caller to have used deliberately.           */
                if (iSourceDepth == ImageBuffer.IMG_BITS_16)
                {   /* From 16 bits there is no exact narrowing worth having. */
                    sReason = "PNG encode: the buffer holds 16-bit samples and depth " +
                              iRequested.ToString() +
                              " would discard part of every one of them";
                    return(false);
                }

                if (!PNG_TrySubByteRepresentable(imgIn, iRequested, out sReason))
                {   /* At least one sample cannot be written exactly. */
                    return(false);
                }
            }

            iBitDepthOut = iRequested;

            /* A depth that is legal for the colour type and either matches   */
            /* the buffer, widens it losslessly, or narrows it exactly.       */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* PNG_TrySubByteRepresentable:                                       */
        /*                                                                    */
        /* Whether every sample in an 8-bit buffer can be written at a        */
        /* narrower depth without loss.                                       */
        /*                                                                    */
        /* A sample survives if it is an exact multiple of the depth's        */
        /* replication step -- 0x11 at depth 4, 0x55 at depth 2, 0xFF at      */
        /* depth 1 -- because that is what decoding the narrow value back     */
        /* produces. Anything else would have to be rounded, and rounding is  */
        /* the caller's decision to make.                                     */
        /*                                                                    */
        /* THE REASON NAMES THE FIRST OFFENDING SAMPLE AND WHAT WOULD HAVE    */
        /* BEEN ACCEPTABLE, because "not representable" on its own sends the  */
        /* reader looking for a bug in their image rather than at a value     */
        /* they can correct.                                                  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     imgIn      : An 8-bit buffer.                                  */
        /*     iBitDepth  : 1, 2 or 4.                                        */
        /*     sReason    : Receives "" or which sample cannot be written.    */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool       : true when every sample is exactly representable.  */
        /*--------------------------------------------------------------------*/

        private static bool PNG_TrySubByteRepresentable(ImageBuffer imgIn, int iBitDepth,
                                                        out string sReason)
        {
            int iScale;
            int iSample;
            int i;

            sReason = "";
            iScale  = (int)PNG_arrDepthScale[iBitDepth];

            for (i = 0; i < imgIn.IMG_arrSamples8.Length; i++)
            {   /* Every sample, stopping at the first that will not fit. */
                iSample = (int)imgIn.IMG_arrSamples8[i];

                if ((iSample % iScale) != 0)
                {   /* Not a multiple of the replication step, so writing it   */
                    /* at this depth would round it.                           */
                    sReason = "PNG encode: sample " + i.ToString() + " is " +
                              iSample.ToString() + ", which cannot be written at depth " +
                              iBitDepth.ToString() + " without rounding; values there must be " +
                              "multiples of " + iScale.ToString() +
                              ". Quantise the image first if that loss is intended";
                    return(false);
                }
            }

            /* Every sample lands exactly on one of the depth's levels. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* PNG_TryResolvePaletteDepth:                                        */
        /*                                                                    */
        /* Works out how many bits an index needs, for a palettised file.     */
        /*                                                                    */
        /* THE SIBLING OF PNG_TryResolveBitDepth AND DELIBERATELY NOT PART OF */
        /* IT, because the question is a different one. That function asks    */
        /* whether the SAMPLE VALUES survive a narrower depth; this one asks  */
        /* only whether the depth can COUNT far enough, and the sample values */
        /* have nothing to do with it -- an index of 5 is the number five at  */
        /* every depth that can hold it, where a grey level of 5 means a      */
        /* different brightness at every depth. Folding the two together      */
        /* would mean a function whose meaning changed halfway down.          */
        /*                                                                    */
        /* AN UNREQUESTED DEPTH IS CHOSEN AUTOMATICALLY, as the smallest that */
        /* can name every entry: 2 colours fit depth 1, 4 fit 2, 16 fit 4 and */
        /* 256 fit 8. That is a decision rather than an accident. The         */
        /* alternative -- always write 8 -- is simpler to describe and is     */
        /* what a caller gets who asks for it explicitly, but it quadruples   */
        /* a two-colour image for no gain, and every production encoder       */
        /* picks the narrow depth. Since the palette is exact, the narrow     */
        /* depth is exactly as lossless as the wide one, so there is nothing  */
        /* to trade off; only the file size differs.                          */
        /*                                                                    */
        /* AN EXPLICIT REQUEST IS HONOURED WHEN IT FITS, including a request  */
        /* WIDER than necessary -- writing a four-colour image at depth 8 is  */
        /* wasteful but not wrong, and refusing it would be this function     */
        /* overruling a caller who may have a reader in mind that dislikes    */
        /* sub-byte indices. Only a depth too narrow to number the entries is */
        /* refused, and the reason names both figures.                        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     optIn         : Resolved options, never null.                  */
        /*     iEntryCount   : How many distinct colours the palette holds,   */
        /*                     from PNG_TryBuildPalette. At least 1.          */
        /*     iBitDepthOut  : Receives the depth to write.                   */
        /*     sReason       : Receives "" or why the request was refused.    */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool          : true when a depth was resolved.                */
        /*--------------------------------------------------------------------*/

        private static bool PNG_TryResolvePaletteDepth(PngEncodeOptions optIn, int iEntryCount,
                                                       out int iBitDepthOut, out string sReason)
        {
            int iNatural;
            int iRequested;

            iBitDepthOut = 0;
            sReason      = "";

            /* The smallest depth that can number every entry. Written as a    */
            /* ladder rather than as a logarithm because there are only four   */
            /* answers and the boundaries are the interesting part.            */
            if (iEntryCount <= 2)
            {   /* One bit names two colours. */
                iNatural = 1;
            }
            else if (iEntryCount <= 4)
            {   /* Two bits name four. */
                iNatural = 2;
            }
            else if (iEntryCount <= 16)
            {   /* Four bits name sixteen. */
                iNatural = 4;
            }
            else
            {   /* Eight bits name 256, and PNG_TryBuildPalette has already   */
                /* refused anything that needs more.                          */
                iNatural = 8;
            }

            iRequested = optIn.PNGOPT_iBitDepth;

            if (iRequested == 0)
            {   /* The sentinel: no depth was asked for, so choose one. */
                iBitDepthOut = iNatural;
                return(true);
            }

            if (iRequested != 1 && iRequested != 2 && iRequested != 4 &&
                iRequested != 8 && iRequested != 16)
            {   /* Not a depth the format has at all. Checked separately from   */
                /* the palette rule below so the reason says which of the two   */
                /* things is wrong.                                             */
                sReason = "PNG encode: a bit depth of " + iRequested.ToString() +
                          " is not 1, 2, 4, 8 or 16";
                return(false);
            }

            if (!PNG_IsDepthValidForColorType(iRequested, PNG_COLOR_PALETTE))
            {   /* Depth 16 is the only case that reaches here: a PLTE entry   */
                /* is 8-bit, so there is nothing for a 16-bit index to name.   */
                /* The same table the decoder validates IHDR against, reused   */
                /* rather than restated.                                       */
                sReason = "PNG encode: bit depth " + iRequested.ToString() +
                          " is not allowed with colour type " + PNG_COLOR_PALETTE.ToString();
                return(false);
            }

            if ((1 << iRequested) < iEntryCount)
            {   /* Too narrow to number the colours this image actually has.   */
                /* The reason names the count and the depth that would work,   */
                /* because "too narrow" alone leaves the caller guessing at    */
                /* which of the two numbers to change.                         */
                sReason = "PNG encode: depth " + iRequested.ToString() + " can name " +
                          (1 << iRequested).ToString() + " palette entries but the image needs " +
                          iEntryCount.ToString() + "; depth " + iNatural.ToString() +
                          " is the narrowest that fits";
                return(false);
            }

            iBitDepthOut = iRequested;

            /* A depth wide enough to number every entry. */
            return(true);
        }

        /*---------------------------------------------------------------------*/
        /* PNG_TryBuildPalette:                                                */
        /*                                                                     */
        /* Collects the distinct colours of an image into a palette and        */
        /* produces the per-pixel index array that names them.                 */
        /*                                                                     */
        /* The inverse of PNG_BuildPaletteRgba, and it produces the same       */
        /* four-bytes-per-entry table that function does, so the two ends of   */
        /* the round trip speak one layout.                                    */
        /*                                                                     */
        /* THE PALETTE IS EXACT AND IS NEVER QUANTISED. An image with more     */
        /* colours than 256 is REFUSED, exactly as BmpCodec refuses one with   */
        /* more colours than its depth can hold. Choosing which colours to     */
        /* throw away, and whether to dither, is real work with real choices   */
        /* in it; it belongs with GIF, which needs it and whose                */
        /* GIFOPT_iPaletteSize and GIFOPT_bDither exist to control it. A       */
        /* silent undithered reduction here would be the worst available       */
        /* answer.                                                             */
        /*                                                                     */
        /* Colours are numbered in the order they are first ENCOUNTERED,       */
        /* scanning top-left to bottom-right. That is arbitrary but it is      */
        /* deterministic, which is what matters: the same image always         */
        /* produces the same palette and therefore the same file. It is also   */
        /* what BmpCodec does, so the two palettising encoders in this library */
        /* number their entries the same way.                                  */
        /*                                                                     */
        /* THE KEY PACKS ALL FOUR CHANNELS even when the image has three, and  */
        /* that is what makes two colours differing only in alpha two entries  */
        /* rather than one. Getting it wrong would merge them and silently     */
        /* drop the transparency of whichever arrived second.                  */
        /*                                                                     */
        /* Arguments:                                                          */
        /*     imgIn           : Source image, 8-bit, exactly 3 or 4 channels; */
        /*                       PNG_TryResolveColorType guarantees both.      */
        /*     arrPaletteOut   : Receives 4 bytes per entry as R, G, B, A.     */
        /*     iEntryCountOut  : Receives the number of distinct colours.      */
        /*     arrIndicesOut   : Receives one palette index per pixel.         */
        /*     sReason         : Receives "" or why the image was refused.     */
        /*                                                                     */
        /* Returns:                                                            */
        /*     bool            : true when the image fits a 256-entry palette. */
        /*---------------------------------------------------------------------*/

        private static bool PNG_TryBuildPalette(ImageBuffer imgIn, out byte[] arrPaletteOut,
                                                out int iEntryCountOut, out byte[] arrIndicesOut,
                                                out string sReason)
        {
            Dictionary<uint, int> dictSeen;
            byte[]                arrPalette;
            byte[]                arrIndices;
            int                   iChannels;
            int                   iPixels;
            int                   iCount;
            int                   iIndex;
            int                   iSrc;
            int                   iAlpha;
            uint                  uKey;
            int                   i;

            arrPaletteOut  = null;
            iEntryCountOut = 0;
            arrIndicesOut  = null;
            sReason        = "";

            iChannels  = imgIn.IMG_iChannels;
            iPixels    = imgIn.IMG_iWidth * imgIn.IMG_iHeight;
            arrPalette = new byte[PNG_PALETTE_MAX_ENTRIES * PNG_RGBA_ENTRY_BYTES];
            arrIndices = new byte[iPixels];
            dictSeen   = new Dictionary<uint, int>();
            iCount     = 0;

            for (i = 0; i < iPixels; i++)
            {   /* One pixel per iteration, in reading order. */
                iSrc = i * iChannels;

                if (iChannels == ImageBuffer.IMG_CHANNELS_RGBA)
                {   /* The buffer carries its own alpha. */
                    iAlpha = (int)imgIn.IMG_arrSamples8[iSrc + 3];
                }
                else
                {   /* Three channels, so every pixel is fully opaque. */
                    iAlpha = ImageBuffer.IMG_OPAQUE_8;
                }

                /* Pack the four channels into one unsigned key. Unsigned   */
                /* rather than int because the red byte occupies the top    */
                /* eight bits and a signed key would go negative for any    */
                /* red above 127 -- which works, but reads as a bug every   */
                /* time someone looks at it.                                */
                uKey = (((uint)imgIn.IMG_arrSamples8[iSrc]) << 24) |
                       (((uint)imgIn.IMG_arrSamples8[iSrc + 1]) << 16) |
                       (((uint)imgIn.IMG_arrSamples8[iSrc + 2]) << 8) |
                        ((uint)iAlpha);

                if (!dictSeen.TryGetValue(uKey, out iIndex))
                {   /* A colour not seen before; give it the next slot. */
                    if (iCount >= PNG_PALETTE_MAX_ENTRIES)
                    {   /* Out of palette entries. Refuse rather than reduce.   */
                        /* THE REFUSAL IS PERMANENT. A quantiser does exist,    */
                        /* in GifCodec, and it is deliberately not reached      */
                        /* from here because a caller asking for colour type    */
                        /* 3 asked for a PALETTE, not for an approximation of   */
                        /* their image, and PNG carries no option to request    */
                        /* one. A caller who wants a lossy reduction asks GIF   */
                        /* for one; a caller who wants this image losslessly    */
                        /* leaves PNGOPT_iColorType at 0 and gets truecolour.   */
                        sReason = "PNG encode: the image has more than " +
                                  PNG_PALETTE_MAX_ENTRIES.ToString() +
                                  " distinct colours, which a palette cannot hold; this " +
                                  "encoder takes an EXACT palette and never quantises";
                        return(false);
                    }

                    iIndex = iCount;
                    dictSeen.Add(uKey, iIndex);

                    arrPalette[(iIndex * PNG_RGBA_ENTRY_BYTES)]     = imgIn.IMG_arrSamples8[iSrc];
                    arrPalette[(iIndex * PNG_RGBA_ENTRY_BYTES) + 1] = imgIn.IMG_arrSamples8[iSrc + 1];
                    arrPalette[(iIndex * PNG_RGBA_ENTRY_BYTES) + 2] = imgIn.IMG_arrSamples8[iSrc + 2];
                    arrPalette[(iIndex * PNG_RGBA_ENTRY_BYTES) + 3] = (byte)iAlpha;

                    iCount = iCount + 1;
                }

                arrIndices[i] = (byte)iIndex;
            }

            arrPaletteOut  = arrPalette;
            iEntryCountOut = iCount;
            arrIndicesOut  = arrIndices;

            /* Every pixel now has an index, and every index a colour. A        */
            /* validated buffer has at least one pixel, so iCount is at least   */
            /* one and the depth ladder above always has something to size.     */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* PNG_TryMakeIndexImage:                                             */
        /*                                                                    */
        /* Wraps a per-pixel palette index array in a one-channel 8-bit       */
        /* ImageBuffer so the ordinary filter and packing stages can write    */
        /* it without knowing it is a palette.                                */
        /*                                                                    */
        /* WHY A BUFFER RATHER THAN A SPECIAL PATH. The packing and filtering */
        /* a palettised scanline needs is EXACTLY what a one-channel image    */
        /* needs at the same depth -- one sample per pixel, packed most       */
        /* significant first, filtered with a step of one byte below depth 8. */
        /* Handing PNG_BuildFilteredStream a buffer whose samples happen to   */
        /* be indices reuses all of it, which is what 11.8 asked for, and     */
        /* keeps interlaced palettised output working for free: the Adam7     */
        /* gather sees an ordinary one-channel image.                         */
        /*                                                                    */
        /* THE ONE THING THAT IS NOT ORDINARY is that these samples must NOT  */
        /* be scaled when they are packed sub-byte, which is why the scale    */
        /* travels as an argument and is chosen by PNG_PackScaleFor from the  */
        /* colour type rather than from the depth alone.                      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iWidth      : Image width in pixels.                           */
        /*     iHeight     : Image height in pixels.                          */
        /*     arrIndices  : One index per pixel, row major.                  */
        /*     imgOut      : Receives the index image; null on failure.       */
        /*     sReason     : Receives "" or the allocation failure.           */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool        : true when imgOut holds the index image.          */
        /*--------------------------------------------------------------------*/

        private static bool PNG_TryMakeIndexImage(int iWidth, int iHeight, byte[] arrIndices,
                                                  out ImageBuffer imgOut, out string sReason)
        {
            ImageBuffer imgNew;

            if (!ImageBuffer.IMG_TryAllocate(iWidth, iHeight, ImageBuffer.IMG_CHANNELS_GRAY,
                                             ImageBuffer.IMG_BITS_8, out imgNew, out sReason))
            {   /* The same geometry was accepted for the source, so this     */
                /* cannot normally fail; reported rather than assumed away.   */
                imgOut = null;
                return(false);
            }

            Array.Copy(arrIndices, 0, imgNew.IMG_arrSamples8, 0, arrIndices.Length);

            imgOut = imgNew;

            /* A buffer the ordinary filter stage can write. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* PNG_BuildPlteData:                                                 */
        /*                                                                    */
        /* The body of a PLTE chunk: three bytes per entry, red then green    */
        /* then blue.                                                         */
        /*                                                                    */
        /* Alpha is dropped here on purpose rather than lost -- PLTE has no   */
        /* room for it and PNG_BuildPaletteTrnsData writes it into the tRNS   */
        /* chunk instead. That split is the format's, not this library's.     */
        /*                                                                    */
        /* Not a Try function because it cannot fail: the entry count came    */
        /* from PNG_TryBuildPalette, which caps it at 256.                    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     arrPaletteRgba  : Four bytes per entry, from                   */
        /*                       PNG_TryBuildPalette.                         */
        /*     iEntryCount     : How many entries to write.                   */
        /*                                                                    */
        /* Returns:                                                           */
        /*     byte[]          : Exactly three bytes per entry.               */
        /*--------------------------------------------------------------------*/

        private static byte[] PNG_BuildPlteData(byte[] arrPaletteRgba, int iEntryCount)
        {
            byte[] arrPlte;
            int    i;

            arrPlte = new byte[iEntryCount * PNG_PALETTE_ENTRY_BYTES];

            for (i = 0; i < iEntryCount; i++)
            {   /* Four bytes in, three bytes out. */
                arrPlte[(i * PNG_PALETTE_ENTRY_BYTES)]     =
                    arrPaletteRgba[(i * PNG_RGBA_ENTRY_BYTES)];
                arrPlte[(i * PNG_PALETTE_ENTRY_BYTES) + 1] =
                    arrPaletteRgba[(i * PNG_RGBA_ENTRY_BYTES) + 1];
                arrPlte[(i * PNG_PALETTE_ENTRY_BYTES) + 2] =
                    arrPaletteRgba[(i * PNG_RGBA_ENTRY_BYTES) + 2];
            }

            /* The chunk body, ready for PNG_WriteChunk. */
            return(arrPlte);
        }

        /*--------------------------------------------------------------------*/
        /* PNG_BuildPaletteTrnsData:                                          */
        /*                                                                    */
        /* The body of a tRNS chunk for a palettised image: one alpha byte    */
        /* per entry, with trailing opaque entries omitted.                   */
        /*                                                                    */
        /* TRAILING OPAQUE ENTRIES ARE IMPLIED, so writing them is waste. The */
        /* format says every entry the chunk does not reach is fully opaque,  */
        /* which is the rule PNG_BuildPaletteRgba relies on when decoding, so */
        /* an image whose first three entries are transparent and whose       */
        /* remaining two hundred are not needs three bytes rather than two    */
        /* hundred and three. Real encoders trim; so does this one.           */
        /*                                                                    */
        /* THE TRIM STOPS AT ONE ENTRY AND NEVER AT ZERO, which is the one    */
        /* thing here that is not obvious. A fully opaque palette would trim  */
        /* away to nothing, and an empty tRNS is a chunk that says nothing    */
        /* while still announcing that the image has transparency -- legal by */
        /* the length rule, pointless, and the sort of thing a strict reader  */
        /* elsewhere may well reject. One byte of 255 says the same thing     */
        /* unambiguously. That case is not hypothetical: it is exactly what   */
        /* an RGBA buffer with no actual transparency produces, and the       */
        /* chunk is written for it because the OUTPUT SHAPE FOLLOWS THE INPUT */
        /* SHAPE -- see the tRNS decision in PNG_TryEncode.                   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     arrPaletteRgba  : Four bytes per entry, from                   */
        /*                       PNG_TryBuildPalette.                         */
        /*     iEntryCount     : How many entries the palette holds.          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     byte[]          : One alpha per entry up to the last           */
        /*                       non-opaque one; at least one byte long.      */
        /*--------------------------------------------------------------------*/

        private static byte[] PNG_BuildPaletteTrnsData(byte[] arrPaletteRgba, int iEntryCount)
        {
            byte[] arrTrns;
            int    iKeep;
            int    i;

            iKeep = iEntryCount;

            while (iKeep > 1 &&
                   arrPaletteRgba[((iKeep - 1) * PNG_RGBA_ENTRY_BYTES) + 3] ==
                       (byte)ImageBuffer.IMG_OPAQUE_8)
            {   /* This entry is opaque and is the last one, so the decoder   */
                /* will assume opacity for it anyway.                         */
                iKeep = iKeep - 1;
            }

            arrTrns = new byte[iKeep];

            for (i = 0; i < iKeep; i++)
            {   /* One alpha per entry the chunk still reaches. */
                arrTrns[i] = arrPaletteRgba[(i * PNG_RGBA_ENTRY_BYTES) + 3];
            }

            /* The chunk body, ready for PNG_WriteChunk. */
            return(arrTrns);
        }

        /*--------------------------------------------------------------------*/
        /* PNG_ColorTypeForChannels:                                          */
        /*                                                                    */
        /* The colour type that stores a given channel count without loss.    */
        /* The inverse of PNG_SamplesForColorType for the four non-palettised */
        /* types, and written separately rather than by searching that one,   */
        /* because a reader following the encoder should not have to run a    */
        /* table backwards in their head.                                     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iChannels  : 1, 2, 3 or 4.                                     */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int        : PNG_COLOR_GRAY, _GRAY_ALPHA, _RGB or _RGBA.       */
        /*--------------------------------------------------------------------*/

        private static int PNG_ColorTypeForChannels(int iChannels)
        {
            if (iChannels == ImageBuffer.IMG_CHANNELS_GRAY_ALPHA)
            {   /* Grey and alpha. */
                return(PNG_COLOR_GRAY_ALPHA);
            }

            if (iChannels == ImageBuffer.IMG_CHANNELS_RGB)
            {   /* Red, green, blue. */
                return(PNG_COLOR_RGB);
            }

            if (iChannels == ImageBuffer.IMG_CHANNELS_RGBA)
            {   /* Red, green, blue, alpha. */
                return(PNG_COLOR_RGBA);
            }

            /* One channel: a grey level. IMG_TryValidate has already refused   */
            /* any count outside 1 to 4, so this is the only case left.         */
            return(PNG_COLOR_GRAY);
        }

        /*--------------------------------------------------------------------*/
        /* PNG_BuildIhdrData:                                                 */
        /*                                                                    */
        /* The thirteen bytes of an IHDR chunk's payload.                     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iWidth      : Image width in pixels.                           */
        /*     iHeight     : Image height in pixels.                          */
        /*     iBitDepth   : Bits per sample.                                 */
        /*     iColorType  : PNG colour type.                                 */
        /*     iInterlace  : PNG_INTERLACE_NONE or PNG_INTERLACE_ADAM7.       */
        /*                                                                    */
        /* Returns:                                                           */
        /*     byte[]      : Exactly thirteen bytes, big-endian throughout.   */
        /*--------------------------------------------------------------------*/

        private static byte[] PNG_BuildIhdrData(int iWidth, int iHeight, int iBitDepth,
                                                int iColorType, int iInterlace)
        {
            ByteWriter bwIhdr;

            bwIhdr = new ByteWriter(PNG_IHDR_DATA_SIZE);

            bwIhdr.BYTEW_WriteU32BE((uint)iWidth);
            bwIhdr.BYTEW_WriteU32BE((uint)iHeight);
            bwIhdr.BYTEW_WriteU8(iBitDepth);
            bwIhdr.BYTEW_WriteU8(iColorType);
            bwIhdr.BYTEW_WriteU8(0);                     // compression method: deflate, the only one
            bwIhdr.BYTEW_WriteU8(0);                     // filter method: per-scanline, the only one
            bwIhdr.BYTEW_WriteU8(iInterlace);            // 0 flat, 1 Adam7; the caller decided

            /* IHDR's fixed-length payload. */
            return(bwIhdr.BYTEW_ToArray());
        }

        /*--------------------------------------------------------------------*/
        /* PNG_WriteChunk:                                                    */
        /*                                                                    */
        /* Writes one chunk: length, type, data, and a CRC over the TYPE AND  */
        /* DATA but not the length.                                           */
        /*                                                                    */
        /* THE CRC IS TAKEN OVER THE WRITER'S OWN BUFFER, which is what       */
        /* ByteWriter.BYTEW_Buffer exists for and the whole reason it exposes */
        /* the live array at all. The alternative is concatenating            */
        /* the four type bytes with the data into a temporary just to         */
        /* checksum it, which on a large IDAT means copying the whole image   */
        /* a second time for no purpose.                                      */
        /*                                                                    */
        /* The buffer is fetched AFTER every write for this chunk, never      */
        /* before. ByteWriter grows by doubling, so a reference taken earlier */
        /* can be to an array that has since been replaced -- a bug that      */
        /* would appear only on chunks that happen to straddle a growth.      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     bwIo     : Writer to append to.                                */
        /*     uType    : Packed four-byte chunk type.                        */
        /*     arrData  : Buffer holding the chunk's data.                    */
        /*     iOffset  : Index of the first data byte.                       */
        /*     iLength  : Data length; may be zero, as IEND's is.             */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void     : The chunk is appended.                              */
        /*--------------------------------------------------------------------*/

        private static void PNG_WriteChunk(ByteWriter bwIo, uint uType, byte[] arrData,
                                           int iOffset, int iLength)
        {
            int  iTypeStart;
            uint uCrc;

            bwIo.BYTEW_WriteU32BE((uint)iLength);

            /* Where the CRC's coverage begins: the type, not the length. */
            iTypeStart = bwIo.BYTEW_Length();

            bwIo.BYTEW_WriteU32BE(uType);

            if (iLength > 0)
            {   /* IEND and any other empty chunk skip this entirely. */
                bwIo.BYTEW_WriteBytes(arrData, iOffset, iLength);
            }

            uCrc = Crc32.CRC32_Compute(bwIo.BYTEW_Buffer(), iTypeStart, 4 + iLength);

            bwIo.BYTEW_WriteU32BE(uCrc);
        }

        /*--------------------------------------------------------------------*/
        /* PNG_BuildFilteredStream:                                           */
        /*                                                                    */
        /* Turns an 8-bit image into the filter-byte-plus-residuals stream a  */
        /* PNG's zlib payload carries, choosing a filter per scanline.        */
        /*                                                                    */
        /* Transcoded from lodepng.cpp (lvandeve/lodepng, version 20260119,   */
        /* commit ed6fe582), the LFS_MINSUM branch of its filter function     */
        /* together with filterScanline.                                      */
        /*                                                                    */
        /* THE FILTER CHOICE IS FREE AND THAT IS WHY IT NEEDS A HEURISTIC.    */
        /* Any of the five produces a valid file and no decoder cares which;  */
        /* the choice only affects how well the row then deflates. lodepng's  */
        /* default strategy, and the conventional one, is to filter the row   */
        /* five ways and keep whichever gives the smallest total absolute     */
        /* deviation, on the reasoning that the flattest byte distribution    */
        /* compresses best. It is a heuristic, not an optimum -- the only     */
        /* way to know which filter actually deflates smallest is to deflate  */
        /* five times -- and it is not worth improving here, because what     */
        /* matters is that the file is CORRECT and small enough, not that it  */
        /* is minimal.                                                        */
        /*                                                                    */
        /* THE COST FUNCTION TREATS FILTER 0 DIFFERENTLY, faithfully to the   */
        /* source, and the asymmetry is deliberate there. For the four        */
        /* differencing filters a residual byte is a SIGNED value, so 250     */
        /* means -6 and is cheap; for filter None the byte is the sample      */
        /* itself and 250 means 250. lodepng's own comment notes the effect:  */
        /* filter None is almost never chosen, and that is justified.         */
        /*                                                                    */
        /* THE PREDICTION COMES FROM THE SOURCE ROW ABOVE, not from a         */
        /* reconstruction, which is correct and is the mirror of the          */
        /* decoder's rule rather than a contradiction of it: on this side the */
        /* source IS what the decoder will reconstruct. Row 0 predicts from a */
        /* zero-filled row, which makes the five filters degenerate into      */
        /* exactly the no-previous-row variants lodepng writes out by hand -- */
        /* Up becomes None, Paeth becomes Sub, Average halves only the left   */
        /* neighbour -- so those variants are not needed here for the same    */
        /* reason the decoder does not need stb's first_row_filter table.     */
        /*                                                                    */
        /* A PACKING STAGE SITS IN FRONT OF THE FILTER LOOP, and it is why    */
        /* the wider output shapes cost almost nothing. Reading the sample    */
        /* array directly works only while one sample is one byte. Every      */
        /* other depth needs the row turned into its ON-THE-WIRE bytes first  */
        /* -- two per sample at 16, several samples per byte below 8 -- and   */
        /* the filters then work on those bytes knowing nothing about the     */
        /* depth, exactly as the decoder's unfilter does. Pack, then filter;  */
        /* never the reverse.                                                 */
        /*                                                                    */
        /* IT IS ALSO WHAT AN ADAM7 PASS GOES THROUGH, unchanged, which is    */
        /* the whole of why interlaced output costs so little. A pass is a    */
        /* complete little image on its own geometry, filtered and padded     */
        /* entirely independently of its neighbours, so                       */
        /* PNG_TryBuildInterlacedStream gathers each one into a buffer of its */
        /* own and calls straight into here. The mirror of the decoder, which */
        /* sends every pass through the one PNG_TryUnfilterImage.             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     imgIn       : An image whose channel count already matches the */
        /*                   colour type being written -- for a palettised    */
        /*                   file that means the ONE-CHANNEL index image, not */
        /*                   the caller's pixels.                             */
        /*     iBitDepth   : The depth to write, already resolved and already */
        /*                   checked to be reachable from the buffer without  */
        /*                   loss.                                            */
        /*     iColorType  : The colour type being written. Needed only to    */
        /*                   decide whether sub-byte samples are scaled; see  */
        /*                   PNG_PackScaleFor.                                */
        /*                                                                    */
        /* Returns:                                                           */
        /*     byte[]      : The filtered stream, ready to deflate.           */
        /*--------------------------------------------------------------------*/

        private static byte[] PNG_BuildFilteredStream(ImageBuffer imgIn, int iBitDepth,
                                                      int iColorType)
        {
            ByteWriter bwOut;
            byte[]     arrRow;
            byte[]     arrPrev;
            byte[]     arrAttempt;
            long       lCost;
            long       lSmallest;
            int        iSamplesPerRow;
            int        iPackedRowBytes;
            int        iByteWidth;
            int        iSampleScale;
            int        iBestType;
            int        iType;
            int        j;

            iSamplesPerRow  = imgIn.IMG_iWidth * imgIn.IMG_iChannels;
            iPackedRowBytes = ((iSamplesPerRow * iBitDepth) + 7) / 8;
            iByteWidth      = PNG_FilterStepFor(iBitDepth, imgIn.IMG_iChannels);
            iSampleScale    = PNG_PackScaleFor(iBitDepth, iColorType);

            /* This row's packed bytes, and the previous row's. The previous    */
            /* row starts zero-filled, which stands in for "no row above" and   */
            /* makes row 0 correct with no special case -- the same trick the   */
            /* decoder uses, and the reason lodepng's no-previous-row filter    */
            /* variants are not needed here.                                    */
            arrRow  = new byte[iPackedRowBytes];
            arrPrev = new byte[iPackedRowBytes];

            /* Five attempts side by side in one allocation, reused for every   */
            /* row rather than allocated per row.                               */
            arrAttempt = new byte[iPackedRowBytes * 5];

            bwOut = new ByteWriter((iPackedRowBytes + 1) * imgIn.IMG_iHeight);

            iBestType = PNG_FILTER_NONE;
            lSmallest = 0L;

            for (j = 0; j < imgIn.IMG_iHeight; j++)
            {   /* One scanline per iteration: pack it, then filter it five   */
                /* ways and keep the cheapest.                                */
                PNG_PackScanline(imgIn, j, iBitDepth, iSamplesPerRow, iSampleScale, arrRow);

                for (iType = PNG_FILTER_NONE; iType <= PNG_FILTER_PAETH; iType++)
                {   /* Filter the packed row every way. */
                    PNG_FilterScanlineInto(arrRow, 0, arrPrev, iPackedRowBytes, iByteWidth,
                                           iType, arrAttempt, iType * iPackedRowBytes);

                    lCost = PNG_ScanlineCost(arrAttempt, iType * iPackedRowBytes,
                                             iPackedRowBytes, iType);

                    if (iType == PNG_FILTER_NONE || lCost < lSmallest)
                    {   /* The first attempt always seeds the comparison;       */
                        /* after that only a strict improvement wins, so ties   */
                        /* go to the earlier filter and the choice is stable.   */
                        iBestType = iType;
                        lSmallest = lCost;
                    }
                }

                bwOut.BYTEW_WriteU8(iBestType);
                bwOut.BYTEW_WriteBytes(arrAttempt, iBestType * iPackedRowBytes, iPackedRowBytes);

                /* The next row predicts from THIS row's PACKED bytes, which   */
                /* is what the decoder will have reconstructed by then.        */
                Array.Copy(arrRow, 0, arrPrev, 0, iPackedRowBytes);
            }

            /* Filter bytes and residuals, ready for the compressor. */
            return(bwOut.BYTEW_ToArray());
        }

        /*--------------------------------------------------------------------*/
        /* PNG_TryBuildInterlacedStream:                                      */
        /*                                                                    */
        /* Builds the filtered stream of an Adam7 image: the seven passes in  */
        /* order, each gathered into a dense little image and filtered on its */
        /* own geometry.                                                      */
        /*                                                                    */
        /* THE MIRROR OF PNG_TryDeinterlace, AND IT REUSES THE SAME FOUR      */
        /* TABLES. Pass p carries the pixels at                               */
        /*                                                                    */
        /*     x = PNG_arrAdam7Xorig[p] + (i * PNG_arrAdam7Xspacing[p])       */
        /*     y = PNG_arrAdam7Yorig[p] + (j * PNG_arrAdam7Yspacing[p])       */
        /*                                                                    */
        /* and is stored as though it were a complete image of its own -- its */
        /* own width, its own row padding, its own filter byte per row. That  */
        /* is why the whole of the encoding work here is a gather and a loop: */
        /* PNG_BuildFilteredStream already does everything one pass needs.    */
        /*                                                                    */
        /* AN EMPTY PASS IS SKIPPED ENTIRELY -- no rows and NOT EVEN A FILTER */
        /* BYTE. This is the single rule the format punishes hardest, and it  */
        /* is the same rule the decoder turns on: a one-pixel-wide image has  */
        /* five empty passes, and emitting so much as one byte for a pass     */
        /* that should not exist puts every later pass out of step for the    */
        /* reader. Writing nothing here and consuming nothing there is what   */
        /* makes the two agree.                                               */
        /*                                                                    */
        /* THE STREAM IS LARGER THAN THE FLAT ONE, which is worth expecting   */
        /* rather than discovering: every pass pays its own row padding and   */
        /* its own filter byte, so a 9x9 image writes 7 filter bytes' worth   */
        /* of passes plus padding the flat form never needs. Interlacing buys */
        /* progressive display, not size.                                     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     imgIn       : The image to write -- for a palettised file the  */
        /*                   one-channel index image, exactly as the flat     */
        /*                   path receives.                                   */
        /*     iBitDepth   : The depth to write.                              */
        /*     iColorType  : The colour type being written.                   */
        /*     arrOut      : Receives the seven passes' filtered bytes,       */
        /*                   concatenated; null on failure.                   */
        /*     sReason     : Receives "" or which pass could not be built.    */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool        : true when arrOut holds the whole stream.         */
        /*--------------------------------------------------------------------*/

        private static bool PNG_TryBuildInterlacedStream(ImageBuffer imgIn, int iBitDepth,
                                                         int iColorType, out byte[] arrOut,
                                                         out string sReason)
        {
            ByteWriter  bwOut;
            ImageBuffer imgPass;
            byte[]      arrPass;
            int         iFlatRowBytes;
            int         iCapacity;
            int         iPassWidth;
            int         iPassHeight;
            int         p;

            arrOut  = null;
            sReason = "";

            /* A starting capacity, not a limit: the writer grows by doubling   */
            /* and nothing breaks if this is short. The flat size plus one      */
            /* row's worth per pass covers the extra padding and filter bytes   */
            /* interlacing costs, which is what makes it a better guess than    */
            /* the flat figure alone.                                           */
            iFlatRowBytes = ((imgIn.IMG_iWidth * imgIn.IMG_iChannels * iBitDepth) + 7) / 8;
            iCapacity     = ((iFlatRowBytes + 1) * imgIn.IMG_iHeight) +
                            (PNG_ADAM7_PASSES * (iFlatRowBytes + 1));

            bwOut = new ByteWriter(iCapacity);

            for (p = 0; p < PNG_ADAM7_PASSES; p++)
            {   /* One pass per iteration, in stream order. */
                iPassWidth  = PNG_Adam7PassWidth(imgIn.IMG_iWidth, p);
                iPassHeight = PNG_Adam7PassHeight(imgIn.IMG_iHeight, p);

                if (iPassWidth <= 0 || iPassHeight <= 0)
                {   /* This pass's grid lands on no pixel of this image, so   */
                    /* it is absent from the stream and contributes not one   */
                    /* byte.                                                  */
                    continue;
                }

                if (!PNG_TryGatherAdam7Pass(imgIn, p, iPassWidth, iPassHeight, out imgPass,
                                            out sReason))
                {   /* Name the pass: without it the reader cannot tell which   */
                    /* of seven sub-images failed to allocate.                  */
                    sReason = "PNG encode: interlace pass " + p.ToString() + ": " + sReason;
                    return(false);
                }

                arrPass = PNG_BuildFilteredStream(imgPass, iBitDepth, iColorType);

                bwOut.BYTEW_WriteBytes(arrPass, 0, arrPass.Length);
            }

            arrOut = bwOut.BYTEW_ToArray();

            /* Seven passes written in order, empty ones genuinely absent. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* PNG_TryGatherAdam7Pass:                                            */
        /*                                                                    */
        /* Copies the pixels one interlace pass covers out of the full image  */
        /* and into a dense buffer of the pass's own dimensions.              */
        /*                                                                    */
        /* THE EXACT INVERSE OF THE SCATTER IN PNG_TryDeinterlace, reading    */
        /* where that one writes, and written as its own function for the     */
        /* same reason the scatter is kept out of the unfilter loop: the      */
        /* filter stage must see a pass's rows contiguously, because each row */
        /* predicts from the row above it IN THE PASS rather than in the      */
        /* finished image. Gathering first is what makes that true.           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     imgIn        : The full image.                                 */
        /*     iPass        : 0 to 6.                                         */
        /*     iPassWidth   : The pass's width, already computed and already  */
        /*                    known to be positive.                           */
        /*     iPassHeight  : Likewise its height.                            */
        /*     imgPassOut   : Receives the dense sub-image; null on failure.  */
        /*     sReason      : Receives "" or the allocation failure.          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool         : true when imgPassOut holds the pass.            */
        /*--------------------------------------------------------------------*/

        private static bool PNG_TryGatherAdam7Pass(ImageBuffer imgIn, int iPass, int iPassWidth,
                                                   int iPassHeight, out ImageBuffer imgPassOut,
                                                   out string sReason)
        {
            ImageBuffer imgPass;
            int         iChannels;
            int         iSrcX;
            int         iSrcY;
            int         iSrcBase;
            int         iDstBase;
            int         i;
            int         j;
            int         k;

            if (!ImageBuffer.IMG_TryAllocate(iPassWidth, iPassHeight, imgIn.IMG_iChannels,
                                             imgIn.IMG_iBitsPerSample, out imgPass, out sReason))
            {   /* A pass is never larger than the image, so an allocator   */
                /* that accepted the image should accept this; reported     */
                /* rather than assumed away.                                */
                imgPassOut = null;
                return(false);
            }

            iChannels = imgIn.IMG_iChannels;

            for (j = 0; j < iPassHeight; j++)
            {   /* One pass row per iteration. */
                iSrcY = (j * PNG_arrAdam7Yspacing[iPass]) + PNG_arrAdam7Yorig[iPass];

                for (i = 0; i < iPassWidth; i++)
                {   /* One pass pixel per iteration. */
                    iSrcX = (i * PNG_arrAdam7Xspacing[iPass]) + PNG_arrAdam7Xorig[iPass];

                    iSrcBase = ((iSrcY * imgIn.IMG_iWidth) + iSrcX) * iChannels;
                    iDstBase = ((j * iPassWidth) + i) * iChannels;

                    for (k = 0; k < iChannels; k++)
                    {   /* Whole pixels move, one sample at a time, out of   */
                        /* whichever of the two sample arrays is live.       */
                        if (imgIn.IMG_iBitsPerSample == ImageBuffer.IMG_BITS_16)
                        {   /* Sixteen-bit samples. */
                            imgPass.IMG_arrSamples16[iDstBase + k] =
                                imgIn.IMG_arrSamples16[iSrcBase + k];
                        }
                        else
                        {   /* Eight-bit samples, or palette indices. */
                            imgPass.IMG_arrSamples8[iDstBase + k] =
                                imgIn.IMG_arrSamples8[iSrcBase + k];
                        }
                    }
                }
            }

            imgPassOut = imgPass;

            /* A dense image the ordinary filter stage can write. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* PNG_PackScaleFor:                                                  */
        /*                                                                    */
        /* The divisor PNG_PackScanline uses to turn a widened 8-bit sample   */
        /* back into the narrow value the file carries.                       */
        /*                                                                    */
        /* PALETTE INDICES ARE NEVER SCALED, and that is the whole reason     */
        /* this is a function taking the COLOUR TYPE rather than a lookup on  */
        /* the depth alone. A 4-bit grey level of 15 is stored in an          */
        /* ImageBuffer as 255, because the decoder replicated its bits to     */
        /* fill a byte, so writing it back means dividing by 0x11. A 4-bit    */
        /* palette index of 15 is stored as 15, because an index is a NUMBER  */
        /* NAMING AN ENTRY and not a light level -- dividing it would write   */
        /* index 1 and produce an image in the wrong colours. The decoder's   */
        /* PNG_arrDepthScale comment states the same rule from the other      */
        /* side; this is where the encoder obeys it.                          */
        /*                                                                    */
        /* Returning 1 at depth 8 and above keeps the function total rather   */
        /* than partial: those depths do not go through the sub-byte path at  */
        /* all, so the value is unused, and a caller that has to know which   */
        /* depths may safely ask is a caller waiting to get it wrong.         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iBitDepth   : 1, 2, 4, 8 or 16.                                */
        /*     iColorType  : The colour type being written.                   */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int         : The divisor; always at least 1.                  */
        /*--------------------------------------------------------------------*/

        private static int PNG_PackScaleFor(int iBitDepth, int iColorType)
        {
            if (iColorType == PNG_COLOR_PALETTE)
            {   /* An index means itself at every depth. */
                return(1);
            }

            if (iBitDepth >= 8)
            {   /* Nothing was widened, so nothing is narrowed. */
                return(1);
            }

            /* A grey level narrower than a byte: undo the bit replication. */
            return((int)PNG_arrDepthScale[iBitDepth]);
        }

        /*--------------------------------------------------------------------*/
        /* PNG_FilterStepFor:                                                 */
        /*                                                                    */
        /* The byte distance to the neighbouring pixel, which is what all     */
        /* five filters predict from.                                         */
        /*                                                                    */
        /* Three cases and no judgement: one byte below depth 8, because the  */
        /* specification rounds a sub-byte pixel size up to one; the sample   */
        /* count at depth 8; twice the sample count at depth 16.              */
        /*                                                                    */
        /* A FUNCTION RATHER THAN A COMPUTED ARGUMENT, deliberately. This     */
        /* was learned the expensive way, where a caller                      */
        /* passing the step got it wrong twice -- once at a call site and     */
        /* once in a helper's own signature -- because the rule looks obvious */
        /* until a pixel stops being one byte. One place, one answer.         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iBitDepth  : 1, 2, 4, 8 or 16.                                 */
        /*     iChannels  : Samples per pixel.                                */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int        : The filter step in bytes, always at least 1.      */
        /*--------------------------------------------------------------------*/

        private static int PNG_FilterStepFor(int iBitDepth, int iChannels)
        {
            if (iBitDepth < 8)
            {   /* Samples share a byte, so a pixel is a fraction of one and   */
                /* the step rounds up.                                         */
                return(1);
            }

            if (iBitDepth == 16)
            {   /* Two bytes per sample. */
                return(iChannels * 2);
            }

            /* One byte per sample. */
            return(iChannels);
        }

        /*--------------------------------------------------------------------*/
        /* PNG_PackScanline:                                                  */
        /*                                                                    */
        /* Turns one row of an ImageBuffer into the bytes a PNG scanline      */
        /* carries at the depth being written.                                */
        /*                                                                    */
        /* The inverse of the decoder's unpacking: PNG_CombineWideRow at      */
        /* depth 16, a straight copy at 8, PNG_ExpandPackedRow below that.    */
        /*                                                                    */
        /* SUB-BYTE SAMPLES ARE PACKED MOST SIGNIFICANT FIRST, so the         */
        /* leftmost pixel of a row occupies the HIGH bits of the first byte   */
        /* and the shift counts down. Getting that backwards produces an      */
        /* image whose pixels are reversed within each group -- plausible     */
        /* enough at a glance to survive a careless test, which is why        */
        /* testing packs independently and compares.                          */
        /*                                                                    */
        /* THE ROW IS CLEARED FIRST, which matters only for the last byte of  */
        /* a row whose width does not fill it. Those trailing bits are real:  */
        /* they are written to the file and covered by the filters, and       */
        /* leaving whatever the previous row put there would make the output  */
        /* depend on rows above it for no reason.                             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     imgIn           : The image being written.                     */
        /*     iRow            : Which row to pack.                           */
        /*     iBitDepth       : The depth being written.                     */
        /*     iSamplesPerRow  : Width times channels.                        */
        /*     iSampleScale    : The divisor that turns a widened 8-bit       */
        /*                       sample back into its narrow form, or 1 when  */
        /*                       the samples are already what the file wants. */
        /*                       From PNG_PackScaleFor; passed in rather than */
        /*                       recomputed per row.                          */
        /*     arrRowIo        : Receives the packed bytes; cleared first.    */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void            : The row is written into arrRowIo.            */
        /*--------------------------------------------------------------------*/

        private static void PNG_PackScanline(ImageBuffer imgIn, int iRow, int iBitDepth,
                                             int iSamplesPerRow, int iSampleScale,
                                             byte[] arrRowIo)
        {
            int iSrcBase;
            int iPerByte;
            int iValue;
            int iShift;
            int i;

            iSrcBase = iRow * iSamplesPerRow;

            if (iBitDepth == 16)
            {   /* Two bytes per sample, high byte first. */
                for (i = 0; i < iSamplesPerRow; i++)
                {   /* The source may be 8-bit, in which case each sample is   */
                    /* widened by bit replication on the way out.              */
                    if (imgIn.IMG_iBitsPerSample == ImageBuffer.IMG_BITS_16)
                    {   /* Already 16-bit; write it as it stands. */
                        iValue = (int)imgIn.IMG_arrSamples16[iSrcBase + i];
                    }
                    else
                    {   /* Widen 8 bits to 16 by copying the byte into both    */
                        /* halves, which is what multiplying by 257 does and   */
                        /* is exactly reversible.                              */
                        iValue = ((int)imgIn.IMG_arrSamples8[iSrcBase + i]) * 257;
                    }

                    arrRowIo[i * 2]       = (byte)((iValue >> 8) & 0xFF);
                    arrRowIo[(i * 2) + 1] = (byte)(iValue & 0xFF);
                }

                return;
            }

            if (iBitDepth == 8)
            {   /* One byte per sample: the samples ARE the scanline. */
                Array.Copy(imgIn.IMG_arrSamples8, iSrcBase, arrRowIo, 0, iSamplesPerRow);
                return;
            }

            /*----------------------------------------------------------------*/
            /* Below eight bits. For a grey level the caller has already      */
            /* proved every sample is an exact multiple of the replication    */
            /* step, so dividing by it recovers the narrow value with nothing */
            /* lost. For a palette index the scale is 1 and the division is a */
            /* no-op, which is the whole reason it arrives as an argument --  */
            /* see PNG_PackScaleFor.                                          */
            /*----------------------------------------------------------------*/

            iPerByte = 8 / iBitDepth;

            for (i = 0; i < arrRowIo.Length; i++)
            {   /* Clear, so the trailing bits of a partial last byte are   */
                /* zero rather than left over from the previous row.        */
                arrRowIo[i] = 0;
            }

            for (i = 0; i < iSamplesPerRow; i++)
            {   /* One sample per iteration, placed from the top of its byte   */
                /* downward.                                                   */
                iValue = ((int)imgIn.IMG_arrSamples8[iSrcBase + i]) / iSampleScale;
                iShift = 8 - iBitDepth - ((i % iPerByte) * iBitDepth);

                arrRowIo[i / iPerByte] = (byte)(arrRowIo[i / iPerByte] |
                                                ((iValue << iShift) & 0xFF));
            }
        }

        /*--------------------------------------------------------------------*/
        /* PNG_FilterScanlineInto:                                            */
        /*                                                                    */
        /* Applies one filter to one scanline, in the ENCODING direction.     */
        /*                                                                    */
        /* Transcoded from lodepng.cpp's filterScanline, taking its           */
        /* has-a-previous-row branches only, because this caller always       */
        /* supplies one -- zero-filled for the first row, which produces the  */
        /* same bytes as the source's no-previous-row branches.               */
        /*                                                                    */
        /* Every residual is taken modulo 256. The subtraction is ALLOWED to  */
        /* wrap and the decoder's addition wraps back, which is why a sample  */
        /* of 2 predicted as 250 stores 8 rather than saturating.             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     arrSrc       : The image's samples.                            */
        /*     iSrcOffset   : Index of this scanline's first sample.          */
        /*     arrPrev      : The previous scanline's samples, zero-filled    */
        /*                    for row 0.                                      */
        /*     iLineBytes   : Bytes in a scanline.                            */
        /*     iByteWidth   : Byte distance to the pixel on the left.         */
        /*     iFilter      : Which filter to apply, 0 to 4.                  */
        /*     arrDestIo    : Receives the residuals.                         */
        /*     iDestOffset  : Where in arrDestIo to write them.               */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void         : The residuals are written into arrDestIo.       */
        /*--------------------------------------------------------------------*/

        private static void PNG_FilterScanlineInto(byte[] arrSrc, int iSrcOffset, byte[] arrPrev,
                                                   int iLineBytes, int iByteWidth, int iFilter,
                                                   byte[] arrDestIo, int iDestOffset)
        {
            int i;

            if (iFilter == PNG_FILTER_NONE)
            {   /* No prediction: the samples stand as they are. */
                Array.Copy(arrSrc, iSrcOffset, arrDestIo, iDestOffset, iLineBytes);
            }
            else if (iFilter == PNG_FILTER_SUB)
            {   /* Predict from the pixel to the left; the first pixel of the   */
                /* row has nothing there, so its bytes stand as they are.       */
                Array.Copy(arrSrc, iSrcOffset, arrDestIo, iDestOffset, iByteWidth);

                for (i = iByteWidth; i < iLineBytes; i++)
                {   /* Subtract the byte one whole pixel back. */
                    arrDestIo[iDestOffset + i] = (byte)((arrSrc[iSrcOffset + i] -
                        arrSrc[iSrcOffset + i - iByteWidth]) & 0xFF);
                }
            }
            else if (iFilter == PNG_FILTER_UP)
            {   /* Predict from the byte directly above. */
                for (i = 0; i < iLineBytes; i++)
                {   /* Subtract the row above, which is zeroes on row 0. */
                    arrDestIo[iDestOffset + i] = (byte)((arrSrc[iSrcOffset + i] -
                        arrPrev[i]) & 0xFF);
                }
            }
            else if (iFilter == PNG_FILTER_AVERAGE)
            {   /* Predict from the mean of left and above, rounded down. */
                for (i = 0; i < iByteWidth; i++)
                {   /* First pixel: nothing to the left, so the mean is of   */
                    /* the byte above and zero.                              */
                    arrDestIo[iDestOffset + i] = (byte)((arrSrc[iSrcOffset + i] -
                        (arrPrev[i] >> 1)) & 0xFF);
                }

                for (i = iByteWidth; i < iLineBytes; i++)
                {   /* Both neighbours present. The sum is taken in int       */
                    /* before the shift: two bytes can total past 255 and a   */
                    /* byte-width add would wrap before the halving.          */
                    arrDestIo[iDestOffset + i] = (byte)((arrSrc[iSrcOffset + i] -
                        (((int)arrSrc[iSrcOffset + i - iByteWidth] + (int)arrPrev[i]) >> 1)) & 0xFF);
                }
            }
            else
            {   /* Paeth: predict with whichever neighbour is closest to   */
                /* left + above - above-left.                              */
                for (i = 0; i < iByteWidth; i++)
                {   /* First pixel: left and above-left are zero, and   */
                    /* Paeth(0, b, 0) is b, so this reduces to Up.      */
                    arrDestIo[iDestOffset + i] = (byte)((arrSrc[iSrcOffset + i] -
                        arrPrev[i]) & 0xFF);
                }

                for (i = iByteWidth; i < iLineBytes; i++)
                {   /* All three neighbours present. */
                    arrDestIo[iDestOffset + i] = (byte)((arrSrc[iSrcOffset + i] -
                        PNG_PaethPredictor((int)arrSrc[iSrcOffset + i - iByteWidth],
                                           (int)arrPrev[i],
                                           (int)arrPrev[i - iByteWidth])) & 0xFF);
                }
            }
        }

        /*--------------------------------------------------------------------*/
        /* PNG_ScanlineCost:                                                  */
        /*                                                                    */
        /* How expensive a filtered scanline looks, by lodepng's MINSUM       */
        /* measure: the total absolute deviation of its bytes.                */
        /*                                                                    */
        /* THE TWO CASES ARE NOT AN OVERSIGHT. For the four differencing      */
        /* filters a residual byte is a signed value in disguise, so 250      */
        /* means -6 and costs 5 rather than 250; for filter None the byte is  */
        /* the sample itself and means what it says. Transcoded exactly,      */
        /* including the source's 255-minus rather than 256-minus, which      */
        /* undercounts a residual of 128 by one and which lodepng evidently   */
        /* considers close enough for a heuristic. Changing it would make     */
        /* this file harder to diff against its source for no gain in         */
        /* correctness, since any choice here still produces a valid file.    */
        /*                                                                    */
        /* Accumulated in long: a wide row of large values overflows an int,  */
        /* and an overflowed cost would look like the cheapest option and be  */
        /* chosen every time.                                                 */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     arrData     : Buffer holding the filtered row.                 */
        /*     iOffset     : Index of the row's first byte.                   */
        /*     iLineBytes  : Bytes in the row.                                */
        /*     iFilter     : Which filter produced it, 0 to 4.                */
        /*                                                                    */
        /* Returns:                                                           */
        /*     long        : The row's cost; smaller is preferred.            */
        /*--------------------------------------------------------------------*/

        private static long PNG_ScanlineCost(byte[] arrData, int iOffset, int iLineBytes,
                                             int iFilter)
        {
            long lSum;
            int  iByte;
            int  i;

            lSum = 0L;

            if (iFilter == PNG_FILTER_NONE)
            {   /* Unfiltered bytes are magnitudes already. */
                for (i = 0; i < iLineBytes; i++)
                {   /* Straight sum. */
                    lSum = lSum + (long)arrData[iOffset + i];
                }

                /* The cost of leaving the row alone. */
                return(lSum);
            }

            for (i = 0; i < iLineBytes; i++)
            {   /* Residuals are signed: 0 to 127 are themselves, 128 to 255   */
                /* are negative and cost their distance from 255.              */
                iByte = (int)arrData[iOffset + i];

                if (iByte < 128)
                {   /* A small positive difference. */
                    lSum = lSum + (long)iByte;
                }
                else
                {   /* A negative difference, measured as the source does. */
                    lSum = lSum + (long)(255 - iByte);
                }
            }

            /* The cost of this filter on this row. */
            return(lSum);
        }
    }
}
