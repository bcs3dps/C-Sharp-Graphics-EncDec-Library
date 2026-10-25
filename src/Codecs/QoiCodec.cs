/*----------------------------------------------------------------------------*/
/* SPDX-License-Identifier: MIT OR GPL-2.0-or-later                           */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* Dual-licensed at the recipient's choice -- see LICENSE in this folder for  */
/* the full terms of both licences.  GPL-2.0 full text in GPL-2.0.txt.        */
/*----------------------------------------------------------------------------*/
/* Transcoded from qoi.h (phoboslab/qoi, commit 97bacc86), its qoi_encode and */
/* qoi_decode functions and the format specification in its header comment.   */
/* Structure and variable roles are kept -- the 64-entry running index, the   */
/* previous-pixel state, the run counter, the order the encoder tries each    */
/* chunk type -- so this file can be diffed against its source when it        */
/* misbehaves.  qoi.h is MIT (Copyright (c) 2021 Dominic Szablewski), which   */
/* satisfies both arms of this file's dual licence.                           */
/*----------------------------------------------------------------------------*/
/* QoiCodec.cs -- the Quite OK Image format, both directions          (QOI_)  */
/*----------------------------------------------------------------------------*/
/* QOI is a lossless RGB and RGBA format designed to be trivial to implement: */
/* one pass, no entropy coding, no tables to build.  Its author's own note is */
/* the reason it is the FIRST codec in this library rather than PNG -- the    */
/* format is "extremely simple, which helps a lot when porting to other       */
/* languages", so it proves the public API shape before anything expensive is */
/* built on top of it.                                                        */
/*                                                                            */
/* THE FORMAT, in full, because it is short enough to state:                  */
/*                                                                            */
/*   14-byte header:  "qoif", width (BE u32), height (BE u32),                */
/*                    channels (1 byte, 3 or 4), colorspace (1 byte, 0 or 1)  */
/*   then chunks:     one of the six operations below, byte-aligned           */
/*   8-byte trailer:  00 00 00 00 00 00 00 01                                 */
/*                                                                            */
/* Both encoder and decoder start from the previous pixel {0, 0, 0, 255} and  */
/* maintain a 64-entry array of recently seen pixels, indexed by              */
/*                                                                            */
/*     (r * 3 + g * 5 + b * 7 + a * 11) % 64                                  */
/*                                                                            */
/* The six chunk types, and the order the encoder tries them, which is also   */
/* the order of preference by size:                                           */
/*                                                                            */
/*   QOI_OP_RUN    11xxxxxx            repeat the previous pixel 1..62 times  */
/*   QOI_OP_INDEX  00xxxxxx            a pixel already in the running index   */
/*   QOI_OP_DIFF   01rrggbb            per-channel delta of -2..1, bias 2     */
/*   QOI_OP_LUMA   10gggggg rrrrbbbb   green delta -32..31, red and blue      */
/*                                     deltas relative to it, -8..7           */
/*   QOI_OP_RGB    11111110 r g b      literal colour, alpha unchanged        */
/*   QOI_OP_RGBA   11111111 r g b a    literal colour and alpha               */
/*                                                                            */
/* THE 8-BIT TAGS TAKE PRECEDENCE.  0xFE and 0xFF both begin 11, which is     */
/* also QOI_OP_RUN's tag, so a decoder MUST test the two full-byte tags       */
/* before masking down to two bits.  Testing in the other order silently      */
/* decodes every literal pixel as a 62- or 63-length run, which is why the    */
/* run length is specified as 1..62 rather than 1..64 and why the tests below */
/* are ordered as they are.                                                   */
/*                                                                            */
/* ALL CHANNEL ARITHMETIC WRAPS AT 8 BITS, and this is the single easiest     */
/* thing to get wrong when moving the reference out of C.  The reference      */
/* stores channels in unsigned char, so "1 - 2" is 255 and "255 + 1" is 0;    */
/* and it computes the encoder's deltas in SIGNED char, so a red channel      */
/* going from 255 to 0 is a delta of +1, not -255.  C# has no such implicit   */
/* narrowing, so every one of those places is an explicit mask or an explicit */
/* cast through sbyte below.  Getting either wrong produces a codec that      */
/* works on most images and corrupts the ones that wrap.                      */
/*                                                                            */
/* THREE DELIBERATE DEVIATIONS FROM THE REFERENCE.  The first two are in the  */
/* DECODER and both move toward strictness:                                   */
/*                                                                            */
/*   1. A TRUNCATED FILE IS REFUSED.  The reference decoder, on running out   */
/*      of chunk bytes early, quietly fills the remaining pixels with the     */
/*      last colour it saw and returns success.  This library reports         */
/*      malformed data instead, per the failure contract every entry point    */
/*      here obeys -- a half image that claims to be whole is worse than an   */
/*      error, because nothing downstream can tell.                           */
/*   2. THE END MARKER IS VERIFIED.  The reference ignores the trailing eight */
/*      bytes entirely.  The specification mandates them, they cost eight     */
/*      byte comparisons, and they are the cheapest possible check that a     */
/*      file arrived complete.                                                */
/*                                                                            */
/* Neither of those can reject a file the reference encoder produced.         */
/*                                                                            */
/* The third is in the ENCODER and it changes THE BYTES THIS LIBRARY WRITES:  */
/*                                                                            */
/*   3. A RUN'S PIXEL ENTERS THE RUNNING INDEX.  qoi.h's encoder adds a       */
/*      pixel to the index only in its non-run branch, while its own DECODER  */
/*      indexes the pixel of every chunk INCLUDING runs.  The two halves of   */
/*      the reference therefore disagree about what the index holds after a   */
/*      run, and the encoder is the half that loses by it: it cannot emit an  */
/*      INDEX chunk for a colour its own decoder would have remembered.       */
/*      This encoder indexes on the run path too, which makes the two halves  */
/*      symmetric and finds INDEX opportunities qoi.h leaves on the table.    */
/*                                                                            */
/* THE OUTPUT STAYS VALID QOI AND GETS SMALLER.  Nothing about the format     */
/* changes: a QOI_OP_INDEX naming a run-indexed colour is exactly what any    */
/* conformant decoder expects -- qoi.h's included, since indexing runs is     */
/* what its decoder already does.  The reference images are the proof: the    */
/* shipped edgecase.qoi was written by an encoder carrying this variant, and  */
/* with the change ALL EIGHT of them re-encode BYTE-IDENTICALLY where before  */
/* only seven did.                                                            */
/*                                                                            */
/* Section 6 rule 5 says transcode rather than paraphrase, so a divergence    */
/* from the declared lineage is recorded here rather than absorbed quietly.   */
/* An earlier draft chose the opposite reading -- stay bit-exact to qoi.h     */
/* and check edgecase for semantic equivalence only.  Byte identity with the  */
/* shipped reference images was judged the more useful guarantee.             */
/*                                                                            */
/* PORTABILITY: pure array and integer work, no platform surface.             */
/*----------------------------------------------------------------------------*/

using System;

namespace GraphicsEncDec
{
    /*------------------------------------------------------------------------*/
    /* QoiEncodeOptions:                                                      */
    /* Encoder settings for QOI.  There is exactly one, because the format    */
    /* has no quality, no filter choice and no compression level -- it is     */
    /* lossless and single-pass, so the only thing left to say about an image */
    /* is how its colour values should be interpreted.                        */
    /*------------------------------------------------------------------------*/

    public sealed class QoiEncodeOptions
    {
        /*--------------------------------------------------------------------*/
        /* The colorspace byte written into the header.  Purely advisory:     */
        /* QOI stores whatever values it is given either way, and no decoder  */
        /* converts anything on the strength of it.  It records the author's  */
        /* intent for a consumer that cares.                                  */
        /*--------------------------------------------------------------------*/

        public int QOIOPT_iColorspace;                   // QOI_SRGB_LINEAR_ALPHA or QOI_LINEAR
    }

    /*------------------------------------------------------------------------*/
    /* QoiCodec:                                                              */
    /* Static-only.  Decodes and encodes the Quite OK Image format.           */
    /*------------------------------------------------------------------------*/

    public static class QoiCodec
    {
        /*--------------------------------------------------------------------*/
        /* Colorspace values, as written into the header's fifteenth byte.    */
        /*--------------------------------------------------------------------*/

        public const int QOI_SRGB_LINEAR_ALPHA = 0;      // sRGB colour channels, linear alpha
        public const int QOI_LINEAR            = 1;      // all channels linear

        /*--------------------------------------------------------------------*/
        /* Chunk tags.  The two-bit tags occupy the top two bits; the two     */
        /* eight-bit tags are complete byte values that collide with the      */
        /* QOI_OP_RUN two-bit tag and must therefore be tested first.         */
        /*--------------------------------------------------------------------*/

        private const int QOI_OP_INDEX = 0x00;           // 00xxxxxx  index into the running array
        private const int QOI_OP_DIFF  = 0x40;           // 01rrggbb  small per-channel deltas
        private const int QOI_OP_LUMA  = 0x80;           // 10gggggg  green-relative deltas
        private const int QOI_OP_RUN   = 0xC0;           // 11xxxxxx  repeat the previous pixel
        private const int QOI_OP_RGB   = 0xFE;           // 11111110  literal r, g, b
        private const int QOI_OP_RGBA  = 0xFF;           // 11111111  literal r, g, b, a
        private const int QOI_MASK_2   = 0xC0;           // isolates the two-bit tag

        /*--------------------------------------------------------------------*/
        /* Structural constants.                                              */
        /*--------------------------------------------------------------------*/

        private const int QOI_HEADER_SIZE  = 14;         // magic, width, height, channels, colorspace
        private const int QOI_PADDING_SIZE = 8;          // the mandatory end marker
        private const int QOI_INDEX_SIZE   = 64;         // entries in the running pixel array
        private const int QOI_RUN_MAX      = 62;         // longest run a single chunk can express

        /*--------------------------------------------------------------------*/
        /* The reference's own pixel-count ceiling, kept rather than          */
        /* reinvented so that a file this library refuses is a file the       */
        /* reference implementation would also refuse.  400 million pixels is */
        /* about 20000 by 20000, far beyond anything real and comfortably     */
        /* short of arithmetic trouble.                                       */
        /*--------------------------------------------------------------------*/

        private const int QOI_PIXELS_MAX = 400000000;

        /*--------------------------------------------------------------------*/
        /* Hash multipliers, from the specification's index formula.  They    */
        /* are the first four odd primes, chosen so that swapping two channel */
        /* values changes the index -- which a sum of equal weights would     */
        /* not.                                                               */
        /*--------------------------------------------------------------------*/

        private const int QOI_HASH_R = 3;                // red weight
        private const int QOI_HASH_G = 5;                // green weight
        private const int QOI_HASH_B = 7;                // blue weight
        private const int QOI_HASH_A = 11;               // alpha weight

        /*--------------------------------------------------------------------*/
        /* The mandatory eight-byte end marker: seven zeroes and a one.       */
        /*--------------------------------------------------------------------*/

        private static readonly byte[] QOI_arrPadding =
            new byte[] { 0, 0, 0, 0, 0, 0, 0, 1 };

        /*--------------------------------------------------------------------*/
        /* QOI_DefaultEncodeOptions:                                          */
        /*                                                                    */
        /* Returns the settings used when a caller passes null options.       */
        /* A static factory rather than a second constructor, because this    */
        /* library never gives two callables the same name.                   */
        /*                                                                    */
        /* sRGB with linear alpha is the default because it is what an image  */
        /* coming from any ordinary source actually is, and because it is the */
        /* value the reference tools write.                                   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     QoiEncodeOptions  : A new options object with defaults set.    */
        /*--------------------------------------------------------------------*/

        public static QoiEncodeOptions QOI_DefaultEncodeOptions()
        {
            QoiEncodeOptions optNew;

            optNew = new QoiEncodeOptions();
            optNew.QOIOPT_iColorspace = QOI_SRGB_LINEAR_ALPHA;

            /* Defaults matching what the reference tools emit. */
            return(optNew);
        }

        /*--------------------------------------------------------------------*/
        /* QOI_TryDecode:                                                     */
        /*                                                                    */
        /* Decodes a QOI image into an 8-bit buffer with the channel count    */
        /* the file itself declares -- 3 for RGB, 4 for RGBA.                 */
        /*                                                                    */
        /* Returning the file's own channel count rather than always four is  */
        /* deliberate: it preserves the information that the image had no     */
        /* alpha, which a caller may care about, and                          */
        /* IMG_TryConvertChannels is right there for a caller that wants a    */
        /* specific shape.  The reference offers a channels argument for the  */
        /* same purpose; here that concern lives in one shared place instead  */
        /* of in every codec.                                                 */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     arrData  : Buffer holding the QOI file.  Must not be null.     */
        /*     iOffset  : Index of the first byte of the image.               */
        /*     iLength  : Byte count of the whole file, trailer included.     */
        /*     imgOut   : Receives the decoded image; null on failure.        */
        /*     sReason  : Receives "" on success, or why the file was         */
        /*                rejected.                                           */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool     : true when imgOut holds the decoded image.           */
        /*--------------------------------------------------------------------*/

        public static bool QOI_TryDecode(byte[] arrData, int iOffset, int iLength,
                                         out ImageBuffer imgOut, out string sReason)
        {
            ImageBuffer imgNew;
            uint[]      arrIndex;
            uint        uWidth;
            uint        uHeight;
            int         iChannels;
            int         iColorspace;
            int         iChunksEnd;
            int         iPos;
            int         iSampleCount;
            int         iSamplePos;
            int         iRun;
            int         iByte1;
            int         iByte2;
            int         iGreenDelta;
            int         iRed;
            int         iGreen;
            int         iBlue;
            int         iAlpha;
            int         i;

            imgOut  = null;
            sReason = "";

            if (arrData == null)
            {   /* Missing argument, not malformed image data. */
                throw new ArgumentNullException("arrData");
            }

            if (!ImageDecoderDispatch.IMGDEC_TryValidateWindow(arrData, iOffset, iLength, out sReason))
            {   /* The window itself is unusable; pass its reason straight up. */
                return(false);
            }

            if (iLength < (QOI_HEADER_SIZE + QOI_PADDING_SIZE))
            {   /* Too short to hold a header and an end marker, never mind pixels. */
                sReason = "QOI: file is " + iLength.ToString() + " bytes, shorter than the 22-byte minimum";
                return(false);
            }

            /* Header.  The magic is checked byte by byte rather than through   */
            /* the sniffer, so this function is complete on its own and can     */
            /* be called directly without IMGDEC_TryDecodeAny.                  */
            if (arrData[iOffset] != (byte)'q' || arrData[iOffset + 1] != (byte)'o' ||
                arrData[iOffset + 2] != (byte)'i' || arrData[iOffset + 3] != (byte)'f')
            {   /* Not a QOI file at all. */
                sReason = "QOI: missing 'qoif' magic";
                return(false);
            }

            uWidth = ((uint)arrData[iOffset + 4] << 24) | ((uint)arrData[iOffset + 5] << 16) |
                     ((uint)arrData[iOffset + 6] <<  8) |  (uint)arrData[iOffset + 7];

            uHeight = ((uint)arrData[iOffset +  8] << 24) | ((uint)arrData[iOffset +  9] << 16) |
                      ((uint)arrData[iOffset + 10] <<  8) |  (uint)arrData[iOffset + 11];

            iChannels   = (int)arrData[iOffset + 12];
            iColorspace = (int)arrData[iOffset + 13];

            if (uWidth == 0u || uHeight == 0u)
            {   /* A zero dimension means there are no pixels to decode. */
                sReason = "QOI: header declares " + uWidth.ToString() + "x" + uHeight.ToString();
                return(false);
            }

            if (iChannels < 3 || iChannels > 4)
            {   /* The format defines only 3 and 4. */
                sReason = "QOI: channel count " + iChannels.ToString() + " is not 3 or 4";
                return(false);
            }

            if (iColorspace > 1)
            {   /* Only 0 and 1 are defined; anything else means a corrupt header. */
                sReason = "QOI: colorspace " + iColorspace.ToString() + " is not 0 or 1";
                return(false);
            }

            /* The reference's pixel ceiling, expressed as the reference        */
            /* expresses it -- as a division rather than a multiplication, so   */
            /* the check itself cannot overflow.                                */
            if (uHeight >= ((uint)QOI_PIXELS_MAX / uWidth))
            {   /* More pixels than the format's own limit allows. */
                sReason = "QOI: " + uWidth.ToString() + "x" + uHeight.ToString() +
                          " exceeds the " + QOI_PIXELS_MAX.ToString() + " pixel limit";
                return(false);
            }

            /* THE END MARKER.  Verified here, before any decoding, so a      */
            /* truncated file is rejected immediately rather than after the   */
            /* work.  The reference skips this check entirely.                */
            for (i = 0; i < QOI_PADDING_SIZE; i++)
            {   /* Seven zero bytes then a single one, per the specification. */
                if (arrData[iOffset + iLength - QOI_PADDING_SIZE + i] != QOI_arrPadding[i])
                {   /* Marker wrong or absent: the file is truncated or damaged. */
                    sReason = "QOI: end marker is missing or damaged at byte " +
                              (iLength - QOI_PADDING_SIZE + i).ToString();
                    return(false);
                }
            }

            if (!ImageBuffer.IMG_TryAllocate((int)uWidth, (int)uHeight, iChannels,
                                             ImageBuffer.IMG_BITS_8, out imgNew, out sReason))
            {   /* Geometry rejected by the allocator; its reason is the right one. */
                return(false);
            }

            arrIndex     = new uint[QOI_INDEX_SIZE];
            iSampleCount = imgNew.IMG_arrSamples8.Length;
            iChunksEnd   = iOffset + iLength - QOI_PADDING_SIZE;
            iPos         = iOffset + QOI_HEADER_SIZE;
            iRun         = 0;

            /* Both sides start from opaque black, per the specification. */
            iRed   = 0;
            iGreen = 0;
            iBlue  = 0;
            iAlpha = 255;

            for (iSamplePos = 0; iSamplePos < iSampleCount; iSamplePos += iChannels)
            {   /* One pixel per iteration, emitting into the buffer at the end. */
                if (iRun > 0)
                {   /* Still inside a run: repeat the previous pixel, consuming no bytes. */
                    iRun = iRun - 1;
                }
                else
                {   /* Need a new chunk.  Unlike the reference, running out     */
                    /* of bytes here is an ERROR rather than a signal to fill   */
                    /* the rest of the image with the last colour seen.         */
                    if (iPos >= iChunksEnd)
                    {   /* Chunk data exhausted before the pixel count was met. */
                        sReason = "QOI: chunk data ran out after " +
                                  (iSamplePos / iChannels).ToString() + " of " +
                                  (iSampleCount / iChannels).ToString() + " pixels";
                        return(false);
                    }

                    iByte1 = (int)arrData[iPos];
                    iPos   = iPos + 1;

                    if (iByte1 == QOI_OP_RGB)
                    {   /* Literal colour, alpha carried over from the previous pixel. */
                        if ((iPos + 3) > iChunksEnd)
                        {   /* Chunk claims three more bytes than the file holds. */
                            sReason = "QOI: truncated QOI_OP_RGB chunk";
                            return(false);
                        }

                        iRed   = (int)arrData[iPos];
                        iGreen = (int)arrData[iPos + 1];
                        iBlue  = (int)arrData[iPos + 2];
                        iPos   = iPos + 3;
                    }
                    else if (iByte1 == QOI_OP_RGBA)
                    {   /* Literal colour and alpha. */
                        if ((iPos + 4) > iChunksEnd)
                        {   /* Chunk claims four more bytes than the file holds. */
                            sReason = "QOI: truncated QOI_OP_RGBA chunk";
                            return(false);
                        }

                        iRed   = (int)arrData[iPos];
                        iGreen = (int)arrData[iPos + 1];
                        iBlue  = (int)arrData[iPos + 2];
                        iAlpha = (int)arrData[iPos + 3];
                        iPos   = iPos + 4;
                    }
                    else if ((iByte1 & QOI_MASK_2) == QOI_OP_INDEX)
                    {   /* A pixel already in the running array; the whole   */
                        /* colour including alpha comes from there.          */
                        iRed   = (int)((arrIndex[iByte1] >> 24) & 0xFFu);
                        iGreen = (int)((arrIndex[iByte1] >> 16) & 0xFFu);
                        iBlue  = (int)((arrIndex[iByte1] >>  8) & 0xFFu);
                        iAlpha = (int)(arrIndex[iByte1] & 0xFFu);
                    }
                    else if ((iByte1 & QOI_MASK_2) == QOI_OP_DIFF)
                    {   /* Three two-bit deltas, each biased by 2, alpha      */
                        /* unchanged.  The mask after each add is the 8-bit   */
                        /* wraparound the specification requires.             */
                        iRed   = (iRed   + (((iByte1 >> 4) & 0x03) - 2)) & 0xFF;
                        iGreen = (iGreen + (((iByte1 >> 2) & 0x03) - 2)) & 0xFF;
                        iBlue  = (iBlue  + ((iByte1        & 0x03) - 2)) & 0xFF;
                    }
                    else if ((iByte1 & QOI_MASK_2) == QOI_OP_LUMA)
                    {   /* Green carries the general direction of change in     */
                        /* six bits; red and blue are stored relative to it     */
                        /* in four bits each, which is why they add the green   */
                        /* delta back in.                                       */
                        if (iPos >= iChunksEnd)
                        {   /* The second byte of the chunk is missing. */
                            sReason = "QOI: truncated QOI_OP_LUMA chunk";
                            return(false);
                        }

                        iByte2 = (int)arrData[iPos];
                        iPos   = iPos + 1;

                        iGreenDelta = (iByte1 & 0x3F) - 32;

                        iRed   = (iRed   + iGreenDelta - 8 + ((iByte2 >> 4) & 0x0F)) & 0xFF;
                        iGreen = (iGreen + iGreenDelta) & 0xFF;
                        iBlue  = (iBlue  + iGreenDelta - 8 + (iByte2 & 0x0F)) & 0xFF;
                    }
                    else
                    {   /* The only tag left is QOI_OP_RUN.  The stored value   */
                        /* is biased by -1, and this pixel is the first of      */
                        /* the run, so the counter holds the REMAINDER.         */
                        iRun = iByte1 & 0x3F;
                    }

                    /* Every decoded pixel enters the running index, exactly   */
                    /* as the encoder does it -- including pixels that came    */
                    /* FROM the index, which is harmless and keeps the two     */
                    /* sides in step.                                          */
                    arrIndex[QOI_ColorHash(iRed, iGreen, iBlue, iAlpha)] =
                        QOI_Pack(iRed, iGreen, iBlue, iAlpha);
                }

                imgNew.IMG_arrSamples8[iSamplePos]     = (byte)iRed;
                imgNew.IMG_arrSamples8[iSamplePos + 1] = (byte)iGreen;
                imgNew.IMG_arrSamples8[iSamplePos + 2] = (byte)iBlue;

                if (iChannels == 4)
                {   /* Four-channel image: the alpha goes out as well. */
                    imgNew.IMG_arrSamples8[iSamplePos + 3] = (byte)iAlpha;
                }
            }

            imgOut = imgNew;

            /* Header valid, end marker present, every pixel accounted for. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* QOI_TryEncode:                                                     */
        /*                                                                    */
        /* Encodes an 8-bit buffer into a complete QOI file.                  */
        /*                                                                    */
        /* ACCEPTS 1, 2, 3 OR 4 CHANNELS, converting the first two up to RGB  */
        /* and RGBA respectively.  REFUSES 16-BIT.  The line between those    */
        /* two answers is whether the conversion loses anything: widening     */
        /* grayscale to RGB is exact and nobody would want it refused, while  */
        /* narrowing 16-bit samples to 8 throws away half of every value and  */
        /* is a decision the caller must make deliberately rather than have   */
        /* made for them silently.                                            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     imgIn    : Source image, 8-bit, 1 to 4 channels.               */
        /*     optIn    : Encoder settings.  null means the defaults.         */
        /*     arrOut   : Receives the complete QOI file; null on failure.    */
        /*     sReason  : Receives "" on success, or why encoding was         */
        /*                refused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool     : true when arrOut holds a complete QOI file.         */
        /*--------------------------------------------------------------------*/

        public static bool QOI_TryEncode(ImageBuffer imgIn, QoiEncodeOptions optIn,
                                         out byte[] arrOut, out string sReason)
        {
            QoiEncodeOptions optUse;
            ImageBuffer      imgUse;
            ByteWriter       bwOut;
            uint[]           arrIndex;
            uint             uPixel;
            uint             uPrevPixel;
            int              iChannels;
            int              iSampleCount;
            int              iSamplePos;
            int              iSampleEnd;
            int              iRun;
            int              iIndexPos;
            int              iRed;
            int              iGreen;
            int              iBlue;
            int              iAlpha;
            int              iPrevRed;
            int              iPrevGreen;
            int              iPrevBlue;
            int              iPrevAlpha;
            int              iDeltaR;
            int              iDeltaG;
            int              iDeltaB;
            int              iLumaR;
            int              iLumaB;
            int              iMaxSize;
            int              i;

            arrOut  = null;
            sReason = "";

            if (!ImageBuffer.IMG_TryValidate(imgIn, out sReason))
            {   /* Source is malformed; the validator's reason is the right one. */
                return(false);
            }

            if (imgIn.IMG_iBitsPerSample != ImageBuffer.IMG_BITS_8)
            {   /* QOI has no 16-bit form, and narrowing is lossy, so the caller must choose. */
                sReason = "QOI: the format stores only 8-bit samples; convert the 16-bit source first";
                return(false);
            }

            if (optIn == null)
            {   /* No options supplied, which is legal and means the defaults. */
                optUse = QOI_DefaultEncodeOptions();
            }
            else
            {   /* Caller supplied settings; use them as given. */
                optUse = optIn;
            }

            if (optUse.QOIOPT_iColorspace != QOI_SRGB_LINEAR_ALPHA &&
                optUse.QOIOPT_iColorspace != QOI_LINEAR)
            {   /* Only two values are defined, and writing a third makes an unreadable header. */
                sReason = "QOI: colorspace " + optUse.QOIOPT_iColorspace.ToString() + " is not 0 or 1";
                return(false);
            }

            /* Widen grayscale to a shape the format can express.  Lossless   */
            /* in both cases: 1 becomes RGB, 2 becomes RGBA.                  */
            if (imgIn.IMG_iChannels == ImageBuffer.IMG_CHANNELS_GRAY)
            {   /* Grayscale to RGB. */
                if (!ImageBuffer.IMG_TryConvertChannels(imgIn, ImageBuffer.IMG_CHANNELS_RGB,
                                                        out imgUse, out sReason))
                {   /* Conversion failed; its reason is the right one to report. */
                    return(false);
                }
            }
            else if (imgIn.IMG_iChannels == ImageBuffer.IMG_CHANNELS_GRAY_ALPHA)
            {   /* Grayscale with alpha to RGBA. */
                if (!ImageBuffer.IMG_TryConvertChannels(imgIn, ImageBuffer.IMG_CHANNELS_RGBA,
                                                        out imgUse, out sReason))
                {   /* Conversion failed. */
                    return(false);
                }
            }
            else
            {   /* Already 3 or 4 channels: encode the caller's buffer directly. */
                imgUse = imgIn;
            }

            iChannels    = imgUse.IMG_iChannels;
            iSampleCount = imgUse.IMG_arrSamples8.Length;
            iSampleEnd   = iSampleCount - iChannels;

            /* The reference's worst-case bound: every pixel emitted as a    */
            /* literal, which is one tag byte plus the channels, plus the    */
            /* header and the end marker.  Reserving it up front means the   */
            /* writer never regrows.                                         */
            iMaxSize = (imgUse.IMG_iWidth * imgUse.IMG_iHeight * (iChannels + 1)) +
                       QOI_HEADER_SIZE + QOI_PADDING_SIZE;

            bwOut = new ByteWriter(iMaxSize);

            /* Header. */
            bwOut.BYTEW_WriteU8((int)'q');
            bwOut.BYTEW_WriteU8((int)'o');
            bwOut.BYTEW_WriteU8((int)'i');
            bwOut.BYTEW_WriteU8((int)'f');
            bwOut.BYTEW_WriteU32BE((uint)imgUse.IMG_iWidth);
            bwOut.BYTEW_WriteU32BE((uint)imgUse.IMG_iHeight);
            bwOut.BYTEW_WriteU8(iChannels);
            bwOut.BYTEW_WriteU8(optUse.QOIOPT_iColorspace);

            arrIndex = new uint[QOI_INDEX_SIZE];
            iRun     = 0;

            /* Both sides start from opaque black, per the specification. */
            iPrevRed   = 0;
            iPrevGreen = 0;
            iPrevBlue  = 0;
            iPrevAlpha = 255;

            for (iSamplePos = 0; iSamplePos < iSampleCount; iSamplePos += iChannels)
            {   /* One source pixel per iteration. */
                iRed   = (int)imgUse.IMG_arrSamples8[iSamplePos];
                iGreen = (int)imgUse.IMG_arrSamples8[iSamplePos + 1];
                iBlue  = (int)imgUse.IMG_arrSamples8[iSamplePos + 2];

                if (iChannels == 4)
                {   /* Four-channel source carries its own alpha. */
                    iAlpha = (int)imgUse.IMG_arrSamples8[iSamplePos + 3];
                }
                else
                {   /* Three-channel source is fully opaque throughout. */
                    iAlpha = iPrevAlpha;
                }

                uPixel     = QOI_Pack(iRed, iGreen, iBlue, iAlpha);
                uPrevPixel = QOI_Pack(iPrevRed, iPrevGreen, iPrevBlue, iPrevAlpha);

                if (uPixel == uPrevPixel)
                {   /* Identical to the previous pixel: extend the run rather   */
                    /* than emitting anything yet.                              */
                    iRun = iRun + 1;

                    /* DEVIATION 3 FROM qoi.h (see the file header).  The       */
                    /* reference encoder does NOT index a run's pixel; its      */
                    /* decoder does.  Indexing here keeps the two halves in     */
                    /* step, so a later occurrence of this colour can be one    */
                    /* INDEX byte instead of a DIFF, LUMA or RGB chunk.         */
                    /*                                                          */
                    /* Every pixel of a run is by definition the same colour,   */
                    /* so this writes the same value to the same slot for the   */
                    /* whole run and the repetition costs nothing.  It is       */
                    /* written per pixel rather than once at the run's start    */
                    /* because that is where the decoder's matching write       */
                    /* sits, and the two are easiest to check against each      */
                    /* other when they are the same shape.                      */
                    arrIndex[QOI_ColorHash(iRed, iGreen, iBlue, iAlpha)] = uPixel;

                    if (iRun == QOI_RUN_MAX || iSamplePos == iSampleEnd)
                    {   /* Run is as long as one chunk can express, or the   */
                        /* image just ended -- flush it either way.          */
                        bwOut.BYTEW_WriteU8(QOI_OP_RUN | (iRun - 1));
                        iRun = 0;
                    }
                }
                else
                {   /* The pixel changed, so any run in progress must be   */
                    /* flushed before this pixel's own chunk is written.   */
                    if (iRun > 0)
                    {   /* Close the run first; order matters to the decoder. */
                        bwOut.BYTEW_WriteU8(QOI_OP_RUN | (iRun - 1));
                        iRun = 0;
                    }

                    iIndexPos = QOI_ColorHash(iRed, iGreen, iBlue, iAlpha);

                    if (arrIndex[iIndexPos] == uPixel)
                    {   /* This exact colour is already in the running array,   */
                        /* so one byte names it.                                */
                        bwOut.BYTEW_WriteU8(QOI_OP_INDEX | iIndexPos);
                    }
                    else
                    {   /* Not in the index: record it there, then choose the   */
                        /* smallest chunk that can express the change.          */
                        arrIndex[iIndexPos] = uPixel;

                        if (iAlpha == iPrevAlpha)
                        {   /* Alpha unchanged, so a delta form is available. */

                            /* The casts through sbyte are the whole subtlety   */
                            /* of this encoder.  The reference computes these   */
                            /* in signed char, so a channel wrapping from 255   */
                            /* to 0 is a delta of +1, not -255 -- and the       */
                            /* decoder's own wraparound reverses it exactly.    */
                            /* Computed as plain ints, every wrapping pixel     */
                            /* would fall through to a literal chunk: still     */
                            /* correct output, but larger, and no longer        */
                            /* byte-identical to the shipped reference          */
                            /* images.  Deviation 3 in the file header is the   */
                            /* only place this encoder departs from qoi.h's     */
                            /* byte-for-byte output; this is not one.           */
                            iDeltaR = (int)(sbyte)(iRed   - iPrevRed);
                            iDeltaG = (int)(sbyte)(iGreen - iPrevGreen);
                            iDeltaB = (int)(sbyte)(iBlue  - iPrevBlue);

                            iLumaR = (int)(sbyte)(iDeltaR - iDeltaG);
                            iLumaB = (int)(sbyte)(iDeltaB - iDeltaG);

                            if (iDeltaR > -3 && iDeltaR < 2 &&
                                iDeltaG > -3 && iDeltaG < 2 &&
                                iDeltaB > -3 && iDeltaB < 2)
                            {   /* All three deltas fit in two bits with a bias of 2. */
                                bwOut.BYTEW_WriteU8(QOI_OP_DIFF |
                                                    ((iDeltaR + 2) << 4) |
                                                    ((iDeltaG + 2) << 2) |
                                                     (iDeltaB + 2));
                            }
                            else if (iLumaR >  -9 && iLumaR <  8 &&
                                     iDeltaG > -33 && iDeltaG < 32 &&
                                     iLumaB >  -9 && iLumaB <  8)
                            {   /* Green fits in six bits and the other two      */
                                /* fit in four each once expressed relative to   */
                                /* it.                                           */
                                bwOut.BYTEW_WriteU8(QOI_OP_LUMA | (iDeltaG + 32));
                                bwOut.BYTEW_WriteU8(((iLumaR + 8) << 4) | (iLumaB + 8));
                            }
                            else
                            {   /* Too large a change for any delta form; emit the colour literally. */
                                bwOut.BYTEW_WriteU8(QOI_OP_RGB);
                                bwOut.BYTEW_WriteU8(iRed);
                                bwOut.BYTEW_WriteU8(iGreen);
                                bwOut.BYTEW_WriteU8(iBlue);
                            }
                        }
                        else
                        {   /* Alpha changed, and no delta form carries alpha,   */
                            /* so the whole pixel goes out literally.            */
                            bwOut.BYTEW_WriteU8(QOI_OP_RGBA);
                            bwOut.BYTEW_WriteU8(iRed);
                            bwOut.BYTEW_WriteU8(iGreen);
                            bwOut.BYTEW_WriteU8(iBlue);
                            bwOut.BYTEW_WriteU8(iAlpha);
                        }
                    }
                }

                iPrevRed   = iRed;
                iPrevGreen = iGreen;
                iPrevBlue  = iBlue;
                iPrevAlpha = iAlpha;
            }

            for (i = 0; i < QOI_PADDING_SIZE; i++)
            {   /* The mandatory end marker closes the file. */
                bwOut.BYTEW_WriteU8((int)QOI_arrPadding[i]);
            }

            arrOut = bwOut.BYTEW_ToArray();

            /* A complete QOI file: header, chunks, end marker. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* QOI_ColorHash:                                                     */
        /*                                                                    */
        /* The specification's index hash: r*3 + g*5 + b*7 + a*11, modulo 64. */
        /*                                                                    */
        /* Encoder and decoder MUST agree on this exactly, so it lives in one */
        /* function that both call rather than being written out twice.  Two  */
        /* copies that drifted by one multiplier would produce a codec whose  */
        /* own output it could still read, and which nothing else could --    */
        /* the worst possible failure mode for a file format.                 */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iRed    : Red channel, 0..255.                                 */
        /*     iGreen  : Green channel, 0..255.                               */
        /*     iBlue   : Blue channel, 0..255.                                */
        /*     iAlpha  : Alpha channel, 0..255.                               */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int     : Index into the 64-entry running array.               */
        /*--------------------------------------------------------------------*/

        private static int QOI_ColorHash(int iRed, int iGreen, int iBlue, int iAlpha)
        {
            /* The modulo is a mask because the array size is a power of two,   */
            /* which is exactly how the reference writes it too.                */
            return(((iRed * QOI_HASH_R) + (iGreen * QOI_HASH_G) +
                    (iBlue * QOI_HASH_B) + (iAlpha * QOI_HASH_A)) & (QOI_INDEX_SIZE - 1));
        }

        /*--------------------------------------------------------------------*/
        /* QOI_Pack:                                                          */
        /*                                                                    */
        /* Packs four channels into one uint so two pixels can be compared in */
        /* a single operation, and so the running index can be a flat uint    */
        /* array.                                                             */
        /*                                                                    */
        /* This mirrors the union in the reference, whose whole purpose is    */
        /* the same single-comparison trick.  The packing order is arbitrary  */
        /* as long as it is consistent, but note one property that is NOT     */
        /* arbitrary: a freshly zeroed index array reads as the colour        */
        /* {0, 0, 0, 0}, so a genuinely transparent-black pixel can match an  */
        /* empty slot.  That is the reference's behaviour, it is harmless --  */
        /* the encoder would emit that exact colour either way -- and         */
        /* preserving it keeps our output byte-identical to the reference's.  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iRed    : Red channel, 0..255.                                 */
        /*     iGreen  : Green channel, 0..255.                               */
        /*     iBlue   : Blue channel, 0..255.                                */
        /*     iAlpha  : Alpha channel, 0..255.                               */
        /*                                                                    */
        /* Returns:                                                           */
        /*     uint    : The four channels packed as r, g, b, a from the top. */
        /*--------------------------------------------------------------------*/

        private static uint QOI_Pack(int iRed, int iGreen, int iBlue, int iAlpha)
        {
            /* Red in the top byte, alpha in the bottom, matching the   */
            /* unpacking in the QOI_OP_INDEX branch of the decoder.     */
            return(((uint)iRed << 24) | ((uint)iGreen << 16) |
                   ((uint)iBlue << 8) | (uint)iAlpha);
        }
    }
}
