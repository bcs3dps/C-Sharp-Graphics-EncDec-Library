/*----------------------------------------------------------------------------*/
/* SPDX-License-Identifier: MIT OR GPL-2.0-or-later                           */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* Dual-licensed at the recipient's choice -- see LICENSE in this folder for  */
/* the full terms of both licences.  GPL-2.0 full text in GPL-2.0.txt.        */
/*----------------------------------------------------------------------------*/
/* ImageBuffer.cs -- the decoded-image carrier every codec produces  (IMG_)   */
/*----------------------------------------------------------------------------*/
/* This is the single type the whole library is built around: every decoder   */
/* returns one, every encoder consumes one, and the resampler maps one to     */
/* another.  Its shape is part of the library's public contract and is not to */
/* be changed casually: it appears in every codec's inner loop, so a change   */
/* here is a change everywhere.                                               */
/*                                                                            */
/* SAMPLE LAYOUT -- interleaved, row-major, top row first, no row padding.    */
/* The stride is IMG_iWidth * IMG_iChannels SAMPLES (not bytes), and the      */
/* sample for pixel (x, y) channel c sits at:                                 */
/*                                                                            */
/*     ((y * IMG_iWidth) + x) * IMG_iChannels + c                             */
/*                                                                            */
/* That is stb_image's own layout, and matching it is deliberate: this        */
/* library's codecs are transcodings of stb, and a transcoded function that   */
/* has to re-index as it goes cannot be diffed against its source when it     */
/* misbehaves.                                                                */
/*                                                                            */
/* TWO SAMPLE ARRAYS, EXACTLY ONE LIVE.  PNG's full specification carries     */
/* 16-bit samples; the other four formats never do.  Rather than widen every  */
/* image to ushort (which would double memory and halve cache density in the  */
/* exact inner loops that matter) or pack 16-bit into byte pairs (which taxes */
/* every read with a shift-pair and gets unpacked at the point of use         */
/* anyway), the buffer carries one array of each type and IMG_iBitsPerSample  */
/* says which is live.  The other is null.  This was chosen deliberately over */
/* a single widened array and over a generic, for the reasons above.          */
/*                                                                            */
/* Reference-type null with an explicit == null test is the sanctioned form.  */
/* Nullable value types, ?? and ?. are banned throughout this library and     */
/* appear nowhere in it.                                                      */
/*                                                                            */
/* WHAT IS DELIBERATELY ABSENT: palette, gamma, ICC profile, EXIF, DPI.  A    */
/* decoder resolves a palette to real samples before returning, so a buffer   */
/* is always true colour or true grayscale and a consumer never has to know   */
/* what the file happened to be.  Metadata, if it is ever wanted, is a purely */
/* additive change -- adding a field here breaks nobody, whereas changing the */
/* sample layout breaks everyone.                                             */
/*                                                                            */
/* PORTABILITY: nothing in this file is Windows-specific, and nothing in this */
/* library may be.  No System.Drawing, no P/Invoke, no registry, no platform  */
/* string handling.  The library references System.dll and nothing else.      */
/*----------------------------------------------------------------------------*/

using System;

namespace GraphicsEncDec
{
    /*------------------------------------------------------------------------*/
    /* ImageBuffer:                                                           */
    /* A decoded raster image, plus the static helpers that create and check  */
    /* one.  Sealed because the "exactly one sample array is live" invariant  */
    /* is enforced by the helpers below, and a subclass could quietly break   */
    /* it in a way no codec would defend against.                             */
    /*------------------------------------------------------------------------*/

    public sealed class ImageBuffer
    {
        /*--------------------------------------------------------------------*/
        /* Channel counts.  These are the only four legal values, and they    */
        /* double as the count itself, so arithmetic can use the field        */
        /* directly rather than switching on an enum.                         */
        /*--------------------------------------------------------------------*/

        public const int IMG_CHANNELS_GRAY       = 1;    // one sample: luminance
        public const int IMG_CHANNELS_GRAY_ALPHA = 2;    // luminance, then alpha
        public const int IMG_CHANNELS_RGB        = 3;    // red, green, blue in that order
        public const int IMG_CHANNELS_RGBA       = 4;    // red, green, blue, then alpha

        /*--------------------------------------------------------------------*/
        /* Sample depths.  Eight or sixteen bits, nothing else.  Sub-byte     */
        /* depths (PNG's 1, 2 and 4) are expanded to 8 by the decoder, so     */
        /* they never reach this type.                                        */
        /*--------------------------------------------------------------------*/

        public const int IMG_BITS_8  = 8;                // samples live in IMG_arrSamples8
        public const int IMG_BITS_16 = 16;               // samples live in IMG_arrSamples16

        /*--------------------------------------------------------------------*/
        /* Sanity ceiling on a single dimension.  Matches stb_image's own     */
        /* limit of 1 << 24.  Its purpose is not to be a real-world cap but   */
        /* to make a corrupt header fail immediately instead of asking the    */
        /* allocator for an absurd block -- a decoder is fed hostile bytes by */
        /* definition, and a bogus width is the cheapest attack there is.     */
        /*--------------------------------------------------------------------*/

        public const int IMG_MAX_DIMENSION = 1 << 24;    // 16777216 pixels per side

        /*--------------------------------------------------------------------*/
        /* The image itself.  Plain public fields, no properties: this is a   */
        /* data carrier and it reads the way the equivalent C struct would.   */
        /* Every name carries the IMG_ group prefix and a Hungarian type tag, */
        /* so a grep for any one of them returns exactly this definition and  */
        /* its real uses.                                                     */
        /*--------------------------------------------------------------------*/

        public int      IMG_iWidth;                      // pixels across; always >= 1 on a valid buffer
        public int      IMG_iHeight;                     // pixel rows; always >= 1 on a valid buffer
        public int      IMG_iChannels;                   // one of the IMG_CHANNELS_* values above
        public int      IMG_iBitsPerSample;              // IMG_BITS_8 or IMG_BITS_16; nothing else
        public byte[]   IMG_arrSamples8;                 // live when 8-bit, else null
        public ushort[] IMG_arrSamples16;                // live when 16-bit, else null

        /*--------------------------------------------------------------------*/
        /* IMG_TryAllocate:                                                   */
        /*                                                                    */
        /* Creates a zero-filled buffer of the requested geometry, validating */
        /* every argument before allocating anything.  This is the ONLY       */
        /* sanctioned way to make an ImageBuffer inside this library: a       */
        /* decoder that hand-rolls one can get the invariant wrong, and the   */
        /* whole point of routing through here is that it cannot.             */
        /*                                                                    */
        /* The sample count is computed in LONG arithmetic and checked before */
        /* it is narrowed to int.  A 30000 x 30000 RGBA image is 3.6 billion  */
        /* samples, which overflows a signed int and would otherwise wrap to  */
        /* a small positive number -- allocating a tiny array that every      */
        /* subsequent write runs off the end of.  A corrupt header can ask    */
        /* for exactly that, so the check is a security boundary and not a    */
        /* tidiness measure.                                                  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iWidth          : Requested width in pixels.  Must be at least */
        /*                       1 and at most IMG_MAX_DIMENSION.             */
        /*     iHeight         : Requested height in pixels.  Same bounds.    */
        /*     iChannels       : One of the IMG_CHANNELS_* constants.         */
        /*     iBitsPerSample  : IMG_BITS_8 or IMG_BITS_16.                   */
        /*     imgOut          : Receives the new buffer on success, or null  */
        /*                       on any failure.                              */
        /*     sReason         : Receives "" on success, or a short human-    */
        /*                       readable cause on failure.  For a log line   */
        /*                       or a diagnostic report, never for the        */
        /*                       caller to parse.                             */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool            : true when imgOut holds a usable buffer;      */
        /*                       false when the geometry was rejected, in     */
        /*                       which case nothing was allocated.            */
        /*--------------------------------------------------------------------*/

        public static bool IMG_TryAllocate(int iWidth, int iHeight, int iChannels,
                                           int iBitsPerSample,
                                           out ImageBuffer imgOut, out string sReason)
        {
            ImageBuffer imgNew;
            long        lSampleCount;

            /* Fail closed: the out parameters are set before any test runs,   */
            /* so no path can leave a caller reading an uninitialised value.   */
            imgOut  = null;
            sReason = "";

            if (iWidth < 1 || iWidth > IMG_MAX_DIMENSION)
            {   /* Width is zero, negative, or absurd -- almost always a corrupt header field. */
                sReason = "image width " + iWidth.ToString() + " is out of range 1.." + IMG_MAX_DIMENSION.ToString();
                return(false);
            }

            if (iHeight < 1 || iHeight > IMG_MAX_DIMENSION)
            {   /* Same test on the other axis, reported separately so the message names the culprit. */
                sReason = "image height " + iHeight.ToString() + " is out of range 1.." + IMG_MAX_DIMENSION.ToString();
                return(false);
            }

            if (iChannels < IMG_CHANNELS_GRAY || iChannels > IMG_CHANNELS_RGBA)
            {   /* Channel counts outside 1..4 have no meaning in this library's buffer layout. */
                sReason = "channel count " + iChannels.ToString() + " is not 1, 2, 3 or 4";
                return(false);
            }

            if (iBitsPerSample != IMG_BITS_8 && iBitsPerSample != IMG_BITS_16)
            {   /* Sub-byte depths are expanded by the decoder and must never arrive here. */
                sReason = "bits per sample " + iBitsPerSample.ToString() + " is not 8 or 16";
                return(false);
            }

            /* Long arithmetic deliberately: see the overflow note in the header. */
            lSampleCount = (long)iWidth * (long)iHeight * (long)iChannels;

            if (lSampleCount > (long)int.MaxValue)
            {   /* The geometry is individually legal but the product does not fit an array index. */
                sReason = "image needs " + lSampleCount.ToString() + " samples, which exceeds the maximum array length";
                return(false);
            }

            /* Geometry accepted.  Build the carrier, then allocate exactly   */
            /* one of the two sample arrays and leave the other null.         */
            imgNew                    = new ImageBuffer();
            imgNew.IMG_iWidth         = iWidth;
            imgNew.IMG_iHeight        = iHeight;
            imgNew.IMG_iChannels      = iChannels;
            imgNew.IMG_iBitsPerSample = iBitsPerSample;

            if (iBitsPerSample == IMG_BITS_8)
            {   /* Eight-bit image: the byte array is live and the ushort array stays null. */
                imgNew.IMG_arrSamples8  = new byte[(int)lSampleCount];
                imgNew.IMG_arrSamples16 = null;
            }
            else
            {   /* Sixteen-bit image: the ushort array is live and the byte array stays null. */
                imgNew.IMG_arrSamples8  = null;
                imgNew.IMG_arrSamples16 = new ushort[(int)lSampleCount];
            }

            imgOut = imgNew;

            /* Success: imgOut holds a zero-filled buffer of the exact         */
            /* geometry asked for, with the array invariant already correct.   */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* IMG_TryValidate:                                                   */
        /*                                                                    */
        /* Checks that a buffer handed in from OUTSIDE this library is        */
        /* internally consistent, before an encoder starts indexing into it.  */
        /* IMG_TryAllocate cannot be the only guard, because a caller may     */
        /* legitimately build a buffer by hand or mutate one it was given,    */
        /* and an encoder reading a buffer whose array length disagrees with  */
        /* its own width and height will walk off the end.                    */
        /*                                                                    */
        /* Every public encode entry point calls this first.  It is cheap --  */
        /* a handful of integer compares, no traversal of the samples.        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     imgIn    : The buffer to check.  A null reference is a caller  */
        /*                bug and is reported as such rather than throwing,   */
        /*                because an encoder's job is to answer with a reason */
        /*                rather than to raise.                               */
        /*     sReason  : Receives "" when the buffer is sound, or a short    */
        /*                description of the first inconsistency found.       */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool     : true when every field agrees with every other and   */
        /*                the live sample array is exactly the right length;  */
        /*                false otherwise.                                    */
        /*--------------------------------------------------------------------*/

        public static bool IMG_TryValidate(ImageBuffer imgIn, out string sReason)
        {
            long lExpectedSamples;

            sReason = "";

            if (imgIn == null)
            {   /* No buffer at all -- report rather than throw, so an encoder can answer normally. */
                sReason = "image buffer is null";
                return(false);
            }

            if (imgIn.IMG_iWidth < 1 || imgIn.IMG_iWidth > IMG_MAX_DIMENSION)
            {   /* Width field is unusable, so nothing derived from it can be trusted either. */
                sReason = "image width " + imgIn.IMG_iWidth.ToString() + " is out of range 1.." + IMG_MAX_DIMENSION.ToString();
                return(false);
            }

            if (imgIn.IMG_iHeight < 1 || imgIn.IMG_iHeight > IMG_MAX_DIMENSION)
            {   /* Height field is unusable for the same reason. */
                sReason = "image height " + imgIn.IMG_iHeight.ToString() + " is out of range 1.." + IMG_MAX_DIMENSION.ToString();
                return(false);
            }

            if (imgIn.IMG_iChannels < IMG_CHANNELS_GRAY || imgIn.IMG_iChannels > IMG_CHANNELS_RGBA)
            {   /* Channel count outside 1..4 makes the index arithmetic meaningless. */
                sReason = "channel count " + imgIn.IMG_iChannels.ToString() + " is not 1, 2, 3 or 4";
                return(false);
            }

            /* How many samples the geometry says the live array must hold. */
            lExpectedSamples = (long)imgIn.IMG_iWidth * (long)imgIn.IMG_iHeight * (long)imgIn.IMG_iChannels;

            if (imgIn.IMG_iBitsPerSample == IMG_BITS_8)
            {   /* Eight-bit: the byte array must be present, the ushort array absent, and the length exact. */
                if (imgIn.IMG_arrSamples8 == null)
                {   /* Depth says 8-bit but the 8-bit array was never allocated. */
                    sReason = "buffer is 8-bit but IMG_arrSamples8 is null";
                    return(false);
                }

                if (imgIn.IMG_arrSamples16 != null)
                {   /* Both arrays live: the invariant is broken and it is ambiguous which one is real. */
                    sReason = "buffer is 8-bit but IMG_arrSamples16 is also populated";
                    return(false);
                }

                if ((long)imgIn.IMG_arrSamples8.Length != lExpectedSamples)
                {   /* Array length disagrees with the geometry -- the exact case that walks off the end. */
                    sReason = "8-bit sample array holds " + imgIn.IMG_arrSamples8.Length.ToString() +
                              " samples but the geometry needs " + lExpectedSamples.ToString();
                    return(false);
                }
            }
            else if (imgIn.IMG_iBitsPerSample == IMG_BITS_16)
            {   /* Sixteen-bit: the mirror image of the checks above. */
                if (imgIn.IMG_arrSamples16 == null)
                {   /* Depth says 16-bit but the 16-bit array was never allocated. */
                    sReason = "buffer is 16-bit but IMG_arrSamples16 is null";
                    return(false);
                }

                if (imgIn.IMG_arrSamples8 != null)
                {   /* Both arrays live again, from the other direction. */
                    sReason = "buffer is 16-bit but IMG_arrSamples8 is also populated";
                    return(false);
                }

                if ((long)imgIn.IMG_arrSamples16.Length != lExpectedSamples)
                {   /* Length disagrees with geometry on the 16-bit path. */
                    sReason = "16-bit sample array holds " + imgIn.IMG_arrSamples16.Length.ToString() +
                              " samples but the geometry needs " + lExpectedSamples.ToString();
                    return(false);
                }
            }
            else
            {   /* Depth is neither 8 nor 16, so neither array can be interpreted at all. */
                sReason = "bits per sample " + imgIn.IMG_iBitsPerSample.ToString() + " is not 8 or 16";
                return(false);
            }

            /* Every field agrees with every other and the live array is the   */
            /* exact length the geometry demands: safe to index into.          */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* IMG_SampleCount:                                                   */
        /*                                                                    */
        /* Returns the total number of samples in a buffer -- width times     */
        /* height times channels -- which is also the length of whichever     */
        /* sample array is live.                                              */
        /*                                                                    */
        /* This exists so codecs stop recomputing the product inline and      */
        /* getting the channel factor wrong, which is the single easiest      */
        /* mistake to make in this layout.  It assumes the buffer has already */
        /* passed IMG_TryValidate; it does not re-check, because it is called */
        /* from loop preambles where a second validation would be waste.      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     imgIn  : A buffer that has already been validated.  Must not   */
        /*              be null.                                              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int    : Total sample count.  Guaranteed to fit an int because */
        /*              IMG_TryAllocate and IMG_TryValidate both reject any   */
        /*              geometry whose product does not.                      */
        /*--------------------------------------------------------------------*/

        public static int IMG_SampleCount(ImageBuffer imgIn)
        {
            if (imgIn == null)
            {   /* A null here is a caller bug, not image data, so it throws. */
                throw new ArgumentNullException("imgIn");
            }

            /* Safe to multiply in int: the allocation and validation paths   */
            /* have both already refused anything that would overflow.        */
            return(imgIn.IMG_iWidth * imgIn.IMG_iHeight * imgIn.IMG_iChannels);
        }

        /*--------------------------------------------------------------------*/
        /* IMG_RowStride:                                                     */
        /*                                                                    */
        /* Returns the number of SAMPLES from the start of one pixel row to   */
        /* the start of the next.  Samples, not bytes: on a 16-bit buffer the */
        /* byte stride is twice this, and codecs that need the byte figure    */
        /* say so at the point of use rather than having two near-identical   */
        /* helpers whose names could be confused.                             */
        /*                                                                    */
        /* There is no row padding in this layout, so this is simply width    */
        /* times channels -- but naming it makes the many loops that step a   */
        /* row at a time read as intent rather than as arithmetic.            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     imgIn  : A buffer that has already been validated.  Must not   */
        /*              be null.                                              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int    : Samples per row.                                      */
        /*--------------------------------------------------------------------*/

        public static int IMG_RowStride(ImageBuffer imgIn)
        {
            if (imgIn == null)
            {   /* Caller bug, same reasoning as IMG_SampleCount. */
                throw new ArgumentNullException("imgIn");
            }

            /* No inter-row padding exists in this layout, so the stride is   */
            /* exactly the width in samples.                                  */
            return(imgIn.IMG_iWidth * imgIn.IMG_iChannels);
        }

        /*--------------------------------------------------------------------*/
        /* IMG_SampleIndex:                                                   */
        /*                                                                    */
        /* Maps a pixel coordinate and channel to an index into the live      */
        /* sample array.  Written once, here, so no codec has to spell the    */
        /* formula out again -- and so the formula appears in exactly one     */
        /* place if the layout is ever revisited.                             */
        /*                                                                    */
        /* Bounds are NOT checked.  This is called from the innermost loop of */
        /* every codec, where the caller has already established the range,   */
        /* and a redundant compare per sample is precisely the cost this      */
        /* library cannot afford.  Callers that are not certain of their      */
        /* bounds must check before calling, not after.                       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     imgIn     : A validated buffer.  Must not be null.             */
        /*     iX        : Pixel column, 0 to IMG_iWidth - 1.                 */
        /*     iY        : Pixel row, 0 to IMG_iHeight - 1.                   */
        /*     iChannel  : Channel within the pixel, 0 to IMG_iChannels - 1.  */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int       : Index into IMG_arrSamples8 or IMG_arrSamples16,    */
        /*                 whichever the buffer's depth makes live.           */
        /*--------------------------------------------------------------------*/

        public static int IMG_SampleIndex(ImageBuffer imgIn, int iX, int iY, int iChannel)
        {
            /* Row-major, interleaved, no padding -- the one formula this   */
            /* whole library indexes by.                                    */
            return((((iY * imgIn.IMG_iWidth) + iX) * imgIn.IMG_iChannels) + iChannel);
        }

        /*--------------------------------------------------------------------*/
        /* Luminance weights, from stb_image.h v2.30 (nothings/stb, commit    */
        /* 2c980bb5), its stbi__compute_y.  These are the integer form of the */
        /* ITU-R BT.601 luma coefficients, and they sum to exactly 256 so the */
        /* division is a shift of 8 and the result cannot exceed the input    */
        /* range.  The SAME weights and the SAME shift serve both 8-bit and   */
        /* 16-bit samples, which looks wrong at a glance and is correct: the  */
        /* weights are a weighted MEAN, so the output range matches the input */
        /* range whatever that range is.                                      */
        /*                                                                    */
        /* Green dominates because human vision is most sensitive to it.  Do  */
        /* not "improve" these to the BT.709 coefficients -- matching stb     */
        /* exactly is what lets a converted image be compared against stb's   */
        /* own output when a codec is suspected of being wrong.               */
        /*--------------------------------------------------------------------*/

        private const int IMG_LUMA_WEIGHT_R = 77;        // 0.299 * 256, rounded
        private const int IMG_LUMA_WEIGHT_G = 150;       // 0.587 * 256, rounded
        private const int IMG_LUMA_WEIGHT_B = 29;        // 0.114 * 256, rounded
        private const int IMG_LUMA_SHIFT    = 8;         // log2(77 + 150 + 29) == log2(256)

        /*--------------------------------------------------------------------*/
        /* Fully opaque alpha, per depth.  Two constants rather than one      */
        /* computed value, because writing 255 into a 16-bit alpha channel    */
        /* produces an image that is very nearly transparent rather than very */
        /* nearly opaque -- a mistake that looks like a decoder bug and is    */
        /* not.                                                               */
        /*                                                                    */
        /* PUBLIC, having started private.  The question "what value means    */
        /* opaque at this sample width?" belongs to the                       */
        /* type that owns the sample width, and every codec that synthesises  */
        /* an alpha channel needs the answer -- PNG's tRNS paths do, and so   */
        /* do PNG encode and GIF.  The alternative was a private copy per     */
        /* codec, which is how one of them eventually gets 255 where it       */
        /* needed 65535.  Additive: nothing that compiled before changes.     */
        /*--------------------------------------------------------------------*/

        public const int IMG_OPAQUE_8  = 255;            // 0xFF
        public const int IMG_OPAQUE_16 = 65535;          // 0xFFFF

        /*--------------------------------------------------------------------*/
        /* IMG_TryConvertChannels:                                            */
        /*                                                                    */
        /* Produces a NEW buffer holding the same image with a different      */
        /* channel count -- grayscale to RGB, RGBA to RGB, and every other    */
        /* combination of 1, 2, 3 and 4.  The sample depth is unchanged.      */
        /*                                                                    */
        /* Transcoded from stb_image.h v2.30 (nothings/stb, commit 2c980bb5), */
        /* its stbi__convert_format and stbi__convert_format16 combination    */
        /* tables.  The twelve cases below are stb's twelve cases in stb's    */
        /* order, so the two can be read side by side.  stb is dual-licensed  */
        /* MIT or public domain, satisfying both arms of this file's licence. */
        /*                                                                    */
        /* WHY EVERY CODEC NEEDS THIS.  A decoder produces whatever the file  */
        /* contained -- PNG alone can hand back any of the four -- while a    */
        /* consumer usually wants one specific shape, and the GDI+ oracle     */
        /* compares only RGBA against RGBA.  Without a shared conversion,     */
        /* each codec would grow its own, they would disagree about the luma  */
        /* weights, and comparisons between them would start failing for      */
        /* reasons that had nothing to do with the codecs.                    */
        /*                                                                    */
        /* ALPHA IS SYNTHESISED AS FULLY OPAQUE when adding a channel that    */
        /* the source did not have, which is the only defensible answer: an   */
        /* image without an alpha channel is an image where every pixel is    */
        /* opaque.                                                            */
        /*                                                                    */
        /* COLOUR IS DISCARDED, NOT AVERAGED, when dropping alpha; and colour */
        /* becomes luminance when dropping to grayscale.  Neither is          */
        /* reversible, which is why this returns a new buffer rather than     */
        /* converting in place -- a caller that still needs the original has  */
        /* it.                                                                */
        /*                                                                    */
        /* Converting to the SAME channel count is legal and returns a copy.  */
        /* A copy rather than the same reference, so a caller can always      */
        /* treat the result as its own to modify without having to know       */
        /* whether a conversion actually happened.                            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     imgIn         : Source buffer.  Must be valid per              */
        /*                     IMG_TryValidate; not modified.                 */
        /*     iNewChannels  : Desired channel count, one of the              */
        /*                     IMG_CHANNELS_* values.                         */
        /*     imgOut        : Receives the converted buffer; null on         */
        /*                     failure.                                       */
        /*     sReason       : Receives "" on success, or the cause.          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool          : true when imgOut holds the converted image.    */
        /*--------------------------------------------------------------------*/

        public static bool IMG_TryConvertChannels(ImageBuffer imgIn, int iNewChannels,
                                                  out ImageBuffer imgOut, out string sReason)
        {
            ImageBuffer imgNew;
            int         iPixels;
            int         iSrcChannels;
            int         iOpaque;
            int         iSrc;
            int         iDst;
            int         iLuma;
            int         i;
            int         c;

            imgOut  = null;
            sReason = "";

            if (!IMG_TryValidate(imgIn, out sReason))
            {   /* Source is malformed; pass the validator's own reason up unchanged. */
                return(false);
            }

            if (iNewChannels < IMG_CHANNELS_GRAY || iNewChannels > IMG_CHANNELS_RGBA)
            {   /* Target channel count outside 1..4 has no meaning in this layout. */
                sReason = "target channel count " + iNewChannels.ToString() + " is not 1, 2, 3 or 4";
                return(false);
            }

            if (!IMG_TryAllocate(imgIn.IMG_iWidth, imgIn.IMG_iHeight, iNewChannels,
                                 imgIn.IMG_iBitsPerSample, out imgNew, out sReason))
            {   /* The destination geometry was rejected; the allocator says why. */
                return(false);
            }

            iPixels      = imgIn.IMG_iWidth * imgIn.IMG_iHeight;
            iSrcChannels = imgIn.IMG_iChannels;

            if (imgIn.IMG_iBitsPerSample == IMG_BITS_8)
            {   /* Eight-bit path: both buffers use the byte arrays. */
                iOpaque = IMG_OPAQUE_8;
            }
            else
            {   /* Sixteen-bit path: both use the ushort arrays, and opaque is 0xFFFF not 0xFF. */
                iOpaque = IMG_OPAQUE_16;
            }

            for (i = 0; i < iPixels; i++)
            {   /* One pixel per iteration.  The index arithmetic is hoisted    */
                /* out of the per-channel work below because the two strides    */
                /* differ and recomputing them per channel is the easiest way   */
                /* to get this loop subtly wrong.                               */
                iSrc = i * iSrcChannels;
                iDst = i * iNewChannels;

                if (iSrcChannels == iNewChannels)
                {   /* No conversion: copy the pixel's samples straight across. */
                    for (c = 0; c < iSrcChannels; c++)
                    {   /* Same count, same order, same values. */
                        IMG_PutSample(imgNew, iDst + c, IMG_GetSample(imgIn, iSrc + c));
                    }
                }
                else if (iSrcChannels == IMG_CHANNELS_GRAY && iNewChannels == IMG_CHANNELS_GRAY_ALPHA)
                {   /* 1 to 2: keep the grey, add an opaque alpha. */
                    IMG_PutSample(imgNew, iDst,     IMG_GetSample(imgIn, iSrc));
                    IMG_PutSample(imgNew, iDst + 1, iOpaque);
                }
                else if (iSrcChannels == IMG_CHANNELS_GRAY && iNewChannels == IMG_CHANNELS_RGB)
                {   /* 1 to 3: the grey value becomes all three colour channels. */
                    IMG_PutSample(imgNew, iDst,     IMG_GetSample(imgIn, iSrc));
                    IMG_PutSample(imgNew, iDst + 1, IMG_GetSample(imgIn, iSrc));
                    IMG_PutSample(imgNew, iDst + 2, IMG_GetSample(imgIn, iSrc));
                }
                else if (iSrcChannels == IMG_CHANNELS_GRAY && iNewChannels == IMG_CHANNELS_RGBA)
                {   /* 1 to 4: grey into all three colours, plus opaque alpha. */
                    IMG_PutSample(imgNew, iDst,     IMG_GetSample(imgIn, iSrc));
                    IMG_PutSample(imgNew, iDst + 1, IMG_GetSample(imgIn, iSrc));
                    IMG_PutSample(imgNew, iDst + 2, IMG_GetSample(imgIn, iSrc));
                    IMG_PutSample(imgNew, iDst + 3, iOpaque);
                }
                else if (iSrcChannels == IMG_CHANNELS_GRAY_ALPHA && iNewChannels == IMG_CHANNELS_GRAY)
                {   /* 2 to 1: keep the grey, discard the alpha. */
                    IMG_PutSample(imgNew, iDst, IMG_GetSample(imgIn, iSrc));
                }
                else if (iSrcChannels == IMG_CHANNELS_GRAY_ALPHA && iNewChannels == IMG_CHANNELS_RGB)
                {   /* 2 to 3: grey into all three colours, alpha discarded. */
                    IMG_PutSample(imgNew, iDst,     IMG_GetSample(imgIn, iSrc));
                    IMG_PutSample(imgNew, iDst + 1, IMG_GetSample(imgIn, iSrc));
                    IMG_PutSample(imgNew, iDst + 2, IMG_GetSample(imgIn, iSrc));
                }
                else if (iSrcChannels == IMG_CHANNELS_GRAY_ALPHA && iNewChannels == IMG_CHANNELS_RGBA)
                {   /* 2 to 4: grey into all three colours, alpha carried across. */
                    IMG_PutSample(imgNew, iDst,     IMG_GetSample(imgIn, iSrc));
                    IMG_PutSample(imgNew, iDst + 1, IMG_GetSample(imgIn, iSrc));
                    IMG_PutSample(imgNew, iDst + 2, IMG_GetSample(imgIn, iSrc));
                    IMG_PutSample(imgNew, iDst + 3, IMG_GetSample(imgIn, iSrc + 1));
                }
                else if (iSrcChannels == IMG_CHANNELS_RGB && iNewChannels == IMG_CHANNELS_GRAY)
                {   /* 3 to 1: colour collapses to weighted luminance. */
                    iLuma = IMG_ComputeLuma(IMG_GetSample(imgIn, iSrc),
                                            IMG_GetSample(imgIn, iSrc + 1),
                                            IMG_GetSample(imgIn, iSrc + 2));
                    IMG_PutSample(imgNew, iDst, iLuma);
                }
                else if (iSrcChannels == IMG_CHANNELS_RGB && iNewChannels == IMG_CHANNELS_GRAY_ALPHA)
                {   /* 3 to 2: luminance plus a synthesised opaque alpha. */
                    iLuma = IMG_ComputeLuma(IMG_GetSample(imgIn, iSrc),
                                            IMG_GetSample(imgIn, iSrc + 1),
                                            IMG_GetSample(imgIn, iSrc + 2));
                    IMG_PutSample(imgNew, iDst,     iLuma);
                    IMG_PutSample(imgNew, iDst + 1, iOpaque);
                }
                else if (iSrcChannels == IMG_CHANNELS_RGB && iNewChannels == IMG_CHANNELS_RGBA)
                {   /* 3 to 4: colours unchanged, opaque alpha appended. */
                    IMG_PutSample(imgNew, iDst,     IMG_GetSample(imgIn, iSrc));
                    IMG_PutSample(imgNew, iDst + 1, IMG_GetSample(imgIn, iSrc + 1));
                    IMG_PutSample(imgNew, iDst + 2, IMG_GetSample(imgIn, iSrc + 2));
                    IMG_PutSample(imgNew, iDst + 3, iOpaque);
                }
                else if (iSrcChannels == IMG_CHANNELS_RGBA && iNewChannels == IMG_CHANNELS_GRAY)
                {   /* 4 to 1: luminance of the colour channels; alpha discarded. */
                    iLuma = IMG_ComputeLuma(IMG_GetSample(imgIn, iSrc),
                                            IMG_GetSample(imgIn, iSrc + 1),
                                            IMG_GetSample(imgIn, iSrc + 2));
                    IMG_PutSample(imgNew, iDst, iLuma);
                }
                else if (iSrcChannels == IMG_CHANNELS_RGBA && iNewChannels == IMG_CHANNELS_GRAY_ALPHA)
                {   /* 4 to 2: luminance, with the original alpha carried across. */
                    iLuma = IMG_ComputeLuma(IMG_GetSample(imgIn, iSrc),
                                            IMG_GetSample(imgIn, iSrc + 1),
                                            IMG_GetSample(imgIn, iSrc + 2));
                    IMG_PutSample(imgNew, iDst,     iLuma);
                    IMG_PutSample(imgNew, iDst + 1, IMG_GetSample(imgIn, iSrc + 3));
                }
                else
                {   /* The only combination left is 4 to 3: colours kept, alpha dropped. */
                    IMG_PutSample(imgNew, iDst,     IMG_GetSample(imgIn, iSrc));
                    IMG_PutSample(imgNew, iDst + 1, IMG_GetSample(imgIn, iSrc + 1));
                    IMG_PutSample(imgNew, iDst + 2, IMG_GetSample(imgIn, iSrc + 2));
                }
            }

            imgOut = imgNew;

            /* A new buffer of the requested shape, holding the same image. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* IMG_ComputeLuma:                                                   */
        /*                                                                    */
        /* Collapses red, green and blue into a single luminance sample using */
        /* the BT.601 integer weights.  See the constants above for why these */
        /* particular numbers and why the same shift serves both depths.      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iRed    : Red sample, in the buffer's own range.               */
        /*     iGreen  : Green sample.                                        */
        /*     iBlue   : Blue sample.                                         */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int     : The luminance, in the same range as the inputs.      */
        /*--------------------------------------------------------------------*/

        private static int IMG_ComputeLuma(int iRed, int iGreen, int iBlue)
        {
            /* Weights sum to 256, so the shift is an exact divide by their   */
            /* total and the result cannot leave the input range.             */
            return(((iRed * IMG_LUMA_WEIGHT_R) +
                    (iGreen * IMG_LUMA_WEIGHT_G) +
                    (iBlue * IMG_LUMA_WEIGHT_B)) >> IMG_LUMA_SHIFT);
        }

        /*--------------------------------------------------------------------*/
        /* IMG_GetSample:                                                     */
        /*                                                                    */
        /* Reads one sample by flat index from whichever array the buffer's   */
        /* depth makes live, returning it as an int so callers do not branch  */
        /* on depth themselves.                                               */
        /*                                                                    */
        /* This is what keeps IMG_TryConvertChannels a single set of twelve   */
        /* cases rather than two near-identical sets -- which is exactly the  */
        /* duplication that lets an 8-bit fix miss its 16-bit twin.  The cost */
        /* is one branch per sample; the conversion is not an inner loop that */
        /* runs per output pixel of a scaled image, so that is the right      */
        /* trade here.                                                        */
        /*                                                                    */
        /* Bounds are not checked; the caller has already established them.   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     imgIn   : A validated buffer.                                  */
        /*     iIndex  : Flat sample index into the live array.               */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int     : The sample value, 0..255 or 0..65535 by depth.       */
        /*--------------------------------------------------------------------*/

        private static int IMG_GetSample(ImageBuffer imgIn, int iIndex)
        {
            if (imgIn.IMG_iBitsPerSample == IMG_BITS_8)
            {   /* Eight-bit: the byte array is the live one. */
                return((int)imgIn.IMG_arrSamples8[iIndex]);
            }

            /* Sixteen-bit: the ushort array is live instead. */
            return((int)imgIn.IMG_arrSamples16[iIndex]);
        }

        /*--------------------------------------------------------------------*/
        /* IMG_PutSample:                                                     */
        /*                                                                    */
        /* Writes one sample by flat index into whichever array the buffer's  */
        /* depth makes live.  The counterpart of IMG_GetSample and paired     */
        /* with it for the same reason.                                       */
        /*                                                                    */
        /* Bounds are not checked; the caller has already established them.   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     imgIo   : A validated buffer, written in place.                */
        /*     iIndex  : Flat sample index into the live array.               */
        /*     iValue  : Value to store.  Narrowed to the array's element     */
        /*               type, which is safe because every value written here */
        /*               originates from a sample of the same depth or from   */
        /*               the matching IMG_OPAQUE_* constant.                  */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void    : The sample is stored.                                */
        /*--------------------------------------------------------------------*/

        private static void IMG_PutSample(ImageBuffer imgIo, int iIndex, int iValue)
        {
            if (imgIo.IMG_iBitsPerSample == IMG_BITS_8)
            {   /* Eight-bit: store into the byte array. */
                imgIo.IMG_arrSamples8[iIndex] = (byte)iValue;
            }
            else
            {   /* Sixteen-bit: store into the ushort array. */
                imgIo.IMG_arrSamples16[iIndex] = (ushort)iValue;
            }
        }
    }
}
