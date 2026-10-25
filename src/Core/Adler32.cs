/*----------------------------------------------------------------------------*/
/* SPDX-License-Identifier: MIT OR GPL-2.0-or-later                           */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* Dual-licensed at the recipient's choice -- see LICENSE in this folder for  */
/* the full terms of both licences.  GPL-2.0 full text in GPL-2.0.txt.        */
/*----------------------------------------------------------------------------*/
/* Transcoded from lodepng.cpp (lvandeve/lodepng, version 20260119, commit    */
/* ed6fe582), its update_adler32 and adler32 functions.  Structure and        */
/* variable roles are kept -- the s1 and s2 accumulators, the 5552-byte       */
/* blocking, the deferred modulo -- so this file can be diffed against its    */
/* source when it misbehaves.  lodepng is under the zlib licence, which       */
/* satisfies both arms of this file's dual licence.                           */
/*----------------------------------------------------------------------------*/
/* Adler32.cs -- the RFC 1950 checksum that terminates a zlib stream (ADLER_) */
/*----------------------------------------------------------------------------*/
/* WHAT IT IS.  Two running sums, defined in RFC 1950 section 9:              */
/*                                                                            */
/*     s1 = 1 + b1 + b2 + ... + bn            (mod 65521)                     */
/*     s2 = (1 + b1) + (1 + b1 + b2) + ...    (mod 65521)                     */
/*                                                                            */
/* and the checksum is s2 in the high sixteen bits, s1 in the low sixteen.    */
/* s1 starts at 1, not 0 -- which is why the checksum of an EMPTY input is 1  */
/* rather than 0, a distinction that matters because it is exactly what       */
/* distinguishes "empty stream" from "uninitialised variable".                */
/*                                                                            */
/* WHY 65521.  It is the largest prime below 2^16.  A prime modulus makes the */
/* sum sensitive to byte ORDER as well as byte VALUES, which a plain sum is   */
/* not -- swap two bytes and s1 is unchanged but s2 moves.  That is the whole */
/* point of the second accumulator.                                           */
/*                                                                            */
/* WHY 5552, WHICH IS THE ONLY NON-OBVIOUS NUMBER HERE.  Taking the modulo    */
/* after every byte would cost two divisions per byte, and divisions are the  */
/* slowest integer operation on most processors.  Instead the sums are        */
/* allowed to run unreduced for as long as they provably cannot overflow a    */
/* 32-bit unsigned, and reduced once per block.  5552 is the largest n for    */
/* which the worst case -- every byte 0xFF -- keeps s2 inside 32 bits:        */
/*                                                                            */
/*     n * (n + 1) / 2 * 255 + (n + 1) * 65520  <  2^32                       */
/*                                                                            */
/* holds at n = 5552 and fails at 5553.  It is not a round number and it is   */
/* not arbitrary; it comes straight from zlib and is reproduced by lodepng.   */
/*                                                                            */
/* THE ROLLOVER IS WORTH A TEST OF ITS OWN.  An input shorter than 5552 bytes */
/* never exercises the blocking loop at all, so an implementation that got    */
/* the block boundary wrong would pass every small test and fail only on real */
/* image data, so this is tested against an input longer than one block.      */
/*                                                                            */
/* PORTABILITY: pure integer arithmetic, no platform surface at all.          */
/*----------------------------------------------------------------------------*/

using System;

namespace GraphicsEncDec
{
    /*------------------------------------------------------------------------*/
    /* Adler32:                                                               */
    /* Static-only and stateless -- a running checksum is carried in a plain  */
    /* uint by the caller, exactly as with Crc32, so there is no object to    */
    /* own and no thread affinity to reason about.                            */
    /*------------------------------------------------------------------------*/

    public static class Adler32
    {
        /*--------------------------------------------------------------------*/
        /* The algorithm's fixed parameters, named rather than written as     */
        /* bare literals, so the values explained in the header above and the */
        /* code below cannot drift apart.                                     */
        /*--------------------------------------------------------------------*/

        private const uint ADLER_MODULUS   = 65521u;   // largest prime below 2^16
        private const uint ADLER_INITIAL   = 1u;       // s1 starts at 1, s2 at 0
        private const int  ADLER_BLOCK_MAX = 5552;     // most bytes that can run unreduced

        /*--------------------------------------------------------------------*/
        /* ADLER_Begin:                                                       */
        /*                                                                    */
        /* Returns the starting value of a running Adler-32.  Named rather    */
        /* than having callers write 1, because a seed of 0 here produces a   */
        /* checksum that looks entirely reasonable and is wrong for every     */
        /* input -- the worst kind of mistake to leave available.             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     uint  : The initial running value, which is also the correct   */
        /*             checksum of a zero-byte input.                         */
        /*--------------------------------------------------------------------*/

        public static uint ADLER_Begin()
        {
            /* s1 = 1, s2 = 0, packed the same way as any other running value. */
            return(ADLER_INITIAL);
        }

        /*--------------------------------------------------------------------*/
        /* ADLER_Update:                                                      */
        /*                                                                    */
        /* Folds a window of bytes into a running Adler-32 and returns the    */
        /* new running value.                                                 */
        /*                                                                    */
        /* Unlike CRC-32 there is no separate finish step, because Adler-32   */
        /* has no final inversion -- the running value IS the checksum at     */
        /* every point.  That asymmetry between the two primitives is real    */
        /* and is not an oversight here; adding a no-op ADLER_Finish for      */
        /* symmetry would suggest a transformation that does not exist.       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     uRunning  : The value from ADLER_Begin, or from the previous   */
        /*                 ADLER_Update.                                      */
        /*     arrData   : Buffer holding the bytes.  Must not be null.       */
        /*     iOffset   : Index of the first byte to fold in.                */
        /*     iLength   : Count of bytes to fold in, starting at iOffset.    */
        /*                                                                    */
        /* Returns:                                                           */
        /*     uint      : The new running value, which is also a valid       */
        /*                 checksum of everything folded in so far.           */
        /*--------------------------------------------------------------------*/

        public static uint ADLER_Update(uint uRunning, byte[] arrData, int iOffset, int iLength)
        {
            uint uS1;
            uint uS2;
            int  iRemaining;
            int  iBlock;
            int  iPos;
            int  i;

            if (arrData == null)
            {   /* Missing argument, not malformed image data, so this raises. */
                throw new ArgumentNullException("arrData");
            }

            /* Unpack the two accumulators out of the caller's running value. */
            uS1 = uRunning & 0xFFFFu;
            uS2 = (uRunning >> 16) & 0xFFFFu;

            iRemaining = iLength;
            iPos       = iOffset;

            while (iRemaining > 0)
            {   /* Process at most one block's worth before reducing, so the   */
                /* inner loop pays no division at all.                         */
                if (iRemaining > ADLER_BLOCK_MAX)
                {   /* More than a block left: take a full block this time round. */
                    iBlock = ADLER_BLOCK_MAX;
                }
                else
                {   /* Final, short block: take whatever remains. */
                    iBlock = iRemaining;
                }

                iRemaining = iRemaining - iBlock;

                for (i = 0; i < iBlock; i++)
                {   /* s1 accumulates the bytes; s2 accumulates s1, which is   */
                    /* what makes the result order-sensitive.  Neither can     */
                    /* overflow within a block -- that is what 5552 buys.      */
                    uS1 = uS1 + arrData[iPos];
                    uS2 = uS2 + uS1;
                    iPos = iPos + 1;
                }

                /* Two divisions per BLOCK rather than per byte. */
                uS1 = uS1 % ADLER_MODULUS;
                uS2 = uS2 % ADLER_MODULUS;
            }

            /* Repack: s2 high, s1 low, exactly as it goes on the wire. */
            return((uS2 << 16) | uS1);
        }

        /*--------------------------------------------------------------------*/
        /* ADLER_Compute:                                                     */
        /*                                                                    */
        /* Computes an Adler-32 over one window in a single call.  Exactly    */
        /* Begin then Update, written out as such rather than reimplemented,  */
        /* so the one-shot and incremental paths cannot diverge.              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     arrData  : Buffer holding the bytes to checksum.  Not          */
        /*                modified.  Must not be null.                        */
        /*     iOffset  : Index of the first byte covered.                    */
        /*     iLength  : Count of bytes covered, starting at iOffset.        */
        /*                                                                    */
        /* Returns:                                                           */
        /*     uint     : The Adler-32, ready to be written big-endian as a   */
        /*                zlib stream's four-byte trailer.                    */
        /*--------------------------------------------------------------------*/

        public static uint ADLER_Compute(byte[] arrData, int iOffset, int iLength)
        {
            uint uRunning;

            uRunning = ADLER_Begin();

            /* No finish step exists for Adler-32; the running value is the   */
            /* checksum, so this is the answer as it stands.                  */
            return(ADLER_Update(uRunning, arrData, iOffset, iLength));
        }
    }
}
