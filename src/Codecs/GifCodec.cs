/*----------------------------------------------------------------------------*/
/* SPDX-License-Identifier: MIT OR GPL-2.0-or-later                           */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* Dual-licensed at the recipient's choice -- see LICENSE in this folder for  */
/* the full terms of both licences.  GPL-2.0 full text in GPL-2.0.txt.        */
/*----------------------------------------------------------------------------*/
/* GifCodec.cs -- GIF decode                                        (GIF_)    */
/*----------------------------------------------------------------------------*/
/* Transcoded from stb_image.h v2.30 (nothings/stb, commit 2c980bb5), the     */
/* GIF decode path at lines 6552 to 7080.  Structure and variable roles are   */
/* kept so this file can be diffed against its source when it misbehaves;     */
/* naming and commentary follow this portfolio's rules on top.                */
/*                                                                            */
/* stb is dual-licensed MIT / public domain, which satisfies both arms of     */
/* this file's licence.                                                       */
/*                                                                            */
/* THE TEN-YEAR-OLD deprecated/stb_image.c v1.35 IN THE SAME CLONE IS NOT AN  */
/* ALTERNATIVE HERE.  Its GIF path decodes a single frame and has no          */
/* multi-frame support at all -- no disposal, no delays, no canvas history -- */
/* so it cannot supply animation at all, and is useful only as a second       */
/* reading of the LZW loop, which is unchanged between the two.               */
/*----------------------------------------------------------------------------*/
/* WHAT THIS FILE DOES: GIF IN BOTH DIRECTIONS, COMPLETE -- stills AND        */
/* animation, decode AND encode.                                              */
/*                                                                            */
/* THE RASTER: the header and logical screen descriptor, global               */
/* and local colour tables, the image descriptor, the LZW stream, the         */
/* four-pass interlace walk and the transparent-index rule, ending at         */
/* GIF_TryDecode, which returns the first frame composited onto the logical   */
/* screen.                                                                    */
/*                                                                            */
/* ANIMATION ON TOP OF IT: per-frame delays, the loop count from the          */
/* NETSCAPE application extension, the four disposal methods, compositing     */
/* across frames, and GIF_TryDecodeFrames, which returns every frame as a     */
/* complete image.                                                            */
/*                                                                            */
/* ENCODE: GIF_TryEncode writes the container, a global colour                */
/* table taken EXACTLY from the image, and an LZW raster.  Because the        */
/* palette is exact that encoding is lossless, which is why its               */
/* milestone is checked at tolerance ZERO through both our own decoder and    */
/* GDI+ -- the only encode milestone in this library that has ever been able  */
/* to say that.                                                               */
/*                                                                            */
/* ADAPTIVE QUANTISATION: a median-split palette over the image's             */
/* own colours, with optional Floyd-Steinberg dithering, so an image with     */
/* more colours than the table can hold is APPROXIMATED rather than refused.  */
/* GIFOPT_iPaletteSize and GIFOPT_bDither are both honoured.                  */
/*                                                                            */
/* THE EXACT PALETTE IS STILL TRIED FIRST, ALWAYS, and that ordering is why   */
/* the zero-tolerance guarantee survives quantisation intact: an image that   */
/* FITS is still encoded losslessly, byte for byte as before, and the         */
/* quantiser is reached only by images that could not be written at all       */
/* otherwise.                                                                 */
/*                                                                            */
/* ANIMATION ENCODE: GIF_TryEncodeFrames, in two forms chosen by whether a    */
/* hole ever OPENS between consecutive frames -- delta frames with disposal   */
/* 1, or self-contained full-canvas frames with disposal 2.                   */
/*----------------------------------------------------------------------------*/
/* THE ENCODER'S LINEAGE IS MIXED, AND DELIBERATELY SO.  Everything above is  */
/* transcoded from stb, which writes no GIF at all, so the encode half has    */
/* no counterpart there to follow.                                            */
/*                                                                            */
/*   - THE LZW WRITER IS OURS, written from the GIF89a specification and      */
/*     from this file's own decoder rather than transcribed from anywhere.    */
/*     The decoder was already here, complete and tested, and an LZW encoder  */
/*     is that run backwards; writing it keeps the two directions symmetric   */
/*     and diffable the way QoiCodec's pair is, and it is the half of the     */
/*     encoder with no design questions in it.  GIF_TryEncodeLzwRaster's      */
/*     header                                                                 */
/*     names the three places the two halves must agree exactly.              */
/*                                                                            */
/*   - THE QUANTISER IS TRANSCODED FROM gif-h (charlietangora/gif-h, commit   */
/*     70b64528), chosen for that purpose because its bitDepth and dither     */
/*     parameters map 1:1 onto this library's palette-size and dither         */
/*     options.  gif-h is released into the PUBLIC DOMAIN under the           */
/*     Unlicense, which satisfies both arms of this file's licence.  Its      */
/*     GifSplitPalette, GifMakePalette, GifGetClosestPaletteColor,            */
/*     GifDitherImage and GifThresholdImage became the GIF_Quant* group.      */
/*                                                                            */
/* A mixed lineage is acceptable because each part names its own source; it   */
/* is chosen deliberately and recorded, never drifted into.  PngCodec.cs made */
/* the same call, decoding from stb and encoding from lodepng.                */
/*                                                                            */
/* TWO THINGS gif-h DOES THAT THIS FILE DELIBERATELY DOES NOT, both           */
/* adaptations rather than defects, and both decided before any code existed: */
/*                                                                            */
/*   1. IT RESERVES PALETTE INDEX 0 AS TRANSPARENT UNCONDITIONALLY, because   */
/*      its whole inter-frame delta scheme marks unchanged pixels             */
/*      transparent.  For an animation that is exactly right; for a still     */
/*      image it silently costs a colour, so a 256-entry table would carry    */
/*      255.  Here it is the caller's choice through GIFOPT_iTransparency,    */
/*      whose default asks the IMAGE rather than the caller.                  */
/*                                                                            */
/*   2. IT WRITES A LOCAL COLOUR TABLE PER FRAME rather than a global one.    */
/*      Legal, and normal for animation -- but a single still image           */
/*      conventionally carries a global table, and that is what both GDI+     */
/*      and Pillow emit, which matters because they are the readers this      */
/*      output is judged against.                                             */
/*----------------------------------------------------------------------------*/
/* DEVIATION 4: gif-h's GifSwapPixels LOSES THE SECOND PIXEL'S ALPHA.         */
/*                                                                            */
/* Its line reads "uint8_t aB = image[pixA*4+3];" where every other line of   */
/* the function uses pixB, so a swap gives BOTH pixels the first one's alpha. */
/* GIF_QuantSwapPixels reads pixB there.                                      */
/*                                                                            */
/* IT IS UNREACHABLE IN BOTH CODEBASES TODAY, which is the whole reason it is */
/* worth writing down.  The buffer being shuffled is a throwaway copy that is */
/* only ever read for R, G and B, so nothing downstream looks at the alpha    */
/* the swap corrupted -- in gif-h or here.  "The bug cannot reach anything"   */
/* is a statement about today's callers, though, and this project has already */
/* paid for that reasoning: stb's swapped upsampler operands were             */
/* reasoned about rather than corrected and sat behind a tolerance of 62 for  */
/* two pieces of work.  A transcribed defect gets fixed and recorded.         */
/*                                                                            */
/* NOT A DEVIATION, and listed here because it looks like one: the tree       */
/* search is APPROXIMATE.  gif-h prunes subtrees the way an exact             */
/* nearest-neighbour search does, but its split planes are medians of the     */
/* pixels while its leaves hold averages of them, so a leaf can lie across an */
/* ancestor's plane and be pruned away.  MEASURED against an                  */
/* exhaustive scan over 20 000 colours: it disagrees 2 times, and the worst   */
/* disagreement costs 6 of a possible 765 -- two counts a channel.  So the    */
/* transcription stands unchanged and is held to those exact figures.         */
/*----------------------------------------------------------------------------*/
/* HOW A GIF IS LAID OUT, because the rest of this file assumes it.           */
/*                                                                            */
/* A six-byte signature, "GIF87a" or "GIF89a".  Then the LOGICAL SCREEN       */
/* DESCRIPTOR -- width and height as little-endian 16-bit values, a packed    */
/* flags byte, a background colour index and a pixel aspect ratio.  Bit 7 of  */
/* the flags says a GLOBAL COLOUR TABLE follows, and its low three bits give  */
/* the size as 2 << n entries of three bytes each, R then G then B.           */
/*                                                                            */
/* After that the file is a sequence of BLOCKS, each introduced by one byte:  */
/*                                                                            */
/*   0x2C   IMAGE DESCRIPTOR -- left, top, width, height (all LE16) and a     */
/*          packed byte whose bit 7 means a LOCAL colour table follows and    */
/*          whose bit 6 means the raster is INTERLACED.  Then the LZW data.   */
/*   0x21   EXTENSION -- a second byte names which.  0xF9 is the Graphic      */
/*          Control Extension, carrying the disposal method, the transparent  */
/*          index and the delay.  Everything else is skipped.                 */
/*   0x3B   TRAILER -- the stream ends.                                       */
/*                                                                            */
/* EVERY MULTI-BYTE FIELD IN A GIF IS LITTLE-ENDIAN, which makes it the only  */
/* format in this library that is: PNG, JPEG and QOI are all big-endian on    */
/* the wire.  Reading one field with the wrong reader yields a plausible      */
/* wrong number rather than a failure, so the calls below are deliberately    */
/* BYTER_TryReadU16LE throughout and never the BE form.                       */
/*                                                                            */
/* SUB-BLOCKS: the LZW data and every extension payload are stored as a chain */
/* of length-prefixed chunks, each up to 255 bytes, terminated by a length of */
/* zero.  A reader that treats the payload as one span walks straight off the */
/* end of the first chunk.                                                    */
/*                                                                            */
/* A FRAME NEED NOT FILL THE SCREEN.  The image descriptor carries its own    */
/* origin and size, and the rest of the logical screen keeps whatever was     */
/* there -- which on the first frame is nothing at all.  That is why the      */
/* decoded buffer is the LOGICAL SCREEN's size and not the frame's.           */
/*----------------------------------------------------------------------------*/
/* THREE DELIBERATE DEVIATIONS FROM stb.  The first is a safety requirement   */
/* C# imposes and C does not; the second is a bug fix; the third is not this  */
/* sub-step's to make and is recorded so it is not missed.                    */
/*                                                                            */
/*   1. THE LZW PREFIX WALK IS ITERATIVE, NOT RECURSIVE.  A dictionary entry  */
/*      names its prefix, so emitting one code means walking a backward       */
/*      chain and writing it out FORWARDS.  stb does that by recursing into   */
/*      stbi__out_gif_code, one C stack frame per character, and a chain may  */
/*      legally reach 4096 entries.  In C that is a deep stack; in C# a       */
/*      StackOverflowException CANNOT BE CAUGHT and takes the process down,   */
/*      which would make a crafted GIF fatal to any program using this        */
/*      library.  This library's contract is explicit that a corrupt          */
/*      image is DATA and must come back as false plus a reason.  So the      */
/*      chain is walked into an explicit stack and replayed in reverse, with  */
/*      a hard depth bound of GIF_LZW_MAX_CODES.  The output is identical     */
/*      character for character; only the mechanism differs.                  */
/*                                                                            */
/*   2. THE FIRST-FRAME BACKGROUND FILL WRITES RGBA, NOT BGRA -- a BUG in     */
/*      stb, and the reason it is invisible is worth knowing.  stb stores a   */
/*      colour table entry as B, G, R, A: its parse_colortable puts the       */
/*      first byte read (RED, per the specification) at index 2.  Its         */
/*      per-pixel writer then un-swaps, emitting c[2], c[1], c[0], c[3] --    */
/*      correct RGBA.  But its first-frame background fill memcpys the        */
/*      palette entry STRAIGHT into the output with no swap, so every pixel   */
/*      the first frame did not draw gets its RED AND BLUE EXCHANGED.         */
/*                                                                            */
/*      This file stores colour table entries as RGBA in the first place, so  */
/*      both paths copy straight through and the two agree by construction.   */
/*      The double swap stb performs on every pixel is removed with it.       */
/*                                                                            */
/*      It survives upstream because the fill is guarded by                   */
/*      "first_frame && bgindex > 0": both GDI+ and Pillow write a background */
/*      index of ZERO, so neither produces a file that reaches it.  Testing   */
/*      therefore uses one written by hand with a non-zero                    */
/*      background index and a frame smaller than the screen, which is the    */
/*      only way to see it.                                                   */
/*                                                                            */
/*   3. THE ANIMATION MODEL IS WRITTEN FROM THE SPECIFICATION, NOT            */
/*      TRANSCRIBED, because stb's is broken.  Its multi-frame driver sets    */
/*      two_back = out - 2 * stride, which addresses memory BEFORE its own    */
/*      allocation -- it means the frame at out + (layers - 2) * stride, so   */
/*      any GIF using disposal method 3 with three or more frames reads out   */
/*      of bounds.  Verified against the clone's full history: HEAD is our    */
/*      pinned commit and no later fix exists, and v1.35 has no multi-frame   */
/*      support to compare against.                                           */
/*                                                                            */
/*      GIF_ApplyPendingDisposal implements the GIF89a definition instead,    */
/*      which is both correct and simpler: "restore to previous" means the    */
/*      canvas as it stood BEFORE THE CURRENT FRAME DREW -- a snapshot taken  */
/*      at that moment -- and has nothing to do with the frame two back.  No  */
/*      history of frames is kept and no pointer arithmetic is involved.      */
/*      That function's header carries the rest, including why "restore to    */
/*      background" clears to transparent rather than to the background       */
/*      colour the specification names.                                       */
/*                                                                            */
/* AND ONE STALE COMMENT IN THE SOURCE, flagged so a reader diffing against   */
/* it is not misled.  stb's raster loop is introduced by "support no starting */
/* clear code", and the code beneath it does the opposite: it pre-seeds avail */
/* and oldcode as though a clear had been seen, and then REFUSES the first    */
/* data code if no clear code actually arrived.  The refusal is the correct   */
/* behaviour and is kept.  This is the second time on this work that an stb   */
/* comment has contradicted its own code, after the EXTEND sign rule in       */
/* JpegCodec.cs.                                                              */
/*----------------------------------------------------------------------------*/

using System;
using System.Collections.Generic;

namespace GraphicsEncDec
{
    /*------------------------------------------------------------------------*/
    /* GifHeader:                                                             */
    /* The logical screen descriptor, which is what a GIF says about itself   */
    /* before any frame appears.                                              */
    /*                                                                        */
    /* THE SCREEN IS NOT THE FRAME.  These dimensions describe the canvas     */
    /* every frame is composited onto; an individual frame carries its own    */
    /* origin and size in its image descriptor and may be smaller, may be     */
    /* offset, and in an animation usually is both.  A decoder that returned  */
    /* the frame's size would disagree with every other reader about how big  */
    /* the picture is.                                                        */
    /*------------------------------------------------------------------------*/

    public sealed class GifHeader
    {
        public int  GIFH_iWidth;                     // logical screen width in pixels, 1..65535
        public int  GIFH_iHeight;                    // logical screen height in pixels, 1..65535
        public int  GIFH_iFlags;                     // packed byte: bit 7 global table, bits 0-2 its size exponent
        public int  GIFH_iBackgroundIndex;           // palette index painted where the first frame draws nothing
        public int  GIFH_iAspectRatio;               // pixel aspect ratio byte; 0 means "not specified", and is what every writer emits
        public bool GIFH_bIs89a;                     // true for "GIF89a", false for "GIF87a"; 89a is the revision that added transparency and animation
        public bool GIFH_bHasGlobalTable;            // bit 7 of GIFH_iFlags, lifted out because every caller wants it
        public int  GIFH_iGlobalTableEntries;        // 2 << (flags & 7) when present, else 0
    }

    /*------------------------------------------------------------------------*/
    /* GifDecodeState:                                                        */
    /* Everything one decode carries between the blocks of a GIF stream.      */
    /*                                                                        */
    /* IT IS PUBLIC FOR TESTABILITY, not because a consumer should build one. */
    /* The test suite drives the LZW raster and the interlace walk directly,  */
    /* against streams it assembles by hand, and neither is reachable through */
    /* GIF_TryDecode.  That makes this type PUBLIC BUT NOT PART OF THE STABLE */
    /* SURFACE -- it may change where GIF_TryDecode may not.                  */
    /*                                                                        */
    /* THREE BUFFERS EXIST AND A STILL IMAGE USES ONLY ONE.                   */
    /* GIFS_arrBackground and GIFS_arrHistory are allocated here because the  */
    /* disposal methods                                                       */
    /* need them and because allocating them in one place keeps the canvas    */
    /* geometry in one place.  GIFS_arrHistory does have a job even now: the  */
    /* first-frame background fill has to know which pixels the frame did NOT */
    /* draw, and that is exactly what it records.                             */
    /*                                                                        */
    /* THE COORDINATE FIELDS ARE IN BYTES, NOT PIXELS, and that is stb's      */
    /* choice kept deliberately.  GIFS_iCurX advances by four per pixel and   */
    /* GIFS_iCurY by a whole row at a time, so the interlace advance is plain */
    /* arithmetic on GIFS_iLineSize rather than a multiply at every step.     */
    /* Converting the walk to pixel units would touch every index expression  */
    /* in the one part of this codec whose control flow is hardest to check   */
    /* by eye, which is a poor trade for tidiness.  The one place it matters  */
    /* to a reader is GIFS_arrHistory, which is indexed in PIXELS -- so an    */
    /* index into it is a byte offset divided by four.                        */
    /*------------------------------------------------------------------------*/

    public sealed class GifDecodeState
    {
        public int    GIFS_iWidth;                   // logical screen width, copied from the header
        public int    GIFS_iHeight;                  // logical screen height, copied from the header

        public byte[] GIFS_arrOut;                   // the canvas, width * height * 4 bytes, RGBA, zero-filled means fully transparent
        public byte[] GIFS_arrBackground;            // animation only: the canvas as it stood before the current frame's disposal
        public byte[] GIFS_arrHistory;               // one byte per PIXEL: 1 where the current frame drew, 0 where it did not

        public byte[] GIFS_arrGlobalPalette;         // 256 entries of RGBA; only the first GIFH_iGlobalTableEntries are meaningful
        public byte[] GIFS_arrLocalPalette;          // 256 entries of RGBA, rebuilt per frame that carries a local table
        public byte[] GIFS_arrColorTable;            // REFERENCE to whichever of the two the current frame uses; never its own array

        public int    GIFS_iFlags;                   // the logical screen descriptor's packed byte
        public int    GIFS_iBackgroundIndex;         // palette index for pixels the first frame leaves undrawn
        public int    GIFS_iTransparentIndex;        // palette index that must not be drawn, or -1 when none is in force
        public int    GIFS_iExtFlags;                // the Graphic Control Extension's packed byte
        public int    GIFS_iDelayMs;                 // this frame's delay, already converted from hundredths to milliseconds
        public int    GIFS_iLocalFlags;              // the current image descriptor's packed byte

        /*--------------------------------------------------------------------*/
        /* ANIMATION STATE, used only by the multi-frame walk.                */
        /*                                                                    */
        /* DISPOSAL IS APPLIED BEFORE THE NEXT FRAME, NOT AFTER THIS ONE, and */
        /* that ordering is the whole of the bookkeeping below.  A Graphic    */
        /* Control Extension names what to do with the frame it introduces    */
        /* ONCE THAT FRAME HAS BEEN SHOWN -- so the frame is emitted first,   */
        /* and its disposal is remembered and carried out when the following  */
        /* frame arrives.  Applying it immediately would erase the frame      */
        /* before anyone saw it.                                              */
        /*--------------------------------------------------------------------*/

        public int    GIFS_iDisposal;                // the CURRENT frame's disposal method, 0 to 7, from its own extension
        public int    GIFS_iPendingDisposal;         // what to do to the canvas before the NEXT frame is drawn
        public int    GIFS_iPendingX;                // the rectangle that disposal applies to: the previous frame's own bounds
        public int    GIFS_iPendingY;                // in PIXELS, unlike the byte-scaled walk cursor above
        public int    GIFS_iPendingW;                // width in pixels; zero means there is nothing pending
        public int    GIFS_iPendingH;                // height in pixels

        public byte[] GIFS_arrRestore;               // disposal 3's snapshot: the canvas as it stood before the current frame drew
        public int    GIFS_iLoopCount;               // from the NETSCAPE application extension; 0 means forever
        public bool   GIFS_bHasLoopCount;            // false when no NETSCAPE extension was present, which is not the same as a count of zero

        public short[] GIFS_arrCodePrefix;           // LZW dictionary: index of the code this one extends, or -1 for a root
        public byte[]  GIFS_arrCodeFirst;            // LZW dictionary: first character of this code's string, used by the KwKwK case
        public byte[]  GIFS_arrCodeSuffix;           // LZW dictionary: the single character this code appends to its prefix

        public byte[] GIFS_arrEmitStack;             // scratch for the iterative prefix walk; see deviation 1 in the file header

        public int    GIFS_iLineSize;                // bytes in one canvas row, always width * 4
        public int    GIFS_iStartX;                  // frame origin, in BYTES across the canvas
        public int    GIFS_iStartY;                  // frame origin, in BYTES down the canvas (a whole number of rows)
        public int    GIFS_iMaxX;                    // one past the frame's right edge, in bytes
        public int    GIFS_iMaxY;                    // one past the frame's bottom edge, in bytes
        public int    GIFS_iCurX;                    // the write cursor, in bytes across
        public int    GIFS_iCurY;                    // the write cursor, in bytes down
        public int    GIFS_iStep;                    // how far GIFS_iCurY advances per row: one row normally, eight on an interlaced first pass
        public int    GIFS_iParse;                   // interlace passes still to come; 0 on a non-interlaced frame
    }

    /*------------------------------------------------------------------------*/
    /* GifEncodeOptions:                                                      */
    /* What a caller may ask of GIF_TryEncode.  This shape is part of the     */
    /* public contract.  A null options argument means these defaults, which  */
    /* GIF_DefaultEncodeOptions supplies.                                     */
    /*                                                                        */
    /* NOTE THAT ZERO IS NOT A DEFAULT HERE, unlike PNGOPT_iBitDepth and      */
    /* PNGOPT_iColorType, where the frozen surface documents zero as "match   */
    /* the source".  It does not do so for GIFOPT_iPaletteSize, so a          */
    /* default-constructed GifEncodeOptions carrying zero is REFUSED the way  */
    /* BMPOPT_iBitsPerPixel refuses zero -- a caller who wants the defaults   */
    /* calls the factory or passes null.  Inventing a sentinel this class     */
    /* does not document would be an unannounced change to the public         */
    /* contract, which is not a codec's decision to take.                     */
    /*------------------------------------------------------------------------*/

    public sealed class GifEncodeOptions
    {
        /*--------------------------------------------------------------------*/
        /* HOW MANY ENTRIES THE COLOUR TABLE HOLDS -- not how many colours    */
        /* are usable, which is one fewer when an index is reserved for       */
        /* transparency.  Entries is the meaning the format's own size field  */
        /* expresses and the one a caller managing file size cares about.     */
        /*                                                                    */
        /* 2 to 256, rounded UP to a power of two, because the size field is  */
        /* a three-bit exponent and 2 << n is the only shape it can name.     */
        /* The table is written at exactly this size; unused entries are      */
        /* zero-filled, which is what GDI+ does when it writes 256 entries    */
        /* for an image holding four colours.                                 */
        /*                                                                    */
        /* IT IS BOTH A CAP AND A TARGET, and which one depends on the image. */
        /* The exact palette is tried first, so an image whose colours FIT is */
        /* written losslessly and this is simply the table size.  An image    */
        /* with more distinct colours than the table can hold goes to the     */
        /* quantiser instead, and the same number becomes the target it aims  */
        /* at.  The field means the same thing either way; only the path      */
        /* through the encoder changes.                                       */
        /*--------------------------------------------------------------------*/

        public int GIFOPT_iPaletteSize;                  // 2..256, rounded up to a power of two; counts TABLE ENTRIES

        /*--------------------------------------------------------------------*/
        /* Floyd-Steinberg dithering, which only has meaning once colours are */
        /* being APPROXIMATED.  On the exact-palette path every pixel gets    */
        /* the entry holding its own colour, so there is no error to          */
        /* diffuse and this field cannot change a single output byte.  It     */
        /* takes effect only when an image goes to the quantiser, which is    */
        /* what gives it something to do.                                     */
        /*--------------------------------------------------------------------*/

        public bool GIFOPT_bDither;                      // true applies Floyd-Steinberg; no effect on the exact-palette path

        /*--------------------------------------------------------------------*/
        /* How many times an animation plays.  0 to 65535 is taken literally  */
        /* and ZERO MEANS FOREVER; GifCodec.GIFOPT_LOOP_FROM_FRAMES defers to */
        /* ImageFrameSet.FRM_iLoopCount instead and is the default.  The      */
        /* frozen surface says the single-frame call ignores this field, and  */
        /* it does; GIF_TryEncodeFrames is where it takes effect.             */
        /*--------------------------------------------------------------------*/

        public int GIFOPT_iLoopCount;                    // 0..65535 plays (0 = forever), or GIFOPT_LOOP_FROM_FRAMES

        /*--------------------------------------------------------------------*/
        /* Whether palette entry 0 is a colour or the transparent index, as   */
        /* one of GifCodec's GIF_TRANSPARENCY_* constants.  The reasoning     */
        /* behind a tri-state rather than a plain on/off is that three        */
        /* separate things hide behind the question; the                      */
        /* short form is that a source carrying real transparency NEEDS an    */
        /* index, so "none" asked of such an image is a refusal and not an    */
        /* override, and AUTO therefore has to exist to be the right default. */
        /*--------------------------------------------------------------------*/

        public int GIFOPT_iTransparency;                 // GifCodec.GIF_TRANSPARENCY_*; 0 is AUTO and is the default
    }

    /*------------------------------------------------------------------------*/
    /* GifEncodeState:                                                        */
    /* Everything the LZW encoder carries between one code and the next.      */
    /*                                                                        */
    /* IT IS PUBLIC FOR TESTABILITY, on the same footing as GifDecodeState    */
    /* and for the same reason: the LZW writer is driven directly             */
    /* against index arrays built by hand, checking the exact bytes that      */
    /* come out, which is not reachable through GIF_TryEncode.  That makes    */
    /* this type PUBLIC BUT NOT PART OF THE STABLE SURFACE -- it may change   */
    /* where GIF_TryEncode may not.                                           */
    /*                                                                        */
    /* THE DICTIONARY IS THE DECODER'S RUN BACKWARDS, AND IS A DIFFERENT      */
    /* SHAPE FOR THAT REASON.  GifDecodeState carries three arrays indexed BY */
    /* CODE -- prefix, first character, suffix -- because decoding asks "what */
    /* string is code N?".  Encoding asks the inverse, "which code names the  */
    /* string I have?", so the natural structure is a map FROM a string TO a  */
    /* code.  A string being always a known code plus one character, the key  */
    /* is exactly that pair packed into one integer.  The two structures are  */
    /* inverses rather than copies, which is why they do not look alike.      */
    /*------------------------------------------------------------------------*/

    public sealed class GifEncodeState
    {
        public ByteWriter GIFE_bwOut;                // where finished sub-blocks are written; not owned
        public byte[]     GIFE_arrPending;           // bytes waiting for a sub-block length header, at most GIF_SUB_BLOCK_MAX of them
        public int        GIFE_iPendingLen;          // how many of GIFE_arrPending are live

        public int        GIFE_iBits;                // the bit accumulator, filled LEAST SIGNIFICANT BIT FIRST to match the decoder
        public int        GIFE_iValidBits;           // how many low bits of GIFE_iBits are live; always under 8 between codes

        public int        GIFE_iMinCodeSize;         // the width the dictionary resets to, minus one; also the byte that opens the raster
        public int        GIFE_iCodeSize;            // current code width in bits, from GIFE_iMinCodeSize + 1 up to twelve
        public int        GIFE_iCodeMask;            // (1 << GIFE_iCodeSize) - 1, kept beside the width so the growth test reads as one comparison
        public int        GIFE_iClear;               // the clear code, 1 << GIFE_iMinCodeSize
        public int        GIFE_iEnd;                 // the end-of-information code, one above the clear code
        public int        GIFE_iNext;                // the dictionary slot the NEXT learned string will be given

        public Dictionary<int, int> GIFE_dictStrings; // (prefix code << 8) | next character  ->  the code naming that string
    }

    /*------------------------------------------------------------------------*/
    /* GifQuantPalette:                                                       */
    /* An ADAPTIVE palette, and the k-d tree over RGB space that was built    */
    /* alongside it to look colours up in.                                    */
    /*                                                                        */
    /* THE TREE IS STORED AS A HEAP, which is gif-h's arrangement kept        */
    /* deliberately: the children of node n are nodes 2n and 2n+1, the root   */
    /* is node 1, and nodes from (1 << bitDepth) upward are implicitly the    */
    /* LEAVES -- node (1 << bitDepth) + e holds palette entry e.  So the      */
    /* interior nodes are 1 to (1 << bitDepth) - 1 and the two split arrays   */
    /* only ever need entries for those.                                      */
    /*                                                                        */
    /* IT IS NOT A PROPER k-d TREE AND ITS AUTHOR SAYS SO.  Each interior     */
    /* node splits on whichever axis had the widest range among the pixels    */
    /* that reached it, at the median of those pixels -- but each LEAF then   */
    /* holds the AVERAGE of its subcube, and an average can fall on the far   */
    /* side of an ancestor's split plane.  That makes the pruned descent an   */
    /* APPROXIMATE nearest-neighbour search rather than an exact one.  It is  */
    /* measured rather than assumed: every lookup was compared                */
    /* against an exhaustive scan of the same palette, giving the             */
    /* disagreement rate, so the approximation is a known quantity instead of */
    /* an inherited surprise.                                                 */
    /*                                                                        */
    /* IT IS PUBLIC FOR TESTABILITY, on the same footing as GifDecodeState    */
    /* and GifEncodeState: a test builds a palette, then drives the           */
    /* lookup against an independent brute-force scan.  Neither is            */
    /* reachable through GIF_TryEncode.                                       */
    /*------------------------------------------------------------------------*/

    public sealed class GifQuantPalette
    {
        public int    GIFQ_iBitDepth;                // 1 to 8; the table holds 1 << GIFQ_iBitDepth entries
        public int    GIFQ_iEntries;                 // 1 << GIFQ_iBitDepth, kept beside it because every loop wants it
        public int    GIFQ_iFirstEntry;              // 0 normally; 1 when index 0 is reserved for transparency and is not a colour

        public byte[] GIFQ_arrRed;                   // GIF_PALETTE_ENTRIES entries; only 0 .. GIFQ_iEntries-1 are meaningful
        public byte[] GIFQ_arrGreen;                 // the green channel of the same entries
        public byte[] GIFQ_arrBlue;                  // the blue channel of the same entries

        public byte[] GIFQ_arrSplitElt;              // per INTERIOR node: which axis it splits on, 0 red, 1 green, 2 blue
        public byte[] GIFQ_arrSplitValue;            // per INTERIOR node: the value on that axis the split falls at
    }

    /*------------------------------------------------------------------------*/
    /* GifCodec:                                                              */
    /* The GIF codec, complete in both directions: stills and animation on    */
    /* the decode side, and on the encode side the container, the LZW writer, */
    /* an exact palette, adaptive quantisation with dithering, and animation. */
    /*                                                                        */
    /* PORTABILITY: pure array and integer work, no platform surface.         */
    /*------------------------------------------------------------------------*/

    public static class GifCodec
    {
        /*---------------------------------------------------------------------*/
        /* Block introducers, from the GIF89a specification.  Every one of     */
        /* these is the FIRST byte of a block, read where a block is expected. */
        /*---------------------------------------------------------------------*/

        public const int GIF_BLOCK_IMAGE     = 0x2C;     // image descriptor: a frame's geometry, flags, optional local table and LZW raster
        public const int GIF_BLOCK_EXTENSION = 0x21;     // extension introducer; the next byte names which extension
        public const int GIF_BLOCK_TRAILER   = 0x3B;     // end of the GIF stream; nothing follows

        /*--------------------------------------------------------------------*/
        /* Extension labels.  Only the Graphic Control Extension changes any  */
        /* pixel; the rest are skipped by their sub-block chain.              */
        /*--------------------------------------------------------------------*/

        public const int GIF_EXT_GRAPHIC_CONTROL = 0xF9; // disposal method, transparent index and delay
        public const int GIF_EXT_COMMENT         = 0xFE; // human-readable text; skipped
        public const int GIF_EXT_PLAIN_TEXT      = 0x01; // an obsolete text-rendering block; skipped
        public const int GIF_EXT_APPLICATION     = 0xFF; // application data; NETSCAPE2.0's loop count is read from here

        /*--------------------------------------------------------------------*/
        /* Bit masks in the packed bytes.  The logical screen descriptor and  */
        /* the image descriptor use the SAME bit for "a colour table          */
        /* follows" and the same low three bits for its size, which is why    */
        /* one pair of constants serves both.                                 */
        /*--------------------------------------------------------------------*/

        public const int GIF_FLAG_COLOR_TABLE = 0x80;    // bit 7: a colour table follows this descriptor
        public const int GIF_FLAG_INTERLACED  = 0x40;    // bit 6, IMAGE DESCRIPTOR ONLY: the raster arrives in four passes
        public const int GIF_FLAG_TABLE_SIZE  = 0x07;    // bits 0-2: the table holds 2 << n entries
        public const int GIF_FLAG_TRANSPARENT = 0x01;    // bit 0 of the Graphic Control Extension: the transparent index is meaningful
        public const int GIF_FLAG_DISPOSAL    = 0x1C;    // bits 2-4 of the same byte: the disposal method
        public const int GIF_DISPOSAL_SHIFT   = 2;       // how far to shift GIF_FLAG_DISPOSAL down to read it as a number

        /*--------------------------------------------------------------------*/
        /* THE FOUR DISPOSAL METHODS, from the GIF89a specification.  They    */
        /* say what happens to the canvas AFTER a frame has been shown, so    */
        /* they are applied when the NEXT frame arrives.                      */
        /*--------------------------------------------------------------------*/

        public const int GIF_DISPOSE_UNSPECIFIED = 0;    // no method given; treated exactly as "do not dispose"
        public const int GIF_DISPOSE_KEEP        = 1;    // leave the frame on the canvas
        public const int GIF_DISPOSE_BACKGROUND  = 2;    // clear the frame's own rectangle
        public const int GIF_DISPOSE_PREVIOUS    = 3;    // put the frame's rectangle back as it was before the frame drew

        /*--------------------------------------------------------------------*/
        /* The NETSCAPE application extension, which is where a loop count    */
        /* lives.  It is not in the specification at all -- it is a           */
        /* convention every decoder implements, and the only way a GIF can    */
        /* say how many times to play.                                        */
        /*--------------------------------------------------------------------*/

        private const int    GIF_APP_ID_LENGTH   = 11;             // the identifier plus authentication code, as one span
        private const string GIF_APP_NETSCAPE    = "NETSCAPE2.0";  // the only application extension this decoder reads
        private const int    GIF_NETSCAPE_LOOP   = 1;              // sub-block byte 0 when the payload carries a loop count

        /*--------------------------------------------------------------------*/
        /* A GIF stores its delay in HUNDREDTHS of a second; this library and */
        /* ImageFrameSet both work in milliseconds.                           */
        /*--------------------------------------------------------------------*/

        private const int GIF_DELAY_TO_MS = 10;          // multiply the wire value by this

        /*--------------------------------------------------------------------*/
        /* LZW limits.  The dictionary is capped at 4096 codes by the format  */
        /* -- twelve bits -- and stb sizes its table at 8192, which this file */
        /* keeps so the two can be diffed.  The spare half is never indexed:  */
        /* GIF_LZW_MAX_CODES is what the allocation and the depth bound use,  */
        /* and GIF_LZW_CODE_LIMIT is the twelve-bit ceiling the code size     */
        /* stops growing at.                                                  */
        /*--------------------------------------------------------------------*/

        public const int GIF_LZW_MAX_CODES   = 8192;     // entries in the dictionary arrays, matching stb's codes[8192]
        public const int GIF_LZW_CODE_LIMIT  = 0x0FFF;   // 4095: the largest code a twelve-bit stream can name
        public const int GIF_LZW_MAX_MINIMUM = 12;       // the largest legal LZW minimum code size; anything above is corrupt

        /*--------------------------------------------------------------------*/
        /* A colour table entry is four bytes here -- RGBA -- although the    */
        /* file stores three.  The alpha is synthesised: 255 for every entry  */
        /* except the transparent index, which gets 0.                        */
        /*--------------------------------------------------------------------*/

        public const int GIF_PALETTE_ENTRIES = 256;      // the largest table the format allows
        public const int GIF_PALETTE_STRIDE  = 4;        // bytes per entry once alpha is attached
        public const int GIF_CHANNELS        = 4;        // the canvas is always RGBA; see GIF_TryBuildImage for why

        /*--------------------------------------------------------------------*/
        /* THE ALPHA TEST stb APPLIES WHEN DRAWING, kept exactly.  A palette  */
        /* entry's alpha is only ever 0 or 255 here, so any threshold between */
        /* them behaves the same -- but the constant is named rather than     */
        /* written as a bare 128, because the reason it is a THRESHOLD rather */
        /* than a comparison against zero is not obvious and would invite     */
        /* someone to "simplify" it.                                          */
        /*--------------------------------------------------------------------*/

        public const int GIF_ALPHA_DRAW_ABOVE = 128;     // a palette entry with alpha above this is drawn; at or below, the pixel is left alone

        /*--------------------------------------------------------------------*/
        /* The signature.  Six bytes: "GIF" then a two-digit revision then    */
        /* 'a'.  Only two revisions were ever published.                      */
        /*--------------------------------------------------------------------*/

        private const int GIF_SIG_LENGTH = 6;            // bytes in "GIF87a" or "GIF89a"

        /*--------------------------------------------------------------------*/
        /* THE TRANSPARENCY TRI-STATE.  The shape was settled BEFORE any of   */
        /* this code existed, rather than emerging from the implementation.   */
        /*                                                                    */
        /* WHY THREE VALUES AND NOT A BOOL.  Three separate things hide       */
        /* behind "is palette entry 0 a colour": whether the file DECLARES a  */
        /* transparent index, whether an index is RESERVED out of the table,  */
        /* and whether an animation can DELTA-ENCODE, which needs an index to */
        /* mark unchanged pixels.  One of them is not the caller's to choose  */
        /* -- a source carrying real transparency needs an index, and         */
        /* producing a file without one would turn transparent pixels into    */
        /* some opaque colour.  That is the silent lossy narrowing this       */
        /* library refuses everywhere else, so GIF_TRANSPARENCY_NONE asked of */
        /* such an image is a REFUSAL rather than an override -- which is     */
        /* precisely why AUTO has to exist as the default.                    */
        /*--------------------------------------------------------------------*/

        public const int GIF_TRANSPARENCY_AUTO    = 0;   // reserve an index only when the source actually needs one; the default
        public const int GIF_TRANSPARENCY_NONE    = 1;   // never declare one, so every table entry is a colour; refused when the source has transparent pixels
        public const int GIF_TRANSPARENCY_RESERVE = 2;   // always reserve index 0, even for a fully opaque image

        /*--------------------------------------------------------------------*/
        /* THE LOOP-COUNT SENTINEL.                                           */
        /*                                                                    */
        /* GIFOPT_iLoopCount is read LITERALLY for 0 to 65535, and ZERO MEANS */
        /* FOREVER -- which is a GIF's commonest intent and was the one value */
        /* the option could not express before this.  Only this sentinel      */
        /* defers to ImageFrameSet.FRM_iLoopCount.                            */
        /*                                                                    */
        /* WHY A SENTINEL IS SAFE HERE WHEN GIFOPT_iPaletteSize REFUSES ONE,  */
        /* which is the obvious objection since the two sit in the same       */
        /* class.  Two reasons, and the second is the load-bearing one:       */
        /*                                                                    */
        /*   - It is NAMED, not implied.  This project's objection has always */
        /*     been to a zeroed struct falling SILENTLY into a meaning, not   */
        /*     to sentinels as such -- the same line ImageResampler's         */
        /*     RESAMP_ALPHA_PREMULTIPLIED draws at zero.                      */
        /*                                                                    */
        /*   - A ZEROED STRUCT CANNOT REACH THE ENCODER AT ALL.               */
        /*     GIFOPT_iPaletteSize refuses 0, so a default-constructed        */
        /*     GifEncodeOptions is rejected long before the loop count is     */
        /*     read.  A caller must go through GIF_DefaultEncodeOptions or    */
        /*     set the fields deliberately -- which is exactly the condition  */
        /*     under which -1 cannot be mistaken for "uninitialised".         */
        /*                                                                    */
        /* ANY OTHER NEGATIVE VALUE IS REFUSED rather than read as the        */
        /* sentinel: this is a documented constant, not "negative means       */
        /* default".                                                          */
        /*--------------------------------------------------------------------*/

        public const int GIFOPT_LOOP_FROM_FRAMES = -1;   // take the count from ImageFrameSet.FRM_iLoopCount; the default

        /*--------------------------------------------------------------------*/
        /* ENCODE LIMITS AND THE WIRE CONSTANTS THAT GO WITH THEM.            */
        /*--------------------------------------------------------------------*/

        public const int GIF_SUB_BLOCK_MAX     = 255;    // the largest sub-block the one-byte length prefix can describe
        public const int GIF_ENCODE_MIN_WIDTH  = 2;      // the smallest LZW minimum code size the format allows, even for a two-colour table
        public const int GIF_ENCODE_MAX_WIDTH  = 8;      // the largest one an encoder can need: a 256-entry table
        public const int GIF_PALETTE_MINIMUM   = 2;      // the smallest colour table the size field can describe
        public const int GIF_RGB_STRIDE        = 3;      // bytes per entry ON THE WIRE, where there is no alpha
        public const int GIF_MAX_DIMENSION     = 65535;  // the logical screen fields are unsigned 16-bit
        public const int GIF_MAX_LOOP_COUNT    = 65535;  // the NETSCAPE extension's count is two little-endian bytes
        public const int GIF_BLOCK_TERMINATOR  = 0x00;   // the zero length that ends a sub-block chain
        public const int GIF_GCE_PAYLOAD_SIZE  = 4;      // the Graphic Control Extension's fixed block size byte

        /*--------------------------------------------------------------------*/
        /* Fields of the packed bytes that only the ENCODER writes.  The      */
        /* decoder ignores all three, and so does every reader we compare     */
        /* against, but they are what a conventional file carries and         */
        /* writing something else would make our output the odd one out.      */
        /*--------------------------------------------------------------------*/

        private const int GIF_COLOR_RESOLUTION = 0x70;   // bits 6-4 of the screen descriptor: 7 means eight bits per primary
        private const int GIF_STILL_DISPOSAL   = 0x00;   // bits 4-2 of the extension byte: a still frame specifies no disposal
        private const int GIF_STILL_DELAY      = 0;      // hundredths of a second; a still image does not wait

        /*--------------------------------------------------------------------*/
        /* QUANTISATION CONSTANTS.                                            */
        /*--------------------------------------------------------------------*/

        /* The starting "best" distance for a palette search.  Any real L1      */
        /* distance is at most 765 -- three channels, 255 apart each -- so      */
        /* anything above that is equivalent; the round number is gif-h's and   */
        /* is kept so the two can be diffed.                                    */
        public const int GIF_QUANT_WORST_DIFF = 1000000;  // larger than any achievable sum of three channel differences

        /* Floyd-Steinberg works in a buffer scaled by this, so that error      */
        /* smaller than one colour count survives being carried to the next     */
        /* pixel.  256 rather than a smaller factor because a 1/16 share of a   */
        /* one-count error still has to be non-zero.                            */
        public const int GIF_DITHER_SCALE = 256;          // fixed-point scale for the dither's working buffer

        /*--------------------------------------------------------------------*/
        /* GIF_TryParseHeader:                                                */
        /*                                                                    */
        /* Reads the signature and the logical screen descriptor, and the     */
        /* global colour table when one is present.                           */
        /*                                                                    */
        /* PUBLIC FOR TESTABILITY.  Headers of hand-built files are parsed    */
        /* directly, to check the field order and the refusals, without       */
        /* a raster behind them.                                              */
        /*                                                                    */
        /* THE VERSION IS RECORDED BUT NOT ENFORCED, deliberately.  A GIF87a  */
        /* file carrying an 89a-only block is malformed by the letter of the  */
        /* specification and is read correctly by every decoder in existence, */
        /* including this one; refusing it would reject working files to make */
        /* a point.  What the flag IS good for is telling writers apart:      */
        /* Pillow writes 87a for a plain still and 89a as soon as             */
        /* transparency or animation appears -- so both revisions are         */
        /* genuinely exercised and a scenario can assert which it got.        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     rdrIo    : Positioned at the first byte of the file.  Advanced */
        /*                past the header and the global table.               */
        /*     stateIo  : Receives the palette and the descriptor fields.     */
        /*     hdrOut   : Receives the parsed header, or null on failure.     */
        /*     sReason  : Receives "" on success, or why the file was         */
        /*                refused.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool     : true when the header is present and self-           */
        /*                consistent.                                         */
        /*--------------------------------------------------------------------*/

        public static bool GIF_TryParseHeader(ByteReader rdrIo, GifDecodeState stateIo,
                                              out GifHeader hdrOut, out string sReason)
        {
            GifHeader hdr;
            byte[]    arrSig;
            int       iVersion;
            int       iEntries;
            int       iValue;

            hdrOut  = null;
            sReason = "";

            if (rdrIo == null)
            {   /* A null reader is a CALLER bug, not malformed image data. */
                throw new ArgumentNullException("rdrIo");
            }

            if (stateIo == null)
            {   /* Likewise -- the caller owns the state object. */
                throw new ArgumentNullException("stateIo");
            }

            if (!rdrIo.BYTER_TryReadBytes(GIF_SIG_LENGTH, out arrSig))
            {   /* Fewer than six bytes: this cannot be a GIF at all. */
                sReason = "GIF: the file is too short to hold a signature";
                return(false);
            }

            /* The first four bytes never vary between the two revisions, so   */
            /* they are checked together and the revision digit separately.    */
            if (arrSig[0] != (byte)'G' || arrSig[1] != (byte)'I' ||
                arrSig[2] != (byte)'F' || arrSig[3] != (byte)'8')
            {   /* Not a GIF.  Said plainly, because that is what a caller   */
                /* handing us the wrong bytes most needs told.               */
                sReason = "GIF: the file does not begin with a GIF signature";
                return(false);
            }

            iVersion = (int)arrSig[4];

            if (iVersion != (int)'7' && iVersion != (int)'9')
            {   /* Only 87a and 89a were ever published. */
                sReason = "GIF: revision '8" + ((char)iVersion).ToString() +
                          "' is not 87a or 89a";
                return(false);
            }

            if (arrSig[5] != (byte)'a')
            {   /* The trailing 'a' is part of the signature, not decoration. */
                sReason = "GIF: the signature does not end with 'a'";
                return(false);
            }

            hdr = new GifHeader();

            hdr.GIFH_bIs89a = (iVersion == (int)'9');

            /* EVERY MULTI-BYTE FIELD IN A GIF IS LITTLE-ENDIAN.  This is the   */
            /* only format in this library that is, so the LE reader is used    */
            /* deliberately here and the BE one appears nowhere in this file.   */
            if (!rdrIo.BYTER_TryReadU16LE(out iValue))
            {   /* Truncated inside the logical screen descriptor. */
                sReason = "GIF: the file ends inside the logical screen width";
                return(false);
            }

            hdr.GIFH_iWidth = iValue;

            if (!rdrIo.BYTER_TryReadU16LE(out iValue))
            {   /* Truncated one field later. */
                sReason = "GIF: the file ends inside the logical screen height";
                return(false);
            }

            hdr.GIFH_iHeight = iValue;

            if (!rdrIo.BYTER_TryReadU8(out iValue))
            {   /* Truncated at the packed flags byte. */
                sReason = "GIF: the file ends at the logical screen flags";
                return(false);
            }

            hdr.GIFH_iFlags = iValue;

            if (!rdrIo.BYTER_TryReadU8(out iValue))
            {   /* Truncated at the background index. */
                sReason = "GIF: the file ends at the background colour index";
                return(false);
            }

            hdr.GIFH_iBackgroundIndex = iValue;

            if (!rdrIo.BYTER_TryReadU8(out iValue))
            {   /* Truncated at the aspect ratio. */
                sReason = "GIF: the file ends at the pixel aspect ratio";
                return(false);
            }

            hdr.GIFH_iAspectRatio = iValue;

            /* A ZERO DIMENSION IS REFUSED HERE rather than being allowed to   */
            /* produce an empty buffer downstream.  ImageBuffer's own          */
            /* validation requires at least one pixel each way, so accepting   */
            /* it would only move the failure somewhere less informative.      */
            if (hdr.GIFH_iWidth < 1 || hdr.GIFH_iHeight < 1)
            {   /* A screen with no area cannot hold a picture. */
                sReason = "GIF: the logical screen is " +
                          hdr.GIFH_iWidth.ToString() + "x" +
                          hdr.GIFH_iHeight.ToString() + ", which has no area";
                return(false);
            }

            hdr.GIFH_bHasGlobalTable = ((hdr.GIFH_iFlags & GIF_FLAG_COLOR_TABLE) != 0);

            /* The size field is an EXPONENT, not a count: n means 2 << n   */
            /* entries, so 7 means 256 and 0 means 2.  There is no way to   */
            /* express a table of one entry or of an odd size.              */
            iEntries = 0;

            if (hdr.GIFH_bHasGlobalTable)
            {   /* Bit 7 says a global table follows the descriptor. */
                iEntries = 2 << (hdr.GIFH_iFlags & GIF_FLAG_TABLE_SIZE);
            }

            hdr.GIFH_iGlobalTableEntries = iEntries;

            /* Carry the descriptor fields into the decode state before the    */
            /* table is read, because GIF_TryParseColorTable writes into the   */
            /* state's palette array.                                          */
            stateIo.GIFS_iWidth           = hdr.GIFH_iWidth;
            stateIo.GIFS_iHeight          = hdr.GIFH_iHeight;
            stateIo.GIFS_iFlags           = hdr.GIFH_iFlags;
            stateIo.GIFS_iBackgroundIndex = hdr.GIFH_iBackgroundIndex;

            /* NO TRANSPARENT INDEX IS IN FORCE UNTIL A GRAPHIC CONTROL        */
            /* EXTENSION SAYS SO.  Minus one rather than zero, because zero    */
            /* is a perfectly ordinary palette index and a decoder that used   */
            /* it as the sentinel would silently punch holes in every image    */
            /* whose first palette entry is used.                              */
            stateIo.GIFS_iTransparentIndex = -1;

            if (stateIo.GIFS_arrGlobalPalette == null)
            {   /* First use of this state object: give it its palettes. */
                stateIo.GIFS_arrGlobalPalette =
                    new byte[GIF_PALETTE_ENTRIES * GIF_PALETTE_STRIDE];
                stateIo.GIFS_arrLocalPalette =
                    new byte[GIF_PALETTE_ENTRIES * GIF_PALETTE_STRIDE];
            }

            if (hdr.GIFH_bHasGlobalTable)
            {   /* Read the global table, with no transparent index yet --     */
                /* the Graphic Control Extension that names one has not been   */
                /* seen, and it will punch the alpha when it arrives.          */
                if (!GIF_TryParseColorTable(rdrIo, stateIo.GIFS_arrGlobalPalette,
                                            iEntries, -1, out sReason))
                {   /* The table is truncated. */
                    return(false);
                }
            }

            hdrOut = hdr;

            /* The screen is described and the global palette, if any, is in   */
            /* hand; the caller may now allocate a canvas and walk blocks.     */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* GIF_TryParseColorTable:                                            */
        /*                                                                    */
        /* Reads one colour table of iEntries three-byte RGB entries and      */
        /* stores them as four-byte RGBA.                                     */
        /*                                                                    */
        /* THE ENTRIES ARE STORED RGBA, WHICH IS DEVIATION 2 (see the file    */
        /* header).  stb stores them B, G, R, A and un-swaps at every pixel;  */
        /* its background fill forgets to, and emits the background colour    */
        /* with red and blue exchanged.  Storing the natural order removes    */
        /* both the per-pixel swap and the bug.                               */
        /*                                                                    */
        /* ENTRIES BEYOND iEntries ARE LEFT ALONE, not cleared.  A frame may  */
        /* legally name an index its own table does not define -- the         */
        /* specification calls that undefined rather than illegal -- and      */
        /* GIF_EmitCode clamps such an index instead of reading past the      */
        /* array.  Clearing would cost 256 writes per frame to change the     */
        /* colour of pixels no correct file produces.                         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     rdrIo         : Positioned at the first table byte.  Advanced  */
        /*                     past the whole table.                          */
        /*     arrPaletteIo  : Receives the entries; at least 256 * 4 bytes.  */
        /*     iEntries      : How many entries to read, 2 to 256.            */
        /*     iTransparent  : Palette index to give an alpha of zero, or -1  */
        /*                     when no index is transparent.                  */
        /*     sReason       : Receives "" or why the table was refused.      */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool          : true when the whole table was present.         */
        /*--------------------------------------------------------------------*/

        public static bool GIF_TryParseColorTable(ByteReader rdrIo, byte[] arrPaletteIo,
                                                  int iEntries, int iTransparent,
                                                  out string sReason)
        {
            int iRed;
            int iGreen;
            int iBlue;
            int iBase;
            int i;

            sReason = "";

            for (i = 0; i < iEntries; i++)
            {   /* One three-byte entry per iteration, in file order. */
                if (!rdrIo.BYTER_TryReadU8(out iRed) ||
                    !rdrIo.BYTER_TryReadU8(out iGreen) ||
                    !rdrIo.BYTER_TryReadU8(out iBlue))
                {   /* The table is cut short.  Naming the entry that failed   */
                    /* tells a reader how much of the table was present.       */
                    sReason = "GIF: the colour table ends after " +
                              i.ToString() + " of " + iEntries.ToString() +
                              " entries";
                    return(false);
                }

                iBase = i * GIF_PALETTE_STRIDE;

                arrPaletteIo[iBase]     = (byte)iRed;
                arrPaletteIo[iBase + 1] = (byte)iGreen;
                arrPaletteIo[iBase + 2] = (byte)iBlue;

                if (i == iTransparent)
                {   /* This is the index the Graphic Control Extension named. */
                    arrPaletteIo[iBase + 3] = 0;
                }
                else
                {   /* Every other entry is fully opaque. */
                    arrPaletteIo[iBase + 3] = (byte)ImageBuffer.IMG_OPAQUE_8;
                }
            }

            /* The whole table was read and is now RGBA. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* GIF_TryAllocateCanvas:                                             */
        /*                                                                    */
        /* Allocates the three per-decode buffers once the screen size is     */
        /* known, and the LZW dictionary that goes with them.                 */
        /*                                                                    */
        /* THE CANVAS STARTS FULLY TRANSPARENT, which is not the same as      */
        /* starting at the background colour.  A GIF's first frame may leave  */
        /* pixels undrawn, and what belongs there is decided AFTER the frame  */
        /* by GIF_FillFirstFrameBackground, which needs to know which pixels  */
        /* were drawn -- so the canvas cannot be pre-painted.  A C# array is  */
        /* already zero-filled, so this costs nothing and stb's three memsets */
        /* have no counterpart here.                                          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     stateIo  : Has its width and height set; receives the buffers. */
        /*     sReason  : Receives "" or why the allocation was refused.      */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool     : true when every buffer exists.                      */
        /*--------------------------------------------------------------------*/

        public static bool GIF_TryAllocateCanvas(GifDecodeState stateIo, out string sReason)
        {
            long lPixels;
            long lBytes;
            int  iPixels;

            sReason = "";

            /* THE PRODUCT IS COMPUTED IN 64 BITS BEFORE ANYTHING IS          */
            /* ALLOCATED.  A GIF's dimensions are sixteen bits each, so the   */
            /* worst case is 65535 x 65535 x 4, which is over seventeen       */
            /* thousand million and overflows a signed int to a NEGATIVE      */
            /* number -- and a negative length reaches new byte[] as an       */
            /* exception rather than as the refusal this library's contract   */
            /* requires.  Checking in long is what keeps it a refusal.        */
            lPixels = (long)stateIo.GIFS_iWidth * (long)stateIo.GIFS_iHeight;
            lBytes  = lPixels * (long)GIF_CHANNELS;

            if (lBytes > (long)int.MaxValue)
            {   /* The canvas cannot be addressed by a C# array index. */
                sReason = "GIF: a logical screen of " +
                          stateIo.GIFS_iWidth.ToString() + "x" +
                          stateIo.GIFS_iHeight.ToString() +
                          " needs more than two gigabytes of canvas";
                return(false);
            }

            iPixels = (int)lPixels;

            stateIo.GIFS_arrOut        = new byte[iPixels * GIF_CHANNELS];
            stateIo.GIFS_arrBackground = new byte[iPixels * GIF_CHANNELS];
            stateIo.GIFS_arrHistory    = new byte[iPixels];
            stateIo.GIFS_arrRestore    = new byte[iPixels * GIF_CHANNELS];

            stateIo.GIFS_arrCodePrefix = new short[GIF_LZW_MAX_CODES];
            stateIo.GIFS_arrCodeFirst  = new byte[GIF_LZW_MAX_CODES];
            stateIo.GIFS_arrCodeSuffix = new byte[GIF_LZW_MAX_CODES];
            stateIo.GIFS_arrEmitStack  = new byte[GIF_LZW_MAX_CODES];

            stateIo.GIFS_iLineSize = stateIo.GIFS_iWidth * GIF_CHANNELS;

            /* The canvas, its two companions and the dictionary all exist. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* GIF_EmitCode:                                                      */
        /*                                                                    */
        /* Writes the whole character string one LZW code stands for, in the  */
        /* order the picture needs it.                                        */
        /*                                                                    */
        /* THIS IS DEVIATION 1, AND IT IS THE ONE PLACE THIS FILE'S SHAPE     */
        /* DIFFERS FROM ITS SOURCE ON PURPOSE.  A dictionary entry stores the */
        /* index of the code it EXTENDS plus the one character it appends, so */
        /* the string is a backward chain and the characters come out in      */
        /* reverse.  stb resolves that by recursing into itself before        */
        /* writing anything, which unwinds the chain on the C call stack.     */
        /* A chain may legally reach 4096 characters, and in C# a stack       */
        /* overflow cannot be caught -- it terminates the process -- so a     */
        /* crafted GIF would be fatal rather than refused.                    */
        /*                                                                    */
        /* So the chain is pushed onto GIFS_arrEmitStack walking backwards    */
        /* and replayed forwards.  The characters written, and their order,   */
        /* are identical to the recursion's.                                  */
        /*                                                                    */
        /* THE DEPTH BOUND IS A REFUSAL, NOT AN ASSERTION.  A valid           */
        /* dictionary cannot loop, because an entry's prefix is always an     */
        /* index BELOW its own and roots carry -1 -- so the walk terminates   */
        /* in at most GIF_LZW_MAX_CODES steps.  A corrupt one could not, and  */
        /* an unbounded walk would spin forever on it.                        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     stateIo  : Supplies the dictionary and the write cursor.       */
        /*     iCode    : The code to expand and draw.                        */
        /*     sReason  : Receives "" or why the code was refused.            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool     : true when the string was written.                   */
        /*--------------------------------------------------------------------*/

        public static bool GIF_EmitCode(GifDecodeState stateIo, int iCode, out string sReason)
        {
            int iDepth;
            int iWalk;
            int i;

            sReason = "";
            iDepth  = 0;
            iWalk   = iCode;

            while (iWalk >= 0)
            {   /* Walk the prefix chain, collecting suffixes as we go.  They   */
                /* come out last-character-first, which is why they are         */
                /* stacked rather than written.                                 */
                if (iDepth >= GIF_LZW_MAX_CODES)
                {   /* Longer than the dictionary can possibly be, so the   */
                    /* chain must be corrupt.  Refuse rather than spin.     */
                    sReason = "GIF: an LZW code expands to a chain longer " +
                              "than the dictionary, so the dictionary is corrupt";
                    return(false);
                }

                stateIo.GIFS_arrEmitStack[iDepth] = stateIo.GIFS_arrCodeSuffix[iWalk];
                iDepth = iDepth + 1;
                iWalk  = (int)stateIo.GIFS_arrCodePrefix[iWalk];
            }

            for (i = iDepth - 1; i >= 0; i--)
            {   /* Replay forwards: the deepest prefix is the first character. */
                GIF_PutPixel(stateIo, (int)stateIo.GIFS_arrEmitStack[i]);
            }

            /* The whole string is on the canvas, or was clipped away by the    */
            /* frame's bottom edge, which GIF_PutPixel decides per character.   */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* GIF_PutPixel:                                                      */
        /*                                                                    */
        /* Draws one palette index at the write cursor and advances it,       */
        /* including across the interlace passes.                             */
        /*                                                                    */
        /* A TRANSPARENT PIXEL IS NOT DRAWN AT ALL -- it is not drawn AS      */
        /* transparent.  Whatever the canvas already held stays, which is how */
        /* a GIF frame shows the frame beneath it through its holes.  Writing */
        /* a transparent black would erase the canvas instead, and on a       */
        /* single frame the two are indistinguishable, so this is the sort of */
        /* fault that appears only once animation is added.  The cursor       */
        /* advances either way: a transparent pixel still occupies its place. */
        /*                                                                    */
        /* THE HISTORY BYTE IS SET EVEN FOR A TRANSPARENT PIXEL, matching     */
        /* stb.  History records where the frame CLAIMED territory, not where */
        /* it changed a colour, and that is what the first-frame background   */
        /* fill and the disposal methods both need.                           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     stateIo  : Supplies the cursor, the canvas and the palette.    */
        /*     iIndex   : Palette index to draw, 0 to 255.                    */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void     : The canvas and the cursor are updated in place.     */
        /*--------------------------------------------------------------------*/

        public static void GIF_PutPixel(GifDecodeState stateIo, int iIndex)
        {
            int iOut;
            int iEntry;
            int iAlpha;

            if (stateIo.GIFS_iCurY >= stateIo.GIFS_iMaxY)
            {   /* Past the frame's bottom edge.  A raster may legally carry    */
                /* more characters than the frame has pixels -- the last LZW    */
                /* code often overruns -- so this is an ordinary stop, not an   */
                /* error, and it is exactly where stb returns too.              */
                return;
            }

            iOut   = stateIo.GIFS_iCurX + stateIo.GIFS_iCurY;
            iEntry = (iIndex & 0xFF) * GIF_PALETTE_STRIDE;

            /* GIFS_arrHistory IS INDEXED IN PIXELS while the cursor is in      */
            /* bytes, so the offset is divided by the channel count.  This is   */
            /* the one place the byte-scaled coordinates need translating and   */
            /* it is the one place a reader is likely to trip.                  */
            stateIo.GIFS_arrHistory[iOut / GIF_CHANNELS] = 1;

            iAlpha = (int)stateIo.GIFS_arrColorTable[iEntry + 3];

            if (iAlpha > GIF_ALPHA_DRAW_ABOVE)
            {   /* An opaque entry: paint it.  The palette is already RGBA in   */
                /* this file (deviation 2), so the four bytes copy straight     */
                /* through with no channel swap.                                */
                stateIo.GIFS_arrOut[iOut]     = stateIo.GIFS_arrColorTable[iEntry];
                stateIo.GIFS_arrOut[iOut + 1] = stateIo.GIFS_arrColorTable[iEntry + 1];
                stateIo.GIFS_arrOut[iOut + 2] = stateIo.GIFS_arrColorTable[iEntry + 2];
                stateIo.GIFS_arrOut[iOut + 3] = stateIo.GIFS_arrColorTable[iEntry + 3];
            }

            stateIo.GIFS_iCurX = stateIo.GIFS_iCurX + GIF_CHANNELS;

            if (stateIo.GIFS_iCurX >= stateIo.GIFS_iMaxX)
            {   /* End of a raster row: back to the frame's left edge and     */
                /* down by one STEP, which is one row on a normal frame and   */
                /* eight on an interlaced frame's first pass.                 */
                stateIo.GIFS_iCurX = stateIo.GIFS_iStartX;
                stateIo.GIFS_iCurY = stateIo.GIFS_iCurY + stateIo.GIFS_iStep;

                while (stateIo.GIFS_iCurY >= stateIo.GIFS_iMaxY &&
                       stateIo.GIFS_iParse > 0)
                {   /* An interlace pass has finished, so drop to the next      */
                    /* one.  GIF interlacing is four passes: rows 0, 8, 16...   */
                    /* then 4, 12, 20... then 2, 6, 10... then every odd row.   */
                    /* Each pass halves the spacing and starts halfway into     */
                    /* the previous pass's gap, which is what the shift by      */
                    /* one expresses.  A WHILE rather than an IF because a      */
                    /* short frame can exhaust several passes at once -- a      */
                    /* three-row frame has no rows at all in pass two.          */
                    stateIo.GIFS_iStep  = (1 << stateIo.GIFS_iParse) * stateIo.GIFS_iLineSize;
                    stateIo.GIFS_iCurY  = stateIo.GIFS_iStartY + (stateIo.GIFS_iStep >> 1);
                    stateIo.GIFS_iParse = stateIo.GIFS_iParse - 1;
                }
            }
        }

        /*--------------------------------------------------------------------*/
        /* GIF_TryDecodeLzwRaster:                                            */
        /*                                                                    */
        /* Decodes one frame's LZW-compressed raster onto the canvas.         */
        /*                                                                    */
        /* GIF's LZW IS NOT DEFLATE AND NOT THE UNISYS TIFF VARIANT.  Codes   */
        /* are packed LEAST SIGNIFICANT BIT FIRST within each byte, which is  */
        /* the opposite of JPEG's entropy stream in this same library, and    */
        /* the code width GROWS as the dictionary fills -- from the minimum   */
        /* code size plus one, up to twelve bits.  Two codes are reserved     */
        /* immediately above the alphabet: CLEAR resets the dictionary and    */
        /* the width, and END terminates the raster.                          */
        /*                                                                    */
        /* THE DATA ARRIVES IN SUB-BLOCKS of at most 255 bytes each, chained  */
        /* by a leading length byte and ended by a length of zero.  The       */
        /* refill below therefore fetches a new length whenever the current   */
        /* chunk is spent, which is why iLen is loop state rather than a      */
        /* parameter.                                                         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     rdrIo    : Positioned at the LZW minimum code size byte.       */
        /*     stateIo  : Supplies the canvas, cursor, palette and            */
        /*                dictionary.                                         */
        /*     sReason  : Receives "" or why the raster was refused.          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool     : true when the raster ended cleanly.                 */
        /*--------------------------------------------------------------------*/

        public static bool GIF_TryDecodeLzwRaster(ByteReader rdrIo, GifDecodeState stateIo,
                                                  out string sReason)
        {
            int  iMinCodeSize;
            int  iClear;
            int  iCodeSize;
            int  iCodeMask;
            int  iBits;
            int  iValidBits;
            int  iAvail;
            int  iOldCode;
            int  iLen;
            int  iCode;
            int  iByte;
            int  iEntry;
            bool bSeenClear;
            int  i;

            sReason = "";

            if (!rdrIo.BYTER_TryReadU8(out iMinCodeSize))
            {   /* The frame has a descriptor but no raster behind it. */
                sReason = "GIF: the file ends where the LZW minimum code size should be";
                return(false);
            }

            if (iMinCodeSize > GIF_LZW_MAX_MINIMUM)
            {   /* Above twelve there is no legal code width to start from. */
                sReason = "GIF: an LZW minimum code size of " +
                          iMinCodeSize.ToString() + " exceeds the maximum of " +
                          GIF_LZW_MAX_MINIMUM.ToString();
                return(false);
            }

            iClear     = 1 << iMinCodeSize;
            iCodeSize  = iMinCodeSize + 1;
            iCodeMask  = (1 << iCodeSize) - 1;
            iBits      = 0;
            iValidBits = 0;

            for (i = 0; i < iClear; i++)
            {   /* Seed the dictionary with the single-character strings.  A   */
                /* root has no prefix, which is what minus one records.        */
                stateIo.GIFS_arrCodePrefix[i] = -1;
                stateIo.GIFS_arrCodeFirst[i]  = (byte)i;
                stateIo.GIFS_arrCodeSuffix[i] = (byte)i;
            }

            /* THE TWO CODES ABOVE THE ALPHABET ARE RESERVED, so the first      */
            /* dictionary entry a stream can define is at clear + 2.  Seeding   */
            /* iAvail here rather than at the first clear code is what lets a   */
            /* stream that omits its leading clear code still have a            */
            /* consistent dictionary -- although bSeenClear then refuses it     */
            /* anyway.  stb's comment claims the opposite; see the file         */
            /* header.                                                          */
            iAvail     = iClear + 2;
            iOldCode   = -1;
            iLen       = 0;
            bSeenClear = false;

            for (;;)
            {   /* One pass either refills the bit window or spends a code    */
                /* from it, never both, which keeps the two concerns apart.   */
                if (iValidBits < iCodeSize)
                {   /* Not enough bits buffered to read a whole code. */
                    if (iLen == 0)
                    {   /* The current sub-block is spent; fetch the next   */
                        /* one's length.                                    */
                        if (!rdrIo.BYTER_TryReadU8(out iLen))
                        {   /* The chain of sub-blocks was never terminated. */
                            sReason = "GIF: the file ends inside the LZW sub-block chain";
                            return(false);
                        }

                        if (iLen == 0)
                        {   /* A zero length ends the chain.  Reaching here    */
                            /* means the raster stopped without an end code,   */
                            /* which stb accepts and so does this -- real      */
                            /* encoders do it and the picture is complete.     */
                            return(true);
                        }
                    }

                    if (!rdrIo.BYTER_TryReadU8(out iByte))
                    {   /* A sub-block declared more bytes than the file has. */
                        sReason = "GIF: the file ends inside an LZW sub-block";
                        return(false);
                    }

                    iLen = iLen - 1;

                    /* LEAST SIGNIFICANT BIT FIRST: each new byte goes ABOVE   */
                    /* the bits already buffered, and codes come off the       */
                    /* bottom.  Shifting the other way decodes plausible       */
                    /* nonsense rather than failing.                           */
                    iBits      = iBits | (iByte << iValidBits);
                    iValidBits = iValidBits + 8;
                }
                else
                {   /* Enough bits buffered: take one code off the bottom. */
                    iCode      = iBits & iCodeMask;
                    iBits      = iBits >> iCodeSize;
                    iValidBits = iValidBits - iCodeSize;

                    if (iCode == iClear)
                    {   /* CLEAR: forget every learned string and go back to   */
                        /* the starting code width.  An encoder emits this     */
                        /* whenever the dictionary fills.                      */
                        iCodeSize  = iMinCodeSize + 1;
                        iCodeMask  = (1 << iCodeSize) - 1;
                        iAvail     = iClear + 2;
                        iOldCode   = -1;
                        bSeenClear = true;
                    }
                    else if (iCode == iClear + 1)
                    {   /* END OF INFORMATION: the raster is complete.  Walk   */
                        /* the rest of the sub-block chain so the reader is    */
                        /* left positioned at the next block rather than in    */
                        /* the middle of this one.                             */
                        if (!GIF_TrySkipSubBlockChain(rdrIo, iLen, out sReason))
                        {   /* Truncated in the trailing sub-blocks. */
                            return(false);
                        }

                        /* The frame's raster ended where it said it would. */
                        return(true);
                    }
                    else if (iCode <= iAvail)
                    {   /* An ordinary data code -- either one already in the    */
                        /* dictionary, or the one this step is about to          */
                        /* define, which is the classic self-referential case.   */
                        if (!bSeenClear)
                        {   /* A raster must open with a clear code.  Without   */
                            /* one the dictionary state is whatever the last    */
                            /* frame left, so the output would be garbage       */
                            /* that looked like a picture.                      */
                            sReason = "GIF: the LZW raster carries data before any clear code";
                            return(false);
                        }

                        if (iOldCode >= 0)
                        {   /* Learn a new string: the previous one plus the   */
                            /* first character of this one.                    */
                            iEntry = iAvail;
                            iAvail = iAvail + 1;

                            if (iAvail > GIF_LZW_MAX_CODES)
                            {   /* More codes than the dictionary can hold. */
                                sReason = "GIF: the LZW stream defines more than " +
                                          GIF_LZW_MAX_CODES.ToString() + " codes";
                                return(false);
                            }

                            stateIo.GIFS_arrCodePrefix[iEntry] = (short)iOldCode;
                            stateIo.GIFS_arrCodeFirst[iEntry]  =
                                stateIo.GIFS_arrCodeFirst[iOldCode];

                            /* THE SELF-REFERENTIAL CASE.  When the incoming    */
                            /* code IS the entry being defined, its first       */
                            /* character is not yet known from the dictionary   */
                            /* -- it is the first character of the previous     */
                            /* string, which is what was just stored.  Both     */
                            /* arms of stb's ternary give that same answer      */
                            /* here, because its comparison is made after the   */
                            /* increment and can therefore never be true;       */
                            /* the form below is the one that is actually       */
                            /* reachable.                                       */
                            if (iCode == iEntry)
                            {   /* The stream named the entry being defined. */
                                stateIo.GIFS_arrCodeSuffix[iEntry] =
                                    stateIo.GIFS_arrCodeFirst[iEntry];
                            }
                            else
                            {   /* An ordinary already-known code. */
                                stateIo.GIFS_arrCodeSuffix[iEntry] =
                                    stateIo.GIFS_arrCodeFirst[iCode];
                            }
                        }
                        else if (iCode == iAvail)
                        {   /* The first code after a clear cannot be the   */
                            /* not-yet-defined one: there is no previous    */
                            /* string for it to extend.                     */
                            sReason = "GIF: the LZW raster names an undefined code " +
                                      iCode.ToString() + " with no previous code";
                            return(false);
                        }

                        if (!GIF_EmitCode(stateIo, iCode, out sReason))
                        {   /* The dictionary chain was corrupt. */
                            return(false);
                        }

                        /* THE CODE WIDTH GROWS WHEN THE DICTIONARY FILLS the   */
                        /* current width, and stops at twelve bits.  Testing    */
                        /* the mask rather than a power of two is stb's form    */
                        /* and is the same test: avail has just wrapped to a    */
                        /* multiple of the width's capacity.                    */
                        if ((iAvail & iCodeMask) == 0 && iAvail <= GIF_LZW_CODE_LIMIT)
                        {   /* One more bit per code from here on. */
                            iCodeSize = iCodeSize + 1;
                            iCodeMask = (1 << iCodeSize) - 1;
                        }

                        iOldCode = iCode;
                    }
                    else
                    {   /* A code beyond anything the dictionary defines. */
                        sReason = "GIF: the LZW raster names code " +
                                  iCode.ToString() + " but only " +
                                  iAvail.ToString() + " codes are defined";
                        return(false);
                    }
                }
            }
        }

        /*--------------------------------------------------------------------*/
        /* GIF_TrySkipSubBlockChain:                                          */
        /*                                                                    */
        /* Walks to the end of a sub-block chain, discarding what it holds.   */
        /*                                                                    */
        /* USED FROM TWO PLACES THAT LOOK UNRELATED and are the same problem: */
        /* an extension whose payload we do not care about, and the tail of   */
        /* an LZW raster after its end code.  Both are chains of length-      */
        /* prefixed chunks and both must leave the reader on the next BLOCK,  */
        /* because a reader left mid-chain reads a payload byte as a block    */
        /* introducer and refuses a perfectly good file.                      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     rdrIo       : Positioned inside the chain.  Advanced past its  */
        /*                   terminating zero.                                */
        /*     iPendingLen : Bytes still owed by the CURRENT chunk, or zero   */
        /*                   when positioned at a length byte.                */
        /*     sReason     : Receives "" or why the chain was refused.        */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool        : true when the terminating zero was reached.      */
        /*--------------------------------------------------------------------*/

        public static bool GIF_TrySkipSubBlockChain(ByteReader rdrIo, int iPendingLen,
                                                    out string sReason)
        {
            int iLen;

            sReason = "";

            if (iPendingLen > 0)
            {   /* Finish the chunk that was already in progress. */
                if (!rdrIo.BYTER_TrySkip(iPendingLen))
                {   /* The chunk claimed more bytes than the file holds. */
                    sReason = "GIF: a sub-block claims " + iPendingLen.ToString() +
                              " bytes past the end of the file";
                    return(false);
                }
            }

            for (;;)
            {   /* One chunk per iteration until the zero-length terminator. */
                if (!rdrIo.BYTER_TryReadU8(out iLen))
                {   /* The chain runs off the end of the file. */
                    sReason = "GIF: the file ends inside a sub-block chain";
                    return(false);
                }

                if (iLen == 0)
                {   /* The terminator.  The reader now sits on the next block. */
                    return(true);
                }

                if (!rdrIo.BYTER_TrySkip(iLen))
                {   /* A chunk claims more bytes than remain. */
                    sReason = "GIF: a sub-block claims " + iLen.ToString() +
                              " bytes past the end of the file";
                    return(false);
                }
            }
        }

        /*--------------------------------------------------------------------*/
        /* GIF_TryParseGraphicControl:                                        */
        /*                                                                    */
        /* Reads a Graphic Control Extension, which is where transparency,    */
        /* the frame delay and the disposal method live.                      */
        /*                                                                    */
        /* IT APPLIES TO THE NEXT FRAME, NOT THIS ONE.  The block sits BEFORE */
        /* the image descriptor it governs, so its effects are recorded in    */
        /* the state and consumed when the descriptor arrives.                */
        /*                                                                    */
        /* THE TRANSPARENT INDEX IS PUNCHED INTO THE GLOBAL PALETTE'S ALPHA,  */
        /* and the PREVIOUS one is restored to opaque first.  A GIF may       */
        /* change its transparent index between frames, and a decoder that    */
        /* only ever punched holes would accumulate them until most of the    */
        /* palette was invisible.  Restoring first is what keeps the palette  */
        /* a function of the CURRENT extension rather than of every extension */
        /* so far.                                                            */
        /*                                                                    */
        /* A BLOCK SIZE OTHER THAN FOUR IS SKIPPED, NOT REFUSED, which is     */
        /* stb's behaviour and the tolerant reading.  The specification fixes */
        /* the size at four; a writer that disagrees has produced something   */
        /* this decoder cannot interpret, and ignoring one extension is a far */
        /* better answer than rejecting the picture.                          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     rdrIo    : Positioned at the extension's block size byte.      */
        /*     stateIo  : Receives the flags, delay and transparent index.    */
        /*     sReason  : Receives "" or why the extension was refused.       */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool     : true when the extension was read or skipped         */
        /*                cleanly.                                            */
        /*--------------------------------------------------------------------*/

        public static bool GIF_TryParseGraphicControl(ByteReader rdrIo, GifDecodeState stateIo,
                                                      out string sReason)
        {
            int iBlockSize;
            int iFlags;
            int iDelay;
            int iIndex;
            int iBase;

            sReason = "";

            if (!rdrIo.BYTER_TryReadU8(out iBlockSize))
            {   /* Truncated at the block size. */
                sReason = "GIF: the file ends at a graphic control block size";
                return(false);
            }

            if (iBlockSize != 4)
            {   /* Not the shape the specification defines, so it cannot be   */
                /* interpreted.  Skip its payload and carry on.               */
                return(GIF_TrySkipSubBlockChain(rdrIo, iBlockSize, out sReason));
            }

            if (!rdrIo.BYTER_TryReadU8(out iFlags))
            {   /* Truncated at the packed field. */
                sReason = "GIF: the file ends inside a graphic control extension";
                return(false);
            }

            if (!rdrIo.BYTER_TryReadU16LE(out iDelay))
            {   /* Truncated at the delay. */
                sReason = "GIF: the file ends at a graphic control delay";
                return(false);
            }

            if (!rdrIo.BYTER_TryReadU8(out iIndex))
            {   /* Truncated at the transparent index. */
                sReason = "GIF: the file ends at a graphic control transparent index";
                return(false);
            }

            stateIo.GIFS_iExtFlags = iFlags;

            /* THE DISPOSAL METHOD IS THREE BITS IN THE MIDDLE OF THE PACKED   */
            /* BYTE, and reading it as the whole byte -- or forgetting the     */
            /* shift -- yields a number that is occasionally right, which is   */
            /* the worst kind of wrong.  Values above 3 are reserved and are   */
            /* carried through unchanged rather than rejected: a file using    */
            /* one is not corrupt, it is using something we do not know, and   */
            /* GIF_ApplyPendingDisposal treats anything unrecognised as        */
            /* "leave it alone", which is the safe reading.                    */
            stateIo.GIFS_iDisposal =
                (iFlags & GIF_FLAG_DISPOSAL) >> GIF_DISPOSAL_SHIFT;

            /* THE DELAY IS IN HUNDREDTHS OF A SECOND ON THE WIRE and this   */
            /* library reports milliseconds, which is what ImageFrameSet     */
            /* declares and what every consumer of an animation wants.       */
            stateIo.GIFS_iDelayMs = iDelay * GIF_DELAY_TO_MS;

            if (stateIo.GIFS_iTransparentIndex >= 0)
            {   /* Undo the previous extension's hole before punching a new   */
                /* one, so the palette reflects only what is in force now.    */
                iBase = stateIo.GIFS_iTransparentIndex * GIF_PALETTE_STRIDE;
                stateIo.GIFS_arrGlobalPalette[iBase + 3] = (byte)ImageBuffer.IMG_OPAQUE_8;
            }

            if ((iFlags & GIF_FLAG_TRANSPARENT) != 0)
            {   /* An index is transparent from here until an extension says   */
                /* otherwise.                                                  */
                stateIo.GIFS_iTransparentIndex = iIndex;

                iBase = iIndex * GIF_PALETTE_STRIDE;
                stateIo.GIFS_arrGlobalPalette[iBase + 3] = 0;
            }
            else
            {   /* The index byte is present but meaningless when the flag is   */
                /* clear, so nothing is transparent.                            */
                stateIo.GIFS_iTransparentIndex = -1;
            }

            /* The extension's payload is followed by its own chain   */
            /* terminator, which has to be consumed like any other.   */
            return(GIF_TrySkipSubBlockChain(rdrIo, 0, out sReason));
        }

        /*--------------------------------------------------------------------*/
        /* GIF_TrySetUpFrameWalk:                                             */
        /*                                                                    */
        /* Turns an image descriptor's geometry into the byte-scaled cursor   */
        /* the raster decoder walks, and chooses the interlace schedule.      */
        /*                                                                    */
        /* THE FRAME IS BOUNDS-CHECKED AGAINST THE SCREEN and a frame that    */
        /* hangs off the edge is REFUSED rather than clipped.  stb refuses it */
        /* too, and the reason is that clipping would need the raster walk to */
        /* test both axes on every pixel, where refusing tests two sums once. */
        /* No encoder produces such a file; a fuzzer does.                    */
        /*                                                                    */
        /* A ZERO-WIDTH FRAME IS THE CASE THAT NEEDS SAYING.  The cursor is   */
        /* driven straight past the bottom edge so GIF_PutPixel discards      */
        /* every character, because with no columns the row-advance test can  */
        /* never fire and the walk would otherwise write the whole raster     */
        /* into one place.  A zero-HEIGHT frame needs no such help: max_y     */
        /* equals start_y, so the very first test already stops it.           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     stateIo  : Receives the cursor, extents and interlace state.   */
        /*     iLeft    : Frame origin from the descriptor, in pixels.        */
        /*     iTop     : Frame origin from the descriptor, in pixels.        */
        /*     iWidth   : Frame width in pixels.                              */
        /*     iHeight  : Frame height in pixels.                             */
        /*     sReason  : Receives "" or why the geometry was refused.        */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool     : true when the frame fits the logical screen.        */
        /*--------------------------------------------------------------------*/

        public static bool GIF_TrySetUpFrameWalk(GifDecodeState stateIo, int iLeft, int iTop,
                                                 int iWidth, int iHeight, out string sReason)
        {
            sReason = "";

            if ((iLeft + iWidth) > stateIo.GIFS_iWidth ||
                (iTop + iHeight) > stateIo.GIFS_iHeight)
            {   /* The frame claims territory outside the canvas. */
                sReason = "GIF: a frame at " + iLeft.ToString() + "," +
                          iTop.ToString() + " sized " + iWidth.ToString() + "x" +
                          iHeight.ToString() + " does not fit a " +
                          stateIo.GIFS_iWidth.ToString() + "x" +
                          stateIo.GIFS_iHeight.ToString() + " logical screen";
                return(false);
            }

            stateIo.GIFS_iLineSize = stateIo.GIFS_iWidth * GIF_CHANNELS;
            stateIo.GIFS_iStartX   = iLeft * GIF_CHANNELS;
            stateIo.GIFS_iStartY   = iTop * stateIo.GIFS_iLineSize;
            stateIo.GIFS_iMaxX     = stateIo.GIFS_iStartX + (iWidth * GIF_CHANNELS);
            stateIo.GIFS_iMaxY     = stateIo.GIFS_iStartY + (iHeight * stateIo.GIFS_iLineSize);
            stateIo.GIFS_iCurX     = stateIo.GIFS_iStartX;
            stateIo.GIFS_iCurY     = stateIo.GIFS_iStartY;

            if (iWidth == 0)
            {   /* No columns, so no pixel can ever be written.  Park the   */
                /* cursor past the bottom edge, which is the one test       */
                /* GIF_PutPixel makes before doing anything.                */
                stateIo.GIFS_iCurY = stateIo.GIFS_iMaxY;
            }

            if ((stateIo.GIFS_iLocalFlags & GIF_FLAG_INTERLACED) != 0)
            {   /* Four passes: start eight rows apart with three passes to   */
                /* follow.                                                    */
                stateIo.GIFS_iStep  = 8 * stateIo.GIFS_iLineSize;
                stateIo.GIFS_iParse = 3;
            }
            else
            {   /* One pass, one row at a time. */
                stateIo.GIFS_iStep  = stateIo.GIFS_iLineSize;
                stateIo.GIFS_iParse = 0;
            }

            /* The cursor is at the frame's top-left and the schedule is set. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* GIF_FillFirstFrameBackground:                                      */
        /*                                                                    */
        /* Paints the background colour into every pixel the FIRST frame did  */
        /* not draw.                                                          */
        /*                                                                    */
        /* ONLY THE FIRST FRAME, AND ONLY WHEN THE BACKGROUND INDEX IS        */
        /* NON-ZERO.  Both conditions are stb's.  After the first frame the   */
        /* undrawn pixels belong to whatever was there before, which is the   */
        /* whole point of an animation; and an index of zero is what every    */
        /* writer emits when it has nothing to say, so honouring it would     */
        /* paint most of the world's GIFs with their first palette entry.     */
        /*                                                                    */
        /* THIS IS WHERE DEVIATION 2 LIVES.  stb copies the palette entry     */
        /* into the canvas with no channel swap while its per-pixel path      */
        /* swaps -- so upstream, the background comes out with red and blue   */
        /* exchanged.  Because this file stores the palette RGBA to begin     */
        /* with, both paths are the same copy and the two agree.              */
        /*                                                                    */
        /* THE ENTRY IS FORCED OPAQUE FIRST, matching stb.  The background    */
        /* index may also be the transparent index, in which case the         */
        /* extension punched its alpha to zero -- and a transparent           */
        /* background would leave the undrawn area showing nothing, which is  */
        /* what the canvas already holds and would make this whole pass a     */
        /* no-op.                                                             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     stateIo  : Supplies the canvas, the history and the palette.   */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void     : The canvas is updated in place.                     */
        /*--------------------------------------------------------------------*/

        public static void GIF_FillFirstFrameBackground(GifDecodeState stateIo)
        {
            int iPixels;
            int iBase;
            int iOut;
            int i;

            if (stateIo.GIFS_iBackgroundIndex <= 0)
            {   /* Nothing to paint: index zero is the "unspecified" value   */
                /* every writer emits.                                       */
                return;
            }

            iBase = stateIo.GIFS_iBackgroundIndex * GIF_PALETTE_STRIDE;

            /* Undo any transparency on this entry; the background is a   */
            /* colour, not a hole.                                        */
            stateIo.GIFS_arrGlobalPalette[iBase + 3] = (byte)ImageBuffer.IMG_OPAQUE_8;

            iPixels = stateIo.GIFS_iWidth * stateIo.GIFS_iHeight;

            for (i = 0; i < iPixels; i++)
            {   /* One canvas pixel per iteration. */
                if (stateIo.GIFS_arrHistory[i] == 0)
                {   /* The frame never claimed this pixel, so the background   */
                    /* shows through.                                          */
                    iOut = i * GIF_CHANNELS;

                    stateIo.GIFS_arrOut[iOut]     = stateIo.GIFS_arrGlobalPalette[iBase];
                    stateIo.GIFS_arrOut[iOut + 1] = stateIo.GIFS_arrGlobalPalette[iBase + 1];
                    stateIo.GIFS_arrOut[iOut + 2] = stateIo.GIFS_arrGlobalPalette[iBase + 2];
                    stateIo.GIFS_arrOut[iOut + 3] = stateIo.GIFS_arrGlobalPalette[iBase + 3];
                }
            }
        }

        /*--------------------------------------------------------------------*/
        /* GIF_TryDecodeNextFrame:                                            */
        /*                                                                    */
        /* Walks blocks until ONE image descriptor has been decoded onto the  */
        /* canvas, or the stream ends.  Called once for a still image and     */
        /* repeatedly for an animation.                                       */
        /*                                                                    */
        /* THE BLOCK WALK IS A LOOP RATHER THAN A LOOKUP because any number   */
        /* of extensions may precede a frame and their order is not fixed.    */
        /* GDI+ writes a Graphic Control Extension before its still image; an */
        /* animation adds an application extension; a comment may sit         */
        /* anywhere.  A decoder that expected the descriptor immediately      */
        /* would refuse most real files.                                      */
        /*                                                                    */
        /* THE PREVIOUS FRAME'S DISPOSAL IS CARRIED OUT HERE, AT THE TOP, and */
        /* that is the ordering the whole animation model turns on.  A        */
        /* Graphic Control Extension says what to do with the frame it        */
        /* introduces ONCE THAT FRAME HAS BEEN SHOWN -- so a frame is emitted */
        /* whole, and its disposal is remembered and performed when the next  */
        /* frame arrives.  Doing it at the end of a frame instead would erase */
        /* the frame before the caller ever received it.                      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     rdrIo         : Positioned after the header, or after the      */
        /*                     previous frame.                                */
        /*     stateIo       : Supplies and receives the whole decode state.  */
        /*     bFirstFrame   : true only for the first frame of the stream.   */
        /*                     Controls the background fill, which applies    */
        /*                     once and never again.                          */
        /*     bEndedOut     : Receives true when the trailer was reached     */
        /*                     before another frame -- for an animation that  */
        /*                     is the normal way the stream finishes.         */
        /*     sReason       : Receives "" or why the stream was refused.     */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool          : true when a frame was decoded, or when the     */
        /*                     stream ended cleanly with none left.           */
        /*--------------------------------------------------------------------*/

        public static bool GIF_TryDecodeNextFrame(ByteReader rdrIo, GifDecodeState stateIo,
                                                  bool bFirstFrame, out bool bEndedOut,
                                                  out string sReason)
        {
            int  iTag;
            int  iLabel;
            int  iLeft;
            int  iTop;
            int  iWidth;
            int  iHeight;
            int  iFlags;
            int  iEntries;

            bEndedOut = false;
            sReason   = "";

            /* THE PREVIOUS FRAME'S DISPOSAL, CARRIED OUT NOW.  On the first   */
            /* frame there is nothing pending and this does nothing.           */
            GIF_ApplyPendingDisposal(stateIo);

            /* A FRAME INHERITS NOTHING FROM THE ONE BEFORE IT.  Both of        */
            /* these are set by a Graphic Control Extension, and a frame that   */
            /* carries none must get the defaults rather than whatever the      */
            /* last extension happened to say -- otherwise one frame's delay    */
            /* or disposal silently governs every frame after it.  The          */
            /* transparent index is deliberately NOT reset here: the            */
            /* specification makes it persist until an extension changes it,    */
            /* and GIF_TryParseGraphicControl already restores the palette      */
            /* entry it displaced.                                              */
            stateIo.GIFS_iDelayMs  = 0;
            stateIo.GIFS_iDisposal = GIF_DISPOSE_UNSPECIFIED;

            for (;;)
            {   /* One block per iteration until a frame or the trailer. */
                if (!rdrIo.BYTER_TryReadU8(out iTag))
                {   /* The file ended without a trailer.  Refused rather than   */
                    /* treated as an end, because a truncated GIF and a         */
                    /* complete one are worth telling apart.                    */
                    sReason = "GIF: the file ends without a trailer block";
                    return(false);
                }

                if (iTag == GIF_BLOCK_IMAGE)
                {   /* An image descriptor: this is the frame we came for. */
                    if (!rdrIo.BYTER_TryReadU16LE(out iLeft) ||
                        !rdrIo.BYTER_TryReadU16LE(out iTop) ||
                        !rdrIo.BYTER_TryReadU16LE(out iWidth) ||
                        !rdrIo.BYTER_TryReadU16LE(out iHeight) ||
                        !rdrIo.BYTER_TryReadU8(out iFlags))
                    {   /* Truncated inside the descriptor. */
                        sReason = "GIF: the file ends inside an image descriptor";
                        return(false);
                    }

                    stateIo.GIFS_iLocalFlags = iFlags;

                    if (!GIF_TrySetUpFrameWalk(stateIo, iLeft, iTop, iWidth, iHeight,
                                               out sReason))
                    {   /* The frame does not fit the logical screen. */
                        return(false);
                    }

                    if ((iFlags & GIF_FLAG_COLOR_TABLE) != 0)
                    {   /* A LOCAL TABLE OVERRIDES THE GLOBAL ONE for this     */
                        /* frame only, and it is read with the transparent     */
                        /* index applied directly, because the local palette   */
                        /* is rebuilt from scratch each time and carries no    */
                        /* history to undo.                                    */
                        iEntries = 2 << (iFlags & GIF_FLAG_TABLE_SIZE);

                        if (!GIF_TryParseColorTable(rdrIo, stateIo.GIFS_arrLocalPalette,
                                                    iEntries,
                                                    stateIo.GIFS_iTransparentIndex,
                                                    out sReason))
                        {   /* The local table is truncated. */
                            return(false);
                        }

                        stateIo.GIFS_arrColorTable = stateIo.GIFS_arrLocalPalette;
                    }
                    else if ((stateIo.GIFS_iFlags & GIF_FLAG_COLOR_TABLE) != 0)
                    {   /* No local table, so the global one applies. */
                        stateIo.GIFS_arrColorTable = stateIo.GIFS_arrGlobalPalette;
                    }
                    else
                    {   /* NEITHER TABLE EXISTS, which is legal to write and    */
                        /* impossible to render: the specification says the     */
                        /* decoder may use its own palette, and any choice we   */
                        /* made would be invented colour.  Refuse and say so.   */
                        sReason = "GIF: the frame has no local colour table and " +
                                  "the file has no global one";
                        return(false);
                    }

                    /* DISPOSAL 3 NEEDS ITS SNAPSHOT TAKEN BEFORE THE RASTER    */
                    /* RUNS, which is the only place it can be taken -- the     */
                    /* method means "put back what was here before this frame   */
                    /* drew", and once the frame has drawn, that is gone.       */
                    /* Only the frame's own rectangle is ever restored, but     */
                    /* the whole canvas is copied because a rectangle copy      */
                    /* would cost the same and read worse.                      */
                    if (stateIo.GIFS_iDisposal == GIF_DISPOSE_PREVIOUS)
                    {   /* This frame will be undone when the next arrives. */
                        Array.Copy(stateIo.GIFS_arrOut, stateIo.GIFS_arrRestore,
                                   stateIo.GIFS_arrOut.Length);
                    }

                    if (!GIF_TryDecodeLzwRaster(rdrIo, stateIo, out sReason))
                    {   /* The raster is corrupt or truncated. */
                        return(false);
                    }

                    if (bFirstFrame)
                    {   /* THE BACKGROUND FILL HAPPENS ONCE, EVER.  After the   */
                        /* first frame, undrawn pixels belong to whatever was   */
                        /* on the canvas already, which is the entire point     */
                        /* of an animation.                                     */
                        GIF_FillFirstFrameBackground(stateIo);
                    }

                    /* REMEMBER WHAT TO UNDO, AND WHERE.  The rectangle is     */
                    /* this frame's own bounds, which is what the              */
                    /* specification says disposal applies to -- "the area     */
                    /* used by the graphic".  stb uses the pixels the raster   */
                    /* actually visited instead; the two agree on any frame    */
                    /* whose raster fills its rectangle, which is every        */
                    /* well-formed one, and the specification is the better    */
                    /* authority where they part.                              */
                    stateIo.GIFS_iPendingDisposal = stateIo.GIFS_iDisposal;
                    stateIo.GIFS_iPendingX        = iLeft;
                    stateIo.GIFS_iPendingY        = iTop;
                    stateIo.GIFS_iPendingW        = iWidth;
                    stateIo.GIFS_iPendingH        = iHeight;

                    /* One frame is composited onto the canvas. */
                    return(true);
                }

                if (iTag == GIF_BLOCK_EXTENSION)
                {   /* An extension.  The next byte says which. */
                    if (!rdrIo.BYTER_TryReadU8(out iLabel))
                    {   /* Truncated at the extension label. */
                        sReason = "GIF: the file ends at an extension label";
                        return(false);
                    }

                    if (iLabel == GIF_EXT_GRAPHIC_CONTROL)
                    {   /* The one extension that changes pixels. */
                        if (!GIF_TryParseGraphicControl(rdrIo, stateIo, out sReason))
                        {   /* Truncated inside it. */
                            return(false);
                        }
                    }
                    else if (iLabel == GIF_EXT_APPLICATION)
                    {   /* Almost always NETSCAPE2.0, carrying the loop count. */
                        if (!GIF_TryParseApplicationExtension(rdrIo, stateIo, out sReason))
                        {   /* Truncated inside it. */
                            return(false);
                        }
                    }
                    else
                    {   /* Comment or plain text: neither affects a pixel, so   */
                        /* the payload is walked and discarded.                 */
                        if (!GIF_TrySkipSubBlockChain(rdrIo, 0, out sReason))
                        {   /* Truncated inside the payload. */
                            return(false);
                        }
                    }

                    continue;
                }

                if (iTag == GIF_BLOCK_TRAILER)
                {   /* The stream ended before any image descriptor.  Legal,   */
                    /* and the caller decides what to make of it.              */
                    bEndedOut = true;

                    /* Nothing was decoded, but nothing was wrong either. */
                    return(true);
                }

                /* AN UNKNOWN INTRODUCER IS FATAL, and it has to be: block   */
                /* lengths in a GIF are carried inside the blocks, so a      */
                /* reader that does not recognise one has no way to find     */
                /* where it ends and cannot skip it.  Guessing would         */
                /* desynchronise the whole rest of the file.                 */
                sReason = "GIF: 0x" + iTag.ToString("X2") +
                          " is not a known block introducer";
                return(false);
            }
        }

        /*--------------------------------------------------------------------*/
        /* GIF_ApplyPendingDisposal:                                          */
        /*                                                                    */
        /* Carries out the PREVIOUS frame's disposal method, over the         */
        /* previous frame's own rectangle.                                    */
        /*                                                                    */
        /* THIS IS DEVIATION 3, AND IT IS WRITTEN FROM THE SPECIFICATION      */
        /* RATHER THAN TRANSCRIBED.  stb's multi-frame driver keeps a pointer */
        /* to "the frame two back" and computes it as `out - 2 * stride`,     */
        /* which addresses memory BEFORE its own allocation -- it means the   */
        /* frame at `out + (layers - 2) * stride`.  Any GIF using disposal 3  */
        /* with three or more frames reads out of bounds there.  Verified     */
        /* against the clone's full history: HEAD is our pinned commit and no */
        /* later fix exists, and v1.35 cannot be compared because it has no   */
        /* multi-frame support at all.                                        */
        /*                                                                    */
        /* SO THE MODEL HERE IS THE SPECIFICATION'S, WHICH IS ALSO SIMPLER.   */
        /* "Restore to previous" does not mean "the frame from two back" at   */
        /* all -- it means the canvas as it stood BEFORE THE CURRENT FRAME    */
        /* DREW, which is a snapshot taken at that moment and needs no        */
        /* history of frames.  GIF_TryDecodeNextFrame takes it.               */
        /*                                                                    */
        /* "RESTORE TO BACKGROUND" CLEARS TO TRANSPARENT, and that is a       */
        /* deliberate reading of a place where the specification and every    */
        /* implementation disagree.  The specification says the area is       */
        /* restored to "the background color"; browsers, and GDI+, clear it   */
        /* to nothing.  MEASURED, not assumed: a real reader's decode of an   */
        /* animation using all four methods returns alpha 0 across a disposed */
        /* region, not an opaque background colour.  Following the literal    */
        /* wording would make this library disagree with every reader a       */
        /* consumer might compare it against, and would flash a solid colour  */
        /* through animations that are meant to be transparent.               */
        /*                                                                    */
        /* AN UNRECOGNISED METHOD LEAVES THE CANVAS ALONE.  Values 4 to 7 are */
        /* reserved and no file should use one, but "do nothing" is the only  */
        /* safe guess: it degrades to disposal 1, which is what a decoder     */
        /* that ignored disposal entirely would do, and cannot erase          */
        /* anything.                                                          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     stateIo  : Supplies the pending method and rectangle, and the  */
        /*                canvas to act on.  The pending state is cleared.    */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void     : The canvas is updated in place.                     */
        /*--------------------------------------------------------------------*/

        public static void GIF_ApplyPendingDisposal(GifDecodeState stateIo)
        {
            int iRowStart;
            int iCount;
            int x;
            int y;

            if (stateIo.GIFS_iPendingW <= 0 || stateIo.GIFS_iPendingH <= 0)
            {   /* Nothing pending: this is the first frame, or the previous   */
                /* one claimed no area.                                        */
                return;
            }

            if (stateIo.GIFS_iPendingDisposal != GIF_DISPOSE_BACKGROUND &&
                stateIo.GIFS_iPendingDisposal != GIF_DISPOSE_PREVIOUS)
            {   /* Methods 0 and 1 both mean "leave it", and so does anything   */
                /* reserved.  The frame stays on the canvas.                    */
                stateIo.GIFS_iPendingW = 0;
                stateIo.GIFS_iPendingH = 0;
                return;
            }

            for (y = 0; y < stateIo.GIFS_iPendingH; y++)
            {   /* One row of the previous frame's rectangle per iteration. */
                iRowStart = (((stateIo.GIFS_iPendingY + y) * stateIo.GIFS_iWidth) +
                             stateIo.GIFS_iPendingX) * GIF_CHANNELS;
                iCount    = stateIo.GIFS_iPendingW * GIF_CHANNELS;

                if (stateIo.GIFS_iPendingDisposal == GIF_DISPOSE_PREVIOUS)
                {   /* Put the row back exactly as the snapshot has it. */
                    Array.Copy(stateIo.GIFS_arrRestore, iRowStart,
                               stateIo.GIFS_arrOut, iRowStart, iCount);
                }
                else
                {   /* Clear the row to nothing.  Writing zeroes rather than    */
                    /* only zeroing alpha matters: a later frame may read       */
                    /* these pixels through its own transparent holes, and a    */
                    /* stale colour under a zero alpha would show through the   */
                    /* moment anything composited over it.                      */
                    for (x = 0; x < iCount; x++)
                    {   /* One sample per iteration. */
                        stateIo.GIFS_arrOut[iRowStart + x] = 0;
                    }
                }
            }

            /* Consumed.  Clearing the rectangle is what makes this idempotent   */
            /* if it were ever called twice.                                     */
            stateIo.GIFS_iPendingW = 0;
            stateIo.GIFS_iPendingH = 0;
        }

        /*--------------------------------------------------------------------*/
        /* GIF_TryParseApplicationExtension:                                  */
        /*                                                                    */
        /* Reads an application extension, and takes the loop count out of it */
        /* when it is NETSCAPE2.0.                                            */
        /*                                                                    */
        /* THE LOOP COUNT IS NOT IN THE SPECIFICATION AT ALL.  GIF89a defines */
        /* the application extension as an escape hatch and says nothing      */
        /* about looping; NETSCAPE2.0 is a convention from 1995 that every    */
        /* decoder implements because there is no other way for a file to say */
        /* how many times to play.  A GIF with no such block plays once.      */
        /*                                                                    */
        /* THE COUNT MEANS REPEATS AFTER THE FIRST PLAY, and ZERO MEANS       */
        /* FOREVER -- which is why GIFS_bHasLoopCount exists beside it.  A    */
        /* file with no extension and a file asking to loop endlessly both    */
        /* leave the count at zero, and they are not the same thing.          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     rdrIo    : Positioned at the extension's block size byte.      */
        /*     stateIo  : Receives the loop count when one is found.          */
        /*     sReason  : Receives "" or why the extension was refused.       */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool     : true when the extension was read or skipped         */
        /*                cleanly.                                            */
        /*--------------------------------------------------------------------*/

        public static bool GIF_TryParseApplicationExtension(ByteReader rdrIo,
                                                            GifDecodeState stateIo,
                                                            out string sReason)
        {
            byte[] arrId;
            string sId;
            int    iBlockSize;
            int    iSubSize;
            int    iKind;
            int    iLow;
            int    iHigh;
            int    i;

            sReason = "";

            if (!rdrIo.BYTER_TryReadU8(out iBlockSize))
            {   /* Truncated at the block size. */
                sReason = "GIF: the file ends at an application extension block size";
                return(false);
            }

            if (iBlockSize != GIF_APP_ID_LENGTH)
            {   /* Not the eleven-byte identifier the convention uses, so it   */
                /* cannot be one we know.  Skip its payload and carry on.      */
                return(GIF_TrySkipSubBlockChain(rdrIo, iBlockSize, out sReason));
            }

            if (!rdrIo.BYTER_TryReadBytes(GIF_APP_ID_LENGTH, out arrId))
            {   /* Truncated inside the identifier. */
                sReason = "GIF: the file ends inside an application identifier";
                return(false);
            }

            /* BUILT CHARACTER BY CHARACTER rather than through a text       */
            /* decoder, because the identifier is eleven raw bytes that a    */
            /* malformed file may fill with anything at all -- and running   */
            /* arbitrary bytes through an encoding is how a comparison       */
            /* becomes culture-sensitive without anyone noticing.            */
            sId = "";

            for (i = 0; i < GIF_APP_ID_LENGTH; i++)
            {   /* One byte per iteration. */
                sId = sId + ((char)arrId[i]).ToString();
            }

            if (!string.Equals(sId, GIF_APP_NETSCAPE, StringComparison.Ordinal))
            {   /* Some other application's data.  Skipping it is correct --   */
                /* the block exists precisely so unknown extensions can be     */
                /* ignored safely.                                             */
                return(GIF_TrySkipSubBlockChain(rdrIo, 0, out sReason));
            }

            /* NETSCAPE2.0's payload is one sub-block of three bytes: a kind   */
            /* byte of 1, then the count little-endian.                        */
            if (!rdrIo.BYTER_TryReadU8(out iSubSize))
            {   /* Truncated at the payload's length. */
                sReason = "GIF: the file ends inside a NETSCAPE extension";
                return(false);
            }

            if (iSubSize == 3)
            {   /* The shape the convention defines. */
                if (!rdrIo.BYTER_TryReadU8(out iKind) ||
                    !rdrIo.BYTER_TryReadU8(out iLow) ||
                    !rdrIo.BYTER_TryReadU8(out iHigh))
                {   /* Truncated inside the payload. */
                    sReason = "GIF: the file ends inside a NETSCAPE loop count";
                    return(false);
                }

                if (iKind == GIF_NETSCAPE_LOOP)
                {   /* A loop count, little-endian like every other multi-   */
                    /* byte field in the format.                             */
                    stateIo.GIFS_iLoopCount    = iLow | (iHigh << 8);
                    stateIo.GIFS_bHasLoopCount = true;
                }

                /* Whatever the kind byte said, the chain still has to be   */
                /* walked to its terminator.                                */
                return(GIF_TrySkipSubBlockChain(rdrIo, 0, out sReason));
            }

            /* An unexpected payload size: skip what it declared and then the   */
            /* rest of the chain.                                               */
            return(GIF_TrySkipSubBlockChain(rdrIo, iSubSize, out sReason));
        }

        /*--------------------------------------------------------------------*/
        /* GIF_TryBuildImage:                                                 */
        /*                                                                    */
        /* Wraps the finished canvas in an ImageBuffer.                       */
        /*                                                                    */
        /* THE RESULT IS ALWAYS FOUR CHANNELS, and that is a decision rather  */
        /* than an accident.  Every other codec here returns the channel      */
        /* count the FILE describes -- PNG's colour type, JPEG's component    */
        /* count.  GIF has no such field: it has a CANVAS that a frame need   */
        /* not fill, so pixels outside the frame belong to no palette entry   */
        /* at all and are transparent by definition.  Alpha is therefore      */
        /* intrinsic to GIF's model and not an optional extra, and a decoder  */
        /* returning three channels would have to invent a colour for those   */
        /* pixels.  stb reaches the same answer by the same route.            */
        /*                                                                    */
        /* A CALLER WANTING RGB USES IMG_TryConvertChannels, which exists for */
        /* exactly this and is what every other codec's callers already do.   */
        /*                                                                    */
        /* WHETHER THE CANVAS IS COPIED IS THE CALLER'S CHOICE, and both      */
        /* answers are right for their caller.  A single-frame decode hands   */
        /* the canvas over: it was allocated at exactly the right size and    */
        /* layout, the state object is discarded immediately afterwards, and  */
        /* copying would double the peak memory of every decode to protect a  */
        /* buffer with no other owner.                                        */
        /*                                                                    */
        /* AN ANIMATION MUST COPY, and getting that wrong is a bug with a     */
        /* very recognisable shape: every frame of the returned set would be  */
        /* the same array, so the whole animation would show the LAST frame   */
        /* N times over.  It would pass any check of frame count, geometry or */
        /* delay, and only a comparison of the pixels between two frames      */
        /* would notice.                                                      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     stateIn  : The finished decode state.                          */
        /*     bCopy    : true takes a private copy of the canvas; false      */
        /*                hands the live one over.                            */
        /*     imgOut   : Receives the image, or null on failure.             */
        /*     sReason  : Receives "" or why the buffer was refused.          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool     : true when a valid buffer was produced.              */
        /*--------------------------------------------------------------------*/

        public static bool GIF_TryBuildImage(GifDecodeState stateIn, bool bCopy,
                                             out ImageBuffer imgOut, out string sReason)
        {
            ImageBuffer img;
            byte[]      arrSamples;

            imgOut  = null;
            sReason = "";

            if (bCopy)
            {   /* An animation frame: the canvas keeps being drawn on, so   */
                /* this frame needs its own copy of it.                      */
                arrSamples = new byte[stateIn.GIFS_arrOut.Length];
                Array.Copy(stateIn.GIFS_arrOut, arrSamples, arrSamples.Length);
            }
            else
            {   /* A single-frame decode: nothing else will ever touch it. */
                arrSamples = stateIn.GIFS_arrOut;
            }

            img = new ImageBuffer();

            img.IMG_iWidth         = stateIn.GIFS_iWidth;
            img.IMG_iHeight        = stateIn.GIFS_iHeight;
            img.IMG_iChannels      = ImageBuffer.IMG_CHANNELS_RGBA;
            img.IMG_iBitsPerSample = ImageBuffer.IMG_BITS_8;
            img.IMG_arrSamples8    = arrSamples;
            img.IMG_arrSamples16   = null;

            if (!ImageBuffer.IMG_TryValidate(img, out sReason))
            {   /* The canvas and the declared geometry disagree, which would   */
                /* be a defect in this file rather than in the GIF.             */
                return(false);
            }

            imgOut = img;

            /* A complete RGBA image of the logical screen. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* GIF_TryDecode:                                                     */
        /*                                                                    */
        /* THE PUBLIC ENTRY POINT.  Decodes a GIF's first frame, composited   */
        /* onto the logical screen.                                           */
        /*                                                                    */
        /* AN ANIMATED GIF DECODES TO ITS FIRST FRAME HERE, which is what a   */
        /* caller that does not care about animation wants.                   */
        /* GIF_TryDecodeFrames is the call that returns all of them.          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     arrData  : Caller-owned buffer holding the file.  Not          */
        /*                modified.  Must not be null.                        */
        /*     iOffset  : Index of the file's first byte within arrData.      */
        /*     iLength  : Length of the file in bytes.                        */
        /*     imgOut   : Receives the decoded image, or null on failure.     */
        /*     sReason  : Receives "" on success, or a short human-readable   */
        /*                cause.  For a log line, never for a caller to       */
        /*                parse.                                              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool     : true when the first frame decoded.                  */
        /*--------------------------------------------------------------------*/

        public static bool GIF_TryDecode(byte[] arrData, int iOffset, int iLength,
                                         out ImageBuffer imgOut, out string sReason)
        {
            ByteReader     rdr;
            GifDecodeState state;
            GifHeader      hdr;
            bool           bEnded;

            imgOut  = null;
            sReason = "";

            if (arrData == null)
            {   /* A null buffer is a CALLER bug, not malformed image data,   */
                /* so it throws rather than returning a reason string.        */
                throw new ArgumentNullException("arrData");
            }

            if (!ImageDecoderDispatch.IMGDEC_TryValidateWindow(arrData, iOffset, iLength,
                                                               out sReason))
            {   /* The offset and length do not describe a region of the   */
                /* array; nothing can be read.                             */
                return(false);
            }

            rdr   = new ByteReader(arrData, iOffset, iLength);
            state = new GifDecodeState();

            if (!GIF_TryParseHeader(rdr, state, out hdr, out sReason))
            {   /* Not a GIF, or a GIF whose screen makes no sense. */
                return(false);
            }

            if (!GIF_TryAllocateCanvas(state, out sReason))
            {   /* The declared screen is too large to hold. */
                return(false);
            }

            if (!GIF_TryDecodeNextFrame(rdr, state, true, out bEnded, out sReason))
            {   /* Malformed somewhere between the header and the raster. */
                return(false);
            }

            if (bEnded)
            {   /* A structurally valid GIF that carries no image at all.     */
                /* There is nothing to return, and returning a blank canvas   */
                /* would be a picture we invented.                            */
                sReason = "GIF: the stream ended without any image descriptor";
                return(false);
            }

            /* Hand the canvas over as an image, uncopied -- nothing else   */
            /* will ever write to it.                                       */
            return(GIF_TryBuildImage(state, false, out imgOut, out sReason));
        }

        /*--------------------------------------------------------------------*/
        /* GIF_TryDecodeFrames:                                               */
        /*                                                                    */
        /* THE ANIMATION ENTRY POINT.                                         */
        /* Decodes every frame of a GIF, each one fully composited onto the   */
        /* logical screen, into an ImageFrameSet.                             */
        /*                                                                    */
        /* THE FRAMES ARE PRE-COMPOSITED AND THAT IS THE WHOLE VALUE OF THIS  */
        /* CALL.  A GIF frame on the wire is a PATCH: a rectangle somewhere   */
        /* on the canvas, possibly with holes, whose meaning depends on what  */
        /* the frames before it left behind and on their disposal methods.    */
        /* Handing a caller the raw patches would make them reimplement half  */
        /* a GIF decoder to display anything.  This library's contract is     */
        /* complete images of identical size, and that is what comes back.    */
        /*                                                                    */
        /* A STILL GIF IS A ONE-FRAME ANIMATION here, not an error.  Nothing  */
        /* in the format distinguishes them -- a still image is simply a      */
        /* stream with one image descriptor -- so a caller that always uses   */
        /* this entry point never has to ask which kind it has.               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     arrData  : Caller-owned buffer holding the file.  Not          */
        /*                modified.  Must not be null.                        */
        /*     iOffset  : Index of the file's first byte within arrData.      */
        /*     iLength  : Length of the file in bytes.                        */
        /*     frmOut   : Receives the frame set, or null on failure.         */
        /*     sReason  : Receives "" on success, or a short human-readable   */
        /*                cause.                                              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool     : true when at least one frame decoded.               */
        /*--------------------------------------------------------------------*/

        public static bool GIF_TryDecodeFrames(byte[] arrData, int iOffset, int iLength,
                                               out ImageFrameSet frmOut, out string sReason)
        {
            System.Collections.Generic.List<ImageBuffer> lstFrames;
            System.Collections.Generic.List<int>         lstDelays;
            ByteReader     rdr;
            GifDecodeState state;
            GifHeader      hdr;
            ImageFrameSet  frm;
            ImageBuffer    img;
            bool           bEnded;
            bool           bFirst;
            int            i;

            frmOut  = null;
            sReason = "";

            if (arrData == null)
            {   /* A null buffer is a CALLER bug, not malformed image data. */
                throw new ArgumentNullException("arrData");
            }

            if (!ImageDecoderDispatch.IMGDEC_TryValidateWindow(arrData, iOffset, iLength,
                                                               out sReason))
            {   /* The offset and length do not describe a region of the   */
                /* array; nothing can be read.                             */
                return(false);
            }

            rdr   = new ByteReader(arrData, iOffset, iLength);
            state = new GifDecodeState();

            if (!GIF_TryParseHeader(rdr, state, out hdr, out sReason))
            {   /* Not a GIF, or a GIF whose screen makes no sense. */
                return(false);
            }

            if (!GIF_TryAllocateCanvas(state, out sReason))
            {   /* The declared screen is too large to hold. */
                return(false);
            }

            lstFrames = new System.Collections.Generic.List<ImageBuffer>();
            lstDelays = new System.Collections.Generic.List<int>();
            bFirst    = true;

            for (;;)
            {   /* One frame per iteration until the trailer. */
                if (!GIF_TryDecodeNextFrame(rdr, state, bFirst, out bEnded, out sReason))
                {   /* Malformed somewhere in this frame. */
                    return(false);
                }

                if (bEnded)
                {   /* The trailer.  Every frame the file holds is decoded. */
                    break;
                }

                /* COPIED, because the canvas keeps being drawn on.  Handing   */
                /* the live array over would give every frame in the set the   */
                /* same pixels -- the last ones.                               */
                if (!GIF_TryBuildImage(state, true, out img, out sReason))
                {   /* The canvas and the declared geometry disagree. */
                    return(false);
                }

                lstFrames.Add(img);
                lstDelays.Add(state.GIFS_iDelayMs);

                bFirst = false;

                if (lstFrames.Count > ImageFrameSet.FRM_MAX_FRAMES)
                {   /* A DECOMPRESSION BOMB GUARD, and it is not theoretical:   */
                    /* a small file can declare an enormous canvas and then     */
                    /* repeat a tiny frame, so the cost is frames times         */
                    /* canvas rather than anything the file's own size          */
                    /* bounds.  ImageFrameSet already caps what it will         */
                    /* validate; refusing here stops the memory being spent     */
                    /* before that check is ever reached.                       */
                    sReason = "GIF: the stream declares more than " +
                              ImageFrameSet.FRM_MAX_FRAMES.ToString() + " frames";
                    return(false);
                }
            }

            if (lstFrames.Count < 1)
            {   /* A structurally valid GIF carrying no image at all. */
                sReason = "GIF: the stream ended without any image descriptor";
                return(false);
            }

            frm = new ImageFrameSet();

            frm.FRM_arrFrames   = lstFrames.ToArray();
            frm.FRM_arrDelaysMs = new int[lstDelays.Count];

            for (i = 0; i < lstDelays.Count; i++)
            {   /* Copied out one at a time rather than through ToArray, so   */
                /* the two arrays are visibly built to the same length.       */
                frm.FRM_arrDelaysMs[i] = lstDelays[i];
            }

            /* NO NETSCAPE EXTENSION MEANS PLAY ONCE, which is not the same   */
            /* as the loop count being zero -- zero means forever.  A file    */
            /* that says nothing gets 1, and only a file that asked for       */
            /* endless play gets FRM_LOOP_FOREVER.                            */
            if (state.GIFS_bHasLoopCount)
            {   /* The file said how many times. */
                frm.FRM_iLoopCount = state.GIFS_iLoopCount;
            }
            else
            {   /* No extension: the default is a single play. */
                frm.FRM_iLoopCount = 1;
            }

            if (!ImageFrameSet.FRM_TryValidate(frm, out sReason))
            {   /* The set is internally inconsistent, which would be a   */
                /* defect in this file rather than in the GIF.            */
                return(false);
            }

            frmOut = frm;

            /* Every frame, composited, in order, with its delay. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* GIF_DefaultEncodeOptions:                                          */
        /*                                                                    */
        /* The options a caller gets by passing null: the largest table the   */
        /* format allows, no dithering, endless looping for an animation, and */
        /* transparency decided from the image rather than by the caller.     */
        /*                                                                    */
        /* THE PALETTE DEFAULT IS 256 RATHER THAN SOMETHING SMALLER because   */
        /* it is the only value that never sends an image to the quantiser    */
        /* that could have been written exactly.  A caller who has not        */
        /* thought about palette size                                         */
        /* wants the picture, not the smallest file.                          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     GifEncodeOptions : A fresh instance, never shared, so a caller */
        /*                        may modify it without affecting anyone      */
        /*                        else's defaults.                            */
        /*--------------------------------------------------------------------*/

        public static GifEncodeOptions GIF_DefaultEncodeOptions()
        {
            GifEncodeOptions optNew;

            optNew = new GifEncodeOptions();

            optNew.GIFOPT_iPaletteSize  = GIF_PALETTE_ENTRIES;
            optNew.GIFOPT_bDither       = false;
            optNew.GIFOPT_iLoopCount    = GIFOPT_LOOP_FROM_FRAMES;
            optNew.GIFOPT_iTransparency = GIF_TRANSPARENCY_AUTO;

            /* Defaults that produce a plain, widely readable file. */
            return(optNew);
        }

        /*--------------------------------------------------------------------*/
        /* GIF_TryResolveTableSize:                                           */
        /*                                                                    */
        /* Turns the caller's requested palette size into the entry count the */
        /* colour table will actually carry, which the format constrains to a */
        /* power of two from 2 to 256.                                        */
        /*                                                                    */
        /* ROUNDING IS UPWARDS, WHICH IS THE ONLY SAFE DIRECTION.  The size   */
        /* is a cap on how many colours the image may hold, so rounding down  */
        /* would refuse images the caller's own number permits.  Rounding up  */
        /* costs three bytes per unused entry and refuses nothing.            */
        /*                                                                    */
        /* ZERO IS REFUSED RATHER THAN TREATED AS A DEFAULT.  The frozen      */
        /* surface documents the range as 2 to 256 and names no sentinel, so  */
        /* a default-constructed options object is a caller mistake worth     */
        /* saying out loud -- exactly as BMPOPT_iBitsPerPixel treats zero.    */
        /* Passing null, or calling GIF_DefaultEncodeOptions, is how a caller */
        /* asks for the defaults.                                             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iRequested : The caller's GIFOPT_iPaletteSize.                 */
        /*     iEntriesOut: Receives the power-of-two entry count to write.   */
        /*     sReason    : Receives "" or why the request was refused.       */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool       : true when the request names a writable table.     */
        /*--------------------------------------------------------------------*/

        public static bool GIF_TryResolveTableSize(int iRequested, out int iEntriesOut,
                                                   out string sReason)
        {
            int iEntries;

            iEntriesOut = 0;
            sReason     = "";

            if (iRequested < GIF_PALETTE_MINIMUM || iRequested > GIF_PALETTE_ENTRIES)
            {   /* Outside the frozen range, which includes the zero a   */
                /* default-constructed options object carries.           */
                sReason = "GIF encode: a palette size of " + iRequested.ToString() +
                          " is outside the permitted range " +
                          GIF_PALETTE_MINIMUM.ToString() + " to " +
                          GIF_PALETTE_ENTRIES.ToString() +
                          "; pass null options for the defaults";
                return(false);
            }

            iEntries = GIF_PALETTE_MINIMUM;

            while (iEntries < iRequested)
            {   /* Double until the table is at least as large as asked for.   */
                /* At most seven iterations, from 2 up to 256.                 */
                iEntries = iEntries * 2;
            }

            iEntriesOut = iEntries;

            /* A legal table size the three-bit size field can express. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* GIF_TryClassifyAlpha:                                              */
        /*                                                                    */
        /* Decides whether an RGBA image carries transparency GIF can express */
        /* and refuses the kind it cannot.                                    */
        /*                                                                    */
        /* GIF HAS EXACTLY ONE BIT OF TRANSPARENCY: a pixel either names the  */
        /* transparent index and is not drawn at all, or it names a colour    */
        /* and is fully opaque.  There is no partial alpha anywhere in the    */
        /* format.  So a source carrying an alpha of, say, 128 cannot be      */
        /* written without deciding on the caller's behalf whether that pixel */
        /* vanishes or becomes solid, and either answer changes the picture.  */
        /* THAT IS REFUSED, on the same line BMP draws when asked to drop an  */
        /* alpha channel and JPEG draws when handed one.                      */
        /*                                                                    */
        /* It is also what makes the zero-tolerance milestone reachable:      */
        /* an encoder that snapped partial alpha would round-trip to          */
        /* different pixels than it was given, and no exact comparison could  */
        /* survive it.                                                        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     imgIn         : Source, 8-bit RGBA.  Not modified.             */
        /*     bHasHolesOut  : Receives true when at least one pixel is fully */
        /*                     transparent.                                   */
        /*     sReason       : Receives "" or which pixel was refused.        */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool          : true when every pixel is fully opaque or fully */
        /*                     transparent.                                   */
        /*--------------------------------------------------------------------*/

        public static bool GIF_TryClassifyAlpha(ImageBuffer imgIn, out bool bHasHolesOut,
                                                out string sReason)
        {
            int iPixels;
            int iAlpha;
            int i;

            bHasHolesOut = false;
            sReason      = "";

            iPixels = imgIn.IMG_iWidth * imgIn.IMG_iHeight;

            for (i = 0; i < iPixels; i++)
            {   /* One pixel per iteration, reading only its alpha sample. */
                iAlpha = (int)imgIn.IMG_arrSamples8[(i * GIF_CHANNELS) + 3];

                if (iAlpha == 0)
                {   /* Fully transparent: this image needs an index reserved. */
                    bHasHolesOut = true;
                }
                else if (iAlpha != ImageBuffer.IMG_OPAQUE_8)
                {   /* Partial alpha, which the format cannot carry.  The     */
                    /* pixel is named so the caller can find it rather than   */
                    /* having to search an image for the one bad sample.      */
                    sReason = "GIF encode: pixel (" +
                              (i % imgIn.IMG_iWidth).ToString() + ", " +
                              (i / imgIn.IMG_iWidth).ToString() + ") has alpha " +
                              iAlpha.ToString() +
                              ", and GIF carries only fully opaque or fully " +
                              "transparent pixels";
                    return(false);
                }
            }

            /* Every pixel is one GIF can represent exactly. */
            return(true);
        }

        /*---------------------------------------------------------------------*/
        /* GIF_TryResolveTransparency:                                         */
        /*                                                                     */
        /* Applies the GIFOPT_iTransparency tri-state to what the image        */
        /* actually contains, and says whether a table entry is reserved.      */
        /*                                                                     */
        /* THE ONE CASE WORTH READING TWICE IS "NONE" AGAINST AN IMAGE WITH    */
        /* HOLES, and it is a refusal rather than an override.  The caller     */
        /* has asked for something that cannot be done without inventing a     */
        /* colour for pixels that have none; saying no is the only answer      */
        /* that does not silently change their picture.  AUTO exists so that   */
        /* a caller who has not thought about any of this never meets that     */
        /* refusal, because AUTO asks the image instead of the caller.         */
        /*                                                                     */
        /* Arguments:                                                          */
        /*     iRequested   : The caller's GIFOPT_iTransparency.               */
        /*     bHasHoles    : Whether the source has fully transparent pixels, */
        /*                    as GIF_TryClassifyAlpha determined.              */
        /*     bReserveOut  : Receives true when index 0 is to be reserved as  */
        /*                    the transparent index.                           */
        /*     sReason      : Receives "" or why the request was refused.      */
        /*                                                                     */
        /* Returns:                                                            */
        /*     bool         : true when the request and the image agree.       */
        /*---------------------------------------------------------------------*/

        public static bool GIF_TryResolveTransparency(int iRequested, bool bHasHoles,
                                                      out bool bReserveOut,
                                                      out string sReason)
        {
            bReserveOut = false;
            sReason     = "";

            if (iRequested == GIF_TRANSPARENCY_AUTO)
            {   /* Ask the image.  A source with holes needs an index; one   */
                /* without one gains nothing from spending an entry on it.   */
                bReserveOut = bHasHoles;

                /* The default, and the answer that is right without being   */
                /* asked for.                                                */
                return(true);
            }

            if (iRequested == GIF_TRANSPARENCY_NONE)
            {   /* The caller wants every entry to be a colour. */
                if (bHasHoles)
                {   /* It cannot be done without turning transparent pixels   */
                    /* into an opaque colour we would have to invent.         */
                    sReason = "GIF encode: transparency was disabled but the image has " +
                              "fully transparent pixels, which would have to become an " +
                              "opaque colour; use GIF_TRANSPARENCY_AUTO";
                    return(false);
                }

                /* An opaque image, so nothing is lost by declaring nothing. */
                return(true);
            }

            if (iRequested == GIF_TRANSPARENCY_RESERVE)
            {   /* Always reserve, even for an image with no holes at all --   */
                /* which is what GDI+ itself writes, declaring an index        */
                /* transparent in every file and then never using it.          */
                bReserveOut = true;

                /* Reserved on the caller's say-so rather than the image's. */
                return(true);
            }

            /* Not one of the three constants the frozen surface names. */
            sReason = "GIF encode: transparency mode " + iRequested.ToString() +
                      " is not one of GIF_TRANSPARENCY_AUTO, _NONE or _RESERVE";
            return(false);
        }

        /*--------------------------------------------------------------------*/
        /* GIF_TryBuildExactPalette:                                          */
        /*                                                                    */
        /* Collects the distinct colours of an RGBA image into a colour table */
        /* and produces the per-pixel index array that names them.            */
        /*                                                                    */
        /* EXACT, NEVER QUANTISED.  Every pixel gets an entry holding its own */
        /* colour, so the encoding is LOSSLESS and an image with more colours */
        /* than the table can hold is REFUSED rather than reduced.  That is   */
        /* the same line BMP and PNG draw at their own palettised outputs,    */
        /* and all three defer the reduction to the GIF quantiser, where      */
        /* GIFOPT_iPaletteSize and GIFOPT_bDither control it.                 */
        /*                                                                    */
        /* IT IS ALSO WHAT MAKES THIS SUB-STEP'S ORACLE EXACT.  With an       */
        /* exact palette there is no error to tolerate anywhere, so both the  */
        /* round trip through our own decoder and GDI+'s reading of the file  */
        /* are checked at tolerance ZERO -- the only encode milestone in this */
        /* library that has ever been able to say that.                       */
        /*                                                                    */
        /* COLOURS ARE NUMBERED IN THE ORDER THEY ARE FIRST ENCOUNTERED,      */
        /* scanning top-left to bottom-right, which is arbitrary but          */
        /* deterministic: the same image always produces the same file.  That */
        /* matters more than it sounds, because a byte-identity check against */
        /* our own output is only meaningful if the output is stable.         */
        /*                                                                    */
        /* A FULLY TRANSPARENT PIXEL'S COLOUR IS IGNORED, and it must be.     */
        /* Such a pixel is never drawn, so its RGB carries no information --  */
        /* but a source can easily hold hundreds of DIFFERENT fully           */
        /* transparent RGB values (a PNG's fully transparent regions often    */
        /* do), and treating each as a distinct colour would exhaust the      */
        /* table on pixels nobody will ever see.                              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     imgIn          : Source, 8-bit RGBA, already checked by        */
        /*                      GIF_TryClassifyAlpha.  Not modified.          */
        /*     iTableEntries  : How many entries the table will carry.        */
        /*     bReserve       : true when index 0 is the transparent index    */
        /*                      and colours therefore start at 1.             */
        /*     arrTableOut    : Receives iTableEntries * 3 bytes, R, G and B  */
        /*                      per entry, ready to write.  Unused entries    */
        /*                      are left zero.                                */
        /*     iColourCountOut: Receives how many distinct colours were used. */
        /*     arrIndicesOut  : Receives one table index per pixel.           */
        /*     sReason        : Receives "" or why the image was refused.     */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool           : true when the image fits the table.           */
        /*--------------------------------------------------------------------*/

        public static bool GIF_TryBuildExactPalette(ImageBuffer imgIn, int iTableEntries,
                                                    bool bReserve, out byte[] arrTableOut,
                                                    out int iColourCountOut,
                                                    out byte[] arrIndicesOut,
                                                    out string sReason)
        {
            Dictionary<int, int> dictSeen;
            byte[]               arrTable;
            byte[]               arrIndices;
            int                  iPixels;
            int                  iUsable;
            int                  iFirst;
            int                  iCount;
            int                  iKey;
            int                  iIndex;
            int                  iSrc;
            int                  i;

            arrTableOut     = null;
            iColourCountOut = 0;
            arrIndicesOut   = null;
            sReason         = "";

            iPixels    = imgIn.IMG_iWidth * imgIn.IMG_iHeight;
            arrTable   = new byte[iTableEntries * GIF_RGB_STRIDE];
            arrIndices = new byte[iPixels];
            dictSeen   = new Dictionary<int, int>();
            iCount     = 0;

            if (bReserve)
            {   /* Entry 0 belongs to transparency, so real colours begin at   */
                /* 1 and one fewer of them will fit.                           */
                iFirst  = 1;
                iUsable = iTableEntries - 1;
            }
            else
            {   /* Every entry is a colour. */
                iFirst  = 0;
                iUsable = iTableEntries;
            }

            for (i = 0; i < iPixels; i++)
            {   /* One pixel per iteration, in reading order. */
                iSrc = i * GIF_CHANNELS;

                if (bReserve && imgIn.IMG_arrSamples8[iSrc + 3] == 0)
                {   /* Fully transparent: it names the reserved index and its   */
                    /* colour plays no part, which is what stops a hundred      */
                    /* invisible RGB values eating the table.                   */
                    arrIndices[i] = 0;
                    continue;
                }

                /* Pack the three channels into one int so the dictionary has   */
                /* a cheap key.  Alpha is deliberately absent: it is known to   */
                /* be fully opaque here, every other case having been either    */
                /* handled above or refused by GIF_TryClassifyAlpha.            */
                iKey = ((int)imgIn.IMG_arrSamples8[iSrc] << 16) |
                       ((int)imgIn.IMG_arrSamples8[iSrc + 1] << 8) |
                        (int)imgIn.IMG_arrSamples8[iSrc + 2];

                if (!dictSeen.TryGetValue(iKey, out iIndex))
                {   /* A colour not seen before; give it the next slot. */
                    if (iCount >= iUsable)
                    {   /* Out of table entries.  This is NOT an error for a   */
                        /* caller of GIF_TryEncode -- it is the signal that    */
                        /* sends it to the quantiser instead, and the only     */
                        /* way this function can fail.  The text used to say   */
                        /* quantisation was not implemented yet, which         */
                        /* stopped being true.                                 */
                        sReason = "GIF encode: the image has more than " +
                                  iUsable.ToString() +
                                  " distinct colours, which a " +
                                  iTableEntries.ToString() +
                                  "-entry palette cannot hold exactly; it needs " +
                                  "quantising";
                        return(false);
                    }

                    iIndex = iFirst + iCount;
                    dictSeen.Add(iKey, iIndex);

                    arrTable[(iIndex * GIF_RGB_STRIDE)]     = imgIn.IMG_arrSamples8[iSrc];
                    arrTable[(iIndex * GIF_RGB_STRIDE) + 1] = imgIn.IMG_arrSamples8[iSrc + 1];
                    arrTable[(iIndex * GIF_RGB_STRIDE) + 2] = imgIn.IMG_arrSamples8[iSrc + 2];

                    iCount = iCount + 1;
                }

                arrIndices[i] = (byte)iIndex;
            }

            arrTableOut     = arrTable;
            iColourCountOut = iCount;
            arrIndicesOut   = arrIndices;

            /* Every pixel now names an entry holding its exact colour. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* GIF_EncodeInitLzw:                                                 */
        /*                                                                    */
        /* Prepares a GifEncodeState to write one raster: the bit             */
        /* accumulator, the sub-block buffer and an empty dictionary.         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     stateIo      : Receives the initialised state.                 */
        /*     bwOut        : Where finished sub-blocks are written.  Held by */
        /*                    reference, not owned.                           */
        /*     iMinCodeSize : The LZW minimum code size for this raster.      */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void         : The state is the output.                        */
        /*--------------------------------------------------------------------*/

        public static void GIF_EncodeInitLzw(GifEncodeState stateIo, ByteWriter bwOut,
                                             int iMinCodeSize)
        {
            stateIo.GIFE_bwOut        = bwOut;
            stateIo.GIFE_arrPending   = new byte[GIF_SUB_BLOCK_MAX];
            stateIo.GIFE_iPendingLen  = 0;
            stateIo.GIFE_iBits        = 0;
            stateIo.GIFE_iValidBits   = 0;
            stateIo.GIFE_iMinCodeSize = iMinCodeSize;
            stateIo.GIFE_iClear       = 1 << iMinCodeSize;
            stateIo.GIFE_iEnd         = stateIo.GIFE_iClear + 1;
            stateIo.GIFE_dictStrings  = new Dictionary<int, int>();

            GIF_ResetLzwDictionary(stateIo);
        }

        /*--------------------------------------------------------------------*/
        /* GIF_ResetLzwDictionary:                                            */
        /*                                                                    */
        /* Returns the dictionary and the code width to their post-clear      */
        /* state, which is exactly what the decoder does when it reads a      */
        /* clear code.                                                        */
        /*                                                                    */
        /* THE FIRST LEARNABLE SLOT IS clear + 2 BECAUSE THE TWO CODES ABOVE  */
        /* THE ALPHABET ARE RESERVED -- the clear code itself and the end-of- */
        /* information code.  GIF_TryDecodeLzwRaster seeds its own avail to   */
        /* the same value for the same reason, and the two must agree or      */
        /* every code after the first learned string is off by one.           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     stateIo : The encoder state to reset.  Its clear and end       */
        /*               codes and its minimum code size are left alone.      */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void    : The state is the output.                             */
        /*--------------------------------------------------------------------*/

        public static void GIF_ResetLzwDictionary(GifEncodeState stateIo)
        {
            stateIo.GIFE_iCodeSize = stateIo.GIFE_iMinCodeSize + 1;
            stateIo.GIFE_iCodeMask = (1 << stateIo.GIFE_iCodeSize) - 1;
            stateIo.GIFE_iNext     = stateIo.GIFE_iClear + 2;

            stateIo.GIFE_dictStrings.Clear();
        }

        /*--------------------------------------------------------------------*/
        /* GIF_FlushLzwSubBlock:                                              */
        /*                                                                    */
        /* Writes whatever bytes are pending as one length-prefixed           */
        /* sub-block, and empties the buffer.  Writes nothing at all when     */
        /* nothing is pending, which matters: a zero-length sub-block is the  */
        /* chain TERMINATOR, so emitting one here would end the raster early. */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     stateIo : Encoder state whose pending buffer is written out.   */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void    : Bytes go to the state's ByteWriter.                  */
        /*--------------------------------------------------------------------*/

        public static void GIF_FlushLzwSubBlock(GifEncodeState stateIo)
        {
            if (stateIo.GIFE_iPendingLen < 1)
            {   /* Nothing buffered.  Returning early is not an optimisation   */
                /* here -- writing a zero length would terminate the chain.    */
                return;
            }

            stateIo.GIFE_bwOut.BYTEW_WriteU8(stateIo.GIFE_iPendingLen);
            stateIo.GIFE_bwOut.BYTEW_WriteBytes(stateIo.GIFE_arrPending, 0,
                                                stateIo.GIFE_iPendingLen);

            stateIo.GIFE_iPendingLen = 0;
        }

        /*--------------------------------------------------------------------*/
        /* GIF_PushLzwByte:                                                   */
        /*                                                                    */
        /* Adds one finished byte to the pending sub-block, flushing it when  */
        /* it reaches the 255 bytes a length prefix can describe.             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     stateIo : Encoder state holding the pending buffer.            */
        /*     iByte   : The byte to append; only its low eight bits are      */
        /*               used.                                                */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void    : The state and its ByteWriter are the output.         */
        /*--------------------------------------------------------------------*/

        public static void GIF_PushLzwByte(GifEncodeState stateIo, int iByte)
        {
            stateIo.GIFE_arrPending[stateIo.GIFE_iPendingLen] = (byte)(iByte & 0xFF);
            stateIo.GIFE_iPendingLen = stateIo.GIFE_iPendingLen + 1;

            if (stateIo.GIFE_iPendingLen >= GIF_SUB_BLOCK_MAX)
            {   /* Full: a length prefix cannot describe more than this. */
                GIF_FlushLzwSubBlock(stateIo);
            }
        }

        /*---------------------------------------------------------------------*/
        /* GIF_EmitLzwCode:                                                    */
        /*                                                                     */
        /* Writes one LZW code at the current width into the bit stream.       */
        /*                                                                     */
        /* LEAST SIGNIFICANT BIT FIRST, which is the mirror of what            */
        /* GIF_TryDecodeLzwRaster does when it reads: the code goes ABOVE the  */
        /* bits already buffered and whole bytes come off the BOTTOM.  Filling */
        /* the accumulator the other way round produces a stream that is a     */
        /* plausible GIF and decodes to noise, which is the failure mode this  */
        /* pairing exists to avoid.                                            */
        /*                                                                     */
        /* THE ACCUMULATOR CANNOT OVERFLOW an int: it holds under eight live   */
        /* bits between codes, and the widest code is twelve, so nineteen      */
        /* bits is the most that is ever in flight.                            */
        /*                                                                     */
        /* Arguments:                                                          */
        /*     stateIo : Encoder state carrying the accumulator and width.     */
        /*     iCode   : The code to write, which must fit the current width.  */
        /*                                                                     */
        /* Returns:                                                            */
        /*     void    : Bytes reach the ByteWriter through the pending        */
        /*               sub-block.                                            */
        /*---------------------------------------------------------------------*/

        public static void GIF_EmitLzwCode(GifEncodeState stateIo, int iCode)
        {
            stateIo.GIFE_iBits = stateIo.GIFE_iBits |
                                 (iCode << stateIo.GIFE_iValidBits);
            stateIo.GIFE_iValidBits = stateIo.GIFE_iValidBits + stateIo.GIFE_iCodeSize;

            while (stateIo.GIFE_iValidBits >= 8)
            {   /* A whole byte is ready at the bottom of the accumulator. */
                GIF_PushLzwByte(stateIo, stateIo.GIFE_iBits & 0xFF);

                stateIo.GIFE_iBits      = stateIo.GIFE_iBits >> 8;
                stateIo.GIFE_iValidBits = stateIo.GIFE_iValidBits - 8;
            }
        }

        /*--------------------------------------------------------------------*/
        /* GIF_FinishLzwRaster:                                               */
        /*                                                                    */
        /* Closes a raster: pads the last partial byte, writes the final      */
        /* sub-block and terminates the chain.                                */
        /*                                                                    */
        /* THE PADDING BITS ARE ZERO AND ARE NEVER READ.  The end-of-         */
        /* information code has already been emitted, so a decoder stops      */
        /* before it reaches them -- but the byte still has to be written,    */
        /* because a sub-block carries whole bytes and the bits below the pad */
        /* are real.                                                          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     stateIo : Encoder state to close out.                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void    : Bytes go to the state's ByteWriter.                  */
        /*--------------------------------------------------------------------*/

        public static void GIF_FinishLzwRaster(GifEncodeState stateIo)
        {
            if (stateIo.GIFE_iValidBits > 0)
            {   /* A partial byte: write it, zero-padded at the top. */
                GIF_PushLzwByte(stateIo, stateIo.GIFE_iBits & 0xFF);

                stateIo.GIFE_iBits      = 0;
                stateIo.GIFE_iValidBits = 0;
            }

            GIF_FlushLzwSubBlock(stateIo);

            stateIo.GIFE_bwOut.BYTEW_WriteU8(GIF_BLOCK_TERMINATOR);
        }

        /*--------------------------------------------------------------------*/
        /* GIF_TryEncodeLzwRaster:                                            */
        /*                                                                    */
        /* Writes a whole raster: the minimum code size byte, the LZW code    */
        /* stream packed into sub-blocks, and the chain terminator.           */
        /*                                                                    */
        /* PUBLIC FOR TESTABILITY.  It is fed index arrays built by hand,     */
        /* checking the bytes that come out, and those bytes are fed          */
        /* bytes straight back through GIF_TryDecodeLzwRaster.  Neither is    */
        /* reachable through GIF_TryEncode.                                   */
        /*                                                                    */
        /* THIS IS THE DECODER RUN BACKWARDS AND THE TWO MUST AGREE ON THREE  */
        /* THINGS OR THE STREAM DESYNCHRONISES SILENTLY -- producing a legal  */
        /* GIF of a different picture rather than an error.  They are worth   */
        /* naming, because each is an off-by-one waiting to happen:           */
        /*                                                                    */
        /*   1. THE FIRST LEARNABLE SLOT IS clear + 2, the two codes above    */
        /*      the alphabet being reserved.  GIF_ResetLzwDictionary and the  */
        /*      decoder's avail agree on this.                                */
        /*                                                                    */
        /*   2. THE ENCODER'S DICTIONARY RUNS EXACTLY ONE ENTRY AHEAD of the  */
        /*      decoder's, and that is correct rather than a discrepancy to   */
        /*      fix.  The encoder learns a string at the moment it emits the  */
        /*      code for that string's prefix; the decoder cannot learn it    */
        /*      until the NEXT code arrives, because only then does it know   */
        /*      the character that extends it.  The gap is what makes the     */
        /*      self-referential case exist at all -- the decoder's           */
        /*      "iCode == iEntry" branch is the encoder referring to a string */
        /*      one step before the decoder would otherwise have it.          */
        /*                                                                    */
        /*   3. THE CODE WIDTH GROWS AT THE SAME EMITTED CODE ON BOTH SIDES,  */
        /*      and because of point 2 the two tests LOOK different.  The     */
        /*      decoder grows once the entry it just defined is the last the  */
        /*      current width can name.  The encoder is one ahead, so it      */
        /*      grows when the entry it just ASSIGNED is one PAST that -- the */
        /*      comparison against GIFE_iCodeMask + 1 below.  Writing the     */
        /*      decoder's test here instead would widen every code one step   */
        /*      early and the picture would dissolve partway down.            */
        /*                                                                    */
        /* WHEN THE DICTIONARY FILLS, A CLEAR CODE RESETS IT rather than the  */
        /* encoder simply stopping learning.  Both are legal and real         */
        /* encoders do both; clearing is chosen because it keeps compression  */
        /* working on images larger than 4096 distinct strings, which is most */
        /* photographs, and because it exercises the decoder's own clear path */
        /* on our own output.                                                 */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     bwOut        : Destination.  Advanced past the whole raster.   */
        /*     arrIndices   : One palette index per pixel, in reading order.  */
        /*     iMinCodeSize : The LZW minimum code size, 2 to 8 here.         */
        /*     sReason      : Receives "" or why the raster was refused.      */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool         : true when the raster was written.               */
        /*--------------------------------------------------------------------*/

        public static bool GIF_TryEncodeLzwRaster(ByteWriter bwOut, byte[] arrIndices,
                                                  int iMinCodeSize, out string sReason)
        {
            GifEncodeState state;
            int            iPrefix;
            int            iSuffix;
            int            iKey;
            int            iFound;
            int            i;

            sReason = "";

            if (bwOut == null)
            {   /* A null writer is a CALLER bug, not malformed image data. */
                throw new ArgumentNullException("bwOut");
            }

            if (arrIndices == null)
            {   /* Likewise -- the caller owns the index array. */
                throw new ArgumentNullException("arrIndices");
            }

            if (arrIndices.Length < 1)
            {   /* A frame with no pixels has no raster, and the loop below   */
                /* would read arrIndices[0].                                  */
                sReason = "GIF encode: a raster must carry at least one pixel";
                return(false);
            }

            if (iMinCodeSize < GIF_ENCODE_MIN_WIDTH || iMinCodeSize > GIF_ENCODE_MAX_WIDTH)
            {   /* Two is the format's floor even for a two-colour table, and   */
                /* eight is all a 256-entry table can need.                     */
                sReason = "GIF encode: an LZW minimum code size of " +
                          iMinCodeSize.ToString() + " is outside the range " +
                          GIF_ENCODE_MIN_WIDTH.ToString() + " to " +
                          GIF_ENCODE_MAX_WIDTH.ToString();
                return(false);
            }

            state = new GifEncodeState();

            GIF_EncodeInitLzw(state, bwOut, iMinCodeSize);

            /* The minimum code size is a plain byte ahead of the sub-block     */
            /* chain, not part of it, and it is written here so that a caller   */
            /* gets a complete raster from one call.                            */
            bwOut.BYTEW_WriteU8(iMinCodeSize);

            /* EVERY RASTER OPENS WITH A CLEAR CODE.  The decoder refuses a   */
            /* stream that carries data before one, so this is required       */
            /* rather than conventional.                                      */
            GIF_EmitLzwCode(state, state.GIFE_iClear);

            iPrefix = (int)arrIndices[0];

            for (i = 1; i < arrIndices.Length; i++)
            {   /* One pixel per iteration, extending the current string for   */
                /* as long as the dictionary already knows the result.         */
                iSuffix = (int)arrIndices[i];
                iKey    = (iPrefix << 8) | iSuffix;

                if (state.GIFE_dictStrings.TryGetValue(iKey, out iFound))
                {   /* The longer string is already known: say nothing yet   */
                    /* and keep growing it, which is the whole of LZW's      */
                    /* compression.                                          */
                    iPrefix = iFound;
                    continue;
                }

                /* The string ends here.  Emit the code for what we had, then   */
                /* learn the one-character-longer string for next time.         */
                GIF_EmitLzwCode(state, iPrefix);

                if (state.GIFE_iNext > GIF_LZW_CODE_LIMIT)
                {   /* The dictionary is full at twelve bits.  Tell the         */
                    /* decoder to forget everything and start again, which is   */
                    /* the only way to keep learning.                           */
                    GIF_EmitLzwCode(state, state.GIFE_iClear);
                    GIF_ResetLzwDictionary(state);
                }
                else
                {   /* Room for one more string. */
                    state.GIFE_dictStrings.Add(iKey, state.GIFE_iNext);

                    /* POINT 3 OF THE HEADER: one PAST the mask, not the mask   */
                    /* itself, because this dictionary runs one entry ahead     */
                    /* of the decoder's.  The width cannot pass twelve from     */
                    /* here -- at twelve the mask is 4095, so this would need   */
                    /* an entry numbered 4096, and the branch above has         */
                    /* already cleared the dictionary by then.                  */
                    if (state.GIFE_iNext == (state.GIFE_iCodeMask + 1))
                    {   /* One more bit per code from here on. */
                        state.GIFE_iCodeSize = state.GIFE_iCodeSize + 1;
                        state.GIFE_iCodeMask = (1 << state.GIFE_iCodeSize) - 1;
                    }

                    state.GIFE_iNext = state.GIFE_iNext + 1;
                }

                iPrefix = iSuffix;
            }

            /* The last string never met a character that ended it, so it is   */
            /* still unemitted.  Dropping this loses the tail of the image.    */
            GIF_EmitLzwCode(state, iPrefix);

            GIF_EmitLzwCode(state, state.GIFE_iEnd);
            GIF_FinishLzwRaster(state);

            /* A complete raster: size byte, sub-block chain, terminator. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* GIF_WriteScreenDescriptor:                                         */
        /*                                                                    */
        /* Writes the signature and the logical screen descriptor, followed   */
        /* by the global colour table.                                        */
        /*                                                                    */
        /* GIF89a IS WRITTEN UNCONDITIONALLY, and the alternative was         */
        /* considered rather than overlooked.  Pillow writes 87a for a plain  */
        /* still and switches to 89a only when transparency or animation      */
        /* appears, which this encoder could mirror.  It does not, for two    */
        /* reasons: GDI+ -- the most conservative reader in play and this     */
        /* work's oracle -- writes 89a for everything; and making the         */
        /* declared revision depend on the PIXELS means a rarely-taken branch */
        /* decides a header field, which is how a file that declares 87a      */
        /* while carrying an 89a-only block eventually gets written.  The     */
        /* revision is a statement about what the file may contain, and this  */
        /* encoder may contain a Graphic Control Extension.                   */
        /*                                                                    */
        /* THE BACKGROUND INDEX IS ZERO, matching GDI+ and Pillow.  It names  */
        /* the colour shown where the first frame draws nothing, and a still  */
        /* image written here always covers the whole screen, so nothing can  */
        /* observe it.                                                        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     bwOut         : Destination.  Advanced past the table.         */
        /*     iWidth        : Logical screen width in pixels.                */
        /*     iHeight       : Logical screen height in pixels.               */
        /*     iTableEntries : Entries in the global colour table; a power of */
        /*                     two from 2 to 256.                             */
        /*     arrTableIn    : iTableEntries * 3 bytes, R, G and B per entry. */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void          : Bytes go to bwOut.                             */
        /*--------------------------------------------------------------------*/

        public static void GIF_WriteScreenDescriptor(ByteWriter bwOut, int iWidth,
                                                     int iHeight, int iTableEntries,
                                                     byte[] arrTableIn)
        {
            int iExponent;
            int iPacked;

            bwOut.BYTEW_WriteU8((int)'G');
            bwOut.BYTEW_WriteU8((int)'I');
            bwOut.BYTEW_WriteU8((int)'F');
            bwOut.BYTEW_WriteU8((int)'8');
            bwOut.BYTEW_WriteU8((int)'9');
            bwOut.BYTEW_WriteU8((int)'a');

            /* EVERY MULTI-BYTE FIELD IN A GIF IS LITTLE-ENDIAN, which makes   */
            /* it the only format in this library that is.  Writing these      */
            /* big-endian would produce a plausible wrong size rather than a   */
            /* failure, so the LE form is deliberate at every call.            */
            bwOut.BYTEW_WriteU16LE(iWidth);
            bwOut.BYTEW_WriteU16LE(iHeight);

            /* The size field is an EXPONENT: the table holds 2 << n entries,   */
            /* so n counts the doublings above two.                             */
            iExponent = 0;

            while ((GIF_PALETTE_MINIMUM << iExponent) < iTableEntries)
            {   /* At most seven doublings, from 2 up to 256. */
                iExponent = iExponent + 1;
            }

            iPacked = GIF_FLAG_COLOR_TABLE | GIF_COLOR_RESOLUTION | iExponent;

            bwOut.BYTEW_WriteU8(iPacked);
            bwOut.BYTEW_WriteU8(0);                      // background colour index
            bwOut.BYTEW_WriteU8(0);                      // pixel aspect ratio: not specified

            bwOut.BYTEW_WriteBytes(arrTableIn, 0, iTableEntries * GIF_RGB_STRIDE);
        }

        /*--------------------------------------------------------------------*/
        /* GIF_WriteGraphicControl:                                           */
        /*                                                                    */
        /* Writes the Graphic Control Extension that declares which palette   */
        /* index is transparent.                                              */
        /*                                                                    */
        /* WITHOUT THIS BLOCK A RESERVED INDEX IS JUST A COLOUR.  Reserving   */
        /* an entry and failing to declare it would produce a file whose      */
        /* transparent pixels came back as whatever RGB the unused entry      */
        /* happened to hold -- black, here -- which is exactly the silent     */
        /* wrong picture the refusals elsewhere in this encoder exist to      */
        /* prevent.  The two always travel together.                          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     bwOut             : Destination.  Advanced past the block.     */
        /*     iTransparentIndex : The palette index that is not drawn.       */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void              : Bytes go to bwOut.                         */
        /*--------------------------------------------------------------------*/

        public static void GIF_WriteGraphicControl(ByteWriter bwOut, int iTransparentIndex)
        {
            bwOut.BYTEW_WriteU8(GIF_BLOCK_EXTENSION);
            bwOut.BYTEW_WriteU8(GIF_EXT_GRAPHIC_CONTROL);
            bwOut.BYTEW_WriteU8(GIF_GCE_PAYLOAD_SIZE);

            /* The packed byte: disposal in bits 4-2, a user-input flag in   */
            /* bit 1 that nothing has honoured in thirty years, and the      */
            /* transparency flag in bit 0.  A still image specifies no       */
            /* disposal, so only the low bit is set.                         */
            bwOut.BYTEW_WriteU8(GIF_STILL_DISPOSAL | GIF_FLAG_TRANSPARENT);

            bwOut.BYTEW_WriteU16LE(GIF_STILL_DELAY);
            bwOut.BYTEW_WriteU8(iTransparentIndex);
            bwOut.BYTEW_WriteU8(GIF_BLOCK_TERMINATOR);
        }

        /*---------------------------------------------------------------------*/
        /* GIF_WriteImageDescriptor:                                           */
        /*                                                                     */
        /* Writes the image descriptor for a frame filling the whole screen,   */
        /* carrying no local colour table and not interlaced.                  */
        /*                                                                     */
        /* THE RASTER IS NOT INTERLACED, and that is forced rather than        */
        /* chosen: the frozen surface carries no option to ask for it, and     */
        /* adding one would amend an API nobody asked to change.  Our decoder  */
        /* reads interlaced files, proven against many of them, so the         */
        /* capability is tested from the other side.                           */
        /*                                                                     */
        /* THERE IS NO LOCAL COLOUR TABLE EITHER.  gif-h writes one per frame, */
        /* which is legal and normal for animation, but a single still image   */
        /* conventionally carries a GLOBAL table and that is what both GDI+    */
        /* and Pillow emit -- which matters, because they are the readers      */
        /* this output is judged against.                                      */
        /*                                                                     */
        /* Arguments:                                                          */
        /*     bwOut   : Destination.  Advanced past the descriptor.           */
        /*     iWidth  : Frame width in pixels.                                */
        /*     iHeight : Frame height in pixels.                               */
        /*                                                                     */
        /* Returns:                                                            */
        /*     void    : Bytes go to bwOut.                                    */
        /*---------------------------------------------------------------------*/

        public static void GIF_WriteImageDescriptor(ByteWriter bwOut, int iWidth,
                                                    int iHeight)
        {
            bwOut.BYTEW_WriteU8(GIF_BLOCK_IMAGE);

            bwOut.BYTEW_WriteU16LE(0);                   // left edge: the frame starts at the origin
            bwOut.BYTEW_WriteU16LE(0);                   // top edge
            bwOut.BYTEW_WriteU16LE(iWidth);
            bwOut.BYTEW_WriteU16LE(iHeight);

            bwOut.BYTEW_WriteU8(0);                      // no local table, not interlaced, not sorted
        }

        /*--------------------------------------------------------------------*/
        /*  ADAPTIVE QUANTISATION                                             */
        /*                                                                    */
        /*  Transcoded from gif-h (charlietangora/gif-h, commit 70b64528),    */
        /*  its GifSplitPalette / GifMakePalette / GifGetClosestPaletteColor  */
        /*  / GifDitherImage / GifThresholdImage group.  gif-h is released    */
        /*  into the PUBLIC DOMAIN under the Unlicense, which satisfies both  */
        /*  arms of this file's licence.  It was chosen as the quantiser's    */
        /*  lineage on the ground that                                        */
        /*  its bitDepth and dither parameters map 1:1 onto the frozen        */
        /*  GIFOPT_iPaletteSize and GIFOPT_bDither where msf_gif's quality    */
        /*  knob has no counterpart at all.                                   */
        /*                                                                    */
        /*  Structure and variable roles are kept so this can be diffed       */
        /*  against the source when it misbehaves; naming and commentary      */
        /*  follow this portfolio's rules on top.                             */
        /*                                                                    */
        /*  THE RECURSION IS KEPT, and that is a decision rather than an      */
        /*  oversight.  The LZW prefix walk had to be de-recursed because a   */
        /*  dictionary chain may legally reach 4096 links and a C# stack      */
        /*  overflow cannot be caught.  Neither recursion here is like that:  */
        /*  GIF_QuantSplitPalette descends one level per tree level and       */
        /*  GIF_FindClosestPaletteColor likewise, so both are bounded by the  */
        /*  BIT DEPTH -- at most eight frames deep, for any image of any      */
        /*  size.  De-recursing them out of habit would obscure the           */
        /*  correspondence with the source for no safety gained.              */
        /*  GIF_QuantPartitionByMedian recurses on a halving interval and is  */
        /*  bounded by log2 of the pixel count.                               */
        /*                                                                    */
        /*  ONE DEVIATION FROM THE SOURCE, AND IT IS A BUG FIX -- see         */
        /*  DEVIATION 4 in this file's header.                                */
        /*--------------------------------------------------------------------*/

        /*--------------------------------------------------------------------*/
        /* GIF_QuantSwapPixels:                                               */
        /*                                                                    */
        /* Exchanges two pixels in the destroyable scratch copy.              */
        /*                                                                    */
        /* DEVIATION 4 LIVES HERE.  gif-h reads the SECOND pixel's alpha from */
        /* the FIRST pixel -- "uint8_t aB = image[pixA*4+3];" where every     */
        /* other line of the function uses pixB -- so a swap leaves both      */
        /* pixels carrying pixel A's alpha and the second one's is lost.      */
        /* Corrected here.                                                    */
        /*                                                                    */
        /* IT IS INVISIBLE IN gif-h AND WOULD BE INVISIBLE HERE TOO, which is */
        /* exactly why it is worth fixing rather than reasoning past.  The    */
        /* scratch buffer this shuffles is only ever read for its R, G and B, */
        /* and the copy is thrown away afterwards, so nothing downstream ever */
        /* looks at the alpha the swap corrupted -- in either codebase.  But  */
        /* "the bug cannot reach anything today" is a statement about today's */
        /* callers, and this project has already paid for what                */
        /* happens when a transcribed defect is reasoned about rather than    */
        /* corrected: stb's swapped upsampler operands sat in two pieces of   */
        /* work behind a tolerance of 62.                                     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     arrIo  : The scratch image, four bytes per pixel.              */
        /*     iPixA  : Index of the first pixel, in PIXELS not bytes.        */
        /*     iPixB  : Index of the second.                                  */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void   : arrIo is modified in place.                           */
        /*--------------------------------------------------------------------*/

        public static void GIF_QuantSwapPixels(byte[] arrIo, int iPixA, int iPixB)
        {
            byte bRedA;
            byte bGreenA;
            byte bBlueA;
            byte bAlphaA;

            bRedA   = arrIo[(iPixA * 4)];
            bGreenA = arrIo[(iPixA * 4) + 1];
            bBlueA  = arrIo[(iPixA * 4) + 2];
            bAlphaA = arrIo[(iPixA * 4) + 3];

            arrIo[(iPixA * 4)]     = arrIo[(iPixB * 4)];
            arrIo[(iPixA * 4) + 1] = arrIo[(iPixB * 4) + 1];
            arrIo[(iPixA * 4) + 2] = arrIo[(iPixB * 4) + 2];
            arrIo[(iPixA * 4) + 3] = arrIo[(iPixB * 4) + 3];

            arrIo[(iPixB * 4)]     = bRedA;
            arrIo[(iPixB * 4) + 1] = bGreenA;
            arrIo[(iPixB * 4) + 2] = bBlueA;
            arrIo[(iPixB * 4) + 3] = bAlphaA;
        }

        /*--------------------------------------------------------------------*/
        /* GIF_QuantPartition:                                                */
        /*                                                                    */
        /* One quicksort partition step over a half-open pixel range, keyed   */
        /* on a single colour axis.                                           */
        /*                                                                    */
        /* THE ALTERNATING "split" FLAG IS NOT DECORATION.  Pixels EQUAL to   */
        /* the pivot are sent left and right alternately rather than all one  */
        /* way, which keeps the partition balanced on an image with a large   */
        /* flat region -- and flat regions are exactly what a synthetic test  */
        /* image is made of.  Sending every equal pixel one way would         */
        /* degenerate the median search to a linear scan there.               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     arrIo    : The scratch image.                                  */
        /*     iLeft    : First pixel of the range.                           */
        /*     iRight   : One past the last pixel of the range.               */
        /*     iElt     : The axis to compare on: 0 red, 1 green, 2 blue.     */
        /*     iPivot   : The value to partition around.                      */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int      : The index everything below the pivot now ends at.   */
        /*--------------------------------------------------------------------*/

        public static int GIF_QuantPartition(byte[] arrIo, int iLeft, int iRight,
                                             int iElt, int iPivot)
        {
            int  iStore;
            int  iValue;
            bool bSplit;
            int  i;

            iStore = iLeft;
            bSplit = false;

            for (i = iLeft; i < iRight; i++)
            {   /* One pixel per iteration. */
                iValue = (int)arrIo[(i * 4) + iElt];

                if (iValue < iPivot)
                {   /* Below the pivot: it belongs on the left. */
                    GIF_QuantSwapPixels(arrIo, i, iStore);
                    iStore = iStore + 1;
                }
                else if (iValue == iPivot)
                {   /* Exactly the pivot: alternate, so a flat region does   */
                    /* not all pile up on one side.                          */
                    if (bSplit)
                    {   /* This one's turn to go left. */
                        GIF_QuantSwapPixels(arrIo, i, iStore);
                        iStore = iStore + 1;
                    }

                    bSplit = !bSplit;
                }
            }

            /* Everything below iStore is less than the pivot. */
            return(iStore);
        }

        /*--------------------------------------------------------------------*/
        /* GIF_QuantPartitionByMedian:                                        */
        /*                                                                    */
        /* An INCOMPLETE sort that puts the pixel at iNeededCenter where it   */
        /* would be if the range were sorted on one axis, with everything     */
        /* smaller before it and everything larger after.                     */
        /*                                                                    */
        /* IT ONLY SORTS THE HALF THAT CONTAINS THE TARGET, which is why it   */
        /* is a quickselect rather than a quicksort: after each partition     */
        /* only the side holding iNeededCenter is recursed into.  A full sort */
        /* would cost more and the palette build does not need one -- all it  */
        /* wants is the median to split at.                                   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     arrIo         : The scratch image.                             */
        /*     iLeft         : First pixel of the range.                      */
        /*     iRight        : One past the last pixel.                       */
        /*     iCom          : The axis to sort on.                           */
        /*     iNeededCenter : The index whose sorted occupant is wanted.     */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void          : arrIo is reordered in place.                   */
        /*--------------------------------------------------------------------*/

        public static void GIF_QuantPartitionByMedian(byte[] arrIo, int iLeft, int iRight,
                                                      int iCom, int iNeededCenter)
        {
            int iPivotValue;
            int iPivotIndex;

            if (iLeft < (iRight - 1))
            {   /* More than one pixel in the range, so there is work to do. */
                iPivotValue = (int)arrIo[(iNeededCenter * 4) + iCom];

                GIF_QuantSwapPixels(arrIo, iNeededCenter, iRight - 1);

                iPivotIndex = GIF_QuantPartition(arrIo, iLeft, iRight - 1, iCom,
                                                 iPivotValue);

                GIF_QuantSwapPixels(arrIo, iPivotIndex, iRight - 1);

                if (iPivotIndex > iNeededCenter)
                {   /* The target is in the left part; only that side needs   */
                    /* sorting further.                                       */
                    GIF_QuantPartitionByMedian(arrIo, iLeft, iPivotIndex, iCom,
                                               iNeededCenter);
                }

                if (iPivotIndex < iNeededCenter)
                {   /* The target is in the right part. */
                    GIF_QuantPartitionByMedian(arrIo, iPivotIndex + 1, iRight, iCom,
                                               iNeededCenter);
                }
            }
        }

        /*--------------------------------------------------------------------*/
        /* GIF_QuantPartitionByMean:                                          */
        /*                                                                    */
        /* Partitions around a GIVEN value rather than around the median, and */
        /* reports where the split fell.                                      */
        /*                                                                    */
        /* WHY THE PALETTE BUILDER EVER WANTS THIS instead of the median: a   */
        /* median split on an image that is 99 percent sky puts almost every  */
        /* subdivision inside the sky and spends the whole palette on shades  */
        /* of blue, throwing away every rare colour in the picture.  When the */
        /* median turns out to be very lopsided within the axis's range,      */
        /* splitting at the RANGE's midpoint instead preserves those rare     */
        /* colours at some cost to the common ones.  See the unbalance test   */
        /* in GIF_QuantSplitPalette.                                          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     arrIo       : The scratch image.                               */
        /*     iLeft       : First pixel of the range.                        */
        /*     iRight      : One past the last pixel.                         */
        /*     iCom        : The axis to compare on.                          */
        /*     iNeededMean : The value to partition around.                   */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int         : Where the split fell.                            */
        /*--------------------------------------------------------------------*/

        public static int GIF_QuantPartitionByMean(byte[] arrIo, int iLeft, int iRight,
                                                   int iCom, int iNeededMean)
        {
            if (iLeft < (iRight - 1))
            {   /* More than one pixel: a real partition. */
                return(GIF_QuantPartition(arrIo, iLeft, iRight - 1, iCom, iNeededMean));
            }

            /* Nothing to divide, so the split is at the start of the range. */
            return(iLeft);
        }

        /*--------------------------------------------------------------------*/
        /* GIF_QuantSplitPalette:                                             */
        /*                                                                    */
        /* Recursively divides a block of pixels along its widest colour axis */
        /* and averages what reaches the bottom, which is the median-split    */
        /* technique and is the whole of the quantiser.                       */
        /*                                                                    */
        /* HOW IT WORKS, because the heap indexing makes it terse.  Node 1 is */
        /* the root and sees every pixel.  Each interior node measures the    */
        /* range of red, green and blue among the pixels it was given, picks  */
        /* the WIDEST of the three, partitions its pixels about the median on */
        /* that axis, and hands each half to a child.  After iBitDepth levels */
        /* the node number reaches 1 << iBitDepth, which is a LEAF, and the   */
        /* pixels that arrived there are averaged into one palette entry.     */
        /* So the palette is the set of averages of 2^bitDepth adaptively     */
        /* chosen boxes in colour space.                                      */
        /*                                                                    */
        /* THE UNBALANCE ESCAPE, and it is the cleverest thing in gif-h.  A   */
        /* median split follows the pixel COUNT, so a picture that is mostly  */
        /* sky subdivides the sky over and over and spends its whole palette  */
        /* there.  When the median sits far from the middle of the axis's     */
        /* RANGE, this splits at the range's midpoint instead, which          */
        /* preserves rare colours.  The threshold 1536 >> treeLevel tightens  */
        /* with depth: near the root, where a bad split costs half the        */
        /* palette, almost any lopsidedness triggers it; deep down, where it  */
        /* costs one entry, only an extreme one does.                         */
        /*                                                                    */
        /* THE RESERVED-INDEX SPECIAL CASE IS OURS TO CONTROL, and this is    */
        /* a deliberate adaptation of gif-h.  gif-h reserves palette          */
        /* entry 0 as transparent UNCONDITIONALLY, because its whole          */
        /* inter-frame delta scheme marks unchanged pixels transparent -- so  */
        /* it forces the node whose children are entries 0 and 1 to give ALL  */
        /* its pixels to entry 1, leaving entry 0 free.  Right for an         */
        /* animation; for a still image it silently costs a colour.  Here it  */
        /* happens only when the caller's GIFOPT_iTransparency, or the image  */
        /* itself, actually asked for a reserved index.                       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     arrIo       : The scratch image, reordered in place.           */
        /*     iOffset     : First pixel of this node's block, in PIXELS.     */
        /*     iNumPixels  : How many pixels this node was given.             */
        /*     iTreeNode   : This node's heap index; 1 is the root.           */
        /*     iTreeLevel  : Depth, 0 at the root, used by the unbalance      */
        /*                   threshold.                                       */
        /*     bForDither  : true reserves the darkest and lightest entries;  */
        /*                   see the note inside.                             */
        /*     palIo       : The palette being filled in.                     */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void        : palIo and arrIo are the output.                  */
        /*--------------------------------------------------------------------*/

        public static void GIF_QuantSplitPalette(byte[] arrIo, int iOffset, int iNumPixels,
                                                 int iTreeNode, int iTreeLevel,
                                                 bool bForDither, GifQuantPalette palIo)
        {
            int iEntries;
            int iEntry;
            int iMinRed;
            int iMaxRed;
            int iMinGreen;
            int iMaxGreen;
            int iMinBlue;
            int iMaxBlue;
            int iValue;
            int iSplitCom;
            int iRangeMin;
            int iRangeMax;
            int iSubPixelsA;
            int iSubPixelsB;
            int iSplitValue;
            int iUnbalance;
            long lRed;
            long lGreen;
            long lBlue;
            int i;

            if (iNumPixels == 0)
            {   /* An empty block contributes no colour, and averaging over   */
                /* zero pixels would divide by zero.                          */
                return;
            }

            iEntries = palIo.GIFQ_iEntries;

            if (iTreeNode >= iEntries)
            {   /* A LEAF: this node IS a palette entry, and the pixels that   */
                /* reached it are the colours it has to represent.             */
                iEntry = iTreeNode - iEntries;

                if (bForDither)
                {   /* DITHERING NEEDS THE EXTREMES PRESENT, and this is the    */
                    /* reason rather than a preference.  Floyd-Steinberg        */
                    /* pushes each pixel's rounding error into its unwritten    */
                    /* neighbours; if the palette holds nothing as dark as      */
                    /* the darkest region of the image, every pixel there is    */
                    /* quantised upward, the error accumulates in one           */
                    /* direction with nothing to discharge it, and the result   */
                    /* is a visible band or a swirl rather than noise.  So      */
                    /* the first real entry is pinned to the darkest colour     */
                    /* present and the last to the lightest.                    */
                    if (iEntry == palIo.GIFQ_iFirstEntry)
                    {   /* The darkest colour in this block, per channel. */
                        iMinRed   = 255;
                        iMinGreen = 255;
                        iMinBlue  = 255;

                        for (i = 0; i < iNumPixels; i++)
                        {   /* One pixel per iteration. */
                            iValue = (int)arrIo[((iOffset + i) * 4)];

                            if (iValue < iMinRed)
                            {   /* A new darkest red. */
                                iMinRed = iValue;
                            }

                            iValue = (int)arrIo[((iOffset + i) * 4) + 1];

                            if (iValue < iMinGreen)
                            {   /* A new darkest green. */
                                iMinGreen = iValue;
                            }

                            iValue = (int)arrIo[((iOffset + i) * 4) + 2];

                            if (iValue < iMinBlue)
                            {   /* A new darkest blue. */
                                iMinBlue = iValue;
                            }
                        }

                        palIo.GIFQ_arrRed[iEntry]   = (byte)iMinRed;
                        palIo.GIFQ_arrGreen[iEntry] = (byte)iMinGreen;
                        palIo.GIFQ_arrBlue[iEntry]  = (byte)iMinBlue;

                        return;
                    }

                    if (iEntry == (iEntries - 1))
                    {   /* The lightest colour in this block, per channel. */
                        iMaxRed   = 0;
                        iMaxGreen = 0;
                        iMaxBlue  = 0;

                        for (i = 0; i < iNumPixels; i++)
                        {   /* One pixel per iteration. */
                            iValue = (int)arrIo[((iOffset + i) * 4)];

                            if (iValue > iMaxRed)
                            {   /* A new lightest red. */
                                iMaxRed = iValue;
                            }

                            iValue = (int)arrIo[((iOffset + i) * 4) + 1];

                            if (iValue > iMaxGreen)
                            {   /* A new lightest green. */
                                iMaxGreen = iValue;
                            }

                            iValue = (int)arrIo[((iOffset + i) * 4) + 2];

                            if (iValue > iMaxBlue)
                            {   /* A new lightest blue. */
                                iMaxBlue = iValue;
                            }
                        }

                        palIo.GIFQ_arrRed[iEntry]   = (byte)iMaxRed;
                        palIo.GIFQ_arrGreen[iEntry] = (byte)iMaxGreen;
                        palIo.GIFQ_arrBlue[iEntry]  = (byte)iMaxBlue;

                        return;
                    }
                }

                /* THE ORDINARY LEAF: the average of the subcube.  Summed in   */
                /* 64 bits because a 65535 x 65535 image would overflow a      */
                /* 32-bit accumulator at 255 per pixel, and the encoder        */
                /* accepts geometries that large.                              */
                lRed   = 0;
                lGreen = 0;
                lBlue  = 0;

                for (i = 0; i < iNumPixels; i++)
                {   /* One pixel per iteration. */
                    lRed   = lRed + (long)arrIo[((iOffset + i) * 4)];
                    lGreen = lGreen + (long)arrIo[((iOffset + i) * 4) + 1];
                    lBlue  = lBlue + (long)arrIo[((iOffset + i) * 4) + 2];
                }

                /* Half the count added before dividing is integer rounding   */
                /* to nearest rather than truncation, which matters: a        */
                /* consistent downward bias across every entry would darken   */
                /* the whole image by up to one count.                        */
                lRed   = (lRed + (long)(iNumPixels / 2)) / (long)iNumPixels;
                lGreen = (lGreen + (long)(iNumPixels / 2)) / (long)iNumPixels;
                lBlue  = (lBlue + (long)(iNumPixels / 2)) / (long)iNumPixels;

                palIo.GIFQ_arrRed[iEntry]   = (byte)lRed;
                palIo.GIFQ_arrGreen[iEntry] = (byte)lGreen;
                palIo.GIFQ_arrBlue[iEntry]  = (byte)lBlue;

                return;
            }

            /* AN INTERIOR NODE: find the axis with the widest spread. */
            iMinRed   = 255;
            iMaxRed   = 0;
            iMinGreen = 255;
            iMaxGreen = 0;
            iMinBlue  = 255;
            iMaxBlue  = 0;

            for (i = 0; i < iNumPixels; i++)
            {   /* One pixel per iteration, tracking six extremes. */
                iValue = (int)arrIo[((iOffset + i) * 4)];

                if (iValue > iMaxRed)
                {   /* A new widest red, upward. */
                    iMaxRed = iValue;
                }

                if (iValue < iMinRed)
                {   /* A new widest red, downward. */
                    iMinRed = iValue;
                }

                iValue = (int)arrIo[((iOffset + i) * 4) + 1];

                if (iValue > iMaxGreen)
                {   /* A new widest green, upward. */
                    iMaxGreen = iValue;
                }

                if (iValue < iMinGreen)
                {   /* A new widest green, downward. */
                    iMinGreen = iValue;
                }

                iValue = (int)arrIo[((iOffset + i) * 4) + 2];

                if (iValue > iMaxBlue)
                {   /* A new widest blue, upward. */
                    iMaxBlue = iValue;
                }

                if (iValue < iMinBlue)
                {   /* A new widest blue, downward. */
                    iMinBlue = iValue;
                }
            }

            /* GREEN IS THE DEFAULT AND THE TESTS ARE ORDERED, not chained      */
            /* with else.  Blue displaces green only if strictly wider, and     */
            /* red displaces both only if strictly wider than each -- so ties   */
            /* resolve green, then blue, then red, deterministically.  The      */
            /* same image must always produce the same palette.                 */
            iSplitCom = 1;
            iRangeMin = iMinGreen;
            iRangeMax = iMaxGreen;

            if ((iMaxBlue - iMinBlue) > (iMaxGreen - iMinGreen))
            {   /* Blue spreads wider than green. */
                iSplitCom = 2;
                iRangeMin = iMinBlue;
                iRangeMax = iMaxBlue;
            }

            if ((iMaxRed - iMinRed) > (iMaxBlue - iMinBlue) &&
                (iMaxRed - iMinRed) > (iMaxGreen - iMinGreen))
            {   /* Red spreads wider than both. */
                iSplitCom = 0;
                iRangeMin = iMinRed;
                iRangeMax = iMaxRed;
            }

            iSubPixelsA = iNumPixels / 2;

            GIF_QuantPartitionByMedian(arrIo, iOffset, iOffset + iNumPixels, iSplitCom,
                                       iOffset + iSubPixelsA);

            iSplitValue = (int)arrIo[((iOffset + iSubPixelsA) * 4) + iSplitCom];

            /* THE UNBALANCE ESCAPE described in this function's header. */
            iUnbalance = (iSplitValue - iRangeMin) - (iRangeMax - iSplitValue);

            if (iUnbalance < 0)
            {   /* Absolute value, spelled out rather than through a helper. */
                iUnbalance = -iUnbalance;
            }

            if (iUnbalance > (1536 >> iTreeLevel))
            {   /* The median is far off the centre of the range, so split at   */
                /* the range's midpoint instead and keep the rare colours.      */
                iSplitValue = iRangeMin + ((iRangeMax - iRangeMin) / 2);

                iSubPixelsA = GIF_QuantPartitionByMean(arrIo, iOffset,
                                                       iOffset + iNumPixels,
                                                       iSplitCom, iSplitValue)
                              - iOffset;
            }

            if (palIo.GIFQ_iFirstEntry > 0 && iTreeNode == (iEntries / 2))
            {   /* THE RESERVED INDEX.  This node's children are entries 0     */
                /* and 1, so giving it no left-hand pixels leaves entry 0      */
                /* empty and free to be the transparent index.  Done ONLY      */
                /* when an index is actually being reserved -- gif-h does it   */
                /* unconditionally and a still image pays a colour for it.     */
                iSubPixelsA = 0;
                iSplitValue = 0;
            }

            iSubPixelsB = iNumPixels - iSubPixelsA;

            palIo.GIFQ_arrSplitElt[iTreeNode]   = (byte)iSplitCom;
            palIo.GIFQ_arrSplitValue[iTreeNode] = (byte)iSplitValue;

            GIF_QuantSplitPalette(arrIo, iOffset, iSubPixelsA, iTreeNode * 2,
                                  iTreeLevel + 1, bForDither, palIo);

            GIF_QuantSplitPalette(arrIo, iOffset + iSubPixelsA, iSubPixelsB,
                                  (iTreeNode * 2) + 1, iTreeLevel + 1, bForDither, palIo);
        }

        /*--------------------------------------------------------------------*/
        /* GIF_FindClosestPaletteColor:                                       */
        /*                                                                    */
        /* Walks the k-d tree to pick the palette entry nearest a wanted      */
        /* colour, improving the caller's running best rather than returning  */
        /* an answer outright.                                                */
        /*                                                                    */
        /* THE DISTANCE IS L1 -- the sum of the three absolute channel        */
        /* differences -- and not Euclidean.  That is gif-h's choice kept:    */
        /* it needs no multiplies, and the pruning test below is only valid   */
        /* for a metric where crossing a split plane costs at least the       */
        /* distance to it, which L1 satisfies exactly.                        */
        /*                                                                    */
        /* THE SEARCH IS APPROXIMATE, AND THAT IS MEASURED RATHER THAN        */
        /* ASSUMED.  It descends to the near side of each split, then visits  */
        /* the far side only if the best distance so far exceeds the distance */
        /* to the plane.  That is the standard exact-search prune -- but it   */
        /* is only sound when every leaf's colour lies on its own side of     */
        /* every ancestor's plane, and here it need not: the planes are       */
        /* medians of the pixels, while the leaves hold AVERAGES of them, and */
        /* an average can fall across a plane.  So this can miss a nearer     */
        /* entry.  How often was MEASURED against an exhaustive scan: 2       */
        /* misses in 20 000, worst cost 6 of a possible 765.                  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     palIn      : The palette and its tree.                         */
        /*     iRed       : Wanted red.  May exceed 0..255 -- the dither      */
        /*                  accumulates error and hands over out-of-range     */
        /*                  values deliberately.                              */
        /*     iGreen     : Wanted green.                                     */
        /*     iBlue      : Wanted blue.                                      */
        /*     iBestInd   : Running best entry; improved in place.            */
        /*     iBestDiff  : Running best distance; improved in place.         */
        /*     iTreeRoot  : Node to search from; callers pass 1, the root.    */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void       : iBestInd and iBestDiff are the output.            */
        /*--------------------------------------------------------------------*/

        public static void GIF_FindClosestPaletteColor(GifQuantPalette palIn, int iRed,
                                                       int iGreen, int iBlue,
                                                       ref int iBestInd, ref int iBestDiff,
                                                       int iTreeRoot)
        {
            int iInd;
            int iRedErr;
            int iGreenErr;
            int iBlueErr;
            int iDiff;
            int iSplitComp;
            int iSplitPos;

            if (iTreeRoot > (palIn.GIFQ_iEntries - 1))
            {   /* A LEAF: compare this one entry against the running best. */
                iInd = iTreeRoot - palIn.GIFQ_iEntries;

                if (iInd < palIn.GIFQ_iFirstEntry)
                {   /* The reserved transparent index is not a colour and   */
                    /* must never be chosen for an opaque pixel.            */
                    return;
                }

                iRedErr   = iRed - (int)palIn.GIFQ_arrRed[iInd];
                iGreenErr = iGreen - (int)palIn.GIFQ_arrGreen[iInd];
                iBlueErr  = iBlue - (int)palIn.GIFQ_arrBlue[iInd];

                if (iRedErr < 0)
                {   /* Absolute value, spelled out. */
                    iRedErr = -iRedErr;
                }

                if (iGreenErr < 0)
                {   /* Absolute value, spelled out. */
                    iGreenErr = -iGreenErr;
                }

                if (iBlueErr < 0)
                {   /* Absolute value, spelled out. */
                    iBlueErr = -iBlueErr;
                }

                iDiff = iRedErr + iGreenErr + iBlueErr;

                if (iDiff < iBestDiff)
                {   /* Nearer than anything seen so far. */
                    iBestInd  = iInd;
                    iBestDiff = iDiff;
                }

                return;
            }

            /* An interior node: pick the wanted colour's value on this    */
            /* node's axis and compare it against where the node splits.   */
            if (palIn.GIFQ_arrSplitElt[iTreeRoot] == 0)
            {   /* This node splits on red. */
                iSplitComp = iRed;
            }
            else if (palIn.GIFQ_arrSplitElt[iTreeRoot] == 1)
            {   /* This node splits on green. */
                iSplitComp = iGreen;
            }
            else
            {   /* This node splits on blue. */
                iSplitComp = iBlue;
            }

            iSplitPos = (int)palIn.GIFQ_arrSplitValue[iTreeRoot];

            if (iSplitPos > iSplitComp)
            {   /* The wanted colour is on the LEFT of the plane, so search   */
                /* that side first -- it holds the likelier answer.           */
                GIF_FindClosestPaletteColor(palIn, iRed, iGreen, iBlue, ref iBestInd,
                                            ref iBestDiff, iTreeRoot * 2);

                if (iBestDiff > (iSplitPos - iSplitComp))
                {   /* The best so far is further away than the plane is, so   */
                    /* the other side could still hold something nearer and    */
                    /* cannot be pruned.                                       */
                    GIF_FindClosestPaletteColor(palIn, iRed, iGreen, iBlue, ref iBestInd,
                                                ref iBestDiff, (iTreeRoot * 2) + 1);
                }
            }
            else
            {   /* The wanted colour is on the RIGHT of the plane. */
                GIF_FindClosestPaletteColor(palIn, iRed, iGreen, iBlue, ref iBestInd,
                                            ref iBestDiff, (iTreeRoot * 2) + 1);

                if (iBestDiff > (iSplitComp - iSplitPos))
                {   /* The left side cannot be pruned either. */
                    GIF_FindClosestPaletteColor(palIn, iRed, iGreen, iBlue, ref iBestInd,
                                                ref iBestDiff, iTreeRoot * 2);
                }
            }
        }

        /*--------------------------------------------------------------------*/
        /* GIF_TryBuildQuantPalette:                                          */
        /*                                                                    */
        /* Builds an adaptive palette for an image that has more colours than */
        /* the table can hold.                                                */
        /*                                                                    */
        /* IT PARTITIONS A DESTROYABLE COPY, which is why the scratch buffer  */
        /* exists: GIF_QuantSplitPalette sorts the pixels by colour as it     */
        /* goes, and the caller's image must survive intact.  Budget four     */
        /* bytes per opaque pixel.                                            */
        /*                                                                    */
        /* ONLY OPAQUE PIXELS GO IN, and that is a departure from gif-h worth */
        /* stating.  A fully transparent pixel is never drawn, so its colour  */
        /* must not pull a palette entry toward itself -- an image with a     */
        /* large transparent region full of stale black would otherwise spend */
        /* entries representing a colour nobody will ever see.  gif-h has no  */
        /* equivalent because it treats alpha as a delta-encoding marker      */
        /* rather than as transparency.                                       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     imgIn         : Source, 8-bit RGBA.  NOT modified.             */
        /*     iTableEntries : Entries the table will carry; a power of two.  */
        /*     bReserve      : true when index 0 is the transparent index.    */
        /*     bDither       : true builds a palette suited to dithering, by  */
        /*                     pinning the darkest and lightest entries.      */
        /*     palOut        : Receives the palette and its tree.             */
        /*     sReason       : Receives "" or why it could not be built.      */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool          : true when palOut holds a usable palette.       */
        /*--------------------------------------------------------------------*/

        public static bool GIF_TryBuildQuantPalette(ImageBuffer imgIn, int iTableEntries,
                                                    bool bReserve, bool bDither,
                                                    out GifQuantPalette palOut,
                                                    out string sReason)
        {
            GifQuantPalette pal;
            byte[]          arrScratch;
            int             iPixels;
            int             iOpaque;
            int             iBitDepth;
            int             iSrc;
            int             i;

            palOut  = null;
            sReason = "";

            if (imgIn.IMG_iChannels != ImageBuffer.IMG_CHANNELS_RGBA ||
                imgIn.IMG_iBitsPerSample != ImageBuffer.IMG_BITS_8)
            {   /* THE SHAPE IS CHECKED RATHER THAN ASSUMED, because this is   */
                /* public for testability and a caller reaching past           */
                /* GIF_TryEncode does not get its channel conversion.  Fed a   */
                /* three-channel buffer this function would read one sample    */
                /* past the end of the last row and throw an index             */
                /* exception -- which is exactly what it did the first time    */
                /* the quantise scenario handed it a photograph straight out   */
                /* of PNG_TryDecode.  A refusal names the problem; an          */
                /* IndexOutOfRangeException names nothing.                     */
                sReason = "GIF encode: the quantiser needs an 8-bit RGBA buffer and was " +
                          "given " + imgIn.IMG_iChannels.ToString() + " channels at " +
                          imgIn.IMG_iBitsPerSample.ToString() + " bits";
                return(false);
            }

            iPixels = imgIn.IMG_iWidth * imgIn.IMG_iHeight;

            iBitDepth = 1;

            while ((1 << iBitDepth) < iTableEntries)
            {   /* At most seven doublings, from 2 up to 256. */
                iBitDepth = iBitDepth + 1;
            }

            pal = new GifQuantPalette();

            pal.GIFQ_iBitDepth  = iBitDepth;
            pal.GIFQ_iEntries   = iTableEntries;
            pal.GIFQ_arrRed     = new byte[GIF_PALETTE_ENTRIES];
            pal.GIFQ_arrGreen   = new byte[GIF_PALETTE_ENTRIES];
            pal.GIFQ_arrBlue    = new byte[GIF_PALETTE_ENTRIES];
            pal.GIFQ_arrSplitElt   = new byte[GIF_PALETTE_ENTRIES];
            pal.GIFQ_arrSplitValue = new byte[GIF_PALETTE_ENTRIES];

            if (bReserve)
            {   /* Entry 0 is the transparent index and is not a colour, so    */
                /* the split must skip it and the search must never pick it.   */
                pal.GIFQ_iFirstEntry = 1;
            }
            else
            {   /* Every entry is a colour. */
                pal.GIFQ_iFirstEntry = 0;
            }

            /* Copy the OPAQUE pixels into the destroyable buffer, compacting   */
            /* as we go so the split never sees a pixel that is not drawn.      */
            arrScratch = new byte[iPixels * GIF_CHANNELS];
            iOpaque    = 0;

            for (i = 0; i < iPixels; i++)
            {   /* One pixel per iteration. */
                iSrc = i * GIF_CHANNELS;

                if (imgIn.IMG_arrSamples8[iSrc + 3] == 0)
                {   /* Fully transparent: contributes no colour. */
                    continue;
                }

                arrScratch[(iOpaque * 4)]     = imgIn.IMG_arrSamples8[iSrc];
                arrScratch[(iOpaque * 4) + 1] = imgIn.IMG_arrSamples8[iSrc + 1];
                arrScratch[(iOpaque * 4) + 2] = imgIn.IMG_arrSamples8[iSrc + 2];
                arrScratch[(iOpaque * 4) + 3] = imgIn.IMG_arrSamples8[iSrc + 3];

                iOpaque = iOpaque + 1;
            }

            if (iOpaque < 1)
            {   /* Every pixel is transparent, so there is no colour to       */
                /* quantise.  Not an error -- the palette is simply unused,   */
                /* and every pixel will name the reserved index.              */
                palOut = pal;

                /* A palette of nothing, for an image of nothing visible. */
                return(true);
            }

            GIF_QuantSplitPalette(arrScratch, 0, iOpaque, 1, 0, bDither, pal);

            if (bReserve)
            {   /* Match gif-h in pinning the reserved node's split to a red    */
                /* axis at zero, and its colour to black.  The colour is        */
                /* never drawn -- the transparent index is not rendered -- so   */
                /* this is about the file being predictable rather than about   */
                /* what it looks like.                                          */
                pal.GIFQ_arrSplitElt[iTableEntries / 2]   = 0;
                pal.GIFQ_arrSplitValue[iTableEntries / 2] = 0;

                pal.GIFQ_arrRed[0]   = 0;
                pal.GIFQ_arrGreen[0] = 0;
                pal.GIFQ_arrBlue[0]  = 0;
            }

            palOut = pal;

            /* An adaptive palette over the image's opaque colours. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* GIF_QuantiseThreshold:                                             */
        /*                                                                    */
        /* Maps every pixel to its nearest palette entry, with no dithering.  */
        /*                                                                    */
        /* THE PLAIN CHOICE, AND THE RIGHT DEFAULT.  Each pixel is decided on */
        /* its own, so flat regions stay flat and a gradient bands.  That is  */
        /* the trade dithering reverses, and which one a caller wants depends */
        /* on their picture -- which is why GIFOPT_bDither exists and is off  */
        /* by default, the same call BMPOPT_bRleCompress makes.               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     imgIn         : Source, 8-bit RGBA.  Not modified.             */
        /*     palIn         : The palette to map into.                       */
        /*     arrIndicesOut : Receives one palette index per pixel.  Must    */
        /*                     already be the pixel count in length.          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void          : arrIndicesOut is the output.                   */
        /*--------------------------------------------------------------------*/

        public static void GIF_QuantiseThreshold(ImageBuffer imgIn, GifQuantPalette palIn,
                                                 byte[] arrIndicesOut)
        {
            int iPixels;
            int iBestInd;
            int iBestDiff;
            int iSrc;
            int i;

            iPixels = imgIn.IMG_iWidth * imgIn.IMG_iHeight;

            for (i = 0; i < iPixels; i++)
            {   /* One pixel per iteration. */
                iSrc = i * GIF_CHANNELS;

                if (palIn.GIFQ_iFirstEntry > 0 && imgIn.IMG_arrSamples8[iSrc + 3] == 0)
                {   /* Fully transparent: it names the reserved index and its   */
                    /* colour plays no part.                                    */
                    arrIndicesOut[i] = 0;
                    continue;
                }

                iBestInd  = palIn.GIFQ_iFirstEntry;
                iBestDiff = GIF_QUANT_WORST_DIFF;

                GIF_FindClosestPaletteColor(palIn, (int)imgIn.IMG_arrSamples8[iSrc],
                                            (int)imgIn.IMG_arrSamples8[iSrc + 1],
                                            (int)imgIn.IMG_arrSamples8[iSrc + 2],
                                            ref iBestInd, ref iBestDiff, 1);

                arrIndicesOut[i] = (byte)iBestInd;
            }
        }

        /*--------------------------------------------------------------------*/
        /* GIF_QuantiseDither:                                                */
        /*                                                                    */
        /* Maps every pixel to a palette entry, diffusing each pixel's        */
        /* rounding error into its neighbours -- Floyd-Steinberg.             */
        /*                                                                    */
        /* THE WORKING BUFFER HOLDS EACH CHANNEL SCALED BY 256, which is the  */
        /* whole reason the algorithm works at eight bits.  The error pushed  */
        /* into a neighbour is usually a fraction of one colour count, and    */
        /* accumulating those fractions is the point; rounding each to a      */
        /* whole count as it was stored would discard almost all of them and  */
        /* leave the output barely different from the undithered version.     */
        /*                                                                    */
        /* THE FOUR WEIGHTS ARE FLOYD AND STEINBERG'S, and they are pushed    */
        /* only FORWARD -- right, and the three below -- because those are    */
        /* the pixels not yet decided.  Diffusing backwards would change a    */
        /* pixel already written.                                             */
        /*                                                                    */
        /*     . . . . . . . . . . . . . . . . . . *  7                       */
        /*     . . . . . . . . . . . . . . . 3   5   1     (all over 16)      */
        /*                                                                    */
        /* THE CLAMP IS ONE-SIDED IN THE SOURCE and is kept that way.  Each   */
        /* accumulation is floored at zero -- "add at least minus what is     */
        /* already there" -- so a channel can never go negative, but nothing  */
        /* caps it above 255.  A wanted colour above the range is handed to   */
        /* the search unchanged, which is why GIF_FindClosestPaletteColor's   */
        /* header says its arguments may exceed 0..255: it simply finds the   */
        /* brightest entry, which is the right answer.                        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     imgIn         : Source, 8-bit RGBA.  Not modified.             */
        /*     palIn         : The palette to map into.  Should have been     */
        /*                     built with bDither true, or the extremes may   */
        /*                     be missing and error will accumulate.          */
        /*     arrIndicesOut : Receives one palette index per pixel.          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void          : arrIndicesOut is the output.                   */
        /*--------------------------------------------------------------------*/

        public static void GIF_QuantiseDither(ImageBuffer imgIn, GifQuantPalette palIn,
                                              byte[] arrIndicesOut)
        {
            int[] arrQuant;
            int   iPixels;
            int   iWidth;
            int   iHeight;
            int   iWantRed;
            int   iWantGreen;
            int   iWantBlue;
            int   iBestInd;
            int   iBestDiff;
            int   iRedErr;
            int   iGreenErr;
            int   iBlueErr;
            int   iHere;
            int   iX;
            int   iY;
            int   i;

            iWidth  = imgIn.IMG_iWidth;
            iHeight = imgIn.IMG_iHeight;
            iPixels = iWidth * iHeight;

            arrQuant = new int[iPixels * GIF_CHANNELS];

            for (i = 0; i < (iPixels * GIF_CHANNELS); i++)
            {   /* Scale every channel up so sub-count error survives. */
                arrQuant[i] = (int)imgIn.IMG_arrSamples8[i] * GIF_DITHER_SCALE;
            }

            for (iY = 0; iY < iHeight; iY++)
            {   /* One row per iteration, top to bottom, because the error   */
                /* is diffused downward and must land on undecided pixels.   */
                for (iX = 0; iX < iWidth; iX++)
                {   /* One pixel per iteration, left to right, for the same   */
                    /* reason.                                                */
                    iHere = ((iY * iWidth) + iX) * GIF_CHANNELS;

                    if (palIn.GIFQ_iFirstEntry > 0 &&
                        imgIn.IMG_arrSamples8[iHere + 3] == 0)
                    {   /* Fully transparent: it names the reserved index,    */
                        /* and NO ERROR IS DIFFUSED FROM IT -- there was no   */
                        /* rounding, because nothing was drawn.  Diffusing    */
                        /* from an invisible pixel would smear a colour       */
                        /* nobody chose into the pixels around a hole.        */
                        arrIndicesOut[(iY * iWidth) + iX] = 0;
                        continue;
                    }

                    /* Round to nearest on the way back down to colour counts. */
                    iWantRed   = (arrQuant[iHere] + (GIF_DITHER_SCALE / 2)) /
                                 GIF_DITHER_SCALE;
                    iWantGreen = (arrQuant[iHere + 1] + (GIF_DITHER_SCALE / 2)) /
                                 GIF_DITHER_SCALE;
                    iWantBlue  = (arrQuant[iHere + 2] + (GIF_DITHER_SCALE / 2)) /
                                 GIF_DITHER_SCALE;

                    iBestInd  = palIn.GIFQ_iFirstEntry;
                    iBestDiff = GIF_QUANT_WORST_DIFF;

                    GIF_FindClosestPaletteColor(palIn, iWantRed, iWantGreen, iWantBlue,
                                                ref iBestInd, ref iBestDiff, 1);

                    /* The error is the difference between what was wanted at   */
                    /* full precision and what the palette could give.          */
                    iRedErr   = arrQuant[iHere] -
                                ((int)palIn.GIFQ_arrRed[iBestInd] * GIF_DITHER_SCALE);
                    iGreenErr = arrQuant[iHere + 1] -
                                ((int)palIn.GIFQ_arrGreen[iBestInd] * GIF_DITHER_SCALE);
                    iBlueErr  = arrQuant[iHere + 2] -
                                ((int)palIn.GIFQ_arrBlue[iBestInd] * GIF_DITHER_SCALE);

                    arrIndicesOut[(iY * iWidth) + iX] = (byte)iBestInd;

                    GIF_DiffuseError(arrQuant, iPixels, (iY * iWidth) + iX + 1,
                                     iRedErr, iGreenErr, iBlueErr, 7);

                    GIF_DiffuseError(arrQuant, iPixels,
                                     (iY * iWidth) + iWidth + iX - 1,
                                     iRedErr, iGreenErr, iBlueErr, 3);

                    GIF_DiffuseError(arrQuant, iPixels, (iY * iWidth) + iWidth + iX,
                                     iRedErr, iGreenErr, iBlueErr, 5);

                    GIF_DiffuseError(arrQuant, iPixels, (iY * iWidth) + iWidth + iX + 1,
                                     iRedErr, iGreenErr, iBlueErr, 1);
                }
            }
        }

        /*--------------------------------------------------------------------*/
        /* GIF_DiffuseError:                                                  */
        /*                                                                    */
        /* Adds a weighted share of one pixel's quantisation error into a     */
        /* later pixel's working value.                                       */
        /*                                                                    */
        /* THE BOUNDS TEST IS DELIBERATELY ONLY AN UPPER ONE, matching the    */
        /* source, and the consequence is worth knowing: the three downward   */
        /* neighbours of the LAST pixel in a row wrap onto the first pixels   */
        /* of the row below rather than being discarded.  On a photograph     */
        /* that is invisible; on a narrow synthetic it is a real if tiny      */
        /* difference from a textbook Floyd-Steinberg.  It is left as the     */
        /* source has it because the alternative is a different algorithm,    */
        /* not a bug fix -- and because the error it can introduce is bounded */
        /* by one pixel's rounding, spread over three.                        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     arrQuantIo : The scaled working buffer.                        */
        /*     iPixels    : Total pixel count, the upper bound.               */
        /*     iTarget    : Which pixel receives the share.                   */
        /*     iRedErr    : The whole red error being diffused.               */
        /*     iGreenErr  : The whole green error.                            */
        /*     iBlueErr   : The whole blue error.                             */
        /*     iWeight    : This neighbour's share, out of sixteen.           */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void       : arrQuantIo is modified in place.                  */
        /*--------------------------------------------------------------------*/

        public static void GIF_DiffuseError(int[] arrQuantIo, int iPixels, int iTarget,
                                            int iRedErr, int iGreenErr, int iBlueErr,
                                            int iWeight)
        {
            int iAt;
            int iShare;

            if (iTarget >= iPixels)
            {   /* Off the end of the image; there is nothing to receive it. */
                return;
            }

            iAt = iTarget * GIF_CHANNELS;

            /* THE FLOOR AT ZERO IS THE SOURCE'S ONE-SIDED CLAMP: add the     */
            /* share, but never take a channel below zero.  Written as a      */
            /* maximum against the negated current value, which is the form   */
            /* gif-h uses and which keeps the two diffable.                   */
            iShare = (iRedErr * iWeight) / 16;

            if (iShare < -arrQuantIo[iAt])
            {   /* The share would drive this channel negative. */
                iShare = -arrQuantIo[iAt];
            }

            arrQuantIo[iAt] = arrQuantIo[iAt] + iShare;

            iShare = (iGreenErr * iWeight) / 16;

            if (iShare < -arrQuantIo[iAt + 1])
            {   /* The share would drive this channel negative. */
                iShare = -arrQuantIo[iAt + 1];
            }

            arrQuantIo[iAt + 1] = arrQuantIo[iAt + 1] + iShare;

            iShare = (iBlueErr * iWeight) / 16;

            if (iShare < -arrQuantIo[iAt + 2])
            {   /* The share would drive this channel negative. */
                iShare = -arrQuantIo[iAt + 2];
            }

            arrQuantIo[iAt + 2] = arrQuantIo[iAt + 2] + iShare;
        }

        /*--------------------------------------------------------------------*/
        /* GIF_TryQuantiseToPalette:                                          */
        /*                                                                    */
        /* The whole quantisation path in one call: build an adaptive palette */
        /* for the image, then map every pixel into it.                       */
        /*                                                                    */
        /* THE PALETTE IS BUILT DIFFERENTLY DEPENDING ON bDither, which is    */
        /* easy to miss and matters.  Dithering does not merely change the    */
        /* MAPPING pass -- it changes the palette itself, because the split   */
        /* pins the darkest and lightest colours present when it knows error  */
        /* will be diffused.  Building a plain palette and then dithering     */
        /* into it produces the accumulation artefacts that pinning exists to */
        /* prevent, so the flag is passed to both halves and not just the     */
        /* second.                                                            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     imgIn         : Source, 8-bit RGBA.  Not modified.             */
        /*     iTableEntries : Entries the table will carry.                  */
        /*     bReserve      : true when index 0 is the transparent index.    */
        /*     bDither       : true applies Floyd-Steinberg, and builds the   */
        /*                     palette to suit it.                            */
        /*     arrTableOut   : Receives the wire-form colour table.           */
        /*     arrIndicesOut : Receives one index per pixel.                  */
        /*     sReason       : Receives "" or why it was refused.             */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool          : true when the image was quantised.             */
        /*--------------------------------------------------------------------*/

        public static bool GIF_TryQuantiseToPalette(ImageBuffer imgIn, int iTableEntries,
                                                    bool bReserve, bool bDither,
                                                    out byte[] arrTableOut,
                                                    out byte[] arrIndicesOut,
                                                    out string sReason)
        {
            GifQuantPalette pal;
            byte[]          arrIndices;

            arrTableOut   = null;
            arrIndicesOut = null;
            sReason       = "";

            if (bReserve && iTableEntries < 2)
            {   /* Reserving an index out of a table this small would leave   */
                /* no colour at all.  Not reachable through GIF_TryEncode,    */
                /* whose table is at least two, but this is public.           */
                sReason = "GIF encode: a reserved transparent index needs a table of at " +
                          "least two entries";
                return(false);
            }

            if (!GIF_TryBuildQuantPalette(imgIn, iTableEntries, bReserve, bDither,
                                          out pal, out sReason))
            {   /* The palette builder declined; it says why. */
                return(false);
            }

            arrIndices = new byte[imgIn.IMG_iWidth * imgIn.IMG_iHeight];

            if (bDither)
            {   /* Diffuse each pixel's error into its undecided neighbours. */
                GIF_QuantiseDither(imgIn, pal, arrIndices);
            }
            else
            {   /* Decide every pixel on its own. */
                GIF_QuantiseThreshold(imgIn, pal, arrIndices);
            }

            arrTableOut   = GIF_BuildQuantTable(pal, iTableEntries);
            arrIndicesOut = arrIndices;

            /* An adaptive palette and the image mapped into it. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* GIF_BuildQuantTable:                                               */
        /*                                                                    */
        /* Flattens a GifQuantPalette into the wire form the colour table     */
        /* writer wants: three bytes an entry, red then green then blue.      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     palIn         : The palette to flatten.                        */
        /*     iTableEntries : How many entries the table carries.            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     byte[]        : iTableEntries * 3 bytes, ready to write.       */
        /*--------------------------------------------------------------------*/

        public static byte[] GIF_BuildQuantTable(GifQuantPalette palIn, int iTableEntries)
        {
            byte[] arrTable;
            int    i;

            arrTable = new byte[iTableEntries * GIF_RGB_STRIDE];

            for (i = 0; i < iTableEntries; i++)
            {   /* One entry per iteration. */
                arrTable[(i * GIF_RGB_STRIDE)]     = palIn.GIFQ_arrRed[i];
                arrTable[(i * GIF_RGB_STRIDE) + 1] = palIn.GIFQ_arrGreen[i];
                arrTable[(i * GIF_RGB_STRIDE) + 2] = palIn.GIFQ_arrBlue[i];
            }

            return(arrTable);
        }

        /*--------------------------------------------------------------------*/
        /* GIF_TryEncode:                                                     */
        /*                                                                    */
        /* THE PALETTE IS EXACT, SO THE ENCODING IS LOSSLESS.  Every pixel    */
        /* names a table entry holding its own colour, which means a file     */
        /* written here decodes back to the pixels it was given, exactly --   */
        /* and that is what lets this path be checked at tolerance            */
        /* ZERO both through this library's own decoder and through GDI+.     */
        /* An image with more distinct colours than the table can hold does   */
        /* not come here at all; it goes to the quantiser instead.            */
        /*                                                                    */
        /* WITH ONE STATED EXCEPTION, AND IT IS THE ONLY ONE: THE COLOUR      */
        /* UNDER A FULLY TRANSPARENT PIXEL IS NOT PRESERVED.  Such a pixel is */
        /* never drawn, so its RGB carries no information -- and ignoring it  */
        /* is not an economy but a requirement, because a real RGBA source    */
        /* can hold hundreds of different colours beneath alpha 0 and giving  */
        /* each a table entry would exhaust the palette on pixels nobody can  */
        /* see.  So the guarantee is exact in every VISIBLE sample: alpha     */
        /* everywhere, and colour wherever alpha is not zero.  A round trip   */
        /* returns those transparent pixels as zero.                          */
        /*                                                                    */
        /* WHAT IS NOT HERE IS ANIMATION, which belongs to the                */
        /* GIF_TryEncodeFrames call.  GIFOPT_iLoopCount is therefore ignored, */
        /* which the frozen surface already says of the single-frame call.    */
        /*                                                                    */
        /* THE OUTPUT IS DETERMINISTIC.  Palette entries are numbered in the  */
        /* order the colours are first encountered, so the same buffer always */
        /* produces the same bytes -- which is what makes a byte-identity     */
        /* check against our own output meaningful at all.                    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     imgIn    : Source image.  8-bit; any of the four channel       */
        /*                counts.  Not modified.  Must not be null.           */
        /*     optIn    : Encode options, or null for the defaults.           */
        /*     arrOut   : Receives the complete file, or null on failure.     */
        /*     sReason  : Receives "" on success, or a short human-readable   */
        /*                cause.  For a log line, never for a caller to       */
        /*                parse.                                              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool     : true when arrOut holds a complete GIF.              */
        /*--------------------------------------------------------------------*/

        public static bool GIF_TryEncode(ImageBuffer imgIn, GifEncodeOptions optIn,
                                         out byte[] arrOut, out string sReason)
        {
            GifEncodeOptions optUse;
            ImageBuffer      imgUse;
            ByteWriter       bwOut;
            byte[]           arrTable;
            byte[]           arrIndices;
            string           sExact;
            int              iTableEntries;
            int              iColourCount;
            int              iMinCodeSize;
            int              iCapacity;
            bool             bHasHoles;
            bool             bReserve;

            arrOut  = null;
            sReason = "";

            if (imgIn == null)
            {   /* Missing argument, not bad data. */
                throw new ArgumentNullException("imgIn");
            }

            if (!ImageBuffer.IMG_TryValidate(imgIn, out sReason))
            {   /* A malformed buffer; the validator says which field. */
                sReason = "GIF encode: " + sReason;
                return(false);
            }

            if (imgIn.IMG_iBitsPerSample != ImageBuffer.IMG_BITS_8)
            {   /* GIF is palettised eight-bit throughout; there is nowhere   */
                /* in the format for a sixteen-bit sample to go, and          */
                /* narrowing one would be a lossy conversion the caller did   */
                /* not ask for.                                               */
                sReason = "GIF encode: the source is " +
                          imgIn.IMG_iBitsPerSample.ToString() +
                          "-bit, and GIF carries only 8-bit samples";
                return(false);
            }

            if (imgIn.IMG_iWidth > GIF_MAX_DIMENSION ||
                imgIn.IMG_iHeight > GIF_MAX_DIMENSION)
            {   /* The logical screen fields are unsigned sixteen-bit, so a   */
                /* larger image cannot state its own size.                    */
                sReason = "GIF encode: " + imgIn.IMG_iWidth.ToString() + "x" +
                          imgIn.IMG_iHeight.ToString() +
                          " exceeds the largest screen a GIF can declare, " +
                          GIF_MAX_DIMENSION.ToString() + " per side";
                return(false);
            }

            if (optIn == null)
            {   /* Null options mean the defaults, everywhere in this library. */
                optUse = GIF_DefaultEncodeOptions();
            }
            else
            {   /* The caller's own settings. */
                optUse = optIn;
            }

            if (!GIF_TryResolveTableSize(optUse.GIFOPT_iPaletteSize, out iTableEntries,
                                         out sReason))
            {   /* A palette size outside the frozen range. */
                return(false);
            }

            /* EVERYTHING BELOW WORKS IN RGBA, which is also the shape the     */
            /* decoder produces -- so the round trip compares like with like   */
            /* and the alpha test has a channel to look at whatever the        */
            /* caller handed in.                                               */
            if (imgIn.IMG_iChannels == ImageBuffer.IMG_CHANNELS_RGBA)
            {   /* Already the shape this encoder works in. */
                imgUse = imgIn;
            }
            else if (!ImageBuffer.IMG_TryConvertChannels(imgIn,
                                                         ImageBuffer.IMG_CHANNELS_RGBA,
                                                         out imgUse, out sReason))
            {   /* A lossless widening the converter nonetheless declined. */
                sReason = "GIF encode: " + sReason;
                return(false);
            }

            if (!GIF_TryClassifyAlpha(imgUse, out bHasHoles, out sReason))
            {   /* Partial alpha, which the format cannot express. */
                return(false);
            }

            if (!GIF_TryResolveTransparency(optUse.GIFOPT_iTransparency, bHasHoles,
                                            out bReserve, out sReason))
            {   /* An unknown mode, or "none" asked of an image with holes. */
                return(false);
            }

            /* THE EXACT PALETTE IS TRIED FIRST, ALWAYS, and that ordering is   */
            /* the point rather than an optimisation.  When the image fits,     */
            /* an exact palette is LOSSLESS and no quantiser can improve on     */
            /* it -- so every file the exact path can write is written          */
            /* byte-for-byte that way, and the quantiser's error appears only   */
            /* on images the exact path cannot write at all.                    */
            /*                                                                  */
            /* GIF_TryBuildExactPalette HAS EXACTLY ONE FAILURE MODE, which     */
            /* is what makes this dispatch sound: its only "return false" is    */
            /* the too-many-colours refusal.  Its header says so, and it is     */
            /* asserted from the outside by requiring a one-colour image to     */
            /* succeed at every table size.                                     */
            if (!GIF_TryBuildExactPalette(imgUse, iTableEntries, bReserve, out arrTable,
                                          out iColourCount, out arrIndices, out sExact))
            {   /* More colours than the table can hold, so approximate them.   */
                /* GIFOPT_bDither has something to do only on this path.        */
                if (!GIF_TryQuantiseToPalette(imgUse, iTableEntries, bReserve,
                                              optUse.GIFOPT_bDither, out arrTable,
                                              out arrIndices, out sReason))
                {   /* The quantiser declined; it says why. */
                    return(false);
                }
            }

            /* THE MINIMUM CODE SIZE IS THE TABLE'S WIDTH IN BITS, floored at   */
            /* two: the format has no one-bit LZW, so even a two-entry table    */
            /* codes at two bits and simply never names indices 2 and 3.        */
            iMinCodeSize = GIF_ENCODE_MIN_WIDTH;

            while ((1 << iMinCodeSize) < iTableEntries)
            {   /* At most six more doublings, from 4 up to 256. */
                iMinCodeSize = iMinCodeSize + 1;
            }

            /* Header, screen descriptor, colour table, an optional extension   */
            /* and a descriptor, then the raster and one trailer byte.  The     */
            /* raster is the only part whose size is not known in advance, so   */
            /* one index per pixel is the estimate -- LZW usually beats it,     */
            /* and the writer grows if it does not.                             */
            iCapacity = (iTableEntries * GIF_RGB_STRIDE) + arrIndices.Length + 64;

            bwOut = new ByteWriter(iCapacity);

            GIF_WriteScreenDescriptor(bwOut, imgUse.IMG_iWidth, imgUse.IMG_iHeight,
                                      iTableEntries, arrTable);

            if (bReserve)
            {   /* Declare the reserved entry, without which it would just be   */
                /* an unused black.                                             */
                GIF_WriteGraphicControl(bwOut, 0);
            }

            GIF_WriteImageDescriptor(bwOut, imgUse.IMG_iWidth, imgUse.IMG_iHeight);

            if (!GIF_TryEncodeLzwRaster(bwOut, arrIndices, iMinCodeSize, out sReason))
            {   /* The raster writer refused; it says why. */
                return(false);
            }

            bwOut.BYTEW_WriteU8(GIF_BLOCK_TRAILER);

            arrOut = bwOut.BYTEW_ToArray();

            /* A complete single-frame GIF holding the caller's exact pixels. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /*  ANIMATION ENCODE                                                  */
        /*                                                                    */
        /*  ONE GLOBAL COLOUR TABLE SERVES EVERY FRAME, which is the first    */
        /*  decision and it shapes the rest.  The alternative -- a local      */
        /*  table per frame, which is what gif-h writes -- lets each frame    */
        /*  have its own optimal palette at the cost of up to 768 bytes per   */
        /*  frame, and it is not what GDI+ or Pillow produce.  A single       */
        /*  global table is the conventional output and the one our oracles   */
        /*  read every day.                                                   */
        /*                                                                    */
        /*  IT ALSO MAKES THE PALETTE WORK REUSE ITSELF EXACTLY.  The frames  */
        /*  are STACKED into one tall ImageBuffer and handed to the same      */
        /*  GIF_TryBuildExactPalette and GIF_TryBuildQuantPalette the still   */
        /*  encoder uses, so an animation gets the identical treatment: exact */
        /*  when the colours across ALL frames fit, adaptive when they do     */
        /*  not.  No second palette path exists to drift from the first.      */
        /*                                                                    */
        /*  THE ONE THING THE STACK MUST NOT DO IS DITHER ACROSS A FRAME      */
        /*  BOUNDARY.  Floyd-Steinberg pushes error downward, so a stacked    */
        /*  image would smear the bottom row of frame k into the top row of   */
        /*  frame k+1 -- a faint band at the top of every frame, from an      */
        /*  image region that is not adjacent to it in time or space.  The    */
        /*  stack is therefore used to CHOOSE the palette and each frame is   */
        /*  MAPPED into it separately.                                        */
        /*--------------------------------------------------------------------*/

        /*--------------------------------------------------------------------*/
        /* GIF_TryStackFrames:                                                */
        /*                                                                    */
        /* Copies every frame into one tall buffer, so the palette machinery  */
        /* can see the whole animation's colours in a single call.            */
        /*                                                                    */
        /* THE RESULT IS DELIBERATELY THE CONCATENATION IN FRAME ORDER, which */
        /* is what lets the exact path slice its index array back apart: the  */
        /* indices for frame k begin at k * width * height.  A different      */
        /* arrangement would work for the palette and would need a second     */
        /* mapping pass to undo.                                              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     frmIn    : The animation.  Already validated.  Not modified.   */
        /*     imgOut   : Receives the stacked buffer.                        */
        /*     sReason  : Receives "" or why it could not be built.           */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool     : true when imgOut holds every frame.                 */
        /*--------------------------------------------------------------------*/

        public static bool GIF_TryStackFrames(ImageFrameSet frmIn, out ImageBuffer imgOut,
                                              out string sReason)
        {
            ImageBuffer imgStack;
            ImageBuffer imgFrame;
            int         iFrames;
            int         iWidth;
            int         iHeight;
            int         iSamples;
            int         i;

            imgOut  = null;
            sReason = "";

            iFrames = frmIn.FRM_arrFrames.Length;
            iWidth  = frmIn.FRM_arrFrames[0].IMG_iWidth;
            iHeight = frmIn.FRM_arrFrames[0].IMG_iHeight;

            /* THE STACK IS HEIGHT TIMES FRAMES TALL, and on a long animation   */
            /* that can exceed the buffer validator's dimension ceiling even    */
            /* though every real frame is small.  Checked here so the failure   */
            /* names the animation rather than surfacing as a confusing         */
            /* complaint about a height nobody asked for.                       */
            if (iHeight > (ImageBuffer.IMG_MAX_DIMENSION / iFrames))
            {   /* Stacking would overflow what a buffer may describe. */
                sReason = "GIF encode: " + iFrames.ToString() + " frames of " +
                          iHeight.ToString() +
                          " rows cannot be stacked for palette selection";
                return(false);
            }

            if (!ImageBuffer.IMG_TryAllocate(iWidth, iHeight * iFrames,
                                             ImageBuffer.IMG_CHANNELS_RGBA,
                                             ImageBuffer.IMG_BITS_8, out imgStack,
                                             out sReason))
            {   /* Too large to hold; the allocator says so. */
                sReason = "GIF encode: " + sReason;
                return(false);
            }

            iSamples = iWidth * iHeight * GIF_CHANNELS;

            for (i = 0; i < iFrames; i++)
            {   /* One frame per iteration, laid end to end. */
                imgFrame = frmIn.FRM_arrFrames[i];

                Array.Copy(imgFrame.IMG_arrSamples8, 0, imgStack.IMG_arrSamples8,
                           i * iSamples, iSamples);
            }

            imgOut = imgStack;

            /* Every frame's pixels, in order, in one buffer. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* GIF_CanDeltaEncode:                                                */
        /*                                                                    */
        /* Says whether the whole animation can be written as deltas on top   */
        /* of one another, which turns on ONE question about transparency.    */
        /*                                                                    */
        /* WHY THIS IS NOT SIMPLY "does it have holes".  Delta encoding marks */
        /* an unchanged pixel TRANSPARENT and relies on disposal method 1 --  */
        /* "do not dispose" -- to leave the previous frame showing through.   */
        /* So a transparent pixel in frame N does not mean "nothing here", it */
        /* means "whatever frame N-1 left here".  That is exactly right for   */
        /* an unchanged pixel and exactly WRONG for a real hole that has just */
        /* opened: if frame N-1 was opaque at that pixel and frame N wants it */
        /* transparent, there is no way to say so, because the only mark      */
        /* available already means "keep what was there".                     */
        /*                                                                    */
        /* SO THE TEST IS: does any pixel go OPAQUE to TRANSPARENT between    */
        /* consecutive frames?  Holes that persist are fine.  Holes that      */
        /* close are fine.  Holes that OPEN are not.                          */
        /*                                                                    */
        /* WHEN IT FAILS, the caller falls back to full-canvas frames with    */
        /* disposal 2, which clears the canvas before each frame so every one */
        /* is self-contained.  Bigger, always exact.  The alternative was a   */
        /* per-frame mixture of the two, which is possible but cascades       */
        /* backwards -- a frame's disposal is written into the frame BEFORE   */
        /* it -- and the complexity buys size on an uncommon input.           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     frmIn  : The animation.  Already validated.                    */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool   : true when no pixel ever goes opaque to transparent.   */
        /*--------------------------------------------------------------------*/

        public static bool GIF_CanDeltaEncode(ImageFrameSet frmIn)
        {
            byte[] arrPrev;
            byte[] arrThis;
            int    iPixels;
            int    i;
            int    k;

            iPixels = frmIn.FRM_arrFrames[0].IMG_iWidth *
                      frmIn.FRM_arrFrames[0].IMG_iHeight;

            for (k = 1; k < frmIn.FRM_arrFrames.Length; k++)
            {   /* One frame per iteration, against its predecessor. */
                arrPrev = frmIn.FRM_arrFrames[k - 1].IMG_arrSamples8;
                arrThis = frmIn.FRM_arrFrames[k].IMG_arrSamples8;

                for (i = 0; i < iPixels; i++)
                {   /* One pixel per iteration, alpha only. */
                    if (arrThis[(i * GIF_CHANNELS) + 3] == 0 &&
                        arrPrev[(i * GIF_CHANNELS) + 3] != 0)
                    {   /* A hole opened where the previous frame drew, and   */
                        /* no delta can express that.                         */
                        return(false);
                    }
                }
            }

            /* Every frame can be written as a patch on the one before it. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* GIF_FindChangedBounds:                                             */
        /*                                                                    */
        /* Finds the smallest rectangle containing every pixel whose INDEX    */
        /* differs from the previous frame's, and rewrites the rest to the    */
        /* transparent index.                                                 */
        /*                                                                    */
        /* IT COMPARES INDICES AND NOT SOURCE COLOURS, which matters once a   */
        /* quantiser is in play: two different source colours that map to the */
        /* same palette entry produce the same output pixel, so the pixel has */
        /* not changed as far as the FILE is concerned.  Comparing the        */
        /* sources would mark it changed, write it, and gain nothing.         */
        /*                                                                    */
        /* AN EMPTY RECTANGLE IS A REAL CASE and is reported as such: two     */
        /* identical consecutive frames are perfectly legal and usually mean  */
        /* the caller wants a longer pause.  The caller writes a minimal      */
        /* one-pixel frame for it rather than nothing, because the DELAY      */
        /* lives on the frame and dropping the frame would drop the pause.    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     arrPrevIn    : The previous frame's indices.                   */
        /*     arrThisIo    : This frame's indices; unchanged pixels are      */
        /*                    rewritten to iTransparent.                      */
        /*     iWidth       : Frame width in pixels.                          */
        /*     iHeight      : Frame height in pixels.                         */
        /*     iTransparent : The index meaning "leave what was there".       */
        /*     iLeftOut     : Receives the rectangle's left edge.             */
        /*     iTopOut      : Receives its top edge.                          */
        /*     iRightOut    : Receives one past its right edge.               */
        /*     iBottomOut   : Receives one past its bottom edge.              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool         : true when anything changed at all.              */
        /*--------------------------------------------------------------------*/

        public static bool GIF_FindChangedBounds(byte[] arrPrevIn, byte[] arrThisIo,
                                                 int iWidth, int iHeight,
                                                 int iTransparent, out int iLeftOut,
                                                 out int iTopOut, out int iRightOut,
                                                 out int iBottomOut)
        {
            int  iLeft;
            int  iTop;
            int  iRight;
            int  iBottom;
            int  iAt;
            bool bAny;
            int  iX;
            int  iY;

            iLeft   = iWidth;
            iTop    = iHeight;
            iRight  = 0;
            iBottom = 0;
            bAny    = false;

            for (iY = 0; iY < iHeight; iY++)
            {   /* One row per iteration. */
                for (iX = 0; iX < iWidth; iX++)
                {   /* One pixel per iteration. */
                    iAt = (iY * iWidth) + iX;

                    if (arrThisIo[iAt] == arrPrevIn[iAt])
                    {   /* Unchanged: mark it so the previous frame shows    */
                        /* through, which is what disposal 1 leaves there.   */
                        arrThisIo[iAt] = (byte)iTransparent;
                        continue;
                    }

                    bAny = true;

                    if (iX < iLeft)
                    {   /* A new leftmost changed column. */
                        iLeft = iX;
                    }

                    if (iX >= iRight)
                    {   /* A new rightmost changed column. */
                        iRight = iX + 1;
                    }

                    if (iY < iTop)
                    {   /* A new topmost changed row. */
                        iTop = iY;
                    }

                    if (iY >= iBottom)
                    {   /* A new bottommost changed row. */
                        iBottom = iY + 1;
                    }
                }
            }

            if (!bAny)
            {   /* Nothing changed, so there is no rectangle.  Reported    */
                /* rather than returned as a degenerate one, because the   */
                /* caller has a specific answer for this case.             */
                iLeftOut   = 0;
                iTopOut    = 0;
                iRightOut  = 0;
                iBottomOut = 0;

                /* The two frames are identical. */
                return(false);
            }

            iLeftOut   = iLeft;
            iTopOut    = iTop;
            iRightOut  = iRight;
            iBottomOut = iBottom;

            /* The smallest rectangle holding every changed pixel. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* GIF_WriteLoopExtension:                                            */
        /*                                                                    */
        /* Writes the NETSCAPE2.0 application extension, which is the only    */
        /* way a GIF can say how many times to play.                          */
        /*                                                                    */
        /* IT IS NOT IN THE SPECIFICATION AT ALL -- it is a convention every  */
        /* decoder implements, including ours (GIF_TryParseApplicationExten-  */
        /* sion) and GDI+.  The shape is fixed: an eleven-byte identifier     */
        /* block, then a three-byte sub-block whose first byte is 1 and whose */
        /* next two are the count, little-endian.                             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     bwOut      : Destination.  Advanced past the extension.        */
        /*     iLoopCount : 0 for endless play, or the number of plays.       */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void       : Bytes go to bwOut.                                */
        /*--------------------------------------------------------------------*/

        public static void GIF_WriteLoopExtension(ByteWriter bwOut, int iLoopCount)
        {
            int i;

            bwOut.BYTEW_WriteU8(GIF_BLOCK_EXTENSION);
            bwOut.BYTEW_WriteU8(GIF_EXT_APPLICATION);
            bwOut.BYTEW_WriteU8(GIF_APP_ID_LENGTH);

            for (i = 0; i < GIF_APP_ID_LENGTH; i++)
            {   /* "NETSCAPE2.0", one byte at a time.  ASCII by construction,   */
                /* so no encoding is involved and none may be introduced --     */
                /* Encoding.Default differs by platform and is barred.          */
                bwOut.BYTEW_WriteU8((int)GIF_APP_NETSCAPE[i]);
            }

            bwOut.BYTEW_WriteU8(3);                      // sub-block length: the three bytes below
            bwOut.BYTEW_WriteU8(GIF_NETSCAPE_LOOP);      // sub-block id 1 means a loop count follows
            bwOut.BYTEW_WriteU16LE(iLoopCount);
            bwOut.BYTEW_WriteU8(GIF_BLOCK_TERMINATOR);
        }

        /*---------------------------------------------------------------------*/
        /* GIF_WriteFrameControl:                                              */
        /*                                                                     */
        /* Writes the Graphic Control Extension in front of one animation      */
        /* frame: its delay, its disposal method and its transparent index.    */
        /*                                                                     */
        /* THE DISPOSAL FIELD DESCRIBES THIS FRAME'S AFTERMATH, not its        */
        /* setup, and that is the single most confusable thing in GIF          */
        /* animation.  It says what to do with this frame's rectangle ONCE     */
        /* THIS FRAME HAS BEEN SHOWN -- so the value that makes the NEXT       */
        /* frame's delta work is written HERE, one frame early.                */
        /* GIF_ApplyPendingDisposal reads it from the same side.               */
        /*                                                                     */
        /* Arguments:                                                          */
        /*     bwOut             : Destination.  Advanced past the block.      */
        /*     iDelayMs          : This frame's delay, in milliseconds; a      */
        /*                         multiple of ten, checked by the caller.     */
        /*     iDisposal         : GIF_DISPOSE_KEEP or GIF_DISPOSE_BACKGROUND. */
        /*     iTransparentIndex : The index not to draw, or -1 for none.      */
        /*                                                                     */
        /* Returns:                                                            */
        /*     void              : Bytes go to bwOut.                          */
        /*---------------------------------------------------------------------*/

        public static void GIF_WriteFrameControl(ByteWriter bwOut, int iDelayMs,
                                                 int iDisposal, int iTransparentIndex)
        {
            int iPacked;
            int iIndex;

            bwOut.BYTEW_WriteU8(GIF_BLOCK_EXTENSION);
            bwOut.BYTEW_WriteU8(GIF_EXT_GRAPHIC_CONTROL);
            bwOut.BYTEW_WriteU8(GIF_GCE_PAYLOAD_SIZE);

            iPacked = (iDisposal << GIF_DISPOSAL_SHIFT) & GIF_FLAG_DISPOSAL;

            if (iTransparentIndex >= 0)
            {   /* An index is declared, so the flag must say so or a decoder   */
                /* reads it as an ordinary colour.                              */
                iPacked = iPacked | GIF_FLAG_TRANSPARENT;
                iIndex  = iTransparentIndex;
            }
            else
            {   /* No index.  The byte still has to be written, and zero is   */
                /* what every encoder puts there when the flag is clear.      */
                iIndex = 0;
            }

            bwOut.BYTEW_WriteU8(iPacked);
            bwOut.BYTEW_WriteU16LE(iDelayMs / GIF_DELAY_TO_MS);
            bwOut.BYTEW_WriteU8(iIndex);
            bwOut.BYTEW_WriteU8(GIF_BLOCK_TERMINATOR);
        }

        /*--------------------------------------------------------------------*/
        /* GIF_WriteFrameDescriptor:                                          */
        /*                                                                    */
        /* Writes an image descriptor for a frame at an arbitrary origin and  */
        /* size, which is what a cropped delta frame needs.                   */
        /*                                                                    */
        /* SEPARATE FROM GIF_WriteImageDescriptor rather than replacing it,   */
        /* because no two functions in this library may share a name, and     */
        /* the still encoder's version genuinely takes fewer                  */
        /* arguments -- a still frame is always at the origin and always      */
        /* fills the screen, and a function that cannot express otherwise is  */
        /* a small guarantee worth keeping.                                   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     bwOut    : Destination.  Advanced past the descriptor.         */
        /*     iLeft    : Frame origin, pixels from the screen's left.        */
        /*     iTop     : Frame origin, pixels from the screen's top.         */
        /*     iWidth   : Frame width in pixels.                              */
        /*     iHeight  : Frame height in pixels.                             */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void     : Bytes go to bwOut.                                  */
        /*--------------------------------------------------------------------*/

        public static void GIF_WriteFrameDescriptor(ByteWriter bwOut, int iLeft, int iTop,
                                                    int iWidth, int iHeight)
        {
            bwOut.BYTEW_WriteU8(GIF_BLOCK_IMAGE);

            bwOut.BYTEW_WriteU16LE(iLeft);
            bwOut.BYTEW_WriteU16LE(iTop);
            bwOut.BYTEW_WriteU16LE(iWidth);
            bwOut.BYTEW_WriteU16LE(iHeight);

            bwOut.BYTEW_WriteU8(0);                      // no local table, not interlaced, not sorted
        }

        /*--------------------------------------------------------------------*/
        /* GIF_CropIndices:                                                   */
        /*                                                                    */
        /* Lifts a rectangle out of a full-canvas index array.                */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     arrIn    : Indices for the whole canvas.                       */
        /*     iStride  : Canvas width in pixels.                             */
        /*     iLeft    : Rectangle's left edge.                              */
        /*     iTop     : Rectangle's top edge.                               */
        /*     iWidth   : Rectangle's width.                                  */
        /*     iHeight  : Rectangle's height.                                 */
        /*                                                                    */
        /* Returns:                                                           */
        /*     byte[]   : iWidth * iHeight indices in reading order.          */
        /*--------------------------------------------------------------------*/

        public static byte[] GIF_CropIndices(byte[] arrIn, int iStride, int iLeft,
                                             int iTop, int iWidth, int iHeight)
        {
            byte[] arrOut;
            int    iY;

            arrOut = new byte[iWidth * iHeight];

            for (iY = 0; iY < iHeight; iY++)
            {   /* One row per iteration, copied whole. */
                Array.Copy(arrIn, ((iTop + iY) * iStride) + iLeft, arrOut, iY * iWidth,
                           iWidth);
            }

            return(arrOut);
        }

        /*--------------------------------------------------------------------*/
        /* GIF_TryCheckFrames:                                                */
        /*                                                                    */
        /* Walks every frame checking what the format can carry, before any   */
        /* palette work is done.                                              */
        /*                                                                    */
        /* THE DELAY CHECK IS THE ONE WORTH ARGUING ABOUT, and it refuses.    */
        /* GIF measures delay in HUNDREDTHS of a second, so 105 milliseconds  */
        /* cannot be written -- it would become 100, and a round trip would   */
        /* silently return a different animation from the one handed in.      */
        /* That is the same silent lossy narrowing this library refuses for   */
        /* pixels: BMP will not drop an alpha channel, JPEG will not take     */
        /* one, and GIF_TryClassifyAlpha will not snap a half-transparent     */
        /* pixel.  A delay is smaller in consequence and identical in kind.   */
        /* The reason string says exactly what to pass instead, which is what */
        /* makes a refusal usable rather than merely correct.                 */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     frmIn         : The animation.  Already validated.             */
        /*     bHasHolesOut  : Receives true when any frame has a fully       */
        /*                     transparent pixel.                             */
        /*     sReason       : Receives "" or which frame was refused.        */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool          : true when every frame can be written.          */
        /*--------------------------------------------------------------------*/

        public static bool GIF_TryCheckFrames(ImageFrameSet frmIn, out bool bHasHolesOut,
                                              out string sReason)
        {
            ImageBuffer imgFrame;
            bool        bFrameHoles;
            int         i;

            bHasHolesOut = false;
            sReason      = "";

            for (i = 0; i < frmIn.FRM_arrFrames.Length; i++)
            {   /* One frame per iteration. */
                imgFrame = frmIn.FRM_arrFrames[i];

                if (imgFrame.IMG_iBitsPerSample != ImageBuffer.IMG_BITS_8)
                {   /* GIF is palettised eight-bit throughout. */
                    sReason = "GIF encode: frame " + i.ToString() + " is " +
                              imgFrame.IMG_iBitsPerSample.ToString() +
                              "-bit, and GIF carries only 8-bit samples";
                    return(false);
                }

                if (imgFrame.IMG_iChannels != ImageBuffer.IMG_CHANNELS_RGBA)
                {   /* THE ANIMATION PATH DOES NOT CONVERT CHANNELS, unlike     */
                    /* the still one, and refusing is the honest answer         */
                    /* rather than a limitation quietly worked around.  Every   */
                    /* frame would have to be converted and the converted       */
                    /* copies held for the whole encode, which doubles the      */
                    /* peak memory of an animation for a case that does not     */
                    /* arise: an ImageFrameSet comes from GIF_TryDecodeFrames   */
                    /* or is built by a caller who chose the shape, and both    */
                    /* produce RGBA.                                            */
                    sReason = "GIF encode: frame " + i.ToString() + " has " +
                              imgFrame.IMG_iChannels.ToString() +
                              " channels; an animation's frames must all be RGBA";
                    return(false);
                }

                if (!GIF_TryClassifyAlpha(imgFrame, out bFrameHoles, out sReason))
                {   /* Partial alpha, which the format cannot express. */
                    sReason = "GIF encode: frame " + i.ToString() + ": " + sReason;
                    return(false);
                }

                if (bFrameHoles)
                {   /* One frame with a hole is enough to need an index. */
                    bHasHolesOut = true;
                }

                if (frmIn.FRM_arrDelaysMs[i] < 0)
                {   /* A negative delay has no encoding. */
                    sReason = "GIF encode: frame " + i.ToString() + " has a delay of " +
                              frmIn.FRM_arrDelaysMs[i].ToString() +
                              " ms, which is negative";
                    return(false);
                }

                if ((frmIn.FRM_arrDelaysMs[i] % GIF_DELAY_TO_MS) != 0)
                {   /* See this function's header: refused rather than         */
                    /* rounded, because rounding would change the animation.   */
                    sReason = "GIF encode: frame " + i.ToString() + " asks for " +
                              frmIn.FRM_arrDelaysMs[i].ToString() +
                              " ms, and GIF measures delay in hundredths of a second; " +
                              "use a multiple of " + GIF_DELAY_TO_MS.ToString() + " ms";
                    return(false);
                }

                if ((frmIn.FRM_arrDelaysMs[i] / GIF_DELAY_TO_MS) > GIF_MAX_DIMENSION)
                {   /* The delay field is unsigned sixteen-bit, same ceiling   */
                    /* as the screen dimensions.                               */
                    sReason = "GIF encode: frame " + i.ToString() + " asks for " +
                              frmIn.FRM_arrDelaysMs[i].ToString() +
                              " ms, which exceeds the largest delay a GIF can declare";
                    return(false);
                }
            }

            /* Every frame is a shape and a timing the format can carry. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* GIF_SliceFrameIndices:                                             */
        /*                                                                    */
        /* Splits the stacked index array back into one array per frame.      */
        /*                                                                    */
        /* THE COPY IS DELIBERATE rather than handing out offsets into the    */
        /* stack: the delta pass REWRITES unchanged pixels to the transparent */
        /* index, and doing that in a shared buffer would corrupt the next    */
        /* frame's comparison against this one.                               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     arrAllIn  : Indices for every frame, laid end to end.          */
        /*     iFrames   : How many frames are in it.                         */
        /*     iPixels   : Pixels per frame.                                  */
        /*                                                                    */
        /* Returns:                                                           */
        /*     byte[][]  : One index array per frame.                         */
        /*--------------------------------------------------------------------*/

        public static byte[][] GIF_SliceFrameIndices(byte[] arrAllIn, int iFrames,
                                                     int iPixels)
        {
            byte[][] arrOut;
            int      i;

            arrOut = new byte[iFrames][];

            for (i = 0; i < iFrames; i++)
            {   /* One frame per iteration. */
                arrOut[i] = new byte[iPixels];

                Array.Copy(arrAllIn, i * iPixels, arrOut[i], 0, iPixels);
            }

            return(arrOut);
        }

        /*--------------------------------------------------------------------*/
        /* GIF_TryQuantiseFrames:                                             */
        /*                                                                    */
        /* Builds one adaptive palette over the whole animation, then maps    */
        /* each frame into it SEPARATELY.                                     */
        /*                                                                    */
        /* THE SEPARATION IS THE POINT, and it is why this does not simply    */
        /* call GIF_TryQuantiseToPalette on the stack.  Floyd-Steinberg       */
        /* diffuses error downward and rightward; on a stacked image the      */
        /* bottom row of frame k would push its error into the top row of     */
        /* frame k+1, which is not adjacent to it in space or in time.  The   */
        /* result is a faint band across the top of every frame after the     */
        /* first.  Choosing the palette from the stack is unaffected -- the   */
        /* split only cares which colours are present -- so the stack is used */
        /* for that and each frame is mapped on its own.                      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     frmIn          : The animation.                                */
        /*     imgStackIn     : Every frame's pixels in one buffer.           */
        /*     iTableEntries  : Entries the table will carry.                 */
        /*     bReserve       : true when index 0 is the transparent index.   */
        /*     bDither        : true applies Floyd-Steinberg per frame.       */
        /*     arrTableOut    : Receives the wire-form colour table.          */
        /*     arrFramesOut   : Receives one index array per frame.           */
        /*     sReason        : Receives "" or why it was refused.            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool           : true when every frame was mapped.             */
        /*--------------------------------------------------------------------*/

        public static bool GIF_TryQuantiseFrames(ImageFrameSet frmIn,
                                                 ImageBuffer imgStackIn,
                                                 int iTableEntries, bool bReserve,
                                                 bool bDither, out byte[] arrTableOut,
                                                 out byte[][] arrFramesOut,
                                                 out string sReason)
        {
            GifQuantPalette pal;
            byte[][]        arrFrames;
            int             iPixels;
            int             i;

            arrTableOut  = null;
            arrFramesOut = null;

            if (!GIF_TryBuildQuantPalette(imgStackIn, iTableEntries, bReserve, bDither,
                                          out pal, out sReason))
            {   /* The palette builder declined; it says why. */
                return(false);
            }

            iPixels   = frmIn.FRM_arrFrames[0].IMG_iWidth *
                        frmIn.FRM_arrFrames[0].IMG_iHeight;
            arrFrames = new byte[frmIn.FRM_arrFrames.Length][];

            for (i = 0; i < frmIn.FRM_arrFrames.Length; i++)
            {   /* One frame per iteration, mapped on its own. */
                arrFrames[i] = new byte[iPixels];

                if (bDither)
                {   /* Diffuse each pixel's error into its undecided   */
                    /* neighbours, within this frame only.             */
                    GIF_QuantiseDither(frmIn.FRM_arrFrames[i], pal, arrFrames[i]);
                }
                else
                {   /* Decide every pixel on its own. */
                    GIF_QuantiseThreshold(frmIn.FRM_arrFrames[i], pal, arrFrames[i]);
                }
            }

            arrTableOut  = GIF_BuildQuantTable(pal, iTableEntries);
            arrFramesOut = arrFrames;

            /* One palette for the animation, every frame mapped into it. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* GIF_TryWriteOneFrame:                                              */
        /*                                                                    */
        /* Writes one animation frame: its control extension, its descriptor  */
        /* and its raster, cropped to the changed region when delta encoding. */
        /*                                                                    */
        /* THE DISPOSAL VALUE IS THE WHOLE DIFFERENCE BETWEEN THE TWO FORMS.  */
        /* Delta frames carry GIF_DISPOSE_KEEP, so each frame stays on the    */
        /* canvas for the next one to patch.  Full-canvas frames carry        */
        /* GIF_DISPOSE_BACKGROUND, which clears the canvas to transparent     */
        /* before the next frame draws and makes each one self-contained --   */
        /* real readers were MEASURED to clear disposal 2 to                  */
        /* transparent rather than to the background colour the specification */
        /* names, and this library's decoder does the same.                   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     bwOut         : Destination.  Advanced past the frame.         */
        /*     frmIn         : The animation, for the delay.                  */
        /*     arrFramesIo   : Per-frame indices; the delta pass rewrites     */
        /*                     unchanged pixels in the current frame's array. */
        /*     iFrame        : Which frame to write.                          */
        /*     iWidth        : Canvas width in pixels.                        */
        /*     iHeight       : Canvas height in pixels.                       */
        /*     bDelta        : true crops to the changed region.              */
        /*     bReserve      : true when index 0 is the transparent index.    */
        /*     iMinCodeSize  : The LZW minimum code size for the table.       */
        /*     sReason       : Receives "" or why the frame was refused.      */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool          : true when the frame was written.               */
        /*--------------------------------------------------------------------*/

        public static bool GIF_TryWriteOneFrame(ByteWriter bwOut, ImageFrameSet frmIn,
                                                byte[][] arrFramesIo, int iFrame,
                                                int iWidth, int iHeight, bool bDelta,
                                                bool bReserve, int iMinCodeSize,
                                                out string sReason)
        {
            byte[] arrRaster;
            int    iLeft;
            int    iTop;
            int    iRight;
            int    iBottom;
            int    iDisposal;
            int    iTransparent;

            sReason = "";

            if (bReserve)
            {   /* Index 0 is declared and must not be drawn. */
                iTransparent = 0;
            }
            else
            {   /* No index is declared at all. */
                iTransparent = -1;
            }

            if (bDelta)
            {   /* Each frame stays on the canvas for the next to patch. */
                iDisposal = GIF_DISPOSE_KEEP;
            }
            else
            {   /* The canvas is cleared before the next frame, so every   */
                /* frame is self-contained.                                */
                iDisposal = GIF_DISPOSE_BACKGROUND;
            }

            iLeft   = 0;
            iTop    = 0;
            iRight  = iWidth;
            iBottom = iHeight;

            if (bDelta && iFrame > 0)
            {   /* Crop to what actually changed, and mark the rest   */
                /* transparent so the previous frame shows through.   */
                if (!GIF_FindChangedBounds(arrFramesIo[iFrame - 1], arrFramesIo[iFrame],
                                           iWidth, iHeight, iTransparent, out iLeft,
                                           out iTop, out iRight, out iBottom))
                {   /* NOTHING CHANGED, which is legal and usually means the   */
                    /* caller wants a longer pause.  The frame still has to    */
                    /* exist, because the DELAY lives on it -- dropping the    */
                    /* frame would drop the pause.  One transparent pixel is   */
                    /* the smallest thing that carries a delay.                */
                    iLeft   = 0;
                    iTop    = 0;
                    iRight  = 1;
                    iBottom = 1;
                }
            }

            GIF_WriteFrameControl(bwOut, frmIn.FRM_arrDelaysMs[iFrame], iDisposal,
                                  iTransparent);

            GIF_WriteFrameDescriptor(bwOut, iLeft, iTop, iRight - iLeft, iBottom - iTop);

            if (iLeft == 0 && iTop == 0 && iRight == iWidth && iBottom == iHeight)
            {   /* The whole canvas, so the index array is already the   */
                /* raster and no copy is needed.                         */
                arrRaster = arrFramesIo[iFrame];
            }
            else
            {   /* A cropped rectangle out of the canvas. */
                arrRaster = GIF_CropIndices(arrFramesIo[iFrame], iWidth, iLeft, iTop,
                                            iRight - iLeft, iBottom - iTop);
            }

            if (!GIF_TryEncodeLzwRaster(bwOut, arrRaster, iMinCodeSize, out sReason))
            {   /* The raster writer refused; it says why. */
                return(false);
            }

            /* One complete frame: control, descriptor and raster. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* GIF_TryEncodeFrames:                                               */
        /*                                                                    */
        /* THE PUBLIC ENTRY POINT FOR ANIMATION.  Writes an ImageFrameSet as  */
        /* a multi-frame GIF.                                                 */
        /*                                                                    */
        /* THE FRAMES ARRIVE FULLY COMPOSITED and are written back as a       */
        /* patch sequence, which is the inverse of what GIF_TryDecodeFrames   */
        /* does.  A caller never sees the patch form in either direction, so  */
        /* building it is this function's job and the round trip through the  */
        /* decoder is what proves it right.                                   */
        /*                                                                    */
        /* TWO ENCODINGS, CHOSEN BY ONE PROPERTY OF THE ANIMATION:            */
        /*                                                                    */
        /*   DELTA, when no pixel ever goes opaque to transparent between     */
        /*   consecutive frames.  Frame 0 covers the screen; every later      */
        /*   frame is cropped to the rectangle that changed and marks         */
        /*   unchanged pixels with the transparent index, and every frame     */
        /*   carries disposal 1 so the one before it stays on the canvas.     */
        /*                                                                    */
        /*   FULL CANVAS, otherwise.  Every frame covers the screen and       */
        /*   carries disposal 2, which clears the canvas before the next one  */
        /*   draws, so each frame is self-contained.  Larger, always exact.   */
        /*   GIF_CanDeltaEncode's header explains why the delta form cannot   */
        /*   express a hole that OPENS.                                       */
        /*                                                                    */
        /* GIF_TRANSPARENCY_NONE FORCES THE SECOND FORM, because delta        */
        /* encoding has nothing to mark unchanged pixels with.  That is a     */
        /* SIZE cost rather than a correctness one: the animation is still    */
        /* exactly right, it is merely larger.                                */
        /*                                                                    */
        /* THE LOOP COUNT: THE OPTION IS READ LITERALLY, AND ONLY THE         */
        /* SENTINEL DEFERS TO THE FRAME SET.  Both ImageFrameSet.             */
        /* FRM_iLoopCount and GifEncodeOptions.GIFOPT_iLoopCount exist and    */
        /* mean the number of plays, and the frozen surface did not say which */
        /* wins until it was amended to.                                      */
        /*                                                                    */
        /* THE DEFAULT IS STILL THE FRAME SET and the reason is unchanged: a  */
        /* round trip must not silently alter the count, because the decoder  */
        /* puts it into FRM_iLoopCount and an encoder that ignored it would   */
        /* turn "play 7 times" into "play forever" on every re-encode.        */
        /*                                                                    */
        /* WHAT CHANGED IS THE HOLE THE FIRST RULE LEFT.  It honoured the     */
        /* option "only when non-zero", so a caller who deliberately wrote 0  */
        /* wanting an infinite loop was silently ignored -- and zero is a     */
        /* GIF's commonest intent, so the one value the option could not      */
        /* express was the one most often wanted.  GIFOPT_LOOP_FROM_FRAMES    */
        /* carries "defer" explicitly and frees 0 to mean what it says.       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     frmIn    : The animation.  Not modified.  Must not be null.    */
        /*     optIn    : Encode options, or null for the defaults.           */
        /*     arrOut   : Receives the complete file, or null on failure.     */
        /*     sReason  : Receives "" on success, or a short human-readable   */
        /*                cause.                                              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool     : true when arrOut holds a complete animated GIF.     */
        /*--------------------------------------------------------------------*/

        public static bool GIF_TryEncodeFrames(ImageFrameSet frmIn, GifEncodeOptions optIn,
                                               out byte[] arrOut, out string sReason)
        {
            GifEncodeOptions optUse;
            ImageBuffer      imgStack;
            ByteWriter       bwOut;
            byte[]           arrTable;
            byte[]           arrAll;
            byte[][]         arrFrameIndices;
            string           sExact;
            int              iTableEntries;
            int              iColourCount;
            int              iMinCodeSize;
            int              iFrames;
            int              iWidth;
            int              iHeight;
            int              iLoopCount;
            bool             bHasHoles;
            bool             bReserve;
            bool             bDelta;
            int              i;

            arrOut  = null;
            sReason = "";

            if (frmIn == null)
            {   /* Missing argument, not bad data. */
                throw new ArgumentNullException("frmIn");
            }

            if (!ImageFrameSet.FRM_TryValidate(frmIn, out sReason))
            {   /* The set is internally inconsistent; the validator says how. */
                sReason = "GIF encode: " + sReason;
                return(false);
            }

            iFrames = frmIn.FRM_arrFrames.Length;
            iWidth  = frmIn.FRM_arrFrames[0].IMG_iWidth;
            iHeight = frmIn.FRM_arrFrames[0].IMG_iHeight;

            if (iWidth > GIF_MAX_DIMENSION || iHeight > GIF_MAX_DIMENSION)
            {   /* The logical screen fields are unsigned sixteen-bit. */
                sReason = "GIF encode: " + iWidth.ToString() + "x" + iHeight.ToString() +
                          " exceeds the largest screen a GIF can declare, " +
                          GIF_MAX_DIMENSION.ToString() + " per side";
                return(false);
            }

            if (optIn == null)
            {   /* Null options mean the defaults, everywhere in this library. */
                optUse = GIF_DefaultEncodeOptions();
            }
            else
            {   /* The caller's own settings. */
                optUse = optIn;
            }

            if (!GIF_TryResolveTableSize(optUse.GIFOPT_iPaletteSize, out iTableEntries,
                                         out sReason))
            {   /* A palette size outside the frozen range. */
                return(false);
            }

            if (!GIF_TryCheckFrames(frmIn, out bHasHoles, out sReason))
            {   /* A frame is the wrong shape, or carries partial alpha, or   */
                /* asks for a delay GIF cannot express.                       */
                return(false);
            }

            if (optUse.GIFOPT_iLoopCount != GIFOPT_LOOP_FROM_FRAMES &&
                (optUse.GIFOPT_iLoopCount < 0 ||
                 optUse.GIFOPT_iLoopCount > GIF_MAX_LOOP_COUNT))
            {   /* GIFOPT_LOOP_FROM_FRAMES is a NAMED constant, not a rule    */
                /* that negatives mean "default", so -2 is a caller mistake   */
                /* and is said out loud.  The upper bound is the NETSCAPE     */
                /* extension's own field width -- the count goes out as two   */
                /* little-endian bytes and there is nowhere to put more.      */
                sReason = "GIF encode: loop count " +
                          optUse.GIFOPT_iLoopCount.ToString() +
                          " is outside 0.." + GIF_MAX_LOOP_COUNT.ToString() +
                          " and is not GIFOPT_LOOP_FROM_FRAMES";
                return(false);
            }

            if (!GIF_TryResolveTransparency(optUse.GIFOPT_iTransparency, bHasHoles,
                                            out bReserve, out sReason))
            {   /* An unknown mode, or "none" asked of frames with holes. */
                return(false);
            }

            /* DELTA ENCODING NEEDS AN INDEX TO MARK UNCHANGED PIXELS WITH,   */
            /* so a single frame never bothers and GIF_TRANSPARENCY_NONE      */
            /* rules it out entirely.  AUTO reserves an index for an          */
            /* animation even when no frame has a hole, which is section      */
            /* 5.5.4's "or when animating and delta encoding would pay".      */
            bDelta = (iFrames > 1) && GIF_CanDeltaEncode(frmIn);

            if (bDelta && optUse.GIFOPT_iTransparency != GIF_TRANSPARENCY_NONE)
            {   /* Delta encoding is on, so an index is required whether or   */
                /* not the frames themselves have holes.                      */
                bReserve = true;
            }
            else
            {   /* Either the frames cannot be delta encoded or the caller   */
                /* forbade the index that would let them be.                 */
                bDelta = false;
            }

            if (!GIF_TryStackFrames(frmIn, out imgStack, out sReason))
            {   /* Too large to stack for palette selection. */
                return(false);
            }

            /* THE PALETTE IS CHOSEN OVER EVERY FRAME AT ONCE, exactly as the   */
            /* still encoder chooses one over a single image -- same call,      */
            /* same exact-first rule.  A per-frame palette would need a local   */
            /* colour table per frame, which is what this file's header says    */
            /* we do not write.                                                 */
            if (GIF_TryBuildExactPalette(imgStack, iTableEntries, bReserve, out arrTable,
                                         out iColourCount, out arrAll, out sExact))
            {   /* Every colour in the whole animation fits, so the frames   */
                /* are LOSSLESS and the stacked index array is already the   */
                /* per-frame arrays laid end to end.                         */
                arrFrameIndices = GIF_SliceFrameIndices(arrAll, iFrames, iWidth * iHeight);
            }
            else if (!GIF_TryQuantiseFrames(frmIn, imgStack, iTableEntries, bReserve,
                                            optUse.GIFOPT_bDither, out arrTable,
                                            out arrFrameIndices, out sReason))
            {   /* The quantiser declined; it says why. */
                return(false);
            }

            iMinCodeSize = GIF_ENCODE_MIN_WIDTH;

            while ((1 << iMinCodeSize) < iTableEntries)
            {   /* At most six more doublings, from 4 up to 256. */
                iMinCodeSize = iMinCodeSize + 1;
            }

            if (optUse.GIFOPT_iLoopCount == GIFOPT_LOOP_FROM_FRAMES)
            {   /* The default: the animation speaks for itself, so a round   */
                /* trip cannot silently change the count.                     */
                iLoopCount = frmIn.FRM_iLoopCount;
            }
            else
            {   /* Any value in range is taken LITERALLY, zero included --    */
                /* which is how a caller says "loop forever" and is the one   */
                /* thing the old non-zero rule could not express.             */
                iLoopCount = optUse.GIFOPT_iLoopCount;
            }

            bwOut = new ByteWriter((iTableEntries * GIF_RGB_STRIDE) +
                                   (iWidth * iHeight * iFrames) + 128);

            GIF_WriteScreenDescriptor(bwOut, iWidth, iHeight, iTableEntries, arrTable);

            if (iLoopCount != 1)
            {   /* NO EXTENSION MEANS PLAY ONCE, which is what a count of one   */
                /* asks for -- so writing one would be redundant, and our own   */
                /* decoder reports the absence as exactly that.  Zero means     */
                /* forever and anything above one is a real repeat count;       */
                /* both need saying.                                            */
                GIF_WriteLoopExtension(bwOut, iLoopCount);
            }

            for (i = 0; i < iFrames; i++)
            {   /* One frame per iteration. */
                if (!GIF_TryWriteOneFrame(bwOut, frmIn, arrFrameIndices, i, iWidth,
                                          iHeight, bDelta, bReserve, iMinCodeSize,
                                          out sReason))
                {   /* The frame writer refused; it says why. */
                    return(false);
                }
            }

            bwOut.BYTEW_WriteU8(GIF_BLOCK_TRAILER);

            arrOut = bwOut.BYTEW_ToArray();

            /* A complete animated GIF. */
            return(true);
        }
    }
}
