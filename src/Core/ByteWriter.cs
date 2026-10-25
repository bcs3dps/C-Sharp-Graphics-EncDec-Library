/*----------------------------------------------------------------------------*/
/* SPDX-License-Identifier: MIT OR GPL-2.0-or-later                           */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* Dual-licensed at the recipient's choice -- see LICENSE in this folder for  */
/* the full terms of both licences.  GPL-2.0 full text in GPL-2.0.txt.        */
/*----------------------------------------------------------------------------*/
/* ByteWriter.cs -- a growable output buffer for encoders           (BYTEW_)  */
/*----------------------------------------------------------------------------*/
/* The mirror of ByteReader, and every encoder's output sink.  Method names   */
/* pair one for one with the reader's, so an encoder and its decoder read as  */
/* reflections of each other and a mismatched endianness between the two      */
/* shows up as a visible asymmetry in the source rather than as a corrupt     */
/* file.                                                                      */
/*                                                                            */
/* WHY NOT MemoryStream.  It would work, and it was considered.  Three        */
/* reasons against it, in order of weight:                                    */
/*                                                                            */
/*   1. It has no big-endian writes, so every encoder would spell out the     */
/*      shifts at each call site -- which is exactly where an endianness bug  */
/*      hides, and exactly what naming BYTEW_WriteU32BE prevents.             */
/*   2. ToArray() always copies.  A PNG encoder needs to CRC the bytes it     */
/*      just wrote, and doing that through ToArray would copy the whole       */
/*      output once per chunk.  BYTEW_Buffer exposes the live array for       */
/*      exactly that case.                                                    */
/*   3. Writing our own keeps the library's dependency surface at System.dll  */
/*      and its behaviour identical on Mono, which is the whole point of the  */
/*      exercise: this library exists to run where System.Drawing does not.   */
/*                                                                            */
/* GROWTH IS BY DOUBLING, which keeps the total copying done over the life of */
/* a buffer proportional to its final size rather than to its size squared.   */
/* An encoder that knows roughly how big its output will be should say so at  */
/* construction and avoid the regrowth entirely.                              */
/*                                                                            */
/* PORTABILITY: pure array handling and integer arithmetic.  No BitConverter, */
/* whose byte order follows the host; the shifts below are explicit.          */
/*----------------------------------------------------------------------------*/

using System;

namespace GraphicsEncDec
{
    /*------------------------------------------------------------------------*/
    /* ByteWriter:                                                            */
    /* An append-only, self-growing byte buffer.  Sealed, for the same        */
    /* reason as ByteReader: the length and capacity invariant is the whole   */
    /* contract and a subclass could break it where no encoder would check.   */
    /*------------------------------------------------------------------------*/

    public sealed class ByteWriter
    {
        /*--------------------------------------------------------------------*/
        /* Used when the caller gives no useful hint.  256 bytes is enough    */
        /* for a PNG header plus its first chunks, or a whole QOI header, so  */
        /* the common small cases never regrow at all.                        */
        /*--------------------------------------------------------------------*/

        private const int BYTEW_DEFAULT_CAPACITY = 256;  // bytes, before the first growth

        /*--------------------------------------------------------------------*/
        /* The buffer and how much of it is live.  BYTEW_arrBuffer.Length is  */
        /* the CAPACITY; BYTEW_iCount is the LENGTH.  Everything at or past   */
        /* BYTEW_iCount is stale and must never be handed out.                */
        /*--------------------------------------------------------------------*/

        private byte[] BYTEW_arrBuffer;                  // grows by doubling; may hold stale tail bytes
        private int    BYTEW_iCount;                     // bytes actually written so far

        /*--------------------------------------------------------------------*/
        /* ByteWriter (constructor):                                          */
        /*                                                                    */
        /* Creates an empty writer.  One constructor only, taking a capacity  */
        /* hint, because a parameterless sibling would be a second callable   */
        /* sharing this name -- so a caller with no useful estimate passes 0  */
        /* and gets the default.                                              */
        /*                                                                    */
        /* The hint is worth giving where it is known.  An encoder that has   */
        /* already computed its output size, as QOI's worst case and BMP's    */
        /* exact size both can be, avoids every reallocation and every copy   */
        /* by passing it here.                                                */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iInitialCapacity  : Expected output size in bytes.  Zero or    */
        /*                         negative means "no estimate", and the      */
        /*                         default is used.  Being wrong costs only   */
        /*                         a regrowth, never correctness.             */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void              : An empty writer.                           */
        /*--------------------------------------------------------------------*/

        public ByteWriter(int iInitialCapacity)
        {
            int iCapacity;

            if (iInitialCapacity > 0)
            {   /* Caller supplied a real estimate; trust it. */
                iCapacity = iInitialCapacity;
            }
            else
            {   /* No estimate, or a nonsensical one; fall back to the default. */
                iCapacity = BYTEW_DEFAULT_CAPACITY;
            }

            BYTEW_arrBuffer = new byte[iCapacity];
            BYTEW_iCount    = 0;
        }

        /*--------------------------------------------------------------------*/
        /* BYTEW_Length:                                                      */
        /*                                                                    */
        /* Bytes written so far.  Note this is the LENGTH, not the capacity;  */
        /* the underlying array is usually larger and its tail is stale.      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int  : Count of live bytes, from index 0.                      */
        /*--------------------------------------------------------------------*/

        public int BYTEW_Length()
        {
            /* The write cursor, which is also the live length. */
            return(BYTEW_iCount);
        }

        /*--------------------------------------------------------------------*/
        /* BYTEW_Buffer:                                                      */
        /*                                                                    */
        /* Exposes the LIVE internal array, without copying.                  */
        /*                                                                    */
        /* *** THE ARRAY IS LONGER THAN THE DATA. *** Only the first          */
        /* BYTEW_Length() bytes are meaningful; everything after them is      */
        /* stale, and a caller that uses Length on this array instead of      */
        /* BYTEW_Length will read rubbish.  The reference is also invalidated */
        /* by the next write that triggers a growth.                          */
        /*                                                                    */
        /* This exists for one reason and should be used for one reason: a    */
        /* PNG encoder must CRC the chunk bytes it has just written, and      */
        /* going through BYTEW_ToArray to do that would copy the entire       */
        /* output once per chunk.  For anything else, use BYTEW_ToArray.      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     byte[]  : The live buffer.  Valid only up to BYTEW_Length(),   */
        /*               and only until the next write that grows it.         */
        /*--------------------------------------------------------------------*/

        public byte[] BYTEW_Buffer()
        {
            /* Deliberately the live array, with the caveats above. */
            return(BYTEW_arrBuffer);
        }

        /*--------------------------------------------------------------------*/
        /* BYTEW_ToArray:                                                     */
        /*                                                                    */
        /* Returns a new array holding exactly the bytes written, trimmed to  */
        /* the live length.  This is what an encoder hands back to its caller */
        /* as the finished file.                                              */
        /*                                                                    */
        /* Always copies, and always should: handing out the internal buffer  */
        /* as a result would give the caller an array whose length lies about */
        /* its contents, and one that a later write could still mutate.       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     byte[]  : A new array of exactly BYTEW_Length() bytes, owned   */
        /*               outright by the caller.                              */
        /*--------------------------------------------------------------------*/

        public byte[] BYTEW_ToArray()
        {
            byte[] arrExact;

            arrExact = new byte[BYTEW_iCount];
            Array.Copy(BYTEW_arrBuffer, 0, arrExact, 0, BYTEW_iCount);

            /* Exactly the live bytes, in an array the caller owns. */
            return(arrExact);
        }

        /*--------------------------------------------------------------------*/
        /* BYTEW_Reserve:                                                     */
        /*                                                                    */
        /* Ensures the buffer can hold iExtra more bytes, growing it if not.  */
        /* Private: every public write calls it first, so no encoder ever has */
        /* to think about capacity.                                           */
        /*                                                                    */
        /* Growth doubles until the requirement fits, rather than growing to  */
        /* exactly what is needed.  Growing exactly would make an encoder     */
        /* that appends one byte at a time reallocate and copy on EVERY byte, */
        /* turning a linear encode into a quadratic one -- which on a         */
        /* megapixel image is the difference between milliseconds and         */
        /* minutes.                                                           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iExtra  : Additional bytes about to be written.                */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void    : The buffer is large enough on return, or the method  */
        /*               threw because it could not be made so.               */
        /*--------------------------------------------------------------------*/

        private void BYTEW_Reserve(int iExtra)
        {
            byte[] arrGrown;
            long   lNeeded;
            long   lCapacity;

            lNeeded = (long)BYTEW_iCount + (long)iExtra;

            if (lNeeded <= (long)BYTEW_arrBuffer.Length)
            {   /* Already big enough -- the overwhelmingly common case, and it allocates nothing. */
                return;
            }

            if (lNeeded > (long)int.MaxValue)
            {   /* Beyond any possible array.  An encoder producing two   */
                /* gigabytes from an image this library would accept      */
                /* means something above is badly wrong, so this raises   */
                /* rather than reporting.                                 */
                throw new InvalidOperationException("ByteWriter output would exceed the maximum array length");
            }

            lCapacity = (long)BYTEW_arrBuffer.Length;

            while (lCapacity < lNeeded)
            {   /* Double until it fits.  The loop, rather than a single    */
                /* doubling, covers a write far larger than the current     */
                /* capacity -- a whole scanline into a fresh writer, say.   */
                lCapacity = lCapacity * 2;

                if (lCapacity > (long)int.MaxValue)
                {   /* Doubling overshot the array limit; settle for exactly what is needed. */
                    lCapacity = lNeeded;
                }
            }

            arrGrown = new byte[(int)lCapacity];
            Array.Copy(BYTEW_arrBuffer, 0, arrGrown, 0, BYTEW_iCount);
            BYTEW_arrBuffer = arrGrown;
        }

        /*--------------------------------------------------------------------*/
        /* BYTEW_WriteU8:                                                     */
        /*                                                                    */
        /* Appends one byte.  Takes an int for symmetry with                  */
        /* BYTER_TryReadU8 and because callers almost always have an int in   */
        /* hand; only the low eight bits are used, and higher bits are        */
        /* discarded silently.                                                */
        /*                                                                    */
        /* Silently rather than by refusing, because masking to eight bits is */
        /* what every caller wants at these sites -- a computed pixel value,  */
        /* a packed bitfield -- and a check would turn ordinary arithmetic    */
        /* into an error path with no benefit.                                */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iValue  : Value whose low eight bits are appended.             */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void    : The byte is appended and the length grows by one.    */
        /*--------------------------------------------------------------------*/

        public void BYTEW_WriteU8(int iValue)
        {
            BYTEW_Reserve(1);

            BYTEW_arrBuffer[BYTEW_iCount] = (byte)(iValue & 0xFF);
            BYTEW_iCount = BYTEW_iCount + 1;
        }

        /*--------------------------------------------------------------------*/
        /* BYTEW_WriteU16BE:                                                  */
        /*                                                                    */
        /* Appends a sixteen-bit value most significant byte first, the order */
        /* PNG, JPEG and QOI use.                                             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iValue  : Value whose low sixteen bits are appended.           */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void    : Two bytes appended.                                  */
        /*--------------------------------------------------------------------*/

        public void BYTEW_WriteU16BE(int iValue)
        {
            BYTEW_Reserve(2);

            BYTEW_arrBuffer[BYTEW_iCount]     = (byte)((iValue >> 8) & 0xFF);
            BYTEW_arrBuffer[BYTEW_iCount + 1] = (byte)(iValue & 0xFF);
            BYTEW_iCount = BYTEW_iCount + 2;
        }

        /*--------------------------------------------------------------------*/
        /* BYTEW_WriteU16LE:                                                  */
        /*                                                                    */
        /* Appends a sixteen-bit value least significant byte first, the      */
        /* order GIF and BMP use.                                             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iValue  : Value whose low sixteen bits are appended.           */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void    : Two bytes appended.                                  */
        /*--------------------------------------------------------------------*/

        public void BYTEW_WriteU16LE(int iValue)
        {
            BYTEW_Reserve(2);

            BYTEW_arrBuffer[BYTEW_iCount]     = (byte)(iValue & 0xFF);
            BYTEW_arrBuffer[BYTEW_iCount + 1] = (byte)((iValue >> 8) & 0xFF);
            BYTEW_iCount = BYTEW_iCount + 2;
        }

        /*--------------------------------------------------------------------*/
        /* BYTEW_WriteU32BE:                                                  */
        /*                                                                    */
        /* Appends a thirty-two-bit value most significant byte first.  Every */
        /* PNG chunk length and every PNG CRC goes out through this method.   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     uValue  : The value to append.                                 */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void    : Four bytes appended.                                 */
        /*--------------------------------------------------------------------*/

        public void BYTEW_WriteU32BE(uint uValue)
        {
            BYTEW_Reserve(4);

            BYTEW_arrBuffer[BYTEW_iCount]     = (byte)((uValue >> 24) & 0xFFu);
            BYTEW_arrBuffer[BYTEW_iCount + 1] = (byte)((uValue >> 16) & 0xFFu);
            BYTEW_arrBuffer[BYTEW_iCount + 2] = (byte)((uValue >>  8) & 0xFFu);
            BYTEW_arrBuffer[BYTEW_iCount + 3] = (byte)(uValue & 0xFFu);
            BYTEW_iCount = BYTEW_iCount + 4;
        }

        /*--------------------------------------------------------------------*/
        /* BYTEW_WriteU32LE:                                                  */
        /*                                                                    */
        /* Appends a thirty-two-bit value least significant byte first, the   */
        /* order BMP's header fields use.                                     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     uValue  : The value to append.                                 */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void    : Four bytes appended.                                 */
        /*--------------------------------------------------------------------*/

        public void BYTEW_WriteU32LE(uint uValue)
        {
            BYTEW_Reserve(4);

            BYTEW_arrBuffer[BYTEW_iCount]     = (byte)(uValue & 0xFFu);
            BYTEW_arrBuffer[BYTEW_iCount + 1] = (byte)((uValue >>  8) & 0xFFu);
            BYTEW_arrBuffer[BYTEW_iCount + 2] = (byte)((uValue >> 16) & 0xFFu);
            BYTEW_arrBuffer[BYTEW_iCount + 3] = (byte)((uValue >> 24) & 0xFFu);
            BYTEW_iCount = BYTEW_iCount + 4;
        }

        /*--------------------------------------------------------------------*/
        /* BYTEW_WriteBytes:                                                  */
        /*                                                                    */
        /* Appends a run of bytes from a source array.  The bulk path: pixel  */
        /* data, compressed chunk payloads, palettes.                         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     arrSrc   : Source buffer.  Must not be null.  Not modified.    */
        /*     iOffset  : Index of the first byte to copy.                    */
        /*     iLength  : Count of bytes to copy.  Zero is legal and does     */
        /*                nothing.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void     : iLength bytes appended.                             */
        /*--------------------------------------------------------------------*/

        public void BYTEW_WriteBytes(byte[] arrSrc, int iOffset, int iLength)
        {
            if (arrSrc == null)
            {   /* Caller bug: this is the encoder's own data, not file input. */
                throw new ArgumentNullException("arrSrc");
            }

            if (iOffset < 0 || iLength < 0)
            {   /* Negative index or count is a programming error, not bad data. */
                throw new ArgumentOutOfRangeException("iOffset");
            }

            /* Long arithmetic, same overflow reasoning as everywhere else a   */
            /* caller-supplied offset and length are added together.           */
            if (((long)iOffset + (long)iLength) > (long)arrSrc.Length)
            {   /* The requested run runs off the end of the source. */
                throw new ArgumentOutOfRangeException("iLength");
            }

            if (iLength == 0)
            {   /* Nothing to do; avoid a pointless Reserve and Array.Copy. */
                return;
            }

            BYTEW_Reserve(iLength);

            Array.Copy(arrSrc, iOffset, BYTEW_arrBuffer, BYTEW_iCount, iLength);
            BYTEW_iCount = BYTEW_iCount + iLength;
        }
    }
}
