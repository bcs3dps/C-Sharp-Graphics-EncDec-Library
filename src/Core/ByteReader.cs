/*----------------------------------------------------------------------------*/
/* SPDX-License-Identifier: MIT OR GPL-2.0-or-later                           */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* Dual-licensed at the recipient's choice -- see LICENSE in this folder for  */
/* the full terms of both licences.  GPL-2.0 full text in GPL-2.0.txt.        */
/*----------------------------------------------------------------------------*/
/* ByteReader.cs -- bounds-checked sequential reads over a window   (BYTER_)  */
/*----------------------------------------------------------------------------*/
/* Every decoder in this library parses a byte stream, and every one of them  */
/* is fed hostile or truncated input as a matter of routine.  This class is   */
/* the single place where "is there another byte" is answered, so that the    */
/* codecs above it can be written as a sequence of reads that either all      */
/* succeed or stop cleanly at the first one that cannot.                      */
/*                                                                            */
/* WHY BOTH ENDIANNESSES.  It is not hedging -- the five formats genuinely    */
/* disagree, and a decoder that assumes one gets garbage from the others:     */
/*                                                                            */
/*     PNG    big-endian     chunk lengths, IHDR width and height, CRCs       */
/*     JPEG   big-endian     segment lengths, image dimensions in SOF0        */
/*     QOI    big-endian     width, height in the 14-byte header              */
/*     GIF    LITTLE-endian  logical screen size, image descriptor fields     */
/*     BMP    LITTLE-endian  essentially everything, being a DOS-era format   */
/*                                                                            */
/* Each width and each order is its own method name rather than one method    */
/* with a flag, so that a grep for BYTER_TryReadU16LE returns exactly the GIF */
/* and BMP call sites -- which is precisely the question worth asking when an */
/* endianness bug is suspected.                                               */
/*                                                                            */
/* NOTHING HERE THROWS ON RUNNING OUT OF DATA.  A truncated file is data, not */
/* an error condition -- that is the library's failure contract -- so every   */
/* read returns a bool and leaves the position untouched when it fails.       */
/* THE UNTOUCHED POSITION IS THE PART THAT MATTERS: it means a caller that    */
/* probes and backtracks does not have to save and restore the position by    */
/* hand, and a caller that simply stops has a position pointing at the byte   */
/* it could not read, which is the useful place for it to point.              */
/*                                                                            */
/* THE READER NEVER COPIES THE INPUT.  It holds the caller's array and a      */
/* window into it, so decoding a thumbnail embedded in a multi-megabyte gcode */
/* buffer costs nothing but the reader object.  The caller therefore must not */
/* mutate the array while a reader is live over it -- stated here because     */
/* nothing in the type can enforce it.                                        */
/*                                                                            */
/* PORTABILITY: pure array indexing and integer arithmetic.  No BitConverter, */
/* whose byte order follows the HOST and would silently do the wrong thing on */
/* a big-endian machine -- the shifts below are explicit for that reason.     */
/*----------------------------------------------------------------------------*/

using System;

namespace GraphicsEncDec
{
    /*------------------------------------------------------------------------*/
    /* ByteReader:                                                            */
    /* A cursor over a bounded window of a caller-owned byte array.  Sealed;  */
    /* the bounds discipline is the entire contract and a subclass that       */
    /* loosened it would defeat every codec's error handling at once.         */
    /*------------------------------------------------------------------------*/

    public sealed class ByteReader
    {
        /*--------------------------------------------------------------------*/
        /* The window and the cursor.  BYTER_iPos is an ABSOLUTE index into   */
        /* BYTER_arrData, not an offset from the window start -- the two      */
        /* differ whenever the window does not begin at zero, and keeping the */
        /* absolute form means every read is one comparison against           */
        /* BYTER_iEnd rather than an addition and then a comparison.          */
        /*--------------------------------------------------------------------*/

        private readonly byte[] BYTER_arrData;           // caller-owned; never copied, never written
        private readonly int    BYTER_iStart;            // absolute index of the window's first byte
        private readonly int    BYTER_iEnd;              // absolute index one past the window's last byte
        private int             BYTER_iPos;              // absolute index of the next byte to read

        /*--------------------------------------------------------------------*/
        /* ByteReader (constructor):                                          */
        /*                                                                    */
        /* Opens a reader over a window of an existing array.  The window is  */
        /* validated here, once, so that no individual read has to consider   */
        /* the possibility of a nonsensical window -- they need only check    */
        /* against BYTER_iEnd.                                                */
        /*                                                                    */
        /* Throws rather than reporting, unlike almost everything else in     */
        /* this library, because a bad window is a CALLER bug: the public     */
        /* decode entry points validate the window with                       */
        /* IMGDEC_TryValidateWindow and report it as bad data before ever     */
        /* constructing a reader, so anything reaching here has already       */
        /* passed that check and a failure means the codec is wrong.          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     arrData  : Buffer to read from.  Held, not copied.  Must not   */
        /*                be null, and must not be mutated while this reader  */
        /*                is in use.                                          */
        /*     iOffset  : Absolute index of the window's first byte.          */
        /*     iLength  : Number of bytes in the window.  Zero is legal and   */
        /*                yields a reader that immediately reports empty.     */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void     : A reader positioned at the window's first byte.     */
        /*--------------------------------------------------------------------*/

        public ByteReader(byte[] arrData, int iOffset, int iLength)
        {
            if (arrData == null)
            {   /* Caller bug: the entry points check this long before here. */
                throw new ArgumentNullException("arrData");
            }

            if (iOffset < 0)
            {   /* A negative start cannot refer into any array. */
                throw new ArgumentOutOfRangeException("iOffset");
            }

            if (iLength < 0)
            {   /* A negative length is meaningless; zero is legal, negative is not. */
                throw new ArgumentOutOfRangeException("iLength");
            }

            /* Long arithmetic: written in int, a large offset plus a large    */
            /* length can wrap to a small number and pass a bounds test that   */
            /* should have failed.  Same reasoning as                          */
            /* IMGDEC_TryValidateWindow, and the same trap.                    */
            if (((long)iOffset + (long)iLength) > (long)arrData.Length)
            {   /* The window runs past the end of the array it claims to sit in. */
                throw new ArgumentOutOfRangeException("iLength");
            }

            BYTER_arrData = arrData;
            BYTER_iStart  = iOffset;
            BYTER_iEnd    = iOffset + iLength;
            BYTER_iPos    = iOffset;
        }

        /*--------------------------------------------------------------------*/
        /* BYTER_Remaining:                                                   */
        /*                                                                    */
        /* How many bytes are left between the cursor and the end of the      */
        /* window.  Used by codecs that need to know whether a whole          */
        /* structure will fit before starting to read it, which produces a    */
        /* better diagnosis than discovering it halfway through.              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int  : Bytes remaining; zero when the window is exhausted.     */
        /*--------------------------------------------------------------------*/

        public int BYTER_Remaining()
        {
            /* The cursor never passes the end, so this is never negative. */
            return(BYTER_iEnd - BYTER_iPos);
        }

        /*--------------------------------------------------------------------*/
        /* BYTER_Position:                                                    */
        /*                                                                    */
        /* The cursor's offset from the START OF THE WINDOW, not from the     */
        /* start of the underlying array.                                     */
        /*                                                                    */
        /* Window-relative on purpose: a codec reasons in terms of "42 bytes  */
        /* into this PNG", and a chunk offset reported relative to a gcode    */
        /* file that happened to contain the PNG would be meaningless in      */
        /* every error message it appeared in.                                */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int  : Zero-based offset within the window.                    */
        /*--------------------------------------------------------------------*/

        public int BYTER_Position()
        {
            /* Subtract the window start to give the caller its own frame. */
            return(BYTER_iPos - BYTER_iStart);
        }

        /*--------------------------------------------------------------------*/
        /* BYTER_TrySeek:                                                     */
        /*                                                                    */
        /* Moves the cursor to a window-relative offset.  Needed because      */
        /* several formats carry explicit offsets: BMP's header points at its */
        /* own pixel data, and a GIF or PNG parser sometimes has to return to */
        /* a remembered position.                                             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iNewPos  : Window-relative offset to move to.  Seeking to      */
        /*                exactly the window length is legal and leaves the   */
        /*                reader empty; seeking past it is not.               */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool     : true when the cursor moved; false when the target   */
        /*                is outside the window, in which case the cursor is  */
        /*                unchanged.                                          */
        /*--------------------------------------------------------------------*/

        public bool BYTER_TrySeek(int iNewPos)
        {
            if (iNewPos < 0)
            {   /* Before the window start -- refuse and leave the cursor alone. */
                return(false);
            }

            if (iNewPos > (BYTER_iEnd - BYTER_iStart))
            {   /* Past the window end.  Equal to the length is allowed: that is "at the end". */
                return(false);
            }

            BYTER_iPos = BYTER_iStart + iNewPos;

            /* The cursor is now at the requested window-relative offset. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* BYTER_TrySkip:                                                     */
        /*                                                                    */
        /* Advances the cursor without reading.  Formats are full of fields   */
        /* this library does not use -- PNG ancillary chunks, JPEG APPn       */
        /* segments, BMP colour masks -- and skipping them is the common      */
        /* case, so it gets its own name rather than a discarded read.        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iCount  : Bytes to advance by.  Must not be negative; use      */
        /*               BYTER_TrySeek to go backwards, where the intent is   */
        /*               visible at the call site.                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool    : true when the cursor advanced; false when there were */
        /*              fewer than iCount bytes left, cursor unchanged.       */
        /*--------------------------------------------------------------------*/

        public bool BYTER_TrySkip(int iCount)
        {
            if (iCount < 0)
            {   /* Backwards movement goes through TrySeek, where it reads as deliberate. */
                return(false);
            }

            if (iCount > BYTER_Remaining())
            {   /* Not enough left; refuse rather than clamping to the end silently. */
                return(false);
            }

            BYTER_iPos = BYTER_iPos + iCount;

            /* Advanced past iCount bytes without inspecting them. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* BYTER_TryPeekU8:                                                   */
        /*                                                                    */
        /* Reads the next byte WITHOUT advancing the cursor.  Marker-driven   */
        /* formats need this: a JPEG parser has to look at the byte after an  */
        /* 0xFF to decide whether it is a marker, a stuffed zero, or padding, */
        /* and only then decide whether to consume it.                        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iValueOut  : Receives the byte as 0..255, or 0 on failure.     */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool       : true when a byte was available.                   */
        /*--------------------------------------------------------------------*/

        public bool BYTER_TryPeekU8(out int iValueOut)
        {
            iValueOut = 0;

            if (BYTER_Remaining() < 1)
            {   /* Window exhausted; nothing to look at. */
                return(false);
            }

            iValueOut = (int)BYTER_arrData[BYTER_iPos];

            /* Read but deliberately did NOT advance the cursor. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* BYTER_TryReadU8:                                                   */
        /*                                                                    */
        /* Reads one byte and advances.                                       */
        /*                                                                    */
        /* Returns it as an int rather than a byte because essentially every  */
        /* caller immediately does arithmetic on it -- shifts it, compares it */
        /* to a marker, uses it as a table index -- and C# would promote it   */
        /* to int at each of those points anyway.  Returning int removes a    */
        /* cast from every call site.                                         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iValueOut  : Receives the byte as 0..255, or 0 on failure.     */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool       : true when a byte was read and the cursor moved.   */
        /*--------------------------------------------------------------------*/

        public bool BYTER_TryReadU8(out int iValueOut)
        {
            iValueOut = 0;

            if (BYTER_Remaining() < 1)
            {   /* Window exhausted; cursor stays where it is. */
                return(false);
            }

            iValueOut  = (int)BYTER_arrData[BYTER_iPos];
            BYTER_iPos = BYTER_iPos + 1;

            /* One byte consumed. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* BYTER_TryReadU16BE:                                                */
        /*                                                                    */
        /* Reads a sixteen-bit big-endian value -- most significant byte      */
        /* first.  PNG, JPEG and QOI all use this order.                      */
        /*                                                                    */
        /* The shift is written out rather than using BitConverter, which     */
        /* follows the HOST's byte order and would produce byte-swapped       */
        /* results on a big-endian machine while working perfectly on x86.    */
        /* That is a bug that cannot be found by testing on the machine it    */
        /* was written on, so it is avoided by construction.                  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iValueOut  : Receives 0..65535, or 0 on failure.               */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool       : true when two bytes were available and read.      */
        /*--------------------------------------------------------------------*/

        public bool BYTER_TryReadU16BE(out int iValueOut)
        {
            iValueOut = 0;

            if (BYTER_Remaining() < 2)
            {   /* Fewer than two bytes left; cursor unchanged so a caller can report position. */
                return(false);
            }

            iValueOut = ((int)BYTER_arrData[BYTER_iPos] << 8) |
                         (int)BYTER_arrData[BYTER_iPos + 1];

            BYTER_iPos = BYTER_iPos + 2;

            /* Two bytes consumed, high byte first. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* BYTER_TryReadU16LE:                                                */
        /*                                                                    */
        /* Reads a sixteen-bit little-endian value -- least significant byte  */
        /* first.  GIF and BMP use this order.                                */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iValueOut  : Receives 0..65535, or 0 on failure.               */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool       : true when two bytes were available and read.      */
        /*--------------------------------------------------------------------*/

        public bool BYTER_TryReadU16LE(out int iValueOut)
        {
            iValueOut = 0;

            if (BYTER_Remaining() < 2)
            {   /* Fewer than two bytes left. */
                return(false);
            }

            iValueOut = ((int)BYTER_arrData[BYTER_iPos + 1] << 8) |
                         (int)BYTER_arrData[BYTER_iPos];

            BYTER_iPos = BYTER_iPos + 2;

            /* Two bytes consumed, low byte first. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* BYTER_TryReadU32BE:                                                */
        /*                                                                    */
        /* Reads a thirty-two-bit big-endian value.  This is PNG's workhorse: */
        /* every chunk length, the IHDR width and height, and every chunk CRC */
        /* is one of these.                                                   */
        /*                                                                    */
        /* Returns uint rather than int deliberately.  A PNG chunk length is  */
        /* an unsigned 32-bit field, and a corrupt file can legitimately      */
        /* contain 0xFFFFFFFF there.  Read into an int that becomes -1, and   */
        /* the obvious "if (length > remaining)" test PASSES, after which the */
        /* decoder allocates or indexes on a negative number.  Keeping it     */
        /* unsigned forces the caller to range-check it explicitly, which is  */
        /* what a decoder should be doing with a length field from a hostile  */
        /* file anyway.                                                       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     uValueOut  : Receives the value, or 0 on failure.              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool       : true when four bytes were available and read.     */
        /*--------------------------------------------------------------------*/

        public bool BYTER_TryReadU32BE(out uint uValueOut)
        {
            uValueOut = 0u;

            if (BYTER_Remaining() < 4)
            {   /* Fewer than four bytes left. */
                return(false);
            }

            uValueOut = ((uint)BYTER_arrData[BYTER_iPos]     << 24) |
                        ((uint)BYTER_arrData[BYTER_iPos + 1] << 16) |
                        ((uint)BYTER_arrData[BYTER_iPos + 2] <<  8) |
                         (uint)BYTER_arrData[BYTER_iPos + 3];

            BYTER_iPos = BYTER_iPos + 4;

            /* Four bytes consumed, most significant first. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* BYTER_TryReadU32LE:                                                */
        /*                                                                    */
        /* Reads a thirty-two-bit little-endian value.  BMP's header is built */
        /* almost entirely from these.                                        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     uValueOut  : Receives the value, or 0 on failure.              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool       : true when four bytes were available and read.     */
        /*--------------------------------------------------------------------*/

        public bool BYTER_TryReadU32LE(out uint uValueOut)
        {
            uValueOut = 0u;

            if (BYTER_Remaining() < 4)
            {   /* Fewer than four bytes left. */
                return(false);
            }

            uValueOut = ((uint)BYTER_arrData[BYTER_iPos + 3] << 24) |
                        ((uint)BYTER_arrData[BYTER_iPos + 2] << 16) |
                        ((uint)BYTER_arrData[BYTER_iPos + 1] <<  8) |
                         (uint)BYTER_arrData[BYTER_iPos];

            BYTER_iPos = BYTER_iPos + 4;

            /* Four bytes consumed, least significant first. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* BYTER_TryReadBytes:                                                */
        /*                                                                    */
        /* Reads a run of bytes into a NEW array.  Convenient where the run   */
        /* is small and its lifetime is its own -- a PNG chunk type, a QOI    */
        /* header's magic -- but it allocates, so a decoder copying pixel     */
        /* data should use BYTER_TryReadInto instead.                         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iCount        : Bytes to read.  Zero yields an empty array and */
        /*                     succeeds.                                      */
        /*     arrValueOut   : Receives the new array, or null on failure.    */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool          : true when iCount bytes were available.         */
        /*--------------------------------------------------------------------*/

        public bool BYTER_TryReadBytes(int iCount, out byte[] arrValueOut)
        {
            byte[] arrNew;

            arrValueOut = null;

            if (iCount < 0)
            {   /* A negative count has no meaning. */
                return(false);
            }

            if (iCount > BYTER_Remaining())
            {   /* Not enough left; refuse rather than returning a short array. */
                return(false);
            }

            arrNew = new byte[iCount];
            Array.Copy(BYTER_arrData, BYTER_iPos, arrNew, 0, iCount);
            BYTER_iPos = BYTER_iPos + iCount;

            arrValueOut = arrNew;

            /* The caller owns the returned array outright. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* BYTER_TryReadInto:                                                 */
        /*                                                                    */
        /* Reads a run of bytes into a caller-supplied array, allocating      */
        /* nothing.  This is the form a decoder uses inside a loop: pulling   */
        /* scanline after scanline into one reused buffer rather than         */
        /* allocating a fresh array per row and handing the garbage collector */
        /* an image's worth of work per decode.                               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     arrDestIo    : Destination buffer, written in place.  Must not */
        /*                    be null and must be large enough.               */
        /*     iDestOffset  : Index in arrDestIo to start writing at.         */
        /*     iCount       : Bytes to read.                                  */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool         : true when iCount bytes were available in the    */
        /*                    window AND fit in the destination; false        */
        /*                    otherwise, with nothing written and the cursor  */
        /*                    unchanged.                                      */
        /*--------------------------------------------------------------------*/

        public bool BYTER_TryReadInto(byte[] arrDestIo, int iDestOffset, int iCount)
        {
            if (arrDestIo == null)
            {   /* Caller bug: this is the caller's own buffer, not file data. */
                throw new ArgumentNullException("arrDestIo");
            }

            if (iCount < 0 || iDestOffset < 0)
            {   /* Negative count or destination index is meaningless. */
                return(false);
            }

            if (iCount > BYTER_Remaining())
            {   /* Not enough bytes left in the window. */
                return(false);
            }

            /* Long arithmetic again, for the same overflow reason as the     */
            /* constructor: this bound is checked against a caller-supplied   */
            /* offset and count, either of which can be large.                */
            if (((long)iDestOffset + (long)iCount) > (long)arrDestIo.Length)
            {   /* Would run off the end of the destination. */
                return(false);
            }

            Array.Copy(BYTER_arrData, BYTER_iPos, arrDestIo, iDestOffset, iCount);
            BYTER_iPos = BYTER_iPos + iCount;

            /* iCount bytes copied into the caller's buffer, nothing allocated. */
            return(true);
        }
    }
}
