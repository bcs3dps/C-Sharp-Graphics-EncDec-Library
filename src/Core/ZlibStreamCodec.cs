/*----------------------------------------------------------------------------*/
/* SPDX-License-Identifier: MIT OR GPL-2.0-or-later                           */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* Dual-licensed at the recipient's choice -- see LICENSE in this folder for  */
/* the full terms of both licences.  GPL-2.0 full text in GPL-2.0.txt.        */
/*----------------------------------------------------------------------------*/
/* ZlibStreamCodec.cs -- RFC 1950 framing over DeflateStream         (ZLIB_)  */
/*----------------------------------------------------------------------------*/
/* PNG does not store raw deflate data.  Its IDAT chunks hold a ZLIB STREAM,  */
/* which is raw deflate wrapped in a two-byte header and a four-byte Adler-32 */
/* trailer (RFC 1950).  The .NET framework supplies the deflate half and      */
/* nothing else -- DeflateStream is deliberately headerless -- so this file   */
/* is the wrapper that turns it into what PNG actually needs.                 */
/*                                                                            */
/* WHY NOT GZipStream, WHICH ALSO HAS A HEADER.  Because it is the WRONG      */
/* header: gzip (RFC 1952) uses a ten-byte header, a CRC-32 trailer and a     */
/* length field.  zlib (RFC 1950) uses two bytes and an Adler-32.  They wrap  */
/* the same deflate payload and are not interchangeable, and reaching for     */
/* GZipStream because it "adds the header" produces a file no PNG decoder     */
/* will read.                                                                 */
/*                                                                            */
/* THE STREAM LAYOUT:                                                         */
/*                                                                            */
/*     byte 0        CMF   low nibble  CM = 8, meaning deflate                */
/*                         high nibble CINFO = log2(window) - 8, 7 for 32 KiB */
/*     byte 1        FLG   bits 0-4    FCHECK, chosen so that                 */
/*                                     (CMF * 256 + FLG) is a multiple of 31  */
/*                         bit  5      FDICT, a preset dictionary follows     */
/*                         bits 6-7    FLEVEL, a hint about effort spent      */
/*     bytes 2..n-5        the raw deflate payload                            */
/*     last 4 bytes        Adler-32 of the UNCOMPRESSED data, big-endian      */
/*                                                                            */
/* THE MULTIPLE-OF-31 CHECK is the format's only structural validation, and   */
/* it is weak -- roughly one random byte pair in 31 passes it. It is still    */
/* worth applying, because it catches the common real failure of handing this */
/* function something that is not a zlib stream at all.                       */
/*                                                                            */
/* THE ADLER TRAILER IS VERIFIED ON INFLATE, and that is not optional         */
/* politeness.  DeflateStream will happily return a truncated result for a    */
/* truncated payload without complaining, so the checksum is the only thing   */
/* standing between a damaged IDAT and an image that decodes to plausible     */
/* garbage.                                                                   */
/*                                                                            */
/* THE OUTPUT CAP IS A SECURITY BOUNDARY, NOT TIDINESS.  Deflate compresses   */
/* a megabyte of zeroes into about a kilobyte, so a few hundred bytes of      */
/* hostile IDAT can expand to gigabytes -- the classic decompression bomb.    */
/* Every inflate here takes a maximum and stops at it.  A PNG decoder knows   */
/* exactly how many bytes its IDAT must produce, from the dimensions in       */
/* IHDR, and should pass that number rather than the default.                 */
/*                                                                            */
/* PORTABILITY: System.IO.Compression.DeflateStream is present in the .NET    */
/* Framework and in Mono, and this file's behaviour is verified on both at    */
/* every checkpoint -- a library that runs only on Windows would defeat the   */
/* purpose of writing it.                                                     */
/*----------------------------------------------------------------------------*/

using System;
using System.IO;
using System.IO.Compression;

namespace GraphicsEncDec
{
    /*------------------------------------------------------------------------*/
    /* ZlibStreamCodec:                                                       */
    /* Static-only.  Wraps and unwraps RFC 1950 framing around the            */
    /* framework's raw deflate implementation.                                */
    /*------------------------------------------------------------------------*/

    public static class ZlibStreamCodec
    {
        /*--------------------------------------------------------------------*/
        /* Header field values and structural constants.                      */
        /*--------------------------------------------------------------------*/

        private const int ZLIB_CM_DEFLATE   = 8;      // the only compression method RFC 1950 defines
        private const int ZLIB_CINFO_32K    = 7;      // log2(32768) - 8; the window every encoder uses
        private const int ZLIB_HEADER_BYTES = 2;      // CMF and FLG
        private const int ZLIB_ADLER_BYTES  = 4;      // big-endian Adler-32 trailer
        private const int ZLIB_CHECK_MODULUS = 31;    // (CMF * 256 + FLG) must be a multiple of this
        private const int ZLIB_FDICT_BIT    = 0x20;   // FLG bit 5: a preset dictionary follows

        /*--------------------------------------------------------------------*/
        /* FLEVEL, the two-bit effort hint in the top of FLG.  Decoders       */
        /* ignore it entirely; it is set correctly anyway so the header       */
        /* describes the stream honestly.                                     */
        /*--------------------------------------------------------------------*/

        private const int ZLIB_FLEVEL_FASTEST = 0;    // fastest algorithm
        private const int ZLIB_FLEVEL_FAST    = 1;    // fast
        private const int ZLIB_FLEVEL_DEFAULT = 2;    // default effort
        private const int ZLIB_FLEVEL_MAXIMUM = 3;    // maximum compression, slowest

        /*--------------------------------------------------------------------*/
        /* Default ceiling on inflate output, used when the caller passes no  */
        /* estimate.  64 MiB is far above any thumbnail this library will     */
        /* ever see and far below anything that would exhaust a machine.  A   */
        /* caller that knows the real figure should pass it and get a much    */
        /* tighter bound.                                                     */
        /*--------------------------------------------------------------------*/

        private const int ZLIB_DEFAULT_MAX_OUTPUT = 64 * 1024 * 1024;

        /*--------------------------------------------------------------------*/
        /* Copy-loop buffer size.  64 KiB is large enough that the per-call   */
        /* overhead of DeflateStream.Read disappears, and small enough to sit */
        /* comfortably below the large object heap threshold.                 */
        /*--------------------------------------------------------------------*/

        private const int ZLIB_COPY_CHUNK = 64 * 1024;

        /*--------------------------------------------------------------------*/
        /* ZLIB_TryDeflate:                                                   */
        /*                                                                    */
        /* Compresses a window of bytes into a complete zlib stream: header,  */
        /* deflate payload, Adler-32 trailer.                                 */
        /*                                                                    */
        /* The Adler-32 is computed over the INPUT, before compression, which */
        /* is what RFC 1950 specifies -- it checksums the data the decoder    */
        /* should end up with, not the compressed form.  Computing it over    */
        /* the wrong one produces a stream that every decoder rejects, and is */
        /* an easy mistake to make because both arrays are in hand here.      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     arrData  : Bytes to compress.  Must not be null.               */
        /*     iOffset  : Index of the first byte.                            */
        /*     iLength  : Count of bytes.  Zero is legal and produces a valid */
        /*                empty stream.                                       */
        /*     iLevel   : Effort, 0 to 9, in the conventional zlib sense: 0   */
        /*                stores without compressing, 1 is fastest, 9 is      */
        /*                smallest.  Values outside the range are clamped.    */
        /*                The framework offers only three real settings, so   */
        /*                the nine map onto those -- see below.               */
        /*     arrOut   : Receives the complete zlib stream; null on failure. */
        /*     sReason  : Receives "" on success, or the cause of failure.    */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool     : true when arrOut holds a complete zlib stream.      */
        /*--------------------------------------------------------------------*/

        public static bool ZLIB_TryDeflate(byte[] arrData, int iOffset, int iLength,
                                           int iLevel, out byte[] arrOut, out string sReason)
        {
            MemoryStream  msOut;
            DeflateStream dsCompress;
            ByteWriter    bwOut;
            byte[]        arrDeflated;
            uint          uAdler;
            int           iFlevel;
            int           iCmf;
            int           iFlg;

            arrOut  = null;
            sReason = "";

            if (arrData == null)
            {   /* Missing argument, not bad data. */
                throw new ArgumentNullException("arrData");
            }

            if (!ZLIB_TryValidateRange(arrData, iOffset, iLength, out sReason))
            {   /* The window does not lie inside the array; the check names why. */
                return(false);
            }

            try
            {
                /* Compress first, so a framework failure is reported before   */
                /* any header bytes are committed to the output.               */
                msOut = new MemoryStream();

                using (msOut)
                {
                    dsCompress = new DeflateStream(msOut, ZLIB_MapLevel(iLevel), true);

                    using (dsCompress)
                    {
                        dsCompress.Write(arrData, iOffset, iLength);
                    }

                    /* DeflateStream flushes its final block on DISPOSE, not   */
                    /* on Write, so the payload is only complete once the      */
                    /* using block above has closed.  Reading msOut before     */
                    /* that point yields a truncated stream -- which is the    */
                    /* single most common way to get this wrong.               */
                    arrDeflated = msOut.ToArray();
                }
            }
            catch (Exception exCaught)
            {   /* A framework-level compression failure; report rather than raise. */
                sReason = "deflate failed: " + exCaught.GetType().Name + ": " + exCaught.Message;
                return(false);
            }

            /* Adler-32 over the UNCOMPRESSED input, per RFC 1950. */
            uAdler = Adler32.ADLER_Compute(arrData, iOffset, iLength);

            /* Build the two header bytes.  CMF is fixed for our purposes;   */
            /* FLG carries the effort hint and then the check bits.          */
            iFlevel = ZLIB_MapFlevel(iLevel);
            iCmf    = (ZLIB_CINFO_32K << 4) | ZLIB_CM_DEFLATE;
            iFlg    = iFlevel << 6;
            iFlg    = iFlg | ZLIB_ComputeFcheck(iCmf, iFlg);

            bwOut = new ByteWriter(arrDeflated.Length + ZLIB_HEADER_BYTES + ZLIB_ADLER_BYTES);
            bwOut.BYTEW_WriteU8(iCmf);
            bwOut.BYTEW_WriteU8(iFlg);
            bwOut.BYTEW_WriteBytes(arrDeflated, 0, arrDeflated.Length);
            bwOut.BYTEW_WriteU32BE(uAdler);

            arrOut = bwOut.BYTEW_ToArray();

            /* A complete RFC 1950 stream: header, payload, checksum. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* ZLIB_TryInflate:                                                   */
        /*                                                                    */
        /* Decompresses a complete zlib stream, validating the header, the    */
        /* output size and the Adler-32 trailer.                              */
        /*                                                                    */
        /* All three validations matter and none is redundant.  The header    */
        /* catches "this is not a zlib stream"; the cap catches a             */
        /* decompression bomb before it exhausts memory; the trailer catches  */
        /* a payload that inflated without complaint but produced the wrong   */
        /* bytes.  Skipping any one of them leaves a real failure mode        */
        /* undetected.                                                        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     arrData        : The zlib stream.  Must not be null.           */
        /*     iOffset        : Index of the first byte of the stream.        */
        /*     iLength        : Byte count of the whole stream, header and    */
        /*                      trailer included.                             */
        /*     iMaxOutput     : Largest acceptable decompressed size.  Zero   */
        /*                      or negative uses the 64 MiB default.  A       */
        /*                      caller that knows the exact expected size --  */
        /*                      as a PNG decoder does, from IHDR -- should    */
        /*                      pass it, turning the cap from a backstop into */
        /*                      a real check.                                 */
        /*     arrOut         : Receives the decompressed bytes; null on      */
        /*                      failure.                                      */
        /*     sReason        : Receives "" on success, or the cause.         */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool           : true when the stream inflated and its         */
        /*                      checksum matched.                             */
        /*--------------------------------------------------------------------*/

        public static bool ZLIB_TryInflate(byte[] arrData, int iOffset, int iLength,
                                           int iMaxOutput, out byte[] arrOut, out string sReason)
        {
            MemoryStream  msIn;
            MemoryStream  msOut;
            DeflateStream dsExpand;
            byte[]        arrChunk;
            byte[]        arrResult;
            uint          uStoredAdler;
            uint          uActualAdler;
            long          lTotal;
            int           iCap;
            int           iPayloadStart;
            int           iPayloadLength;
            int           iRead;

            arrOut  = null;
            sReason = "";

            if (arrData == null)
            {   /* Missing argument, not bad data. */
                throw new ArgumentNullException("arrData");
            }

            if (!ZLIB_TryValidateRange(arrData, iOffset, iLength, out sReason))
            {   /* The window does not lie inside the array. */
                return(false);
            }

            if (iLength < (ZLIB_HEADER_BYTES + ZLIB_ADLER_BYTES))
            {   /* Too short to hold even an empty payload's framing. */
                sReason = "zlib stream is " + iLength.ToString() + " bytes, too short for a 2-byte header and 4-byte trailer";
                return(false);
            }

            if (!ZLIB_TryValidateHeader(arrData, iOffset, out sReason))
            {   /* Header is not a zlib header; the check names which field failed. */
                return(false);
            }

            if (iMaxOutput > 0)
            {   /* Caller supplied a real expectation; use it as the ceiling. */
                iCap = iMaxOutput;
            }
            else
            {   /* No expectation given; fall back to the generous default. */
                iCap = ZLIB_DEFAULT_MAX_OUTPUT;
            }

            /* The deflate payload sits between the header and the trailer. */
            iPayloadStart  = iOffset + ZLIB_HEADER_BYTES;
            iPayloadLength = iLength - ZLIB_HEADER_BYTES - ZLIB_ADLER_BYTES;

            try
            {
                msIn = new MemoryStream(arrData, iPayloadStart, iPayloadLength, false, false);

                using (msIn)
                {
                    msOut = new MemoryStream();

                    using (msOut)
                    {
                        dsExpand = new DeflateStream(msIn, CompressionMode.Decompress, true);

                        using (dsExpand)
                        {
                            arrChunk = new byte[ZLIB_COPY_CHUNK];
                            lTotal   = 0;

                            /* Copy by hand rather than with CopyTo, because   */
                            /* the cap has to be enforced DURING the copy.     */
                            /* CopyTo would happily expand a bomb to           */
                            /* completion and only then let us notice.         */
                            iRead = dsExpand.Read(arrChunk, 0, arrChunk.Length);

                            while (iRead > 0)
                            {   /* Account for the bytes first, so the cap is   */
                                /* tested before they are retained.             */
                                lTotal = lTotal + (long)iRead;

                                if (lTotal > (long)iCap)
                                {   /* Over the ceiling: stop immediately rather than finishing the expansion. */
                                    sReason = "zlib stream expands to more than the " + iCap.ToString() +
                                              " byte limit, refusing as a decompression bomb";
                                    return(false);
                                }

                                msOut.Write(arrChunk, 0, iRead);
                                iRead = dsExpand.Read(arrChunk, 0, arrChunk.Length);
                            }
                        }

                        arrResult = msOut.ToArray();
                    }
                }
            }
            catch (Exception exCaught)
            {   /* Malformed deflate data raises from the framework; that is   */
                /* ordinary bad input here, so it is reported not rethrown.    */
                sReason = "inflate failed: " + exCaught.GetType().Name + ": " + exCaught.Message;
                return(false);
            }

            /* The trailer is the last four bytes of the stream, big-endian. */
            uStoredAdler = ((uint)arrData[iOffset + iLength - 4] << 24) |
                           ((uint)arrData[iOffset + iLength - 3] << 16) |
                           ((uint)arrData[iOffset + iLength - 2] <<  8) |
                            (uint)arrData[iOffset + iLength - 1];

            uActualAdler = Adler32.ADLER_Compute(arrResult, 0, arrResult.Length);

            if (uActualAdler != uStoredAdler)
            {   /* Inflated without error but produced the wrong bytes -- the   */
                /* exact case DeflateStream cannot detect on its own.           */
                sReason = "zlib Adler-32 mismatch: stream says " + uStoredAdler.ToString("X8") +
                          ", data gives " + uActualAdler.ToString("X8");
                return(false);
            }

            arrOut = arrResult;

            /* Header valid, size within the cap, checksum matched. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* ZLIB_TryValidateHeader:                                            */
        /*                                                                    */
        /* Checks the two header bytes: the compression method, the multiple- */
        /* of-31 check, and the preset-dictionary flag.                       */
        /*                                                                    */
        /* FDICT is REFUSED rather than handled.  A preset dictionary means   */
        /* the stream can only be decoded by someone holding the same         */
        /* dictionary, which is not something PNG ever uses and not something */
        /* this library can supply.  Refusing with a clear reason beats       */
        /* attempting it and producing wrong pixels.                          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     arrData  : Buffer holding the stream.                          */
        /*     iOffset  : Index of the CMF byte.                              */
        /*     sReason  : Receives "" when the header is valid, or which      */
        /*                field failed.                                       */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool     : true when the header is a usable zlib header.       */
        /*--------------------------------------------------------------------*/

        private static bool ZLIB_TryValidateHeader(byte[] arrData, int iOffset, out string sReason)
        {
            int iCmf;
            int iFlg;

            sReason = "";

            iCmf = (int)arrData[iOffset];
            iFlg = (int)arrData[iOffset + 1];

            if ((iCmf & 0x0F) != ZLIB_CM_DEFLATE)
            {   /* CM is not 8, so this is not deflate and RFC 1950 defines nothing else. */
                sReason = "zlib compression method " + (iCmf & 0x0F).ToString() + " is not deflate";
                return(false);
            }

            if (((iCmf * 256) + iFlg) % ZLIB_CHECK_MODULUS != 0)
            {   /* The header's own consistency check failed -- usually means this is not a zlib stream at all. */
                sReason = "zlib header check failed: " + iCmf.ToString("X2") + iFlg.ToString("X2") +
                          " is not a multiple of 31";
                return(false);
            }

            if ((iFlg & ZLIB_FDICT_BIT) != 0)
            {   /* A preset dictionary we do not have and PNG never uses. */
                sReason = "zlib stream requires a preset dictionary, which is not supported";
                return(false);
            }

            /* Method, check bits and flags are all acceptable. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* ZLIB_ComputeFcheck:                                                */
        /*                                                                    */
        /* Finds the five-bit FCHECK value that makes the two header bytes a  */
        /* multiple of 31.                                                    */
        /*                                                                    */
        /* Computed by search rather than by arithmetic.  The closed form is  */
        /* a modular inverse that reads as line noise, the search runs at     */
        /* most 31 iterations once per compressed stream, and being able to   */
        /* see that it tests the actual condition is worth more here than the */
        /* microseconds.                                                      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iCmf      : The finished CMF byte.                             */
        /*     iFlgBase  : FLG with FLEVEL and FDICT set and FCHECK zero.     */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int       : The FCHECK bits, 0 to 30, to OR into FLG.          */
        /*--------------------------------------------------------------------*/

        private static int ZLIB_ComputeFcheck(int iCmf, int iFlgBase)
        {
            int i;

            for (i = 0; i < ZLIB_CHECK_MODULUS; i++)
            {   /* Try each candidate against the real condition. */
                if ((((iCmf * 256) + (iFlgBase | i)) % ZLIB_CHECK_MODULUS) == 0)
                {   /* This value makes the header pass its own check. */
                    return(i);
                }
            }

            /* Unreachable: for any iCmf and iFlgBase one of the 31 residues    */
            /* must work, because 256 and 31 are coprime.  Returning 0 rather   */
            /* than throwing keeps the encoder alive if that reasoning is       */
            /* ever wrong, and the resulting stream would fail its own header   */
            /* check loudly at the first decoder that reads it.                 */
            return(0);
        }

        /*--------------------------------------------------------------------*/
        /* ZLIB_MapLevel:                                                     */
        /*                                                                    */
        /* Maps a conventional zlib effort level, 0 to 9, onto the three      */
        /* settings the framework actually offers.                            */
        /*                                                                    */
        /* The lossy mapping is unavoidable and harmless: the level affects   */
        /* only how small the output is, never whether it decodes, and every  */
        /* zlib decoder in existence reads all three.  Accepting 0 to 9       */
        /* anyway keeps the public option field familiar to anyone who has    */
        /* used zlib, rather than exposing a framework detail through the     */
        /* API.                                                               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iLevel  : 0 to 9; values outside are clamped into range.       */
        /*                                                                    */
        /* Returns:                                                           */
        /*     CompressionLevel  : NoCompression for 0, Fastest for 1 and 2,  */
        /*                         Optimal for everything above.              */
        /*--------------------------------------------------------------------*/

        private static CompressionLevel ZLIB_MapLevel(int iLevel)
        {
            if (iLevel <= 0)
            {   /* Level 0 means store: deflate still frames it, but does not compress. */
                return(CompressionLevel.NoCompression);
            }

            if (iLevel <= 2)
            {   /* The fast end of the scale. */
                return(CompressionLevel.Fastest);
            }

            /* Everything from 3 upward, including zlib's default 6 and its   */
            /* maximum 9, gets the framework's best effort.                   */
            return(CompressionLevel.Optimal);
        }

        /*--------------------------------------------------------------------*/
        /* ZLIB_MapFlevel:                                                    */
        /*                                                                    */
        /* Maps the same 0-to-9 level onto the two-bit FLEVEL hint in the     */
        /* header, so the stream describes itself honestly even though no     */
        /* decoder reads the field.                                           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iLevel  : 0 to 9.                                              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int     : One of the ZLIB_FLEVEL_* values.                     */
        /*--------------------------------------------------------------------*/

        private static int ZLIB_MapFlevel(int iLevel)
        {
            if (iLevel <= 0)
            {   /* Stored: the fastest possible, since nothing is compressed. */
                return(ZLIB_FLEVEL_FASTEST);
            }

            if (iLevel <= 2)
            {   /* Fast. */
                return(ZLIB_FLEVEL_FAST);
            }

            if (iLevel <= 6)
            {   /* zlib's own default sits at 6, so this is the default band. */
                return(ZLIB_FLEVEL_DEFAULT);
            }

            /* 7 and above: maximum compression. */
            return(ZLIB_FLEVEL_MAXIMUM);
        }

        /*--------------------------------------------------------------------*/
        /* ZLIB_TryValidateRange:                                             */
        /*                                                                    */
        /* Checks an (array, offset, length) window, with the same long       */
        /* arithmetic used everywhere else a caller-supplied offset and       */
        /* length are added -- in int, the sum can wrap and pass a bounds     */
        /* test that should have failed.                                      */
        /*                                                                    */
        /* Local rather than calling IMGDEC_TryValidateWindow, because that   */
        /* one requires at least one byte and a zero-length deflate input is  */
        /* legitimate: compressing nothing produces a valid empty stream.     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     arrData  : Buffer the window refers into.                      */
        /*     iOffset  : Index of the first byte.                            */
        /*     iLength  : Byte count; zero is accepted here.                  */
        /*     sReason  : Receives "" when sound, or what is wrong.           */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool     : true when the window lies inside the array.         */
        /*--------------------------------------------------------------------*/

        private static bool ZLIB_TryValidateRange(byte[] arrData, int iOffset, int iLength,
                                                  out string sReason)
        {
            sReason = "";

            if (iOffset < 0)
            {   /* A negative start cannot refer into any array. */
                sReason = "offset " + iOffset.ToString() + " is negative";
                return(false);
            }

            if (iLength < 0)
            {   /* Negative length is meaningless; zero is fine. */
                sReason = "length " + iLength.ToString() + " is negative";
                return(false);
            }

            if (((long)iOffset + (long)iLength) > (long)arrData.Length)
            {   /* The window runs past the end of the array. */
                sReason = "offset " + iOffset.ToString() + " plus length " + iLength.ToString() +
                          " runs past the end of a " + arrData.Length.ToString() + " byte buffer";
                return(false);
            }

            /* The window lies wholly inside the array. */
            return(true);
        }
    }
}
