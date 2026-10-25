/*----------------------------------------------------------------------------*/
/* SPDX-License-Identifier: MIT OR GPL-2.0-or-later                           */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* Dual-licensed at the recipient's choice -- see LICENSE in this folder for  */
/* the full terms of both licences.  GPL-2.0 full text in GPL-2.0.txt.        */
/*----------------------------------------------------------------------------*/
/* JpegCodec.cs -- JPEG decode                                     (JPEG_)    */
/*----------------------------------------------------------------------------*/
/* Transcoded from stb_image.h v2.30 (nothings/stb, commit 2c980bb5), the     */
/* JPEG decode path at lines 1914 to 4090.  Structure and variable roles are  */
/* kept so this file can be diffed against its source when it misbehaves;     */
/* naming and commentary follow this portfolio's rules on top.                */
/*                                                                            */
/* stb is dual-licensed MIT / public domain, which satisfies both arms of     */
/* this file's licence.                                                       */
/*                                                                            */
/* WHY THE CURRENT stb AND NOT ITS TEN-YEAR-OLD VERSION.  The same clone      */
/* carries deprecated/stb_image.c v1.35, whose JPEG decoder is 1018 lines     */
/* against this one's 2177 and which the maintainer notes is easier to        */
/* extract from.  It is also BASELINE ONLY -- its own header says "JPEG       */
/* baseline (no JPEG progressive)" and its SOF handler refuses 0xC2 outright  */
/* -- and this library supports progressive.  Starting there                  */
/* would buy a smaller first draft and then need the same functions           */
/* transcoded a second time.  The size gap is also smaller than it looks:     */
/* 641 of the current file's lines are SSE2 and NEON kernels that C# discards */
/* anyway, leaving about 1536 scalar lines.                                   */
/*----------------------------------------------------------------------------*/
/* TWO DELIBERATE DEVIATIONS FROM stb, AND BOTH ARE libjpeg-turbo's.          */
/*                                                                            */
/*   1. THE LAST COLUMN OF JPEG_ResampleRowH2.  stb's horizontal chroma       */
/*      upsampler ends its row with                                           */
/*                                                                            */
/*          out[2*(w-1)] = (3 * in[w-2] + in[w-1] + 2) >> 2                   */
/*                                                                            */
/*      weighting the FARTHER of the two samples by three.  Every other       */
/*      output in the same function -- the first column and both halves of    */
/*      every interior pair -- weights the NEARER one, so the file            */
/*      contradicts itself at exactly one place.  This library writes         */
/*                                                                            */
/*          out[2*(w-1)] = (3 * in[w-1] + in[w-2] + 1) >> 2                   */
/*                                                                            */
/*      which is libjpeg-turbo 3.1.4's h2v1_fancy_upsample (src/jdsample.c).  */
/*                                                                            */
/* IT IS PROVABLY WRONG RATHER THAN MERELY DIFFERENT.  A chroma filter is     */
/* not prescribed by T.81, so two decoders may legitimately disagree -- but   */
/* stb's form turns a MONOTONIC input into a NON-MONOTONIC output, which no   */
/* interpolation may do.  On {0, 100, 200} it yields                          */
/*                                                                            */
/*      0, 25, 75, 125, 125, 200      stb -- the fifth sample falls back      */
/*      0, 25, 75, 125, 175, 200      libjpeg, and this file                  */
/*                                                                            */
/* stb v2.30 and the decade-old v1.35 in the same clone are byte-identical    */
/* here, so no upstream fix was missed.  row_hv_2 (the 4:2:0 path) is CLEAN   */
/* -- it copies each end's vertical blend rather than using a special         */
/* formula -- which is why 4:2:0 always measured better than 4:2:2.           */
/*                                                                            */
/*   2. THE INTERIOR ROUNDING BIAS, ADOPTED 2026-09-15.  libjpeg adds 1 to    */
/*      the LEFT output of each interpolated pair and 2 to the RIGHT, so a    */
/*      value landing exactly halfway rounds DOWN at even output positions    */
/*      and UP at odd ones -- its own comment calls that "a simple ordered    */
/*      dither pattern".  stb adds 2 to both, and so did this file until      */
/*      the difference was measured.                                          */
/*                                                                            */
/* THE SECOND ONE WAS SETTLED BY MEASUREMENT, NOT BY LINEAGE.  Three variants */
/* were run against 30 4:2:2 reference images with the tolerances at zero,    */
/* worst single sample against GDI+:                                          */
/*                                                                            */
/*      stb, uniform +2               4 photographs    3 synthetics           */
/*      libjpeg, +1 even / +2 odd     3 photographs    1 synthetic            */
/*                                                                            */
/* The whole distribution moves down about a count, and 4:2:0 held at 3 and   */
/* 21 because row_hv_2 is a different function and was not touched -- the     */
/* falsifiable control.  GDI+'s decoder is therefore measurably closer to     */
/* libjpeg's rounding than to stb's, which is unsurprising once said, since   */
/* Microsoft's JPEG lineage traces to IJG -- but it was not knowable from     */
/* reading either source.                                                     */
/*                                                                            */
/* A TRANSCRIBED FILE KEEPS ITS SOURCE'S STRUCTURE so it can be diffed        */
/* against that source when it misbehaves, so a deliberate divergence from    */
/* the declared lineage is recorded here rather than absorbed quietly.  The   */
/* authority for both changes above is libjpeg-turbo tag 3.1.4, commit        */
/* e352b02f, its src/jdsample.c -- read as a reference, never copied from.    */
/*----------------------------------------------------------------------------*/
/* WHAT THIS FILE DOES: JPEG IN BOTH DIRECTIONS, COMPLETE.                    */
/*                                                                            */
/* Decode reads baseline and progressive, grayscale and colour, 4:4:4, 4:2:2, */
/* 4:2:0 and 4:1:1, plus the four-component corners -- CMYK and YCCK behind   */
/* an Adobe APP14, and the raw-RGB case where the component ids are 'R', 'G'  */
/* and 'B'.  Encode writes baseline grayscale and colour at 4:4:4, 4:2:2 and  */
/* 4:2:0, at any geometry.                                                    */
/*                                                                            */
/* THE CONTAINER LAYER CAME FIRST AND IS STILL THE SPINE OF THE FILE:         */
/* markers, segment framing, the frame header (SOF), the quantisation tables  */
/* (DQT), the Huffman tables (DHT, built into decode tables), the restart     */
/* interval (DRI), and skipping the application and comment segments.  It     */
/* reports what a file CLAIMS to be and refuses files whose claims are        */
/* impossible, before a single pixel is produced.                             */
/*                                                                            */
/* PNG's chunk layer was built the same way and for the same reason: the      */
/* container layer is where a malformed file is caught, it is testable        */
/* without any pixel machinery at all, and everything above it is a           */
/* consumer of the tables it builds.                                          */
/*----------------------------------------------------------------------------*/
/* HOW A JPEG IS LAID OUT, because the rest of this file assumes it.          */
/*                                                                            */
/* A JPEG is a sequence of MARKERS.  A marker is 0xFF followed by a byte that */
/* is neither 0x00 nor 0xFF; the value names what follows.  Most markers      */
/* introduce a SEGMENT, whose first two bytes are a big-endian length that    */
/* INCLUDES THOSE TWO BYTES and excludes the marker itself -- so a segment    */
/* carrying N bytes of payload declares N+2.  Getting that inclusive length   */
/* wrong by two is the classic JPEG parsing bug and it desynchronises         */
/* everything after it.                                                       */
/*                                                                            */
/* Four markers carry no length at all: SOI, EOI, and the eight RSTn restart  */
/* markers.  They are two bytes and nothing more.                             */
/*                                                                            */
/* A run of 0xFF bytes before a marker is legal FILL and is skipped.  That is */
/* why JPEG_TryReadMarker loops rather than reading exactly two bytes.        */
/*----------------------------------------------------------------------------*/

using System;

namespace GraphicsEncDec
{
    /*------------------------------------------------------------------------*/
    /* JpegComponent:                                                         */
    /* One colour component as the frame header declares it.  A grayscale     */
    /* image has one, a colour image three, and a CMYK or YCCK image four.    */
    /*                                                                        */
    /* THE SAMPLING FACTORS ARE THE WHOLE REASON THIS TYPE EXISTS.  Every     */
    /* component may be stored at a different resolution -- that is what      */
    /* chroma subsampling IS -- and the factors are RELATIVE, not absolute.   */
    /* A 4:2:0 image has luma at h=2,v=2 and both chroma at h=1,v=1, which    */
    /* means chroma is stored at half width and half height.  Nothing in the  */
    /* file states "4:2:0"; that name is a reading of these four numbers.     */
    /*------------------------------------------------------------------------*/

    public sealed class JpegComponent
    {
        public int JPEGC_iId;                            // component identifier byte from SOF; 1,2,3 by convention but not by rule
        public int JPEGC_iHorizontalFactor;              // 1 to 4; relative horizontal sampling
        public int JPEGC_iVerticalFactor;                // 1 to 4; relative vertical sampling
        public int JPEGC_iQuantTable;                    // 0 to 3; which DQT table dequantises this component
    }

    /*------------------------------------------------------------------------*/
    /* JpegHeader:                                                            */
    /* Everything the container layer learns about a file, which is           */
    /* everything needed to decide whether to decode it and how much memory   */
    /* that would take.                                                       */
    /*------------------------------------------------------------------------*/

    public sealed class JpegHeader
    {
        /*--------------------------------------------------------------------*/
        /* Straight from the frame header (SOF).                              */
        /*--------------------------------------------------------------------*/

        public int  JPEGH_iWidth;                        // pixels across, at least 1
        public int  JPEGH_iHeight;                       // pixel rows, at least 1
        public int  JPEGH_iPrecision;                    // bits per sample; only 8 is supported
        public int  JPEGH_iComponentCount;               // 1 grayscale, 3 colour, 4 CMYK or YCCK
        public bool JPEGH_bProgressive;                  // true when the frame marker was SOF2

        public JpegComponent[] JPEGH_arrComponents;      // exactly JPEGH_iComponentCount entries

        /*--------------------------------------------------------------------*/
        /* Derived from the sampling factors.  Pure header arithmetic, kept   */
        /* here because every later stage needs it and the derivation is a    */
        /* small thing that is easy to get subtly wrong.                      */
        /*--------------------------------------------------------------------*/

        public int JPEGH_iMaxHorizontalFactor;           // largest h across the components
        public int JPEGH_iMaxVerticalFactor;             // largest v across the components
        public int JPEGH_iMcuWidth;                      // pixels across one MCU: max h times 8
        public int JPEGH_iMcuHeight;                     // pixel rows in one MCU: max v times 8
        public int JPEGH_iMcuCountX;                     // MCUs across the image, rounded up
        public int JPEGH_iMcuCountY;                     // MCU rows in the image, rounded up

        /*--------------------------------------------------------------------*/
        /* Gathered from the other header segments.                           */
        /*--------------------------------------------------------------------*/

        public int  JPEGH_iRestartInterval;              // MCUs between RSTn markers; 0 means none (no DRI)
        public bool JPEGH_bHasJfif;                      // an APP0 segment tagged "JFIF\0" was seen
        public int  JPEGH_iAdobeTransform;               // Adobe APP14 colour transform: -1 none, else 0, 1 or 2
        public int  JPEGH_iRgbComponentIds;              // of a 3-component frame, how many components are named 'R', 'G', 'B' in that order
    }

    /*------------------------------------------------------------------------*/
    /* JpegScanHeader:                                                        */
    /* What one SOS segment says: which components this scan carries, which   */
    /* Huffman tables each of them uses, and which coefficients it covers.    */
    /*                                                                        */
    /* WHY THIS IS SEPARATE FROM JpegHeader, which is the thing to understand */
    /* before using either.  A frame header describes the IMAGE and there is  */
    /* one of them.  A scan header describes ONE PASS over some of that       */
    /* image's components, and a file may contain many -- a progressive JPEG  */
    /* is nothing but a stack of scans over the same coefficients.  So the    */
    /* per-component quantisation table lives on JpegComponent, where it      */
    /* belongs to the frame, while the DC and AC HUFFMAN table indices live   */
    /* here, because a later scan may legitimately use different ones for the */
    /* same component.  Putting the Huffman indices on JpegComponent would    */
    /* work for every baseline file and quietly break on the first            */
    /* progressive one.                                                       */
    /*                                                                        */
    /* JPEGS_arrComponentIndex IS AN INDIRECTION AND NOT A COMPONENT ID.  SOS */
    /* names components by the id byte SOF gave them, which is conventionally */
    /* 1, 2, 3 but is not required to be; this array holds the INDEX into     */
    /* JpegHeader.JPEGH_arrComponents that each id resolved to, in the order  */
    /* the scan interleaves them.  Confusing the id with the index is the     */
    /* classic way to decode a file whose components are numbered oddly into  */
    /* colour-swapped rubbish.                                                */
    /*------------------------------------------------------------------------*/

    public sealed class JpegScanHeader
    {
        public int   JPEGS_iComponentCount;              // 1 to 4; how many components this scan carries
        public int[] JPEGS_arrComponentIndex;            // per scanned component: index into JPEGH_arrComponents, in interleave order
        public int[] JPEGS_arrDcTable;                   // per scanned component: which of the four DC Huffman tables it uses
        public int[] JPEGS_arrAcTable;                   // per scanned component: which of the four AC Huffman tables it uses

        /*--------------------------------------------------------------------*/
        /* The spectral selection and successive approximation, which are     */
        /* meaningful only for progressive scans.  A baseline scan MUST say   */
        /* 0, 63, 0, 0 and is refused otherwise -- those four bytes are where */
        /* a progressive file wearing a baseline frame marker gives itself    */
        /* away, so they are validated rather than skipped.                   */
        /*--------------------------------------------------------------------*/

        public int JPEGS_iSpectralStart;                 // Ss: first coefficient in zig-zag order this scan codes
        public int JPEGS_iSpectralEnd;                   // Se: last one; 63 for a baseline scan
        public int JPEGS_iApproxHigh;                    // Ah: the bit position the previous scan for these coefficients stopped at
        public int JPEGS_iApproxLow;                     // Al: the bit position this scan codes down to
    }

    /*------------------------------------------------------------------------*/
    /* JpegProgressiveState:                                                  */
    /* The coefficients of a whole progressive frame, plus the one piece of   */
    /* entropy state that outlives a single block.                            */
    /*                                                                        */
    /* WHY THIS EXISTS AT ALL, AND IT IS THE WHOLE OF WHAT MAKES PROGRESSIVE  */
    /* DIFFERENT.  A baseline scan codes every block once and completely, so  */
    /* the coefficients can be a local that dies with the block.  A           */
    /* progressive frame spreads the SAME coefficients over several scans --  */
    /* one carries the DC at reduced precision, later ones add bands of AC    */
    /* and extra low-order bits -- so the buffer has to belong to the FRAME   */
    /* and every scan refines what the previous ones left.  That is the       */
    /* structural change; the block decoders themselves are small.            */
    /*                                                                        */
    /* THE COEFFICIENTS HERE ARE RAW, NOT DEQUANTISED, which is the opposite  */
    /* of the baseline path.  JPEG_TryDecodeBlock multiplies by the           */
    /* quantisation table as it scatters, because it sees the block once and  */
    /* is finished with it.  A progressive block is revisited, and            */
    /* multiplying on the first visit would then have every later refinement  */
    /* adding un-scaled bits to scaled ones.  So dequantisation moves to the  */
    /* end, in JPEG_DequantizeProgressive, once no scan can touch the block   */
    /* again.  They ARE already in natural order -- the scatter through the   */
    /* dezigzag table happens per coefficient as it is decoded, exactly as    */
    /* baseline does it.                                                      */
    /*                                                                        */
    /* JPEGPG_arrBlocksX IS MCU-ALIGNED FOR EVERY COMPONENT, EVEN THOUGH A    */
    /* NON-INTERLEAVED SCAN WALKS FEWER BLOCKS THAN THAT.  A progressive file */
    /* mixes the two interleave modes over one buffer: the DC scans are       */
    /* interleaved and MCU-aligned, the AC scans are per-component and are    */
    /* not.  One buffer cannot have two shapes, so it takes the larger, and   */
    /* a non-interleaved scan walks its own smaller extent while indexing at  */
    /* the MCU-aligned STRIDE.  Walking and striding by the same number is    */
    /* the classic way to decode a progressive file into a picture that       */
    /* shears progressively further right the further down it goes.           */
    /*                                                                        */
    /* JPEGPG_iEobRun IS A COUNT OF BLOCKS, NOT OF COEFFICIENTS.  An          */
    /* end-of-band run says "this band is empty in the next N blocks too",    */
    /* so it is consumed one whole block at a time and legitimately spans     */
    /* many of them.  It does NOT survive a restart marker or a scan          */
    /* boundary -- both reset it, and forgetting that silently swallows the   */
    /* first blocks after every restart.                                      */
    /*------------------------------------------------------------------------*/

    public sealed class JpegProgressiveState
    {
        public short[][][] JPEGPG_arrCoefficients;       // per component, per block, 64 raw coefficients in natural order
        public int[]       JPEGPG_arrBlocksX;            // per component: MCU-aligned block columns, and the buffer's stride
        public int[]       JPEGPG_arrBlocksY;            // per component: MCU-aligned block rows
        public int         JPEGPG_iEobRun;               // blocks still covered by the current end-of-band run; 0 when none
    }

    /*------------------------------------------------------------------------*/
    /* JpegResamplerState:                                                    */
    /* Everything one component needs while its rows are being upsampled to   */
    /* the image's full size.                                                 */
    /*                                                                        */
    /* WHY THIS IS STATE AND NOT ARGUMENTS.  Upsampling is not a per-row      */
    /* function of the row: producing output row j needs to know WHICH source */
    /* rows straddle it and WHICH of the two is nearer, and that walks        */
    /* forward at a different rate for every component -- a 4:2:0 chroma      */
    /* plane advances one source row for every two output rows, while luma    */
    /* advances one for one.  The counters below are that walk.               */
    /*                                                                        */
    /* JPEGRS_iYStep IS A PHASE, NOT A POSITION.  It counts 0, 1, ... up to   */
    /* JPEGRS_iVs and then wraps, and its value decides which of the two      */
    /* source rows is the near one.  Starting it at vs >> 1 rather than at    */
    /* zero is deliberate and is the source's choice: it centres the output   */
    /* rows between the source rows instead of aligning them to the top,      */
    /* which is what stops a subsampled image drifting half a chroma row      */
    /* upward against its luma.                                               */
    /*------------------------------------------------------------------------*/

    public sealed class JpegResamplerState
    {
        public int    JPEGRS_iHs;                        // horizontal expansion: h_max / this component's h
        public int    JPEGRS_iVs;                        // vertical expansion: v_max / this component's v
        public int    JPEGRS_iYStep;                     // phase within the vertical expansion, 0 to Vs-1
        public int    JPEGRS_iYPos;                      // which source row line1 currently points at
        public int    JPEGRS_iLine0;                     // offset of the first of the two straddling source rows
        public int    JPEGRS_iLine1;                     // offset of the second
        public int    JPEGRS_iWidthLores;                // this component's own width in samples, before expansion
        public int    JPEGRS_iHeightLores;               // and its own height, which bounds the row walk
        public int    JPEGRS_iStride;                    // samples per row of the component's plane
        public int    JPEGRS_iKind;                      // which resampler: one of JPEG_RESAMPLE_*
        public byte[] JPEGRS_arrLineBuffer;              // scratch for one expanded row
    }

    /*--------------------------------------------------------------------------*/
    /* JpegHuffmanTable:                                                        */
    /* One Huffman table, in the decode-ready form stb builds rather than in    */
    /* the form DHT stores.                                                     */
    /*                                                                          */
    /* DHT STORES A HISTOGRAM, NOT A TREE, and that is the thing to             */
    /* understand about this type.  The segment gives sixteen counts -- how     */
    /* many codes are one bit long, how many are two bits long, and so on --    */
    /* followed by the symbol values in code order.  The CODES THEMSELVES       */
    /* ARE NOT STORED because they are completely determined by the counts:     */
    /* the specification's algorithm assigns them canonically, shortest         */
    /* first, incrementing and shifting left at each length.  So the table      */
    /* has to be RECONSTRUCTED on load, which is what                           */
    /* JPEG_TryBuildHuffmanTable does.                                          */
    /*                                                                          */
    /* JPEGHT_arrFastAc IS A SECOND ACCELERATOR AND IT IS NOT THE SAME THING    */
    /* AS JPEGHT_arrFast.  The plain fast table turns a peek at the next nine   */
    /* bits into a SYMBOL.  In an AC scan a symbol is only half the work: the   */
    /* symbol names a run length and a magnitude WIDTH, and the coefficient     */
    /* itself is the next few bits after the code.  The fast-AC table folds     */
    /* both halves into one lookup, so the common case -- a short code with a   */
    /* small coefficient -- costs one array read and no bit-by-bit walking at   */
    /* all.  Each entry packs three things, or is zero when the pair does not   */
    /* fit in nine bits and the slow path must run:                             */
    /*                                                                          */
    /*     bits 15..8  the signed coefficient, already sign-extended, -128..127 */
    /*     bits  7..4  the run of zero coefficients that precedes it, 0..15     */
    /*     bits  3..0  total bits to consume: code length plus magnitude bits   */
    /*                                                                          */
    /* Built only for AC tables, so it is NULL on a DC table and a decoder      */
    /* that consults it on one has confused its table classes.                  */
    /*--------------------------------------------------------------------------*/

    public sealed class JpegHuffmanTable
    {
        public byte[] JPEGHT_arrFast;                    // JPEG_HUFFMAN_FAST_SIZE entries: symbol index, or 255 for "not accelerated"
        public int[]  JPEGHT_arrCode;                    // the canonical code for each symbol, in DHT order
        public byte[] JPEGHT_arrValues;                  // the symbol values, straight from DHT
        public byte[] JPEGHT_arrSize;                    // code length in bits for each symbol; a 0 terminator follows the last
        public uint[] JPEGHT_arrMaxCode;                 // per length 1..16: first code too large for that length, pre-shifted to bit 16
        public int[]  JPEGHT_arrDelta;                   // per length 1..16: add to a code to get its symbol index
        public int    JPEGHT_iSymbolCount;               // how many symbols the table defines, 0 to 256
        public short[] JPEGHT_arrFastAc;                 // AC TABLES ONLY: see the note below.  Null on a DC table.
    }

    /*------------------------------------------------------------------------*/
    /* JpegBitReader:                                                         */
    /* The entropy-coded bit stream: a sliding 32-bit window over the bytes   */
    /* between SOS and the next marker, and the four ways a JPEG decoder      */
    /* takes bits out of it.                                                  */
    /*                                                                        */
    /* Transcoded from stb_image.h v2.30 (commit 2c980bb5): the buffer refill */
    /* from stbi__grow_buffer_unsafe, the Huffman walk from                   */
    /* stbi__jpeg_huff_decode, and the three bit-taking calls from            */
    /* stbi__extend_receive, stbi__jpeg_get_bits and stbi__jpeg_get_bit.      */
    /*                                                                        */
    /* WHY THE WINDOW IS LEFT-ALIGNED, because nothing else here makes sense  */
    /* until that is clear.  Incoming bytes are packed into the TOP of a      */
    /* 32-bit register and consumed from the top downward, so the next bit to */
    /* be read is always bit 31.  That is what lets a Huffman code of unknown */
    /* length be compared against a table without shifting first -- the       */
    /* comparison value is pre-shifted to match instead, once, when the table */
    /* is built.  A right-aligned buffer would need a shift per candidate     */
    /* length on every symbol.                                                */
    /*                                                                        */
    /* TWO BYTES ARE NOT DATA, AND BOTH MATTER:                               */
    /*                                                                        */
    /*   0xFF 0x00  a STUFFED literal.  Entropy data can produce a 0xFF byte, */
    /*              which would otherwise look like the start of a marker, so */
    /*              the encoder writes a zero after it.  The 0xFF is data;    */
    /*              the 0x00 is not and is dropped.                           */
    /*   0xFF <m>   a real MARKER, where m is neither 0x00 nor 0xFF.  The     */
    /*              entropy stream has ended.  The marker is REMEMBERED and   */
    /*              NOT CONSUMED from the stream's point of view, and the     */
    /*              reader stalls: from here it hands out zero bits forever.  */
    /*                                                                        */
    /* THE STALL IS NOT AN ERROR AND MUST NOT BE MADE ONE.  A JPEG's last     */
    /* entropy byte is padded with one-bits and the marker follows            */
    /* immediately, so the final MCU of a perfectly good file routinely asks  */
    /* for bits that are not there.  Refusing at that point would reject      */
    /* files every other decoder reads.  What IS an error is running out of   */
    /* BYTES without ever meeting a marker, which means the file was cut      */
    /* short -- and the two are distinguished here, which stb does not do.    */
    /*------------------------------------------------------------------------*/

    public sealed class JpegBitReader
    {
        /*--------------------------------------------------------------------*/
        /* State.  Public fields rather than properties, matching ImageBuffer */
        /* and the rest of this library.                                      */
        /*--------------------------------------------------------------------*/

        public ByteReader JPEGBR_brSource;               // the bytes of the entropy segment
        public uint       JPEGBR_uCodeBuffer;            // the window; next bit to read is bit 31
        public int        JPEGBR_iCodeBits;              // how many of the window's bits are loaded, real or padding
        public int        JPEGBR_iRealBits;              // how many of those came from actual file bytes
        public int        JPEGBR_iMarker;                // the marker that ended the stream, or JPEG_MARKER_NONE
        public bool       JPEGBR_bStalled;               // true once a marker was met; the window now yields zeroes
        public bool       JPEGBR_bTruncated;             // true if the BYTES ran out with no marker -- a broken file

        /*--------------------------------------------------------------------*/
        /* WHY THERE ARE TWO BIT COUNTS, which is not in the source and was   */
        /* added because a test caught its absence.                           */
        /*                                                                    */
        /* The refill always tops the window up past 24 bits, padding with    */
        /* zeroes when the data has run out. That means a read never SEES the */
        /* shortage -- it asks for eight bits, the window has thirty-two, and */
        /* it is handed padding as though it were data. stb behaves exactly   */
        /* this way and cannot do otherwise, because its byte source returns  */
        /* zeroes past the end and the two cases are indistinguishable to it. */
        /*                                                                    */
        /* Counting the REAL bits separately makes them distinguishable: a    */
        /* request that reaches past them, in a stream that never met a       */
        /* marker, is reading a file that was cut short and says so. In a     */
        /* stream that DID meet a marker the same request is reading the      */
        /* encoder's padding and is perfectly normal, which is why the        */
        /* refusal is conditioned on truncation and not on the shortfall.     */
        /*--------------------------------------------------------------------*/

        /*--------------------------------------------------------------------*/
        /* THE REFILL THRESHOLD.  grow keeps pulling until more than 24 bits  */
        /* are buffered, which guarantees at least 25 and at most 32.  Why 24 */
        /* and not 32: a byte is added at bit position (24 - code_bits), so   */
        /* code_bits must stay at or below 24 for the shift to be in range.   */
        /* The loop condition is therefore the same number as the shift, and  */
        /* changing one without the other silently corrupts the window.       */
        /*--------------------------------------------------------------------*/

        private const int JPEGBR_REFILL_CEILING = 24;
        private const int JPEGBR_WINDOW_BITS    = 32;
        private const int JPEGBR_MAX_CODE_BITS  = 16;    // longest Huffman code, and widest magnitude field, the format allows

        /*--------------------------------------------------------------------*/
        /* Masks of n low bits, indexed by n.  A table rather than            */
        /* (1 << n) - 1 because n reaches 16 and the expression is evaluated  */
        /* in the innermost loop of the whole decoder; stb tabulates it for   */
        /* the same reason and the values are checkable by eye.               */
        /*--------------------------------------------------------------------*/

        private static readonly uint[] JPEGBR_arrBitMask = new uint[]
        {
            0, 1, 3, 7, 15, 31, 63, 127, 255, 511, 1023, 2047, 4095, 8191,
            16383, 32767, 65535
        };

        /*---------------------------------------------------------------------*/
        /* THE SIGN BIAS, and it is the subtlest table in the JPEG decoder.    */
        /*                                                                     */
        /* A coefficient is stored as a LENGTH and then that many raw bits.    */
        /* The specification's EXTEND procedure turns those raw bits into a    */
        /* signed value by a rule that is not two's complement: if the TOP bit */
        /* of the raw value is SET the value is positive and is itself; if the */
        /* top bit is CLEAR the value is negative and is v - 2^n + 1.          */
        /*                                                                     */
        /* So bias[n] is (-1 << n) + 1, added only when the top bit is clear.  */
        /* At n = 1 the two possible values are -1 and 1; there is no zero,    */
        /* because a zero coefficient is coded as a length of zero and never   */
        /* reaches here.                                                       */
        /*                                                                     */
        /* NOTE FOR ANYONE DIFFING THIS AGAINST stb: ITS COMMENT ON THIS IS    */
        /* BACKWARDS.  The line reads "0 if MSB clear (positive), 1 if MSB set */
        /* (negative)", and the code beneath it does the opposite -- and the   */
        /* code is right.  A transcription that trusted the comment would      */
        /* invert the sign of every negative coefficient in every image, which */
        /* is a solid-looking picture with its contrast inside out.            */
        /*---------------------------------------------------------------------*/

        private static readonly int[] JPEGBR_arrSignBias = new int[]
        {
            0, -1, -3, -7, -15, -31, -63, -127, -255, -511, -1023, -2047,
            -4095, -8191, -16383, -32767
        };

        /*--------------------------------------------------------------------*/
        /* JpegBitReader:                                                     */
        /*                                                                    */
        /* Starts a bit reader over the entropy data beginning at the source  */
        /* reader's current position.                                         */
        /*                                                                    */
        /* THE SOURCE IS SHARED, NOT COPIED.  The caller's ByteReader is      */
        /* advanced as bits are consumed, so when the stream stalls on a      */
        /* marker the caller can carry on parsing from where the entropy data */
        /* stopped.  That is how a multi-scan progressive file works,         */
        /* and it is why this takes the reader rather than a byte array.      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     brSource  : Reader positioned at the first entropy byte, which */
        /*                 is immediately after the SOS segment. Must not be  */
        /*                 null.                                              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     Nothing; the instance is ready to read bits.                   */
        /*--------------------------------------------------------------------*/

        public JpegBitReader(ByteReader brSource)
        {
            if (brSource == null)
            {   /* Missing argument, not bad data. */
                throw new ArgumentNullException("brSource");
            }

            JPEGBR_brSource   = brSource;
            JPEGBR_uCodeBuffer = 0u;
            JPEGBR_iCodeBits   = 0;
            JPEGBR_iRealBits   = 0;
            JPEGBR_iMarker     = JpegCodec.JPEG_MARKER_NONE;
            JPEGBR_bStalled    = false;
            JPEGBR_bTruncated  = false;
        }

        /*--------------------------------------------------------------------*/
        /* JPEGBR_Reset:                                                      */
        /*                                                                    */
        /* Throws away the buffered bits and clears the stall, which is what  */
        /* a restart marker requires.                                         */
        /*                                                                    */
        /* RSTn MARKERS EXIST TO LET A DECODER RESYNCHRONISE, and the price   */
        /* is that everything in flight is abandoned at one: partially        */
        /* buffered bits are discarded rather than carried across, because    */
        /* the encoder padded to a byte boundary before writing the marker    */
        /* and those pad bits are not data. The DC predictors reset too, but  */
        /* those belong to the block decoder and are not this                 */
        /* type's business.                                                   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void  : The reader is ready to read from the next byte.        */
        /*--------------------------------------------------------------------*/

        public void JPEGBR_Reset()
        {
            JPEGBR_uCodeBuffer = 0u;
            JPEGBR_iCodeBits   = 0;
            JPEGBR_iRealBits   = 0;
            JPEGBR_iMarker     = JpegCodec.JPEG_MARKER_NONE;
            JPEGBR_bStalled    = false;
        }

        /*--------------------------------------------------------------------*/
        /* JPEGBR_GrowBuffer:                                                 */
        /*                                                                    */
        /* Pulls bytes from the source until more than 24 bits are buffered,  */
        /* handling byte stuffing and stopping at a marker.                   */
        /*                                                                    */
        /* Transcoded from stbi__grow_buffer_unsafe. Its name says "unsafe"   */
        /* because it does no bounds checking at all -- stb's byte reader     */
        /* returns 0 past the end of the data and the function relies on      */
        /* that. Ours cannot: ByteReader refuses a read past its window, so   */
        /* the exhausted case is detected and recorded here instead of being  */
        /* papered over, which is the one deliberate difference.              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void  : The window holds 25 to 32 valid bits, or the reader    */
        /*             has stalled and the window is padded with zeroes.      */
        /*--------------------------------------------------------------------*/

        private void JPEGBR_GrowBuffer()
        {
            int  iByte;
            int  iNext;
            bool bReal;

            do
            {   /* One byte per iteration until the window is full enough. */
                bReal = true;

                if (JPEGBR_bStalled)
                {   /* Past the marker: the stream is over and zero bits are   */
                    /* handed out for as long as anyone keeps asking.          */
                    iByte = 0;
                    bReal = false;
                }
                else if (!JPEGBR_brSource.BYTER_TryReadU8(out iByte))
                {   /* The DATA ran out and no marker was ever seen, which      */
                    /* means the file was cut short. Recorded rather than       */
                    /* silently zero-filled, which is where this differs from   */
                    /* the source.                                              */
                    JPEGBR_bTruncated = true;
                    JPEGBR_bStalled   = true;
                    iByte             = 0;
                    bReal             = false;
                }
                else if (iByte == 0xFF)
                {   /* Either a stuffed literal or the end of the stream. */
                    if (!JPEGBR_brSource.BYTER_TryReadU8(out iNext))
                    {   /* A trailing 0xFF with nothing after it. */
                        JPEGBR_bTruncated = true;
                        JPEGBR_bStalled   = true;
                        iByte             = 0;
                        bReal             = false;
                    }
                    else
                    {
                        while (iNext == 0xFF)
                        {   /* A run of 0xFF before a marker is legal fill. */
                            if (!JPEGBR_brSource.BYTER_TryReadU8(out iNext))
                            {   /* Fill that never reaches a marker. */
                                JPEGBR_bTruncated = true;
                                iNext             = 0;
                                break;
                            }
                        }

                        if (JPEGBR_bTruncated)
                        {   /* Fell out of the fill loop at the end of data. */
                            JPEGBR_bStalled = true;
                            iByte           = 0;
                            bReal           = false;
                        }
                        else if (iNext != 0x00)
                        {   /* A real marker. The entropy stream ends HERE:   */
                            /* the marker is remembered for the caller and    */
                            /* the 0xFF is NOT data, so nothing is added to   */
                            /* the window and the loop ends immediately --    */
                            /* leaving the window short on purpose.           */
                            JPEGBR_iMarker  = iNext;
                            JPEGBR_bStalled = true;
                            return;
                        }

                        /* Otherwise 0xFF 0x00: the 0xFF IS data and the zero   */
                        /* is dropped. iByte is already 0xFF.                   */
                    }
                }

                /* Pack the byte into the top of the window, just below   */
                /* whatever is already there.                             */
                JPEGBR_uCodeBuffer = JPEGBR_uCodeBuffer |
                                     ((uint)iByte << (JPEGBR_REFILL_CEILING - JPEGBR_iCodeBits));
                JPEGBR_iCodeBits   = JPEGBR_iCodeBits + 8;

                if (bReal)
                {   /* Only bytes that genuinely came out of the file count   */
                    /* toward the real-bit tally; padding does not.           */
                    JPEGBR_iRealBits = JPEGBR_iRealBits + 8;
                }
            }
            while (JPEGBR_iCodeBits <= JPEGBR_REFILL_CEILING);
        }

        /*--------------------------------------------------------------------*/
        /* JPEGBR_TryDecodeHuffman:                                           */
        /*                                                                    */
        /* Reads one Huffman-coded symbol.                                    */
        /*                                                                    */
        /* Transcoded from stbi__jpeg_huff_decode. TWO PATHS, and the fast    */
        /* one carries almost all the traffic:                                */
        /*                                                                    */
        /*   THE FAST PATH takes the top nine bits of the window as an index  */
        /*   into a table built at DHT time, which names the symbol outright  */
        /*   for any code of nine bits or fewer. One indexed read, no loop.   */
        /*                                                                    */
        /*   THE SLOW PATH runs only for longer codes. It walks lengths from  */
        /*   ten upward comparing the window's top sixteen bits against       */
        /*   maxcode[k] -- the first code too large to be k bits, PRE-SHIFTED */
        /*   when the table was built so no shifting happens here. The first  */
        /*   length whose maxcode exceeds the window is the code's length.    */
        /*                                                                    */
        /* A CODE THAT MATCHES NOTHING IS A CORRUPT FILE, not a stall, and is */
        /* refused. stb returns -1 and lets the caller decide; this library's */
        /* contract is a bool and a reason, which also removes the trap of a  */
        /* caller forgetting that -1 is not a symbol.                         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     tblIn        : The table to decode against, from DHT.          */
        /*     iSymbolOut   : Receives the symbol value, 0 to 255.            */
        /*     sReason      : Receives "" or why the code was rejected.       */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool         : true when a symbol was decoded.                 */
        /*--------------------------------------------------------------------*/

        public bool JPEGBR_TryDecodeHuffman(JpegHuffmanTable tblIn, out int iSymbolOut,
                                            out string sReason)
        {
            uint uTop;
            int  iFastIndex;
            int  iEntry;
            int  iLength;
            int  iSymbolIndex;

            iSymbolOut = 0;
            sReason    = "";

            if (tblIn == null)
            {   /* A scan naming a Huffman table the file never defined. That   */
                /* is a malformed file rather than a caller bug, so it is a     */
                /* reason and not an exception.                                 */
                sReason = "JPEG: the scan uses a Huffman table that was never defined";
                return(false);
            }

            if (JPEGBR_iCodeBits < 16)
            {   /* Top up so a full-length code is certainly present. */
                JPEGBR_GrowBuffer();
            }

            /* The fast table is indexed by the top JPEG_HUFFMAN_FAST_BITS   */
            /* bits of the window.                                           */
            iFastIndex = (int)((JPEGBR_uCodeBuffer >> (JPEGBR_WINDOW_BITS -
                                                       JpegCodec.JPEG_HUFFMAN_FAST_BITS)) &
                               (uint)(JpegCodec.JPEG_HUFFMAN_FAST_SIZE - 1));
            iEntry     = (int)tblIn.JPEGHT_arrFast[iFastIndex];

            if (iEntry < JpegCodec.JPEG_HUFFMAN_NOT_FAST)
            {   /* Resolved in one lookup. */
                iLength = (int)tblIn.JPEGHT_arrSize[iEntry];

                if (!JPEGBR_TryCheckRealBits(iLength, "a Huffman code", out sReason))
                {   /* The code would be read out of a truncated file's   */
                    /* padding.                                           */
                    return(false);
                }

                if (iLength > JPEGBR_iCodeBits)
                {   /* The window does not actually hold this whole code,   */
                    /* which can only happen at the very end of a stalled   */
                    /* stream where the remaining bits are padding.         */
                    return(JPEGBR_TryReportStarved("a Huffman code", out sReason));
                }

                JPEGBR_uCodeBuffer = JPEGBR_uCodeBuffer << iLength;
                JPEGBR_iCodeBits   = JPEGBR_iCodeBits - iLength;
                JPEGBR_SpendRealBits(iLength);

                iSymbolOut = (int)tblIn.JPEGHT_arrValues[iEntry];

                /* The common case, and the reason the table exists. */
                return(true);
            }

            /*----------------------------------------------------------------*/
            /* The slow path: a code longer than the fast table covers.       */
            /*----------------------------------------------------------------*/

            uTop = JPEGBR_uCodeBuffer >> 16;

            for (iLength = JpegCodec.JPEG_HUFFMAN_FAST_BITS + 1; ; iLength++)
            {   /* The first length whose maxcode exceeds the window is the   */
                /* code's length. Index 17 holds 0xFFFFFFFF, so this always   */
                /* terminates.                                                */
                if (uTop < tblIn.JPEGHT_arrMaxCode[iLength])
                {   /* Found it. */
                    break;
                }
            }

            if (iLength == 17)
            {   /* Past every real length: the bits match no code in the      */
                /* table at all. stb consumes sixteen bits here so a caller   */
                /* that ignores the error still advances; this refuses        */
                /* outright, so nothing downstream can act on a non-symbol.   */
                sReason = "JPEG: the entropy stream holds a bit pattern that matches no code " +
                          "in the Huffman table, at byte " +
                          JPEGBR_brSource.BYTER_Position().ToString();
                return(false);
            }

            if (!JPEGBR_TryCheckRealBits(iLength, "a long Huffman code", out sReason))
            {   /* The code would be read out of a truncated file's padding. */
                return(false);
            }

            if (iLength > JPEGBR_iCodeBits)
            {   /* As above: only reachable in the padding of a stalled   */
                /* stream.                                                */
                return(JPEGBR_TryReportStarved("a long Huffman code", out sReason));
            }

            /* The code's numeric value plus the table's per-length offset is   */
            /* the symbol's index. An addition instead of a search, which is    */
            /* the whole purpose of the delta table.                            */
            iSymbolIndex = (int)((JPEGBR_uCodeBuffer >> (JPEGBR_WINDOW_BITS - iLength)) &
                                 JPEGBR_arrBitMask[iLength]) +
                           tblIn.JPEGHT_arrDelta[iLength];

            if (iSymbolIndex < 0 || iSymbolIndex >= tblIn.JPEGHT_iSymbolCount)
            {   /* The arithmetic landed outside the symbols this table       */
                /* actually defines. stb bounds-checks against a fixed 256;   */
                /* checking against the REAL count is tighter and catches a   */
                /* corrupt stream that stb would resolve to an undefined      */
                /* entry.                                                     */
                sReason = "JPEG: a Huffman code resolved to symbol index " +
                          iSymbolIndex.ToString() + ", but the table defines only " +
                          tblIn.JPEGHT_iSymbolCount.ToString() + " symbols";
                return(false);
            }

            JPEGBR_iCodeBits   = JPEGBR_iCodeBits - iLength;
            JPEGBR_uCodeBuffer = JPEGBR_uCodeBuffer << iLength;
            JPEGBR_SpendRealBits(iLength);

            iSymbolOut = (int)tblIn.JPEGHT_arrValues[iSymbolIndex];

            /* A code longer than the fast table, resolved by the walk. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* JPEGBR_TryReceiveExtend:                                           */
        /*                                                                    */
        /* Reads n raw bits and turns them into a signed coefficient by the   */
        /* specification's EXTEND rule.                                       */
        /*                                                                    */
        /* Transcoded from stbi__extend_receive, which fuses the              */
        /* specification's separate RECEIVE and EXTEND steps because baseline */
        /* JPEG always does both together.                                    */
        /*                                                                    */
        /* THE RULE IS NOT TWO'S COMPLEMENT and that is the thing to hold on  */
        /* to. With n bits read as a plain unsigned value v: if the TOP bit   */
        /* of v is set the coefficient is v; if it is clear the coefficient   */
        /* is v - 2^n + 1, a negative number. So n = 3 covers -7..-4 and      */
        /* 4..7, with nothing in between -- the small values live at smaller  */
        /* n, and zero is never coded at all.                                 */
        /*                                                                    */
        /* See JPEGBR_arrSignBias for the note about stb's comment on this    */
        /* being inverted relative to its own code.                           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iBits      : How many bits the coefficient occupies, 0 to 16.  */
        /*     iValueOut  : Receives the signed coefficient.                  */
        /*     sReason    : Receives "" or why the read failed.               */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool       : true when a value was produced.                   */
        /*--------------------------------------------------------------------*/

        public bool JPEGBR_TryReceiveExtend(int iBits, out int iValueOut, out string sReason)
        {
            uint uRotated;
            int  iSignBit;
            int  iRaw;

            iValueOut = 0;
            sReason   = "";

            if (iBits < 0 || iBits > 16)
            {   /* A length no coefficient can have. Reached only from a        */
                /* corrupt Huffman symbol, so it is data rather than a caller   */
                /* bug.                                                         */
                sReason = "JPEG: a coefficient claims to occupy " + iBits.ToString() +
                          " bits, which is outside 0 to 16";
                return(false);
            }

            if (iBits == 0)
            {   /* Zero bits is a zero coefficient and consumes nothing. The   */
                /* rotate below would be a no-op anyway, but saying so here    */
                /* keeps the shift arithmetic away from the zero case, where   */
                /* C#'s shift-count masking would make a 32-bit shift behave   */
                /* as a 0-bit one.                                             */
                return(true);
            }

            if (JPEGBR_iCodeBits < iBits)
            {   /* Top up. */
                JPEGBR_GrowBuffer();
            }

            if (!JPEGBR_TryCheckRealBits(iBits, "a coefficient", out sReason))
            {   /* Reading past the real data of a truncated file. */
                return(false);
            }

            if (JPEGBR_iCodeBits < iBits)
            {   /* Still short: the stream has ended under us. */
                return(JPEGBR_TryReportStarved("a coefficient", out sReason));
            }

            /* The sign is decided by the FIRST of the n bits, which is bit   */
            /* 31 of the left-aligned window.                                 */
            iSignBit = (int)(JPEGBR_uCodeBuffer >> 31);

            /* Rotating left by n brings those n bits down to the bottom and   */
            /* carries the rest of the window round behind them, so one        */
            /* operation both extracts and consumes.                           */
            uRotated = (JPEGBR_uCodeBuffer << iBits) |
                       (JPEGBR_uCodeBuffer >> (JPEGBR_WINDOW_BITS - iBits));

            JPEGBR_uCodeBuffer = uRotated & ~JPEGBR_arrBitMask[iBits];
            iRaw               = (int)(uRotated & JPEGBR_arrBitMask[iBits]);
            JPEGBR_iCodeBits   = JPEGBR_iCodeBits - iBits;
            JPEGBR_SpendRealBits(iBits);

            if (iSignBit != 0)
            {   /* Top bit set: the value is positive and stands as it is. */
                iValueOut = iRaw;
            }
            else
            {   /* Top bit clear: the value is negative and the bias applies. */
                iValueOut = iRaw + JPEGBR_arrSignBias[iBits];
            }

            /* A signed coefficient, ready for dequantisation. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* JPEGBR_TryGetBits:                                                 */
        /*                                                                    */
        /* Reads n raw bits as an unsigned value, with no sign rule applied.  */
        /*                                                                    */
        /* Used by progressive decoding, where successive                     */
        /* approximation reads correction bits that mean exactly what they    */
        /* say. Baseline never calls it.                                      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iBits      : How many bits to take, 0 to 16.                   */
        /*     iValueOut  : Receives the unsigned value.                      */
        /*     sReason    : Receives "" or why the read failed.               */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool       : true when a value was produced.                   */
        /*--------------------------------------------------------------------*/

        public bool JPEGBR_TryGetBits(int iBits, out int iValueOut, out string sReason)
        {
            uint uRotated;

            iValueOut = 0;
            sReason   = "";

            if (iBits < 0 || iBits > 16)
            {   /* Outside what any JPEG field asks for. */
                sReason = "JPEG: a raw bit read of " + iBits.ToString() +
                          " bits is outside 0 to 16";
                return(false);
            }

            if (iBits == 0)
            {   /* Nothing to read, and the rotate is skipped for the reason   */
                /* given in JPEGBR_TryReceiveExtend.                           */
                return(true);
            }

            if (JPEGBR_iCodeBits < iBits)
            {   /* Top up. */
                JPEGBR_GrowBuffer();
            }

            if (!JPEGBR_TryCheckRealBits(iBits, "raw bits", out sReason))
            {   /* Reading past the real data of a truncated file. */
                return(false);
            }

            if (JPEGBR_iCodeBits < iBits)
            {   /* Still short: the stream has ended under us. */
                return(JPEGBR_TryReportStarved("raw bits", out sReason));
            }

            uRotated = (JPEGBR_uCodeBuffer << iBits) |
                       (JPEGBR_uCodeBuffer >> (JPEGBR_WINDOW_BITS - iBits));

            JPEGBR_uCodeBuffer = uRotated & ~JPEGBR_arrBitMask[iBits];
            iValueOut          = (int)(uRotated & JPEGBR_arrBitMask[iBits]);
            JPEGBR_iCodeBits   = JPEGBR_iCodeBits - iBits;
            JPEGBR_SpendRealBits(iBits);

            /* n bits, unsigned, exactly as they appear. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* JPEGBR_TryGetBit:                                                  */
        /*                                                                    */
        /* Reads one bit.                                                     */
        /*                                                                    */
        /* RETURNS A bool WHERE stb RETURNS AN int, and the difference is     */
        /* worth stating. Its version hands back the masked sign bit --       */
        /* 0x80000000 or 0 -- which every caller then uses as a truth value.  */
        /* In C# that value does not fit an int as a positive number, so a    */
        /* faithful transcription would return a large negative number for    */
        /* "one" and invite exactly the arithmetic mistake the C original     */
        /* never quite makes.                                                 */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     bValueOut  : Receives the bit as true or false.                */
        /*     sReason    : Receives "" or why the read failed.               */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool       : true when a bit was read.                         */
        /*--------------------------------------------------------------------*/

        public bool JPEGBR_TryGetBit(out bool bValueOut, out string sReason)
        {
            uint uBefore;

            bValueOut = false;
            sReason   = "";

            if (JPEGBR_iCodeBits < 1)
            {   /* Top up. */
                JPEGBR_GrowBuffer();
            }

            if (!JPEGBR_TryCheckRealBits(1, "a bit", out sReason))
            {   /* Reading past the real data of a truncated file. */
                return(false);
            }

            if (JPEGBR_iCodeBits < 1)
            {   /* Still short: the stream has ended under us. */
                return(JPEGBR_TryReportStarved("a bit", out sReason));
            }

            uBefore            = JPEGBR_uCodeBuffer;
            JPEGBR_uCodeBuffer = JPEGBR_uCodeBuffer << 1;
            JPEGBR_iCodeBits   = JPEGBR_iCodeBits - 1;
            JPEGBR_SpendRealBits(1);

            bValueOut = ((uBefore & 0x80000000u) != 0u);

            /* One bit off the top of the window. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* JPEGBR_PeekFast:                                                   */
        /*                                                                    */
        /* Returns the next JPEG_HUFFMAN_FAST_BITS bits WITHOUT consuming     */
        /* them, as an index into a fast or fast-AC table.                    */
        /*                                                                    */
        /* NOT a Try function, and the reason is the whole point of a peek:   */
        /* it consumes nothing, so it cannot run past anything and there is   */
        /* no failure to report.  A stalled or truncated reader yields the    */
        /* zero-padding it is already holding, which indexes entry 0 of the   */
        /* table -- and entry 0 is the "use the slow path" value, so the      */
        /* caller falls through to a read that DOES check and DOES refuse.    */
        /* Putting the check here as well would refuse the last block of      */
        /* every valid file, which is the mistake JPEGBR_TryCheckRealBits's   */
        /* header describes at length.                                        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iPrefixOut : Receives the nine-bit lookahead, 0 to 511.        */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void       : The window is topped up but never advanced.       */
        /*--------------------------------------------------------------------*/

        public void JPEGBR_PeekFast(out int iPrefixOut)
        {
            if (JPEGBR_iCodeBits < JPEGBR_MAX_CODE_BITS)
            {   /* Top up so a full-length code plus its magnitude bits are   */
                /* present to be looked at.                                   */
                JPEGBR_GrowBuffer();
            }

            iPrefixOut = (int)((JPEGBR_uCodeBuffer >>
                                (JPEGBR_WINDOW_BITS - JpegCodec.JPEG_HUFFMAN_FAST_BITS)) &
                               (uint)(JpegCodec.JPEG_HUFFMAN_FAST_SIZE - 1));
        }

        /*--------------------------------------------------------------------*/
        /* JPEGBR_TopUp:                                                      */
        /*                                                                    */
        /* Fills the window if it has fallen below the refill ceiling, purely */
        /* so that a marker sitting just past the current position gets       */
        /* LATCHED into JPEGBR_iMarker where a caller can see it.             */
        /*                                                                    */
        /* THIS EXISTS FOR THE RESTART CHECK AND FOR NOTHING ELSE.  At the    */
        /* end of a restart interval the decoder has to ask "is the next      */
        /* thing an RSTn?", and it cannot ask until the reader has actually   */
        /* walked far enough to meet it -- the marker is discovered by the    */
        /* refill, not by looking ahead in the source.  Without this the      */
        /* marker is still sitting unread and the check reports "no restart   */
        /* here" on a perfectly good file.                                    */
        /*                                                                    */
        /* Not a Try function: it consumes nothing the caller can observe and */
        /* the exhausted case is already recorded in JPEGBR_bTruncated.       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void      : The window may be fuller and JPEGBR_iMarker may    */
        /*                 now be set; nothing else changes.                  */
        /*--------------------------------------------------------------------*/

        public void JPEGBR_TopUp()
        {
            if (JPEGBR_iCodeBits < JPEGBR_REFILL_CEILING)
            {   /* Below the ceiling, so a pending marker may not have been   */
                /* met yet. Growing is what discovers it.                     */
                JPEGBR_GrowBuffer();
            }
        }

        /*--------------------------------------------------------------------*/
        /* JPEGBR_TrySpendBits:                                               */
        /*                                                                    */
        /* Advances the window by a count the caller already knows, throwing  */
        /* the bits away.                                                     */
        /*                                                                    */
        /* This is the consuming half of the fast-AC path: the table lookup   */
        /* has already produced the run and the coefficient, so the bits      */
        /* themselves carry no further information and only the ADVANCE is    */
        /* needed.  Reading them with JPEGBR_TryGetBits would work and would  */
        /* waste the mask-and-rotate the fast path exists to avoid.           */
        /*                                                                    */
        /* It still refuses a truncated stream, because unlike a peek this    */
        /* does consume, and a fast-AC hit resolved out of pure padding would */
        /* be a coefficient invented from nothing.                            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iBits    : How many bits to discard, 1 to 16.                  */
        /*     sReason  : Receives "" or why the advance was refused.         */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool     : true when the window advanced by iBits.             */
        /*--------------------------------------------------------------------*/

        public bool JPEGBR_TrySpendBits(int iBits, out string sReason)
        {
            sReason = "";

            if (iBits < 1 || iBits > JPEGBR_MAX_CODE_BITS)
            {   /* A packed length outside this range means the fast-AC table   */
                /* was built wrong, which is a defect rather than bad data.     */
                sReason = "JPEG: cannot spend " + iBits.ToString() +
                          " bits; the fast-AC length is out of range";
                return(false);
            }

            if (!JPEGBR_TryCheckRealBits(iBits, "fast AC bits", out sReason))
            {   /* Reaching past the real data of a truncated file. */
                return(false);
            }

            if (JPEGBR_iCodeBits < iBits)
            {   /* The peek that produced this length saw a full window, so a   */
                /* shortfall now means the stream ended between the two.        */
                return(JPEGBR_TryReportStarved("fast AC bits", out sReason));
            }

            JPEGBR_uCodeBuffer = JPEGBR_uCodeBuffer << iBits;
            JPEGBR_iCodeBits   = JPEGBR_iCodeBits - iBits;
            JPEGBR_SpendRealBits(iBits);

            /* The window now starts at the coefficient after this pair. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* JPEGBR_TryCheckRealBits:                                           */
        /*                                                                    */
        /* Decides whether a request reaches past the data the file actually  */
        /* contained.                                                         */
        /*                                                                    */
        /* THIS IS THE ONE PLACE THIS READER IS STRICTER THAN ITS SOURCE, and */
        /* it is a real distinction rather than a preference:                 */
        /*                                                                    */
        /*   A MARKER WAS REACHED -- the entropy stream ended where it was    */
        /*   supposed to. The last MCU of a valid file routinely reads into   */
        /*   the encoder's padding, so this passes and the padding is used,   */
        /*   exactly as stb does. Refusing here would reject good files.      */
        /*                                                                    */
        /*   THE BYTES RAN OUT -- no marker was ever seen, so the file is cut */
        /*   short. stb cannot tell these apart because its byte reader       */
        /*   silently returns zeroes past the end; ours refuses the read, so  */
        /*   the difference is knowable and is reported.                      */
        /*                                                                    */
        /* IT MUST BE CALLED BEFORE THE SHORTFALL DISAPPEARS. The refill tops */
        /* the window up past 24 bits whatever happens, so by the time a read */
        /* looks at JPEGBR_iCodeBits the padding has already made the window  */
        /* look healthy. Only the real-bit tally still knows.                 */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iBits     : How many bits the caller is about to take.         */
        /*     sWanted   : What was being read, for the reason text.          */
        /*     sReason   : Receives "" when the read is legitimate, or the    */
        /*                 truncation message.                                */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool      : true when the caller may proceed.                  */
        /*--------------------------------------------------------------------*/

        private bool JPEGBR_TryCheckRealBits(int iBits, string sWanted, out string sReason)
        {
            sReason = "";

            if (JPEGBR_bTruncated && iBits > JPEGBR_iRealBits)
            {   /* The request reaches past the last byte the file actually    */
                /* held, in a stream that never met a marker. The refill has   */
                /* already padded the window with zeroes, so without this      */
                /* check the read would quietly succeed and hand back          */
                /* padding as data -- which is precisely how a truncated       */
                /* JPEG decodes to a half-image with no complaint anywhere.    */
                sReason = "JPEG: the entropy data ended while reading " + sWanted +
                          ", with no marker to end it; the file is truncated";
                return(false);
            }

            /* Either the bits are real, or a marker ended the stream legally   */
            /* and padding is the correct answer.                               */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* JPEGBR_SpendRealBits:                                              */
        /*                                                                    */
        /* Debits the real-bit tally when bits are consumed.                  */
        /*                                                                    */
        /* Floors at zero rather than going negative, because once the window */
        /* is into its padding every further read is spending nothing real    */
        /* and a negative count would make the truncation test above pass     */
        /* again by accident.                                                 */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iBits  : How many bits were just taken from the window.        */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void   : The tally is reduced.                                 */
        /*--------------------------------------------------------------------*/

        private void JPEGBR_SpendRealBits(int iBits)
        {
            if (JPEGBR_iRealBits > iBits)
            {   /* Still some real data left in the window. */
                JPEGBR_iRealBits = JPEGBR_iRealBits - iBits;
            }
            else
            {   /* Spent everything real that was buffered. */
                JPEGBR_iRealBits = 0;
            }
        }

        /*--------------------------------------------------------------------*/
        /* JPEGBR_TryReportStarved:                                           */
        /*                                                                    */
        /* Decides what a request the window cannot satisfy AT ALL means.     */
        /*                                                                    */
        /* Reached only when the refill could not bring the window up to the  */
        /* requested width, which after a successful refill means the stream  */
        /* has stalled on a marker and the caller is in the encoder's         */
        /* padding. The truncated case is caught earlier, by                  */
        /* JPEGBR_TryCheckRealBits, because it has to be checked BEFORE the   */
        /* padding makes the shortfall invisible.                             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sWanted   : What was being read, for the reason text.          */
        /*     sReason   : Receives "" when the stall is legitimate, or the   */
        /*                 truncation message.                                */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool      : true when the caller should carry on with zeroes.  */
        /*--------------------------------------------------------------------*/

        private bool JPEGBR_TryReportStarved(string sWanted, out string sReason)
        {
            sReason = "";

            if (JPEGBR_bTruncated)
            {   /* The data ended without a marker: the file is incomplete. */
                sReason = "JPEG: the entropy data ended while reading " + sWanted +
                          ", with no marker to end it; the file is truncated";
                return(false);
            }

            /* A marker ended the stream properly and the caller is reading   */
            /* the encoder's padding. Zeroes are the right answer and the     */
            /* out values the callers already set are already zero.           */
            return(true);
        }
    }

    /*------------------------------------------------------------------------*/
    /* JpegCodec:                                                             */
    /* Static-only.  The marker layer, the header reader, and every pixel     */
    /* path above them: entropy decode, the IDCT, colour and upsampling.      */
    /*------------------------------------------------------------------------*/

    /*------------------------------------------------------------------------*/
    /* JpegEncodeOptions:                                                     */
    /* What a caller may ask of JPEG_TryEncode.                               */
    /*                                                                        */
    /* THIS SHAPE IS PART OF THE PUBLIC CONTRACT.  Fields are added, never    */
    /* repurposed.  A null options argument is legal and means the defaults,  */
    /* which is where the no-overloading rule sends every optional argument.  */
    /*------------------------------------------------------------------------*/

    public sealed class JpegEncodeOptions
    {
        public int JPEGOPT_iQuality;                     // 1 to 100 on the Annex K scale; 0 means the default
        public int JPEGOPT_iSubsampling;                 // JPEG_SUBSAMPLE_444 / _422 / _420
    }

    /*------------------------------------------------------------------------*/
    /* JpegBitWriter:                                                         */
    /* The entropy stream's bit sink: packs codes MSB first and stuffs the    */
    /* zero byte that a literal 0xFF requires.                                */
    /*                                                                        */
    /* IT IS THE MIRROR OF JpegBitReader AND IS DELIBERATELY NOT ITS INVERSE  */
    /* IN CODE.  The reader keeps a left-aligned 32-bit window so a code of   */
    /* unknown length can be compared without shifting; a writer always KNOWS */
    /* the length it is emitting, so it needs no window at all -- just an     */
    /* accumulator and a count.  Writing this as "the reader backwards" would */
    /* import machinery that buys nothing here.                               */
    /*                                                                        */
    /* BYTE STUFFING IS THE PART THAT MUST NOT BE FORGOTTEN.  Any 0xFF that   */
    /* falls out of the entropy stream has to be followed by a literal 0x00,  */
    /* because 0xFF begins a marker everywhere else in the format.  Omitting  */
    /* it produces a file that decodes correctly right up to the first 0xFF   */
    /* byte -- which most photographs contain and most test patterns do not.  */
    /*                                                                        */
    /* THE FLUSH PADS WITH ONE-BITS, not zeroes, and that is the              */
    /* specification's choice rather than an arbitrary one: a run of ones     */
    /* cannot be mistaken for the start of a valid short code in the tables   */
    /* Annex K defines, so a decoder that reads into the padding meets        */
    /* something obviously wrong instead of a plausible extra symbol.         */
    /*------------------------------------------------------------------------*/

    /*------------------------------------------------------------------------*/
    /* JpegEncoderTables:                                                     */
    /* One component class's expanded Huffman codes -- DC and AC, code and    */
    /* length per symbol.                                                     */
    /*                                                                        */
    /* WHY A TYPE RATHER THAN FOUR ARGUMENTS OR A BOOLEAN.  The block coder   */
    /* needs four parallel arrays and they are only ever used as a set, so    */
    /* passing them individually is four chances to hand it a luma DC table   */
    /* beside a chroma AC one -- a file that decodes to plausible rubbish in  */
    /* the colour planes alone.  A boolean `bChroma` would be worse still:    */
    /* it hides a lookup inside the callee and makes the call site say        */
    /* nothing about which tables are in play.  A named pair says it at the   */
    /* call site and cannot be mismatched.                                    */
    /*------------------------------------------------------------------------*/

    public sealed class JpegEncoderTables
    {
        public int[] JPEGET_arrDcCode;                   // DC symbol -> code bits
        public int[] JPEGET_arrDcSize;                   // DC symbol -> code length; 0 means the table defines none
        public int[] JPEGET_arrAcCode;                   // AC symbol -> code bits
        public int[] JPEGET_arrAcSize;                   // AC symbol -> code length; 0 means the table defines none
    }

    public sealed class JpegBitWriter
    {
        private ByteWriter JPEGBW_bwSink;                // where finished bytes go
        private int        JPEGBW_iAccumulator;          // bits not yet whole
        private int        JPEGBW_iBitCount;             // how many of them there are

        /*--------------------------------------------------------------------*/
        /* JpegBitWriter (constructor):                                       */
        /*                                                                    */
        /* Binds the writer to the byte sink the finished stream goes into.   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     bwSink : the writer that receives the stuffed bytes.           */
        /*                                                                    */
        /* Returns:                                                           */
        /*     JpegBitWriter : ready to accept bits.                          */
        /*--------------------------------------------------------------------*/

        public JpegBitWriter(ByteWriter bwSink)
        {
            JPEGBW_bwSink       = bwSink;
            JPEGBW_iAccumulator = 0;
            JPEGBW_iBitCount    = 0;
        }

        /*--------------------------------------------------------------------*/
        /* JPEGBW_WriteBits:                                                  */
        /*                                                                    */
        /* Appends the low iCount bits of iValue, most significant first.     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iValue : the bits, in the low iCount positions.                */
        /*     iCount : how many to take; zero is legal and does nothing,     */
        /*              which a category-0 DC difference relies on.           */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void   : whole bytes are pushed to the sink as they form.      */
        /*--------------------------------------------------------------------*/

        public void JPEGBW_WriteBits(int iValue, int iCount)
        {
            int iByte;
            int i;

            for (i = iCount - 1; i >= 0; i--)
            {   /* One bit per iteration, most significant first. */
                JPEGBW_iAccumulator = (JPEGBW_iAccumulator << 1) |
                                      ((iValue >> i) & 1);
                JPEGBW_iBitCount    = JPEGBW_iBitCount + 1;

                if (JPEGBW_iBitCount == 8)
                {   /* A whole byte has formed. */
                    iByte = JPEGBW_iAccumulator & 0xFF;

                    JPEGBW_bwSink.BYTEW_WriteU8(iByte);

                    if (iByte == 0xFF)
                    {   /* A literal 0xFF must be followed by a zero, or the   */
                        /* decoder reads it as the start of a marker.          */
                        JPEGBW_bwSink.BYTEW_WriteU8(0x00);
                    }

                    JPEGBW_iAccumulator = 0;
                    JPEGBW_iBitCount    = 0;
                }
            }
        }

        /*--------------------------------------------------------------------*/
        /* JPEGBW_Flush:                                                      */
        /*                                                                    */
        /* Pads the final partial byte with one-bits and emits it.            */
        /*                                                                    */
        /* CALLING THIS IS NOT OPTIONAL.  Up to seven coded bits can be       */
        /* sitting in the accumulator when the last block finishes, and they  */
        /* are real data -- losing them truncates the last MCU of every image */
        /* whose bit count is not a multiple of eight, which is nearly all of */
        /* them.                                                              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the sink holds every coded bit.                         */
        /*--------------------------------------------------------------------*/

        public void JPEGBW_Flush()
        {
            if (JPEGBW_iBitCount == 0)
            {   /* Nothing part-formed, so nothing to pad. */
                return;
            }

            /* Padding with ones is the specification's choice; see the type's   */
            /* header for why it beats padding with zeroes.                      */
            JPEGBW_WriteBits((1 << (8 - JPEGBW_iBitCount)) - 1,
                             8 - JPEGBW_iBitCount);
        }
    }

    public static class JpegCodec
    {
        /*--------------------------------------------------------------------*/
        /* MARKERS.  Only the second byte is given; every one of these is     */
        /* preceded by 0xFF in the file.                                      */
        /*                                                                    */
        /* THE VALUES ARE NOT ARBITRARY and the groupings matter more than    */
        /* the individual numbers.  0xC0 to 0xCF are the frame and table      */
        /* markers, with 0xC4 (DHT), 0xC8 (reserved) and 0xCC (DAC) carved    */
        /* out of the run; everything else in that range is a START OF FRAME  */
        /* whose low bits say which compression the frame uses.  That is why  */
        /* JPEG_IsStartOfFrame tests a range rather than a list, and why the  */
        /* refusals below can name the exact coding a file asked for.         */
        /*--------------------------------------------------------------------*/

        public const int JPEG_MARKER_SOI  = 0xD8;        // start of image; two bytes, no segment
        public const int JPEG_MARKER_EOI  = 0xD9;        // end of image; two bytes, no segment
        public const int JPEG_MARKER_SOS  = 0xDA;        // start of scan; entropy-coded data follows the segment
        public const int JPEG_MARKER_DQT  = 0xDB;        // define quantisation table(s)
        public const int JPEG_MARKER_DNL  = 0xDC;        // define number of lines; the deferred-height form
        public const int JPEG_MARKER_DRI  = 0xDD;        // define restart interval
        public const int JPEG_MARKER_DHT  = 0xC4;        // define Huffman table(s)
        public const int JPEG_MARKER_DAC  = 0xCC;        // define arithmetic coding conditioning -- not supported
        public const int JPEG_MARKER_COM  = 0xFE;        // comment
        public const int JPEG_MARKER_TEM  = 0x01;        // temporary private use; two bytes, no segment

        public const int JPEG_MARKER_SOF0 = 0xC0;        // baseline DCT, Huffman
        public const int JPEG_MARKER_SOF1 = 0xC1;        // extended sequential DCT, Huffman
        public const int JPEG_MARKER_SOF2 = 0xC2;        // progressive DCT, Huffman

        public const int JPEG_MARKER_APP0  = 0xE0;       // first of the sixteen application segments
        public const int JPEG_MARKER_APP14 = 0xEE;       // the Adobe one, which carries the colour transform
        public const int JPEG_MARKER_APP15 = 0xEF;       // last of the sixteen

        public const int JPEG_MARKER_RST0 = 0xD0;        // first of the eight restart markers; two bytes, no segment
        public const int JPEG_MARKER_RST7 = 0xD7;        // last of the eight

        /*--------------------------------------------------------------------*/
        /* The two bytes that are never markers.                              */
        /*                                                                    */
        /* 0xFF00 is a STUFFED BYTE -- inside entropy-coded data a literal    */
        /* 0xFF is written as 0xFF 0x00 so it cannot be mistaken for a        */
        /* marker -- and 0xFFFF is fill.  Neither ends a marker scan.         */
        /*--------------------------------------------------------------------*/

        public const int JPEG_MARKER_STUFF = 0x00;       // 0xFF 0x00 is a literal 0xFF in entropy data
        public const int JPEG_MARKER_FILL  = 0xFF;       // a run of 0xFF before a marker is legal padding
        public const int JPEG_MARKER_NONE  = -1;         // no marker found; not a value the file can hold

        /*--------------------------------------------------------------------*/
        /* Structural sizes.                                                  */
        /*--------------------------------------------------------------------*/

        private const int JPEG_SEGMENT_LENGTH_BYTES = 2;  // the big-endian length that opens a segment, and includes itself
        private const int JPEG_BLOCK_SAMPLES        = 64; // an 8x8 DCT block
        private const int JPEG_HUFFMAN_LENGTHS      = 16; // codes are 1 to 16 bits long, so DHT gives sixteen counts
        private const int JPEG_MAX_SYMBOLS          = 256;// a Huffman table defines at most this many symbols

        private const int JPEG_MAX_QUANT_TABLES = 4;     // Tq is two bits wide
        private const int JPEG_MAX_HUFF_TABLES  = 4;     // Th is two bits wide
        private const int JPEG_MAX_COMPONENTS   = 4;     // the format allows more; every real file has 1, 3 or 4

        private const int JPEG_HUFFMAN_CLASS_DC = 0;     // Tc = 0: the table codes DC magnitude widths
        private const int JPEG_HUFFMAN_CLASS_AC = 1;     // Tc = 1: the table codes AC run/width pairs, and gets a fast-AC accelerator

        private const int JPEG_SOS_FIXED_BYTES = 4;      // SOS payload beyond its per-component pairs: the count byte plus Ss, Se and AhAl
        private const int JPEG_MAX_SPECTRAL    = 63;     // last coefficient index in a block, so the largest legal Ss or Se
        private const int JPEG_MAX_APPROX      = 13;     // largest legal Ah or Al; the format caps successive approximation here

        private const int JPEG_MIN_SOF_LENGTH = 11;      // 8 fixed bytes plus one three-byte component
        private const int JPEG_SOF_FIXED_BYTES = 8;      // length, precision, height, width, component count
        private const int JPEG_SOF_BYTES_PER_COMPONENT = 3;

        private const int JPEG_SUPPORTED_PRECISION = 8;  // 12-bit is legal and is not supported here; see JPEG_TryParseSof

        private const int JPEG_MAX_SAMPLING_FACTOR = 4;  // H and V are four bits but the specification caps them at 4

        private const int JPEG_RGB_COMPONENT_COUNT = 3;  // raw-RGB detection applies only to a three-component frame
        private const int JPEG_CMYK_COMPONENT_COUNT = 4; // CMYK and YCCK both carry four

        private const int JPEG_ADOBE_TRANSFORM_NONE  = 0; // APP14 transform 0: no transform -- RGB, or CMYK when four components
        private const int JPEG_ADOBE_TRANSFORM_YCCK  = 2; // APP14 transform 2: YCCK, which is YCbCr plus a K channel

        /*--------------------------------------------------------------------*/
        /* The component ids that mark a frame as carrying RGB directly       */
        /* rather than YCbCr.  Adobe and some scanner firmware write these    */
        /* instead of the conventional 1, 2, 3.                               */
        /*--------------------------------------------------------------------*/

        private static readonly int[] JPEG_arrRgbComponentIds = new int[]
        {
            0x52,                                        // 'R'
            0x47,                                        // 'G'
            0x42                                         // 'B'
        };

        /*--------------------------------------------------------------------*/
        /* THE FAST HUFFMAN TABLE'S WIDTH.                                    */
        /*                                                                    */
        /* Transcoded from stb's FAST_BITS.  The idea is that most Huffman    */
        /* codes in a real JPEG are short, so a direct-mapped table indexed   */
        /* by the next nine bits resolves them in one lookup and only the     */
        /* long codes need the bit-by-bit walk.  Nine is stb's measured       */
        /* choice: large enough to catch the overwhelming majority of codes,  */
        /* small enough that the table stays at 512 bytes per Huffman table   */
        /* and therefore in cache.                                            */
        /*                                                                    */
        /* THIS TABLE IS BUILT AT DHT TIME, not when the bit reader first     */
        /* needs it, because it is a pure function of that segment and        */
        /* belongs with the rest of the table construction; building it later */
        /* would mean the DHT parser had to be revisited rather than merely   */
        /* called.                                                            */
        /*                                                                    */
        /* PUBLIC, having started private.  JpegBitReader                     */
        /* indexes the fast table and compares against the not-accelerated    */
        /* flag, and testing builds a code deliberately LONGER than the       */
        /* fast width to force the slow path -- a test that hard-coded 9      */
        /* would silently stop testing what it means to if the width ever     */
        /* changed.  Additive: nothing that compiled before behaves           */
        /* differently.                                                       */
        /*--------------------------------------------------------------------*/

        public const int JPEG_HUFFMAN_FAST_BITS = 9;
        public const int JPEG_HUFFMAN_FAST_SIZE = 1 << JPEG_HUFFMAN_FAST_BITS;
        public const int JPEG_HUFFMAN_NOT_FAST  = 255;  // the "walk it the slow way" flag stb stores in the fast table

        /*--------------------------------------------------------------------*/
        /* THE ZIG-ZAG ORDER, and it runs the direction people expect least.  */
        /*                                                                    */
        /* A DCT block is stored as 64 coefficients in zig-zag sequence --    */
        /* diagonally from the top-left corner -- because that puts the low   */
        /* frequencies first and the long run of zeroes last, which is what   */
        /* makes the entropy coding pay.  This table maps a ZIG-ZAG INDEX to  */
        /* a ROW-MAJOR INDEX, so it is used to SCATTER incoming coefficients  */
        /* into a natural-order block, never to gather from one.              */
        /*                                                                    */
        /* THE FIFTEEN TRAILING 63s ARE DELIBERATE and are stb's, not a       */
        /* transcription slip.  A corrupt file can run the coefficient index  */
        /* past 63; every one of those reads lands on the last coefficient    */
        /* instead of outside the array, so damaged input produces a wrong    */
        /* block rather than an exception.  The bounds are still checked      */
        /* where it matters -- this is a second line, not the only one.       */
        /*--------------------------------------------------------------------*/

        private static readonly byte[] JPEG_arrDezigzag = new byte[]
        {
             0,  1,  8, 16,  9,  2,  3, 10,
            17, 24, 32, 25, 18, 11,  4,  5,
            12, 19, 26, 33, 40, 48, 41, 34,
            27, 20, 13,  6,  7, 14, 21, 28,
            35, 42, 49, 56, 57, 50, 43, 36,
            29, 22, 15, 23, 30, 37, 44, 51,
            58, 59, 52, 45, 38, 31, 39, 46,
            53, 60, 61, 54, 47, 55, 62, 63,
            63, 63, 63, 63, 63, 63, 63, 63,
            63, 63, 63, 63, 63, 63, 63
        };

        /*--------------------------------------------------------------------*/
        /* JPEG_TryReadHeader:                                                */
        /*                                                                    */
        /* Reads a JPEG's markers up to and including its frame header and    */
        /* stops, without touching a single pixel.                            */
        /*                                                                    */
        /* A DELIBERATE API ADDITION, the sibling of PNG_TryReadHeader and    */
        /* added for the same reason.  Section 9's first consumer chooses a   */
        /* thumbnail by its dimensions before deciding to fetch or decode it, */
        /* and JPEG thumbnails are exactly what a slicer embeds in a gcode    */
        /* file.  Reading the front of the file answers that question for a   */
        /* few hundred bytes of work instead of a full entropy decode.        */
        /*                                                                    */
        /* HOW FAR IT ACTUALLY READS.  The frame header may sit behind any    */
        /* number of table and application segments, so this walks them all,  */
        /* parsing what it understands and skipping what it does not, and     */
        /* returns the moment a start-of-frame marker has been parsed.  On a  */
        /* typical file that is a few hundred bytes; on a file with a large   */
        /* embedded colour profile it may be more, and there is no shortcut   */
        /* because the format allows the segments in any order.               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     arrData  : Buffer holding the file. Must not be null.          */
        /*     iOffset  : Index of the first byte of the JPEG.                */
        /*     iLength  : How many bytes of arrData belong to it.             */
        /*     hdrOut   : Receives the header; null on failure.               */
        /*     sReason  : Receives "" on success, or why it was refused.      */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool     : true when hdrOut describes a JPEG this library      */
        /*                could go on to decode.                              */
        /*--------------------------------------------------------------------*/

        public static bool JPEG_TryReadHeader(byte[] arrData, int iOffset, int iLength,
                                              out JpegHeader hdrOut, out string sReason)
        {
            ByteReader brIn;

            hdrOut  = null;
            sReason = "";

            if (arrData == null)
            {   /* Missing argument, not bad data. */
                throw new ArgumentNullException("arrData");
            }

            if (!ImageDecoderDispatch.IMGDEC_TryValidateWindow(arrData, iOffset, iLength,
                                                               out sReason))
            {   /* A window that does not lie inside the array. */
                return(false);
            }

            brIn = new ByteReader(arrData, iOffset, iLength);

            /* The walk stops at the frame header rather than running to the    */
            /* end of the file, which is the whole point of this entry point.   */
            return(JPEG_TryWalkToFrameHeader(brIn, null, null, out hdrOut, out sReason));
        }

        /*--------------------------------------------------------------------*/
        /* JPEG_TryWalkToFrameHeader:                                         */
        /*                                                                    */
        /* The marker walk: reads segments from the current position until a  */
        /* start-of-frame has been parsed, gathering the tables on the way.   */
        /*                                                                    */
        /* THE TABLE ARRAYS ARE OPTIONAL, and that is what lets one walker    */
        /* serve both callers.  JPEG_TryReadHeader wants geometry and passes  */
        /* null for both, so the DQT and DHT segments are parsed for          */
        /* VALIDITY -- a malformed table still refuses the file -- and their  */
        /* contents are then discarded.  The full decoder passes real         */
        /* arrays and keep them.  Parsing but discarding is deliberate: a     */
        /* header reader that accepted a file with a corrupt Huffman table    */
        /* would report a size for an image that cannot be decoded, which is  */
        /* worse than useless to a caller deciding whether to fetch it.       */
        /*                                                                    */
        /* WHY IT STOPS AT SOF RATHER THAN AT SOS.  The frame header is the   */
        /* last thing that describes the IMAGE; everything after it describes */
        /* the scans that encode it.  Stopping here is what makes the header  */
        /* read cheap, and it is where stb's own stbi__decode_jpeg_header     */
        /* stops for the same reason.                                         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     brIo         : Reader positioned at the start of the file.     */
        /*     arrQuantIo   : Receives the dequantisation tables, four rows   */
        /*                    of 64 in NATURAL order; null to discard them.   */
        /*     arrHuffIo    : Receives the Huffman tables, DC 0 to 3 then AC  */
        /*                    0 to 3; null to discard them.                   */
        /*     hdrOut       : Receives the header; null on failure.           */
        /*     sReason      : Receives "" or why the file was refused.        */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool         : true when a frame header was reached and        */
        /*                    parsed.                                         */
        /*--------------------------------------------------------------------*/

        public static bool JPEG_TryWalkToFrameHeader(ByteReader brIo, int[][] arrQuantIo,
                                                     JpegHuffmanTable[] arrHuffIo,
                                                     out JpegHeader hdrOut, out string sReason)
        {
            JpegHeader hdrNew;
            int        iMarker;

            hdrOut  = null;
            sReason = "";

            hdrNew = new JpegHeader();

            /* Defaults for the fields the optional segments fill in. A file   */
            /* with no DRI has no restart interval, and -1 is the "no Adobe    */
            /* segment was seen" value stb uses, chosen because 0, 1 and 2     */
            /* are all real transforms.                                        */
            hdrNew.JPEGH_iRestartInterval = 0;
            hdrNew.JPEGH_bHasJfif         = false;
            hdrNew.JPEGH_iAdobeTransform  = -1;

            if (!JPEG_TryReadMarker(brIo, out iMarker, out sReason))
            {   /* No marker at all at offset 0. The marker reader's own         */
                /* message is accurate but low-level -- it reports the byte it   */
                /* found -- and the useful thing to tell a caller here is that   */
                /* THIS IS NOT A JPEG, which is overwhelmingly the reason a      */
                /* file fails at its very first byte. The precise finding is     */
                /* kept after the colon rather than replaced.                    */
                sReason = "JPEG: the data does not begin with an SOI marker, so it is not a " +
                          "JPEG: " + sReason;
                return(false);
            }

            if (iMarker != JPEG_MARKER_SOI)
            {   /* A marker, but the wrong one. Distinguished from the case   */
                /* above because a file that starts with a real marker is     */
                /* damaged or truncated rather than a different format        */
                /* altogether, and those want different investigations.       */
                sReason = "JPEG: expected SOI, found " + JPEG_MarkerToText(iMarker);
                return(false);
            }

            for (;;)
            {   /* One marker per iteration, until the frame header or an   */
                /* error ends it.                                           */
                if (!JPEG_TryReadMarker(brIo, out iMarker, out sReason))
                {   /* Ran out of file before any frame header appeared. */
                    sReason = "JPEG: no frame header before the end of the data";
                    return(false);
                }

                if (JPEG_IsStartOfFrame(iMarker))
                {   /* The thing we came for. Everything else in the loop is   */
                    /* preamble to it.                                         */
                    hdrNew.JPEGH_bProgressive = (iMarker == JPEG_MARKER_SOF2);

                    if (!JPEG_TryParseSof(brIo, hdrNew, out sReason))
                    {   /* A frame header that contradicts itself. */
                        return(false);
                    }

                    hdrOut = hdrNew;

                    /* A file whose geometry and tables are all consistent. */
                    return(true);
                }

                if (!JPEG_TryProcessSegment(brIo, iMarker, hdrNew, arrQuantIo, arrHuffIo,
                                            out sReason))
                {   /* A segment that is malformed, or a coding this library   */
                    /* does not implement.                                     */
                    return(false);
                }
            }
        }

        /*--------------------------------------------------------------------*/
        /* JPEG_TryReadMarker:                                                */
        /*                                                                    */
        /* Reads the next marker, skipping any fill bytes before it.          */
        /*                                                                    */
        /* A MARKER IS 0xFF FOLLOWED BY SOMETHING THAT IS NEITHER 0x00 NOR    */
        /* 0xFF.  Repeated 0xFF bytes are legal padding, so the second byte   */
        /* is read in a loop; 0xFF 0x00 is a stuffed literal that only        */
        /* appears inside entropy-coded data and is not a marker at all.      */
        /*                                                                    */
        /* Transcoded from stb's stbi__get_marker, with one deviation stated  */
        /* here: STB RETURNS A SENTINEL WHERE THIS RETURNS FALSE.  Its        */
        /* function hands back 0xFF for "no marker" and leaves the caller to  */
        /* notice; this library's contract is a bool and a reason, and a      */
        /* caller that forgets to check a sentinel gets a wrong answer        */
        /* silently where a caller that ignores a bool gets a compiler        */
        /* warning at worst and a null dereference at best.                   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     brIo        : Reader positioned where a marker is expected.    */
        /*     iMarkerOut  : Receives the marker's second byte.               */
        /*     sReason     : Receives "" or why no marker was found.          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool        : true when a marker was read.                     */
        /*--------------------------------------------------------------------*/

        public static bool JPEG_TryReadMarker(ByteReader brIo, out int iMarkerOut,
                                              out string sReason)
        {
            int iByte;

            iMarkerOut = JPEG_MARKER_NONE;
            sReason    = "";

            if (!brIo.BYTER_TryReadU8(out iByte))
            {   /* No bytes left at all. */
                sReason = "JPEG: expected a marker at offset " +
                          brIo.BYTER_Position().ToString() + " but the data ended";
                return(false);
            }

            if (iByte != 0xFF)
            {   /* Whatever this is, it is not the start of a marker. Naming   */
                /* the byte and the offset is what makes a desynchronised      */
                /* parse diagnosable rather than merely failed.                */
                sReason = "JPEG: expected 0xFF to open a marker at offset " +
                          (brIo.BYTER_Position() - 1).ToString() + " but found 0x" +
                          iByte.ToString("X2");
                return(false);
            }

            for (;;)
            {   /* Consume the run of fill bytes, if there is one. */
                if (!brIo.BYTER_TryReadU8(out iByte))
                {   /* A 0xFF at the very end of the data names nothing. */
                    sReason = "JPEG: the data ends with an unterminated marker at offset " +
                              brIo.BYTER_Position().ToString();
                    return(false);
                }

                if (iByte != JPEG_MARKER_FILL)
                {   /* The first byte after the fill run is the marker. */
                    break;
                }
            }

            if (iByte == JPEG_MARKER_STUFF)
            {   /* 0xFF 0x00 is a stuffed literal inside entropy data, never   */
                /* a marker. Reaching one here means the parse has walked      */
                /* into the middle of a scan.                                  */
                sReason = "JPEG: found a stuffed 0xFF00 where a marker was expected, at offset " +
                          (brIo.BYTER_Position() - 2).ToString();
                return(false);
            }

            iMarkerOut = iByte;

            /* A real marker, with any fill before it consumed. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* JPEG_TryProcessSegment:                                            */
        /*                                                                    */
        /* Handles one non-frame marker: parses it when this library          */
        /* understands it, skips it when it is harmless, refuses it when it   */
        /* names a coding that is not implemented.                            */
        /*                                                                    */
        /* THE THREE OUTCOMES ARE DELIBERATELY DIFFERENT and the difference   */
        /* is the same one PNG draws between critical and ancillary chunks.   */
        /* A table segment is parsed because the pixels depend on it.  An     */
        /* application or comment segment is skipped because nothing in the   */
        /* image depends on it and refusing a file over a comment we do not   */
        /* recognise would reject files every other decoder displays.  An     */
        /* arithmetic-coded or lossless frame is refused because decoding it  */
        /* wrongly would produce confident wrong output, which is the failure */
        /* this whole layer exists to prevent.                                */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     brIo        : Reader positioned just past the marker.          */
        /*     iMarker     : The marker's second byte.                        */
        /*     hdrIo       : Header being filled in; DRI and APP14 land here. */
        /*     arrQuantIo  : Dequantisation tables, or null to discard.       */
        /*     arrHuffIo   : Huffman tables, or null to discard.              */
        /*     sReason     : Receives "" or why the segment was refused.      */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool        : true when the segment was handled and the        */
        /*                   reader sits at the next marker.                  */
        /*--------------------------------------------------------------------*/

        private static bool JPEG_TryProcessSegment(ByteReader brIo, int iMarker, JpegHeader hdrIo,
                                                   int[][] arrQuantIo,
                                                   JpegHuffmanTable[] arrHuffIo,
                                                   out string sReason)
        {
            int iLength;

            sReason = "";

            if (iMarker == JPEG_MARKER_SOI)
            {   /* A second SOI before any frame header. The format has no   */
                /* nesting, so this is a damaged or concatenated file.       */
                sReason = "JPEG: a second SOI appeared before the frame header";
                return(false);
            }

            if (iMarker == JPEG_MARKER_EOI)
            {   /* The file ended without ever describing an image. */
                sReason = "JPEG: EOI reached before any frame header";
                return(false);
            }

            if (iMarker == JPEG_MARKER_SOS)
            {   /* A scan cannot precede the frame it belongs to; without the   */
                /* frame header there is nothing to say how to read it.         */
                sReason = "JPEG: SOS appeared before the frame header";
                return(false);
            }

            if (JPEG_IsRestart(iMarker) || iMarker == JPEG_MARKER_TEM)
            {   /* Standalone markers carrying no segment. They have no         */
                /* business here -- restarts belong inside a scan -- but they   */
                /* carry no length, so refusing is the only correct answer;     */
                /* skipping would need a length that does not exist.            */
                sReason = "JPEG: unexpected standalone marker " + JPEG_MarkerToText(iMarker) +
                          " before the frame header";
                return(false);
            }

            if (JPEG_IsUnsupportedFrame(iMarker))
            {   /* A frame this library will not decode. Named exactly,         */
                /* because "unsupported" alone sends the reader looking for a   */
                /* corrupt file when what they have is a legal one of a kind    */
                /* nothing here reads.                                          */
                sReason = "JPEG: " + JPEG_MarkerToText(iMarker) +
                          " frames are not supported; only baseline, extended sequential " +
                          "and progressive Huffman frames are";
                return(false);
            }

            if (iMarker == JPEG_MARKER_DAC)
            {   /* Arithmetic coding. Legal, patent-free since 2003, and   */
                /* implemented by almost nothing -- stb refuses it too.    */
                sReason = "JPEG: arithmetic coding is not supported";
                return(false);
            }

            /*----------------------------------------------------------------*/
            /* Everything from here carries a segment, so read its length.    */
            /*----------------------------------------------------------------*/

            if (!JPEG_TryReadSegmentLength(brIo, iMarker, out iLength, out sReason))
            {   /* A length that is absent, too small to include itself, or   */
                /* longer than the data that follows it.                      */
                return(false);
            }

            if (iMarker == JPEG_MARKER_DQT)
            {   /* Quantisation tables. */
                return(JPEG_TryParseDqt(brIo, iLength, arrQuantIo, out sReason));
            }

            if (iMarker == JPEG_MARKER_DHT)
            {   /* Huffman tables. */
                return(JPEG_TryParseDht(brIo, iLength, arrHuffIo, out sReason));
            }

            if (iMarker == JPEG_MARKER_DRI)
            {   /* Restart interval. */
                return(JPEG_TryParseDri(brIo, iLength, hdrIo, out sReason));
            }

            if (iMarker >= JPEG_MARKER_APP0 && iMarker <= JPEG_MARKER_APP15)
            {   /* An application segment. Two of the sixteen say something   */
                /* this library cares about; the rest are skipped whole.      */
                return(JPEG_TryParseApp(brIo, iMarker, iLength, hdrIo, out sReason));
            }

            if (iMarker == JPEG_MARKER_COM)
            {   /* A comment. Nothing in the image depends on it. */
                return(JPEG_TrySkipSegmentBody(brIo, iLength, iMarker, out sReason));
            }

            if (iMarker == JPEG_MARKER_DNL)
            {   /* Define number of lines: the deferred-height form, where      */
                /* SOF declares height 0 and this segment supplies it after     */
                /* the first scan. Legal, and refused here for the reason stb   */
                /* gives -- neither it nor the IJG reference library handles    */
                /* it, and a header reader that reported height 0 would be      */
                /* worse than one that says it cannot tell.                     */
                sReason = "JPEG: deferred height (DNL) is not supported";
                return(false);
            }

            /*----------------------------------------------------------------*/
            /* Anything else with a length is skipped. The format reserves    */
            /* marker values for future use and an unknown one carrying a     */
            /* well-formed segment says nothing about the pixels, so this is  */
            /* the same call PNG makes for an ancillary chunk.                */
            /*----------------------------------------------------------------*/

            return(JPEG_TrySkipSegmentBody(brIo, iLength, iMarker, out sReason));
        }

        /*--------------------------------------------------------------------*/
        /* JPEG_TryReadSegmentLength:                                         */
        /*                                                                    */
        /* Reads a segment's two-byte length and converts it to a PAYLOAD     */
        /* length.                                                            */
        /*                                                                    */
        /* THE DECLARED LENGTH INCLUDES ITS OWN TWO BYTES.  A segment with    */
        /* four bytes of payload declares six.  This function subtracts the   */
        /* two so that every caller works in payload bytes and none of them   */
        /* has to remember -- which is the entire reason it exists as a       */
        /* function rather than two lines at each of six call sites.          */
        /*                                                                    */
        /* A DECLARED LENGTH BELOW TWO IS REFUSED because it would make the   */
        /* payload negative, and a length longer than the remaining data is   */
        /* refused here rather than discovered halfway through parsing the    */
        /* segment's contents.                                                */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     brIo        : Reader positioned at the length field.           */
        /*     iMarker     : The owning marker, for the reason text.          */
        /*     iLengthOut  : Receives the PAYLOAD length in bytes.            */
        /*     sReason     : Receives "" or why the length is impossible.     */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool        : true when a usable payload length was read.      */
        /*--------------------------------------------------------------------*/

        private static bool JPEG_TryReadSegmentLength(ByteReader brIo, int iMarker,
                                                      out int iLengthOut, out string sReason)
        {
            int iDeclared;

            iLengthOut = 0;
            sReason    = "";

            if (!brIo.BYTER_TryReadU16BE(out iDeclared))
            {   /* The segment claims to exist but its length is off the end. */
                sReason = "JPEG: " + JPEG_MarkerToText(iMarker) +
                          " has no length field; the data ends at offset " +
                          brIo.BYTER_Position().ToString();
                return(false);
            }

            if (iDeclared < JPEG_SEGMENT_LENGTH_BYTES)
            {   /* The length counts itself, so it can never be below two. */
                sReason = "JPEG: " + JPEG_MarkerToText(iMarker) + " declares a length of " +
                          iDeclared.ToString() + ", which is less than the two bytes the " +
                          "length field itself occupies";
                return(false);
            }

            iLengthOut = iDeclared - JPEG_SEGMENT_LENGTH_BYTES;

            if (iLengthOut > brIo.BYTER_Remaining())
            {   /* Declared longer than the file has left. Caught here so the   */
                /* reason names the segment rather than whichever field         */
                /* happened to run off the end while parsing it.                */
                sReason = "JPEG: " + JPEG_MarkerToText(iMarker) + " declares " +
                          iLengthOut.ToString() + " bytes of payload but only " +
                          brIo.BYTER_Remaining().ToString() + " remain";
                iLengthOut = 0;
                return(false);
            }

            /* A payload length that fits inside the data. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* JPEG_TrySkipSegmentBody:                                           */
        /*                                                                    */
        /* Steps over a segment's payload without looking at it.              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     brIo      : Reader positioned at the payload.                  */
        /*     iLength   : Payload length in bytes.                           */
        /*     iMarker   : The owning marker, for the reason text.            */
        /*     sReason   : Receives "" or why the skip failed.                */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool      : true when the reader now sits at the next marker.  */
        /*--------------------------------------------------------------------*/

        private static bool JPEG_TrySkipSegmentBody(ByteReader brIo, int iLength, int iMarker,
                                                    out string sReason)
        {
            sReason = "";

            if (!brIo.BYTER_TrySkip(iLength))
            {   /* The length was checked against the remaining data before   */
                /* this call, so reaching here means the reader and the       */
                /* length disagree -- stated rather than assumed away.        */
                sReason = "JPEG: could not skip the " + iLength.ToString() +
                          " byte payload of " + JPEG_MarkerToText(iMarker);
                return(false);
            }

            /* Positioned at whatever follows. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* JPEG_TryParseSof:                                                  */
        /*                                                                    */
        /* Parses a start-of-frame segment: precision, dimensions, and one    */
        /* descriptor per component.                                          */
        /*                                                                    */
        /* Transcoded from stb's stbi__process_frame_header, taking its       */
        /* validation and its derived MCU arithmetic and leaving its buffer   */
        /* allocation to the decode path, which is where they are used.       */
        /*                                                                    */
        /* THE HEIGHT COMES BEFORE THE WIDTH.  Every other format in this     */
        /* library writes width first and so does ordinary intuition; JPEG    */
        /* does not.  A transposed read produces a perfectly plausible        */
        /* header for a rotated image and is invisible on a square test       */
        /* image, which is why no test geometry is square.                    */
        /*                                                                    */
        /* WHY ONLY 8-BIT PRECISION.  12-bit JPEG is legal and is refused     */
        /* here, as stb refuses it: it doubles every coefficient path, no     */
        /* consumer in this portfolio produces one, and a wrong guess would   */
        /* decode to noise rather than to an obvious failure.  This is a      */
        /* PERMANENT refusal and names no sub-step.                           */
        /*                                                                    */
        /* THE SUBSAMPLING RATIOS MUST DIVIDE EVENLY, which is stb's rule and */
        /* is kept.  A component whose factor does not divide the maximum     */
        /* would need a fractional resampler; the specification permits it,   */
        /* no real encoder emits it, and refusing is better than a resampler  */
        /* whose only exercise is corrupt files.                              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     brIo     : Reader positioned at the segment length.            */
        /*     hdrIo    : Header to fill in.                                  */
        /*     sReason  : Receives "" or which field was impossible.          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool     : true when the frame header is self-consistent.      */
        /*--------------------------------------------------------------------*/

        private static bool JPEG_TryParseSof(ByteReader brIo, JpegHeader hdrIo, out string sReason)
        {
            JpegComponent[] arrComponents;
            JpegComponent   cmpNew;
            int             iLength;
            int             iPrecision;
            int             iWidth;
            int             iHeight;
            int             iCount;
            int             iSampling;
            int             iMaxH;
            int             iMaxV;
            int             iRgbIds;
            int             i;

            sReason = "";

            if (!JPEG_TryReadSegmentLength(brIo, JPEG_MARKER_SOF0, out iLength, out sReason))
            {   /* No length, or one that does not fit. */
                return(false);
            }

            if (iLength < (JPEG_MIN_SOF_LENGTH - JPEG_SEGMENT_LENGTH_BYTES))
            {   /* Too short to hold even one component descriptor. */
                sReason = "JPEG: the frame header payload is " + iLength.ToString() +
                          " bytes, too short to describe even one component";
                return(false);
            }

            if (!brIo.BYTER_TryReadU8(out iPrecision) ||
                !brIo.BYTER_TryReadU16BE(out iHeight) ||
                !brIo.BYTER_TryReadU16BE(out iWidth) ||
                !brIo.BYTER_TryReadU8(out iCount))
            {   /* The length said these bytes were there and they are not. */
                sReason = "JPEG: the frame header ended inside its fixed fields";
                return(false);
            }

            if (iPrecision != JPEG_SUPPORTED_PRECISION)
            {   /* 12-bit and 16-bit frames are legal and unsupported. */
                sReason = "JPEG: a sample precision of " + iPrecision.ToString() +
                          " bits is not supported; only 8-bit samples are";
                return(false);
            }

            if (iHeight < 1)
            {   /* Height 0 is the deferred-height form, which DNL would      */
                /* supply later and which this library refuses; see the DNL   */
                /* branch in JPEG_TryProcessSegment.                          */
                sReason = "JPEG: the frame header declares height 0, which means the height " +
                          "is deferred to a DNL segment; that form is not supported";
                return(false);
            }

            if (iWidth < 1)
            {   /* Width 0 is not legal at all, deferred or otherwise. */
                sReason = "JPEG: the frame header declares a width of 0";
                return(false);
            }

            if (iWidth > ImageBuffer.IMG_MAX_DIMENSION || iHeight > ImageBuffer.IMG_MAX_DIMENSION)
            {   /* The same cap every other codec here applies, so a corrupt   */
                /* dimension fails the same way whatever format carried it.    */
                sReason = "JPEG: " + iWidth.ToString() + "x" + iHeight.ToString() +
                          " exceeds the " + ImageBuffer.IMG_MAX_DIMENSION.ToString() +
                          " pixel limit on a side";
                return(false);
            }

            if (iCount != 1 && iCount != 3 && iCount != JPEG_MAX_COMPONENTS)
            {   /* One grayscale, three colour, four CMYK or YCCK. Two is    */
                /* legal by the letter of the format and means nothing any   */
                /* decoder can interpret.                                    */
                sReason = "JPEG: a component count of " + iCount.ToString() +
                          " is not 1, 3 or 4";
                return(false);
            }

            if (iLength != (JPEG_SOF_FIXED_BYTES - JPEG_SEGMENT_LENGTH_BYTES) +
                           (JPEG_SOF_BYTES_PER_COMPONENT * iCount))
            {   /* The declared length and the component count contradict      */
                /* each other. Checked AFTER the count is known, because the   */
                /* count is what makes the expected length computable.         */
                sReason = "JPEG: the frame header payload is " + iLength.ToString() +
                          " bytes but " + iCount.ToString() + " components need " +
                          ((JPEG_SOF_FIXED_BYTES - JPEG_SEGMENT_LENGTH_BYTES) +
                           (JPEG_SOF_BYTES_PER_COMPONENT * iCount)).ToString();
                return(false);
            }

            arrComponents = new JpegComponent[iCount];
            iMaxH         = 1;
            iMaxV         = 1;
            iRgbIds       = 0;

            for (i = 0; i < iCount; i++)
            {   /* One three-byte descriptor per iteration. */
                cmpNew = new JpegComponent();

                if (!brIo.BYTER_TryReadU8(out cmpNew.JPEGC_iId) ||
                    !brIo.BYTER_TryReadU8(out iSampling) ||
                    !brIo.BYTER_TryReadU8(out cmpNew.JPEGC_iQuantTable))
                {   /* The length check above should make this unreachable;   */
                    /* reported rather than assumed away.                     */
                    sReason = "JPEG: the frame header ended inside component " + i.ToString();
                    return(false);
                }

                /* RAW-RGB DETECTION, counted here because this is the only    */
                /* place the ids are read.  A three-component frame whose      */
                /* components are literally named 'R', 'G' and 'B' in that     */
                /* order carries RGB samples directly and must NOT have the    */
                /* YCbCr transform applied to it -- doing so produces a        */
                /* sharp, correctly sized picture in badly wrong colours.      */
                /* Counting rather than flagging keeps the test honest: two    */
                /* of three matching is not RGB, and a coincidental 'R' in a   */
                /* YCbCr file's first component means nothing on its own.      */
                if (iCount == JPEG_RGB_COMPONENT_COUNT &&
                    cmpNew.JPEGC_iId == JPEG_arrRgbComponentIds[i])
                {   /* This component is named for its colour, in position. */
                    iRgbIds = iRgbIds + 1;
                }

                /* One byte carries both factors, horizontal in the high   */
                /* nibble. Splitting them is the only place the packing    */
                /* matters, so it is done once and stored apart.           */
                cmpNew.JPEGC_iHorizontalFactor = (iSampling >> 4) & 0x0F;
                cmpNew.JPEGC_iVerticalFactor   = iSampling & 0x0F;

                if (cmpNew.JPEGC_iHorizontalFactor < 1 ||
                    cmpNew.JPEGC_iHorizontalFactor > JPEG_MAX_SAMPLING_FACTOR)
                {   /* Zero would mean a component with no samples; above   */
                    /* four is outside what the specification permits.      */
                    sReason = "JPEG: component " + i.ToString() +
                              " has a horizontal sampling factor of " +
                              cmpNew.JPEGC_iHorizontalFactor.ToString() + ", not 1 to 4";
                    return(false);
                }

                if (cmpNew.JPEGC_iVerticalFactor < 1 ||
                    cmpNew.JPEGC_iVerticalFactor > JPEG_MAX_SAMPLING_FACTOR)
                {   /* Likewise on the other axis. */
                    sReason = "JPEG: component " + i.ToString() +
                              " has a vertical sampling factor of " +
                              cmpNew.JPEGC_iVerticalFactor.ToString() + ", not 1 to 4";
                    return(false);
                }

                if (cmpNew.JPEGC_iQuantTable >= JPEG_MAX_QUANT_TABLES)
                {   /* Tq is two bits of meaning in a whole byte. */
                    sReason = "JPEG: component " + i.ToString() +
                              " names quantisation table " +
                              cmpNew.JPEGC_iQuantTable.ToString() + ", but only 0 to 3 exist";
                    return(false);
                }

                if (cmpNew.JPEGC_iHorizontalFactor > iMaxH)
                {   /* A new widest component. */
                    iMaxH = cmpNew.JPEGC_iHorizontalFactor;
                }

                if (cmpNew.JPEGC_iVerticalFactor > iMaxV)
                {   /* A new tallest component. */
                    iMaxV = cmpNew.JPEGC_iVerticalFactor;
                }

                arrComponents[i] = cmpNew;
            }

            for (i = 0; i < iCount; i++)
            {   /* Every component's factors must divide the maxima evenly. */
                if ((iMaxH % arrComponents[i].JPEGC_iHorizontalFactor) != 0 ||
                    (iMaxV % arrComponents[i].JPEGC_iVerticalFactor) != 0)
                {   /* A fractional ratio, which needs a resampler this      */
                    /* library does not have and no real encoder produces.   */
                    sReason = "JPEG: component " + i.ToString() + " samples at " +
                              arrComponents[i].JPEGC_iHorizontalFactor.ToString() + "x" +
                              arrComponents[i].JPEGC_iVerticalFactor.ToString() +
                              ", which does not divide the frame's " + iMaxH.ToString() + "x" +
                              iMaxV.ToString() + " evenly";
                    return(false);
                }
            }

            hdrIo.JPEGH_iPrecision           = iPrecision;
            hdrIo.JPEGH_iWidth               = iWidth;
            hdrIo.JPEGH_iHeight              = iHeight;
            hdrIo.JPEGH_iComponentCount      = iCount;
            hdrIo.JPEGH_arrComponents        = arrComponents;
            hdrIo.JPEGH_iMaxHorizontalFactor = iMaxH;
            hdrIo.JPEGH_iMaxVerticalFactor   = iMaxV;
            hdrIo.JPEGH_iRgbComponentIds     = iRgbIds;

            /* An MCU is one sample from the least-sampled component and as    */
            /* many as the factors demand from the others, so it covers        */
            /* max-h by max-v blocks of eight pixels each. The counts round    */
            /* UP: a partial MCU at the right or bottom edge is still stored   */
            /* whole and its surplus pixels are discarded on output.           */
            hdrIo.JPEGH_iMcuWidth  = iMaxH * 8;
            hdrIo.JPEGH_iMcuHeight = iMaxV * 8;
            hdrIo.JPEGH_iMcuCountX = (iWidth + hdrIo.JPEGH_iMcuWidth - 1) / hdrIo.JPEGH_iMcuWidth;
            hdrIo.JPEGH_iMcuCountY = (iHeight + hdrIo.JPEGH_iMcuHeight - 1) /
                                     hdrIo.JPEGH_iMcuHeight;

            /* A frame header whose every field agrees with every other. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* JPEG_TryParseDqt:                                                  */
        /*                                                                    */
        /* Parses a DQT segment, which may define more than one table.        */
        /*                                                                    */
        /* Transcoded from the 0xDB case of stb's stbi__process_marker.       */
        /*                                                                    */
        /* THE TABLE IS STORED IN ZIG-ZAG ORDER AND UNPACKED ON THE WAY IN,   */
        /* using JPEG_arrDezigzag as a SCATTER.  Every later stage wants the  */
        /* dequantisation factor for a natural-order coefficient, so undoing  */
        /* the ordering once here beats undoing it per block for the life of  */
        /* the decode.                                                        */
        /*                                                                    */
        /* THE PRECISION BIT IS PER TABLE, NOT PER FILE.  The high nibble of  */
        /* each table's first byte says whether its 64 values are one byte or */
        /* two, so one segment may hold an 8-bit table and a 16-bit one.      */
        /* Sixteen-bit tables are legal only in frames this library refuses   */
        /* anyway, but they are parsed rather than refused, because the       */
        /* reason to refuse belongs to the FRAME and giving it twice in two   */
        /* voices would confuse rather than help.                             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     brIo        : Reader positioned at the payload.                */
        /*     iLength     : Payload length in bytes.                         */
        /*     arrQuantIo  : Four rows of 64 to fill in, or null to discard.  */
        /*     sReason     : Receives "" or what was malformed.               */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool        : true when every table in the segment parsed.     */
        /*--------------------------------------------------------------------*/

        private static bool JPEG_TryParseDqt(ByteReader brIo, int iLength, int[][] arrQuantIo,
                                             out string sReason)
        {
            int  iLeft;
            int  iSpec;
            int  iPrecision;
            int  iTable;
            int  iValue;
            bool bSixteen;
            int  i;

            sReason = "";
            iLeft   = iLength;

            while (iLeft > 0)
            {   /* One table per iteration; a segment may carry several. */
                if (!brIo.BYTER_TryReadU8(out iSpec))
                {   /* The payload ended where a table specifier belongs. */
                    sReason = "JPEG: DQT ended where a table specifier was expected";
                    return(false);
                }

                iPrecision = (iSpec >> 4) & 0x0F;
                iTable     = iSpec & 0x0F;
                bSixteen   = (iPrecision != 0);

                if (iPrecision != 0 && iPrecision != 1)
                {   /* Only two precisions are defined. */
                    sReason = "JPEG: DQT declares precision " + iPrecision.ToString() +
                              ", which is neither 0 (8-bit) nor 1 (16-bit)";
                    return(false);
                }

                if (iTable >= JPEG_MAX_QUANT_TABLES)
                {   /* Tq is two bits of meaning. */
                    sReason = "JPEG: DQT defines table " + iTable.ToString() +
                              ", but only 0 to 3 exist";
                    return(false);
                }

                /* One byte of specifier plus 64 values at one or two bytes. */
                iLeft = iLeft - 1 - (JPEG_BLOCK_SAMPLES * (bSixteen ? 2 : 1));

                if (iLeft < 0)
                {   /* The segment is shorter than the tables it declares. */
                    sReason = "JPEG: DQT table " + iTable.ToString() +
                              " runs past the end of its segment";
                    return(false);
                }

                for (i = 0; i < JPEG_BLOCK_SAMPLES; i++)
                {   /* One coefficient per iteration, scattered out of   */
                    /* zig-zag order as it arrives.                      */
                    if (bSixteen)
                    {   /* Two bytes, big-endian like everything else here. */
                        if (!brIo.BYTER_TryReadU16BE(out iValue))
                        {   /* The length arithmetic above should prevent   */
                            /* this; reported rather than assumed away.     */
                            sReason = "JPEG: DQT table " + iTable.ToString() +
                                      " ended at value " + i.ToString();
                            return(false);
                        }
                    }
                    else if (!brIo.BYTER_TryReadU8(out iValue))
                    {   /* Likewise for the one-byte form. */
                        sReason = "JPEG: DQT table " + iTable.ToString() + " ended at value " +
                                  i.ToString();
                        return(false);
                    }

                    if (arrQuantIo != null)
                    {   /* The decoder wants these; the header reader does     */
                        /* not and passed null, having parsed them purely to   */
                        /* prove the file is well formed.                      */
                        arrQuantIo[iTable][JPEG_arrDezigzag[i]] = iValue;
                    }
                }
            }

            if (iLeft != 0)
            {   /* Trailing bytes inside the segment that belong to no table. */
                sReason = "JPEG: DQT has " + (-iLeft).ToString() +
                          " bytes left over after its tables";
                return(false);
            }

            /* Every table in the segment read cleanly. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* JPEG_TryParseDht:                                                  */
        /*                                                                    */
        /* Parses a DHT segment, which may define more than one table, and    */
        /* builds each one into decode-ready form.                            */
        /*                                                                    */
        /* Transcoded from the 0xC4 case of stb's stbi__process_marker.       */
        /*                                                                    */
        /* THE CLASS BIT SELECTS DC OR AC, and the two are separate table     */
        /* sets sharing one numbering.  DC table 0 and AC table 0 are         */
        /* different tables; a component names one of each.  Storing them in  */
        /* one array with the AC set offset by four is stb's layout and is    */
        /* kept, because it makes the component's two table indices two       */
        /* additions rather than two branches.                                */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     brIo       : Reader positioned at the payload.                 */
        /*     iLength    : Payload length in bytes.                          */
        /*     arrHuffIo  : Eight slots -- DC 0 to 3 then AC 0 to 3 -- to     */
        /*                  fill in, or null to discard.                      */
        /*     sReason    : Receives "" or what was malformed.                */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool       : true when every table in the segment built.       */
        /*--------------------------------------------------------------------*/

        private static bool JPEG_TryParseDht(ByteReader brIo, int iLength,
                                             JpegHuffmanTable[] arrHuffIo, out string sReason)
        {
            JpegHuffmanTable tblNew;
            int[]            arrCounts;
            int              iLeft;
            int              iSpec;
            int              iClass;
            int              iTable;
            int              iTotal;
            int              iSlot;
            int              i;

            sReason   = "";
            iLeft     = iLength;
            arrCounts = new int[JPEG_HUFFMAN_LENGTHS];

            while (iLeft > 0)
            {   /* One table per iteration; a segment may carry several. */
                if (!brIo.BYTER_TryReadU8(out iSpec))
                {   /* The payload ended where a table specifier belongs. */
                    sReason = "JPEG: DHT ended where a table specifier was expected";
                    return(false);
                }

                iClass = (iSpec >> 4) & 0x0F;
                iTable = iSpec & 0x0F;

                if (iClass > 1)
                {   /* Class is 0 for DC and 1 for AC; there is no third. */
                    sReason = "JPEG: DHT declares table class " + iClass.ToString() +
                              ", which is neither 0 (DC) nor 1 (AC)";
                    return(false);
                }

                if (iTable >= JPEG_MAX_HUFF_TABLES)
                {   /* Th is two bits of meaning. */
                    sReason = "JPEG: DHT defines table " + iTable.ToString() +
                              ", but only 0 to 3 exist";
                    return(false);
                }

                iTotal = 0;

                for (i = 0; i < JPEG_HUFFMAN_LENGTHS; i++)
                {   /* Sixteen counts: how many codes are i+1 bits long. */
                    if (!brIo.BYTER_TryReadU8(out arrCounts[i]))
                    {   /* The segment ended inside the histogram. */
                        sReason = "JPEG: DHT ended inside the code-length counts of table " +
                                  iTable.ToString();
                        return(false);
                    }

                    iTotal = iTotal + arrCounts[i];
                }

                if (iTotal > JPEG_MAX_SYMBOLS)
                {   /* More symbols than a byte can name. stb notes this     */
                    /* explicitly as a write-past-the-end guard, and it is   */
                    /* the check that stops a crafted DHT overrunning the    */
                    /* value array below.                                    */
                    sReason = "JPEG: DHT table " + iTable.ToString() + " declares " +
                              iTotal.ToString() + " symbols, more than the " +
                              JPEG_MAX_SYMBOLS.ToString() + " a byte can name";
                    return(false);
                }

                /* One specifier byte plus sixteen counts plus the symbols. */
                iLeft = iLeft - 1 - JPEG_HUFFMAN_LENGTHS - iTotal;

                if (iLeft < 0)
                {   /* The segment is shorter than the tables it declares. */
                    sReason = "JPEG: DHT table " + iTable.ToString() +
                              " runs past the end of its segment";
                    return(false);
                }

                tblNew = new JpegHuffmanTable();

                if (!JPEG_TryBuildHuffmanTable(arrCounts, tblNew, out sReason))
                {   /* Code lengths that describe no valid Huffman code. */
                    sReason = "JPEG: DHT table " + iTable.ToString() + ": " + sReason;
                    return(false);
                }

                for (i = 0; i < iTotal; i++)
                {   /* The symbol values, in code order. */
                    if (!brIo.BYTER_TryReadU8(out iSpec))
                    {   /* The length arithmetic above should prevent this. */
                        sReason = "JPEG: DHT table " + iTable.ToString() +
                                  " ended at symbol " + i.ToString();
                        return(false);
                    }

                    tblNew.JPEGHT_arrValues[i] = (byte)iSpec;
                }

                if (iClass == JPEG_HUFFMAN_CLASS_AC)
                {   /* An AC table also gets the fast-AC accelerator, built    */
                    /* here rather than at first use because it depends only   */
                    /* on the table and the table is now complete.  This is    */
                    /* where the source builds it too.  A DC table leaves      */
                    /* JPEGHT_arrFastAc null, which is what makes a            */
                    /* class mix-up fail loudly instead of decoding noise.     */
                    JPEG_BuildFastAc(tblNew);
                }

                if (arrHuffIo != null)
                {   /* DC tables occupy the first four slots, AC the next    */
                    /* four, so a component's two indices are one addition   */
                    /* each rather than a branch.                            */
                    iSlot = (iClass * JPEG_MAX_HUFF_TABLES) + iTable;
                    arrHuffIo[iSlot] = tblNew;
                }
            }

            if (iLeft != 0)
            {   /* Trailing bytes inside the segment that belong to no table. */
                sReason = "JPEG: DHT has " + (-iLeft).ToString() +
                          " bytes left over after its tables";
                return(false);
            }

            /* Every table in the segment built cleanly. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* JPEG_TryBuildHuffmanTable:                                         */
        /*                                                                    */
        /* Turns the sixteen code-length counts a DHT segment carries into    */
        /* the tables the bit reader walks.                                   */
        /*                                                                    */
        /* Transcoded from stb's stbi__build_huffman, which is itself the     */
        /* specification's Annex C procedure with an acceleration table       */
        /* bolted on.                                                         */
        /*                                                                    */
        /* THE CODES ARE NOT IN THE FILE.  This is the part worth             */
        /* understanding before reading the loops.  DHT gives only a          */
        /* histogram of code LENGTHS and the symbol values in order; the      */
        /* codes themselves are reconstructed by a rule both ends agree on -- */
        /* start at zero, hand out consecutive codes to every symbol of the   */
        /* current length, then shift left by one and move to the next        */
        /* length.  That canonical assignment is why a JPEG's Huffman tables  */
        /* cost seventeen bytes plus the symbols instead of a code per        */
        /* symbol.                                                            */
        /*                                                                    */
        /* THE THREE OUTPUTS SERVE ONE LOOKUP EACH:                           */
        /*                                                                    */
        /*   arrMaxCode[n]  the first code too large to be n bits, shifted up */
        /*                  to bit 16 so the reader can compare it against    */
        /*                  its bit buffer without shifting the buffer;       */
        /*   arrDelta[n]    what to add to an n-bit code to get the index of  */
        /*                  its symbol, which turns a search into an add;     */
        /*   arrFast[]      a direct-mapped table over the next nine bits,    */
        /*                  resolving the short codes -- almost all of them   */
        /*                  in practice -- in a single indexed read.          */
        /*                                                                    */
        /* THE OVERLONG-CODE CHECK IS NOT COSMETIC.  If the counts describe   */
        /* more codes of some length than that length can express, the        */
        /* canonical assignment overflows and every code after it is wrong.   */
        /* stb catches it with the code-1 test kept here; without it a        */
        /* crafted table decodes to plausible garbage.                        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     arrCounts  : Sixteen counts, one per code length 1 to 16.      */
        /*     tblIo      : Table to fill in; its arrays are allocated here.  */
        /*     sReason    : Receives "" or why the counts are not a code.     */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool       : true when the counts describe a valid code.       */
        /*--------------------------------------------------------------------*/

        private static bool JPEG_TryBuildHuffmanTable(int[] arrCounts, JpegHuffmanTable tblIo,
                                                      out string sReason)
        {
            uint uCode;
            int  iSymbol;
            int  iFirst;
            int  iSpan;
            int  iLength;
            int  i;
            int  j;

            sReason = "";

            tblIo.JPEGHT_arrFast    = new byte[JPEG_HUFFMAN_FAST_SIZE];
            tblIo.JPEGHT_arrCode    = new int[JPEG_MAX_SYMBOLS];
            tblIo.JPEGHT_arrValues  = new byte[JPEG_MAX_SYMBOLS];
            tblIo.JPEGHT_arrSize    = new byte[JPEG_MAX_SYMBOLS + 1];
            tblIo.JPEGHT_arrMaxCode = new uint[JPEG_HUFFMAN_LENGTHS + 2];
            tblIo.JPEGHT_arrDelta   = new int[JPEG_HUFFMAN_LENGTHS + 1];

            /*----------------------------------------------------------------*/
            /* Expand the histogram into a per-symbol length list. Symbol k   */
            /* is arrSize[k] bits long, and the list is terminated by a zero  */
            /* so the assignment loop below can test for the end without      */
            /* carrying the count.                                            */
            /*----------------------------------------------------------------*/

            iSymbol = 0;

            for (i = 0; i < JPEG_HUFFMAN_LENGTHS; i++)
            {   /* One code length per iteration. */
                for (j = 0; j < arrCounts[i]; j++)
                {   /* One symbol of that length per iteration. */
                    if (iSymbol >= JPEG_MAX_SYMBOLS)
                    {   /* The caller checked the total, so this is a second   */
                        /* line rather than the only one -- and it is here     */
                        /* because the array write below is what it guards.    */
                        sReason = "the code-length counts describe more than " +
                                  JPEG_MAX_SYMBOLS.ToString() + " symbols";
                        return(false);
                    }

                    tblIo.JPEGHT_arrSize[iSymbol] = (byte)(i + 1);
                    iSymbol = iSymbol + 1;
                }
            }

            tblIo.JPEGHT_arrSize[iSymbol]  = 0;
            tblIo.JPEGHT_iSymbolCount = iSymbol;

            /*----------------------------------------------------------------*/
            /* Assign the canonical codes, shortest length first.             */
            /*----------------------------------------------------------------*/

            uCode   = 0u;
            iSymbol = 0;

            for (iLength = 1; iLength <= JPEG_HUFFMAN_LENGTHS; iLength++)
            {   /* One code length per iteration. */

                /* What to add to a code of this length to reach its symbol's   */
                /* index. Computed before the codes are handed out, because     */
                /* it is the difference between where this length's codes       */
                /* START and where its symbols start.                           */
                tblIo.JPEGHT_arrDelta[iLength] = iSymbol - (int)uCode;

                if (tblIo.JPEGHT_arrSize[iSymbol] == iLength)
                {   /* This length has at least one symbol. */
                    while (tblIo.JPEGHT_arrSize[iSymbol] == iLength)
                    {   /* Consecutive codes to consecutive symbols. */
                        tblIo.JPEGHT_arrCode[iSymbol] = (int)uCode;
                        uCode   = uCode + 1u;
                        iSymbol = iSymbol + 1;
                    }

                    if ((uCode - 1u) >= (1u << iLength))
                    {   /* More codes of this length than the length can       */
                        /* express. Everything after this point would be       */
                        /* wrong, so the table is refused rather than built.   */
                        sReason = "the code-length counts overflow at length " +
                                  iLength.ToString() + ", which cannot express " +
                                  "that many codes";
                        return(false);
                    }
                }

                /* The first code too large to be this length, pre-shifted so   */
                /* the bit reader compares it against a left-aligned window     */
                /* without shifting anything at run time.                       */
                tblIo.JPEGHT_arrMaxCode[iLength] = uCode << (16 - iLength);

                uCode = uCode << 1;
            }

            /* One past the longest length: nothing can exceed this, which is   */
            /* what ends the reader's search when a code is 16 bits.            */
            tblIo.JPEGHT_arrMaxCode[JPEG_HUFFMAN_LENGTHS + 1] = 0xFFFFFFFFu;

            /*-----------------------------------------------------------------*/
            /* The acceleration table. Every entry starts as "not accelerated" */
            /* and the short codes then claim their spans.                     */
            /*-----------------------------------------------------------------*/

            for (i = 0; i < JPEG_HUFFMAN_FAST_SIZE; i++)
            {   /* Not accelerated until a short code claims it. */
                tblIo.JPEGHT_arrFast[i] = JPEG_HUFFMAN_NOT_FAST;
            }

            for (i = 0; i < iSymbol; i++)
            {   /* One symbol per iteration. */
                iLength = (int)tblIo.JPEGHT_arrSize[i];

                if (iLength <= JPEG_HUFFMAN_FAST_BITS)
                {   /* A code short enough to resolve in one lookup. It     */
                    /* occupies every fast-table slot whose leading bits    */
                    /* match it, which is 2^(FAST_BITS - length) of them.   */
                    iFirst = tblIo.JPEGHT_arrCode[i] << (JPEG_HUFFMAN_FAST_BITS - iLength);
                    iSpan  = 1 << (JPEG_HUFFMAN_FAST_BITS - iLength);

                    for (j = 0; j < iSpan; j++)
                    {   /* Every slot the code covers names this symbol. */
                        tblIo.JPEGHT_arrFast[iFirst + j] = (byte)i;
                    }
                }
            }

            /* A table the bit reader can walk. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* JPEG_BuildFastAc:                                                  */
        /*                                                                    */
        /* Fills an AC table's fast-AC accelerator from its already-built     */
        /* plain fast table.  See the note on JpegHuffmanTable for what the   */
        /* packed entries mean and why the accelerator exists.                */
        /*                                                                    */
        /* This is NOT a Try function because it cannot fail.  Every input it */
        /* reads has already been validated by JPEG_TryBuildHuffmanTable, and */
        /* an entry that will not fit the packing is simply left at zero,     */
        /* which is the defined "use the slow path" value rather than an      */
        /* error.  That is the same reason JPEG_arrDezigzag needs no bounds   */
        /* check: the fallback is correct, only slower.                       */
        /*                                                                    */
        /* WHY THE WHOLE THING IS PRECOMPUTED rather than derived per symbol  */
        /* at decode time: the table has 512 entries and is built once per    */
        /* image, while the lookup it replaces runs once per non-zero         */
        /* coefficient -- tens of thousands of times in a small photograph.   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     tblIo  : The AC table to fill.  Its JPEGHT_arrFast,            */
        /*              JPEGHT_arrValues and JPEGHT_arrSize must already be   */
        /*              built; JPEGHT_arrFastAc is allocated here and         */
        /*              overwritten in full, so the caller need not clear it. */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void   : The table is filled in place.                         */
        /*--------------------------------------------------------------------*/

        private static void JPEG_BuildFastAc(JpegHuffmanTable tblIo)
        {
            short[] arrFastAc;
            int     iFastSlot;
            int     iSymbol;
            int     iRun;
            int     iMagBits;
            int     iLength;
            int     iValue;
            int     iHalf;
            int     i;

            arrFastAc = new short[JPEG_HUFFMAN_FAST_SIZE];

            for (i = 0; i < JPEG_HUFFMAN_FAST_SIZE; i++)
            {   /* One candidate nine-bit prefix per iteration. */
                arrFastAc[i] = 0;
                iFastSlot    = (int)tblIo.JPEGHT_arrFast[i];

                if (iFastSlot == JPEG_HUFFMAN_NOT_FAST)
                {   /* No code short enough starts with these nine bits, so     */
                    /* the slow walk has to run and there is nothing to pack.   */
                    continue;
                }

                /* The symbol byte splits into a zero-run and a magnitude     */
                /* width, which is the AC coding the specification defines:   */
                /* high nibble is how many zero coefficients to skip, low     */
                /* nibble is how many bits the coefficient itself occupies.   */
                iSymbol  = (int)tblIo.JPEGHT_arrValues[iFastSlot];
                iRun     = (iSymbol >> 4) & 0x0F;
                iMagBits = iSymbol & 0x0F;
                iLength  = (int)tblIo.JPEGHT_arrSize[iFastSlot];

                if (iMagBits == 0 || (iLength + iMagBits) > JPEG_HUFFMAN_FAST_BITS)
                {   /* Either the symbol carries no coefficient at all (EOB     */
                    /* and ZRL live here) or the code plus its magnitude bits   */
                    /* run past the nine we peeked at, so the combined form     */
                    /* cannot be resolved from this lookup.  Leave it zero.     */
                    continue;
                }

                /* Pull the magnitude bits out of the prefix.  Shifting left    */
                /* by the code length discards the code itself and leaves the   */
                /* magnitude at the top of the nine-bit field; shifting right   */
                /* then brings it down to its own width.                        */
                iValue = ((i << iLength) & (JPEG_HUFFMAN_FAST_SIZE - 1)) >>
                         (JPEG_HUFFMAN_FAST_BITS - iMagBits);

                /* The EXTEND rule, and it is not two's complement.  A raw      */
                /* value in the top half of the range is already positive; a    */
                /* value in the bottom half is negative and is biased down by   */
                /* one less than twice the half.  This is the same rule         */
                /* JPEGBR_TryReceiveExtend applies, written out here because    */
                /* the fast path never calls it -- and BOTH must agree, which   */
                /* is why they are tested against each other.                   */
                iHalf = 1 << (iMagBits - 1);

                if (iValue < iHalf)
                {   /* Bottom half: negative, so bias it down. */
                    iValue = iValue + (int)(~0u << iMagBits) + 1;
                }

                if (iValue >= -128 && iValue <= 127)
                {   /* Fits the signed byte the packing reserves for it, so   */
                    /* this prefix can be resolved in one lookup.  A          */
                    /* coefficient outside that range is rare and falls to    */
                    /* the slow path, which handles any width.                */
                    arrFastAc[i] = (short)((iValue * 256) + (iRun * 16) +
                                           (iLength + iMagBits));
                }
            }

            tblIo.JPEGHT_arrFastAc = arrFastAc;
        }

        /*--------------------------------------------------------------------*/
        /* JPEG_AddIntsValid:                                                 */
        /*                                                                    */
        /* Reports whether two ints can be added without overflowing an int.  */
        /* Used on the DC predictor chain, where a corrupt file can otherwise */
        /* drive the running total past the type's range.                     */
        /*                                                                    */
        /* WHY THIS EXISTS AT ALL, since C# integer overflow is defined to    */
        /* wrap rather than to trap: a wrapped DC predictor is not a crash,   */
        /* it is a SILENTLY WRONG IMAGE -- the block where it wraps and every */
        /* block after it decode to the wrong brightness, because the         */
        /* predictor is a running sum that never resets except at a restart.  */
        /* Refusing is the honest answer and matches this library's contract  */
        /* that malformed input is reported, not guessed at.                  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iA     : First addend, the running DC predictor.               */
        /*     iB     : Second addend, this block's decoded DC difference.    */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool   : true when iA + iB is representable, false when the    */
        /*              sum would overflow in either direction.               */
        /*--------------------------------------------------------------------*/

        private static bool JPEG_AddIntsValid(int iA, int iB)
        {
            if ((iA >= 0) != (iB >= 0))
            {   /* Opposite signs, so the sum lies between them and cannot   */
                /* leave the range whichever way it goes.                    */
                return(true);
            }

            if (iA < 0 && iB < 0)
            {   /* Both negative: the sum is safe while it stays at or above   */
                /* the minimum.  Written as a subtraction from the minimum     */
                /* rather than as an addition, because the addition is the     */
                /* very thing being tested; iB is negative here, so the        */
                /* subtraction itself cannot overflow.                         */
                return(iA >= (int.MinValue - iB));
            }

            /* Both non-negative: mirror image of the case above. */
            return(iA <= (int.MaxValue - iB));
        }

        /*--------------------------------------------------------------------*/
        /* JPEG_Mul2ShortsValid:                                              */
        /*                                                                    */
        /* Reports whether a coefficient times a quantisation factor still    */
        /* fits in a short, which is the width the coefficient block uses.    */
        /*                                                                    */
        /* A dequantised coefficient is defined to fit -- the specification's */
        /* ranges guarantee it for any file an encoder produced -- so a       */
        /* failure here always means damaged data.  Checking is cheap and the */
        /* alternative is a truncated coefficient that produces a plausible   */
        /* block with the wrong contents, which is the worst kind of wrong.   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iA     : The coefficient as decoded, before dequantisation.    */
        /*     iB     : The quantisation table entry it is multiplied by.     */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool   : true when the product fits a short, false otherwise.  */
        /*--------------------------------------------------------------------*/

        private static bool JPEG_Mul2ShortsValid(int iA, int iB)
        {
            if (iB == 0 || iB == -1)
            {   /* Zero always fits.  Minus one is excluded separately        */
                /* because the divisions below would themselves overflow on   */
                /* it, the classic minimum-over-minus-one case.               */
                return(true);
            }

            if ((iA >= 0) == (iB >= 0))
            {   /* Same signs, so the product is positive and only the upper   */
                /* bound can be crossed.                                       */
                return(iA <= (short.MaxValue / iB));
            }

            if (iB < 0)
            {   /* Mixed signs with a negative multiplier. */
                return(iA <= (short.MinValue / iB));
            }

            /* Mixed signs with a positive multiplier. */
            return(iA >= (short.MinValue / iB));
        }

        /*--------------------------------------------------------------------*/
        /* JPEG_TryDecodeBlock:                                               */
        /*                                                                    */
        /* Decodes one 8x8 block of an entropy-coded baseline scan into       */
        /* DEQUANTISED coefficients in NATURAL order.  This is the heart of   */
        /* JPEG decoding and everything above it is framing.                  */
        /*                                                                    */
        /* THE BLOCK IS NOT STORED AS SIXTY-FOUR NUMBERS.  It is stored as a  */
        /* DC term coded as a DIFFERENCE from the previous block's DC, then a */
        /* series of (run, value) pairs naming only the non-zero AC           */
        /* coefficients, in zig-zag order, ending at an end-of-block symbol   */
        /* the moment nothing but zeroes remains.  Three consequences follow  */
        /* and each is a place this function can go wrong invisibly:          */
        /*                                                                    */
        /*   1. THE DC IS A RUNNING TOTAL across the whole scan, per          */
        /*      component, reset only at a restart marker.  Lose one block's  */
        /*      difference and every later block in that component is wrong,  */
        /*      not just the one -- which is why a bad block is refused here  */
        /*      rather than skipped.                                          */
        /*   2. THE ZIG-ZAG IS A SCATTER, not a reordering afterwards.  The   */
        /*      k-th coefficient in the stream belongs at                     */
        /*      JPEG_arrDezigzag[k] in the block, and writing it at k instead */
        /*      produces a picture that is recognisable but wrong everywhere, */
        /*      which is exactly the kind of error a tolerance absorbs.       */
        /*   3. DEQUANTISATION HAPPENS HERE, not later, because the           */
        /*      quantisation table is already in natural order and the        */
        /*      scatter has the index in hand.  One multiply, no second pass. */
        /*                                                                    */
        /* Transcoded from stbi__jpeg_decode_block, including its two         */
        /* overflow guards and its fast-AC path.                              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     brdIo        : The entropy bit reader, advanced past this      */
        /*                    block's bits.  Its stall and truncation state   */
        /*                    is what distinguishes a finished scan from a    */
        /*                    broken file.                                    */
        /*     tblDc        : Huffman table for this component's DC symbols.  */
        /*     tblAc        : Huffman table for its AC symbols.  Must be an   */
        /*                    AC table -- its JPEGHT_arrFastAc is consulted.  */
        /*     arrDequant   : The 64-entry quantisation table, ALREADY in     */
        /*                    natural order (JPEG_TryParseDqt de-zigzags it   */
        /*                    on load, so no second de-zigzag happens here).  */
        /*     arrDcPredIo  : Per-component running DC predictors.  Read and  */
        /*                    updated at iComponent.                          */
        /*     iComponent   : Which predictor this block belongs to.          */
        /*     arrBlockOut  : Caller-owned 64-entry destination, overwritten  */
        /*                    in full including the zeroes, so the caller may */
        /*                    and should reuse one buffer for the whole scan. */
        /*     sReason      : Set to the cause when the return is false.      */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool         : true when a whole block was decoded; false on   */
        /*                    a corrupt code, an out-of-range DC symbol, an   */
        /*                    overflow, or a bit reader that refused.         */
        /*--------------------------------------------------------------------*/

        public static bool JPEG_TryDecodeBlock(JpegBitReader brdIo,
                                               JpegHuffmanTable tblDc,
                                               JpegHuffmanTable tblAc,
                                               int[] arrDequant, int[] arrDcPredIo,
                                               int iComponent, short[] arrBlockOut,
                                               out string sReason)
        {
            int iSymbol;
            int iDiff;
            int iDc;
            int iPacked;
            int iPrefix;
            int iRun;
            int iMagBits;
            int iZig;
            int iValue;
            int k;
            int i;

            sReason = "";

            if (!brdIo.JPEGBR_TryDecodeHuffman(tblDc, out iSymbol, out sReason))
            {   /* The DC code itself is unreadable, so nothing about this   */
                /* block can be trusted.                                     */
                return(false);
            }

            if (iSymbol < 0 || iSymbol > JPEG_HUFFMAN_LENGTHS)
            {   /* A DC symbol is a MAGNITUDE WIDTH, so it cannot exceed the   */
                /* sixteen bits a coefficient can occupy.  A larger value      */
                /* means the table and the stream disagree.                    */
                sReason = "JPEG: DC magnitude width " + iSymbol.ToString() +
                          " is out of range for component " + iComponent.ToString();
                return(false);
            }

            /* Clear the whole block before anything is scattered into it.      */
            /* The stream names only non-zero coefficients, so every position   */
            /* it does NOT name has to be zero already -- and the block is a    */
            /* reused buffer, so last block's values are sitting in it.         */
            for (i = 0; i < JPEG_BLOCK_SAMPLES; i++)
            {   /* One coefficient slot per iteration. */
                arrBlockOut[i] = 0;
            }

            if (iSymbol == 0)
            {   /* Width zero is the DC unchanged from the previous block.   */
                /* It consumes no further bits, which is a real case worth   */
                /* getting right -- a flat region codes every DC this way.   */
                iDiff = 0;
            }
            else
            {   /* A width of n means n bits follow, EXTEND-decoded. */
                if (!brdIo.JPEGBR_TryReceiveExtend(iSymbol, out iDiff, out sReason))
                {   /* The magnitude bits ran out or the reader refused. */
                    return(false);
                }
            }

            if (!JPEG_AddIntsValid(arrDcPredIo[iComponent], iDiff))
            {   /* The running predictor would overflow, so the file is     */
                /* damaged; see JPEG_AddIntsValid for why this is refused   */
                /* rather than wrapped.                                     */
                sReason = "JPEG: DC predictor overflow on component " +
                          iComponent.ToString();
                return(false);
            }

            iDc                     = arrDcPredIo[iComponent] + iDiff;
            arrDcPredIo[iComponent] = iDc;

            if (!JPEG_Mul2ShortsValid(iDc, arrDequant[0]))
            {   /* The dequantised DC would not fit the block's element type. */
                sReason = "JPEG: dequantised DC out of range on component " +
                          iComponent.ToString();
                return(false);
            }

            /* The DC always lands at index 0, which is where the zig-zag   */
            /* starts, so it needs no scatter.                              */
            arrBlockOut[0] = (short)(iDc * arrDequant[0]);

            /* AC coefficients, from position 1 up to at most 63.  A do-while   */
            /* rather than a for, matching the source: the end-of-block         */
            /* symbol breaks out from the middle, and position 63 is a legal    */
            /* place to land as well as a legal place to stop.                  */
            k = 1;

            do
            {   /* One (run, value) pair per iteration, or one fast-AC hit.   */
                /* A peek, not a read: it tops the window up and consumes     */
                /* nothing, so it cannot fail.  A stalled reader hands back   */
                /* padding, which indexes the table's "slow path" entry and   */
                /* falls through to a read that does check.                   */
                brdIo.JPEGBR_PeekFast(out iPrefix);

                iPacked = (int)tblAc.JPEGHT_arrFastAc[iPrefix];

                if (iPacked != 0)
                {   /* FAST PATH: this prefix resolves a whole (run, value)   */
                    /* pair, so the run, the coefficient and the exact bit    */
                    /* cost all come out of one table read.                   */
                    k = k + ((iPacked >> 4) & 15);

                    if (!brdIo.JPEGBR_TrySpendBits(iPacked & 15, out sReason))
                    {   /* The packed length exceeded what the window holds,   */
                        /* which means the stream ended inside this code.      */
                        return(false);
                    }

                    /* The scatter.  k is post-incremented so the next pair   */
                    /* starts after this coefficient's slot.                  */
                    iZig                 = (int)JPEG_arrDezigzag[k];
                    k                    = k + 1;
                    arrBlockOut[iZig]    = (short)((iPacked >> 8) * arrDequant[iZig]);
                }
                else
                {   /* SLOW PATH: the code is longer than the fast table        */
                    /* covers, or its coefficient is too wide to pack, so the   */
                    /* symbol and the magnitude are read separately.            */
                    if (!brdIo.JPEGBR_TryDecodeHuffman(tblAc, out iSymbol, out sReason))
                    {   /* An AC code the table does not contain. */
                        return(false);
                    }

                    iMagBits = iSymbol & 0x0F;
                    iRun     = iSymbol >> 4;

                    if (iMagBits == 0)
                    {   /* No magnitude bits: the symbol is one of the two   */
                        /* structural ones rather than a coefficient.        */
                        if (iRun != 15)
                        {   /* END OF BLOCK.  Everything from here to 63 is   */
                            /* zero and is already zero, so stop.             */
                            break;
                        }

                        /* ZRL: sixteen zeroes in a row, named as one symbol   */
                        /* because a run nibble only reaches fifteen.  It      */
                        /* writes nothing -- it only advances the position.    */
                        k = k + 16;
                    }
                    else
                    {   /* A real coefficient, preceded by iRun zeroes. */
                        k = k + iRun;

                        iZig = (int)JPEG_arrDezigzag[k];
                        k    = k + 1;

                        if (!brdIo.JPEGBR_TryReceiveExtend(iMagBits, out iValue, out sReason))
                        {   /* The magnitude bits ran out mid-coefficient. */
                            return(false);
                        }

                        if (!JPEG_Mul2ShortsValid(iValue, arrDequant[iZig]))
                        {   /* Dequantising would not fit a short. */
                            sReason = "JPEG: dequantised AC coefficient at zig-zag " +
                                      k.ToString() + " is out of range";
                            return(false);
                        }

                        arrBlockOut[iZig] = (short)(iValue * arrDequant[iZig]);
                    }
                }
            }
            while (k < JPEG_BLOCK_SAMPLES);

            /* A complete block: DC plus every AC the stream named, all   */
            /* dequantised and all in natural order.                      */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* JPEG_TryDecodeBlockProgDc:                                         */
        /*                                                                    */
        /* Decodes one block's DC contribution from a PROGRESSIVE scan, which */
        /* is either the first approximation of the value or one more bit of  */
        /* an approximation an earlier scan started.                          */
        /*                                                                    */
        /* Transcoded from stbi__jpeg_decode_block_prog_dc.                   */
        /*                                                                    */
        /* THE TWO CASES ARE DISTINGUISHED BY Ah, AND THEY ARE NOT VARIATIONS */
        /* OF ONE ANOTHER.  With Ah zero this is the first scan to touch the  */
        /* DC, so it clears the whole block and runs the ordinary difference  */
        /* chain, shifted up by Al to leave room for the bits later scans     */
        /* will add underneath.  With Ah non-zero the value already exists    */
        /* and the scan carries exactly ONE raw bit per block, which is OR-ed */
        /* into position Al -- no Huffman code, no predictor, no clearing.    */
        /* Clearing the block in the refinement case would erase every AC     */
        /* coefficient decoded so far, which is the single most destructive   */
        /* mistake available in this function and produces an image that is   */
        /* recognisable but flat.                                             */
        /*                                                                    */
        /* Ss MUST BE ZERO HERE.  A scan carrying the DC may carry nothing    */
        /* else, and one that claims to do both is malformed rather than      */
        /* merely unusual -- the two use different code tables and different  */
        /* refinement rules, so there is no sensible way to serve both.       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     brdIo       : The entropy reader, positioned at this block.    */
        /*     tblDc       : The DC Huffman table this scan selected.  Unused */
        /*                   in the refinement case, which reads raw bits.    */
        /*     arrDcPredIo : Per-component DC predictors, updated here.       */
        /*     iComponent  : Index of the component this block belongs to,    */
        /*                   used to pick the predictor and to name faults.   */
        /*     schIn       : The scan header, for Ss, Ah and Al.              */
        /*     arrBlockIo  : The block's 64 raw coefficients, natural order,  */
        /*                   carried over from earlier scans and refined in   */
        /*                   place.                                           */
        /*     sReason     : Receives the cause when the return is false.     */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool        : true when the block's DC contribution was read;  */
        /*                   false when the stream is malformed.              */
        /*--------------------------------------------------------------------*/

        public static bool JPEG_TryDecodeBlockProgDc(JpegBitReader brdIo,
                                                     JpegHuffmanTable tblDc,
                                                     int[] arrDcPredIo, int iComponent,
                                                     JpegScanHeader schIn,
                                                     short[] arrBlockIo,
                                                     out string sReason)
        {
            bool bBit;
            int  iSymbol;
            int  iDiff;
            int  iDc;
            int  iScale;
            int  i;

            sReason = "";

            if (schIn.JPEGS_iSpectralEnd != 0)
            {   /* A DC scan that also claims a spectral band. See the header   */
                /* for why this cannot be served rather than merely being       */
                /* unusual.                                                     */
                sReason = "JPEG: a progressive DC scan may not also carry AC " +
                          "coefficients (Se is " +
                          schIn.JPEGS_iSpectralEnd.ToString() + ", must be 0)";
                return(false);
            }

            if (schIn.JPEGS_iApproxHigh == 0)
            {   /* FIRST DC SCAN for these blocks: the ordinary difference    */
                /* chain, then a shift to leave room for later refinements.   */
                for (i = 0; i < JPEG_BLOCK_SAMPLES; i++)
                {   /* Every AC slot starts empty; later scans fill them in. */
                    arrBlockIo[i] = 0;
                }

                if (!brdIo.JPEGBR_TryDecodeHuffman(tblDc, out iSymbol, out sReason))
                {   /* The DC code is unreadable. */
                    return(false);
                }

                if (iSymbol < 0 || iSymbol > 15)
                {   /* A DC symbol is a magnitude WIDTH. stb caps this at 15   */
                    /* rather than 16 on the progressive path; the width is    */
                    /* about to be shifted left by Al, so the tighter bound    */
                    /* is what keeps the shifted value inside a short.         */
                    sReason = "JPEG: progressive DC magnitude width " +
                              iSymbol.ToString() + " is out of range for component " +
                              iComponent.ToString();
                    return(false);
                }

                if (iSymbol == 0)
                {   /* Width zero: the DC is unchanged from the last block. */
                    iDiff = 0;
                }
                else
                {   /* A width of n means n bits follow, EXTEND-decoded. */
                    if (!brdIo.JPEGBR_TryReceiveExtend(iSymbol, out iDiff, out sReason))
                    {   /* The magnitude bits ran out. */
                        return(false);
                    }
                }

                if (!JPEG_AddIntsValid(arrDcPredIo[iComponent], iDiff))
                {   /* The running predictor would overflow. */
                    sReason = "JPEG: progressive DC predictor overflow on component " +
                              iComponent.ToString();
                    return(false);
                }

                iDc                     = arrDcPredIo[iComponent] + iDiff;
                arrDcPredIo[iComponent] = iDc;

                /* The shift is written as a multiply by a power of two,        */
                /* matching the source, because that is the form the overflow   */
                /* guard below takes and the two must agree about the value     */
                /* being checked.                                               */
                iScale = 1 << schIn.JPEGS_iApproxLow;

                if (!JPEG_Mul2ShortsValid(iDc, iScale))
                {   /* The shifted DC would not fit the block's element type. */
                    sReason = "JPEG: progressive DC out of range on component " +
                              iComponent.ToString();
                    return(false);
                }

                arrBlockIo[0] = (short)(iDc * iScale);
            }
            else
            {   /* REFINEMENT: exactly one raw bit, OR-ed in at position Al.   */
                /* No table, no predictor, and emphatically no clearing.       */
                if (!brdIo.JPEGBR_TryGetBit(out bBit, out sReason))
                {   /* The stream ended inside the refinement bit. */
                    return(false);
                }

                if (bBit)
                {   /* The bit is set, so this power of two joins the value. */
                    arrBlockIo[0] = (short)(arrBlockIo[0] +
                                            (short)(1 << schIn.JPEGS_iApproxLow));
                }
            }

            /* This scan's contribution to the block's DC is in place. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* JPEG_TryDecodeBlockProgAc:                                         */
        /*                                                                    */
        /* Decodes one block's contribution to a band of AC coefficients from */
        /* a PROGRESSIVE scan -- either the band's first approximation or one */
        /* more bit of every value already in it.                             */
        /*                                                                    */
        /* Transcoded from stbi__jpeg_decode_block_prog_ac.  This is the      */
        /* hardest function in the JPEG decoder and the two halves below have */
        /* almost nothing in common beyond their vocabulary.                  */
        /*                                                                    */
        /* THE FIRST-PASS HALF (Ah zero) reads like the baseline AC loop with */
        /* two differences: values are shifted up by Al, and the end-of-block */
        /* symbol is replaced by an END-OF-BAND RUN.  A baseline EOB says     */
        /* "the rest of this block is zero"; a progressive EOB run says "this */
        /* band is empty in this block AND in the next N blocks", so it is    */
        /* state that outlives the call and lives on JpegProgressiveState.    */
        /* The run length is 1 << r plus r further raw bits, minus the block  */
        /* being finished now.                                                */
        /*                                                                    */
        /* THE REFINEMENT HALF (Ah non-zero) is a different algorithm         */
        /* wearing similar clothes, and the thing to understand is that it    */
        /* reads bits for coefficients it is NOT introducing.  Every value    */
        /* already non-zero in the band consumes one correction bit, in       */
        /* order, whether or not this scan has anything else to say -- so the */
        /* run length r counts only ZERO positions, and the walk past         */
        /* non-zero ones is not optional bookkeeping but literally where      */
        /* those bits are spent.  A refinement that skipped them would        */
        /* desynchronise the stream within one block.                         */
        /*                                                                    */
        /* THE CORRECTION IS A MAGNITUDE STEP, NOT AN OR.  A set bit adds one */
        /* unit of 1 << Al AWAY FROM ZERO -- upward for a positive value and  */
        /* downward for a negative one -- and only when that bit is not       */
        /* already present. Treating it as a plain OR is correct for positive */
        /* coefficients and wrong for negative ones, which is the kind of     */
        /* fault that leaves an image very slightly and unevenly wrong.       */
        /*                                                                    */
        /* THE r == 15, s == 0 CASE WRITES SIXTEEN ZEROES AND IS DELIBERATELY */
        /* NOT SPECIAL-CASED, matching the source.  The advance-by-r walk     */
        /* below skips fifteen zero positions and then "writes" s, which is   */
        /* zero, into the sixteenth -- so the ordinary path already does what */
        /* ZRL means and a branch for it would only be a chance to differ.    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     brdIo      : The entropy reader, positioned at this block.     */
        /*     tblAc      : The AC Huffman table this scan selected.          */
        /*     schIn      : The scan header, for Ss, Se, Ah and Al.           */
        /*     pgIo       : Frame-wide progressive state; its end-of-band run */
        /*                  is read and written here.                         */
        /*     arrBlockIo : The block's 64 raw coefficients, natural order,   */
        /*                  refined in place.                                 */
        /*     sReason    : Receives the cause when the return is false.      */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool       : true when this scan's contribution was read;      */
        /*                  false when the stream is malformed.               */
        /*--------------------------------------------------------------------*/

        public static bool JPEG_TryDecodeBlockProgAc(JpegBitReader brdIo,
                                                     JpegHuffmanTable tblAc,
                                                     JpegScanHeader schIn,
                                                     JpegProgressiveState pgIo,
                                                     short[] arrBlockIo,
                                                     out string sReason)
        {
            bool bBit;
            int  iShift;
            int  iBitValue;
            int  iPrefix;
            int  iPacked;
            int  iSymbol;
            int  iRun;
            int  iMagBits;
            int  iValue;
            int  iNewValue;
            int  iExtra;
            int  iZig;
            int  k;

            sReason = "";

            if (schIn.JPEGS_iSpectralStart == 0)
            {   /* An AC scan that claims to start at the DC. The two are   */
                /* coded by different rules and cannot share a scan.        */
                sReason = "JPEG: a progressive AC scan may not start at " +
                          "coefficient 0, which is the DC";
                return(false);
            }

            if (schIn.JPEGS_iApproxHigh == 0)
            {   /* FIRST PASS over this band: values arrive whole, shifted up   */
                /* by Al so later refinements have room underneath.             */
                iShift = schIn.JPEGS_iApproxLow;

                if (pgIo.JPEGPG_iEobRun > 0)
                {   /* A previous block's end-of-band run still covers this   */
                    /* block, so the whole band is zero here and NOTHING is   */
                    /* read from the stream.                                  */
                    pgIo.JPEGPG_iEobRun = pgIo.JPEGPG_iEobRun - 1;

                    /* Covered by the run, and correctly left untouched. */
                    return(true);
                }

                k = schIn.JPEGS_iSpectralStart;

                do
                {   /* One (run, value) pair per iteration, or one fast hit. */
                    brdIo.JPEGBR_PeekFast(out iPrefix);

                    iPacked = (int)tblAc.JPEGHT_arrFastAc[iPrefix];

                    if (iPacked != 0)
                    {   /* FAST PATH: run, coefficient and bit cost in one     */
                        /* table read, exactly as the baseline loop does it.   */
                        k = k + ((iPacked >> 4) & 15);

                        if (!brdIo.JPEGBR_TrySpendBits(iPacked & 15, out sReason))
                        {   /* The packed length exceeded the window. */
                            return(false);
                        }

                        iZig = (int)JPEG_arrDezigzag[k];
                        k    = k + 1;

                        arrBlockIo[iZig] = (short)((iPacked >> 8) * (1 << iShift));
                    }
                    else
                    {   /* SLOW PATH: the symbol and magnitude read apart. */
                        if (!brdIo.JPEGBR_TryDecodeHuffman(tblAc, out iSymbol,
                                                           out sReason))
                        {   /* An AC code the table does not contain. */
                            return(false);
                        }

                        iMagBits = iSymbol & 0x0F;
                        iRun     = iSymbol >> 4;

                        if (iMagBits == 0)
                        {   /* No magnitude bits: a structural symbol. */
                            if (iRun < 15)
                            {   /* END-OF-BAND RUN. The band is empty in this   */
                                /* block and in the next (1 << r) - 1 blocks,   */
                                /* with r further bits refining the count.      */
                                pgIo.JPEGPG_iEobRun = 1 << iRun;

                                if (iRun > 0)
                                {   /* A longer run carries its extra length. */
                                    if (!brdIo.JPEGBR_TryGetBits(iRun, out iExtra,
                                                                 out sReason))
                                    {   /* The run length ran off the end. */
                                        return(false);
                                    }

                                    pgIo.JPEGPG_iEobRun = pgIo.JPEGPG_iEobRun + iExtra;
                                }

                                /* This block is the first the run covers, so   */
                                /* it is spent here and the rest carry over.    */
                                pgIo.JPEGPG_iEobRun = pgIo.JPEGPG_iEobRun - 1;
                                break;
                            }

                            /* ZRL: sixteen zeroes, position only. */
                            k = k + 16;
                        }
                        else
                        {   /* A real coefficient after iRun zeroes. */
                            k = k + iRun;

                            iZig = (int)JPEG_arrDezigzag[k];
                            k    = k + 1;

                            if (!brdIo.JPEGBR_TryReceiveExtend(iMagBits, out iValue,
                                                               out sReason))
                            {   /* The magnitude bits ran out. */
                                return(false);
                            }

                            arrBlockIo[iZig] = (short)(iValue * (1 << iShift));
                        }
                    }
                }
                while (k <= schIn.JPEGS_iSpectralEnd);
            }
            else
            {   /* REFINEMENT PASS: one more bit for everything already here,   */
                /* and possibly one newly introduced coefficient.               */
                iBitValue = 1 << schIn.JPEGS_iApproxLow;

                if (pgIo.JPEGPG_iEobRun > 0)
                {   /* The band introduces nothing new in this block -- but   */
                    /* every coefficient ALREADY in it still spends its       */
                    /* correction bit, so this is emphatically not a skip.    */
                    pgIo.JPEGPG_iEobRun = pgIo.JPEGPG_iEobRun - 1;

                    for (k = schIn.JPEGS_iSpectralStart;
                         k <= schIn.JPEGS_iSpectralEnd; k++)
                    {   /* One band position per iteration. */
                        iZig = (int)JPEG_arrDezigzag[k];

                        if (arrBlockIo[iZig] != 0)
                        {   /* A live coefficient, so a bit is waiting. */
                            if (!brdIo.JPEGBR_TryGetBit(out bBit, out sReason))
                            {   /* The stream ended inside a correction bit. */
                                return(false);
                            }

                            JPEG_ApplyRefinementBit(arrBlockIo, iZig, bBit, iBitValue);
                        }
                    }
                }
                else
                {   /* The ordinary refinement walk: symbols name runs of      */
                    /* ZERO positions and introduce at most one coefficient.   */
                    k = schIn.JPEGS_iSpectralStart;

                    do
                    {   /* One symbol, then the advance it describes. */
                        if (!brdIo.JPEGBR_TryDecodeHuffman(tblAc, out iSymbol,
                                                           out sReason))
                        {   /* An AC code the table does not contain. */
                            return(false);
                        }

                        iMagBits  = iSymbol & 0x0F;
                        iRun      = iSymbol >> 4;
                        iNewValue = 0;

                        if (iMagBits == 0)
                        {   /* Structural: an end-of-band run, or a ZRL. */
                            if (iRun < 15)
                            {   /* END-OF-BAND RUN, one shorter than the       */
                                /* first-pass form because this block is       */
                                /* finished by the advance below rather than   */
                                /* by breaking out.                            */
                                pgIo.JPEGPG_iEobRun = (1 << iRun) - 1;

                                if (iRun > 0)
                                {   /* Extra length bits, as in the first pass. */
                                    if (!brdIo.JPEGBR_TryGetBits(iRun, out iExtra,
                                                                 out sReason))
                                    {   /* The run length ran off the end. */
                                        return(false);
                                    }

                                    pgIo.JPEGPG_iEobRun = pgIo.JPEGPG_iEobRun + iExtra;
                                }

                                /* Force the advance to run to the end of the   */
                                /* band, spending every correction bit on the   */
                                /* way, and introduce nothing.                  */
                                iRun = JPEG_BLOCK_SAMPLES;
                            }

                            /* r == 15 with s == 0 needs no branch: see the   */
                            /* function header.                               */
                        }
                        else
                        {   /* A coefficient is being introduced. */
                            if (iMagBits != 1)
                            {   /* A refinement can only ever ADD one unit at   */
                                /* position Al, so the only legal magnitude     */
                                /* width is 1. Anything else means the stream   */
                                /* and the table disagree.                      */
                                sReason = "JPEG: progressive AC refinement " +
                                          "magnitude width " + iMagBits.ToString() +
                                          " is illegal; only 1 is defined";
                                return(false);
                            }

                            if (!brdIo.JPEGBR_TryGetBit(out bBit, out sReason))
                            {   /* The stream ended inside the sign bit. */
                                return(false);
                            }

                            if (bBit)
                            {   /* Sign bit set: the new coefficient is   */
                                /* positive.                              */
                                iNewValue = iBitValue;
                            }
                            else
                            {   /* Clear: negative. */
                                iNewValue = -iBitValue;
                            }
                        }

                        /* ADVANCE BY iRun, spending a correction bit at every    */
                        /* live coefficient passed and counting down only the     */
                        /* zero ones, then plant the new value in the zero slot   */
                        /* the run lands on.                                      */
                        while (k <= schIn.JPEGS_iSpectralEnd)
                        {   /* One band position per iteration. */
                            iZig = (int)JPEG_arrDezigzag[k];
                            k    = k + 1;

                            if (arrBlockIo[iZig] != 0)
                            {   /* Live: this position owes a correction bit   */
                                /* and does NOT count against the run.         */
                                if (!brdIo.JPEGBR_TryGetBit(out bBit, out sReason))
                                {   /* The stream ended inside a correction. */
                                    return(false);
                                }

                                JPEG_ApplyRefinementBit(arrBlockIo, iZig, bBit,
                                                        iBitValue);
                            }
                            else
                            {   /* A zero position, which is what the run   */
                                /* counts.                                  */
                                if (iRun == 0)
                                {   /* The run is spent, so this slot is where   */
                                    /* the new coefficient goes -- and writing   */
                                    /* a zero here, the ZRL case, is a correct   */
                                    /* no-op.                                    */
                                    arrBlockIo[iZig] = (short)iNewValue;
                                    break;
                                }

                                iRun = iRun - 1;
                            }
                        }
                    }
                    while (k <= schIn.JPEGS_iSpectralEnd);
                }
            }

            /* This scan's contribution to the block's band is in place. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* JPEG_ApplyRefinementBit:                                           */
        /*                                                                    */
        /* Applies one successive-approximation correction bit to a           */
        /* coefficient that is already non-zero.                              */
        /*                                                                    */
        /* FACTORED OUT BECAUSE THE REFINEMENT PASS DOES THIS IN TWO PLACES   */
        /* -- once inside an end-of-band run and once during the ordinary     */
        /* advance -- and the two must agree exactly.  The rule is easy to    */
        /* state and easy to get subtly wrong: the bit moves the magnitude    */
        /* AWAY FROM ZERO by one unit, so a positive coefficient grows and a  */
        /* negative one shrinks, and nothing happens at all when that unit is */
        /* already present.  Two copies would be two chances to write the     */
        /* positive case correctly and the negative case as an OR.            */
        /*                                                                    */
        /* Not a Try function: it cannot fail.  The caller has already read   */
        /* the bit and established that the coefficient is non-zero, so there */
        /* is no input left that could be rejected.                           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     arrBlockIo : The block being refined, in natural order.        */
        /*     iZig       : Natural-order index of the coefficient to touch.  */
        /*     bBitSet    : The correction bit just read from the stream.     */
        /*     iBitValue  : The magnitude one unit represents, 1 << Al.       */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void       : The coefficient is updated in place.              */
        /*--------------------------------------------------------------------*/

        private static void JPEG_ApplyRefinementBit(short[] arrBlockIo, int iZig,
                                                    bool bBitSet, int iBitValue)
        {
            if (!bBitSet)
            {   /* A clear bit says this coefficient is already as precise as   */
                /* this scan can make it.                                       */
                return;
            }

            if ((arrBlockIo[iZig] & iBitValue) != 0)
            {   /* The unit is already present, so adding it again would   */
                /* double a step the encoder only meant once.              */
                return;
            }

            if (arrBlockIo[iZig] > 0)
            {   /* Positive: away from zero is upward. */
                arrBlockIo[iZig] = (short)(arrBlockIo[iZig] + iBitValue);
            }
            else
            {   /* Negative: away from zero is downward. */
                arrBlockIo[iZig] = (short)(arrBlockIo[iZig] - iBitValue);
            }
        }

        /*--------------------------------------------------------------------*/
        /* JPEG_TryParseSos:                                                  */
        /*                                                                    */
        /* Parses an SOS segment into a JpegScanHeader.  The entropy-coded    */
        /* bytes begin immediately after it, with no length and no marker to  */
        /* announce them, which is why this is the one segment whose parse    */
        /* has to leave the reader exactly where it stopped.                  */
        /*                                                                    */
        /* Transcoded from stbi__process_scan_header.                         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     brIo     : Positioned on the segment length.  Left immediately */
        /*                after the segment, on the first entropy byte.       */
        /*     hdrIn    : The frame header, needed to resolve component ids   */
        /*                and to know whether progressive rules apply.        */
        /*     schOut   : Receives the parsed scan header, or null.           */
        /*     sReason  : Set to the cause when the return is false.          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool     : true when the scan header is valid and consistent   */
        /*                with the frame.                                     */
        /*--------------------------------------------------------------------*/

        private static bool JPEG_TryParseSos(ByteReader brIo, JpegHeader hdrIn,
                                             out JpegScanHeader schOut, out string sReason)
        {
            JpegScanHeader schNew;
            int            iLength;
            int            iCount;
            int            iId;
            int            iTables;
            int            iApprox;
            int            iWhich;
            int            i;

            schOut  = null;
            sReason = "";

            if (!JPEG_TryReadSegmentLength(brIo, JPEG_MARKER_SOS, out iLength, out sReason))
            {   /* No length where the segment must start. */
                return(false);
            }

            if (!brIo.BYTER_TryReadU8(out iCount))
            {   /* The payload ended before the component count. */
                sReason = "JPEG: SOS ended where its component count belongs";
                return(false);
            }

            if (iCount < 1 || iCount > JPEG_MAX_COMPONENTS ||
                iCount > hdrIn.JPEGH_iComponentCount)
            {   /* A scan cannot carry more components than the frame has,   */
                /* and cannot carry none.                                    */
                sReason = "JPEG: SOS names " + iCount.ToString() +
                          " components but the frame has " +
                          hdrIn.JPEGH_iComponentCount.ToString();
                return(false);
            }

            /* The length is fixed by the component count: one byte of count,   */
            /* two per component, then the three spectral bytes, plus the two   */
            /* the length includes for itself. Checking it here means a         */
            /* damaged count is caught before it drives the loop below.         */
            if (iLength != (JPEG_SOS_FIXED_BYTES + (2 * iCount)))
            {   /* The declared length contradicts the component count. */
                sReason = "JPEG: SOS length " +
                          (iLength + JPEG_SEGMENT_LENGTH_BYTES).ToString() +
                          " does not match its " + iCount.ToString() +
                          " components";
                return(false);
            }

            schNew                          = new JpegScanHeader();
            schNew.JPEGS_iComponentCount    = iCount;
            schNew.JPEGS_arrComponentIndex  = new int[iCount];
            schNew.JPEGS_arrDcTable         = new int[iCount];
            schNew.JPEGS_arrAcTable         = new int[iCount];

            for (i = 0; i < iCount; i++)
            {   /* One scanned component per iteration. */
                if (!brIo.BYTER_TryReadU8(out iId))
                {   /* Ran out inside the component list. */
                    sReason = "JPEG: SOS ended at component " + i.ToString();
                    return(false);
                }

                if (!brIo.BYTER_TryReadU8(out iTables))
                {   /* The id arrived but not its table selectors. */
                    sReason = "JPEG: SOS component " + i.ToString() +
                              " has no table selector byte";
                    return(false);
                }

                /* Resolve the id to a frame component. A linear search over    */
                /* at most four entries, which is what the format's own         */
                /* structure invites -- ids are arbitrary bytes, not indices.   */
                for (iWhich = 0; iWhich < hdrIn.JPEGH_iComponentCount; iWhich++)
                {   /* Compare against each frame component's declared id. */
                    if (hdrIn.JPEGH_arrComponents[iWhich].JPEGC_iId == iId)
                    {   /* Found the component this scan entry refers to. */
                        break;
                    }
                }

                if (iWhich == hdrIn.JPEGH_iComponentCount)
                {   /* An id the frame never declared. Refused rather than   */
                    /* guessed at, because guessing produces a decode that   */
                    /* looks right and assigns the wrong plane.              */
                    sReason = "JPEG: SOS names component id " + iId.ToString() +
                              ", which the frame header does not declare";
                    return(false);
                }

                schNew.JPEGS_arrDcTable[i] = iTables >> 4;
                schNew.JPEGS_arrAcTable[i] = iTables & 0x0F;

                if (schNew.JPEGS_arrDcTable[i] >= JPEG_MAX_HUFF_TABLES)
                {   /* Td is four bits wide but only four tables exist. */
                    sReason = "JPEG: SOS component " + i.ToString() +
                              " selects DC Huffman table " +
                              schNew.JPEGS_arrDcTable[i].ToString() +
                              ", which is out of range";
                    return(false);
                }

                if (schNew.JPEGS_arrAcTable[i] >= JPEG_MAX_HUFF_TABLES)
                {   /* Ta, same reasoning. */
                    sReason = "JPEG: SOS component " + i.ToString() +
                              " selects AC Huffman table " +
                              schNew.JPEGS_arrAcTable[i].ToString() +
                              ", which is out of range";
                    return(false);
                }

                schNew.JPEGS_arrComponentIndex[i] = iWhich;
            }

            if (!brIo.BYTER_TryReadU8(out schNew.JPEGS_iSpectralStart) ||
                !brIo.BYTER_TryReadU8(out schNew.JPEGS_iSpectralEnd) ||
                !brIo.BYTER_TryReadU8(out iApprox))
            {   /* The three bytes that close every SOS. */
                sReason = "JPEG: SOS ended before its spectral selection bytes";
                return(false);
            }

            schNew.JPEGS_iApproxHigh = iApprox >> 4;
            schNew.JPEGS_iApproxLow  = iApprox & 0x0F;

            if (hdrIn.JPEGH_bProgressive)
            {   /* A progressive scan codes a band of coefficients at a band    */
                /* of bit positions, so all four bytes carry real meaning and   */
                /* only their ranges can be checked here.                       */
                if (schNew.JPEGS_iSpectralStart > JPEG_MAX_SPECTRAL ||
                    schNew.JPEGS_iSpectralEnd > JPEG_MAX_SPECTRAL ||
                    schNew.JPEGS_iSpectralStart > schNew.JPEGS_iSpectralEnd ||
                    schNew.JPEGS_iApproxHigh > JPEG_MAX_APPROX ||
                    schNew.JPEGS_iApproxLow > JPEG_MAX_APPROX)
                {   /* A band that does not exist or runs backwards. */
                    sReason = "JPEG: progressive SOS has an impossible spectral " +
                              "selection: Ss=" + schNew.JPEGS_iSpectralStart.ToString() +
                              " Se=" + schNew.JPEGS_iSpectralEnd.ToString() +
                              " Ah=" + schNew.JPEGS_iApproxHigh.ToString() +
                              " Al=" + schNew.JPEGS_iApproxLow.ToString();
                    return(false);
                }
            }
            else
            {   /* A baseline scan always codes the whole block at full         */
                /* precision, so these bytes have exactly one legal value       */
                /* each. Enforcing that is worth more than it looks: it is      */
                /* how a progressive file carrying a baseline frame marker is   */
                /* caught here rather than decoding to noise later.             */
                if (schNew.JPEGS_iSpectralStart != 0)
                {   /* A baseline scan that claims not to start at the DC. */
                    sReason = "JPEG: baseline SOS must start at coefficient 0, not " +
                              schNew.JPEGS_iSpectralStart.ToString();
                    return(false);
                }

                if (schNew.JPEGS_iApproxHigh != 0 || schNew.JPEGS_iApproxLow != 0)
                {   /* Successive approximation in a baseline scan. */
                    sReason = "JPEG: baseline SOS must not use successive " +
                              "approximation, but declares Ah=" +
                              schNew.JPEGS_iApproxHigh.ToString() + " Al=" +
                              schNew.JPEGS_iApproxLow.ToString();
                    return(false);
                }

                /* Se is written as 63 by every encoder but is tolerated at     */
                /* any value and forced, exactly as the source does: the        */
                /* baseline scan covers the whole block by definition, so the   */
                /* byte carries no information to preserve.                     */
                schNew.JPEGS_iSpectralEnd = JPEG_MAX_SPECTRAL;
            }

            schOut = schNew;

            /* A scan header consistent with its frame, and the reader is now   */
            /* sitting on the first entropy-coded byte.                         */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* JPEG_TryWalkToScan:                                                */
        /*                                                                    */
        /* Continues a walk that has already reached the frame header, past   */
        /* any further table segments, and stops having parsed the first SOS. */
        /*                                                                    */
        /* WHY THIS IS A SEPARATE WALK RATHER THAN AN EXTENSION OF            */
        /* JPEG_TryWalkToFrameHeader, which is the thing not to "tidy up"     */
        /* later: that function is also the body of JPEG_TryReadHeader, the   */
        /* cheap header-only entry point a consumer uses to size a thumbnail  */
        /* before fetching it.  It stops at SOF ON PURPOSE, because that is   */
        /* the last byte it needs and reading further would make a header     */
        /* probe pay for the whole file.  Decoding needs more, so decoding    */
        /* calls this afterwards.                                             */
        /*                                                                    */
        /* MOST ENCODERS PUT DHT AFTER SOF, so this is not an edge case -- it */
        /* is the normal path, and a decoder that skipped it would find no    */
        /* Huffman tables at all on the majority of real files.               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     brIo       : Positioned immediately after the frame header.    */
        /*                  Left on the first entropy-coded byte.             */
        /*     hdrIo      : The frame header. A DRI met here updates its      */
        /*                  restart interval, which is why it is in/out.      */
        /*     arrQuantIo : Sixteen-entry-per-table quantisation tables, in   */
        /*                  natural order. Filled by any DQT met here.        */
        /*     arrHuffIo  : The eight Huffman table slots. Filled by any DHT. */
        /*     schOut     : Receives the parsed scan header.                  */
        /*     sReason    : Set to the cause when the return is false.        */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool       : true when an SOS was reached and parsed.          */
        /*--------------------------------------------------------------------*/

        public static bool JPEG_TryWalkToScan(ByteReader brIo, JpegHeader hdrIo,
                                              int[][] arrQuantIo,
                                              JpegHuffmanTable[] arrHuffIo,
                                              out JpegScanHeader schOut,
                                              out string sReason)
        {
            int iMarker;

            schOut  = null;
            sReason = "";

            for (;;)
            {   /* One marker per iteration until SOS or an error ends it. */
                if (!JPEG_TryReadMarker(brIo, out iMarker, out sReason))
                {   /* The file ended with no scan in it, so it declares an   */
                    /* image and then never codes one.                        */
                    sReason = "JPEG: no scan (SOS) before the end of the data";
                    return(false);
                }

                if (iMarker == JPEG_MARKER_SOS)
                {   /* The thing we came for. */
                    return(JPEG_TryParseSos(brIo, hdrIo, out schOut, out sReason));
                }

                if (iMarker == JPEG_MARKER_EOI)
                {   /* A complete file that codes nothing. Distinguished from   */
                    /* running off the end because it is a DIFFERENT defect:    */
                    /* the file is intact and simply has no scan, which an      */
                    /* encoder bug produces and a truncation does not.          */
                    sReason = "JPEG: reached EOI without finding a scan";
                    return(false);
                }

                if (JPEG_IsStartOfFrame(iMarker))
                {   /* A second frame header. Multi-frame JPEG is not a thing   */
                    /* this library decodes, and silently taking the first      */
                    /* would be a guess about which one the data belongs to.    */
                    sReason = "JPEG: a second frame header (" +
                              JPEG_MarkerToText(iMarker) +
                              ") appeared before any scan";
                    return(false);
                }

                if (!JPEG_TryProcessSegment(brIo, iMarker, hdrIo, arrQuantIo, arrHuffIo,
                                            out sReason))
                {   /* A malformed table segment, or a coding this library   */
                    /* does not support. The message already names which.    */
                    return(false);
                }
            }
        }

        /*--------------------------------------------------------------------*/
        /* JPEG_ComponentBlockCounts:                                         */
        /*                                                                    */
        /* Works out how many 8x8 blocks one component occupies, across and   */
        /* down, in a NON-INTERLEAVED scan.                                   */
        /*                                                                    */
        /* THIS IS NOT THE MCU COUNT AND THE DIFFERENCE MATTERS.  An          */
        /* interleaved scan is walked in MCUs, and a component's blocks       */
        /* inside one are fixed by its sampling factors.  A non-interleaved   */
        /* scan has no MCU structure at all: it is simply that component's    */
        /* own blocks in raster order, and how many there are depends only on */
        /* the component's own effective size.  Using the frame's MCU counts  */
        /* here would decode the right number of blocks only when nothing is  */
        /* subsampled, and silently too many otherwise.                       */
        /*                                                                    */
        /* The effective size is the image size scaled by this component's    */
        /* sampling factors against the maxima, rounded UP -- a chroma plane  */
        /* at half resolution still needs a whole final block for an odd      */
        /* remainder.                                                         */
        /*                                                                    */
        /* Not a Try function: every input has already been validated by      */
        /* JPEG_TryParseSof, which rejects a zero factor and a zero           */
        /* dimension, so there is no failure left to report.                  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     hdrIn        : The frame header, for the image size and the    */
        /*                    sampling maxima.                                */
        /*     iComponent   : Index into hdrIn.JPEGH_arrComponents.           */
        /*     iBlocksXOut  : Receives the block count across.                */
        /*     iBlocksYOut  : Receives the block count down.                  */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void         : Both counts are at least 1.                     */
        /*--------------------------------------------------------------------*/

        private static void JPEG_ComponentBlockCounts(JpegHeader hdrIn, int iComponent,
                                                      out int iBlocksXOut,
                                                      out int iBlocksYOut)
        {
            JpegComponent cmpUse;
            int           iEffectiveWidth;
            int           iEffectiveHeight;

            cmpUse = hdrIn.JPEGH_arrComponents[iComponent];

            /* Scale by this component's factors against the frame's maxima,   */
            /* rounding up so a partial sample still gets covered.             */
            iEffectiveWidth = ((hdrIn.JPEGH_iWidth * cmpUse.JPEGC_iHorizontalFactor) +
                               hdrIn.JPEGH_iMaxHorizontalFactor - 1) /
                              hdrIn.JPEGH_iMaxHorizontalFactor;

            iEffectiveHeight = ((hdrIn.JPEGH_iHeight * cmpUse.JPEGC_iVerticalFactor) +
                                hdrIn.JPEGH_iMaxVerticalFactor - 1) /
                               hdrIn.JPEGH_iMaxVerticalFactor;

            /* Then round each up to whole blocks. */
            iBlocksXOut = (iEffectiveWidth + 7) / 8;
            iBlocksYOut = (iEffectiveHeight + 7) / 8;
        }

        /*--------------------------------------------------------------------*/
        /* JPEG_TryDecodeScanBlocks:                                          */
        /*                                                                    */
        /* Decodes a whole NON-INTERLEAVED baseline scan into coefficient     */
        /* blocks, handling restart markers.  One component, in raster order, */
        /* every block dequantised and in natural order.                      */
        /*                                                                    */
        /* SCOPE, STATED SO IT IS NOT ACCIDENTALLY WIDENED: this handles the  */
        /* single-component case only.  The interleaved MCU walk -- several   */
        /* components taking turns inside one MCU -- is a separate function,  */
        /* and the seam is deliberate: interleaving and colour are the two    */
        /* things most likely to be wrong together and hardest to tell apart. */
        /* A scan naming more than one component is REFUSED here rather than  */
        /* half-handled.                                                      */
        /*                                                                    */
        /* HOW RESTARTS WORK, because the countdown is easy to get subtly     */
        /* wrong.  In a non-interleaved scan every BLOCK is one MCU, so the   */
        /* interval counts blocks.  When it expires the encoder has written   */
        /* an RSTn marker, flushed to a byte boundary, and reset its own DC   */
        /* predictor; the decoder must discard its partial byte, clear the    */
        /* predictor and carry on.  JPEGBR_Reset does the first two.  If what */
        /* is waiting is NOT a restart marker the scan simply ENDS -- that is */
        /* the source's behaviour and it is deliberate, because a file that   */
        /* stops early should yield the blocks it did contain rather than     */
        /* nothing at all.                                                    */
        /*                                                                    */
        /* Transcoded from the scan_n == 1 branch of                          */
        /* stbi__parse_entropy_coded_data, minus the IDCT call, which this    */
        /* function deliberately leaves to its caller.                        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     brIo         : Positioned on the first entropy-coded byte.     */
        /*     hdrIn        : The frame header.                               */
        /*     schIn        : The scan header from JPEG_TryParseSos.          */
        /*     arrQuantIn   : The four quantisation tables, natural order.    */
        /*     arrHuffIn    : The eight Huffman table slots.                  */
        /*     arrBlocksOut : Receives one 64-entry block per block position, */
        /*                    in raster order, length iBlocksX * iBlocksY.    */
        /*     iBlocksXOut  : Receives the block count across, so the caller  */
        /*                    can index the flat array above.                 */
        /*     iBlocksYOut  : Receives the block count down.                  */
        /*     sReason      : Set to the cause when the return is false.      */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool         : true when every block position was decoded.     */
        /*--------------------------------------------------------------------*/

        public static bool JPEG_TryDecodeScanBlocks(ByteReader brIo, JpegHeader hdrIn,
                                                    JpegScanHeader schIn,
                                                    int[][] arrQuantIn,
                                                    JpegHuffmanTable[] arrHuffIn,
                                                    out short[][][] arrBlocksOut,
                                                    out int[] arrBlocksXOut,
                                                    out int[] arrBlocksYOut,
                                                    out string sReason)
        {
            JpegBitReader      brdEntropy;
            JpegComponent      cmpUse;
            JpegHuffmanTable[] arrScanDc;
            JpegHuffmanTable[] arrScanAc;
            short[][][]        arrBlocks;
            short[]            arrBlock;
            int[][]            arrScanQuant;
            int[]              arrScanIndex;
            int[]              arrBlocksX;
            int[]              arrBlocksY;
            int[]              arrDcPred;
            int                iComponent;
            int                iToGo;
            int                iInterval;
            int                iSlot;
            int                iBlockX;
            int                iBlockY;
            int                k;
            int                i;
            int                j;
            int                x;
            int                y;

            arrBlocksOut = null;
            arrBlocksXOut = null;
            arrBlocksYOut = null;
            sReason       = "";

            arrScanIndex = new int[schIn.JPEGS_iComponentCount];
            arrScanQuant = new int[schIn.JPEGS_iComponentCount][];
            arrScanDc    = new JpegHuffmanTable[schIn.JPEGS_iComponentCount];
            arrScanAc    = new JpegHuffmanTable[schIn.JPEGS_iComponentCount];

            arrBlocks  = new short[hdrIn.JPEGH_iComponentCount][][];
            arrBlocksX = new int[hdrIn.JPEGH_iComponentCount];
            arrBlocksY = new int[hdrIn.JPEGH_iComponentCount];

            /* RESOLVE EVERY SCANNED COMPONENT BEFORE DECODING ANYTHING.        */
            /* A table this scan needs but the file never defined is a fault    */
            /* in the FILE, and finding all of them up front means the reason   */
            /* names the missing table rather than reporting a corrupt block    */
            /* three hundred blocks later.                                      */
            for (k = 0; k < schIn.JPEGS_iComponentCount; k++)
            {   /* One scanned component per iteration, in interleave order. */
                iComponent      = schIn.JPEGS_arrComponentIndex[k];
                arrScanIndex[k] = iComponent;
                cmpUse          = hdrIn.JPEGH_arrComponents[iComponent];

                arrScanQuant[k] = arrQuantIn[cmpUse.JPEGC_iQuantTable];

                if (arrScanQuant[k] == null)
                {   /* The frame points this component at a DQT slot the file   */
                    /* never filled. A decode would multiply by nothing.        */
                    sReason = "JPEG: component " + iComponent.ToString() +
                              " needs quantisation table " +
                              cmpUse.JPEGC_iQuantTable.ToString() +
                              ", which the file never defined";
                    return(false);
                }

                iSlot        = (JPEG_HUFFMAN_CLASS_DC * JPEG_MAX_HUFF_TABLES) +
                               schIn.JPEGS_arrDcTable[k];
                arrScanDc[k] = arrHuffIn[iSlot];

                if (arrScanDc[k] == null)
                {   /* A scan selecting a DC table no DHT defined. */
                    sReason = "JPEG: the scan needs DC Huffman table " +
                              schIn.JPEGS_arrDcTable[k].ToString() +
                              ", which the file never defined";
                    return(false);
                }

                iSlot        = (JPEG_HUFFMAN_CLASS_AC * JPEG_MAX_HUFF_TABLES) +
                               schIn.JPEGS_arrAcTable[k];
                arrScanAc[k] = arrHuffIn[iSlot];

                if (arrScanAc[k] == null)
                {   /* Same for AC. Checked separately so the reason names   */
                    /* which of the two is missing.                          */
                    sReason = "JPEG: the scan needs AC Huffman table " +
                              schIn.JPEGS_arrAcTable[k].ToString() +
                              ", which the file never defined";
                    return(false);
                }

                /* THE TWO INTERLEAVE MODES SIZE THEIR GRIDS DIFFERENTLY, and   */
                /* this is the single most important difference between them.   */
                /* A non-interleaved scan holds exactly the blocks the          */
                /* component's own size needs.  An interleaved one is walked    */
                /* in whole MCUs, so the last MCU of a row or column is         */
                /* decoded in full even where it hangs off the edge -- the      */
                /* grid is MCU-ALIGNED and larger, and the surplus is dropped   */
                /* only at colour conversion.                                   */
                if (schIn.JPEGS_iComponentCount == 1)
                {   /* Non-interleaved: the component's own blocks. */
                    JPEG_ComponentBlockCounts(hdrIn, iComponent, out iBlockX,
                                              out iBlockY);
                }
                else
                {   /* Interleaved: MCU-aligned, so a whole number of MCUs. */
                    iBlockX = hdrIn.JPEGH_iMcuCountX * cmpUse.JPEGC_iHorizontalFactor;
                    iBlockY = hdrIn.JPEGH_iMcuCountY * cmpUse.JPEGC_iVerticalFactor;
                }

                arrBlocksX[iComponent] = iBlockX;
                arrBlocksY[iComponent] = iBlockY;
                arrBlocks[iComponent]  = new short[iBlockX * iBlockY][];
            }

            arrDcPred  = new int[JPEG_MAX_COMPONENTS];
            brdEntropy = new JpegBitReader(brIo);

            /* A restart interval of zero means "no restarts", expressed as a   */
            /* countdown that cannot reach zero within any real image rather    */
            /* than as a flag tested in the inner loop. That is the source's    */
            /* trick and it keeps the hot path branch-free of the question.     */
            if (hdrIn.JPEGH_iRestartInterval > 0)
            {   /* DRI gave a real interval. */
                iInterval = hdrIn.JPEGH_iRestartInterval;
            }
            else
            {   /* No DRI, so nothing will ever restart. */
                iInterval = int.MaxValue;
            }

            iToGo = iInterval;

            if (schIn.JPEGS_iComponentCount == 1)
            {   /* NON-INTERLEAVED: one component, its own blocks in raster   */
                /* order, and every BLOCK counts as one MCU for restarts.     */
                iComponent = arrScanIndex[0];
                iBlockX    = arrBlocksX[iComponent];
                iBlockY    = arrBlocksY[iComponent];

                for (y = 0; y < iBlockY; y++)
                {   /* One row of blocks per iteration. */
                    for (x = 0; x < iBlockX; x++)
                    {   /* One block per iteration. */
                        arrBlock = new short[JPEG_BLOCK_SAMPLES];

                        if (!JPEG_TryDecodeBlock(brdEntropy, arrScanDc[0], arrScanAc[0],
                                                 arrScanQuant[0], arrDcPred, iComponent,
                                                 arrBlock, out sReason))
                        {   /* A corrupt block. Refused rather than skipped:    */
                            /* the DC predictor is a running total, so every    */
                            /* later block would be wrong too and the damage    */
                            /* would look like a decoder fault, not bad data.   */
                            sReason = "JPEG: block (" + x.ToString() + "," +
                                      y.ToString() + "): " + sReason;
                            return(false);
                        }

                        arrBlocks[iComponent][(y * iBlockX) + x] = arrBlock;

                        iToGo = iToGo - 1;

                        if (iToGo <= 0)
                        {   /* The interval expired, so an RSTn ought to be   */
                            /* sitting here.                                  */
                            if (!JPEG_TryHandleRestart(brdEntropy, arrDcPred, null))
                            {   /* Not a restart: the scan is over early. See   */
                                /* this function's header for why that yields   */
                                /* what was decoded instead of failing.         */
                                arrBlocksOut  = arrBlocks;
                                arrBlocksXOut = arrBlocksX;
                                arrBlocksYOut = arrBlocksY;

                                /* Short, but what there is of it is sound. */
                                return(true);
                            }

                            iToGo = iInterval;
                        }
                    }
                }
            }
            else
            {   /* INTERLEAVED: the components take turns INSIDE each MCU,    */
                /* each contributing h x v blocks, and the whole MCU counts   */
                /* as one unit for restarts.  The order is the SCAN's, not    */
                /* the frame's, which is why arrScanIndex exists.             */
                for (j = 0; j < hdrIn.JPEGH_iMcuCountY; j++)
                {   /* One MCU row per iteration. */
                    for (i = 0; i < hdrIn.JPEGH_iMcuCountX; i++)
                    {   /* One MCU per iteration. */
                        for (k = 0; k < schIn.JPEGS_iComponentCount; k++)
                        {   /* One component's turn within this MCU. */
                            iComponent = arrScanIndex[k];
                            cmpUse     = hdrIn.JPEGH_arrComponents[iComponent];

                            for (y = 0; y < cmpUse.JPEGC_iVerticalFactor; y++)
                            {   /* This component's block rows in the MCU. */
                                for (x = 0; x < cmpUse.JPEGC_iHorizontalFactor; x++)
                                {   /* One block per iteration. */
                                    iBlockX = (i * cmpUse.JPEGC_iHorizontalFactor) + x;
                                    iBlockY = (j * cmpUse.JPEGC_iVerticalFactor) + y;

                                    arrBlock = new short[JPEG_BLOCK_SAMPLES];

                                    if (!JPEG_TryDecodeBlock(brdEntropy, arrScanDc[k],
                                                             arrScanAc[k], arrScanQuant[k],
                                                             arrDcPred, iComponent,
                                                             arrBlock, out sReason))
                                    {   /* A corrupt block anywhere derails    */
                                        /* every component after it, because   */
                                        /* they share one bit stream.          */
                                        sReason = "JPEG: MCU (" + i.ToString() + "," +
                                                  j.ToString() + ") component " +
                                                  iComponent.ToString() + ": " + sReason;
                                        return(false);
                                    }

                                    arrBlocks[iComponent][
                                        (iBlockY * arrBlocksX[iComponent]) + iBlockX] =
                                            arrBlock;
                                }
                            }
                        }

                        /* THE COUNTDOWN IS PER MCU, NOT PER BLOCK, here.       */
                        /* A restart interval counts MCUs, and an interleaved   */
                        /* MCU holds several blocks -- decrementing per block   */
                        /* would look for a marker in the middle of one.        */
                        iToGo = iToGo - 1;

                        if (iToGo <= 0)
                        {   /* The interval expired between MCUs. */
                            if (!JPEG_TryHandleRestart(brdEntropy, arrDcPred, null))
                            {   /* Not a restart: the scan is over early. */
                                arrBlocksOut  = arrBlocks;
                                arrBlocksXOut = arrBlocksX;
                                arrBlocksYOut = arrBlocksY;

                                /* Short, but sound as far as it goes. */
                                return(true);
                            }

                            iToGo = iInterval;
                        }
                    }
                }
            }

            arrBlocksOut  = arrBlocks;
            arrBlocksXOut = arrBlocksX;
            arrBlocksYOut = arrBlocksY;

            /* Every block position in every scanned component was decoded. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* JPEG_TryHandleRestart:                                             */
        /*                                                                    */
        /* Consumes a restart marker if one is waiting, and resets the state  */
        /* a restart is defined to reset.                                     */
        /*                                                                    */
        /* FACTORED OUT BECAUSE BOTH INTERLEAVE MODES DO IT IDENTICALLY, and  */
        /* because the half that gets forgotten is the DC predictors rather   */
        /* than the marker.  Forgetting the predictors still decodes the      */
        /* first interval perfectly and produces a brightness that drifts     */
        /* from there on -- a fault that looks like a bad file.  One copy is  */
        /* one place to get it right.                                         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     brdIo       : The entropy reader, topped up so a pending       */
        /*                   marker has been latched.                         */
        /*     arrDcPredIo : Per-component DC predictors, zeroed on a real    */
        /*                   restart and left alone otherwise.                */
        /*     pgIo        : Progressive state whose end-of-band run is       */
        /*                   cleared, or null on the baseline path, which has */
        /*                   no such run to clear.                            */
        /*                                                                    */
        /* THE END-OF-BAND RUN IS RESET TOO, and that half is progressive-    */
        /* only and easy to miss.  A restart resynchronises the entropy       */
        /* stream completely, so a run still counting down across blocks does */
        /* not continue past the marker.  Letting it survive silently         */
        /* swallows the first blocks of the next interval -- they are treated */
        /* as empty rather than decoded -- which reads as patches of missing  */
        /* detail rather than as a fault.                                     */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool        : true when a restart was consumed and the caller  */
        /*                   should continue; false when what is waiting is   */
        /*                   NOT a restart, which ends the scan.              */
        /*--------------------------------------------------------------------*/

        private static bool JPEG_TryHandleRestart(JpegBitReader brdIo, int[] arrDcPredIo,
                                                  JpegProgressiveState pgIo)
        {
            int i;

            /* The marker is discovered by the refill, not by looking ahead,   */
            /* so the reader has to be walked far enough to meet it first.     */
            brdIo.JPEGBR_TopUp();

            if (!JPEG_IsRestart(brdIo.JPEGBR_iMarker))
            {   /* Whatever is here, it is not a restart, so the scan ends. */
                return(false);
            }

            brdIo.JPEGBR_Reset();

            for (i = 0; i < JPEG_MAX_COMPONENTS; i++)
            {   /* Every component's running DC returns to zero. */
                arrDcPredIo[i] = 0;
            }

            if (pgIo != null)
            {   /* A progressive scan: the band run stops at the marker. */
                pgIo.JPEGPG_iEobRun = 0;
            }

            /* Resynchronised, and the caller may carry on. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* JPEG_AllocateProgressive:                                          */
        /*                                                                    */
        /* Builds the frame-wide coefficient buffers a progressive file       */
        /* refines across its scans.                                          */
        /*                                                                    */
        /* EVERY BLOCK IS ALLOCATED UP FRONT, which is the opposite of the    */
        /* baseline path's allocate-as-you-decode.  A progressive scan does   */
        /* not necessarily visit every block -- an end-of-band run skips      */
        /* whole stretches, and a band scan touches only part of each block   */
        /* -- so a block that no scan ever wrote is still a real block whose  */
        /* coefficients are all legitimately zero.  Allocating lazily would   */
        /* leave those as nulls indistinguishable from "never coded".         */
        /*                                                                    */
        /* THE GRID IS MCU-ALIGNED FOR EVERY COMPONENT.  See the note on      */
        /* JpegProgressiveState: one buffer serves both interleaved DC scans  */
        /* and non-interleaved AC scans, so it takes the larger shape and the */
        /* non-interleaved walk uses the smaller extent with the larger       */
        /* stride.                                                            */
        /*                                                                    */
        /* Not a Try function: the only thing it can do wrong is run out of   */
        /* memory, which is not malformed input and which the framework       */
        /* reports for us.  Every dimension it uses was validated by          */
        /* JPEG_TryParseSof.                                                  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     hdrIn : The frame header, for the component list and the MCU   */
        /*             counts.                                                */
        /*                                                                    */
        /* Returns:                                                           */
        /*     JpegProgressiveState : Fully allocated, every coefficient      */
        /*                            zero, the band run idle.                */
        /*--------------------------------------------------------------------*/

        private static JpegProgressiveState JPEG_AllocateProgressive(JpegHeader hdrIn)
        {
            JpegProgressiveState pgNew;
            JpegComponent        cmpUse;
            int                  iBlockX;
            int                  iBlockY;
            int                  iBlocks;
            int                  i;
            int                  b;

            pgNew = new JpegProgressiveState();

            pgNew.JPEGPG_arrCoefficients = new short[hdrIn.JPEGH_iComponentCount][][];
            pgNew.JPEGPG_arrBlocksX      = new int[hdrIn.JPEGH_iComponentCount];
            pgNew.JPEGPG_arrBlocksY      = new int[hdrIn.JPEGH_iComponentCount];
            pgNew.JPEGPG_iEobRun         = 0;

            for (i = 0; i < hdrIn.JPEGH_iComponentCount; i++)
            {   /* One component per iteration. */
                cmpUse = hdrIn.JPEGH_arrComponents[i];

                iBlockX = hdrIn.JPEGH_iMcuCountX * cmpUse.JPEGC_iHorizontalFactor;
                iBlockY = hdrIn.JPEGH_iMcuCountY * cmpUse.JPEGC_iVerticalFactor;
                iBlocks = iBlockX * iBlockY;

                pgNew.JPEGPG_arrBlocksX[i]      = iBlockX;
                pgNew.JPEGPG_arrBlocksY[i]      = iBlockY;
                pgNew.JPEGPG_arrCoefficients[i] = new short[iBlocks][];

                for (b = 0; b < iBlocks; b++)
                {   /* One block's coefficients, zero until a scan says         */
                    /* otherwise. A C# array arrives zero-filled, so there is   */
                    /* nothing to clear.                                        */
                    pgNew.JPEGPG_arrCoefficients[i][b] =
                        new short[JPEG_BLOCK_SAMPLES];
                }
            }

            /* Ready for the first scan to start refining. */
            return(pgNew);
        }

        /*--------------------------------------------------------------------*/
        /* JPEG_TryDecodeScanProgressive:                                     */
        /*                                                                    */
        /* Walks one progressive scan's entropy data, refining the frame's    */
        /* coefficient buffers in place.                                      */
        /*                                                                    */
        /* Transcoded from the progressive half of                            */
        /* stbi__parse_entropy_coded_data.                                    */
        /*                                                                    */
        /* THE INTERLEAVE RULE IS NOT THE BASELINE ONE, and this is the trap  */
        /* the function exists to get right.  A progressive DC scan may       */
        /* interleave several components and is walked in MCUs; a progressive */
        /* AC scan is ALWAYS single-component and is walked in that           */
        /* component's own blocks, because a band of one component cannot be  */
        /* interleaved with a band of another.  So the mode is chosen by the  */
        /* scan's component COUNT exactly as in baseline, but the two arms    */
        /* now call different block decoders and the non-interleaved arm      */
        /* indexes at a stride it does not walk.                              */
        /*                                                                    */
        /* A SHORT SCAN IS NOT AN ERROR, matching the baseline walk: when the */
        /* restart check finds something that is not a restart, what has been */
        /* decoded so far stands and the caller moves on to the next marker.  */
        /* A progressive file that loses its last scans is still a decodable  */
        /* picture, merely a less refined one -- which is the entire premise  */
        /* of progressive coding.                                             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     brIo      : The byte reader, positioned at the entropy data.   */
        /*     hdrIn     : The frame header.                                  */
        /*     schIn     : This scan's header.                                */
        /*     arrHuffIn : All defined Huffman tables, DC then AC.            */
        /*     pgIo      : The frame's coefficients, refined in place.        */
        /*     iMarkerOut: Receives the marker that ended the entropy data if */
        /*                 the reader happened to run into one, or            */
        /*                 JPEG_MARKER_NONE if it stopped before reaching it. */
        /*                 BOTH ARE NORMAL -- see below.                      */
        /*     sReason   : Receives the cause when the return is false.       */
        /*                                                                    */
        /* WHY THE MARKER COMES BACK OUT.  The bit reader looks ahead, so by  */
        /* the time the last block is decoded it has usually already met the  */
        /* next marker, CONSUMED its two bytes and latched it.  A caller that */
        /* then went looking for a marker at the byte cursor would be two     */
        /* bytes past it.  But the look-ahead is only a few bytes deep, so    */
        /* the reader can equally stop short of the marker -- which is why    */
        /* the caller must handle both and cannot simply assume either.       */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool      : true when the scan was consumed; false when the    */
        /*                 file is malformed.                                 */
        /*--------------------------------------------------------------------*/

        public static bool JPEG_TryDecodeScanProgressive(ByteReader brIo,
                                                         JpegHeader hdrIn,
                                                         JpegScanHeader schIn,
                                                         JpegHuffmanTable[] arrHuffIn,
                                                         JpegProgressiveState pgIo,
                                                         out int iMarkerOut,
                                                         out string sReason)
        {
            JpegBitReader      brdEntropy;
            JpegComponent      cmpUse;
            JpegHuffmanTable   tblUse;
            JpegHuffmanTable[] arrScanDc;
            JpegHuffmanTable[] arrScanAc;
            short[][]          arrComponentBlocks;
            int[]              arrScanIndex;
            int[]              arrDcPred;
            int                iComponent;
            int                iStride;
            int                iWalkX;
            int                iWalkY;
            int                iToGo;
            int                iInterval;
            int                iSlot;
            int                iBlockX;
            int                iBlockY;
            int                k;
            int                i;
            int                j;
            int                x;
            int                y;

            iMarkerOut = JPEG_MARKER_NONE;
            sReason    = "";

            arrScanIndex = new int[schIn.JPEGS_iComponentCount];
            arrScanDc    = new JpegHuffmanTable[schIn.JPEGS_iComponentCount];
            arrScanAc    = new JpegHuffmanTable[schIn.JPEGS_iComponentCount];

            /* RESOLVE EVERY TABLE THIS SCAN NEEDS BEFORE DECODING ANYTHING,   */
            /* the same discipline the baseline walk uses and for the same     */
            /* reason: a missing table named up front beats a corrupt block    */
            /* reported three hundred blocks later.                            */
            /*                                                                 */
            /* ONLY THE TABLES THE SCAN ACTUALLY USES ARE REQUIRED, which is   */
            /* the one difference from baseline.  A DC scan selects no AC      */
            /* table and an AC scan selects no DC table, and progressive       */
            /* files routinely leave the unused index pointing at a slot no    */
            /* DHT ever filled.  Demanding both would refuse perfectly good    */
            /* files.                                                          */
            for (k = 0; k < schIn.JPEGS_iComponentCount; k++)
            {   /* One scanned component per iteration, in interleave order. */
                iComponent      = schIn.JPEGS_arrComponentIndex[k];
                arrScanIndex[k] = iComponent;

                if (schIn.JPEGS_iSpectralStart == 0)
                {   /* A DC scan, so a DC table must exist. */
                    iSlot  = (JPEG_HUFFMAN_CLASS_DC * JPEG_MAX_HUFF_TABLES) +
                             schIn.JPEGS_arrDcTable[k];
                    tblUse = arrHuffIn[iSlot];

                    if (tblUse == null)
                    {   /* A progressive DC scan naming a table no DHT built. */
                        sReason = "JPEG: the progressive DC scan needs Huffman " +
                                  "table " + schIn.JPEGS_arrDcTable[k].ToString() +
                                  ", which the file never defined";
                        return(false);
                    }

                    arrScanDc[k] = tblUse;
                }
                else
                {   /* An AC scan, so an AC table must exist instead. */
                    iSlot  = (JPEG_HUFFMAN_CLASS_AC * JPEG_MAX_HUFF_TABLES) +
                             schIn.JPEGS_arrAcTable[k];
                    tblUse = arrHuffIn[iSlot];

                    if (tblUse == null)
                    {   /* A progressive AC scan naming a table no DHT built. */
                        sReason = "JPEG: the progressive AC scan needs Huffman " +
                                  "table " + schIn.JPEGS_arrAcTable[k].ToString() +
                                  ", which the file never defined";
                        return(false);
                    }

                    arrScanAc[k] = tblUse;
                }
            }

            arrDcPred  = new int[JPEG_MAX_COMPONENTS];
            brdEntropy = new JpegBitReader(brIo);

            /* THE BAND RUN STARTS IDLE AT EVERY SCAN, not merely at every    */
            /* restart. stbi__jpeg_reset is called at the head of each scan   */
            /* and clears it there too; a run left over from the previous     */
            /* scan would swallow the first blocks of this one.               */
            pgIo.JPEGPG_iEobRun = 0;

            if (hdrIn.JPEGH_iRestartInterval > 0)
            {   /* DRI gave a real interval. */
                iInterval = hdrIn.JPEGH_iRestartInterval;
            }
            else
            {   /* No DRI, so nothing will ever restart. */
                iInterval = int.MaxValue;
            }

            iToGo = iInterval;

            if (schIn.JPEGS_iComponentCount == 1)
            {   /* NON-INTERLEAVED: this component's own blocks in raster   */
                /* order, every block counting as one MCU for restarts.     */
                iComponent         = arrScanIndex[0];
                arrComponentBlocks = pgIo.JPEGPG_arrCoefficients[iComponent];
                iStride            = pgIo.JPEGPG_arrBlocksX[iComponent];

                /* THE WALK AND THE STRIDE ARE DIFFERENT NUMBERS HERE. The    */
                /* extent comes from the component's own size; the index      */
                /* multiplier is the MCU-aligned width the buffer was built   */
                /* with. Using one for both shears the image.                 */
                JPEG_ComponentBlockCounts(hdrIn, iComponent, out iWalkX, out iWalkY);

                for (y = 0; y < iWalkY; y++)
                {   /* One row of blocks per iteration. */
                    for (x = 0; x < iWalkX; x++)
                    {   /* One block per iteration. */
                        if (schIn.JPEGS_iSpectralStart == 0)
                        {   /* A DC scan that happens to name one component. */
                            if (!JPEG_TryDecodeBlockProgDc(brdEntropy, arrScanDc[0],
                                                           arrDcPred, iComponent,
                                                           schIn,
                                                           arrComponentBlocks[
                                                               (y * iStride) + x],
                                                           out sReason))
                            {   /* A corrupt block derails everything after. */
                                sReason = "JPEG: progressive DC block (" +
                                          x.ToString() + "," + y.ToString() +
                                          "): " + sReason;
                                return(false);
                            }
                        }
                        else
                        {   /* The ordinary case: a band of one component. */
                            if (!JPEG_TryDecodeBlockProgAc(brdEntropy, arrScanAc[0],
                                                           schIn, pgIo,
                                                           arrComponentBlocks[
                                                               (y * iStride) + x],
                                                           out sReason))
                            {   /* A corrupt block derails everything after. */
                                sReason = "JPEG: progressive AC block (" +
                                          x.ToString() + "," + y.ToString() +
                                          "): " + sReason;
                                return(false);
                            }
                        }

                        iToGo = iToGo - 1;

                        if (iToGo <= 0)
                        {   /* The interval expired, so an RSTn ought to be   */
                            /* sitting here.                                  */
                            if (!JPEG_TryHandleRestart(brdEntropy, arrDcPred, pgIo))
                            {   /* Not a restart: this scan ends early, and   */
                                /* what it refined stands.                    */
                                iMarkerOut = brdEntropy.JPEGBR_iMarker;

                                /* Short, but what there is of it is sound. */
                                return(true);
                            }

                            iToGo = iInterval;
                        }
                    }
                }
            }
            else
            {   /* INTERLEAVED: only a DC scan can be, and it is walked in    */
                /* whole MCUs with the components taking turns inside each.   */
                if (schIn.JPEGS_iSpectralStart != 0)
                {   /* An AC scan naming several components. The format does   */
                    /* not allow it -- bands of different components cannot    */
                    /* share a scan -- so this is a malformed file rather      */
                    /* than something unsupported.                             */
                    sReason = "JPEG: a progressive AC scan must name exactly " +
                              "one component, but this one names " +
                              schIn.JPEGS_iComponentCount.ToString();
                    return(false);
                }

                for (j = 0; j < hdrIn.JPEGH_iMcuCountY; j++)
                {   /* One MCU row per iteration. */
                    for (i = 0; i < hdrIn.JPEGH_iMcuCountX; i++)
                    {   /* One MCU per iteration. */
                        for (k = 0; k < schIn.JPEGS_iComponentCount; k++)
                        {   /* One component's turn within this MCU. */
                            iComponent         = arrScanIndex[k];
                            cmpUse             = hdrIn.JPEGH_arrComponents[iComponent];
                            arrComponentBlocks = pgIo.JPEGPG_arrCoefficients[iComponent];
                            iStride            = pgIo.JPEGPG_arrBlocksX[iComponent];

                            for (y = 0; y < cmpUse.JPEGC_iVerticalFactor; y++)
                            {   /* This component's block rows in the MCU. */
                                for (x = 0; x < cmpUse.JPEGC_iHorizontalFactor; x++)
                                {   /* One block per iteration. */
                                    iBlockX = (i * cmpUse.JPEGC_iHorizontalFactor) + x;
                                    iBlockY = (j * cmpUse.JPEGC_iVerticalFactor) + y;

                                    if (!JPEG_TryDecodeBlockProgDc(brdEntropy,
                                                                   arrScanDc[k],
                                                                   arrDcPred,
                                                                   iComponent, schIn,
                                                                   arrComponentBlocks[
                                                                       (iBlockY * iStride) +
                                                                       iBlockX],
                                                                   out sReason))
                                    {   /* One corrupt block derails every     */
                                        /* component after it in the stream.   */
                                        sReason = "JPEG: progressive MCU (" +
                                                  i.ToString() + "," + j.ToString() +
                                                  ") component " +
                                                  iComponent.ToString() + ": " +
                                                  sReason;
                                        return(false);
                                    }
                                }
                            }
                        }

                        /* The countdown is per MCU, not per block, exactly   */
                        /* as in the baseline interleaved walk.               */
                        iToGo = iToGo - 1;

                        if (iToGo <= 0)
                        {   /* The interval expired between MCUs. */
                            if (!JPEG_TryHandleRestart(brdEntropy, arrDcPred, pgIo))
                            {   /* Not a restart: this scan ends early. */
                                iMarkerOut = brdEntropy.JPEGBR_iMarker;

                                /* Short, but sound as far as it goes. */
                                return(true);
                            }

                            iToGo = iInterval;
                        }
                    }
                }
            }

            /* The reader has usually met the next marker on its look-ahead   */
            /* by now; when it has not, this stays JPEG_MARKER_NONE and the   */
            /* caller scans forward for it instead.                           */
            iMarkerOut = brdEntropy.JPEGBR_iMarker;

            /* Every block this scan was responsible for has been refined. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* JPEG_DequantizeProgressive:                                        */
        /*                                                                    */
        /* Multiplies every coefficient of every block by its quantisation    */
        /* table, once the last scan has been read.                           */
        /*                                                                    */
        /* THIS IS THE STEP THE BASELINE PATH DOES INSIDE THE BLOCK DECODER,  */
        /* moved to the end because a progressive block is revisited.  See    */
        /* the note on JpegProgressiveState: scaling on the first visit would */
        /* leave every later refinement adding un-scaled units to scaled      */
        /* ones, which produces an image that is right in its low frequencies */
        /* and increasingly wrong in its detail -- plausible enough to pass a */
        /* casual look.                                                       */
        /*                                                                    */
        /* Both sides are already in NATURAL order -- the coefficients were   */
        /* scattered through the dezigzag table as they were decoded, and     */
        /* JPEG_TryParseDqt de-zigzags the tables as it reads them -- so this */
        /* is a straight element-wise multiply with no reordering.            */
        /*                                                                    */
        /* Not a Try function: the tables were proven present before the      */
        /* first scan ran, and a product that will not fit a short is         */
        /* saturated rather than refused.  See the note on the clamp below.   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     pgIo      : The frame's coefficients, scaled in place.         */
        /*     hdrIn     : The frame header, for the per-component table      */
        /*                 selection.                                         */
        /*     arrQuantIn: The four quantisation tables, natural order.       */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void      : Every coefficient is dequantised.                  */
        /*--------------------------------------------------------------------*/

        private static void JPEG_DequantizeProgressive(JpegProgressiveState pgIo,
                                                       JpegHeader hdrIn,
                                                       int[][] arrQuantIn)
        {
            short[][] arrComponentBlocks;
            short[]   arrBlock;
            int[]     arrTable;
            int       iProduct;
            int       iComponent;
            int       b;
            int       i;

            for (iComponent = 0; iComponent < hdrIn.JPEGH_iComponentCount; iComponent++)
            {   /* One component per iteration. */
                arrComponentBlocks = pgIo.JPEGPG_arrCoefficients[iComponent];
                arrTable           = arrQuantIn[
                    hdrIn.JPEGH_arrComponents[iComponent].JPEGC_iQuantTable];

                for (b = 0; b < arrComponentBlocks.Length; b++)
                {   /* One block per iteration. */
                    arrBlock = arrComponentBlocks[b];

                    for (i = 0; i < JPEG_BLOCK_SAMPLES; i++)
                    {   /* One coefficient per iteration. */
                        iProduct = arrBlock[i] * arrTable[i];

                        /* SATURATED RATHER THAN REFUSED, which is a            */
                        /* deliberate departure from the baseline decoder's     */
                        /* JPEG_Mul2ShortsValid check and matches the source.   */
                        /* stb multiplies into a short and lets it wrap; this   */
                        /* clamps instead, because a wrap turns one extreme     */
                        /* coefficient into its opposite and puts a bright      */
                        /* speck in a dark block, while a clamp merely loses    */
                        /* precision the picture never had.  Refusing is not    */
                        /* an option here: the file may be perfectly valid      */
                        /* and only the last refinement pushed the product      */
                        /* past the range.                                      */
                        if (iProduct > short.MaxValue)
                        {   /* Above the range a short can hold. */
                            iProduct = short.MaxValue;
                        }
                        else if (iProduct < short.MinValue)
                        {   /* Below it. */
                            iProduct = short.MinValue;
                        }

                        arrBlock[i] = (short)iProduct;
                    }
                }
            }
        }

        /*--------------------------------------------------------------------*/
        /* JPEG_TryScanForMarker:                                             */
        /*                                                                    */
        /* Walks forward through the data until it finds the next real        */
        /* marker, skipping anything that is not one.                         */
        /*                                                                    */
        /* WHY THIS IS NOT JPEG_TryReadMarker.  That function demands a 0xFF  */
        /* at the cursor and reports a fault when it does not find one, which */
        /* is exactly right between segments, where the position is known     */
        /* precisely.  After a scan's entropy data the position is NOT known  */
        /* precisely: the encoder pads to a byte boundary, the bit reader     */
        /* stops when its window is full rather than when the data runs out,  */
        /* and some encoders leave junk before the next marker.  Demanding    */
        /* the marker be the very next byte would refuse ordinary files.      */
        /*                                                                    */
        /* A STUFFED 0xFF00 IS SKIPPED RATHER THAN REPORTED, for the same     */
        /* reason: it means the walk is still inside entropy data that the    */
        /* bit reader did not consume, which is normal here and a fault only  */
        /* where the position was supposed to be exact.                       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     brIo       : The byte reader, advanced past the marker found.  */
        /*     iMarkerOut : Receives the marker's second byte.                */
        /*     sReason    : Receives the cause when the return is false.      */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool       : true when a marker was found; false when the data */
        /*                  ran out first, which for a progressive file is    */
        /*                  the end of the work rather than an error.         */
        /*--------------------------------------------------------------------*/

        private static bool JPEG_TryScanForMarker(ByteReader brIo, out int iMarkerOut,
                                                  out string sReason)
        {
            int iByte;
            int iNext;

            iMarkerOut = JPEG_MARKER_NONE;
            sReason    = "";

            for (;;)
            {   /* One byte per iteration until a marker turns up. */
                if (!brIo.BYTER_TryReadU8(out iByte))
                {   /* The data ended with no further marker in it. */
                    sReason = "JPEG: the data ended while looking for the marker " +
                              "after a scan";
                    return(false);
                }

                if (iByte != 0xFF)
                {   /* Padding, or entropy data the bit reader left behind. */
                    continue;
                }

                for (;;)
                {   /* Consume a run of fill bytes, if there is one. */
                    if (!brIo.BYTER_TryReadU8(out iNext))
                    {   /* A trailing 0xFF that names nothing. */
                        sReason = "JPEG: the data ends with an unterminated " +
                                  "marker after a scan";
                        return(false);
                    }

                    if (iNext != JPEG_MARKER_FILL)
                    {   /* The first byte after the fill run. */
                        break;
                    }
                }

                if (iNext == JPEG_MARKER_STUFF)
                {   /* A stuffed literal, so this 0xFF was data. Keep going. */
                    continue;
                }

                iMarkerOut = iNext;

                /* A real marker, with any fill before it consumed. */
                return(true);
            }
        }

        /*--------------------------------------------------------------------*/
        /* JPEG_TryDecodeProgressiveFrame:                                    */
        /*                                                                    */
        /* Decodes every scan of a progressive frame into one coefficient     */
        /* buffer and dequantises the result.                                 */
        /*                                                                    */
        /* Transcoded from the scan loop of stbi__decode_jpeg_image plus      */
        /* stbi__jpeg_finish.                                                 */
        /*                                                                    */
        /* THE SEGMENTS BETWEEN SCANS ARE NOT DECORATION.  A progressive file */
        /* routinely defines NEW HUFFMAN TABLES between one scan and the      */
        /* next -- the DC pass and the AC passes want different code lengths, */
        /* and an encoder that optimises its tables emits a fresh DHT before  */
        /* nearly every scan.  The reference files this was built against     */
        /* carry TEN scans each and redefine tables throughout.  A driver     */
        /* that looked only for the next SOS and skipped the segments would   */
        /* decode the first scan and then fail on a table the file did define */
        /* and it did not read.                                               */
        /*                                                                    */
        /* A MISSING EOI IS TOLERATED, and so is a scan that ends early.      */
        /* Progressive coding exists so that a partial file is a viewable     */
        /* picture; refusing one because its last refinement is missing would */
        /* throw away an image every other decoder displays.  What is decoded */
        /* stands, and the caller gets it.                                    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     brIo       : The byte reader, positioned at the first scan's   */
        /*                  entropy data.                                     */
        /*     hdrIn      : The frame header.                                 */
        /*     schFirst   : The first scan's header, already parsed by        */
        /*                  JPEG_TryWalkToScan.                               */
        /*     arrQuantIo : The quantisation tables; a later DQT may add to   */
        /*                  them, which is why this is not read-only.         */
        /*     arrHuffIo  : The Huffman tables, likewise added to by a later  */
        /*                  DHT.                                              */
        /*     pgOut      : Receives the finished, dequantised coefficients.  */
        /*     sReason    : Receives the cause when the return is false.      */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool       : true when the frame was decoded; false when the   */
        /*                  file is malformed.                                */
        /*--------------------------------------------------------------------*/

        public static bool JPEG_TryDecodeProgressiveFrame(ByteReader brIo,
                                                          JpegHeader hdrIn,
                                                          JpegScanHeader schFirst,
                                                          int[][] arrQuantIo,
                                                          JpegHuffmanTable[] arrHuffIo,
                                                          out JpegProgressiveState pgOut,
                                                          out string sReason)
        {
            JpegProgressiveState pgNew;
            JpegScanHeader       schUse;
            bool                 bDone;
            bool                 bFoundScan;
            int                  iMarker;
            int                  iScans;

            pgOut   = null;
            sReason = "";

            pgNew  = JPEG_AllocateProgressive(hdrIn);
            schUse = schFirst;
            bDone  = false;
            iScans = 0;

            while (!bDone)
            {   /* One scan, then whatever segments precede the next one. */
                if (!JPEG_TryDecodeScanProgressive(brIo, hdrIn, schUse, arrHuffIo,
                                                   pgNew, out iMarker, out sReason))
                {   /* A malformed scan. Refused rather than salvaged: unlike   */
                    /* a scan that merely ends early, this one desynchronised   */
                    /* the entropy stream, so every later scan would refine     */
                    /* the wrong coefficients.                                  */
                    return(false);
                }

                iScans = iScans + 1;

                if (iMarker == JPEG_MARKER_NONE)
                {   /* The reader stopped before reaching the next marker, so   */
                    /* walk forward and find it.                                */
                    if (!JPEG_TryScanForMarker(brIo, out iMarker, out sReason))
                    {   /* No marker and no more data: the file simply ends    */
                        /* here, which is tolerated. What is decoded stands.   */
                        break;
                    }
                }

                bFoundScan = false;

                while (!bFoundScan)
                {   /* One marker per iteration until the next scan starts,   */
                    /* the file ends, or something is wrong.                  */
                    if (iMarker == JPEG_MARKER_EOI)
                    {   /* The frame is complete. */
                        bDone = true;
                        break;
                    }

                    if (iMarker == JPEG_MARKER_SOS)
                    {   /* The next scan. Parsing it also validates Ss, Se,   */
                        /* Ah and Al against the progressive rules.           */
                        if (!JPEG_TryParseSos(brIo, hdrIn, out schUse, out sReason))
                        {   /* A malformed scan header. */
                            return(false);
                        }

                        bFoundScan = true;
                        break;
                    }

                    if (JPEG_IsStartOfFrame(iMarker))
                    {   /* A second frame header mid-file. */
                        sReason = "JPEG: a second frame header (" +
                                  JPEG_MarkerToText(iMarker) +
                                  ") appeared between progressive scans";
                        return(false);
                    }

                    if (!JPEG_IsRestart(iMarker))
                    {   /* A table or metadata segment. THIS is where the new   */
                        /* Huffman tables between scans are read.               */
                        if (!JPEG_TryProcessSegment(brIo, iMarker, hdrIn, arrQuantIo,
                                                    arrHuffIo, out sReason))
                        {   /* A malformed segment. */
                            return(false);
                        }
                    }

                    /* A stray restart marker between scans carries no          */
                    /* payload and needs nothing but stepping over, which the   */
                    /* branch above deliberately does not do for it.            */
                    if (!JPEG_TryReadMarker(brIo, out iMarker, out sReason))
                    {   /* The data ended after a segment with no EOI. */
                        bDone = true;
                        break;
                    }
                }
            }

            if (iScans == 0)
            {   /* Defensive: the loop always runs once, so this cannot be      */
                /* reached today. Stated anyway because a zero-scan frame       */
                /* would silently produce a mid-grey image, and a future edit   */
                /* to the loop shape is exactly how that would happen.          */
                sReason = "JPEG: the progressive frame carried no scans";
                return(false);
            }

            /* Nothing can touch the coefficients again, so this is the   */
            /* moment the quantisation tables apply.                      */
            JPEG_DequantizeProgressive(pgNew, hdrIn, arrQuantIo);

            pgOut = pgNew;

            /* Every scan the file carried has been folded into one buffer. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* THE INVERSE DCT AND ITS CONSTANTS                                  */
        /*                                                                    */
        /* Transcoded from stbi__idct_block and its STBI__IDCT_1D macro,      */
        /* which stb in turn derived from the Independent JPEG Group's        */
        /* jidctint.c "slow" integer IDCT -- the accurate one, not the fast   */
        /* approximation.                                                     */
        /*                                                                    */
        /* THE COEFFICIENTS ARE COMPUTED, NOT PASTED, and that is deliberate. */
        /* stb writes them as stbi__f2f(0.5411961f) and friends, a macro that */
        /* scales a real number by 4096 and rounds.  Transcribing the twelve  */
        /* resulting integers by hand would be twelve chances to fat-finger a */
        /* digit, and a single wrong constant produces an image that is       */
        /* almost right -- slightly soft, or faintly ringing -- which is the  */
        /* hardest kind of error to see and exactly what a tolerance absorbs. */
        /* JPEG_F2F runs the same arithmetic at static-init time instead, so  */
        /* the source of truth is the printed decimal rather than my typing.  */
        /* Calculated beats hard-coded as a general rule, and here it also    */
        /* buys correctness.                                                  */
        /*--------------------------------------------------------------------*/

        private const int JPEG_IDCT_SCALE_BITS = 12;     // the constants are scaled by 1 << 12
        private const int JPEG_IDCT_PASS1_BITS = 10;     // first pass keeps 2 extra bits of precision, so shifts 12 - 2
        private const int JPEG_IDCT_PASS2_BITS = 17;     // second pass removes 12 + 2 from pass 1 + 3 from the two sqrt(8) scalings
        private const int JPEG_LEVEL_SHIFT     = 128;    // samples are coded centred on zero and stored centred on 128

        private static readonly int JPEG_IDCT_C_0_541196 = JPEG_F2F(0.5411961);
        private static readonly int JPEG_IDCT_C_N1_847759 = JPEG_F2F(-1.847759065);
        private static readonly int JPEG_IDCT_C_0_765367 = JPEG_F2F(0.765366865);
        private static readonly int JPEG_IDCT_C_1_175876 = JPEG_F2F(1.175875602);
        private static readonly int JPEG_IDCT_C_0_298631 = JPEG_F2F(0.298631336);
        private static readonly int JPEG_IDCT_C_2_053120 = JPEG_F2F(2.053119869);
        private static readonly int JPEG_IDCT_C_3_072711 = JPEG_F2F(3.072711026);
        private static readonly int JPEG_IDCT_C_1_501321 = JPEG_F2F(1.501321110);
        private static readonly int JPEG_IDCT_C_N0_899976 = JPEG_F2F(-0.899976223);
        private static readonly int JPEG_IDCT_C_N2_562915 = JPEG_F2F(-2.562915447);
        private static readonly int JPEG_IDCT_C_N1_961571 = JPEG_F2F(-1.961570560);
        private static readonly int JPEG_IDCT_C_N0_390181 = JPEG_F2F(-0.390180644);

        /*--------------------------------------------------------------------*/
        /* JPEG_F2F:                                                          */
        /*                                                                    */
        /* Turns one of the IDCT's real coefficients into the fixed-point     */
        /* integer the integer IDCT multiplies by: scale by 1 << 12, add a    */
        /* half, and truncate.                                                */
        /*                                                                    */
        /* THE TRUNCATION IS TOWARD ZERO ON BOTH SIDES, which matters because */
        /* half the coefficients are negative.  Adding 0.5 and truncating is  */
        /* round-half-up for a positive number and round-half-DOWN in         */
        /* magnitude for a negative one -- not symmetric rounding.  C# casts  */
        /* a double to int exactly as C does, so the arithmetic reproduces    */
        /* the source's constants bit for bit rather than merely closely.     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     dValue  : The real coefficient, as the source prints it.       */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int     : The scaled fixed-point form.                         */
        /*--------------------------------------------------------------------*/

        private static int JPEG_F2F(double dValue)
        {
            /* Scale, bias, truncate -- the source's macro, verbatim. */
            return((int)((dValue * 4096.0) + 0.5));
        }

        /*--------------------------------------------------------------------*/
        /* JPEG_Clamp:                                                        */
        /*                                                                    */
        /* Saturates an int into a byte, which is what turns a mathematically */
        /* correct but out-of-range IDCT result into a displayable sample.    */
        /*                                                                    */
        /* OUT OF RANGE IS NORMAL, not a sign of damage: the IDCT of a real   */
        /* block routinely overshoots slightly at a sharp edge, which is      */
        /* ringing, and the format expects the decoder to clip it.  Treating  */
        /* an overshoot as corruption would reject ordinary photographs.      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iValue  : The IDCT output, already level-shifted.              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     byte    : iValue clipped to 0 to 255.                          */
        /*--------------------------------------------------------------------*/

        private static byte JPEG_Clamp(int iValue)
        {
            if (iValue < 0)
            {   /* Undershoot, which a dark edge produces. */
                return(0);
            }

            if (iValue > 255)
            {   /* Overshoot, which a bright edge produces. */
                return(255);
            }

            /* Already in range, which is the overwhelmingly common case. */
            return((byte)iValue);
        }

        /*--------------------------------------------------------------------*/
        /* JPEG_Idct1D:                                                       */
        /*                                                                    */
        /* One dimension of the inverse DCT over eight samples, leaving its   */
        /* results in the eight outputs the caller then combines.             */
        /*                                                                    */
        /* WHY THIS IS A FUNCTION WITH SIXTEEN PARAMETERS rather than         */
        /* something tidier.  In the source it is a MACRO, textually pasted   */
        /* into both passes, and it deliberately leaves eight named           */
        /* intermediates in the caller's scope because the two passes combine */
        /* them DIFFERENTLY -- the first pass shifts by 10 and stores, the    */
        /* second adds the level shift, shifts by 17 and clamps.  C# has no   */
        /* macro, and the two honest transcriptions are this or writing the   */
        /* body out twice.  A function keeps the arithmetic in ONE place,     */
        /* which is what matters: a discrepancy between two copies of a       */
        /* thirty-line butterfly would be nearly impossible to spot and would */
        /* show up only as a subtly wrong image.                              */
        /*                                                                    */
        /* THE OUTPUTS ARE NOT SAMPLES.  x0 to x3 and t0 to t3 are the        */
        /* butterfly's halves; a sample is a sum or difference of one from    */
        /* each, which is why the caller does the combining.                  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     s0 to s7  : The eight inputs, in natural order.                */
        /*     x0 to x3  : Receive the even-part results.                     */
        /*     t0 to t3  : Receive the odd-part results.                      */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void      : Results are in the eight out parameters.           */
        /*--------------------------------------------------------------------*/

        private static void JPEG_Idct1D(int s0, int s1, int s2, int s3,
                                        int s4, int s5, int s6, int s7,
                                        out int x0, out int x1, out int x2, out int x3,
                                        out int t0, out int t1, out int t2, out int t3)
        {
            int p1;
            int p2;
            int p3;
            int p4;
            int p5;

            /* THE EVEN PART, from coefficients 2 and 6. One multiply is        */
            /* shared between the two outputs, which is the whole reason this   */
            /* form is faster than the definition.                              */
            p2 = s2;
            p3 = s6;
            p1 = (p2 + p3) * JPEG_IDCT_C_0_541196;
            t2 = p1 + (p3 * JPEG_IDCT_C_N1_847759);
            t3 = p1 + (p2 * JPEG_IDCT_C_0_765367);

            /* Coefficients 0 and 4 need no multiply at all -- their weights   */
            /* are 1, so they are only scaled up to match the others.          */
            p2 = s0;
            p3 = s4;
            t0 = (p2 + p3) << JPEG_IDCT_SCALE_BITS;
            t1 = (p2 - p3) << JPEG_IDCT_SCALE_BITS;

            x0 = t0 + t3;
            x3 = t0 - t3;
            x1 = t1 + t2;
            x2 = t1 - t2;

            /* THE ODD PART, from coefficients 1, 3, 5 and 7. */
            t0 = s7;
            t1 = s5;
            t2 = s3;
            t3 = s1;

            p3 = t0 + t2;
            p4 = t1 + t3;
            p1 = t0 + t3;
            p2 = t1 + t2;
            p5 = (p3 + p4) * JPEG_IDCT_C_1_175876;

            t0 = t0 * JPEG_IDCT_C_0_298631;
            t1 = t1 * JPEG_IDCT_C_2_053120;
            t2 = t2 * JPEG_IDCT_C_3_072711;
            t3 = t3 * JPEG_IDCT_C_1_501321;

            p1 = p5 + (p1 * JPEG_IDCT_C_N0_899976);
            p2 = p5 + (p2 * JPEG_IDCT_C_N2_562915);
            p3 = p3 * JPEG_IDCT_C_N1_961571;
            p4 = p4 * JPEG_IDCT_C_N0_390181;

            t3 = t3 + p1 + p4;
            t2 = t2 + p2 + p3;
            t1 = t1 + p2 + p4;
            t0 = t0 + p1 + p3;
        }

        /*--------------------------------------------------------------------*/
        /* JPEG_IdctBlock:                                                    */
        /*                                                                    */
        /* Turns one 8x8 block of dequantised coefficients into 64 samples,   */
        /* level-shifted and clamped, written into a caller's plane.          */
        /*                                                                    */
        /* TWO PASSES, COLUMNS THEN ROWS, because a 2-D DCT is separable --   */
        /* eight one-dimensional transforms down, then eight across, is the   */
        /* same answer as the two-dimensional definition and costs vastly     */
        /* less.  The intermediate keeps TWO EXTRA BITS of precision (the     */
        /* first pass shifts by 10 rather than 12) so the rounding of the     */
        /* first pass does not accumulate visibly into the second.            */
        /*                                                                    */
        /* THE ALL-ZERO COLUMN SHORTCUT IS NOT AN OPTIMISATION ONLY.  When    */
        /* every AC coefficient in a column is zero the transform of that     */
        /* column is a constant, and computing it directly as d[0] * 4 avoids */
        /* eight multiplies AND is exactly what the general path would        */
        /* produce.  Most blocks in most photographs have mostly-empty        */
        /* columns, so this is the common case rather than a corner.          */
        /*                                                                    */
        /* THE LEVEL SHIFT AND THE ROUNDING RIDE ALONG WITH THE SECOND PASS.  */
        /* JPEG codes samples centred on zero, so 128 has to be added back;   */
        /* adding it BEFORE the final shift, pre-scaled by 1 << 17, costs     */
        /* nothing, and the 65536 beside it is the half-bit that turns a      */
        /* truncating shift into a rounding one.                              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     arrBlockIn  : 64 dequantised coefficients in NATURAL order.    */
        /*     arrPlaneIo  : Destination plane, one byte per sample.          */
        /*     iOffset     : Index in arrPlaneIo of this block's top-left.    */
        /*     iStride     : Samples per plane row.                           */
        /*                                                                    */
        /* PUBLIC FOR TESTABILITY, like JpegBitReader and JPEG_TryDecodeBlock */
        /* before it, and for a reason specific to this function: the only    */
        /* worthwhile check on an IDCT is against the MATHEMATICAL DEFINITION */
        /* in floating point, and that comparison cannot be made through      */
        /* JPEG_TryDecode because a whole-file path can only reach the        */
        /* coefficient blocks an encoder is willing to produce.  Reaching the */
        /* transform directly lets a test drive it with arbitrary             */
        /* coefficients, including the extremes that decide the tolerance.    */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void        : 64 samples are written into the plane.           */
        /*--------------------------------------------------------------------*/

        public static void JPEG_IdctBlock(short[] arrBlockIn, byte[] arrPlaneIo,
                                          int iOffset, int iStride)
        {
            int[] arrWork;
            int   iDcTerm;
            int   iRowBase;
            int   x0;
            int   x1;
            int   x2;
            int   x3;
            int   t0;
            int   t1;
            int   t2;
            int   t3;
            int   i;

            arrWork = new int[JPEG_BLOCK_SAMPLES];

            for (i = 0; i < 8; i++)
            {   /* One COLUMN per iteration; the stride through the block is   */
                /* eight because the block is stored row-major.                */
                if (arrBlockIn[i + 8] == 0 && arrBlockIn[i + 16] == 0 &&
                    arrBlockIn[i + 24] == 0 && arrBlockIn[i + 32] == 0 &&
                    arrBlockIn[i + 40] == 0 && arrBlockIn[i + 48] == 0 &&
                    arrBlockIn[i + 56] == 0)
                {   /* Every AC in this column is zero, so the column        */
                    /* transforms to a constant. Times four because the      */
                    /* general path would leave the same two extra bits of   */
                    /* precision the next pass expects.                      */
                    iDcTerm = arrBlockIn[i] * 4;

                    arrWork[i]      = iDcTerm;
                    arrWork[i + 8]  = iDcTerm;
                    arrWork[i + 16] = iDcTerm;
                    arrWork[i + 24] = iDcTerm;
                    arrWork[i + 32] = iDcTerm;
                    arrWork[i + 40] = iDcTerm;
                    arrWork[i + 48] = iDcTerm;
                    arrWork[i + 56] = iDcTerm;
                }
                else
                {   /* A column with real content, so the full butterfly. */
                    JPEG_Idct1D((int)arrBlockIn[i], (int)arrBlockIn[i + 8],
                                (int)arrBlockIn[i + 16], (int)arrBlockIn[i + 24],
                                (int)arrBlockIn[i + 32], (int)arrBlockIn[i + 40],
                                (int)arrBlockIn[i + 48], (int)arrBlockIn[i + 56],
                                out x0, out x1, out x2, out x3,
                                out t0, out t1, out t2, out t3);

                    /* Round rather than truncate, keeping two extra bits. */
                    x0 = x0 + 512;
                    x1 = x1 + 512;
                    x2 = x2 + 512;
                    x3 = x3 + 512;

                    arrWork[i]      = (x0 + t3) >> JPEG_IDCT_PASS1_BITS;
                    arrWork[i + 56] = (x0 - t3) >> JPEG_IDCT_PASS1_BITS;
                    arrWork[i + 8]  = (x1 + t2) >> JPEG_IDCT_PASS1_BITS;
                    arrWork[i + 48] = (x1 - t2) >> JPEG_IDCT_PASS1_BITS;
                    arrWork[i + 16] = (x2 + t1) >> JPEG_IDCT_PASS1_BITS;
                    arrWork[i + 40] = (x2 - t1) >> JPEG_IDCT_PASS1_BITS;
                    arrWork[i + 24] = (x3 + t0) >> JPEG_IDCT_PASS1_BITS;
                    arrWork[i + 32] = (x3 - t0) >> JPEG_IDCT_PASS1_BITS;
                }
            }

            for (i = 0; i < 8; i++)
            {   /* One ROW per iteration. No all-zero shortcut here: the       */
                /* first pass spread each column's content across every row,   */
                /* so a row of zeroes is no longer likely.                     */
                iRowBase = i * 8;

                JPEG_Idct1D(arrWork[iRowBase], arrWork[iRowBase + 1],
                            arrWork[iRowBase + 2], arrWork[iRowBase + 3],
                            arrWork[iRowBase + 4], arrWork[iRowBase + 5],
                            arrWork[iRowBase + 6], arrWork[iRowBase + 7],
                            out x0, out x1, out x2, out x3,
                            out t0, out t1, out t2, out t3);

                /* The half-bit for rounding, plus the level shift pre-scaled   */
                /* so it survives the shift below.                              */
                x0 = x0 + 65536 + (JPEG_LEVEL_SHIFT << JPEG_IDCT_PASS2_BITS);
                x1 = x1 + 65536 + (JPEG_LEVEL_SHIFT << JPEG_IDCT_PASS2_BITS);
                x2 = x2 + 65536 + (JPEG_LEVEL_SHIFT << JPEG_IDCT_PASS2_BITS);
                x3 = x3 + 65536 + (JPEG_LEVEL_SHIFT << JPEG_IDCT_PASS2_BITS);

                arrPlaneIo[iOffset + (i * iStride)]     = JPEG_Clamp((x0 + t3) >> JPEG_IDCT_PASS2_BITS);
                arrPlaneIo[iOffset + (i * iStride) + 7] = JPEG_Clamp((x0 - t3) >> JPEG_IDCT_PASS2_BITS);
                arrPlaneIo[iOffset + (i * iStride) + 1] = JPEG_Clamp((x1 + t2) >> JPEG_IDCT_PASS2_BITS);
                arrPlaneIo[iOffset + (i * iStride) + 6] = JPEG_Clamp((x1 - t2) >> JPEG_IDCT_PASS2_BITS);
                arrPlaneIo[iOffset + (i * iStride) + 2] = JPEG_Clamp((x2 + t1) >> JPEG_IDCT_PASS2_BITS);
                arrPlaneIo[iOffset + (i * iStride) + 5] = JPEG_Clamp((x2 - t1) >> JPEG_IDCT_PASS2_BITS);
                arrPlaneIo[iOffset + (i * iStride) + 3] = JPEG_Clamp((x3 + t0) >> JPEG_IDCT_PASS2_BITS);
                arrPlaneIo[iOffset + (i * iStride) + 4] = JPEG_Clamp((x3 - t0) >> JPEG_IDCT_PASS2_BITS);
            }
        }

        /*--------------------------------------------------------------------*/
        /* THE CHROMA RESAMPLERS                                              */
        /*                                                                    */
        /* A subsampled JPEG stores its chroma planes SMALLER than its luma   */
        /* plane, and these five functions put them back to full size, one    */
        /* row at a time.  Which one a component uses follows from its        */
        /* sampling factors against the frame's maxima: hs = h_max / h and    */
        /* vs = v_max / v, so 4:2:0 chroma is hs 2 and vs 2, 4:2:2 chroma is  */
        /* hs 2 and vs 1, and anything already full size is hs 1 and vs 1.    */
        /*                                                                    */
        /* THEY ARE NOT DUPLICATION, AND THAT IS THE WHOLE POINT OF TESTING   */
        /* THEM SEPARATELY.  The obvious implementation of "make this row     */
        /* twice as wide" is to emit every sample twice, and it is WRONG:     */
        /* the 2x filters interpolate, weighting the nearer source sample     */
        /* three to one against its neighbour, and they treat the first and   */
        /* last sample of a row as special cases with no neighbour on one     */
        /* side.  Plain duplication is correct at every interior sample of a  */
        /* FLAT region and wrong only on gradients and at edges -- which is   */
        /* to say, wrong exactly where a tolerance-based image comparison is  */
        /* least likely to notice.  Hand-computed expectations catch it at    */
        /* once, which is why these were built and proven on their own.       */
        /*                                                                    */
        /* ALL FIVE SHARE ONE SIGNATURE so a caller can choose between them   */
        /* on the component's factors without special-casing the call.  stb   */
        /* uses a function pointer for that; this library uses a switch on an */
        /* identifier instead, because an explicit dispatch keeps every name  */
        /* greppable where a runtime-resolved one does not.                   */
        /*                                                                    */
        /* THE RETURN VALUE IS THE BUFFER THAT HOLDS THE RESULT, and it is    */
        /* not always arrOutIo.  JPEG_ResampleRow1 has nothing to do and      */
        /* hands back the INPUT buffer untouched, exactly as the source does. */
        /* That is not a micro-optimisation to be tidied away: in a 4:2:0     */
        /* image the LUMA plane -- the largest of the three -- uses that      */
        /* resampler on every row, so copying instead would add a full-frame  */
        /* memory copy per image for no benefit whatsoever.  Callers must     */
        /* use the returned array and offset rather than assuming arrOutIo.   */
        /*                                                                    */
        /* Transcoded from resample_row_1, stbi__resample_row_v_2,            */
        /* stbi__resample_row_h_2, stbi__resample_row_hv_2 and                */
        /* stbi__resample_row_generic.  The SIMD variant of hv_2 is one of    */
        /* the 641 lines C# discards.                                         */
        /*--------------------------------------------------------------------*/

        /*--------------------------------------------------------------------*/
        /* JPEG_ResampleRow1:                                                 */
        /*                                                                    */
        /* The no-op resampler, for a component already at full resolution.   */
        /*                                                                    */
        /* Returns the INPUT buffer rather than writing anything -- see the   */
        /* note above for why that is deliberate and must not be "fixed".     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     arrOutIo      : Ignored; present so all five share a shape.    */
        /*     iOutOffset    : Ignored.                                       */
        /*     arrNear       : The row itself.                                */
        /*     iNearOffset   : Where that row starts.                         */
        /*     arrFar        : Ignored; there is nothing to interpolate with. */
        /*     iFarOffset    : Ignored.                                       */
        /*     iWidth        : Ignored.                                       */
        /*     iStep         : Ignored.                                       */
        /*     iResultOffset : Receives iNearOffset.                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     byte[]        : arrNear, unchanged.                            */
        /*--------------------------------------------------------------------*/

        public static byte[] JPEG_ResampleRow1(byte[] arrOutIo, int iOutOffset,
                                               byte[] arrNear, int iNearOffset,
                                               byte[] arrFar, int iFarOffset,
                                               int iWidth, int iStep,
                                               out int iResultOffset)
        {
            iResultOffset = iNearOffset;

            /* Nothing to resample: the caller already has the row it wants. */
            return(arrNear);
        }

        /*---------------------------------------------------------------------*/
        /* JPEG_ResampleRowV2:                                                 */
        /*                                                                     */
        /* Doubles a component VERTICALLY: one output row interpolated         */
        /* between the nearer source row and the one beyond it.                */
        /*                                                                     */
        /* THE 3:1 WEIGHTING IS THE INTERPOLATION.  An output row sits one     */
        /* quarter of the way between two source rows, not halfway, because    */
        /* doubling produces TWO output rows per source row and each is        */
        /* nearer to one source than the other.  The caller supplies whichever */
        /* neighbour is correct for the row being produced, which is why       */
        /* this function does not know or care which of the two it is making.  */
        /*                                                                     */
        /* The +2 before the shift is rounding: it turns a truncating divide   */
        /* by four into a rounding one.                                        */
        /*                                                                     */
        /* Arguments:                                                          */
        /*     arrOutIo      : Receives iWidth samples.                        */
        /*     iOutOffset    : Where to write them.                            */
        /*     arrNear       : The nearer source row.                          */
        /*     iNearOffset   : Where it starts.                                */
        /*     arrFar        : The further source row.                         */
        /*     iFarOffset    : Where it starts.                                */
        /*     iWidth        : Samples in a source row.                        */
        /*     iStep         : Ignored; vertical doubling does not widen.      */
        /*     iResultOffset : Receives iOutOffset.                            */
        /*                                                                     */
        /* Returns:                                                            */
        /*     byte[]        : arrOutIo.                                       */
        /*---------------------------------------------------------------------*/

        public static byte[] JPEG_ResampleRowV2(byte[] arrOutIo, int iOutOffset,
                                                byte[] arrNear, int iNearOffset,
                                                byte[] arrFar, int iFarOffset,
                                                int iWidth, int iStep,
                                                out int iResultOffset)
        {
            int i;

            for (i = 0; i < iWidth; i++)
            {   /* Three parts the near row, one part the far one. */
                arrOutIo[iOutOffset + i] =
                    (byte)(((3 * (int)arrNear[iNearOffset + i]) +
                            (int)arrFar[iFarOffset + i] + 2) >> 2);
            }

            iResultOffset = iOutOffset;

            /* One row, same width, interpolated between two source rows. */
            return(arrOutIo);
        }

        /*--------------------------------------------------------------------*/
        /* JPEG_ResampleRowH2:                                                */
        /*                                                                    */
        /* Doubles a row HORIZONTALLY, producing 2 * iWidth samples.          */
        /*                                                                    */
        /* THE TWO END SAMPLES ARE COPIED, NOT INTERPOLATED, and that is the  */
        /* detail most easily got wrong.  The first output sample has no      */
        /* source sample to its left and the last has none to its right, so   */
        /* each simply takes the value it sits on.  Interpolating them        */
        /* against a phantom neighbour -- or against the wrong one -- shifts  */
        /* the whole row by half a sample and puts a visible seam down one    */
        /* edge of every subsampled image.                                    */
        /*                                                                    */
        /* A ONE-SAMPLE ROW IS ITS OWN CASE.  There is no neighbour in either */
        /* direction, so both outputs are the input.  Not a theoretical       */
        /* corner: a chroma plane of a very narrow image really is one sample */
        /* wide, and the general loop below would read past the row's end.    */
        /*                                                                    */
        /* THIS FUNCTION CARRIES DEVIATION 1, AND IT IS NOW TWO CHANGES       */
        /* rather than one, both taken from libjpeg-turbo 3.1.4's             */
        /* h2v1_fancy_upsample.  FIRST, stb swaps the two operands of         */
        /* the LAST interpolated output, weighting the farther sample         */
        /* by three where every other output weights the nearer one --        */
        /* a bug, not a defensible filter.  SECOND, the interior's            */
        /* rounding bias alternates +1 / +2 by output parity, which is        */
        /* libjpeg's ordered dither and was MEASURED against GDI+ to be       */
        /* closer than stb's uniform +2.  Do not restore either.              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     arrOutIo      : Receives 2 * iWidth samples.                   */
        /*     iOutOffset    : Where to write them.                           */
        /*     arrNear       : The source row.                                */
        /*     iNearOffset   : Where it starts.                               */
        /*     arrFar        : Ignored; horizontal doubling uses one row.     */
        /*     iFarOffset    : Ignored.                                       */
        /*     iWidth        : Samples in the source row, at least 1.         */
        /*     iStep         : Ignored.                                       */
        /*     iResultOffset : Receives iOutOffset.                           */
        /*                                                                    */
        /* Returns:                                                           */
        /*     byte[]        : arrOutIo.                                      */
        /*--------------------------------------------------------------------*/

        public static byte[] JPEG_ResampleRowH2(byte[] arrOutIo, int iOutOffset,
                                                byte[] arrNear, int iNearOffset,
                                                byte[] arrFar, int iFarOffset,
                                                int iWidth, int iStep,
                                                out int iResultOffset)
        {
            int iBlend;
            int i;

            iResultOffset = iOutOffset;

            if (iWidth == 1)
            {   /* One sample and no neighbour either side, so there is   */
                /* nothing to interpolate and both outputs take it.       */
                arrOutIo[iOutOffset]     = arrNear[iNearOffset];
                arrOutIo[iOutOffset + 1] = arrNear[iNearOffset];

                /* A two-sample row, both the same. */
                return(arrOutIo);
            }

            /* The first output sample has no left neighbour: it is a copy. */
            arrOutIo[iOutOffset] = arrNear[iNearOffset];

            /* The second leans three to one toward the sample it sits on. */
            arrOutIo[iOutOffset + 1] =
                (byte)((((int)arrNear[iNearOffset] * 3) +
                        (int)arrNear[iNearOffset + 1] + 2) >> 2);

            for (i = 1; i < (iWidth - 1); i++)
            {   /* Every interior sample produces two outputs, each leaning   */
                /* three to one toward it and one part toward the neighbour   */
                /* on that side.  The rounding bias is deliberately NOT       */
                /* hoisted with the triple: the two outputs round apart.      */
                iBlend = 3 * (int)arrNear[iNearOffset + i];

                /* THE EVEN OUTPUT ROUNDS WITH +1 AND THE ODD ONE WITH +2.     */
                /* That asymmetry is libjpeg's ordered dither, not a typo.     */
                /* A sample landing exactly halfway rounds DOWN at an even     */
                /* output position and UP at an odd one, so a run of halfway   */
                /* cases alternates rather than drifting the whole row one     */
                /* way.  stb biases both by +2 and this library did until      */
                /* item 12; DEVIATION 1 in the file header carries the         */
                /* measurement against GDI+ that chose libjpeg's scheme.       */
                arrOutIo[iOutOffset + (i * 2)] =
                    (byte)((iBlend + (int)arrNear[iNearOffset + i - 1] + 1) >> 2);
                arrOutIo[iOutOffset + (i * 2) + 1] =
                    (byte)((iBlend + (int)arrNear[iNearOffset + i + 1] + 2) >> 2);
            }

            /* THE LAST INTERPOLATED OUTPUT, AND A DELIBERATE DEVIATION FROM   */
            /* stb.  It leans three to one toward the sample it SITS ON --     */
            /* the last one -- mirroring what the first column does.  stb      */
            /* has these two operands the other way round, weighting the       */
            /* last-but-one sample, which turns a monotonic input into a       */
            /* non-monotonic output.  DEVIATION 1 in this file's header has    */
            /* the evidence that this is a BUG in stb and not a different      */
            /* defensible filter.  Its +1 is NOT a special case any more:      */
            /* this is an EVEN output position, so the ordered dither above    */
            /* gives it +1 exactly like every other even output.               */
            arrOutIo[iOutOffset + (i * 2)] =
                (byte)((((int)arrNear[iNearOffset + iWidth - 1] * 3) +
                        (int)arrNear[iNearOffset + iWidth - 2] + 1) >> 2);

            /* And the last has no right neighbour: another copy. */
            arrOutIo[iOutOffset + (i * 2) + 1] = arrNear[iNearOffset + iWidth - 1];

            /* Twice as many samples, interpolated except at the two ends. */
            return(arrOutIo);
        }

        /*--------------------------------------------------------------------*/
        /* JPEG_ResampleRowHV2:                                               */
        /*                                                                    */
        /* Doubles a row in BOTH directions at once: 2 * iWidth samples,      */
        /* each interpolated vertically between two source rows and           */
        /* horizontally between two source columns.                           */
        /*                                                                    */
        /* THIS IS THE 4:2:0 CASE and therefore the one that runs on almost   */
        /* every photograph in existence, GDI+'s output included.             */
        /*                                                                    */
        /* IT IS NOT TWO PASSES.  Doing the vertical blend and then the       */
        /* horizontal one would round twice and cost a temporary row; instead */
        /* each source column's vertical blend is computed ONCE, kept in t1,  */
        /* carried into the next iteration as t0, and the two horizontal      */
        /* outputs are formed from that pair.  The divisor is sixteen rather  */
        /* than four because the values being combined are themselves already */
        /* scaled by four -- 3*near + far, not (3*near + far)/4.              */
        /*                                                                    */
        /* THE ENDS ARE VERTICAL-ONLY.  The first and last output samples     */
        /* have no horizontal neighbour, so they take their column's vertical */
        /* blend directly, divided by four rather than sixteen.               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     arrOutIo      : Receives 2 * iWidth samples.                   */
        /*     iOutOffset    : Where to write them.                           */
        /*     arrNear       : The nearer source row.                         */
        /*     iNearOffset   : Where it starts.                               */
        /*     arrFar        : The further source row.                        */
        /*     iFarOffset    : Where it starts.                               */
        /*     iWidth        : Samples in a source row, at least 1.           */
        /*     iStep         : Ignored.                                       */
        /*     iResultOffset : Receives iOutOffset.                           */
        /*                                                                    */
        /* Returns:                                                           */
        /*     byte[]        : arrOutIo.                                      */
        /*--------------------------------------------------------------------*/

        public static byte[] JPEG_ResampleRowHV2(byte[] arrOutIo, int iOutOffset,
                                                 byte[] arrNear, int iNearOffset,
                                                 byte[] arrFar, int iFarOffset,
                                                 int iWidth, int iStep,
                                                 out int iResultOffset)
        {
            int iPrevColumn;
            int iThisColumn;
            int i;

            iResultOffset = iOutOffset;

            if (iWidth == 1)
            {   /* One column, so no horizontal interpolation is possible and   */
                /* both outputs are the vertical blend of that column.          */
                arrOutIo[iOutOffset] =
                    (byte)(((3 * (int)arrNear[iNearOffset]) +
                            (int)arrFar[iFarOffset] + 2) >> 2);
                arrOutIo[iOutOffset + 1] = arrOutIo[iOutOffset];

                /* Two samples, both the same vertical blend. */
                return(arrOutIo);
            }

            /* The vertical blend of the first column, kept UNSCALED so the   */
            /* horizontal step below can combine two of them at full          */
            /* precision.                                                     */
            iThisColumn = (3 * (int)arrNear[iNearOffset]) + (int)arrFar[iFarOffset];

            /* The first output has no left neighbour: vertical blend only. */
            arrOutIo[iOutOffset] = (byte)((iThisColumn + 2) >> 2);

            for (i = 1; i < iWidth; i++)
            {   /* One source column per iteration, producing the two outputs   */
                /* that straddle the gap between it and the previous column.    */
                iPrevColumn = iThisColumn;
                iThisColumn = (3 * (int)arrNear[iNearOffset + i]) +
                              (int)arrFar[iFarOffset + i];

                arrOutIo[iOutOffset + (i * 2) - 1] =
                    (byte)(((3 * iPrevColumn) + iThisColumn + 8) >> 4);
                arrOutIo[iOutOffset + (i * 2)] =
                    (byte)(((3 * iThisColumn) + iPrevColumn + 8) >> 4);
            }

            /* The last output has no right neighbour: vertical blend only. */
            arrOutIo[iOutOffset + (iWidth * 2) - 1] = (byte)((iThisColumn + 2) >> 2);

            /* Twice as wide, and vertically blended throughout. */
            return(arrOutIo);
        }

        /*--------------------------------------------------------------------*/
        /* JPEG_ResampleRowGeneric:                                           */
        /*                                                                    */
        /* Widens a row by any whole factor, by NEAREST NEIGHBOUR -- every    */
        /* input sample repeated iStep times.                                 */
        /*                                                                    */
        /* THE FALLBACK, AND IT IS DELIBERATELY CRUDE.  The specification     */
        /* allows sampling factors up to four, so ratios of 3 and 4 are legal */
        /* and a decoder must handle them; they are also vanishingly rare in  */
        /* real files, so the source does not interpolate them and neither    */
        /* does this.  A file that used one would decode correctly but with   */
        /* blockier chroma than a dedicated filter would give.                */
        /*                                                                    */
        /* WORTH KNOWING WHEN READING THE TESTS: this is the one resampler    */
        /* for which plain duplication IS the right answer, which makes it    */
        /* the perfect control for the others -- if a 2x filter ever agrees   */
        /* with this function on a gradient, that filter has been reduced to  */
        /* duplication and its interpolation is gone.                         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     arrOutIo      : Receives iWidth * iStep samples.               */
        /*     iOutOffset    : Where to write them.                           */
        /*     arrNear       : The source row.                                */
        /*     iNearOffset   : Where it starts.                               */
        /*     arrFar        : Ignored.                                       */
        /*     iFarOffset    : Ignored.                                       */
        /*     iWidth        : Samples in the source row.                     */
        /*     iStep         : How many times to repeat each, at least 1.     */
        /*     iResultOffset : Receives iOutOffset.                           */
        /*                                                                    */
        /* Returns:                                                           */
        /*     byte[]        : arrOutIo.                                      */
        /*--------------------------------------------------------------------*/

        public static byte[] JPEG_ResampleRowGeneric(byte[] arrOutIo, int iOutOffset,
                                                     byte[] arrNear, int iNearOffset,
                                                     byte[] arrFar, int iFarOffset,
                                                     int iWidth, int iStep,
                                                     out int iResultOffset)
        {
            int i;
            int j;

            for (i = 0; i < iWidth; i++)
            {   /* One source sample per iteration. */
                for (j = 0; j < iStep; j++)
                {   /* Repeated iStep times, with no blending at all. */
                    arrOutIo[iOutOffset + (i * iStep) + j] = arrNear[iNearOffset + i];
                }
            }

            iResultOffset = iOutOffset;

            /* iStep times as many samples, each a copy. */
            return(arrOutIo);
        }

        /*--------------------------------------------------------------------*/
        /* Which resampler a component uses, chosen from its expansion        */
        /* factors.  An IDENTIFIER AND A SWITCH rather than a function        */
        /* pointer, which is what the source uses: this library prefers an    */
        /* explicit dispatch to a runtime-resolved one, and a                 */
        /* switch also keeps every call site's arguments visible at the point */
        /* they are passed.                                                   */
        /*--------------------------------------------------------------------*/

        private const int JPEG_RESAMPLE_ROW1    = 0;     // hs 1, vs 1: nothing to do
        private const int JPEG_RESAMPLE_ROW_V2  = 1;     // hs 1, vs 2: vertical only
        private const int JPEG_RESAMPLE_ROW_H2  = 2;     // hs 2, vs 1: horizontal only, the 4:2:2 chroma case
        private const int JPEG_RESAMPLE_ROW_HV2 = 3;     // hs 2, vs 2: both, the 4:2:0 chroma case
        private const int JPEG_RESAMPLE_GENERIC = 4;     // any other ratio: nearest neighbour

        private const int JPEG_LINE_BUFFER_SLACK = 3;    // a factor-four filter can write up to three samples past the last whole group

        /*--------------------------------------------------------------------*/
        /* JPEG_ResamplerKindFor:                                             */
        /*                                                                    */
        /* Picks the resampler for one pair of expansion factors.             */
        /*                                                                    */
        /* THE FALLBACK IS NOT DEAD CODE.  Sampling factors up to four are    */
        /* legal, so ratios of 3 and 4 can occur and a decoder must handle    */
        /* them; they are merely rare enough that nobody writes a dedicated   */
        /* filter for them, and neither does the source.                      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iHs   : Horizontal expansion, at least 1.                      */
        /*     iVs   : Vertical expansion, at least 1.                        */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int   : One of the JPEG_RESAMPLE_* identifiers.                */
        /*--------------------------------------------------------------------*/

        private static int JPEG_ResamplerKindFor(int iHs, int iVs)
        {
            if (iHs == 1 && iVs == 1)
            {   /* Already full size. */
                return(JPEG_RESAMPLE_ROW1);
            }

            if (iHs == 1 && iVs == 2)
            {   /* Half height only. */
                return(JPEG_RESAMPLE_ROW_V2);
            }

            if (iHs == 2 && iVs == 1)
            {   /* Half width only -- 4:2:2 chroma. */
                return(JPEG_RESAMPLE_ROW_H2);
            }

            if (iHs == 2 && iVs == 2)
            {   /* Half in both -- 4:2:0 chroma, the common case. */
                return(JPEG_RESAMPLE_ROW_HV2);
            }

            /* Some other legal but unusual ratio. */
            return(JPEG_RESAMPLE_GENERIC);
        }

        /*--------------------------------------------------------------------*/
        /* JPEG_ResampleOneRow:                                               */
        /*                                                                    */
        /* Runs one component's chosen resampler over the two source rows its */
        /* state currently straddles, then advances that state by one output  */
        /* row.                                                               */
        /*                                                                    */
        /* WHICH SOURCE ROW IS "NEAR" FLIPS HALFWAY THROUGH THE PHASE, and    */
        /* that is the subtlest thing here.  When a chroma row is expanded to */
        /* two output rows, the first output sits nearer the PREVIOUS source  */
        /* row and the second nearer the NEXT one, so the pair handed to the  */
        /* filter swaps over.  Get it wrong and the image is not obviously    */
        /* broken -- it is shifted half a chroma row, which reads as slightly */
        /* soft colour rather than as a fault.                                */
        /*                                                                    */
        /* THE ADVANCE STOPS AT THE COMPONENT'S LAST ROW.  line1 is only      */
        /* moved on while there is another source row to move to; at the      */
        /* bottom edge both pointers settle on the final row and the filter   */
        /* blends it with itself, which is the correct edge behaviour and     */
        /* also what stops the walk reading past the plane.                   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     rsIo       : The component's walk state, advanced by one row.  */
        /*     arrPlaneIn : The component's block-aligned sample plane.       */
        /*     iOffsetOut : Receives where the expanded row starts.           */
        /*                                                                    */
        /* Returns:                                                           */
        /*     byte[]     : The buffer holding the expanded row, which may be */
        /*                  the PLANE itself when nothing had to be done.     */
        /*--------------------------------------------------------------------*/

        private static byte[] JPEG_ResampleOneRow(JpegResamplerState rsIo,
                                                  byte[] arrPlaneIn,
                                                  out int iOffsetOut)
        {
            byte[] arrRow;
            int    iNear;
            int    iFar;
            bool   bSecondHalf;

            /* Past the halfway point of the phase the NEXT source row is the   */
            /* nearer one, so the pair swaps over.                              */
            bSecondHalf = (rsIo.JPEGRS_iYStep >= (rsIo.JPEGRS_iVs >> 1));

            if (bSecondHalf)
            {   /* Nearer the later row. */
                iNear = rsIo.JPEGRS_iLine1;
                iFar  = rsIo.JPEGRS_iLine0;
            }
            else
            {   /* Nearer the earlier row. */
                iNear = rsIo.JPEGRS_iLine0;
                iFar  = rsIo.JPEGRS_iLine1;
            }

            if (rsIo.JPEGRS_iKind == JPEG_RESAMPLE_ROW1)
            {   /* Nothing to do; the plane row IS the output row. */
                arrRow = JPEG_ResampleRow1(rsIo.JPEGRS_arrLineBuffer, 0,
                                           arrPlaneIn, iNear, arrPlaneIn, iFar,
                                           rsIo.JPEGRS_iWidthLores, rsIo.JPEGRS_iHs,
                                           out iOffsetOut);
            }
            else if (rsIo.JPEGRS_iKind == JPEG_RESAMPLE_ROW_V2)
            {   /* Vertical doubling. */
                arrRow = JPEG_ResampleRowV2(rsIo.JPEGRS_arrLineBuffer, 0,
                                            arrPlaneIn, iNear, arrPlaneIn, iFar,
                                            rsIo.JPEGRS_iWidthLores, rsIo.JPEGRS_iHs,
                                            out iOffsetOut);
            }
            else if (rsIo.JPEGRS_iKind == JPEG_RESAMPLE_ROW_H2)
            {   /* Horizontal doubling. */
                arrRow = JPEG_ResampleRowH2(rsIo.JPEGRS_arrLineBuffer, 0,
                                            arrPlaneIn, iNear, arrPlaneIn, iFar,
                                            rsIo.JPEGRS_iWidthLores, rsIo.JPEGRS_iHs,
                                            out iOffsetOut);
            }
            else if (rsIo.JPEGRS_iKind == JPEG_RESAMPLE_ROW_HV2)
            {   /* Both directions -- the 4:2:0 case. */
                arrRow = JPEG_ResampleRowHV2(rsIo.JPEGRS_arrLineBuffer, 0,
                                             arrPlaneIn, iNear, arrPlaneIn, iFar,
                                             rsIo.JPEGRS_iWidthLores, rsIo.JPEGRS_iHs,
                                             out iOffsetOut);
            }
            else
            {   /* Some other legal ratio: nearest neighbour. */
                arrRow = JPEG_ResampleRowGeneric(rsIo.JPEGRS_arrLineBuffer, 0,
                                                 arrPlaneIn, iNear, arrPlaneIn, iFar,
                                                 rsIo.JPEGRS_iWidthLores,
                                                 rsIo.JPEGRS_iHs, out iOffsetOut);
            }

            /* Advance the phase, and the source rows with it when it wraps. */
            rsIo.JPEGRS_iYStep = rsIo.JPEGRS_iYStep + 1;

            if (rsIo.JPEGRS_iYStep >= rsIo.JPEGRS_iVs)
            {   /* This source row is spent; step to the next pair. */
                rsIo.JPEGRS_iYStep = 0;
                rsIo.JPEGRS_iLine0 = rsIo.JPEGRS_iLine1;
                rsIo.JPEGRS_iYPos  = rsIo.JPEGRS_iYPos + 1;

                if (rsIo.JPEGRS_iYPos < rsIo.JPEGRS_iHeightLores)
                {   /* There is another source row, so line1 moves on. At the   */
                    /* bottom edge it does NOT, leaving both pointers on the    */
                    /* last row so the filter blends it with itself.            */
                    rsIo.JPEGRS_iLine1 = rsIo.JPEGRS_iLine1 + rsIo.JPEGRS_iStride;
                }
            }

            /* One row at the image's full width. */
            return(arrRow);
        }

        /*--------------------------------------------------------------------*/
        /* THE COLOUR TRANSFORM                                               */
        /*                                                                    */
        /* JPEG does not store red, green and blue.  It stores LUMA and two   */
        /* CHROMA DIFFERENCES, because the eye is far more sensitive to       */
        /* brightness than to colour and that is what makes chroma            */
        /* subsampling nearly free.  Undoing it is the last step of a colour  */
        /* decode:                                                            */
        /*                                                                    */
        /*     R = Y                 + 1.40200 * (Cr - 128)                   */
        /*     G = Y - 0.34414 * (Cb - 128) - 0.71414 * (Cr - 128)            */
        /*     B = Y + 1.77200 * (Cb - 128)                                   */
        /*                                                                    */
        /* Transcoded from stbi__YCbCr_to_RGB_row, in fixed point, with the   */
        /* constants COMPUTED at static init for the same reason the IDCT's   */
        /* are -- a mistyped digit here tints the whole image, which reads as */
        /* a plausible photograph taken under odd light rather than as a bug. */
        /*--------------------------------------------------------------------*/

        private const int JPEG_YCBCR_FRACTION_BITS = 20;    // the working scale; results are shifted back down by this
        private const int JPEG_CHROMA_CENTRE       = 128;   // Cb and Cr are stored offset by half the range

        private static readonly int JPEG_YCBCR_C_1_40200 = JPEG_Float2Fixed(1.40200);
        private static readonly int JPEG_YCBCR_C_0_71414 = JPEG_Float2Fixed(0.71414);
        private static readonly int JPEG_YCBCR_C_0_34414 = JPEG_Float2Fixed(0.34414);
        private static readonly int JPEG_YCBCR_C_1_77200 = JPEG_Float2Fixed(1.77200);

        /*--------------------------------------------------------------------*/
        /* JPEG_Float2Fixed:                                                  */
        /*                                                                    */
        /* Scales one colour-transform coefficient into the fixed-point form  */
        /* the conversion multiplies by.                                      */
        /*                                                                    */
        /* NOT THE SAME SCALE AS THE IDCT'S JPEG_F2F, which is why it is a    */
        /* separate function with a separate name rather than a shared one    */
        /* with a parameter.  This one scales by 4096 and then shifts up by a */
        /* further 8, giving 20 fractional bits, because the conversion works */
        /* at 20 bits while the transform works at 12.  Merging them behind   */
        /* one name would make the two scales look interchangeable, and they  */
        /* are not.                                                           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     dValue  : The real coefficient, as the source prints it.       */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int     : The scaled fixed-point form.                         */
        /*--------------------------------------------------------------------*/

        private static int JPEG_Float2Fixed(double dValue)
        {
            /* Scale, bias, truncate, then shift up the extra eight bits. */
            return(((int)((dValue * 4096.0) + 0.5)) << 8);
        }

        /*--------------------------------------------------------------------*/
        /* JPEG_YCbCrToRgbRow:                                                */
        /*                                                                    */
        /* Converts one row of Y, Cb and Cr samples into RGB, writing three   */
        /* bytes per pixel into a caller's buffer.                            */
        /*                                                                    */
        /* THE ODD-LOOKING MASK ON THE GREEN TERM IS DELIBERATE AND IS COPIED */
        /* FROM THE SOURCE.  stb writes                                       */
        /*                                                                    */
        /*     ((cb * -stbi__float2fixed(0.34414f)) & 0xffff0000)             */
        /*                                                                    */
        /* which throws away the low sixteen bits of that one product.  It is */
        /* not a rounding refinement -- it makes the result agree bit for bit */
        /* with an older version of the same decoder, and stb keeps it for    */
        /* compatibility.  Transcribed rather than "corrected" because        */
        /* the rule here is to transcode, and dropping it would put this      */
        /* library a count away from stb on some pixels for no gain.  It is   */
        /* flagged here so the next reader does not mistake it for a typo.    */
        /*                                                                    */
        /* Arguments:                                                         */
        /* EACH PLANE GETS ITS OWN OFFSET, and that is not over-generality.   */
        /* After resampling the three rows may sit in three different         */
        /* buffers at three different places: a component that needed no      */
        /* expansion hands back a row of its own PLANE, while one that was    */
        /* upsampled hands back its scratch line buffer.  A single shared     */
        /* base would silently read the wrong bytes for two of the three.     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     arrY        : Buffer holding the luma row.                     */
        /*     iYBase      : Where that row starts.                           */
        /*     arrCb       : Buffer holding the blue-difference row.          */
        /*     iCbBase     : Where that row starts.                           */
        /*     arrCr       : Buffer holding the red-difference row.           */
        /*     iCrBase     : Where that row starts.                           */
        /*     arrOutIo    : Destination, three bytes per pixel.              */
        /*     iOutBase    : Index of the row's first output byte.            */
        /*     iCount      : How many pixels to convert.  This is the IMAGE's */
        /*                   width, which is how the surplus the expansion    */
        /*                   produced gets cropped away here rather than in   */
        /*                   a separate pass.                                 */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void        : iCount * 3 bytes are written.                    */
        /*--------------------------------------------------------------------*/

        /*--------------------------------------------------------------------*/
        /* JPEG_Blinn8x8:                                                     */
        /*                                                                    */
        /* Multiplies two 0..255 values as though they were fractions of one, */
        /* rounding correctly, without a divide.                              */
        /*                                                                    */
        /* Transcoded from stbi__blinn_8x8, which takes its name from Jim     */
        /* Blinn's treatment of exactly this problem.  The naive form is      */
        /* (x * y) / 255, and the divide is both slow and, done as a shift,   */
        /* wrong: (x * y) >> 8 divides by 256 and so drifts every product     */
        /* slightly toward zero -- 255 times 255 comes out 254 instead of     */
        /* 255, and a fully inked CMYK white turns very faintly grey.  Adding */
        /* the high byte back in corrects that to the unit across the whole   */
        /* range, and the + 128 rounds instead of truncating.                 */
        /*                                                                    */
        /* Not a Try function: every input is a byte and every output is a    */
        /* byte, so there is nothing that can fail.                           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iX  : One factor, 0 to 255.                                    */
        /*     iY  : The other, 0 to 255.                                     */
        /*                                                                    */
        /* Returns:                                                           */
        /*     byte : The rounded product, 0 to 255.                          */
        /*--------------------------------------------------------------------*/

        private static byte JPEG_Blinn8x8(int iX, int iY)
        {
            int iProduct;

            iProduct = (iX * iY) + 128;

            /* The high byte added back is what makes this a divide by 255   */
            /* rather than by 256.                                           */
            return((byte)((iProduct + (iProduct >> 8)) >> 8));
        }

        /*--------------------------------------------------------------------*/
        /* JPEG_IsRawRgb:                                                     */
        /*                                                                    */
        /* Says whether a three-component frame carries RGB samples directly  */
        /* rather than YCbCr.                                                 */
        /*                                                                    */
        /* TWO INDEPENDENT SIGNALS, EITHER OF WHICH IS ENOUGH, transcoded     */
        /* from stb's is_rgb test.  The first is explicit: the components are */
        /* named 'R', 'G' and 'B' rather than 1, 2 and 3.  The second is      */
        /* circumstantial and is the one worth explaining -- an Adobe APP14   */
        /* saying "transform 0" means no colour transform was applied, but    */
        /* ONLY when the file is not also a JFIF.  JFIF mandates YCbCr, so a  */
        /* file claiming both is contradicting itself, and stb resolves that  */
        /* in favour of JFIF because that is what other decoders do.          */
        /*                                                                    */
        /* GETTING THIS WRONG IS INVISIBLE TO EVERY GEOMETRY CHECK.  Applying */
        /* the YCbCr transform to RGB data, or failing to apply it to YCbCr   */
        /* data, yields a sharp, correctly sized, entirely plausible picture  */
        /* in the wrong colours -- which only an oracle notices.              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     hdrIn : The frame header, already parsed.                      */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool  : true when the three planes are already R, G and B.     */
        /*--------------------------------------------------------------------*/

        private static bool JPEG_IsRawRgb(JpegHeader hdrIn)
        {
            if (hdrIn.JPEGH_iComponentCount != JPEG_RGB_COMPONENT_COUNT)
            {   /* Only a three-component frame can be raw RGB. */
                return(false);
            }

            if (hdrIn.JPEGH_iRgbComponentIds == JPEG_RGB_COMPONENT_COUNT)
            {   /* All three components are named for their colour. */
                return(true);
            }

            if (hdrIn.JPEGH_iAdobeTransform == JPEG_ADOBE_TRANSFORM_NONE &&
                !hdrIn.JPEGH_bHasJfif)
            {   /* Adobe says no transform was applied and no JFIF contradicts   */
                /* it.                                                           */
                return(true);
            }

            /* Nothing says otherwise, so the ordinary YCbCr reading stands. */
            return(false);
        }

        private static void JPEG_YCbCrToRgbRowAt(byte[] arrY, int iYBase,
                                                 byte[] arrCb, int iCbBase,
                                                 byte[] arrCr, int iCrBase,
                                                 byte[] arrOutIo, int iOutBase,
                                                 int iCount)
        {
            int iYFixed;
            int iCb;
            int iCr;
            int iRed;
            int iGreen;
            int iBlue;
            int i;

            for (i = 0; i < iCount; i++)
            {   /* One pixel per iteration. */

                /* The half added here is the rounding bit for the shift at   */
                /* the end, folded in once rather than three times.           */
                iYFixed = ((int)arrY[iYBase + i] << JPEG_YCBCR_FRACTION_BITS) +
                          (1 << (JPEG_YCBCR_FRACTION_BITS - 1));

                iCb = (int)arrCb[iCbBase + i] - JPEG_CHROMA_CENTRE;
                iCr = (int)arrCr[iCrBase + i] - JPEG_CHROMA_CENTRE;

                iRed   = iYFixed + (iCr * JPEG_YCBCR_C_1_40200);
                iGreen = iYFixed + (iCr * -JPEG_YCBCR_C_0_71414) +
                         ((iCb * -JPEG_YCBCR_C_0_34414) & ~0xFFFF);
                iBlue  = iYFixed + (iCb * JPEG_YCBCR_C_1_77200);

                arrOutIo[iOutBase + (i * 3)]     =
                    JPEG_Clamp(iRed >> JPEG_YCBCR_FRACTION_BITS);
                arrOutIo[iOutBase + (i * 3) + 1] =
                    JPEG_Clamp(iGreen >> JPEG_YCBCR_FRACTION_BITS);
                arrOutIo[iOutBase + (i * 3) + 2] =
                    JPEG_Clamp(iBlue >> JPEG_YCBCR_FRACTION_BITS);
            }
        }

        /*--------------------------------------------------------------------*/
        /* JPEG_CopyRgbRowAt:                                                 */
        /*                                                                    */
        /* Interleaves three already-RGB planes into the output row, applying */
        /* no colour transform at all.                                        */
        /*                                                                    */
        /* THE WHOLE POINT IS THAT IT DOES NOTHING CLEVER.  A raw-RGB JPEG's  */
        /* three planes already hold red, green and blue, so the only work is */
        /* interleaving them and cropping to the image width.  Running the    */
        /* YCbCr transform over them instead is the failure this exists to    */
        /* prevent, and it produces a plausible picture in wrong colours.     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     arrR      : Expanded red plane row.                            */
        /*     iRBase    : Offset of the row's first sample within it.        */
        /*     arrG      : Green, and its offset.                             */
        /*     iGBase    :                                                    */
        /*     arrB      : Blue, and its offset.                              */
        /*     iBBase    :                                                    */
        /*     arrOutIo  : Interleaved destination.                           */
        /*     iOutBase  : Offset of this row within it.                      */
        /*     iWidth    : Pixels to write, the IMAGE's width, which crops.   */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void      : iWidth pixels are written.                         */
        /*--------------------------------------------------------------------*/

        private static void JPEG_CopyRgbRowAt(byte[] arrR, int iRBase,
                                              byte[] arrG, int iGBase,
                                              byte[] arrB, int iBBase,
                                              byte[] arrOutIo, int iOutBase,
                                              int iWidth)
        {
            int i;

            for (i = 0; i < iWidth; i++)
            {   /* One pixel per iteration. */
                arrOutIo[iOutBase + (i * 3)]     = arrR[iRBase + i];
                arrOutIo[iOutBase + (i * 3) + 1] = arrG[iGBase + i];
                arrOutIo[iOutBase + (i * 3) + 2] = arrB[iBBase + i];
            }
        }

        /*--------------------------------------------------------------------*/
        /* JPEG_CmykToRgbRowAt:                                               */
        /*                                                                    */
        /* Converts a row of four-component CMYK samples to RGB.              */
        /*                                                                    */
        /* THE CHANNELS ARE STORED INVERTED, and that is why this looks like  */
        /* a multiply rather than the subtraction the CMYK model describes.   */
        /* Adobe writes 255 for "no ink" and 0 for "full ink", so the stored  */
        /* value is already (255 - ink) -- which IS the light that survives   */
        /* that channel.  Red is therefore the surviving cyan light scaled by */
        /* the surviving black, and so on, which is exactly one Blinn         */
        /* multiply per channel.  Writing the textbook 255-(c+k) form instead */
        /* would double-invert and produce a negative image.                  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     arrC      : Expanded cyan plane row, and its offset.           */
        /*     iCBase    :                                                    */
        /*     arrM      : Magenta, and its offset.                           */
        /*     iMBase    :                                                    */
        /*     arrY      : Yellow, and its offset.                            */
        /*     iYBase    :                                                    */
        /*     arrK      : Black, and its offset.                             */
        /*     iKBase    :                                                    */
        /*     arrOutIo  : Interleaved RGB destination.                       */
        /*     iOutBase  : Offset of this row within it.                      */
        /*     iWidth    : Pixels to write, which crops.                      */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void      : iWidth RGB pixels are written.                     */
        /*--------------------------------------------------------------------*/

        private static void JPEG_CmykToRgbRowAt(byte[] arrC, int iCBase,
                                                byte[] arrM, int iMBase,
                                                byte[] arrY, int iYBase,
                                                byte[] arrK, int iKBase,
                                                byte[] arrOutIo, int iOutBase,
                                                int iWidth)
        {
            int iBlack;
            int i;

            for (i = 0; i < iWidth; i++)
            {   /* One pixel per iteration. */
                iBlack = arrK[iKBase + i];

                arrOutIo[iOutBase + (i * 3)]     =
                    JPEG_Blinn8x8(arrC[iCBase + i], iBlack);
                arrOutIo[iOutBase + (i * 3) + 1] =
                    JPEG_Blinn8x8(arrM[iMBase + i], iBlack);
                arrOutIo[iOutBase + (i * 3) + 2] =
                    JPEG_Blinn8x8(arrY[iYBase + i], iBlack);
            }
        }

        /*--------------------------------------------------------------------*/
        /* JPEG_YcckToRgbRowAt:                                               */
        /*                                                                    */
        /* Converts a row of four-component YCCK samples to RGB.              */
        /*                                                                    */
        /* YCCK IS CMYK WITH ITS FIRST THREE CHANNELS PUT THROUGH THE YCbCr   */
        /* TRANSFORM, so undoing it is two steps in order: run the ordinary   */
        /* YCbCr-to-RGB conversion to recover the CMY channels, then invert   */
        /* each and apply the black exactly as the CMYK path does.  The       */
        /* inversion is what distinguishes it -- the YCbCr step hands back    */
        /* ink amounts where the CMYK path expects surviving light.           */
        /*                                                                    */
        /* IT WRITES THE OUTPUT TWICE ON PURPOSE, transcoding the source's    */
        /* shape rather than fusing the two steps.  The first pass leaves     */
        /* intermediate values in the destination and the second corrects     */
        /* them in place, which keeps the shared YCbCr conversion a single    */
        /* function used by both colour paths instead of a second copy that   */
        /* could drift from the first.                                        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     arrY      : Expanded luma plane row, and its offset.           */
        /*     iYBase    :                                                    */
        /*     arrCb     : Cb, and its offset.                                */
        /*     iCbBase   :                                                    */
        /*     arrCr     : Cr, and its offset.                                */
        /*     iCrBase   :                                                    */
        /*     arrK      : Black, and its offset.                             */
        /*     iKBase    :                                                    */
        /*     arrOutIo  : Interleaved RGB destination.                       */
        /*     iOutBase  : Offset of this row within it.                      */
        /*     iWidth    : Pixels to write, which crops.                      */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void      : iWidth RGB pixels are written.                     */
        /*--------------------------------------------------------------------*/

        private static void JPEG_YcckToRgbRowAt(byte[] arrY, int iYBase,
                                                byte[] arrCb, int iCbBase,
                                                byte[] arrCr, int iCrBase,
                                                byte[] arrK, int iKBase,
                                                byte[] arrOutIo, int iOutBase,
                                                int iWidth)
        {
            int iBlack;
            int i;

            /* Step one: recover the CMY channels through the shared   */
            /* transform, leaving them in the destination.             */
            JPEG_YCbCrToRgbRowAt(arrY, iYBase, arrCb, iCbBase, arrCr, iCrBase,
                                 arrOutIo, iOutBase, iWidth);

            for (i = 0; i < iWidth; i++)
            {   /* Step two: invert each and apply the black. */
                iBlack = arrK[iKBase + i];

                arrOutIo[iOutBase + (i * 3)]     =
                    JPEG_Blinn8x8(255 - arrOutIo[iOutBase + (i * 3)], iBlack);
                arrOutIo[iOutBase + (i * 3) + 1] =
                    JPEG_Blinn8x8(255 - arrOutIo[iOutBase + (i * 3) + 1], iBlack);
                arrOutIo[iOutBase + (i * 3) + 2] =
                    JPEG_Blinn8x8(255 - arrOutIo[iOutBase + (i * 3) + 2], iBlack);
            }
        }

        /*--------------------------------------------------------------------*/
        /* JPEG_BuildComponentPlane:                                          */
        /*                                                                    */
        /* Runs the inverse transform over every block of one component and   */
        /* assembles them into a block-aligned sample plane.                  */
        /*                                                                    */
        /* THE PLANE IS BLOCK-ALIGNED AND THE IMAGE IS NOT, which is the      */
        /* whole reason this is a separate step.  A block is eight samples    */
        /* square whatever the image's size, and an interleaved scan is       */
        /* decoded in whole MCUs, so the plane can overhang the image on two  */
        /* sides.  Building it oversized and cropping later means the         */
        /* transform never has to bounds-check its 64 writes.                 */
        /*                                                                    */
        /* A BLOCK POSITION THAT WAS NEVER DECODED IS LEFT AT ZERO, which     */
        /* reads as black rather than as mid-grey because no level shift has  */
        /* been applied to it.  That case only arises when a scan ended early */
        /* at a missing restart, and every decoder shows something arbitrary  */
        /* there; leaving the allocation's zero is the honest choice and      */
        /* costs no code.                                                     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     arrBlocksIn  : The component's coefficient blocks, raster.     */
        /*     iBlocksX     : Blocks across.                                  */
        /*     iBlocksY     : Blocks down.                                    */
        /*     arrPlaneOut  : Receives iBlocksX*8 by iBlocksY*8 samples.      */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void         : The plane is filled.                            */
        /*--------------------------------------------------------------------*/

        private static void JPEG_BuildComponentPlane(short[][] arrBlocksIn, int iBlocksX,
                                                     int iBlocksY, byte[] arrPlaneOut)
        {
            int iPlaneWidth;
            int x;
            int y;

            iPlaneWidth = iBlocksX * 8;

            for (y = 0; y < iBlocksY; y++)
            {   /* One row of blocks per iteration. */
                for (x = 0; x < iBlocksX; x++)
                {   /* One block per iteration. */
                    if (arrBlocksIn[(y * iBlocksX) + x] == null)
                    {   /* Never decoded; see this function's header. */
                        continue;
                    }

                    JPEG_IdctBlock(arrBlocksIn[(y * iBlocksX) + x], arrPlaneOut,
                                   (y * 8 * iPlaneWidth) + (x * 8), iPlaneWidth);
                }
            }
        }

        /*--------------------------------------------------------------------*/
        /* JPEG_TryDecode:                                                    */
        /*                                                                    */
        /* THE PUBLIC DECODE ENTRY POINT.                                     */
        /* Decodes a JPEG into an ImageBuffer: baseline or progressive,       */
        /* grayscale or colour, 4:4:4, 4:2:2, 4:2:0 and 4:1:1, plus the       */
        /* four-component corners -- CMYK and YCCK behind an Adobe APP14, and */
        /* the raw-RGB case whose component ids are 'R', 'G' and 'B'.         */
        /*                                                                    */
        /* A FILE IT CANNOT HANDLE IS REFUSED with a reason saying so, never  */
        /* decoded approximately -- a half-supported colour transform         */
        /* produces a plausible picture in the wrong colours, which is worse  */
        /* than an honest refusal.  Every refusal it can still produce is a   */
        /* statement about the FILE, not about this library.                  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     arrData  : Caller-owned buffer holding the file.               */
        /*     iOffset  : Index of the first byte of the JPEG.                */
        /*     iLength  : How many bytes the JPEG occupies.                   */
        /*     imgOut   : Receives the decoded image, or null.                */
        /*     sReason  : "" on success, else why the decode was refused.     */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool     : true when an image was produced.                    */
        /*--------------------------------------------------------------------*/

        public static bool JPEG_TryDecode(byte[] arrData, int iOffset, int iLength,
                                          out ImageBuffer imgOut, out string sReason)
        {
            JpegHeader           hdrFrame;
            JpegScanHeader       schScan;
            JpegHuffmanTable[]   arrHuff;
            JpegResamplerState[] arrResamplers;
            JpegResamplerState   rsNew;
            JpegProgressiveState pgState;
            JpegComponent        cmpUse;
            bool                 bRawRgb;
            ImageBuffer          imgNew;
            ByteReader           brIn;
            short[][][]          arrBlocks;
            int[][]              arrQuant;
            byte[][]             arrPlanes;
            byte[][]             arrRows;
            int[]                arrRowOffsets;
            int[]                arrBlocksX;
            int[]                arrBlocksY;
            int                  iSourceIndex;
            int                iTargetIndex;
            int                x;
            int                y;
            int                i;

            imgOut  = null;
            sReason = "";

            if (arrData == null)
            {   /* A null buffer is a CALLER bug, not malformed image data,   */
                /* so it throws rather than returning a reason string. Bad    */
                /* DATA earns a reason; a missing argument is a bug.          */
                throw new ArgumentNullException("arrData");
            }

            if (!ImageDecoderDispatch.IMGDEC_TryValidateWindow(arrData, iOffset, iLength,
                                                               out sReason))
            {   /* An offset or length that does not describe a real window. */
                return(false);
            }

            arrQuant = new int[JPEG_MAX_HUFF_TABLES][];
            arrHuff  = new JpegHuffmanTable[JPEG_MAX_HUFF_TABLES * 2];

            for (i = 0; i < JPEG_MAX_HUFF_TABLES; i++)
            {   /* Every quantisation slot gets storage; whether a DQT ever   */
                /* filled it is a separate question the scan walk asks.       */
                arrQuant[i] = new int[JPEG_BLOCK_SAMPLES];
            }

            brIn = new ByteReader(arrData, iOffset, iLength);

            if (!JPEG_TryWalkToFrameHeader(brIn, arrQuant, arrHuff, out hdrFrame,
                                           out sReason))
            {   /* Not a JPEG, or a frame header that contradicts itself. */
                return(false);
            }

            if (hdrFrame.JPEGH_iComponentCount != 1 &&
                hdrFrame.JPEGH_iComponentCount != JPEG_CMYK_COMPONENT_COUNT &&
                hdrFrame.JPEGH_iComponentCount != 3)
            {   /* One, three and four are the counts that mean something:     */
                /* grayscale, colour, and CMYK or YCCK.  Two components, or    */
                /* more than four, is a file no colour model describes -- so   */
                /* this is a PERMANENT refusal about the file rather than a    */
                /* piece of work not yet done.                                 */
                sReason = "JPEG: this file has " +
                          hdrFrame.JPEGH_iComponentCount.ToString() +
                          " components; only 1 (grayscale), 3 (colour) and 4 " +
                          "(CMYK or YCCK) describe an image";
                return(false);
            }


            if (!JPEG_TryWalkToScan(brIn, hdrFrame, arrQuant, arrHuff, out schScan,
                                    out sReason))
            {   /* No usable scan header. */
                return(false);
            }

            if (hdrFrame.JPEGH_bProgressive)
            {   /* PROGRESSIVE: several scans refine one coefficient buffer,   */
                /* and dequantisation waits until the last of them has been    */
                /* read. What comes back is the same shape the baseline walk   */
                /* produces, so everything downstream of here -- the plane     */
                /* build, the resampler walk, the colour transform and the     */
                /* crop -- is shared between the two and needs no branch.      */
                if (!JPEG_TryDecodeProgressiveFrame(brIn, hdrFrame, schScan, arrQuant,
                                                    arrHuff, out pgState, out sReason))
                {   /* The scans were refused. */
                    return(false);
                }

                arrBlocks  = pgState.JPEGPG_arrCoefficients;
                arrBlocksX = pgState.JPEGPG_arrBlocksX;
                arrBlocksY = pgState.JPEGPG_arrBlocksY;
            }
            else
            {   /* BASELINE: one scan, dequantised as it is decoded. */
                if (!JPEG_TryDecodeScanBlocks(brIn, hdrFrame, schScan, arrQuant, arrHuff,
                                              out arrBlocks, out arrBlocksX,
                                              out arrBlocksY, out sReason))
                {   /* The entropy-coded data was refused. */
                    return(false);
                }
            }

            /* Build one block-aligned sample plane per component. At 4:4:4    */
            /* every component has the same grid, but the code does not rely   */
            /* on that -- each plane is sized from its OWN block counts, and   */
            /* at anything but 4:4:4 they genuinely differ from one another.   */
            arrPlanes = new byte[hdrFrame.JPEGH_iComponentCount][];

            for (i = 0; i < hdrFrame.JPEGH_iComponentCount; i++)
            {   /* One component per iteration. */
                if (arrBlocks[i] == null)
                {   /* A component the scan never carried. A baseline file    */
                    /* codes every component in one scan, so this means the   */
                    /* file is inconsistent rather than merely unusual.       */
                    sReason = "JPEG: component " + i.ToString() +
                              " was never coded by any scan";
                    return(false);
                }

                arrPlanes[i] = new byte[(arrBlocksX[i] * 8) * (arrBlocksY[i] * 8)];

                JPEG_BuildComponentPlane(arrBlocks[i], arrBlocksX[i], arrBlocksY[i],
                                         arrPlanes[i]);
            }

            /* SET UP ONE RESAMPLER WALK PER COMPONENT. Even at 4:4:4, where   */
            /* every expansion factor is one and every resampler is a no-op,   */
            /* the walk runs -- there is no separate unsubsampled path. One    */
            /* code path for every layout is worth more than the handful of    */
            /* instructions a special case would save, because a second path   */
            /* is a second thing to get wrong and the rarely-taken one is      */
            /* always the one that rots.                                       */
            arrResamplers = new JpegResamplerState[hdrFrame.JPEGH_iComponentCount];

            for (i = 0; i < hdrFrame.JPEGH_iComponentCount; i++)
            {   /* One component per iteration. */
                cmpUse = hdrFrame.JPEGH_arrComponents[i];
                rsNew  = new JpegResamplerState();

                rsNew.JPEGRS_iHs = hdrFrame.JPEGH_iMaxHorizontalFactor /
                                   cmpUse.JPEGC_iHorizontalFactor;
                rsNew.JPEGRS_iVs = hdrFrame.JPEGH_iMaxVerticalFactor /
                                   cmpUse.JPEGC_iVerticalFactor;

                /* The component's OWN size, which is the image's size scaled   */
                /* down by the expansion and rounded UP -- a half-resolution    */
                /* plane of an odd-width image still needs the odd sample.      */
                rsNew.JPEGRS_iWidthLores  = (hdrFrame.JPEGH_iWidth +
                                             rsNew.JPEGRS_iHs - 1) / rsNew.JPEGRS_iHs;
                rsNew.JPEGRS_iHeightLores = (hdrFrame.JPEGH_iHeight +
                                             rsNew.JPEGRS_iVs - 1) / rsNew.JPEGRS_iVs;

                rsNew.JPEGRS_iStride = arrBlocksX[i] * 8;
                rsNew.JPEGRS_iKind   = JPEG_ResamplerKindFor(rsNew.JPEGRS_iHs,
                                                             rsNew.JPEGRS_iVs);

                /* The phase starts HALFWAY, not at zero -- see the note on   */
                /* JpegResamplerState for why.                                */
                rsNew.JPEGRS_iYStep = rsNew.JPEGRS_iVs >> 1;
                rsNew.JPEGRS_iYPos  = 0;
                rsNew.JPEGRS_iLine0 = 0;
                rsNew.JPEGRS_iLine1 = 0;

                /* Room for the widest expansion plus slack, because a filter   */
                /* at factor four writes up to three samples past the last      */
                /* whole group.                                                 */
                rsNew.JPEGRS_arrLineBuffer = new byte[hdrFrame.JPEGH_iWidth +
                                                      JPEG_LINE_BUFFER_SLACK];

                arrResamplers[i] = rsNew;
            }

            if (hdrFrame.JPEGH_iComponentCount == 1)
            {   /* GRAYSCALE: one channel out. */
                if (!ImageBuffer.IMG_TryAllocate(hdrFrame.JPEGH_iWidth,
                                                 hdrFrame.JPEGH_iHeight, 1, 8,
                                                 out imgNew, out sReason))
                {   /* A geometry the buffer type refuses. */
                    return(false);
                }
            }
            else
            {   /* COLOUR: three. */
                if (!ImageBuffer.IMG_TryAllocate(hdrFrame.JPEGH_iWidth,
                                                 hdrFrame.JPEGH_iHeight, 3, 8,
                                                 out imgNew, out sReason))
                {   /* A geometry the buffer type refuses. */
                    return(false);
                }
            }

            /* Settled once, before the row loop, because it depends only on    */
            /* the frame header and asking per row would invite the answer to   */
            /* be computed differently in two places.                           */
            bRawRgb = JPEG_IsRawRgb(hdrFrame);

            arrRows       = new byte[hdrFrame.JPEGH_iComponentCount][];
            arrRowOffsets = new int[hdrFrame.JPEGH_iComponentCount];

            for (y = 0; y < hdrFrame.JPEGH_iHeight; y++)
            {   /* One OUTPUT row per iteration. */
                for (i = 0; i < hdrFrame.JPEGH_iComponentCount; i++)
                {   /* Expand each component's contribution to this row. */
                    arrRows[i] = JPEG_ResampleOneRow(arrResamplers[i], arrPlanes[i],
                                                     out arrRowOffsets[i]);
                }

                if (hdrFrame.JPEGH_iComponentCount == 1)
                {   /* Copy the expanded luma row, cropped to the image's   */
                    /* width -- the surplus the block alignment and the     */
                    /* expansion added both stop here.                      */
                    iSourceIndex = arrRowOffsets[0];
                    iTargetIndex = y * hdrFrame.JPEGH_iWidth;

                    for (x = 0; x < hdrFrame.JPEGH_iWidth; x++)
                    {   /* One sample per iteration. */
                        imgNew.IMG_arrSamples8[iTargetIndex + x] =
                            arrRows[0][iSourceIndex + x];
                    }
                }
                else if (hdrFrame.JPEGH_iComponentCount == JPEG_CMYK_COMPONENT_COUNT)
                {   /* FOUR COMPONENTS: CMYK or YCCK, told apart by the Adobe   */
                    /* APP14 transform. Both come out as three-channel RGB.     */
                    if (hdrFrame.JPEGH_iAdobeTransform == JPEG_ADOBE_TRANSFORM_YCCK)
                    {   /* YCCK: undo the YCbCr step, then the ink. */
                        JPEG_YcckToRgbRowAt(arrRows[0], arrRowOffsets[0],
                                            arrRows[1], arrRowOffsets[1],
                                            arrRows[2], arrRowOffsets[2],
                                            arrRows[3], arrRowOffsets[3],
                                            imgNew.IMG_arrSamples8,
                                            y * hdrFrame.JPEGH_iWidth * 3,
                                            hdrFrame.JPEGH_iWidth);
                    }
                    else
                    {   /* CMYK, which is transform 0 and is also what an     */
                        /* absent APP14 is read as: four components with no   */
                        /* transform declared are ink, not colour.            */
                        JPEG_CmykToRgbRowAt(arrRows[0], arrRowOffsets[0],
                                            arrRows[1], arrRowOffsets[1],
                                            arrRows[2], arrRowOffsets[2],
                                            arrRows[3], arrRowOffsets[3],
                                            imgNew.IMG_arrSamples8,
                                            y * hdrFrame.JPEGH_iWidth * 3,
                                            hdrFrame.JPEGH_iWidth);
                    }
                }
                else if (bRawRgb)
                {   /* THREE COMPONENTS THAT ARE ALREADY RGB. Interleave and   */
                    /* crop, and apply no transform whatever.                  */
                    JPEG_CopyRgbRowAt(arrRows[0], arrRowOffsets[0],
                                      arrRows[1], arrRowOffsets[1],
                                      arrRows[2], arrRowOffsets[2],
                                      imgNew.IMG_arrSamples8,
                                      y * hdrFrame.JPEGH_iWidth * 3,
                                      hdrFrame.JPEGH_iWidth);
                }
                else
                {   /* Convert the three expanded rows to RGB, cropping in     */
                    /* the same pass because the conversion is told how many   */
                    /* pixels the IMAGE has rather than how many the planes    */
                    /* hold.                                                   */
                    JPEG_YCbCrToRgbRowAt(arrRows[0], arrRowOffsets[0],
                                         arrRows[1], arrRowOffsets[1],
                                         arrRows[2], arrRowOffsets[2],
                                         imgNew.IMG_arrSamples8,
                                         y * hdrFrame.JPEGH_iWidth * 3,
                                         hdrFrame.JPEGH_iWidth);
                }
            }

            imgOut = imgNew;

            /* An image cropped to the size the frame header declared: one   */
            /* channel for grayscale, three for colour, at any legal         */
            /* subsampling.                                                  */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* JPEG_TryParseDri:                                                  */
        /*                                                                    */
        /* Parses a DRI segment: how many MCUs pass between restart markers.  */
        /*                                                                    */
        /* A restart interval lets a decoder resynchronise after damage and   */
        /* lets an encoder parallelise, at the cost of a two-byte marker      */
        /* every N MCUs.  Zero means the mechanism is off, which is also what */
        /* a file with no DRI at all means.                                   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     brIo     : Reader positioned at the payload.                   */
        /*     iLength  : Payload length in bytes; must be exactly 2.         */
        /*     hdrIo    : Header to record the interval in.                   */
        /*     sReason  : Receives "" or what was malformed.                  */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool     : true when the interval was read.                    */
        /*--------------------------------------------------------------------*/

        private static bool JPEG_TryParseDri(ByteReader brIo, int iLength, JpegHeader hdrIo,
                                             out string sReason)
        {
            int iInterval;

            sReason = "";

            if (iLength != 2)
            {   /* DRI's payload is one 16-bit count and nothing else. */
                sReason = "JPEG: DRI has a " + iLength.ToString() +
                          " byte payload, but the restart interval is exactly 2 bytes";
                return(false);
            }

            if (!brIo.BYTER_TryReadU16BE(out iInterval))
            {   /* The length said it was there. */
                sReason = "JPEG: DRI ended before its interval";
                return(false);
            }

            hdrIo.JPEGH_iRestartInterval = iInterval;

            /* Recorded; the entropy decoder is what acts on it. */
            return(true);
        }

        /*---------------------------------------------------------------------*/
        /* JPEG_TryParseApp:                                                   */
        /*                                                                     */
        /* Handles one application segment: notes the two this library cares   */
        /* about and skips the rest whole.                                     */
        /*                                                                     */
        /* APP0 "JFIF" AND APP14 "Adobe" ARE THE ONLY TWO THAT MATTER, and     */
        /* between them they answer one question: what do the three            */
        /* components MEAN?  A JFIF file is YCbCr by definition.  An Adobe     */
        /* file says so explicitly in its transform byte, and a file with      */
        /* neither, whose components are labelled 'R', 'G' and 'B', is         */
        /* already RGB and must not be colour-converted.  The colour paths     */
        /* act on this; the header reader only records it.                     */
        /*                                                                     */
        /* THE TAG IS CHECKED, NOT ASSUMED.  Plenty of files put something     */
        /* else entirely in APP0, and treating any APP0 as JFIF would mislabel */
        /* their colour space.                                                 */
        /*                                                                     */
        /* Arguments:                                                          */
        /*     brIo     : Reader positioned at the payload.                    */
        /*     iMarker  : Which APPn this is.                                  */
        /*     iLength  : Payload length in bytes.                             */
        /*     hdrIo    : Header to record the findings in.                    */
        /*     sReason  : Receives "" or what was malformed.                   */
        /*                                                                     */
        /* Returns:                                                            */
        /*     bool     : true when the segment was consumed.                  */
        /*---------------------------------------------------------------------*/

        private static bool JPEG_TryParseApp(ByteReader brIo, int iMarker, int iLength,
                                             JpegHeader hdrIo, out string sReason)
        {
            byte[] arrTag;
            int    iLeft;
            int    iValue;

            sReason = "";
            iLeft   = iLength;

            if (iMarker == JPEG_MARKER_APP0 && iLeft >= 5)
            {   /* Possibly JFIF. Five bytes: 'J' 'F' 'I' 'F' and a NUL. */
                if (!brIo.BYTER_TryReadBytes(5, out arrTag))
                {   /* The length said they were there. */
                    sReason = "JPEG: APP0 ended inside its identifier";
                    return(false);
                }

                iLeft = iLeft - 5;

                if (arrTag[0] == (byte)'J' && arrTag[1] == (byte)'F' &&
                    arrTag[2] == (byte)'I' && arrTag[3] == (byte)'F' && arrTag[4] == 0)
                {   /* A real JFIF header, so the colour space is YCbCr. */
                    hdrIo.JPEGH_bHasJfif = true;
                }
            }
            else if (iMarker == JPEG_MARKER_APP14 && iLeft >= 12)
            {   /* Possibly Adobe. Six bytes of tag then version, two flag   */
                /* words, and the transform byte.                            */
                if (!brIo.BYTER_TryReadBytes(6, out arrTag))
                {   /* The length said they were there. */
                    sReason = "JPEG: APP14 ended inside its identifier";
                    return(false);
                }

                iLeft = iLeft - 6;

                if (arrTag[0] == (byte)'A' && arrTag[1] == (byte)'d' &&
                    arrTag[2] == (byte)'o' && arrTag[3] == (byte)'b' &&
                    arrTag[4] == (byte)'e' && arrTag[5] == 0)
                {   /* A real Adobe marker. Step over version and the two   */
                    /* flag words to reach the transform byte.              */
                    if (!brIo.BYTER_TrySkip(5) || !brIo.BYTER_TryReadU8(out iValue))
                    {   /* The length said they were there. */
                        sReason = "JPEG: APP14 ended inside its Adobe payload";
                        return(false);
                    }

                    iLeft = iLeft - 6;
                    hdrIo.JPEGH_iAdobeTransform = iValue;
                }
            }

            /* Whatever the segment still holds is of no interest. */
            return(JPEG_TrySkipSegmentBody(brIo, iLeft, iMarker, out sReason));
        }

        /*--------------------------------------------------------------------*/
        /* JPEG_IsStartOfFrame:                                               */
        /*                                                                    */
        /* Whether a marker opens a frame this library can decode.            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iMarker  : A marker's second byte.                             */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool     : true for SOF0, SOF1 and SOF2.                       */
        /*--------------------------------------------------------------------*/

        public static bool JPEG_IsStartOfFrame(int iMarker)
        {
            /* The three Huffman-coded DCT frames: baseline, extended          */
            /* sequential and progressive. Extended sequential differs from    */
            /* baseline only in permitting 12-bit samples and more tables,     */
            /* and at 8 bits it decodes identically, which is why stb treats   */
            /* the two the same and so does this.                              */
            return(iMarker == JPEG_MARKER_SOF0 || iMarker == JPEG_MARKER_SOF1 ||
                   iMarker == JPEG_MARKER_SOF2);
        }

        /*--------------------------------------------------------------------*/
        /* JPEG_IsUnsupportedFrame:                                           */
        /*                                                                    */
        /* Whether a marker opens a frame this library will NOT decode.       */
        /*                                                                    */
        /* THE RANGE IS THE POINT.  Markers 0xC0 to 0xCF are frame markers    */
        /* except for three carve-outs -- DHT at 0xC4, a reserved value at    */
        /* 0xC8, and DAC at 0xCC -- so anything else in that range that is    */
        /* not one of the three supported frames is a frame using lossless,   */
        /* differential or arithmetic coding.  Testing the range rather than  */
        /* listing nine values means a frame type nobody here has heard of    */
        /* is still refused as a frame rather than skipped as an unknown      */
        /* segment, which is the safe direction to be wrong in.               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iMarker  : A marker's second byte.                             */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool     : true for a frame marker this library refuses.       */
        /*--------------------------------------------------------------------*/

        public static bool JPEG_IsUnsupportedFrame(int iMarker)
        {
            if (iMarker < 0xC0 || iMarker > 0xCF)
            {   /* Outside the frame-marker range entirely. */
                return(false);
            }

            if (JPEG_IsStartOfFrame(iMarker))
            {   /* One of the three this library reads. */
                return(false);
            }

            if (iMarker == JPEG_MARKER_DHT || iMarker == JPEG_MARKER_DAC || iMarker == 0xC8)
            {   /* The three carve-outs in the range. DAC is refused, but by   */
                /* its own branch and with its own reason, because "an         */
                /* unsupported frame" would be a lie about what it is.         */
                return(false);
            }

            /* A lossless, differential or hierarchical frame. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* JPEG_IsRestart:                                                    */
        /*                                                                    */
        /* Whether a marker is one of the eight restart markers.              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iMarker  : A marker's second byte.                             */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool     : true for RST0 through RST7.                         */
        /*--------------------------------------------------------------------*/

        public static bool JPEG_IsRestart(int iMarker)
        {
            /* Eight consecutive values, and the low three bits are the   */
            /* restart's sequence number modulo eight.                    */
            return(iMarker >= JPEG_MARKER_RST0 && iMarker <= JPEG_MARKER_RST7);
        }

        /*--------------------------------------------------------------------*/
        /* JPEG_MarkerToText:                                                 */
        /*                                                                    */
        /* A marker's conventional name, for a reason string.                 */
        /*                                                                    */
        /* EVERY REFUSAL IN THIS FILE GOES THROUGH HERE, because "marker      */
        /* 0xC3 is not supported" sends the reader to a table and "SOF3       */
        /* (lossless) frames are not supported" does not.  The hex is kept    */
        /* alongside the name for the values that have no name.               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iMarker  : A marker's second byte, or JPEG_MARKER_NONE.        */
        /*                                                                    */
        /* Returns:                                                           */
        /*     string   : A short name, always non-empty.                     */
        /*--------------------------------------------------------------------*/

        public static string JPEG_MarkerToText(int iMarker)
        {
            if (iMarker == JPEG_MARKER_NONE)
            {   /* Not a marker at all. */
                return("no marker");
            }

            if (iMarker == JPEG_MARKER_SOI)   { return("SOI"); }
            if (iMarker == JPEG_MARKER_EOI)   { return("EOI"); }
            if (iMarker == JPEG_MARKER_SOS)   { return("SOS"); }
            if (iMarker == JPEG_MARKER_DQT)   { return("DQT"); }
            if (iMarker == JPEG_MARKER_DHT)   { return("DHT"); }
            if (iMarker == JPEG_MARKER_DRI)   { return("DRI"); }
            if (iMarker == JPEG_MARKER_DNL)   { return("DNL"); }
            if (iMarker == JPEG_MARKER_DAC)   { return("DAC (arithmetic coding)"); }
            if (iMarker == JPEG_MARKER_COM)   { return("COM"); }
            if (iMarker == JPEG_MARKER_TEM)   { return("TEM"); }
            if (iMarker == JPEG_MARKER_SOF0)  { return("SOF0 (baseline)"); }
            if (iMarker == JPEG_MARKER_SOF1)  { return("SOF1 (extended sequential)"); }
            if (iMarker == JPEG_MARKER_SOF2)  { return("SOF2 (progressive)"); }

            if (JPEG_IsRestart(iMarker))
            {   /* One of eight; the low bits give which. */
                return("RST" + (iMarker - JPEG_MARKER_RST0).ToString());
            }

            if (iMarker >= JPEG_MARKER_APP0 && iMarker <= JPEG_MARKER_APP15)
            {   /* One of sixteen. */
                return("APP" + (iMarker - JPEG_MARKER_APP0).ToString());
            }

            if (iMarker == 0xC3)  { return("SOF3 (lossless)"); }
            if (iMarker == 0xC5)  { return("SOF5 (differential sequential)"); }
            if (iMarker == 0xC6)  { return("SOF6 (differential progressive)"); }
            if (iMarker == 0xC7)  { return("SOF7 (differential lossless)"); }
            if (iMarker == 0xC9)  { return("SOF9 (arithmetic sequential)"); }
            if (iMarker == 0xCA)  { return("SOF10 (arithmetic progressive)"); }
            if (iMarker == 0xCB)  { return("SOF11 (arithmetic lossless)"); }
            if (iMarker == 0xCD)  { return("SOF13 (differential arithmetic sequential)"); }
            if (iMarker == 0xCE)  { return("SOF14 (differential arithmetic progressive)"); }
            if (iMarker == 0xCF)  { return("SOF15 (differential arithmetic lossless)"); }

            /* Anything else: the number is all there is to say. */
            return("marker 0x" + iMarker.ToString("X2"));
        }

        /*--------------------------------------------------------------------*/
        /* THE ENCODER, FROM HERE DOWN.                                       */
        /*                                                                    */
        /* LINEAGE IS DIFFERENT ON THIS HALF OF THE FILE and it is stated     */
        /* here rather than only in the file header.  Everything above is     */
        /* transcoded from stb_image.h; everything below is adapted from a    */
        /* 1034-line baseline JPEG encoder written from ITU-T T.81 by this    */
        /* library's own author -- Annexes A, B, C, F and K, one component    */
        /* and no subsampling -- and extended here with colour, chroma        */
        /* subsampling and this library's naming.  Both halves are therefore  */
        /* covered by this file's own licence.                                */
        /*                                                                    */
        /* WHAT WAS RECONCILED RATHER THAN DUPLICATED.  The source carries    */
        /* its own zig-zag table and its own canonical-Huffman construction,  */
        /* and the decoder above already has both, proven against             */
        /* independently derived expectations.  A second copy                 */
        /* of either would be a second thing to keep right, so the encoder    */
        /* uses JPEG_arrDezigzag -- which maps a zig-zag POSITION to a        */
        /* natural INDEX, exactly what the encoder's scatter needs, read in   */
        /* the same direction.  The Annex K quantisation and Huffman          */
        /* specification tables ARE new here, because the decoder never       */
        /* needs them: a decoder reads whatever tables the file carries.      */
        /*--------------------------------------------------------------------*/

        public const int JPEG_QUALITY_MIN     = 1;       // heaviest quantisation the scaler will produce
        public const int JPEG_QUALITY_MAX     = 100;     // no quantisation beyond rounding
        public const int JPEG_QUALITY_DEFAULT = 90;      // what JPEG_DefaultEncodeOptions returns

        public const int JPEG_SUBSAMPLE_444 = 0;         // no chroma subsampling; chroma planes are full size
        public const int JPEG_SUBSAMPLE_422 = 1;         // chroma halved horizontally
        public const int JPEG_SUBSAMPLE_420 = 2;         // chroma halved both ways

        private const int JPEG_QUALITY_PIVOT     = 50;   // the quality the Annex K table is stated at
        private const int JPEG_QUANT_ENTRY_MAX   = 255;  // an 8-bit DQT entry cannot exceed this
        private const int JPEG_DC_CATEGORY_MAX   = 11;   // Annex K's DC table defines categories 0 to 11
        private const int JPEG_AC_SYMBOL_EOB     = 0x00; // end of block: every remaining coefficient is zero
        private const int JPEG_AC_SYMBOL_ZRL     = 0xF0; // sixteen zeroes, named as one symbol
        private const int JPEG_MAX_ZERO_RUN      = 16;   // the run a ZRL stands for

        /*--------------------------------------------------------------------*/
        /* Annex K.1: the luminance quantisation table, NATURAL (row-major)   */
        /* order, stated at quality 50.  JPEG_ScaleQuantTable derives every   */
        /* other quality from it.                                             */
        /*                                                                    */
        /* NATURAL ORDER, NOT ZIG-ZAG, and the distinction matters twice: the */
        /* quantise step indexes it by natural position, and the DQT segment  */
        /* must WRITE it in zig-zag order.  Storing it one way and converting */
        /* at the one place that needs the other is what stops the two uses   */
        /* disagreeing.                                                       */
        /*--------------------------------------------------------------------*/

        private static readonly int[] JPEG_arrQuantLumaBase = new int[]
        {
            16, 11, 10, 16,  24,  40,  51,  61,           // low horizontal frequencies, quantised finely
            12, 12, 14, 19,  26,  58,  60,  55,
            14, 13, 16, 24,  40,  57,  69,  56,
            14, 17, 22, 29,  51,  87,  80,  62,
            18, 22, 37, 56,  68, 109, 103,  77,
            24, 35, 55, 64,  81, 104, 113,  92,
            49, 64, 78, 87, 103, 121, 120, 101,
            72, 92, 95, 98, 112, 100, 103,  99            // high frequencies, quantised coarsely
        };

        /*--------------------------------------------------------------------*/
        /* Annex K.3 and K.5: the specification's luminance Huffman tables.   */
        /* BITS[i] is how many codes have length i+1; VALS lists the symbols  */
        /* in increasing code order.  These two arrays ARE the DHT segment's  */
        /* payload, so they are written to the file verbatim as well as being */
        /* expanded into per-symbol codes.                                    */
        /*--------------------------------------------------------------------*/

        private static readonly byte[] JPEG_arrDcLumaBits = new byte[]
        {
            0, 1, 5, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0
        };

        private static readonly byte[] JPEG_arrDcLumaVals = new byte[]
        {
            0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11          // DC categories, in code order
        };

        private static readonly byte[] JPEG_arrAcLumaBits = new byte[]
        {
            0, 2, 1, 3, 3, 2, 4, 3, 5, 5, 4, 4, 0, 0, 1, 0x7D
        };

        private static readonly byte[] JPEG_arrAcLumaVals = new byte[]
        {
            0x01, 0x02, 0x03, 0x00, 0x04, 0x11, 0x05, 0x12,
            0x21, 0x31, 0x41, 0x06, 0x13, 0x51, 0x61, 0x07,
            0x22, 0x71, 0x14, 0x32, 0x81, 0x91, 0xA1, 0x08,
            0x23, 0x42, 0xB1, 0xC1, 0x15, 0x52, 0xD1, 0xF0,
            0x24, 0x33, 0x62, 0x72, 0x82, 0x09, 0x0A, 0x16,
            0x17, 0x18, 0x19, 0x1A, 0x25, 0x26, 0x27, 0x28,
            0x29, 0x2A, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39,
            0x3A, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49,
            0x4A, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59,
            0x5A, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69,
            0x6A, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79,
            0x7A, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89,
            0x8A, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98,
            0x99, 0x9A, 0xA2, 0xA3, 0xA4, 0xA5, 0xA6, 0xA7,
            0xA8, 0xA9, 0xAA, 0xB2, 0xB3, 0xB4, 0xB5, 0xB6,
            0xB7, 0xB8, 0xB9, 0xBA, 0xC2, 0xC3, 0xC4, 0xC5,
            0xC6, 0xC7, 0xC8, 0xC9, 0xCA, 0xD2, 0xD3, 0xD4,
            0xD5, 0xD6, 0xD7, 0xD8, 0xD9, 0xDA, 0xE1, 0xE2,
            0xE3, 0xE4, 0xE5, 0xE6, 0xE7, 0xE8, 0xE9, 0xEA,
            0xF1, 0xF2, 0xF3, 0xF4, 0xF5, 0xF6, 0xF7, 0xF8,
            0xF9, 0xFA
        };

        /*--------------------------------------------------------------------*/
        /* Annex K.2, K.4 and K.6: the CHROMINANCE tables.                    */
        /*                                                                    */
        /* THESE WERE READ FROM stb_image_write.h RATHER THAN RECALLED, and   */
        /* the luminance ones above were cross-checked against the same copy  */
        /* at the same time -- they agree byte for byte with the set this     */
        /* file already carried, which independently confirms both.  Note     */
        /* stb's BITS arrays carry a LEADING DUMMY BYTE because it indexes    */
        /* them from one; it writes them to the file as `nrcodes + 1`.  The   */
        /* arrays here are the sixteen real counts, so the dummy is dropped.  */
        /* Copying stb's arrays verbatim would put a spurious zero at the     */
        /* front and shift every code length by one -- a file that is         */
        /* structurally valid and decodes to noise.                           */
        /*                                                                    */
        /* THE CHROMA QUANTISATION TABLE IS MUCH COARSER THAN LUMA'S and      */
        /* that is the point of it rather than an accident: the eye resolves  */
        /* brightness far better than colour, so the high-frequency entries   */
        /* sit at 99 where luma's reach only 121, and the whole lower-right   */
        /* quadrant is flat.  A file that used the luma table for chroma      */
        /* would be correct, larger, and no better to look at.                */
        /*--------------------------------------------------------------------*/

        private static readonly int[] JPEG_arrQuantChromaBase = new int[]
        {
            17, 18, 24, 47, 99, 99, 99, 99,
            18, 21, 26, 66, 99, 99, 99, 99,
            24, 26, 56, 99, 99, 99, 99, 99,
            47, 66, 99, 99, 99, 99, 99, 99,
            99, 99, 99, 99, 99, 99, 99, 99,
            99, 99, 99, 99, 99, 99, 99, 99,
            99, 99, 99, 99, 99, 99, 99, 99,
            99, 99, 99, 99, 99, 99, 99, 99
        };

        private static readonly byte[] JPEG_arrDcChromaBits = new byte[]
        {
            0, 3, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0
        };

        private static readonly byte[] JPEG_arrDcChromaVals = new byte[]
        {
            0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11
        };

        private static readonly byte[] JPEG_arrAcChromaBits = new byte[]
        {
            0, 2, 1, 2, 4, 4, 3, 4, 7, 5, 4, 4, 0, 1, 2, 0x77
        };

        private static readonly byte[] JPEG_arrAcChromaVals = new byte[]
        {
            0x00, 0x01, 0x02, 0x03, 0x11, 0x04, 0x05, 0x21,
            0x31, 0x06, 0x12, 0x41, 0x51, 0x07, 0x61, 0x71,
            0x13, 0x22, 0x32, 0x81, 0x08, 0x14, 0x42, 0x91,
            0xA1, 0xB1, 0xC1, 0x09, 0x23, 0x33, 0x52, 0xF0,
            0x15, 0x62, 0x72, 0xD1, 0x0A, 0x16, 0x24, 0x34,
            0xE1, 0x25, 0xF1, 0x17, 0x18, 0x19, 0x1A, 0x26,
            0x27, 0x28, 0x29, 0x2A, 0x35, 0x36, 0x37, 0x38,
            0x39, 0x3A, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48,
            0x49, 0x4A, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58,
            0x59, 0x5A, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68,
            0x69, 0x6A, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78,
            0x79, 0x7A, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87,
            0x88, 0x89, 0x8A, 0x92, 0x93, 0x94, 0x95, 0x96,
            0x97, 0x98, 0x99, 0x9A, 0xA2, 0xA3, 0xA4, 0xA5,
            0xA6, 0xA7, 0xA8, 0xA9, 0xAA, 0xB2, 0xB3, 0xB4,
            0xB5, 0xB6, 0xB7, 0xB8, 0xB9, 0xBA, 0xC2, 0xC3,
            0xC4, 0xC5, 0xC6, 0xC7, 0xC8, 0xC9, 0xCA, 0xD2,
            0xD3, 0xD4, 0xD5, 0xD6, 0xD7, 0xD8, 0xD9, 0xDA,
            0xE2, 0xE3, 0xE4, 0xE5, 0xE6, 0xE7, 0xE8, 0xE9,
            0xEA, 0xF2, 0xF3, 0xF4, 0xF5, 0xF6, 0xF7, 0xF8,
            0xF9, 0xFA
        };

        /*--------------------------------------------------------------------*/
        /* THE FORWARD COLOUR TRANSFORM'S COEFFICIENTS.                       */
        /*                                                                    */
        /* WRITTEN AS LITERALS, unlike the IDCT's twelve constants and the    */
        /* forward transform's cosine factors, and the difference is worth    */
        /* stating because this file computes those on purpose.  Those are    */
        /* DERIVED -- a real number scaled by a power of two and truncated -- */
        /* so a mistyped digit is invisible and computing them removes the    */
        /* chance of one.  These six are not derived from anything: they ARE  */
        /* the definition, the JFIF specification's own numbers, and there is */
        /* nothing to compute them from.  Reading them out of a source is the */
        /* strongest check available, so they were taken from                 */
        /* stb_image_write.h's forward transform rather than from memory.     */
        /*                                                                    */
        /* THE DECODER'S INVERSE IS NOT THEIR EXACT MATHEMATICAL INVERSE and  */
        /* that is expected: it works in 20-bit fixed point and deliberately  */
        /* truncates the green Cb term for backward compatibility (section    */
        /* 11.22).  So a round trip through both loses a little, which is     */
        /* what the measured encode tolerance covers.                         */
        /*--------------------------------------------------------------------*/

        private const double JPEG_RGB_TO_Y_RED    =  0.29900;
        private const double JPEG_RGB_TO_Y_GREEN  =  0.58700;
        private const double JPEG_RGB_TO_Y_BLUE   =  0.11400;
        private const double JPEG_RGB_TO_CB_RED   = -0.16874;
        private const double JPEG_RGB_TO_CB_GREEN = -0.33126;
        private const double JPEG_RGB_TO_CB_BLUE  =  0.50000;
        private const double JPEG_RGB_TO_CR_RED   =  0.50000;
        private const double JPEG_RGB_TO_CR_GREEN = -0.41869;
        private const double JPEG_RGB_TO_CR_BLUE  = -0.08131;

        /*--------------------------------------------------------------------*/
        /* Per-symbol code and length, expanded from the tables above by      */
        /* JPEG_BuildEncoderCodes at static-init time, and the cosine factors */
        /* the forward transform uses.  Index is the SYMBOL; a length of zero */
        /* means the table defines no code for it, which the encoder treats   */
        /* as a fault rather than emitting nothing.                           */
        /*--------------------------------------------------------------------*/

        private static readonly JpegEncoderTables JPEG_tblEncodeLuma;
        private static readonly JpegEncoderTables JPEG_tblEncodeChroma;
        private static readonly double[]          JPEG_arrForwardCos;

        /*--------------------------------------------------------------------*/
        /* JpegCodec (static constructor):                                    */
        /*                                                                    */
        /* Expands the Annex K specification tables into per-symbol codes and */
        /* precomputes the forward transform's cosine factors.                */
        /*                                                                    */
        /* THE COSINE FACTORS ARE COMPUTED, NOT PASTED, for the same reason   */
        /* the inverse transform's twelve constants are: a mistyped digit     */
        /* produces an image that is very slightly wrong everywhere, which is */
        /* the hardest kind of error to see.  Here the argument is even       */
        /* stronger, because there would be sixty-four of them.               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : the static tables are filled.                           */
        /*--------------------------------------------------------------------*/

        static JpegCodec()
        {
            double dblScale;
            int    u;
            int    x;

            JPEG_tblEncodeLuma   = JPEG_BuildEncoderTables(JPEG_arrDcLumaBits,
                                                           JPEG_arrDcLumaVals,
                                                           JPEG_arrAcLumaBits,
                                                           JPEG_arrAcLumaVals);
            JPEG_tblEncodeChroma = JPEG_BuildEncoderTables(JPEG_arrDcChromaBits,
                                                           JPEG_arrDcChromaVals,
                                                           JPEG_arrAcChromaBits,
                                                           JPEG_arrAcChromaVals);

            JPEG_arrForwardCos = new double[JPEG_BLOCK_SAMPLES];

            for (u = 0; u < 8; u++)
            {   /* One output frequency per iteration. */
                if (u == 0)
                {   /* C(0) is 1 over root two. */
                    dblScale = 1.0 / Math.Sqrt(2.0);
                }
                else
                {   /* C(u) is 1 for every other frequency. */
                    dblScale = 1.0;
                }

                for (x = 0; x < 8; x++)
                {   /* One input sample position per iteration.             */
                    /* Half of C(u) is folded in here so that the two       */
                    /* separable passes multiply together to the 1/4 that   */
                    /* Annex A.3.3 puts in front of the whole transform.    */
                    JPEG_arrForwardCos[(u * 8) + x] =
                        (dblScale / 2.0) *
                        Math.Cos(((2.0 * x) + 1.0) * u * Math.PI / 16.0);
                }
            }
        }

        /*--------------------------------------------------------------------*/
        /* JPEG_BuildEncoderTables:                                           */
        /*                                                                    */
        /* Annex C.2: turns a matched DC and AC BITS / VALS specification     */
        /* pair into the code and length tables one component class encodes   */
        /* with.                                                              */
        /*                                                                    */
        /* THIS IS THE SAME CANONICAL RULE THE DECODER RECONSTRUCTS, read in  */
        /* the opposite direction.  Codes of one length are consecutive, and  */
        /* the running code shifts left by one each time the length grows.    */
        /* The decoder's version builds maxcode and delta tables for looking  */
        /* a code UP; this one builds a code per symbol for writing one OUT,  */
        /* so they cannot share an implementation -- but they must agree, and */
        /* the encode scenarios check exactly that by round-tripping through  */
        /* both.                                                              */
        /*                                                                    */
        /* THE PAIR IS BUILT TOGETHER so a caller cannot end up holding a     */
        /* luma DC table beside a chroma AC one; see JpegEncoderTables.       */
        /*                                                                    */
        /* Not a Try function: the Annex K tables are compile-time constants  */
        /* and are known well-formed, so there is no input left to reject.    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     arrDcBits : sixteen DC counts; index 0 is codes of length 1.   */
        /*     arrDcVals : the DC symbols, in increasing code order.          */
        /*     arrAcBits : the same for AC.                                   */
        /*     arrAcVals :                                                    */
        /*                                                                    */
        /* Returns:                                                           */
        /*     JpegEncoderTables : the matched pair, ready to encode with.    */
        /*--------------------------------------------------------------------*/

        private static JpegEncoderTables JPEG_BuildEncoderTables(byte[] arrDcBits,
                                                                 byte[] arrDcVals,
                                                                 byte[] arrAcBits,
                                                                 byte[] arrAcVals)
        {
            JpegEncoderTables tblNew;

            tblNew = new JpegEncoderTables();

            tblNew.JPEGET_arrDcCode = new int[JPEG_MAX_SYMBOLS];
            tblNew.JPEGET_arrDcSize = new int[JPEG_MAX_SYMBOLS];
            tblNew.JPEGET_arrAcCode = new int[JPEG_MAX_SYMBOLS];
            tblNew.JPEGET_arrAcSize = new int[JPEG_MAX_SYMBOLS];

            JPEG_BuildEncoderCodes(arrDcBits, arrDcVals, tblNew.JPEGET_arrDcCode,
                                   tblNew.JPEGET_arrDcSize);
            JPEG_BuildEncoderCodes(arrAcBits, arrAcVals, tblNew.JPEGET_arrAcCode,
                                   tblNew.JPEGET_arrAcSize);

            /* A matched DC and AC pair for one component class. */
            return(tblNew);
        }

        /*--------------------------------------------------------------------*/
        /* JPEG_BuildEncoderCodes:                                            */
        /*                                                                    */
        /* Fills one code and length table from a BITS / VALS specification.  */
        /* See JPEG_BuildEncoderTables for the canonical rule and why this is */
        /* not shared with the decoder's reconstruction.                      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     arrBits    : sixteen counts; index 0 is codes of length 1.     */
        /*     arrVals    : the symbols, in increasing code order.            */
        /*     arrCodeOut : receives the code bits, indexed by symbol.        */
        /*     arrSizeOut : receives the code length, indexed by symbol.      */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void       : both tables are filled.                           */
        /*--------------------------------------------------------------------*/

        private static void JPEG_BuildEncoderCodes(byte[] arrBits, byte[] arrVals,
                                                   int[] arrCodeOut, int[] arrSizeOut)
        {
            int iCode;
            int iLength;
            int iTaken;
            int iSymbol;
            int i;

            iCode  = 0;
            iTaken = 0;

            for (iLength = 1; iLength <= JPEG_HUFFMAN_LENGTHS; iLength++)
            {   /* One code length per iteration. */
                for (i = 0; i < (int)arrBits[iLength - 1]; i++)
                {   /* One symbol of this length per iteration. */
                    iSymbol = (int)arrVals[iTaken];

                    arrCodeOut[iSymbol] = iCode;
                    arrSizeOut[iSymbol] = iLength;

                    iCode  = iCode + 1;
                    iTaken = iTaken + 1;
                }

                /* Growing the length shifts the running code left, which is   */
                /* what makes the assignment prefix-free.                      */
                iCode = iCode << 1;
            }
        }

        /*--------------------------------------------------------------------*/
        /* JPEG_ScaleQuantTable:                                              */
        /*                                                                    */
        /* Derives a quantisation table for a given quality from the Annex K  */
        /* table, which is stated at quality 50.                              */
        /*                                                                    */
        /* THE TWO-BRANCH SCALE IS THE INDUSTRY ONE, not an invention, and it */
        /* is worth knowing that it is not linear.  Below 50 the divisor      */
        /* grows as 5000/q, so quality 1 multiplies the table by fifty; above */
        /* 50 it shrinks as 200 - 2q, reaching zero at 100 and so clamping    */
        /* every entry to one -- which is as close to lossless as a DCT-based */
        /* format gets, since only the rounding remains.                      */
        /*                                                                    */
        /* EVERY ENTRY IS CLAMPED TO 1..255.  A zero would be a divide by     */
        /* zero at quantise time; above 255 will not fit the 8-bit DQT the    */
        /* baseline format defines, and writing a truncated value there would */
        /* make the decoder dequantise by a different number than the encoder */
        /* quantised by -- silently, and only on the coarsest tables.         */
        /*                                                                    */
        /* Not a Try function: the quality is clamped rather than rejected,   */
        /* because a caller asking for 0 or 200 has expressed an intent the   */
        /* endpoints serve perfectly well.                                    */
        /*                                                                    */
        /* IT TAKES THE BASE TABLE AS AN ARGUMENT rather than reading the     */
        /* luma one directly, because luma and chroma are scaled by the SAME  */
        /* rule from DIFFERENT tables.  Two copies of this arithmetic would   */
        /* be two places for the 5000/q branch to drift.                      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     arrBaseIn : the Annex K table to scale, NATURAL order.         */
        /*     iQuality  : 1 to 100; anything outside is clamped.             */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int[]     : 64 entries in NATURAL order, every one 1 to 255.   */
        /*--------------------------------------------------------------------*/

        private static int[] JPEG_ScaleQuantTable(int[] arrBaseIn, int iQuality)
        {
            int[] arrTable;
            int   iScale;
            int   iValue;
            int   i;

            if (iQuality < JPEG_QUALITY_MIN)
            {   /* Below the range: the heaviest quantisation on offer. */
                iQuality = JPEG_QUALITY_MIN;
            }

            if (iQuality > JPEG_QUALITY_MAX)
            {   /* Above it: the lightest. */
                iQuality = JPEG_QUALITY_MAX;
            }

            if (iQuality < JPEG_QUALITY_PIVOT)
            {   /* Coarser than the base table. */
                iScale = 5000 / iQuality;
            }
            else
            {   /* Finer than it; 100 gives a scale of zero, so every entry   */
                /* clamps to one.                                             */
                iScale = 200 - (2 * iQuality);
            }

            arrTable = new int[JPEG_BLOCK_SAMPLES];

            for (i = 0; i < JPEG_BLOCK_SAMPLES; i++)
            {   /* One entry per iteration.                             */
                /* The + 50 rounds to nearest rather than truncating.   */
                iValue = ((arrBaseIn[i] * iScale) + 50) / 100;

                if (iValue < 1)
                {   /* A divisor of zero is meaningless. */
                    iValue = 1;
                }

                if (iValue > JPEG_QUANT_ENTRY_MAX)
                {   /* An 8-bit DQT entry cannot hold more. */
                    iValue = JPEG_QUANT_ENTRY_MAX;
                }

                arrTable[i] = iValue;
            }

            /* A table the DQT segment can carry and the quantiser can use. */
            return(arrTable);
        }

        /*---------------------------------------------------------------------*/
        /* JPEG_LoadBlockForEncode:                                            */
        /*                                                                     */
        /* Copies one 8x8 block out of a sample plane, level-shifted, with     */
        /* the edges replicated where the block hangs off the image.           */
        /*                                                                     */
        /* THE EDGE IS REPLICATED, NOT ZERO-FILLED, and the difference is      */
        /* visible in the output.  An image whose width is not a multiple of   */
        /* eight still has to be coded in whole blocks, and the samples past   */
        /* the edge are the encoder's free choice because the decoder crops    */
        /* them away.  Filling with zero would put a hard black edge inside    */
        /* the last block, and the DCT would spend real coefficients coding    */
        /* that edge -- making the file bigger AND ringing the visible pixels  */
        /* next to it.  Repeating the last row and column costs nothing and    */
        /* leaves the block flat where the picture ends.                       */
        /*                                                                     */
        /* THE LEVEL SHIFT IS PART OF LOADING, matching the source and         */
        /* mirroring the decoder, which folds the inverse shift into its own   */
        /* final pass.  Samples are centred on zero for the transform.         */
        /*                                                                     */
        /* Arguments:                                                          */
        /*     arrPlane  : the sample plane, row-major, one byte per sample.   */
        /*     iWidth    : the plane's width in samples.                       */
        /*     iHeight   : its height.                                         */
        /*     iBlockX   : block column, so samples start at iBlockX * 8.      */
        /*     iBlockY   : block row.                                          */
        /*     arrBlockOut : receives 64 level-shifted samples, natural order. */
        /*                                                                     */
        /* Returns:                                                            */
        /*     void      : the block is filled.                                */
        /*---------------------------------------------------------------------*/

        private static void JPEG_LoadBlockForEncode(byte[] arrPlane, int iWidth,
                                                    int iHeight, int iBlockX,
                                                    int iBlockY, double[] arrBlockOut)
        {
            int iSourceX;
            int iSourceY;
            int x;
            int y;

            for (y = 0; y < 8; y++)
            {   /* One block row per iteration. */
                iSourceY = (iBlockY * 8) + y;

                if (iSourceY >= iHeight)
                {   /* Below the picture: repeat its last row. */
                    iSourceY = iHeight - 1;
                }

                for (x = 0; x < 8; x++)
                {   /* One sample per iteration. */
                    iSourceX = (iBlockX * 8) + x;

                    if (iSourceX >= iWidth)
                    {   /* Right of the picture: repeat its last column. */
                        iSourceX = iWidth - 1;
                    }

                    arrBlockOut[(y * 8) + x] =
                        (double)arrPlane[(iSourceY * iWidth) + iSourceX] -
                        (double)JPEG_LEVEL_SHIFT;
                }
            }
        }

        /*--------------------------------------------------------------------*/
        /* JPEG_ForwardDct:                                                   */
        /*                                                                    */
        /* The two-dimensional forward DCT of Annex A.3.3, done separably:    */
        /* rows first, then columns, each an 8x8 product against the          */
        /* precomputed cosine table.                                          */
        /*                                                                    */
        /* THIS IS THE NAIVE FORM AND THAT IS A DELIBERATE, RECORDED CHOICE.  */
        /* It costs 1024 multiply-accumulates per block against roughly 80    */
        /* for a factorised transform such as the AAN one, so a production    */
        /* encoder would not use it.  It is kept because it is already        */
        /* correct and it reads straight against the                          */
        /* specification's formula -- and because this library's first        */
        /* consumer encodes a thumbnail occasionally rather than video.       */
        /* Section 5.3.0 records the trade and flags the faster transcription */
        /* from stb_image_write.h as available if the cost ever bites.        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     arrBlockIo : 64 level-shifted samples in, 64 coefficients out, */
        /*                  natural order, transformed IN PLACE.              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void       : the block is transformed.                         */
        /*--------------------------------------------------------------------*/

        private static void JPEG_ForwardDct(double[] arrBlockIo)
        {
            double[] arrTemp;
            double   dblSum;
            int      u;
            int      v;
            int      x;
            int      y;

            arrTemp = new double[JPEG_BLOCK_SAMPLES];

            for (y = 0; y < 8; y++)
            {   /* Pass one: transform each ROW into arrTemp. */
                for (u = 0; u < 8; u++)
                {   /* One output frequency per iteration. */
                    dblSum = 0.0;

                    for (x = 0; x < 8; x++)
                    {   /* One input sample per iteration. */
                        dblSum = dblSum + (arrBlockIo[(y * 8) + x] *
                                           JPEG_arrForwardCos[(u * 8) + x]);
                    }

                    arrTemp[(y * 8) + u] = dblSum;
                }
            }

            for (u = 0; u < 8; u++)
            {   /* Pass two: transform each COLUMN of the intermediate. */
                for (v = 0; v < 8; v++)
                {   /* One output frequency per iteration. */
                    dblSum = 0.0;

                    for (y = 0; y < 8; y++)
                    {   /* One intermediate row per iteration. */
                        dblSum = dblSum + (arrTemp[(y * 8) + u] *
                                           JPEG_arrForwardCos[(v * 8) + y]);
                    }

                    arrBlockIo[(v * 8) + u] = dblSum;
                }
            }
        }

        /*--------------------------------------------------------------------*/
        /* JPEG_QuantizeToZigzag:                                             */
        /*                                                                    */
        /* Divides each coefficient by its quantisation entry, rounds to      */
        /* nearest, and writes the result out in ZIG-ZAG order.               */
        /*                                                                    */
        /* THE TWO STEPS ARE FUSED ON PURPOSE, which is a small departure     */
        /* from the source's separate Quantize and ToZigzag.  Both walk all   */
        /* 64 positions and the intermediate natural-order array is used by   */
        /* nothing else, so fusing them removes a buffer and, more usefully,  */
        /* removes the chance of quantising in one order and reordering in    */
        /* the other -- which produces a valid file of a scrambled picture.   */
        /*                                                                    */
        /* THE ROUNDING IS AWAY FROM ZERO AT THE HALFWAY POINT, matching the  */
        /* source and libjpeg closely enough.  Which way a midpoint goes      */
        /* shifts individual coefficients by one and changes no structure,    */
        /* but it must be STATED, because a decoder comparison that expected  */
        /* banker's rounding would see a scatter of one-count differences and */
        /* read them as a transform fault.                                    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     arrBlockIn  : 64 coefficients, natural order.                  */
        /*     arrQuantIn  : 64 divisors, natural order.                      */
        /*     arrZigzagOut: receives 64 quantised coefficients, zig-zag.     */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void        : the output is filled.                            */
        /*--------------------------------------------------------------------*/

        private static void JPEG_QuantizeToZigzag(double[] arrBlockIn, int[] arrQuantIn,
                                                  int[] arrZigzagOut)
        {
            int iNatural;
            int i;

            for (i = 0; i < JPEG_BLOCK_SAMPLES; i++)
            {   /* One zig-zag position per iteration.                          */
                /* JPEG_arrDezigzag maps a zig-zag POSITION to the NATURAL      */
                /* index, which is the direction this needs: the decoder uses   */
                /* the same table to scatter, and this gathers through it.      */
                iNatural = (int)JPEG_arrDezigzag[i];

                arrZigzagOut[i] = (int)Math.Round(
                    arrBlockIn[iNatural] / (double)arrQuantIn[iNatural],
                    MidpointRounding.AwayFromZero);
            }
        }

        /*--------------------------------------------------------------------*/
        /* JPEG_MagnitudeCategory:                                            */
        /*                                                                    */
        /* The number of bits a value's magnitude needs, which Annex F.1.2.1  */
        /* calls its category: 0 for zero, 1 for plus or minus one, 2 for 2   */
        /* to 3, 3 for 4 to 7, and so on.                                     */
        /*                                                                    */
        /* THIS IS THE ENCODER'S HALF OF THE RULE THE DECODER'S EXTEND        */
        /* IMPLEMENTS, and the two must agree exactly or every coefficient    */
        /* comes back a different number.  The decoder's half is tested       */
        /* exhaustively at every width from 1 to 8; this one is tested        */
        /* against it by round-trip rather than against a second table.       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iValue : a coefficient or a DC difference, either sign.        */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int    : the category, 0 to 15.                                */
        /*--------------------------------------------------------------------*/

        private static int JPEG_MagnitudeCategory(int iValue)
        {
            int iMagnitude;
            int iBits;

            if (iValue < 0)
            {   /* Only the magnitude decides the category. */
                iMagnitude = -iValue;
            }
            else
            {   /* Already non-negative. */
                iMagnitude = iValue;
            }

            iBits = 0;

            while (iMagnitude > 0)
            {   /* One significant bit per iteration. */
                iBits      = iBits + 1;
                iMagnitude = iMagnitude >> 1;
            }

            /* The count of significant bits IS the category. */
            return(iBits);
        }

        /*--------------------------------------------------------------------*/
        /* JPEG_MagnitudeBitsFor:                                             */
        /*                                                                    */
        /* The bits that follow a category symbol, for a value of that        */
        /* category.                                                          */
        /*                                                                    */
        /* THE NEGATIVE CASE IS THE WHOLE OF THIS FUNCTION.  A positive value */
        /* is sent as itself; a negative one is sent as value - 1 in the low  */
        /* bits, which is what makes the top bit distinguish the two signs    */
        /* and is exactly the rule the decoder's EXTEND undoes.  stb's        */
        /* comment on that rule is inverted against its own code (see section */
        /* 11.17), so this is written from the SPECIFICATION rather than from */
        /* the neighbouring decoder's prose, and proven by round trip.        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iValue    : the value being coded; must not be zero.           */
        /*     iCategory : its category, from JPEG_MagnitudeCategory.         */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int       : the iCategory low bits to append.                  */
        /*--------------------------------------------------------------------*/

        private static int JPEG_MagnitudeBitsFor(int iValue, int iCategory)
        {
            if (iValue < 0)
            {   /* Negative: one less than the value, so the leading bit is   */
                /* clear and the decoder can tell the sign from it alone.     */
                return((iValue - 1) & ((1 << iCategory) - 1));
            }

            /* Positive: the value itself, whose leading bit is set. */
            return(iValue & ((1 << iCategory) - 1));
        }

        /*--------------------------------------------------------------------*/
        /* JPEG_TryEncodeBlockEntropy:                                        */
        /*                                                                    */
        /* Entropy-codes one zig-zag block: the DC as a difference from the   */
        /* previous block's, then the non-zero ACs as (run, category) pairs.  */
        /*                                                                    */
        /* Annex F.1.2, and the exact inverse of JPEG_TryDecodeBlock above.   */
        /*                                                                    */
        /* THE DC IS A DIFFERENCE AND THE PREDICTOR IS CARRIED BY REFERENCE.  */
        /* Every block's DC is coded relative to the previous block of the    */
        /* SAME COMPONENT, which is why the predictor is a caller's variable  */
        /* rather than a local: in a colour file there is one per component   */
        /* and                                                                */
        /* they interleave.  A shared predictor decodes the first block       */
        /* perfectly and drifts thereafter.                                   */
        /*                                                                    */
        /* A RUN OF MORE THAN FIFTEEN ZEROES NEEDS ZRL, because the run       */
        /* nibble only reaches fifteen.  Emitting a longer run without        */
        /* breaking it up writes a symbol the table does not define, and the  */
        /* stream desynchronises from that block onward.                      */
        /*                                                                    */
        /* IT IS A Try FUNCTION, unlike the source's, and the reason is the   */
        /* table lookup.  A symbol with no code in the Annex K table would be */
        /* written as zero bits of nothing, producing a file that is subtly   */
        /* and undetectably wrong; refusing names the symbol instead.  The    */
        /* case is unreachable with the standard tables and a correct         */
        /* quantiser, which is precisely why it is worth a check rather than  */
        /* an assumption.                                                     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     bwIo        : the bit sink.                                    */
        /*     arrZigzagIn : 64 quantised coefficients in zig-zag order.      */
        /*     arrDcPredIo : the caller's DC predictor array.                 */
        /*     iComponent  : which predictor this block uses.                 */
        /*     tblIn       : the DC and AC codes for this component's class.  */
        /*     sReason     : receives the cause when the return is false.     */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool        : true when the block was coded.                   */
        /*--------------------------------------------------------------------*/

        private static bool JPEG_TryEncodeBlockEntropy(JpegBitWriter bwIo,
                                                       int[] arrZigzagIn,
                                                       int[] arrDcPredIo,
                                                       int iComponent,
                                                       JpegEncoderTables tblIn,
                                                       out string sReason)
        {
            int iDiff;
            int iCategory;
            int iRun;
            int iSymbol;
            int iLast;
            int k;

            sReason = "";

            /* ---- The DC, as a difference from this component's previous. -- */
            iDiff                   = arrZigzagIn[0] - arrDcPredIo[iComponent];
            arrDcPredIo[iComponent] = arrZigzagIn[0];

            iCategory = JPEG_MagnitudeCategory(iDiff);

            if (iCategory > JPEG_DC_CATEGORY_MAX)
            {   /* Beyond what Annex K's DC table defines. Only reachable     */
                /* with a quantisation table so fine that a coefficient       */
                /* survives at full magnitude, which the 1..255 clamp makes   */
                /* very unlikely -- but a silent bad symbol is worse than a   */
                /* named refusal.                                             */
                sReason = "JPEG: a DC difference of " + iDiff.ToString() +
                          " needs category " + iCategory.ToString() +
                          ", beyond the " + JPEG_DC_CATEGORY_MAX.ToString() +
                          " the standard table defines";
                return(false);
            }

            if (tblIn.JPEGET_arrDcSize[iCategory] == 0)
            {   /* The table defines no code for that category. */
                sReason = "JPEG: the DC Huffman table has no code for category " +
                          iCategory.ToString();
                return(false);
            }

            bwIo.JPEGBW_WriteBits(tblIn.JPEGET_arrDcCode[iCategory],
                                  tblIn.JPEGET_arrDcSize[iCategory]);

            if (iCategory > 0)
            {   /* Category zero means the DC is unchanged and carries no    */
                /* magnitude bits at all, which is a real and common case.   */
                bwIo.JPEGBW_WriteBits(JPEG_MagnitudeBitsFor(iDiff, iCategory),
                                      iCategory);
            }

            /* ---- The ACs. Find the last non-zero first, so the loop knows    */
            /* where end-of-block belongs without looking ahead each step. --   */
            iLast = 0;

            for (k = 1; k < JPEG_BLOCK_SAMPLES; k++)
            {   /* One coefficient per iteration. */
                if (arrZigzagIn[k] != 0)
                {   /* A live coefficient, so the block runs at least here. */
                    iLast = k;
                }
            }

            iRun = 0;

            for (k = 1; k <= iLast; k++)
            {   /* One coefficient per iteration, up to the last live one. */
                if (arrZigzagIn[k] == 0)
                {   /* A zero: it joins the run rather than being coded. */
                    iRun = iRun + 1;
                    continue;
                }

                while (iRun >= JPEG_MAX_ZERO_RUN)
                {   /* A run longer than the nibble can name is emitted as   */
                    /* one or more ZRLs first.                               */
                    if (tblIn.JPEGET_arrAcSize[JPEG_AC_SYMBOL_ZRL] == 0)
                    {   /* No ZRL in the table. */
                        sReason = "JPEG: the AC Huffman table has no ZRL symbol";
                        return(false);
                    }

                    bwIo.JPEGBW_WriteBits(tblIn.JPEGET_arrAcCode[JPEG_AC_SYMBOL_ZRL],
                                          tblIn.JPEGET_arrAcSize[JPEG_AC_SYMBOL_ZRL]);

                    iRun = iRun - JPEG_MAX_ZERO_RUN;
                }

                iCategory = JPEG_MagnitudeCategory(arrZigzagIn[k]);
                iSymbol   = (iRun << 4) | iCategory;

                if (iCategory > 15 || tblIn.JPEGET_arrAcSize[iSymbol] == 0)
                {   /* A symbol the standard table does not define. */
                    sReason = "JPEG: the AC Huffman table has no code for run " +
                              iRun.ToString() + " category " + iCategory.ToString();
                    return(false);
                }

                bwIo.JPEGBW_WriteBits(tblIn.JPEGET_arrAcCode[iSymbol],
                                      tblIn.JPEGET_arrAcSize[iSymbol]);
                bwIo.JPEGBW_WriteBits(
                    JPEG_MagnitudeBitsFor(arrZigzagIn[k], iCategory), iCategory);

                iRun = 0;
            }

            if (iLast < JPEG_BLOCK_SAMPLES - 1)
            {   /* Everything past the last live coefficient is zero, and    */
                /* end-of-block says so in one symbol.  A block whose LAST   */
                /* zig-zag position is live needs no EOB, because there is   */
                /* nothing left for it to cover.                             */
                if (tblIn.JPEGET_arrAcSize[JPEG_AC_SYMBOL_EOB] == 0)
                {   /* No EOB in the table. */
                    sReason = "JPEG: the AC Huffman table has no EOB symbol";
                    return(false);
                }

                bwIo.JPEGBW_WriteBits(tblIn.JPEGET_arrAcCode[JPEG_AC_SYMBOL_EOB],
                                      tblIn.JPEGET_arrAcSize[JPEG_AC_SYMBOL_EOB]);
            }

            /* The block is on the wire. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* JPEG_BuildYCbCrPlanes:                                             */
        /*                                                                    */
        /* Converts an RGB image into three separate sample planes: luma,     */
        /* then the two chroma differences.                                   */
        /*                                                                    */
        /* SEPARATE PLANES RATHER THAN INTERLEAVED SAMPLES, because that is   */
        /* what the rest of the encoder wants.  Blocks are gathered one       */
        /* component at a time, and JPEG_LoadBlockForEncode already reads a   */
        /* plane -- so producing planes here means the grayscale path's block */
        /* loader, transform, quantiser and entropy coder are all reused      */
        /* unchanged for colour.  Interleaving would have forced a second     */
        /* loader that strides by three.                                      */
        /*                                                                    */
        /* CHROMA IS STORED OFFSET BY 128, NOT CENTRED ON ZERO.  The maths    */
        /* produces a difference around zero, and the format stores it with   */
        /* 128 added so it fits an unsigned byte like every other sample.     */
        /* That matters because JPEG_LoadBlockForEncode then subtracts 128    */
        /* from EVERY plane as its level shift -- so chroma arrives at the    */
        /* transform centred on zero again, which is where it belongs.        */
        /* Storing it centred here would double-shift it and turn a neutral   */
        /* grey into a strong colour cast.                                    */
        /*                                                                    */
        /* THE OUTPUT IS CLAMPED because the transform's coefficients are     */
        /* real numbers and rounding can push a legitimate extreme just past  */
        /* the byte range -- a saturated red gives a Cr fractionally over     */
        /* 255.  Letting that wrap would put a bright cyan speck in the       */
        /* reddest part of the picture.                                       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     imgIn      : an RGB image, three channels, 8-bit.              */
        /*     arrYOut    : receives width * height luma samples.             */
        /*     arrCbOut   : receives the same count of Cb samples.            */
        /*     arrCrOut   : and of Cr.                                        */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void       : the three planes are filled.                      */
        /*--------------------------------------------------------------------*/

        private static void JPEG_BuildYCbCrPlanes(ImageBuffer imgIn, byte[] arrYOut,
                                                  byte[] arrCbOut, byte[] arrCrOut)
        {
            double dblRed;
            double dblGreen;
            double dblBlue;
            int    iCount;
            int    iBase;
            int    i;

            iCount = imgIn.IMG_iWidth * imgIn.IMG_iHeight;

            for (i = 0; i < iCount; i++)
            {   /* One pixel per iteration. */
                iBase = i * 3;

                dblRed   = (double)imgIn.IMG_arrSamples8[iBase];
                dblGreen = (double)imgIn.IMG_arrSamples8[iBase + 1];
                dblBlue  = (double)imgIn.IMG_arrSamples8[iBase + 2];

                arrYOut[i] = JPEG_ClampToByte(
                    (JPEG_RGB_TO_Y_RED * dblRed) +
                    (JPEG_RGB_TO_Y_GREEN * dblGreen) +
                    (JPEG_RGB_TO_Y_BLUE * dblBlue));

                /* The chroma centre is added here and taken away again by   */
                /* the block loader's level shift; see the header.           */
                arrCbOut[i] = JPEG_ClampToByte(
                    (JPEG_RGB_TO_CB_RED * dblRed) +
                    (JPEG_RGB_TO_CB_GREEN * dblGreen) +
                    (JPEG_RGB_TO_CB_BLUE * dblBlue) +
                    (double)JPEG_CHROMA_CENTRE);

                arrCrOut[i] = JPEG_ClampToByte(
                    (JPEG_RGB_TO_CR_RED * dblRed) +
                    (JPEG_RGB_TO_CR_GREEN * dblGreen) +
                    (JPEG_RGB_TO_CR_BLUE * dblBlue) +
                    (double)JPEG_CHROMA_CENTRE);
            }
        }

        /*--------------------------------------------------------------------*/
        /* JPEG_DefaultEncodeOptions:                                         */
        /*                                                                    */
        /* Builds a JpegEncodeOptions carrying the documented defaults.       */
        /*                                                                    */
        /* EVERY FORMAT HAS ONE OF THESE, and JPEG was written without it by  */
        /* oversight -- PNG, QOI and BMP each had theirs from the start.      */
        /* Nothing was broken by that, because a null options argument        */
        /* already means the defaults, but a caller who wants to START from   */
        /* the defaults and change ONE field had no way to ask what they      */
        /* were, and would have had to hard-code 90 at the call site.  That   */
        /* is exactly the drift the factory exists to prevent.                */
        /*                                                                    */
        /* Not a Try function: it cannot fail.                                */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     JpegEncodeOptions : quality 90, no chroma subsampling.         */
        /*--------------------------------------------------------------------*/

        public static JpegEncodeOptions JPEG_DefaultEncodeOptions()
        {
            JpegEncodeOptions optNew;

            optNew = new JpegEncodeOptions();

            optNew.JPEGOPT_iQuality     = JPEG_QUALITY_DEFAULT;
            optNew.JPEGOPT_iSubsampling = JPEG_SUBSAMPLE_444;

            /* The same settings a null options argument selects. */
            return(optNew);
        }

        /*--------------------------------------------------------------------*/
        /* JPEG_TryResolveSamplingFactors:                                    */
        /*                                                                    */
        /* Turns a requested subsampling mode into the per-component          */
        /* horizontal and vertical sampling factors the frame header carries. */
        /*                                                                    */
        /* THE FACTORS ARE RELATIVE, NOT ABSOLUTE, which is the thing about   */
        /* this corner of the format most easily got backwards.  Luma is      */
        /* given the LARGER factor and chroma 1x1; the ratio between them is  */
        /* what says the chroma planes are smaller.  4:2:0 is luma 2x2, not   */
        /* chroma 0.5 -- there is no way to express a fraction, so the        */
        /* reduction is written as luma being sampled more often.  Inverting  */
        /* this produces a file whose chroma is claimed to be LARGER than its */
        /* luma, which is legal to state and impossible to satisfy.           */
        /*                                                                    */
        /* A GRAYSCALE IMAGE CANNOT BE SUBSAMPLED and asking is refused       */
        /* rather than ignored.  There is no chroma to reduce, so honouring   */
        /* the request would mean writing a 2x2 luma factor with no second    */
        /* component to be larger THAN -- a frame that says nothing and       */
        /* wastes three quarters of its blocks.  Refusing names the           */
        /* contradiction instead of silently producing it.                    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iSubsampling : JPEG_SUBSAMPLE_444, _422 or _420.               */
        /*     iComponents  : 1 or 3.                                         */
        /*     arrHOut      : receives one horizontal factor per component.   */
        /*     arrVOut      : and one vertical.                               */
        /*     sReason      : receives the cause when the return is false.    */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool         : true when the request is coherent.              */
        /*--------------------------------------------------------------------*/

        private static bool JPEG_TryResolveSamplingFactors(int iSubsampling,
                                                           int iComponents,
                                                           out int[] arrHOut,
                                                           out int[] arrVOut,
                                                           out string sReason)
        {
            int iLumaH;
            int iLumaV;
            int c;

            arrHOut = null;
            arrVOut = null;
            sReason = "";

            if (iSubsampling == JPEG_SUBSAMPLE_444)
            {   /* Every component sampled equally. */
                iLumaH = 1;
                iLumaV = 1;
            }
            else if (iSubsampling == JPEG_SUBSAMPLE_422)
            {   /* Chroma halved horizontally only. */
                iLumaH = 2;
                iLumaV = 1;
            }
            else if (iSubsampling == JPEG_SUBSAMPLE_420)
            {   /* Chroma halved on both axes. */
                iLumaH = 2;
                iLumaV = 2;
            }
            else
            {   /* A mode this library does not define. */
                sReason = "JPEG: subsampling mode " + iSubsampling.ToString() +
                          " is not one of JPEG_SUBSAMPLE_444, _422 or _420";
                return(false);
            }

            if (iComponents == 1 && iSubsampling != JPEG_SUBSAMPLE_444)
            {   /* Nothing to subsample; see this function's header. */
                sReason = "JPEG: a grayscale image has no chroma to subsample, " +
                          "so only JPEG_SUBSAMPLE_444 is meaningful for it";
                return(false);
            }

            arrHOut = new int[iComponents];
            arrVOut = new int[iComponents];

            /* Component 0 is luma and carries the larger factor; every   */
            /* chroma component is 1x1 against it.                        */
            arrHOut[0] = iLumaH;
            arrVOut[0] = iLumaV;

            for (c = 1; c < iComponents; c++)
            {   /* One chroma component per iteration. */
                arrHOut[c] = 1;
                arrVOut[c] = 1;
            }

            /* Factors a frame header can carry and an MCU walk can use. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* JPEG_DownsamplePlane:                                              */
        /*                                                                    */
        /* Reduces a full-resolution plane by averaging each iHs by iVs group */
        /* of samples into one.                                               */
        /*                                                                    */
        /* AVERAGING, NOT POINT SAMPLING, and that is the whole substance of  */
        /* this function.  Taking every other sample is a one-line            */
        /* alternative that produces a plausible picture and is wrong: it     */
        /* throws away half the information instead of folding it in, so      */
        /* every edge in the chroma planes aliases -- a red-to-blue boundary  */
        /* picks up a staircase that was never in the source.  Averaging is   */
        /* what libjpeg does by default and is the counterpart the decoder's  */
        /* interpolating upsamplers expect; a box average in and a 3:1        */
        /* weighted interpolation out is the pairing the format assumes.      */
        /*                                                                    */
        /* THE SOURCE COORDINATES ARE CLAMPED, not skipped, for the group     */
        /* that hangs off the right or bottom edge.  An odd-width image at    */
        /* 4:2:0 has a final chroma column whose group is half outside the    */
        /* picture; clamping repeats the last real sample, which is the same  */
        /* choice JPEG_LoadBlockForEncode makes for the same reason.          */
        /* Averaging in a zero instead would darken the entire right edge of  */
        /* every odd-sized image -- a defect that is invisible on the even    */
        /* sizes a casual test uses.                                          */
        /*                                                                    */
        /* THE ROUNDING ADDS HALF THE DIVISOR before dividing, so a group     */
        /* averaging 127.5 becomes 128 rather than 127.  Truncating instead   */
        /* biases every chroma sample downward by up to half a count, which   */
        /* across a whole plane is a faint but real colour cast.              */
        /*                                                                    */
        /* Not a Try function: every dimension is derived from the frame      */
        /* geometry, which was validated before any of this ran.              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     arrFullIn : the full-resolution plane.                         */
        /*     iFullW    : its width in samples.                              */
        /*     iFullH    : its height.                                        */
        /*     iHs       : how many source columns make one output column.    */
        /*     iVs       : how many source rows make one output row.          */
        /*     arrOut    : receives iOutW by iOutH samples.                   */
        /*     iOutW     : the reduced width.                                 */
        /*     iOutH     : the reduced height.                                */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void      : the reduced plane is filled.                       */
        /*--------------------------------------------------------------------*/

        private static void JPEG_DownsamplePlane(byte[] arrFullIn, int iFullW,
                                                 int iFullH, int iHs, int iVs,
                                                 byte[] arrOut, int iOutW,
                                                 int iOutH)
        {
            int iSum;
            int iDivisor;
            int iSourceX;
            int iSourceY;
            int x;
            int y;
            int dx;
            int dy;

            iDivisor = iHs * iVs;

            for (y = 0; y < iOutH; y++)
            {   /* One output row per iteration. */
                for (x = 0; x < iOutW; x++)
                {   /* One output sample per iteration. */
                    iSum = 0;

                    for (dy = 0; dy < iVs; dy++)
                    {   /* One source row of the group. */
                        iSourceY = (y * iVs) + dy;

                        if (iSourceY >= iFullH)
                        {   /* Past the bottom: repeat the last real row. */
                            iSourceY = iFullH - 1;
                        }

                        for (dx = 0; dx < iHs; dx++)
                        {   /* One source column of the group. */
                            iSourceX = (x * iHs) + dx;

                            if (iSourceX >= iFullW)
                            {   /* Past the right: repeat the last column. */
                                iSourceX = iFullW - 1;
                            }

                            iSum = iSum +
                                   (int)arrFullIn[(iSourceY * iFullW) + iSourceX];
                        }
                    }

                    /* Half the divisor rounds to nearest instead of down. */
                    arrOut[(y * iOutW) + x] =
                        (byte)((iSum + (iDivisor / 2)) / iDivisor);
                }
            }
        }

        /*--------------------------------------------------------------------*/
        /* JPEG_ClampToByte:                                                  */
        /*                                                                    */
        /* Rounds a real number to the nearest whole sample and saturates it  */
        /* into 0 to 255.                                                     */
        /*                                                                    */
        /* SEPARATE FROM JPEG_Clamp, which the decoder uses, because that one */
        /* takes an int that has already been rounded by a fixed-point shift. */
        /* This one owns the rounding as well, and rounding before clamping   */
        /* is the order that matters: clamping first would turn 255.4 into    */
        /* 255 correctly but 255.6 into 255 as well, losing nothing -- while  */
        /* rounding first turns -0.4 into 0 rather than into -1 and then 0.   */
        /* Same answer, but only one of the two orders is obviously right at  */
        /* every input, and mixing them across the file would invite a        */
        /* mismatch.                                                          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     dblValue : the real sample value.                              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     byte     : 0 to 255.                                           */
        /*--------------------------------------------------------------------*/

        private static byte JPEG_ClampToByte(double dblValue)
        {
            int iRounded;

            iRounded = (int)Math.Round(dblValue, MidpointRounding.AwayFromZero);

            if (iRounded < 0)
            {   /* Below the range a sample can hold. */
                return(0);
            }

            if (iRounded > 255)
            {   /* Above it. */
                return(255);
            }

            /* Inside the range already. */
            return((byte)iRounded);
        }

        /*--------------------------------------------------------------------*/
        /* JPEG_WriteMarkerSegments:                                          */
        /*                                                                    */
        /* Writes everything before the entropy data: SOI, a JFIF APP0, the   */
        /* quantisation table, the frame header, both Huffman tables and the  */
        /* scan header.                                                       */
        /*                                                                    */
        /* THE DQT IS WRITTEN IN ZIG-ZAG ORDER while the table is HELD in     */
        /* natural order, and that conversion happens here and nowhere else.  */
        /* The format defines the segment's 64 bytes as the zig-zag sequence; */
        /* the quantiser wants natural indexing.  Getting this backwards      */
        /* produces a file whose own decoder undoes the wrong divisor per     */
        /* coefficient -- an image that is recognisable and wrong in a way    */
        /* that looks like a transform bug.  Our decoder de-zigzags DQT as it */
        /* reads, so a round trip through this library alone would NOT catch  */
        /* it; only an independent decoder would.                             */
        /*                                                                    */
        /* A SEGMENT'S DECLARED LENGTH INCLUDES ITS OWN TWO BYTES, the same   */
        /* trap the decoder above documents from the other side.              */
        /*                                                                    */
        /* ONE COMPONENT OR THREE, and the shape follows from that alone.     */
        /* A grayscale file carries one DQT, two DHTs and a one-component     */
        /* SOF and SOS; a colour one carries two DQTs, four DHTs and three    */
        /* components, with the sampling factors the caller asked for.        */
        /* Everything else is identical, which is why this is one             */
        /* function with a count rather than two that would drift.            */
        /*                                                                    */
        /* THE COMPONENT IDS ARE 1, 2, 3, the JFIF convention.  The decoder   */
        /* deliberately treats ids 'R', 'G', 'B' as meaning raw RGB (section  */
        /* 11.26), so writing anything unconventional here would make our own */
        /* reader skip the colour transform on our own file.                  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     bwOut        : the file under construction.                    */
        /*     iWidth       : image width in pixels.                          */
        /*     iHeight      : image height.                                   */
        /*     iComponents  : 1 for grayscale, 3 for colour.                  */
        /*     arrQuantLuma : the luma quantisation table, NATURAL order.     */
        /*     arrQuantChroma : the chroma one, or null when grayscale.       */
        /*     arrH         : per-component horizontal sampling factor.       */
        /*     arrV         : per-component vertical sampling factor.         */
        /*                                                                    */
        /* THE SAMPLING FACTORS ARE PASSED IN rather than assumed to be 1x1.  */
        /* They are the ONLY thing in the                                     */
        /* header that tells a decoder the chroma planes are smaller than the */
        /* image, so a file whose factors say 1x1 while its scan carries      */
        /* subsampled data decodes to a quarter-sized picture in the corner   */
        /* of a full-sized frame.                                             */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void         : the segments are written.                       */
        /*--------------------------------------------------------------------*/

        private static void JPEG_WriteMarkerSegments(ByteWriter bwOut, int iWidth,
                                                     int iHeight, int iComponents,
                                                     int[] arrQuantLuma,
                                                     int[] arrQuantChroma,
                                                     int[] arrH, int[] arrV)
        {
            int iTable;
            int c;

            /* SOI. */
            bwOut.BYTEW_WriteU8(0xFF);
            bwOut.BYTEW_WriteU8(JPEG_MARKER_SOI);

            /* APP0, the JFIF identity: version 1.1, no density units, no      */
            /* embedded thumbnail.  Written because a decoder is entitled to   */
            /* read the absence of it as a hint that the file is not YCbCr     */
            /* (see JPEG_IsRawRgb), and this file certainly is.                */
            bwOut.BYTEW_WriteU8(0xFF);
            bwOut.BYTEW_WriteU8(JPEG_MARKER_APP0);
            bwOut.BYTEW_WriteU16BE(16);
            bwOut.BYTEW_WriteU8(0x4A);                   // 'J'
            bwOut.BYTEW_WriteU8(0x46);                   // 'F'
            bwOut.BYTEW_WriteU8(0x49);                   // 'I'
            bwOut.BYTEW_WriteU8(0x46);                   // 'F'
            bwOut.BYTEW_WriteU8(0x00);                   // the tag's terminator
            bwOut.BYTEW_WriteU8(1);                      // major version
            bwOut.BYTEW_WriteU8(1);                      // minor version
            bwOut.BYTEW_WriteU8(0);                      // density units: none, so the ratio is all that is meant
            bwOut.BYTEW_WriteU16BE(1);                   // horizontal density
            bwOut.BYTEW_WriteU16BE(1);                   // vertical density
            bwOut.BYTEW_WriteU8(0);                      // thumbnail width: none
            bwOut.BYTEW_WriteU8(0);                      // thumbnail height: none

            /* DQT: one table for grayscale, two for colour. */
            JPEG_WriteDqtSegment(bwOut, 0, arrQuantLuma);

            if (iComponents == 3)
            {   /* Chroma gets its own, much coarser table. */
                JPEG_WriteDqtSegment(bwOut, 1, arrQuantChroma);
            }

            /* SOF0: baseline, 8-bit, no subsampling. */
            bwOut.BYTEW_WriteU8(0xFF);
            bwOut.BYTEW_WriteU8(JPEG_MARKER_SOF0);
            bwOut.BYTEW_WriteU16BE(2 + 1 + 2 + 2 + 1 + (3 * iComponents));
            bwOut.BYTEW_WriteU8(JPEG_SUPPORTED_PRECISION);

            /* HEIGHT BEFORE WIDTH.  The only field order in this library   */
            /* that is not width-first, and invisible on a square image.    */
            bwOut.BYTEW_WriteU16BE(iHeight);
            bwOut.BYTEW_WriteU16BE(iWidth);

            bwOut.BYTEW_WriteU8(iComponents);

            for (c = 0; c < iComponents; c++)
            {   /* One three-byte descriptor per component. */
                if (c == 0)
                {   /* Luma uses quantisation table 0. */
                    iTable = 0;
                }
                else
                {   /* Both chroma planes share table 1. */
                    iTable = 1;
                }

                bwOut.BYTEW_WriteU8(c + 1);              // the JFIF ids: 1, 2, 3

                /* Horizontal factor in the high nibble, vertical in the low. */
                bwOut.BYTEW_WriteU8(((arrH[c] & 0x0F) << 4) | (arrV[c] & 0x0F));

                bwOut.BYTEW_WriteU8(iTable);
            }

            /* The Huffman tables, written from the same arrays the encoder's   */
            /* own codes were expanded from -- so the file and the encoder      */
            /* cannot disagree about them.                                      */
            JPEG_WriteDhtSegment(bwOut, 0x00, JPEG_arrDcLumaBits,
                                 JPEG_arrDcLumaVals);
            JPEG_WriteDhtSegment(bwOut, 0x10, JPEG_arrAcLumaBits,
                                 JPEG_arrAcLumaVals);

            if (iComponents == 3)
            {   /* Chroma's own pair, at table id 1 in each class. */
                JPEG_WriteDhtSegment(bwOut, 0x01, JPEG_arrDcChromaBits,
                                     JPEG_arrDcChromaVals);
                JPEG_WriteDhtSegment(bwOut, 0x11, JPEG_arrAcChromaBits,
                                     JPEG_arrAcChromaVals);
            }

            /* SOS: every component, then the three baseline bytes 0, 63, 0   */
            /* that say "every coefficient, no successive approximation".     */
            bwOut.BYTEW_WriteU8(0xFF);
            bwOut.BYTEW_WriteU8(JPEG_MARKER_SOS);
            bwOut.BYTEW_WriteU16BE(2 + 1 + (2 * iComponents) + 3);
            bwOut.BYTEW_WriteU8(iComponents);

            for (c = 0; c < iComponents; c++)
            {   /* One two-byte selector pair per component. */
                if (c == 0)
                {   /* Luma selects id 0 in both classes. */
                    iTable = 0x00;
                }
                else
                {   /* Chroma selects id 1 in both: DC high nibble, AC low. */
                    iTable = 0x11;
                }

                bwOut.BYTEW_WriteU8(c + 1);              // the component's id, matching SOF
                bwOut.BYTEW_WriteU8(iTable);
            }

            bwOut.BYTEW_WriteU8(0);                      // Ss
            bwOut.BYTEW_WriteU8(JPEG_MAX_SPECTRAL);      // Se
            bwOut.BYTEW_WriteU8(0);                      // Ah and Al, both zero
        }

        /*--------------------------------------------------------------------*/
        /* JPEG_WriteDqtSegment:                                              */
        /*                                                                    */
        /* Writes one quantisation table, converting it from the natural      */
        /* order the encoder holds into the zig-zag order the format defines. */
        /*                                                                    */
        /* FACTORED OUT WHEN CHROMA ARRIVED, because a second inline copy of  */
        /* the zig-zag conversion is exactly the duplication that eventually  */
        /* disagrees with itself -- and this is the one conversion our own    */
        /* decoder would CANCEL rather than catch, so a drift between two     */
        /* copies of it would survive every round-trip check in the suite.    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     bwOut     : the file under construction.                       */
        /*     iTableId  : 0 for luma, 1 for chroma.                          */
        /*     arrQuantIn: the table, NATURAL order.                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void      : the segment is written.                            */
        /*--------------------------------------------------------------------*/

        private static void JPEG_WriteDqtSegment(ByteWriter bwOut, int iTableId,
                                                 int[] arrQuantIn)
        {
            int i;

            bwOut.BYTEW_WriteU8(0xFF);
            bwOut.BYTEW_WriteU8(JPEG_MARKER_DQT);
            bwOut.BYTEW_WriteU16BE(2 + 1 + JPEG_BLOCK_SAMPLES);

            /* Precision 0 means 8-bit entries; the id is the low nibble. */
            bwOut.BYTEW_WriteU8(iTableId & 0x0F);

            for (i = 0; i < JPEG_BLOCK_SAMPLES; i++)
            {   /* The segment carries the table in ZIG-ZAG order. */
                bwOut.BYTEW_WriteU8(arrQuantIn[(int)JPEG_arrDezigzag[i]]);
            }
        }

        /*--------------------------------------------------------------------*/
        /* JPEG_WriteDhtSegment:                                              */
        /*                                                                    */
        /* Writes one DHT segment from a BITS / VALS specification table.     */
        /*                                                                    */
        /* THE SEGMENT CARRIES THE SPECIFICATION TABLE, NOT THE EXPANDED      */
        /* CODES, and that is the format's design rather than an economy: a   */
        /* decoder rebuilds the codes canonically from the counts, which is   */
        /* the trap the decoder above documents on the reading side.  So      */
        /* the bytes written here are literally the Annex K arrays.           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     bwOut    : the file under construction.                        */
        /*     iTcTh    : class in the high nibble, table id in the low.      */
        /*     arrBits  : sixteen counts.                                     */
        /*     arrVals  : the symbols, in code order.                         */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void     : the segment is written.                             */
        /*--------------------------------------------------------------------*/

        private static void JPEG_WriteDhtSegment(ByteWriter bwOut, int iTcTh,
                                                 byte[] arrBits, byte[] arrVals)
        {
            int i;

            bwOut.BYTEW_WriteU8(0xFF);
            bwOut.BYTEW_WriteU8(JPEG_MARKER_DHT);
            bwOut.BYTEW_WriteU16BE(2 + 1 + JPEG_HUFFMAN_LENGTHS + arrVals.Length);
            bwOut.BYTEW_WriteU8(iTcTh);

            for (i = 0; i < JPEG_HUFFMAN_LENGTHS; i++)
            {   /* The sixteen length counts. */
                bwOut.BYTEW_WriteU8((int)arrBits[i]);
            }

            for (i = 0; i < arrVals.Length; i++)
            {   /* Then the symbols they name. */
                bwOut.BYTEW_WriteU8((int)arrVals[i]);
            }
        }

        /*---------------------------------------------------------------------*/
        /* JPEG_TryEncode:                                                     */
        /*                                                                     */
        /* Encodes an ImageBuffer as a baseline JPEG.                          */
        /*                                                                     */
        /* THE PUBLIC ENCODE ENTRY POINT, and the only call in this half of    */
        /* the file a consumer needs.                                          */
        /*                                                                     */
        /* IT WRITES GRAYSCALE AND COLOUR, at 4:4:4, 4:2:2 and 4:2:0, at any   */
        /* geometry.  No refusal it can produce defers to unwritten work; each */
        /* one is a statement about the request -- an unrepresentable depth,   */
        /* an alpha channel JPEG cannot carry, a subsampling mode that does    */
        /* not exist, or subsampling asked of an image with no chroma.         */
        /*                                                                     */
        /* SIXTEEN-BIT INPUT IS REFUSED PERMANENTLY, not deferred.  Baseline   */
        /* JPEG is an 8-bit format; the 12-bit extension is a different mode   */
        /* this library does not decode either, so accepting a 16-bit buffer   */
        /* would mean silently narrowing the caller's data.  That is the line  */
        /* every encoder here draws -- lossless conversions are done for the   */
        /* caller, lossy ones are theirs to ask for.                           */
        /*                                                                     */
        /* Arguments:                                                          */
        /*     imgIn   : the image to encode.                                  */
        /*     optIn   : encode options, or null for the defaults.             */
        /*     arrOut  : receives the JPEG bytes; null on failure.             */
        /*     sReason : receives the cause when the return is false.          */
        /*                                                                     */
        /* Returns:                                                            */
        /*     bool    : true when a file was produced.                        */
        /*---------------------------------------------------------------------*/

        public static bool JPEG_TryEncode(ImageBuffer imgIn, JpegEncodeOptions optIn,
                                          out byte[] arrOut, out string sReason)
        {
            ByteWriter        bwOut;
            JpegBitWriter     bwBits;
            JpegEncoderTables tblUse;
            byte[][]          arrPlanes;
            byte[]            arrFullY;
            byte[]            arrFullCb;
            byte[]            arrFullCr;
            byte[]            arrFullUse;
            double[]          arrBlock;
            int[]             arrZigzag;
            int[]             arrQuant;
            int[]             arrQuantChroma;
            int[]             arrQuantUse;
            int[]             arrDcPred;
            int[]             arrH;
            int[]             arrV;
            int[]             arrPlaneWidth;
            int[]             arrPlaneHeight;
            int               iComponents;
            int               iSubsampling;
            int               iQuality;
            int               iMaxH;
            int               iMaxV;
            int               iMcuCountX;
            int               iMcuCountY;
            int               iBlocksX;
            int               iBlocksY;
            int               bx;
            int               by;
            int               c;
            int               x;
            int               y;

            arrOut  = null;
            sReason = "";

            if (imgIn == null)
            {   /* A null image is a CALLER bug, not malformed data, so it   */
                /* throws rather than returning a reason string.             */
                throw new ArgumentNullException("imgIn");
            }

            if (!ImageBuffer.IMG_TryValidate(imgIn, out sReason))
            {   /* A buffer whose fields contradict each other. */
                return(false);
            }

            if (imgIn.IMG_iBitsPerSample != 8)
            {   /* Baseline JPEG is an 8-bit format; see the header. */
                sReason = "JPEG: encoding needs 8-bit samples, but this buffer " +
                          "holds " + imgIn.IMG_iBitsPerSample.ToString() +
                          "-bit; convert it first";
                return(false);
            }

            if (imgIn.IMG_iChannels != 1 && imgIn.IMG_iChannels != 3)
            {   /* Two channels is grey-plus-alpha and four is RGBA, and JPEG   */
                /* has no transparency at all -- so this is a PERMANENT         */
                /* refusal rather than a deferral.  Dropping the alpha          */
                /* silently is the lossy narrowing every encoder here           */
                /* refuses; a caller who wants it says so with                  */
                /* IMG_TryConvertChannels.                                      */
                sReason = "JPEG: encoding needs 1 channel (grayscale) or 3 " +
                          "(colour), but this buffer has " +
                          imgIn.IMG_iChannels.ToString() +
                          "; JPEG cannot carry an alpha channel";
                return(false);
            }

            if (optIn == null)
            {   /* A null options argument means the defaults. */
                iQuality = JPEG_QUALITY_DEFAULT;
            }
            else if (optIn.JPEGOPT_iQuality == 0)
            {   /* An unset quality field means the same thing, so a caller   */
                /* can build the options object for its other field alone.    */
                iQuality = JPEG_QUALITY_DEFAULT;
            }
            else
            {   /* An explicit quality, clamped inside the scaler. */
                iQuality = optIn.JPEGOPT_iQuality;
            }

            if (optIn == null)
            {   /* No options at all means no subsampling, which is the     */
                /* safest default: it is the only mode that loses nothing   */
                /* beyond the quantisation the caller asked for.            */
                iSubsampling = JPEG_SUBSAMPLE_444;
            }
            else
            {   /* Whatever the caller named; validated below. */
                iSubsampling = optIn.JPEGOPT_iSubsampling;
            }

            iComponents = imgIn.IMG_iChannels;

            if (!JPEG_TryResolveSamplingFactors(iSubsampling, iComponents,
                                                out arrH, out arrV, out sReason))
            {   /* A subsampling mode that does not exist, or one asked of a   */
                /* grayscale image, which has no chroma to subsample.          */
                return(false);
            }

            arrQuant       = JPEG_ScaleQuantTable(JPEG_arrQuantLumaBase, iQuality);
            arrQuantChroma = null;

            if (iComponents == 3)
            {   /* Colour needs the coarser chroma table as well. */
                arrQuantChroma = JPEG_ScaleQuantTable(JPEG_arrQuantChromaBase,
                                                      iQuality);
            }

            /* THE MCU IS SIZED BY THE MAXIMA, not by any one component.  At    */
            /* 4:2:0 it is sixteen pixels square and carries four luma blocks   */
            /* against one of each chroma; at 4:4:4 it is eight square and      */
            /* carries one of each.  Everything below is written in terms of    */
            /* these two numbers so that all three layouts are one code path.   */
            iMaxH = arrH[0];
            iMaxV = arrV[0];

            iMcuCountX = (imgIn.IMG_iWidth + (8 * iMaxH) - 1) / (8 * iMaxH);
            iMcuCountY = (imgIn.IMG_iHeight + (8 * iMaxV) - 1) / (8 * iMaxV);

            /* BUILD THE PLANES, each at ITS OWN size.  A subsampled chroma     */
            /* plane is genuinely smaller than the image -- that is the point   */
            /* of subsampling -- so its dimensions are the image's scaled by    */
            /* this component's factors against the maxima, rounded UP: a       */
            /* half-resolution plane of an odd-width image still needs the      */
            /* odd sample.                                                      */
            arrPlanes     = new byte[iComponents][];
            arrPlaneWidth  = new int[iComponents];
            arrPlaneHeight = new int[iComponents];

            if (iComponents == 1)
            {   /* Grayscale: the buffer IS the luma plane, used directly   */
                /* rather than copied.                                      */
                arrPlanes[0]      = imgIn.IMG_arrSamples8;
                arrPlaneWidth[0]  = imgIn.IMG_iWidth;
                arrPlaneHeight[0] = imgIn.IMG_iHeight;
            }
            else
            {   /* Colour: three full-size planes first, then the chroma pair   */
                /* reduced if the layout asks for it.                           */
                arrFullY  = new byte[imgIn.IMG_iWidth * imgIn.IMG_iHeight];
                arrFullCb = new byte[imgIn.IMG_iWidth * imgIn.IMG_iHeight];
                arrFullCr = new byte[imgIn.IMG_iWidth * imgIn.IMG_iHeight];

                JPEG_BuildYCbCrPlanes(imgIn, arrFullY, arrFullCb, arrFullCr);

                arrPlanes[0]      = arrFullY;
                arrPlaneWidth[0]  = imgIn.IMG_iWidth;
                arrPlaneHeight[0] = imgIn.IMG_iHeight;

                for (c = 1; c < iComponents; c++)
                {   /* One chroma plane per iteration. */
                    arrPlaneWidth[c]  = ((imgIn.IMG_iWidth * arrH[c]) +
                                         iMaxH - 1) / iMaxH;
                    arrPlaneHeight[c] = ((imgIn.IMG_iHeight * arrV[c]) +
                                         iMaxV - 1) / iMaxV;

                    if (arrH[c] == iMaxH && arrV[c] == iMaxV)
                    {   /* 4:4:4: nothing to reduce, so the full plane is   */
                        /* used as it stands rather than copied through a   */
                        /* one-to-one average.                              */
                        if (c == 1)
                        {   /* Cb. */
                            arrPlanes[c] = arrFullCb;
                        }
                        else
                        {   /* Cr. */
                            arrPlanes[c] = arrFullCr;
                        }
                    }
                    else
                    {   /* Reduce by the ratio of this component's factors to   */
                        /* the maxima.                                          */
                        arrPlanes[c] = new byte[arrPlaneWidth[c] *
                                                arrPlaneHeight[c]];

                        if (c == 1)
                        {   /* Cb. */
                            arrFullUse = arrFullCb;
                        }
                        else
                        {   /* Cr. */
                            arrFullUse = arrFullCr;
                        }

                        JPEG_DownsamplePlane(arrFullUse, imgIn.IMG_iWidth,
                                             imgIn.IMG_iHeight,
                                             iMaxH / arrH[c], iMaxV / arrV[c],
                                             arrPlanes[c], arrPlaneWidth[c],
                                             arrPlaneHeight[c]);
                    }
                }
            }

            bwOut = new ByteWriter(1024 + (iMcuCountX * iMcuCountY * 64 *
                                           iMaxH * iMaxV * iComponents));

            JPEG_WriteMarkerSegments(bwOut, imgIn.IMG_iWidth, imgIn.IMG_iHeight,
                                     iComponents, arrQuant, arrQuantChroma,
                                     arrH, arrV);

            bwBits    = new JpegBitWriter(bwOut);
            arrBlock  = new double[JPEG_BLOCK_SAMPLES];
            arrZigzag = new int[JPEG_BLOCK_SAMPLES];
            arrDcPred = new int[JPEG_MAX_COMPONENTS];

            for (y = 0; y < iMcuCountY; y++)
            {   /* One MCU row per iteration. */
                for (x = 0; x < iMcuCountX; x++)
                {   /* One MCU per iteration. */
                    for (c = 0; c < iComponents; c++)
                    {   /* THE COMPONENTS INTERLEAVE INSIDE THE MCU: all of   */
                        /* component 0's blocks do NOT come first.  Writing   */
                        /* them plane by plane produces a file whose every    */
                        /* geometry check passes and whose colours are        */
                        /* scattered.                                         */
                        if (c == 0)
                        {   /* Luma: its own tables. */
                            arrQuantUse = arrQuant;
                            tblUse      = JPEG_tblEncodeLuma;
                        }
                        else
                        {   /* Both chroma planes share the other set. */
                            arrQuantUse = arrQuantChroma;
                            tblUse      = JPEG_tblEncodeChroma;
                        }

                        for (by = 0; by < arrV[c]; by++)
                        {   /* This component's block rows within the MCU. */
                            for (bx = 0; bx < arrH[c]; bx++)
                            {   /* AND ITS COLUMNS.  A component contributes   */
                                /* h by v blocks to every MCU, which is one    */
                                /* at 4:4:4 and four for luma at 4:2:0.        */
                                /* Coding one block per component regardless   */
                                /* would leave three quarters of the luma      */
                                /* unwritten.                                  */
                                iBlocksX = (x * arrH[c]) + bx;
                                iBlocksY = (y * arrV[c]) + by;

                                JPEG_LoadBlockForEncode(arrPlanes[c],
                                                        arrPlaneWidth[c],
                                                        arrPlaneHeight[c],
                                                        iBlocksX, iBlocksY,
                                                        arrBlock);

                                JPEG_ForwardDct(arrBlock);
                                JPEG_QuantizeToZigzag(arrBlock, arrQuantUse,
                                                      arrZigzag);

                                /* THE DC PREDICTOR IS PER COMPONENT.  One   */
                                /* shared predictor decodes the first MCU    */
                                /* perfectly and then drifts, producing an   */
                                /* image whose colour slides across it.      */
                                if (!JPEG_TryEncodeBlockEntropy(bwBits, arrZigzag,
                                                                arrDcPred, c,
                                                                tblUse,
                                                                out sReason))
                                {   /* A symbol the tables cannot express. */
                                    sReason = "JPEG: MCU (" + x.ToString() + "," +
                                              y.ToString() + ") component " +
                                              c.ToString() + " block (" +
                                              bx.ToString() + "," +
                                              by.ToString() + "): " + sReason;
                                    return(false);
                                }
                            }
                        }
                    }
                }
            }

            /* The last partial byte is real data and has to go out. */
            bwBits.JPEGBW_Flush();

            bwOut.BYTEW_WriteU8(0xFF);
            bwOut.BYTEW_WriteU8(JPEG_MARKER_EOI);

            arrOut = bwOut.BYTEW_ToArray();

            /* A complete baseline grayscale JPEG. */
            return(true);
        }
    }
}
