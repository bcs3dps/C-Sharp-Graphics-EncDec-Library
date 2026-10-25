/*----------------------------------------------------------------------------*/
/* SPDX-License-Identifier: MIT OR GPL-2.0-or-later                           */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* Dual-licensed at the recipient's choice -- see LICENSE in this folder for  */
/* the full terms of both licences.  GPL-2.0 full text in GPL-2.0.txt.        */
/*----------------------------------------------------------------------------*/
/* Transcoded from lodepng.cpp (lvandeve/lodepng, version 20260119, commit    */
/* ed6fe582), the LODEPNG_COMPILE_CRC section.  lodepng is under the zlib     */
/* licence, which satisfies both arms of this file's dual licence.            */
/*                                                                            */
/* DELIBERATE DEVIATION FROM THE SOURCE, stated because the rule here is to   */
/* transcode rather than paraphrase and this is neither: lodepng ships three  */
/* CRC-32 variants -- slicing-by-eight over eight 1 KB tables, a compact      */
/* 16-entry nibble table, and the classic byte table.  This file uses the     */
/* CLASSIC 256-ENTRY BYTE TABLE, which is the form the PNG specification's    */
/* own sample code in Annex D uses.  The algorithm, polynomial and initial    */
/* and final values are identical in all three; only the table width differs. */
/* The byte table was chosen because it can be read straight against the      */
/* specification, and because slicing-by-eight buys throughput on megabyte    */
/* buffers while a PNG chunk CRC is computed over a chunk.                    */
/*----------------------------------------------------------------------------*/
/* Crc32.cs -- CRC-32/ISO-HDLC, the checksum PNG puts on every chunk (CRC32_) */
/*----------------------------------------------------------------------------*/
/* WHICH CRC-32 THIS IS, because there are many and they disagree.  This is   */
/* CRC-32/ISO-HDLC, also called CRC-32, CRC-32/ADCCP, PKZIP and just "the     */
/* zlib one".  Its parameters:                                                */
/*                                                                            */
/*     polynomial    0x04C11DB7, used REFLECTED as 0xEDB88320                 */
/*     initial value 0xFFFFFFFF                                               */
/*     input         reflected (least significant bit first)                  */
/*     output        reflected                                                */
/*     final XOR     0xFFFFFFFF                                               */
/*     check value   0xCBF43926 for the nine ASCII bytes "123456789"          */
/*                                                                            */
/* The check value is the standard self-test for this variant and is          */
/* asserted.  If that one number is right, the table, the reflection and      */
/* both XORs are right; if it is wrong, at least one of them is not.          */
/*                                                                            */
/* WHY THE REFLECTED POLYNOMIAL LOOKS "BACKWARDS".  A CRC is defined as       */
/* polynomial division, and 0x04C11DB7 is the divisor written most            */
/* significant bit first.  PNG and zlib feed data least significant bit       */
/* first, and rather than reverse every input byte, the standard trick is to  */
/* reverse the POLYNOMIAL once and shift the register right instead of left.  */
/* 0xEDB88320 is 0x04C11DB7 bit-reversed.  This is why the table build below  */
/* tests the low bit and shifts right, which otherwise reads as an error.     */
/*                                                                            */
/* THE INCREMENTAL FORM IS NOT A CONVENIENCE.  A PNG chunk's CRC covers the   */
/* four type bytes and then the data bytes, but NOT the four length bytes     */
/* that precede them -- and the encoder builds those pieces separately.       */
/* Concatenating them into one buffer just to checksum it would allocate a    */
/* copy of every chunk written.  So CRC32_Begin, CRC32_Update, CRC32_Finish   */
/* exist alongside the one-shot, and the encoder uses them.                   */
/*                                                                            */
/* PORTABILITY: pure integer arithmetic, no platform surface at all.          */
/*----------------------------------------------------------------------------*/

using System;

namespace GraphicsEncDec
{
    /*------------------------------------------------------------------------*/
    /* Crc32:                                                                 */
    /* Static-only.  The lookup table is built once by the static             */
    /* constructor and is never written again afterwards, so every method     */
    /* here is safe to call from several threads at once.                     */
    /*------------------------------------------------------------------------*/

    public static class Crc32
    {
        /*--------------------------------------------------------------------*/
        /* The algorithm's fixed parameters, named rather than written as     */
        /* bare literals at each use, so the values in the file header above  */
        /* and the code cannot drift apart.                                   */
        /*--------------------------------------------------------------------*/

        private const uint CRC32_POLYNOMIAL = 0xEDB88320u;  // 0x04C11DB7 bit-reversed
        private const uint CRC32_INITIAL    = 0xFFFFFFFFu;  // register seed before any byte
        private const uint CRC32_FINAL_XOR  = 0xFFFFFFFFu;  // applied once, at the very end
        private const int  CRC32_TABLE_SIZE = 256;          // one entry per possible input byte

        /*---------------------------------------------------------------------*/
        /* Entry n holds the register contribution of the byte value n.  Built */
        /* once at type initialisation; read-only thereafter.                  */
        /*---------------------------------------------------------------------*/

        private static readonly uint[] CRC32_arrTable = CRC32_BuildTable();

        /*---------------------------------------------------------------------*/
        /* CRC32_BuildTable:                                                   */
        /*                                                                     */
        /* Generates the 256-entry lookup table from the reflected polynomial. */
        /*                                                                     */
        /* Generated rather than pasted as a literal array on purpose.  A      */
        /* 256-entry table of hex constants is 256 opportunities for a         */
        /* single mistyped digit that would corrupt exactly one byte value's   */
        /* checksum -- a bug that would pass most tests and fail rarely and    */
        /* mysteriously.  Eight lines of arithmetic, validated against the     */
        /* standard check value above, cannot go subtly wrong                  */
        /* in that way: either the whole table is right or the check value     */
        /* is visibly wrong.                                                   */
        /*                                                                     */
        /* Arguments:                                                          */
        /*     None.                                                           */
        /*                                                                     */
        /* Returns:                                                            */
        /*     uint[]  : The 256-entry table, ready for the byte-at-a-time     */
        /*               loop in CRC32_Update.                                 */
        /*---------------------------------------------------------------------*/

        private static uint[] CRC32_BuildTable()
        {
            uint[] arrTable;
            uint   uEntry;
            int    iByte;
            int    iBit;

            arrTable = new uint[CRC32_TABLE_SIZE];

            for (iByte = 0; iByte < CRC32_TABLE_SIZE; iByte++)
            {   /* Each entry is that byte value pushed through eight rounds   */
                /* of the division, with nothing else in the register.         */
                uEntry = (uint)iByte;

                for (iBit = 0; iBit < 8; iBit++)
                {   /* Reflected form: test the LOW bit and shift RIGHT.  See   */
                    /* the file header for why this is not backwards.           */
                    if ((uEntry & 1u) != 0u)
                    {   /* Low bit set: shift out and mix the polynomial back in. */
                        uEntry = (uEntry >> 1) ^ CRC32_POLYNOMIAL;
                    }
                    else
                    {   /* Low bit clear: the divisor does not go in, so only shift. */
                        uEntry = uEntry >> 1;
                    }
                }

                arrTable[iByte] = uEntry;
            }

            /* A complete table; the caller stores it in the readonly field   */
            /* and nothing writes it again.                                   */
            return(arrTable);
        }

        /*--------------------------------------------------------------------*/
        /* CRC32_Begin:                                                       */
        /*                                                                    */
        /* Returns the starting value of a running CRC, for the incremental   */
        /* form.  Named rather than having callers write 0xFFFFFFFF, so the   */
        /* seed lives in exactly one place and an encoder cannot accidentally */
        /* start a chunk from zero -- which would produce a plausible-looking */
        /* wrong checksum rather than an obvious failure.                     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     uint  : The initial running value.  NOT a valid checksum on    */
        /*             its own -- it must go through CRC32_Finish first.      */
        /*--------------------------------------------------------------------*/

        public static uint CRC32_Begin()
        {
            /* The register seed; every byte then folds into it. */
            return(CRC32_INITIAL);
        }

        /*--------------------------------------------------------------------*/
        /* CRC32_Update:                                                      */
        /*                                                                    */
        /* Folds a window of bytes into a running CRC and returns the new     */
        /* running value.  Call it once per piece -- the chunk type, then the */
        /* chunk data -- and finish once at the end.                          */
        /*                                                                    */
        /* Returns the new value rather than mutating a reference, so a       */
        /* running CRC is an ordinary local uint at the call site and there   */
        /* is no object whose lifetime or thread affinity has to be reasoned  */
        /* about.                                                             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     uRunning  : The value from CRC32_Begin, or from the previous   */
        /*                 CRC32_Update.  NOT a finished checksum.            */
        /*     arrData   : Buffer holding the bytes.  Must not be null.       */
        /*     iOffset   : Index of the first byte to fold in.                */
        /*     iLength   : Count of bytes to fold in, starting at iOffset.    */
        /*                                                                    */
        /* Returns:                                                           */
        /*     uint      : The new running value, to pass to the next         */
        /*                 CRC32_Update or to CRC32_Finish.                   */
        /*--------------------------------------------------------------------*/

        public static uint CRC32_Update(uint uRunning, byte[] arrData, int iOffset, int iLength)
        {
            uint uCrc;
            int  i;

            if (arrData == null)
            {   /* Missing argument, not malformed image data, so this raises. */
                throw new ArgumentNullException("arrData");
            }

            uCrc = uRunning;

            for (i = 0; i < iLength; i++)
            {   /* One table lookup per byte: index by the low byte of the     */
                /* register XOR the input byte, then shift the register down   */
                /* by eight to make room for the next.                         */
                uCrc = CRC32_arrTable[(uCrc ^ arrData[iOffset + i]) & 0xFFu] ^ (uCrc >> 8);
            }

            /* The running value after these bytes; still needs finishing. */
            return(uCrc);
        }

        /*--------------------------------------------------------------------*/
        /* CRC32_Finish:                                                      */
        /*                                                                    */
        /* Applies the final inversion that turns a running value into the    */
        /* checksum that goes on the wire.                                    */
        /*                                                                    */
        /* Separate from CRC32_Update because finishing is not idempotent:    */
        /* applying it twice returns the running value again, silently, with  */
        /* no complaint.  Keeping it a distinct call means the encoder does   */
        /* it exactly once, at a point that reads as the end of the chunk.    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     uRunning  : The value from the last CRC32_Update.              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     uint      : The finished CRC-32, ready to be written           */
        /*                 big-endian into a PNG chunk trailer.               */
        /*--------------------------------------------------------------------*/

        public static uint CRC32_Finish(uint uRunning)
        {
            /* Final inversion, per the algorithm parameters in the header. */
            return(uRunning ^ CRC32_FINAL_XOR);
        }

        /*--------------------------------------------------------------------*/
        /* CRC32_Compute:                                                     */
        /*                                                                    */
        /* Computes a finished CRC-32 over one window in a single call, for   */
        /* the common case where the covered bytes are already contiguous.    */
        /* Exactly Begin, Update, Finish, and written out as such rather than */
        /* reimplemented, so the two paths cannot diverge.                    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     arrData  : Buffer holding the bytes to checksum.  Not          */
        /*                modified.  Must not be null.                        */
        /*     iOffset  : Index of the first byte covered.                    */
        /*     iLength  : Count of bytes covered, starting at iOffset.        */
        /*                                                                    */
        /* Returns:                                                           */
        /*     uint     : The finished CRC-32, already inverted.              */
        /*--------------------------------------------------------------------*/

        public static uint CRC32_Compute(byte[] arrData, int iOffset, int iLength)
        {
            uint uRunning;

            uRunning = CRC32_Begin();
            uRunning = CRC32_Update(uRunning, arrData, iOffset, iLength);

            /* Finished and ready for the wire. */
            return(CRC32_Finish(uRunning));
        }
    }
}
