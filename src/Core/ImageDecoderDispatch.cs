/*----------------------------------------------------------------------------*/
/* SPDX-License-Identifier: MIT OR GPL-2.0-or-later                           */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* Dual-licensed at the recipient's choice -- see LICENSE in this folder for  */
/* the full terms of both licences.  GPL-2.0 full text in GPL-2.0.txt.        */
/*----------------------------------------------------------------------------*/
/* ImageDecoderDispatch.cs -- format sniffing and dispatch         (IMGDEC_)  */
/*----------------------------------------------------------------------------*/
/* The format-agnostic front door.  A consumer that holds "some thumbnail"    */
/* and does not know or care what it is calls IMGDEC_TryDecodeAny and gets an */
/* ImageBuffer back.  That is the call the first consumer will actually use,  */
/* because a slicer's embedded thumbnail can be PNG, JPEG or QOI depending on */
/* which slicer wrote the file.                                               */
/*                                                                            */
/* SNIFFING IS BY MAGIC BYTES ONLY, NEVER BY FILE EXTENSION.  Extensions do   */
/* not exist inside a base64 blob, and where they do exist they are a claim   */
/* by whoever named the file rather than a fact about its contents.  Every    */
/* format this library handles carries an unambiguous signature in its first  */
/* few bytes, so there is no reason to trust anything weaker.                 */
/*                                                                            */
/* SIGNATURES ARE TESTED LONGEST FIRST.  BMP's is only the two ASCII bytes    */
/* "BM", which is weak enough that some other format's payload could begin    */
/* with it by chance; PNG's is eight bytes chosen by its designers to survive */
/* exactly this kind of confusion.  Testing the strong signatures first means */
/* a weak one can only match when nothing better did.                         */
/*                                                                            */
/* THE SNIFFER AND THE DISPATCHER ARE BOTH COMPLETE for all five formats.     */
/* Every branch of IMGDEC_TryDecodeAny calls a real codec; none of them       */
/* reports an unwritten format, and any refusal a caller sees comes from the  */
/* codec itself and describes the FILE rather than this library.              */
/*                                                                            */
/* THIS FILE ALSO OWNS INPUT-WINDOW VALIDATION.  Every public decode entry    */
/* point takes an (array, offset, length) window rather than a whole array,   */
/* so a caller with a thumbnail embedded in a larger gcode buffer need not    */
/* copy it out first.  The bounds checking for that window is identical for   */
/* every codec, so it lives here once, at the format-agnostic layer where     */
/* input-shape concerns belong, rather than five times over.                  */
/*                                                                            */
/* PORTABILITY: nothing here is Windows-specific, and nothing in this library */
/* is -- that constraint is the reason it exists.                             */
/*----------------------------------------------------------------------------*/

using System;

namespace GraphicsEncDec
{
    /*------------------------------------------------------------------------*/
    /* ImageDecoderDispatch:                                                  */
    /* Static-only.  Identifies an image format from its leading bytes and    */
    /* routes a decode to the codec that owns it.  Holds no state, so it is   */
    /* safe to call from several threads at once.                             */
    /*------------------------------------------------------------------------*/

    public static class ImageDecoderDispatch
    {
        /*--------------------------------------------------------------------*/
        /* Format identifiers returned by IMGDEC_TrySniffFormat.  Plain int   */
        /* constants rather than an enum, to stay consistent with the rest of */
        /* the library's C-shaped surface and to keep the values stable if a  */
        /* format is ever added in the middle of the list.                    */
        /*--------------------------------------------------------------------*/

        public const int IMGDEC_FORMAT_UNKNOWN = 0;      // no signature matched
        public const int IMGDEC_FORMAT_PNG     = 1;      // Portable Network Graphics
        public const int IMGDEC_FORMAT_JPEG    = 2;      // JFIF / EXIF JPEG
        public const int IMGDEC_FORMAT_BMP     = 3;      // Windows / OS-2 device-independent bitmap
        public const int IMGDEC_FORMAT_QOI     = 4;      // Quite OK Image
        public const int IMGDEC_FORMAT_GIF     = 5;      // Graphics Interchange Format, 87a or 89a

        /*--------------------------------------------------------------------*/
        /* The signatures themselves.                                         */
        /*                                                                    */
        /* PNG's eight bytes are the most carefully designed of the set: the  */
        /* high bit in 0x89 detects a transport that stripped it, "PNG" is    */
        /* human-readable in a hex dump, the CR-LF pair catches a transfer    */
        /* that converted line endings, the 0x1A stops MS-DOS TYPE from       */
        /* dumping the rest of the file, and the trailing LF catches the      */
        /* reverse line-ending conversion.  It is worth knowing that this is  */
        /* deliberate rather than arbitrary, because it is why PNG is tested  */
        /* first and trusted most.                                            */
        /*--------------------------------------------------------------------*/

        private static readonly byte[] IMGDEC_arrMagicPng =
            new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };   // \x89 P N G CR LF SUB LF

        private static readonly byte[] IMGDEC_arrMagicGif87 =
            new byte[] { 0x47, 0x49, 0x46, 0x38, 0x37, 0x61 };               // "GIF87a", the 1987 revision

        private static readonly byte[] IMGDEC_arrMagicGif89 =
            new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 };               // "GIF89a", adds transparency and animation

        private static readonly byte[] IMGDEC_arrMagicQoi =
            new byte[] { 0x71, 0x6F, 0x69, 0x66 };                           // "qoif"

        private static readonly byte[] IMGDEC_arrMagicJpeg =
            new byte[] { 0xFF, 0xD8, 0xFF };                                 // SOI marker, then the first segment's marker prefix

        private static readonly byte[] IMGDEC_arrMagicBmp =
            new byte[] { 0x42, 0x4D };                                       // "BM" -- only two bytes, so tested last

        /*---------------------------------------------------------------------*/
        /* IMGDEC_TryValidateWindow:                                           */
        /*                                                                     */
        /* Checks that an (array, offset, length) triple describes a range     */
        /* that actually lies inside the array, before any codec indexes into  */
        /* it.  Shared by every decode entry point in the library.             */
        /*                                                                     */
        /* The addition offset + length is done in LONG arithmetic on purpose. */
        /* Written in int, a large offset plus a large length can overflow to  */
        /* a small or negative number, and the obvious bounds test then        */
        /* PASSES on a window that runs far off the end of the array.  That    */
        /* is a classic and entirely silent way to turn a bounds check into a  */
        /* buffer overrun, and a decoder is fed hostile input by definition.   */
        /*                                                                     */
        /* A null array is treated as a caller bug and throws, matching the    */
        /* rest of the library: bad IMAGE DATA earns a reason string, but a    */
        /* missing argument is a programming error and is not the caller's to  */
        /* recover from.                                                       */
        /*                                                                     */
        /* Arguments:                                                          */
        /*     arrData  : Buffer the window refers into.  Must not be null.    */
        /*     iOffset  : Index of the first byte of the window.               */
        /*     iLength  : Number of bytes in the window.                       */
        /*     sReason  : Receives "" when the window is sound, or a short     */
        /*                description of what is wrong with it.                */
        /*                                                                     */
        /* Returns:                                                            */
        /*     bool     : true when the window lies wholly inside arrData and  */
        /*                holds at least one byte; false otherwise.            */
        /*---------------------------------------------------------------------*/

        public static bool IMGDEC_TryValidateWindow(byte[] arrData, int iOffset, int iLength,
                                                    out string sReason)
        {
            sReason = "";

            if (arrData == null)
            {   /* Missing argument, not malformed data -- this is a caller bug and raises. */
                throw new ArgumentNullException("arrData");
            }

            if (iOffset < 0)
            {   /* A negative start index cannot refer into any array. */
                sReason = "window offset " + iOffset.ToString() + " is negative";
                return(false);
            }

            if (iLength < 1)
            {   /* An empty window holds no image, so there is nothing any codec could do with it. */
                sReason = "window length " + iLength.ToString() + " is not at least 1";
                return(false);
            }

            /* Long arithmetic deliberately: see the overflow note above. */
            if (((long)iOffset + (long)iLength) > (long)arrData.Length)
            {   /* The window runs past the end of the array it claims to sit inside. */
                sReason = "window offset " + iOffset.ToString() + " plus length " + iLength.ToString() +
                          " runs past the end of a " + arrData.Length.ToString() + " byte buffer";
                return(false);
            }

            /* The window starts inside the array, holds at least one byte,   */
            /* and ends at or before the final byte: safe for a codec to      */
            /* index across.                                                  */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* IMGDEC_TrySniffFormat:                                             */
        /*                                                                    */
        /* Identifies which of the library's five formats a window of bytes   */
        /* holds, by matching the leading bytes against each format's         */
        /* signature.  Does not decode anything and does not validate the     */
        /* body -- a file can sniff as PNG and still be corrupt two bytes     */
        /* later, and reporting that is the decoder's job, not this one's.    */
        /*                                                                    */
        /* Signatures are tested longest and strongest first, so BMP's        */
        /* two-byte "BM" can only win when no better-evidenced format did.    */
        /*                                                                    */
        /* Returning false with IMGDEC_FORMAT_UNKNOWN is a NORMAL outcome,    */
        /* not an error: it means "this is not one of the five", which is a   */
        /* perfectly ordinary thing for a caller to be told about arbitrary   */
        /* input.                                                             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     arrData     : Buffer holding the image.  Must not be null.     */
        /*     iOffset     : Index of the image's first byte.                 */
        /*     iLength     : Number of bytes available from iOffset.          */
        /*     iFormatOut  : Receives an IMGDEC_FORMAT_* value.  Set to       */
        /*                   IMGDEC_FORMAT_UNKNOWN on every failure path, so  */
        /*                   a caller that ignores the bool still reads       */
        /*                   something meaningful.                            */
        /*     sReason     : Receives "" on a match, or why no match was      */
        /*                   found.                                           */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool        : true when a signature matched; false when the    */
        /*                   window is unusable or matches nothing known.     */
        /*--------------------------------------------------------------------*/

        public static bool IMGDEC_TrySniffFormat(byte[] arrData, int iOffset, int iLength,
                                                 out int iFormatOut, out string sReason)
        {
            /* Fail closed: both out parameters carry a defined value before   */
            /* any test runs.                                                  */
            iFormatOut = IMGDEC_FORMAT_UNKNOWN;
            sReason    = "";

            if (!IMGDEC_TryValidateWindow(arrData, iOffset, iLength, out sReason))
            {   /* The window itself is unusable; pass the window's own reason straight up. */
                return(false);
            }

            if (IMGDEC_MatchesMagic(arrData, iOffset, iLength, IMGDEC_arrMagicPng))
            {   /* Eight-byte PNG signature: the strongest evidence any of these formats offers. */
                iFormatOut = IMGDEC_FORMAT_PNG;
                return(true);
            }

            if (IMGDEC_MatchesMagic(arrData, iOffset, iLength, IMGDEC_arrMagicGif87) ||
                IMGDEC_MatchesMagic(arrData, iOffset, iLength, IMGDEC_arrMagicGif89))
            {   /* Either GIF revision; the decoder cares which, the sniffer does not. */
                iFormatOut = IMGDEC_FORMAT_GIF;
                return(true);
            }

            if (IMGDEC_MatchesMagic(arrData, iOffset, iLength, IMGDEC_arrMagicQoi))
            {   /* Four-byte "qoif" header tag. */
                iFormatOut = IMGDEC_FORMAT_QOI;
                return(true);
            }

            if (IMGDEC_MatchesMagic(arrData, iOffset, iLength, IMGDEC_arrMagicJpeg))
            {   /* SOI followed by the next marker's 0xFF prefix -- three bytes, and reliable in practice. */
                iFormatOut = IMGDEC_FORMAT_JPEG;
                return(true);
            }

            if (IMGDEC_MatchesMagic(arrData, iOffset, iLength, IMGDEC_arrMagicBmp))
            {   /* Weakest signature in the set, so it is only reached once every other test failed. */
                iFormatOut = IMGDEC_FORMAT_BMP;
                return(true);
            }

            /* Nothing matched.  This is an ordinary answer about arbitrary   */
            /* input, not a malfunction, and the caller is told so plainly.   */
            sReason = "no known image signature at the start of the window";
            return(false);
        }

        /*--------------------------------------------------------------------*/
        /* IMGDEC_TryDecodeAny:                                               */
        /*                                                                    */
        /* Sniffs the format and hands the window to whichever codec owns it, */
        /* so a consumer holding an unidentified image never has to switch on */
        /* the format itself.                                                 */
        /*                                                                    */
        /* EVERY BRANCH CALLS A REAL CODEC.  The dispatch shape was built and */
        /* compiled before the codecs behind it existed, because it is part   */
        /* of the frozen public surface; each branch then became a one-line   */
        /* call as its codec landed.  Until then a caller got an honest,      */
        /* specific refusal rather than a misleading "unknown format", which  */
        /* is why the shape was worth compiling early.                        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     arrData  : Buffer holding the image.  Must not be null.        */
        /*     iOffset  : Index of the image's first byte.                    */
        /*     iLength  : Number of bytes available from iOffset.             */
        /*     imgOut   : Receives the decoded image on success, null on any  */
        /*                failure.                                            */
        /*     sReason  : Receives "" on success, or the cause of failure --  */
        /*                either the sniffer's, the codec's, or the           */
        /*                not-implemented-yet notice below.                   */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool     : true when imgOut holds a decoded image.             */
        /*--------------------------------------------------------------------*/

        public static bool IMGDEC_TryDecodeAny(byte[] arrData, int iOffset, int iLength,
                                               out ImageBuffer imgOut, out string sReason)
        {
            int iFormat;

            imgOut  = null;
            sReason = "";

            if (!IMGDEC_TrySniffFormat(arrData, iOffset, iLength, out iFormat, out sReason))
            {   /* Unusable window, or nothing we recognise; the sniffer's reason is already the right one. */
                return(false);
            }

            if (iFormat == IMGDEC_FORMAT_PNG)
            {   /* Recognised as PNG, and PngCodec decodes every legal PNG    */
                /* there is -- depths 1 to 16, every colour type,             */
                /* tRNS in both forms, interlaced or not.  This branch is a   */
                /* complete dispatch now rather than a partial one, and the   */
                /* only refusals that can come back from it are statements    */
                /* about the FILE.                                            */
                return(PngCodec.PNG_TryDecode(arrData, iOffset, iLength, out imgOut, out sReason));
            }

            if (iFormat == IMGDEC_FORMAT_QOI)
            {   /* Recognised as QOI, so this is a real dispatch into           */
                /* QoiCodec.  The codec re-checks the magic                     */
                /* itself rather than trusting the sniff, so that it can also   */
                /* be called directly without going through here.               */
                return(QoiCodec.QOI_TryDecode(arrData, iOffset, iLength, out imgOut, out sReason));
            }

            if (iFormat == IMGDEC_FORMAT_BMP)
            {   /* Recognised as BMP, so this is a real dispatch into        */
                /* BmpCodec.  As with QOI, the codec re-checks               */
                /* the signature itself so it can also be called directly.   */
                return(BmpCodec.BMP_TryDecode(arrData, iOffset, iLength, out imgOut, out sReason));
            }

            if (iFormat == IMGDEC_FORMAT_JPEG)
            {   /* Recognised as JPEG, so this is a real dispatch into       */
                /* JpegCodec.  As with the others, the codec                 */
                /* re-checks the signature itself so it can also be called   */
                /* directly.                                                 */
                return(JpegCodec.JPEG_TryDecode(arrData, iOffset, iLength, out imgOut, out sReason));
            }

            if (iFormat == IMGDEC_FORMAT_GIF)
            {   /* Recognised as GIF, so this is a real dispatch into          */
                /* GifCodec.  As with the others, the codec                    */
                /* re-checks the signature itself so it can also be called     */
                /* directly.  AN ANIMATED GIF COMES BACK AS ITS FIRST FRAME    */
                /* here, because this call returns one ImageBuffer; a caller   */
                /* wanting the animation calls GIF_TryDecodeFrames instead.    */
                return(GifCodec.GIF_TryDecode(arrData, iOffset, iLength, out imgOut, out sReason));
            }

            /* Unreachable while the sniffer only ever returns true for the     */
            /* five formats above, but written out rather than assumed: if a    */
            /* sixth format is added to the sniffer and its branch is missed    */
            /* here, this says so instead of silently returning a null image.   */
            sReason = "format identifier " + iFormat.ToString() + " has no decoder branch in IMGDEC_TryDecodeAny";
            return(false);
        }

        /*--------------------------------------------------------------------*/
        /* IMGDEC_FormatName:                                                 */
        /*                                                                    */
        /* Turns an IMGDEC_FORMAT_* value into a short display name, for log  */
        /* lines, diagnostic reports and reason strings.  Kept beside the     */
        /* constants so a new format cannot be added without the name for it  */
        /* being an obvious two lines away.                                   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iFormat  : An IMGDEC_FORMAT_* value, or any other int.         */
        /*                                                                    */
        /* Returns:                                                           */
        /*     string   : The format's short name, or a bracketed rendering   */
        /*                of the raw number when the value is not one this    */
        /*                function knows.  Never null, so a caller can        */
        /*                concatenate the result without a guard.             */
        /*--------------------------------------------------------------------*/

        public static string IMGDEC_FormatName(int iFormat)
        {
            if (iFormat == IMGDEC_FORMAT_PNG)
            {   /* Portable Network Graphics. */
                return("PNG");
            }

            if (iFormat == IMGDEC_FORMAT_JPEG)
            {   /* JFIF or EXIF JPEG. */
                return("JPEG");
            }

            if (iFormat == IMGDEC_FORMAT_BMP)
            {   /* Windows or OS-2 device-independent bitmap. */
                return("BMP");
            }

            if (iFormat == IMGDEC_FORMAT_QOI)
            {   /* Quite OK Image. */
                return("QOI");
            }

            if (iFormat == IMGDEC_FORMAT_GIF)
            {   /* Either GIF revision. */
                return("GIF");
            }

            if (iFormat == IMGDEC_FORMAT_UNKNOWN)
            {   /* The sniffer's own "nothing matched" value. */
                return("unknown");
            }

            /* Some value that is not one of the constants at all -- render   */
            /* it visibly rather than pretending it is "unknown", so a bad    */
            /* identifier shows up in a report instead of blending in.        */
            return("<format " + iFormat.ToString() + ">");
        }

        /*--------------------------------------------------------------------*/
        /* IMGDEC_MatchesMagic:                                               */
        /*                                                                    */
        /* Compares the first bytes of a window against a signature.  Private */
        /* because it is a detail of sniffing, and because it deliberately    */
        /* does no bounds validation of its own -- IMGDEC_TrySniffFormat has  */
        /* already validated the window before the first call, and repeating  */
        /* that per signature would be six redundant checks per sniff.        */
        /*                                                                    */
        /* A window SHORTER than the signature is a non-match rather than an  */
        /* error: a four-byte file is simply not a PNG, and saying so is the  */
        /* correct answer rather than a complaint.                            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     arrData   : Buffer holding the image; already validated.       */
        /*     iOffset   : Index of the image's first byte.                   */
        /*     iLength   : Number of bytes available from iOffset.            */
        /*     arrMagic  : The signature to compare against.                  */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool      : true when every byte of arrMagic is present at the */
        /*                 start of the window; false otherwise, including    */
        /*                 when the window is too short to hold it.           */
        /*--------------------------------------------------------------------*/

        private static bool IMGDEC_MatchesMagic(byte[] arrData, int iOffset, int iLength,
                                                byte[] arrMagic)
        {
            int i;

            if (iLength < arrMagic.Length)
            {   /* Too few bytes available for this signature to be present at all. */
                return(false);
            }

            for (i = 0; i < arrMagic.Length; i++)
            {   /* Byte-for-byte compare; bail on the first difference rather   */
                /* than scanning the whole signature every time.                */
                if (arrData[iOffset + i] != arrMagic[i])
                {   /* This byte differs, so the signature cannot match. */
                    return(false);
                }
            }

            /* Every byte of the signature was present, in order, at the   */
            /* start of the window.                                        */
            return(true);
        }
    }
}
