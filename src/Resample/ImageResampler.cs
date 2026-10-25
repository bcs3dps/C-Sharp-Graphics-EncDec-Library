/*----------------------------------------------------------------------------*/
/* SPDX-License-Identifier: MIT OR GPL-2.0-or-later                           */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* Dual-licensed at the recipient's choice -- see LICENSE in this folder for  */
/* the full terms of both licences.  GPL-2.0 full text in GPL-2.0.txt.        */
/*----------------------------------------------------------------------------*/
/* ImageResampler.cs -- image scaling                             (RESAMP_)   */
/*----------------------------------------------------------------------------*/
/* THE LINEAGE IS MIXED, DELIBERATELY, and the split is not where a reader    */
/* would guess.  It was measured before any code was written; the short form  */
/* is below.                                                                  */
/*                                                                            */
/*   - THE MATHEMATICS IS TRANSCODED from stb_image_resize2.h v2.18           */
/*     (nothings/stb), public domain, which satisfies both arms of this       */
/*     file's licence.  Specifically: the five filter kernels and their       */
/*     support radii from its stbir__filter_* group, the pixel-centre and     */
/*     contributor-range arithmetic from                                      */
/*     stbir__calculate_coefficients_for_gather_upsample and its downsample   */
/*     counterpart, and the normalise-then-fold-the-edge order from           */
/*     stbir__cleanup_gathered_coefficients.                                  */
/*                                                                            */
/*   - THE PIPELINE IS OURS.  stb's is a gather/scatter architecture with     */
/*     ring buffers, split ranges, a coder dispatch table and roughly 2 500   */
/*     lines of the file INCLUDING ITSELF to generate per-layout and          */
/*     per-channel-count variants.  All of that exists to feed SIMD lanes and */
/*     worker threads that C# discards, and it would be thousands of lines of */
/*     performance scaffolding carried for no performance.  Only the SCALAR   */
/*     reference path is taken from that source, and this is what that means  */
/*     in practice.                                                           */
/*                                                                            */
/* Same call GifCodec.cs made for the LZW encoder and for the animation       */
/* model: take the part we do not already understand, write the part we do,   */
/* and record the seam rather than drifting across it.                        */
/*----------------------------------------------------------------------------*/
/* WHAT THIS FILE DOES: THE COEFFICIENTS AND THE TWO PASSES.                  */
/*                                                                            */
/* THE COEFFICIENTS -- the five filter kernels, their support radii, and the  */
/* contributor table for ONE axis: which input pixels feed each output pixel, */
/* and with what weights.  There is no image in any of it.                    */
/*                                                                            */
/* WHY THAT WAS BUILT AND PROVEN FIRST.  It is the only part of a resampler   */
/* with no image in it, so its expected output is arithmetic a person can     */
/* work out and write beside the test.  It is also the part whose failures    */
/* are least legible downstream: a coefficient table that is subtly wrong     */
/* produces an image that is slightly soft or slightly shifted EVERYWHERE,    */
/* which is precisely what a tolerance absorbs and therefore precisely what a */
/* tolerance hides.  The JPEG bit reader was separated out on the same        */
/* argument, for the same reason.                                             */
/*                                                                            */
/* THE RESIZE -- RESAMP_TryResize and the two passes that consume those       */
/* tables: horizontal into a FLOAT intermediate, vertical out of it, and one  */
/* clamp back at the very end.  Every channel count, every ratio.             */
/*                                                                            */
/* SIXTEEN-BIT SAMPLES go through the same pipeline, as do the degenerate     */
/* geometries.  The prediction made when the two passes were written held:    */
/* the intermediate is                                                        */
/* float and therefore depth-agnostic, so 16-bit is a different source read   */
/* and a different clamp ceiling, not a second pipeline.  What it cost was    */
/* four pass functions where there were two, because C# 5 has no way to       */
/* write one loop over two element types that does not either box every       */
/* sample or widen the whole source first.                                    */
/*                                                                            */
/* THE RESAMPLER IS COMPLETE: both sample depths, all four channel counts,    */
/* every ratio including a single pixel in or out, and no shape an            */
/* ImageBuffer can hold is refused.                                           */
/*----------------------------------------------------------------------------*/
/* THREE DECISIONS THE PLAN DID NOT NAME, recorded here because each is the   */
/* kind of thing a reader would otherwise take for an oversight.              */
/*                                                                            */
/* 1. THE INTERMEDIATE IS FLOAT, AND A SAMPLE IS ROUNDED EXACTLY ONCE.        */
/*    Quantising between the passes would round twice and throw away up to    */
/*    half a count per axis, which on a four-times downscale of a gradient    */
/*    is visible banding.  It costs nothing structurally -- the intermediate  */
/*    is a scratch array either way.                                          */
/*                                                                            */
/*    A FLOAT INTERMEDIATE IS ALSO WHY 16-BIT FITS AT ALL.  A 24-bit mantissa */
/*    represents every integer up to 16 777 216 exactly, so a 65 535 sample   */
/*    and every weighted sum of a few hundred of them survive the middle of   */
/*    the pipeline without losing a count.  Had the intermediate been the     */
/*    obvious int or short, 16-bit support would have been a second pipeline  */
/*    rather than a second clamp.                                             */
/*                                                                            */
/* 2. HORIZONTAL FIRST, ALWAYS, rather than whichever order is cheaper.       */
/*    stb picks the order that minimises the intermediate -- horizontal       */
/*    first when narrowing, vertical first when widening -- and on a large    */
/*    resize that is a real saving.  It is deliberately NOT done here: it     */
/*    doubles the pass code or forces a transpose, and it buys speed on a     */
/*    path whose first consumer scales one thumbnail per upload.  Worth       */
/*    revisiting only if a profile ever says so.                              */
/*                                                                            */
/* 3. ALPHA HAS A MODE, AND IT DEFAULTS TO PREMULTIPLIED.  Colour is          */
/*    multiplied by coverage on the way into the intermediate and divided by  */
/*    it on the way out, so an invisible pixel contributes no colour to its   */
/*    visible neighbours.  RESAMP_ALPHA_STRAIGHT turns that off and           */
/*    resamples every channel independently, alpha included.                  */
/*                                                                            */
/*    STRAIGHT WAS BUILT FIRST AND ALONE, and the reasoning is worth keeping  */
/*    because it shows how the decision was reached rather than assumed.  The */
/*    stated acceptance criterion for the resize was CHANNEL INDEPENDENCE --  */
/*    "resizing RGBA equals resizing each channel alone" -- which             */
/*    premultiplying breaks by construction, and the frozen surface had no    */
/*    parameter to select anything else.  So what was specified got built and */
/*    the question was raised rather than answered.                           */
/*                                                                            */
/*    THE ANSWER, SETTLED 2026-09-15: premultiplied by default, with straight */
/*    kept as an option.  The public signature was settled first and this     */
/*    code written against it, rather than the parameter being discovered     */
/*    by the implementation -- the same order GIFOPT_iTransparency followed.  */
/*    Channel independence now holds in STRAIGHT mode and is asserted there,  */
/*    beside a premultiplied check that the halo is gone.                     */
/*----------------------------------------------------------------------------*/
/* HOW A SEPARABLE RESAMPLER WORKS, because the rest of this file assumes it. */
/*                                                                            */
/* Resizing an image is done one AXIS at a time: the source is resampled      */
/* horizontally into an intermediate of the new width and the old height,     */
/* and that intermediate is resampled vertically into the output.  The two    */
/* passes are identical in form, which is why the contributor table below     */
/* knows nothing about images -- it is built once per axis and used for every */
/* row or column on that axis.                                                */
/*                                                                            */
/* AN OUTPUT PIXEL IS A WEIGHTED SUM OF INPUT PIXELS.  For output pixel n the */
/* centre in input coordinates is (n + 0.5) * invScale, and every input pixel */
/* within the filter's support of that centre contributes, weighted by the    */
/* kernel evaluated at the distance between them.  The half-pixel offsets are */
/* not decoration: a pixel is a SAMPLE AT THE CENTRE OF A CELL, and dropping  */
/* the 0.5 shifts the whole image by half an output pixel -- which looks      */
/* almost right, which is what makes it dangerous.                            */
/*                                                                            */
/* THE KERNEL IS MEASURED IN DIFFERENT UNITS IN THE TWO DIRECTIONS, and this  */
/* is the single easiest thing to get wrong.                                  */
/*                                                                            */
/*   UPSAMPLING -- more output pixels than input.  Each output pixel reads a  */
/*   few input pixels around it, so the kernel keeps its natural width and is */
/*   measured in INPUT pixels.                                                */
/*                                                                            */
/*   DOWNSAMPLING -- fewer output pixels than input.  Each output pixel must  */
/*   average a whole neighbourhood of input pixels or the result aliases, so  */
/*   the kernel is STRETCHED by the ratio and is measured in OUTPUT pixels.   */
/*   A kernel that was not stretched would sample a handful of input pixels   */
/*   and ignore everything between them, which is point sampling wearing a    */
/*   filter's name and which produces the shimmering that a 4x downscale is   */
/*   most likely to show.                                                     */
/*                                                                            */
/* EVERY OUTPUT PIXEL'S WEIGHTS SUM TO ONE, enforced by normalising after the */
/* kernel has been evaluated.  That is what makes a flat image stay flat at   */
/* every ratio and every filter, and it is the sharpest single check in the   */
/* whole piece -- almost no coefficient fault survives it.                    */
/*                                                                            */
/* EDGES CLAMP.  An output pixel near a border wants input pixels that do not */
/* exist; their weight is folded onto the nearest pixel that does, so the sum */
/* stays one and the border does not darken.  stb calls this STBIR_EDGE_CLAMP */
/* and it is the only edge mode this library exposes, because the frozen      */
/* surface has no parameter for another.                                      */
/*----------------------------------------------------------------------------*/

using System;

namespace GraphicsEncDec
{
    /*------------------------------------------------------------------------*/
    /* ResampleAxis:                                                          */
    /* The contributor table for ONE axis of one resize: for every output     */
    /* pixel, which input pixels feed it and with what weights.               */
    /*                                                                        */
    /* IT IS BUILT ONCE AND USED FOR EVERY ROW OR COLUMN, which is the whole  */
    /* point of a separable resampler and the reason this is a table rather   */
    /* than a calculation done per pixel.  A 512-wide resize evaluates the    */
    /* kernel a few thousand times here instead of a few million times in the */
    /* inner loop.                                                            */
    /*                                                                        */
    /* THE WEIGHTS ARE A RECTANGULAR TABLE, not a ragged one.  Every output   */
    /* pixel gets RSMPA_iMaxCount slots whether it needs them all or not, so  */
    /* the weights for output pixel n start at n * RSMPA_iMaxCount and the    */
    /* inner loop indexes without a second lookup.  The wasted slots are a    */
    /* few hundred floats and they buy a flat array in the hot path.          */
    /*                                                                        */
    /* IT IS PUBLIC FOR TESTABILITY, on the same footing as GifDecodeState    */
    /* and GifEncodeState: the test suite builds a table and checks its       */
    /* weights against hand-computed values, which is not reachable through   */
    /* RESAMP_TryResize.  That makes this type PUBLIC BUT NOT PART OF THE     */
    /* STABLE SURFACE -- it may change where RESAMP_TryResize may not.        */
    /*------------------------------------------------------------------------*/

    public sealed class ResampleAxis
    {
        public int     RSMPA_iInSize;                // input pixels on this axis
        public int     RSMPA_iOutSize;               // output pixels on this axis
        public int     RSMPA_iFilter;                // the RESAMP_FILTER_* this table was built with

        public int[]   RSMPA_arrFirst;               // one per output pixel: the first input pixel that contributes, always in range
        public int[]   RSMPA_arrCount;               // one per output pixel: how many consecutive input pixels contribute
        public float[] RSMPA_arrWeights;             // RSMPA_iOutSize * RSMPA_iMaxCount weights; output pixel n starts at n * RSMPA_iMaxCount
        public int     RSMPA_iMaxCount;              // the table's stride, and the widest contributor run any output pixel has
    }

    /*------------------------------------------------------------------------*/
    /* ImageResampler:                                                        */
    /* Image scaling: the coefficient tables, and the resize that uses them.  */
    /*                                                                        */
    /* PORTABILITY: pure array and floating-point work, no platform surface.  */
    /*------------------------------------------------------------------------*/

    public static class ImageResampler
    {
        /*--------------------------------------------------------------------*/
        /* THE FIVE FILTERS a caller may ask RESAMP_TryResize for.            */
        /*                                                                    */
        /* THE NUMBERS ARE stb's OWN, deliberately.  Its stbir_filter enum    */
        /* gives BOX 1, TRIANGLE 2, CUBICBSPLINE 3, CATMULLROM 4 and MITCHELL */
        /* 5, and our five names map onto those five kernels one for one      */
        /* checked before any of this was written.  Keeping the values        */
        /* identical means a reader comparing the two files is never doing    */
        /* arithmetic in their head, and it costs nothing.                    */
        /*                                                                    */
        /* ZERO IS DELIBERATELY NOT A FILTER.  stb reserves it for "pick one  */
        /* for me" and this library does not offer that -- so zero is the     */
        /* value an uninitialised variable holds, and it is REFUSED rather    */
        /* than quietly meaning BOX.  Same call GIFOPT_iPaletteSize makes.    */
        /*--------------------------------------------------------------------*/

        public const int RESAMP_FILTER_BOX        = 1;   // a trapezoid with one-pixel ramps; a true box at integer ratios
        public const int RESAMP_FILTER_TRIANGLE   = 2;   // linear; on upsampling this is bilinear interpolation
        public const int RESAMP_FILTER_CUBIC      = 3;   // the cubic B-spline, Mitchell-Netravali with B=1 C=0; smooth and blurring
        public const int RESAMP_FILTER_CATMULLROM = 4;   // an INTERPOLATING cubic spline; sharp, and exact at scale 1
        public const int RESAMP_FILTER_MITCHELL   = 5;   // Mitchell-Netravali with B=C=1/3; the usual compromise, and stb's downsample default

        /*--------------------------------------------------------------------*/
        /* A weight below this is treated as zero.                            */
        /*                                                                    */
        /* IT IS NOT AN ARBITRARY EPSILON: stb uses the same value to keep    */
        /* DENORMALS out of the inner loop.  A denormal float is one so close */
        /* to zero that the hardware handles it on a slow path, and a filter  */
        /* tail generates plenty of them -- so they are flushed at build time */
        /* rather than being multiplied a million times at run time.  The     */
        /* value is small enough that flushing one cannot move an eight-bit   */
        /* or sixteen-bit sample.                                             */
        /*--------------------------------------------------------------------*/

        public const float RESAMP_SMALL_WEIGHT = 0.000001f;   // below this, a contribution is dropped

        /*--------------------------------------------------------------------*/
        /* THE TWO ALPHA MODES RESAMP_TryResize accepts.                      */
        /*                                                                    */
        /* WHAT THEY DECIDE.  Resampling averages pixels, and the question is */
        /* what the average of a VISIBLE pixel and an INVISIBLE one should    */
        /* be.  Take opaque red (255,0,0,255) beside fully transparent blue   */
        /* (0,0,255,0), averaged half and half:                               */
        /*                                                                    */
        /*     STRAIGHT        (128, 0, 128, 128)   half-transparent PURPLE   */
        /*     PREMULTIPLIED   (255, 0, 0,   128)   half-transparent RED      */
        /*                                                                    */
        /* The blue pixel is invisible and should contribute nothing.         */
        /* Straight alpha averages RGB as though it were data even where      */
        /* coverage is zero; premultiplied averages LIGHT -- colour times     */
        /* coverage -- which is the quantity that physically exists.  The     */
        /* symptom of getting it wrong is a fringe of whatever colour sits    */
        /* under the transparent region, around every edge, on every          */
        /* downscale.                                                         */
        /*                                                                    */
        /* AND THIS LIBRARY FEEDS ITSELF THE TRIGGERING SHAPE.  GifCodec      */
        /* zero-fills its canvas, so every transparent pixel in a decoded GIF */
        /* carries RGB (0,0,0): decode a transparent GIF here, resize it here */
        /* with STRAIGHT, and everything gets a black halo.                   */
        /*                                                                    */
        /* STRAIGHT IS KEPT BECAUSE ALPHA IS NOT ALWAYS COVERAGE.  Where it   */
        /* is a MASK over real data, the RGB beneath alpha 0 is meaningful    */
        /* and premultiplying destroys it irrecoverably.  That caller knows   */
        /* who they are; the caller with an icon does not, which is why the   */
        /* default points the other way.                                      */
        /*                                                                    */
        /* ZERO IS A LEGAL VALUE HERE AND IS REFUSED FOR iFilter A HUNDRED    */
        /* LINES ABOVE, AND THE INCONSISTENCY IS DELIBERATE.  An              */
        /* uninitialised FILTER has no correct meaning, so reading it as one  */
        /* would be inventing an answer -- the same call GIFOPT_iPaletteSize  */
        /* makes about a palette of zero colours.  An uninitialised ALPHA     */
        /* MODE does have a correct meaning: it says the caller has not       */
        /* thought about alpha, and the right thing to hand them is the       */
        /* behaviour that is correct when you have not.  So zero is not an    */
        /* unnamed sentinel here, it is a named constant that happens to be   */
        /* the default.                                                       */
        /*--------------------------------------------------------------------*/

        public const int RESAMP_ALPHA_PREMULTIPLIED = 0;   // the DEFAULT; correct for coverage alpha
        public const int RESAMP_ALPHA_STRAIGHT      = 1;   // every channel independent, alpha included

        /*--------------------------------------------------------------------*/
        /* Below this many counts of alpha, a pixel is not un-premultiplied   */
        /* and its colour is written as zero.                                 */
        /*                                                                    */
        /* THE VALUE IS NOT ARBITRARY: it is the point at which the output    */
        /* ALPHA ITSELF ROUNDS TO ZERO.  RESAMP_ClampToByte and its 16-bit    */
        /* twin add a half and truncate, so any alpha below 0.5 is stored as  */
        /* 0 and the pixel is invisible however its colour is filled in.      */
        /* Dividing by an alpha that small would amplify float noise into a   */
        /* bright speckle nobody can see but a later compositing step might.  */
        /* Above it the division is well behaved -- at alpha 0.6 the          */
        /* premultiplied colour cannot exceed 0.6, so the quotient cannot     */
        /* exceed full scale.                                                 */
        /*--------------------------------------------------------------------*/

        private const float RESAMP_ALPHA_FLOOR = 0.5f;   // below this the stored alpha is 0 anyway

        /*--------------------------------------------------------------------*/
        /* Dimension limits.  The upper one is ImageBuffer's own; the lower   */
        /* one is that an axis of zero pixels has no centre to sample.        */
        /*--------------------------------------------------------------------*/

        public const int RESAMP_MIN_DIMENSION = 1;                                // a one-pixel axis is legal, and is exercised deliberately
        public const int RESAMP_MAX_DIMENSION = ImageBuffer.IMG_MAX_DIMENSION;    // shared with the buffer, so the two cannot disagree

        /*--------------------------------------------------------------------*/
        /* RESAMP_FilterKernel:                                               */
        /*                                                                    */
        /* Evaluates one of the five filters at a distance from its centre.   */
        /*                                                                    */
        /* ALL FIVE ARE EVEN FUNCTIONS -- f(-x) equals f(x) -- which is why   */
        /* each folds a negative argument before doing anything else, and why */
        /* the callers below never care which side of the centre a sample     */
        /* falls on.  A kernel that was accidentally asymmetric would shift   */
        /* the image by a fraction of a pixel, so the symmetry is asserted    */
        /* directly rather than trusted.                                      */
        /*                                                                    */
        /* THE SCALE ARGUMENT IS USED BY EXACTLY ONE OF THEM.  Only the box   */
        /* trapezoid changes shape with the ratio; the other four are fixed   */
        /* curves and ignore it.  It is still passed to all five, because     */
        /* that is stb's signature and a caller should not have to know which */
        /* kernels care.                                                      */
        /*                                                                    */
        /* Transcoded from stb_image_resize2.h's stbir__filter_trapezoid,     */
        /* _triangle, _cubic, _catmullrom and _mitchell.                      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iFilter  : One of the RESAMP_FILTER_* constants.               */
        /*     fX       : Distance from the kernel's centre, in the units the */
        /*                caller is working in -- input pixels when           */
        /*                upsampling, output pixels when downsampling.        */
        /*     fScale   : The ratio the box filter widens by.  Ignored by the */
        /*                other four.                                         */
        /*                                                                    */
        /* Returns:                                                           */
        /*     float    : The weight, which may be NEGATIVE for Catmull-Rom   */
        /*                and Mitchell -- that is what makes them sharpen.    */
        /*--------------------------------------------------------------------*/

        public static float RESAMP_FilterKernel(int iFilter, float fX, float fScale)
        {
            float fHalf;
            float fTop;
            float fFlat;

            if (fX < 0.0f)
            {   /* Every kernel here is even, so only the magnitude matters. */
                fX = -fX;
            }

            if (iFilter == RESAMP_FILTER_BOX)
            {   /* A TRAPEZOID, not a rectangle, and the difference matters    */
                /* at non-integer ratios.  A true box would include or         */
                /* exclude a whole input pixel depending on which side of a    */
                /* boundary its centre fell, so a 2.5x downscale would show    */
                /* rows alternating between two brightnesses.  Ramping the     */
                /* edges over one pixel makes the transition continuous.  At   */
                /* an INTEGER ratio the ramps land exactly on pixel            */
                /* boundaries and the result is a true box, which is why the   */
                /* name is still honest.                                       */
                fHalf = fScale / 2.0f;
                fTop  = 0.5f + fHalf;

                if (fX >= fTop)
                {   /* Outside the trapezoid entirely. */
                    return(0.0f);
                }

                fFlat = 0.5f - fHalf;

                if (fX <= fFlat)
                {   /* On the flat top, where the weight is full. */
                    return(1.0f);
                }

                /* On one of the ramps, falling linearly to zero at fTop. */
                return((fTop - fX) / fScale);
            }

            if (iFilter == RESAMP_FILTER_TRIANGLE)
            {   /* Linear falloff over one pixel.  On upsampling this is   */
                /* exactly bilinear interpolation.                         */
                if (fX <= 1.0f)
                {   /* Inside the support. */
                    return(1.0f - fX);
                }

                /* Outside; contributes nothing. */
                return(0.0f);
            }

            if (iFilter == RESAMP_FILTER_CUBIC)
            {   /* The cubic B-spline.  It never goes negative, so it cannot   */
                /* overshoot or ring -- and it does not pass through the       */
                /* source samples either, so it BLURS even at scale 1.  That   */
                /* is a property of the curve rather than a defect, and is     */
                /* asserted rather than left to look like one.                 */
                if (fX < 1.0f)
                {   /* The central lobe. */
                    return((4.0f + (fX * fX * ((3.0f * fX) - 6.0f))) / 6.0f);
                }

                if (fX < 2.0f)
                {   /* The outer lobe, falling to zero at two. */
                    return((8.0f + (fX * (-12.0f + (fX * (6.0f - fX))))) / 6.0f);
                }

                /* Outside the support. */
                return(0.0f);
            }

            if (iFilter == RESAMP_FILTER_CATMULLROM)
            {   /* An INTERPOLATING spline: it passes exactly through the   */
                /* source samples, so at scale 1 it reproduces the image    */
                /* unchanged.  Its outer lobe is negative, which sharpens   */
                /* edges and can overshoot into ringing on a hard one.      */
                if (fX < 1.0f)
                {   /* The central lobe. */
                    return(1.0f - (fX * fX * (2.5f - (1.5f * fX))));
                }

                if (fX < 2.0f)
                {   /* The negative outer lobe. */
                    return(2.0f - (fX * (4.0f + (fX * ((0.5f * fX) - 2.5f)))));
                }

                /* Outside the support. */
                return(0.0f);
            }

            if (iFilter == RESAMP_FILTER_MITCHELL)
            {   /* Mitchell-Netravali with B = C = 1/3, the compromise the      */
                /* authors chose to balance blurring against ringing.  Mildly   */
                /* negative outer lobe, so mildly sharpening.  stb makes it     */
                /* the downsampling default and so would we if the frozen       */
                /* surface had a default to make.                               */
                if (fX < 1.0f)
                {   /* The central lobe. */
                    return((16.0f + (fX * fX * ((21.0f * fX) - 36.0f))) / 18.0f);
                }

                if (fX < 2.0f)
                {   /* The outer lobe. */
                    return((32.0f + (fX * (-60.0f + (fX * (36.0f - (7.0f * fX)))))) /
                           18.0f);
                }

                /* Outside the support. */
                return(0.0f);
            }

            /* NOT A FILTER THIS LIBRARY KNOWS.  Returning zero rather than     */
            /* throwing, because every caller validated the filter before the   */
            /* loop that reaches here -- a throw from an inner loop would be    */
            /* a worse answer than a weight that normalises away, and           */
            /* RESAMP_TryBuildAxis refuses an unknown filter up front.          */
            return(0.0f);
        }

        /*--------------------------------------------------------------------*/
        /* RESAMP_FilterSupport:                                              */
        /*                                                                    */
        /* How far from its centre a filter reaches before it is identically  */
        /* zero, which is what decides how many input pixels contribute.      */
        /*                                                                    */
        /* GET THIS TOO SMALL AND THE FILTER IS SILENTLY TRUNCATED -- the     */
        /* image is subtly wrong and the weights still sum to one, because    */
        /* normalisation hides the missing tail.  Get it too LARGE and        */
        /* nothing is wrong at all, only slower.  So the support is checked   */
        /* against the kernel directly: just outside it, the kernel           */
        /* must actually be zero.                                             */
        /*                                                                    */
        /* Transcoded from stb_image_resize2.h's stbir__support_trapezoid,    */
        /* _one and _two.                                                     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iFilter  : One of the RESAMP_FILTER_* constants.               */
        /*     fScale   : The ratio the box filter widens by.  Ignored by the */
        /*                other four.                                         */
        /*                                                                    */
        /* Returns:                                                           */
        /*     float    : The radius, in the same units the kernel takes.     */
        /*--------------------------------------------------------------------*/

        public static float RESAMP_FilterSupport(int iFilter, float fScale)
        {
            if (iFilter == RESAMP_FILTER_BOX)
            {   /* Half the flat top plus half a pixel of ramp on each side. */
                return(0.5f + (fScale / 2.0f));
            }

            if (iFilter == RESAMP_FILTER_TRIANGLE)
            {   /* Reaches exactly one pixel. */
                return(1.0f);
            }

            if (iFilter == RESAMP_FILTER_CUBIC || iFilter == RESAMP_FILTER_CATMULLROM ||
                iFilter == RESAMP_FILTER_MITCHELL)
            {   /* All three cubics have a second lobe and reach two pixels. */
                return(2.0f);
            }

            /* Not a filter this library knows; the caller refused it already. */
            return(0.0f);
        }

        /*--------------------------------------------------------------------*/
        /* RESAMP_IsKnownFilter:                                              */
        /*                                                                    */
        /* Says whether a value names one of the five filters.                */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iFilter  : The value to check.                                 */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool     : true when it is a RESAMP_FILTER_* constant.         */
        /*--------------------------------------------------------------------*/

        public static bool RESAMP_IsKnownFilter(int iFilter)
        {
            /* The five are consecutive, which is stb's numbering and is the   */
            /* only reason a range test is honest here rather than a list.     */
            return(iFilter >= RESAMP_FILTER_BOX && iFilter <= RESAMP_FILTER_MITCHELL);
        }

        /*--------------------------------------------------------------------*/
        /* RESAMP_FilterName:                                                 */
        /*                                                                    */
        /* A short name for a filter, for refusal messages and diagnostic     */
        /* reports.                                                           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iFilter  : One of the RESAMP_FILTER_* constants.               */
        /*                                                                    */
        /* Returns:                                                           */
        /*     string   : The name, or "unknown" for anything else.           */
        /*--------------------------------------------------------------------*/

        public static string RESAMP_FilterName(int iFilter)
        {
            if (iFilter == RESAMP_FILTER_BOX)
            {   /* The trapezoid. */
                return("box");
            }

            if (iFilter == RESAMP_FILTER_TRIANGLE)
            {   /* Linear. */
                return("triangle");
            }

            if (iFilter == RESAMP_FILTER_CUBIC)
            {   /* The B-spline. */
                return("cubic");
            }

            if (iFilter == RESAMP_FILTER_CATMULLROM)
            {   /* The interpolating spline. */
                return("catmull-rom");
            }

            if (iFilter == RESAMP_FILTER_MITCHELL)
            {   /* B = C = 1/3. */
                return("mitchell");
            }

            /* Not one of the five. */
            return("unknown");
        }

        /*--------------------------------------------------------------------*/
        /* RESAMP_TryBuildAxis:                                               */
        /*                                                                    */
        /* Builds the contributor table for ONE axis: for every output pixel, */
        /* which input pixels feed it and with what weights.                  */
        /*                                                                    */
        /* THIS TABLE IS THE HEART OF THE RESAMPLER.  Once                    */
        /* this table is right, the two passes that use it are loops.         */
        /*                                                                    */
        /* THE FOUR STEPS, in the order stb does them and for the reasons its */
        /* source and this file's header give:                                */
        /*                                                                    */
        /*   1. CENTRE.  Output pixel n samples input coordinate              */
        /*      (n + 0.5) * invScale.  The half-pixels make the centres of    */
        /*      cells line up; dropping them shifts the image by half an      */
        /*      output pixel.                                                 */
        /*                                                                    */
        /*   2. RANGE.  Every input pixel whose centre is within the support  */
        /*      of that point contributes.  The range is computed UNCLAMPED,  */
        /*      so near a border it runs off the end of the image -- that is  */
        /*      deliberate and step 4 deals with it.                          */
        /*                                                                    */
        /*   3. NORMALISE.  Divide by the total so the weights sum to one.    */
        /*      Done BEFORE the edge fold, which is stb's order.  Folding     */
        /*      first would give the same answer -- folding moves weight      */
        /*      around without changing the total -- but following the source */
        /*      keeps the two diffable, and the equivalence is worth knowing  */
        /*      rather than worrying about.                                   */
        /*                                                                    */
        /*   4. FOLD THE EDGE.  A weight belonging to an input pixel that     */
        /*      does not exist is added to the nearest one that does.  The    */
        /*      sum stays one, so the border neither darkens nor brightens.   */
        /*                                                                    */
        /* THE UNITS DIFFER BETWEEN THE TWO DIRECTIONS and that is the single */
        /* easiest thing to get wrong here; this file's header explains why.  */
        /* Upsampling measures the kernel in INPUT pixels; downsampling       */
        /* stretches it and measures in OUTPUT pixels.                        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iInSize   : Input pixels on this axis; at least one.           */
        /*     iOutSize  : Output pixels on this axis; at least one.          */
        /*     iFilter   : One of the RESAMP_FILTER_* constants.              */
        /*     axisOut   : Receives the table, or null on failure.            */
        /*     sReason   : Receives "" or why it was refused.                 */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool      : true when axisOut holds a usable table.            */
        /*--------------------------------------------------------------------*/

        public static bool RESAMP_TryBuildAxis(int iInSize, int iOutSize, int iFilter,
                                               out ResampleAxis axisOut, out string sReason)
        {
            ResampleAxis axis;
            float[]      arrRaw;
            float        fScale;
            float        fInvScale;
            float        fKernelScale;
            float        fSupportIn;
            float        fCentre;
            float        fOffset;
            float        fWeight;
            float        fTotal;
            int          iMaxCount;
            int          iFirst;
            int          iLast;
            int          iRunFirst;
            int          iRunLast;
            int          iClamped;
            int          iAt;
            int          iCount;
            int          n;
            int          i;

            axisOut = null;
            sReason = "";

            if (iInSize < RESAMP_MIN_DIMENSION || iInSize > RESAMP_MAX_DIMENSION)
            {   /* An axis of no pixels has no centres to sample. */
                sReason = "resample: an input axis of " + iInSize.ToString() +
                          " pixels is outside the range " +
                          RESAMP_MIN_DIMENSION.ToString() + " to " +
                          RESAMP_MAX_DIMENSION.ToString();
                return(false);
            }

            if (iOutSize < RESAMP_MIN_DIMENSION || iOutSize > RESAMP_MAX_DIMENSION)
            {   /* Likewise for the output. */
                sReason = "resample: an output axis of " + iOutSize.ToString() +
                          " pixels is outside the range " +
                          RESAMP_MIN_DIMENSION.ToString() + " to " +
                          RESAMP_MAX_DIMENSION.ToString();
                return(false);
            }

            if (!RESAMP_IsKnownFilter(iFilter))
            {   /* Zero lands here, which is the point: an uninitialised     */
                /* filter is a caller mistake worth saying out loud rather   */
                /* than quietly meaning box.                                 */
                sReason = "resample: filter " + iFilter.ToString() +
                          " is not one of RESAMP_FILTER_BOX, _TRIANGLE, _CUBIC, " +
                          "_CATMULLROM or _MITCHELL";
                return(false);
            }

            fScale    = (float)iOutSize / (float)iInSize;
            fInvScale = (float)iInSize / (float)iOutSize;

            if (fScale >= 1.0f)
            {   /* UPSAMPLING.  The kernel keeps its natural width and the      */
                /* offsets below are measured in INPUT pixels, so a unit step   */
                /* along the input is a unit step for the kernel.               */
                fKernelScale = 1.0f;
                fSupportIn   = RESAMP_FilterSupport(iFilter, fInvScale);
            }
            else
            {   /* DOWNSAMPLING.  The kernel is stretched to cover the whole   */
                /* input neighbourhood an output pixel averages, so the        */
                /* offsets are converted to OUTPUT pixels before the kernel    */
                /* sees them -- which is what multiplying by fScale does       */
                /* below -- and the support correspondingly widens in input    */
                /* pixels.                                                     */
                fKernelScale = fScale;
                fSupportIn   = RESAMP_FilterSupport(iFilter, fScale) * fInvScale;
            }

            /* THE WIDEST ANY OUTPUT PIXEL CAN REACH, plus a slot of headroom   */
            /* for the rounding in the range arithmetic.  This is the table's   */
            /* stride, so it is computed once from the support rather than      */
            /* discovered by growing an array.                                  */
            iMaxCount = (int)Math.Ceiling((double)(fSupportIn * 2.0f)) + 2;

            if (iMaxCount > iInSize)
            {   /* A filter wider than the whole image is legal -- it happens   */
                /* on any large downscale -- but it can never touch more        */
                /* pixels than exist, and capping keeps the table small.        */
                iMaxCount = iInSize;
            }

            axis = new ResampleAxis();

            axis.RSMPA_iInSize    = iInSize;
            axis.RSMPA_iOutSize   = iOutSize;
            axis.RSMPA_iFilter    = iFilter;
            axis.RSMPA_iMaxCount  = iMaxCount;
            axis.RSMPA_arrFirst   = new int[iOutSize];
            axis.RSMPA_arrCount   = new int[iOutSize];
            axis.RSMPA_arrWeights = new float[iOutSize * iMaxCount];

            arrRaw = new float[iMaxCount];

            for (n = 0; n < iOutSize; n++)
            {   /* One output pixel per iteration. */

                /* STEP 1: where this output pixel sits in input coordinates. */
                fCentre = ((float)n + 0.5f) * fInvScale;

                /* STEP 2: the unclamped range of input pixels within reach.    */
                /* The +0.5 and -0.5 convert a coordinate span into the pixel   */
                /* INDICES whose centres fall inside it.                        */
                iFirst = (int)Math.Floor((double)(fCentre - fSupportIn + 0.5f));
                iLast  = (int)Math.Floor((double)(fCentre + fSupportIn - 0.5f));

                if (iLast < iFirst)
                {   /* A support narrower than one pixel can leave the two   */
                    /* crossed, which stb guards the same way.  One pixel    */
                    /* always contributes.                                   */
                    iLast = iFirst;
                }

                if ((iLast - iFirst + 1) > iMaxCount)
                {   /* Never exceed the stride the table was sized for.  This   */
                    /* can only bite when the cap above trimmed iMaxCount to    */
                    /* the image width.                                         */
                    iLast = iFirst + iMaxCount - 1;
                }

                /* Evaluate the kernel across that range, in the units this   */
                /* direction works in.                                        */
                fTotal = 0.0f;
                iCount = iLast - iFirst + 1;

                for (i = 0; i < iCount; i++)
                {   /* One candidate input pixel per iteration. */
                    fOffset = (fCentre - ((float)(iFirst + i) + 0.5f)) * fKernelScale;
                    fWeight = RESAMP_FilterKernel(iFilter, fOffset, fKernelScale);

                    if (fWeight < RESAMP_SMALL_WEIGHT && fWeight > -RESAMP_SMALL_WEIGHT)
                    {   /* Flush a denormal tail to a true zero. */
                        fWeight = 0.0f;
                    }

                    arrRaw[i] = fWeight;
                    fTotal    = fTotal + fWeight;
                }

                /* STEP 3: normalise, so the weights sum to one and a flat   */
                /* image stays flat.                                         */
                if (fTotal > RESAMP_SMALL_WEIGHT || fTotal < -RESAMP_SMALL_WEIGHT)
                {   /* An ordinary filter footprint. */
                    for (i = 0; i < iCount; i++)
                    {   /* One weight per iteration. */
                        arrRaw[i] = arrRaw[i] / fTotal;
                    }
                }
                else
                {   /* EVERY WEIGHT CANCELLED, which the cubics with negative   */
                    /* lobes can just about manage on a degenerate footprint.   */
                    /* Dividing by that total would produce infinities, so      */
                    /* the pixel falls back to taking its centre sample whole   */
                    /* -- which is what the filter was trying to approximate    */
                    /* anyway.                                                  */
                    for (i = 0; i < iCount; i++)
                    {   /* Clear the cancelled weights. */
                        arrRaw[i] = 0.0f;
                    }

                    arrRaw[0] = 1.0f;
                }

                /* STEP 4: fold anything outside the image onto the nearest   */
                /* pixel inside it, then write the run into the table.        */
                for (i = 0; i < iMaxCount; i++)
                {   /* Clear this output pixel's slots before accumulating. */
                    axis.RSMPA_arrWeights[(n * iMaxCount) + i] = 0.0f;
                }

                /* THE RUN IS THE CLAMPED RANGE.  iFirst and iLast are kept    */
                /* UNTOUCHED here and the clamped pair gets its own names --   */
                /* the fold below has to map each weight from its ORIGINAL     */
                /* input index onto its clamped home, so overwriting the       */
                /* original base would silently shift every weight near a      */
                /* border.  That is a one-character mistake that produces a    */
                /* picture which is right in the middle and wrong at the       */
                /* edges, so the two are named apart rather than reused.       */
                iRunFirst = RESAMP_ClampIndex(iFirst, iInSize);
                iRunLast  = RESAMP_ClampIndex(iLast, iInSize);

                axis.RSMPA_arrFirst[n] = iRunFirst;
                axis.RSMPA_arrCount[n] = iRunLast - iRunFirst + 1;

                for (i = 0; i < iCount; i++)
                {   /* One weight per iteration, onto its clamped home.  The   */
                    /* offset cannot fall outside the run: the clamp lands     */
                    /* every index in 0 .. iInSize-1, and the run spans from   */
                    /* the clamped first to the clamped last, so it contains   */
                    /* every clamped value by construction.                    */
                    iClamped = RESAMP_ClampIndex(iFirst + i, iInSize);
                    iAt      = iClamped - iRunFirst;

                    axis.RSMPA_arrWeights[(n * iMaxCount) + iAt] =
                        axis.RSMPA_arrWeights[(n * iMaxCount) + iAt] + arrRaw[i];
                }
            }

            axisOut = axis;

            /* A contributor table every row or column on this axis can use. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* RESAMP_ClampIndex:                                                 */
        /*                                                                    */
        /* Brings a pixel index inside the image, which is the whole of the   */
        /* CLAMP edge mode.                                                   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iIndex  : The index, possibly outside the image.               */
        /*     iSize   : The axis length in pixels.                           */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int     : The index, brought into 0 .. iSize-1.                */
        /*--------------------------------------------------------------------*/

        public static int RESAMP_ClampIndex(int iIndex, int iSize)
        {
            if (iIndex < 0)
            {   /* Off the top or left. */
                return(0);
            }

            if (iIndex >= iSize)
            {   /* Off the bottom or right. */
                return(iSize - 1);
            }

            /* Already inside. */
            return(iIndex);
        }

        /*--------------------------------------------------------------------*/
        /*                                                                    */
        /*   THE RESIZE.  Everything above is per-axis arithmetic             */
        /*   with no image in it; everything below moves pixels.              */
        /*                                                                    */
        /*--------------------------------------------------------------------*/

        /*--------------------------------------------------------------------*/
        /* RESAMP_TryResize:                                                  */
        /*                                                                    */
        /* Scales an image to a new size with one of the five filters.  This  */
        /* is the resampler's entire consumer API -- everything else in this  */
        /* file is public only so the test suite can reach it.                */
        /*                                                                    */
        /* HOW IT WORKS, IN ONE PARAGRAPH.  A separable filter lets the two   */
        /* axes be done one at a time.  The source is resampled HORIZONTALLY  */
        /* into a float intermediate of the new width and the OLD height, and */
        /* that intermediate is resampled VERTICALLY into the output.  Both   */
        /* passes read a contributor table built once per axis by             */
        /* RESAMP_TryBuildAxis, which is why the kernel is evaluated a few    */
        /* thousand times in this function and not a few million times in the */
        /* inner loops.                                                       */
        /*                                                                    */
        /* THE INPUT IS NEVER WRITTEN TO and the output is always a fresh     */
        /* buffer, so a caller may keep using the source afterwards and may   */
        /* resize the same image repeatedly.                                  */
        /*                                                                    */
        /* WHY THERE IS NO SHORTCUT FOR "SAME SIZE".  Asking for the size the */
        /* image already has runs the full pipeline, and that is correct      */
        /* rather than lazy: CUBIC and MITCHELL are BLURRING kernels and are  */
        /* meant to blur at scale 1, so returning a copy would silently give  */
        /* a caller a different answer from the one the filter names.  Both   */
        /* halves of that are asserted -- TRIANGLE and CATMULLROM must come   */
        /* back identical, CUBIC and MITCHELL must not.                       */
        /*                                                                    */
        /* THE RESULT KEEPS THE SOURCE'S DEPTH AND CHANNEL COUNT.  Resizing   */
        /* is a geometry operation; narrowing 16 bits to 8 on the way through */
        /* would be a second, invisible decision, and a caller who wants it   */
        /* has ImageBuffer's own converter.                                   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     imgIn       : The image to scale.  Eight- OR sixteen-bit       */
        /*                   samples, 1 to 4 channels.  Not modified.         */
        /*     iNewWidth   : Width of the result in pixels; at least 1.       */
        /*     iNewHeight  : Height of the result in pixels; at least 1.      */
        /*     iFilter     : One of the RESAMP_FILTER_* constants.  Zero is   */
        /*                   refused rather than quietly defaulted.           */
        /*     iAlphaMode  : RESAMP_ALPHA_PREMULTIPLIED (0, the default and   */
        /*                   the right answer for coverage alpha) or          */
        /*                   RESAMP_ALPHA_STRAIGHT (1).  Has no effect on an  */
        /*                   image with no alpha channel, and is ACCEPTED     */
        /*                   rather than refused there, so a caller resizing  */
        /*                   a mixed set need not vary it per image.          */
        /*     imgOut      : Receives the scaled image, or null on failure.   */
        /*     sReason     : Receives "" or why the resize was refused.       */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool        : true when imgOut holds the scaled image; false   */
        /*                   when nothing was allocated and sReason says why. */
        /*--------------------------------------------------------------------*/

        public static bool RESAMP_TryResize(ImageBuffer imgIn, int iNewWidth,
                                            int iNewHeight, int iFilter,
                                            int iAlphaMode,
                                            out ImageBuffer imgOut, out string sReason)
        {
            ResampleAxis axisWide;
            ResampleAxis axisTall;
            ImageBuffer  imgNew;
            float[]      arrMiddle;
            string       sInner;
            long         lMiddleCount;
            float        fAlphaMax;
            bool         bPremultiply;
            int          iAlphaChannel;
            int          iChannels;

            /* Fail closed, the way every other entry point in this library   */
            /* does: the out parameters are set before the first test runs.   */
            imgOut  = null;
            sReason = "";

            if (imgIn == null)
            {   /* A null buffer is a caller mistake rather than bad data,     */
                /* but it is REPORTED rather than thrown, because a consumer   */
                /* checking one bool is the contract this library offers and   */
                /* a single exception escaping it would break that promise.    */
                sReason = "resample: no image was supplied";
                return(false);
            }

            if (!ImageBuffer.IMG_TryValidate(imgIn, out sInner))
            {   /* Malformed geometry, a channel count outside 1 to 4, or the   */
                /* wrong sample array live for the declared depth.  Validate    */
                /* rather than trust, because the two passes below index off    */
                /* these fields and a lie in any of them reads out of bounds.   */
                sReason = "resample: the source image is not usable: " + sInner;
                return(false);
            }

            /* THERE IS NO DEPTH REFUSAL HERE ANY MORE.  There was one --      */
            /* "this resampler works on 8-bit samples" -- and it was DELETED   */
            /* rather than edited, which is the whole reason that              */
            /* refusal was written to name the CAPABILITY and never the        */
            /* sub-step: a sentence that names a piece of work goes quietly    */
            /* stale when the work lands, and nothing complains.  This         */
            /* comment is the only trace, and it is here so the next person    */
            /* to add a temporary refusal writes it the same way.              */

            if (iAlphaMode != RESAMP_ALPHA_PREMULTIPLIED &&
                iAlphaMode != RESAMP_ALPHA_STRAIGHT)
            {   /* Zero IS legal here, unlike the filter -- see the constants   */
                /* above for why the two differ.  Anything else is a caller     */
                /* mistake worth naming rather than rounding toward a mode.     */
                sReason = "resample: alpha mode " + iAlphaMode.ToString() +
                          " is not RESAMP_ALPHA_PREMULTIPLIED or " +
                          "RESAMP_ALPHA_STRAIGHT";
                return(false);
            }

            /* THE AXIS BUILDER IS THE ONE VALIDATOR FOR SIZES AND FILTERS,     */
            /* so a bad new width, a bad new height and an unknown filter all   */
            /* refuse in the same words here as they do when a caller drives    */
            /* RESAMP_TryBuildAxis directly.  Two call sites, one set           */
            /* of messages, and no second copy of the bounds to drift.          */
            if (!RESAMP_TryBuildAxis(imgIn.IMG_iWidth, iNewWidth, iFilter,
                                     out axisWide, out sInner))
            {   /* The horizontal axis was refused; its reason already names   */
                /* which bound or which filter value was at fault.             */
                sReason = sInner;
                return(false);
            }

            if (!RESAMP_TryBuildAxis(imgIn.IMG_iHeight, iNewHeight, iFilter,
                                     out axisTall, out sInner))
            {   /* Likewise the vertical axis. */
                sReason = sInner;
                return(false);
            }

            iChannels = imgIn.IMG_iChannels;

            /* WHICH CHANNEL IS ALPHA, OR -1 WHEN THERE IS NONE.  Exactly two   */
            /* of the four layouts carry one and in both it is LAST:            */
            /* GRAY_ALPHA is luminance then alpha, RGBA is red green blue       */
            /* then alpha.  Deriving it rather than branching on the channel    */
            /* count keeps the two cases identical in the passes below --       */
            /* premultiplying a grayscale image's one colour channel is the     */
            /* same operation as premultiplying three.                          */
            if (iChannels == ImageBuffer.IMG_CHANNELS_GRAY_ALPHA ||
                iChannels == ImageBuffer.IMG_CHANNELS_RGBA)
            {   /* The last channel is coverage. */
                iAlphaChannel = iChannels - 1;
            }
            else
            {   /* One or three channels: no alpha, so the mode cannot   */
                /* matter and the passes take the plain path.            */
                iAlphaChannel = -1;
            }

            /* Premultiply only when there is something to premultiply BY.   */
            /* An image with no alpha takes the straight path whatever the   */
            /* caller asked for, which is why the mode is accepted rather    */
            /* than refused on a 1- or 3-channel image.                      */
            bPremultiply = (iAlphaMode == RESAMP_ALPHA_PREMULTIPLIED) &&
                           (iAlphaChannel >= 0);

            /* Full scale for this depth, which is what the premultiply   */
            /* divides by and the un-premultiply multiplies back.         */
            if (imgIn.IMG_iBitsPerSample == ImageBuffer.IMG_BITS_8)
            {   /* Eight-bit samples run 0 to 255. */
                fAlphaMax = 255.0f;
            }
            else
            {   /* Sixteen-bit samples run 0 to 65535. */
                fAlphaMax = 65535.0f;
            }

            /* THE INTERMEDIATE CARRIES ITS OWN OVERFLOW RISK, and it is NOT    */
            /* the one IMG_TryAllocate guards.  That check covers the OUTPUT    */
            /* geometry; this array is the NEW width by the OLD height, a       */
            /* product neither buffer ever holds.  Upscaling a tall image       */
            /* horizontally is exactly the shape that makes it the largest      */
            /* of the three, so it is checked in long arithmetic before it is   */
            /* narrowed -- a wrapped count would allocate a small array that    */
            /* the first pass then runs off the end of.                         */
            lMiddleCount = (long)iNewWidth * (long)imgIn.IMG_iHeight *
                           (long)iChannels;

            if (lMiddleCount > (long)int.MaxValue)
            {   /* Individually legal sizes whose product does not fit an   */
                /* array index.                                             */
                sReason = "resample: the intermediate needs " +
                          lMiddleCount.ToString() +
                          " samples, which exceeds the maximum array length";
                return(false);
            }

            arrMiddle = new float[(int)lMiddleCount];

            /* THE DEPTH IS DECIDED ONCE, HERE AND AT THE VERTICAL PASS, and   */
            /* never inside a loop.  Branching per sample would cost a test    */
            /* on the hottest line in the file to save two function bodies     */
            /* that differ only in an array type -- which is the trade C       */
            /* makes with a macro, C++ with a template, and C# 5 with          */
            /* neither.  Duplication is the honest answer at this size.        */
            if (imgIn.IMG_iBitsPerSample == ImageBuffer.IMG_BITS_8)
            {   /* Eight-bit source. */
                RESAMP_PassHorizontal8(imgIn.IMG_arrSamples8, imgIn.IMG_iWidth,
                                       imgIn.IMG_iHeight, iChannels, axisWide,
                                       bPremultiply, iAlphaChannel, fAlphaMax,
                                       arrMiddle);
            }
            else
            {   /* Sixteen-bit source.  IMG_TryValidate has already confirmed   */
                /* the matching array is the live one, so no null check is      */
                /* needed on either branch.                                     */
                RESAMP_PassHorizontal16(imgIn.IMG_arrSamples16, imgIn.IMG_iWidth,
                                        imgIn.IMG_iHeight, iChannels, axisWide,
                                        bPremultiply, iAlphaChannel, fAlphaMax,
                                        arrMiddle);
            }

            if (!ImageBuffer.IMG_TryAllocate(iNewWidth, iNewHeight, iChannels,
                                             imgIn.IMG_iBitsPerSample, out imgNew,
                                             out sInner))
            {   /* The two axes accepted the sizes one at a time; only their   */
                /* product can still be refused here.  THE OUTPUT KEEPS THE    */
                /* SOURCE'S DEPTH: resizing is a geometry operation and has    */
                /* no business narrowing or widening a sample.                 */
                sReason = "resample: the output buffer could not be allocated: " +
                          sInner;
                return(false);
            }

            if (imgIn.IMG_iBitsPerSample == ImageBuffer.IMG_BITS_8)
            {   /* Eight-bit output, clamped to 0..255. */
                RESAMP_PassVertical8(arrMiddle, iNewWidth, imgIn.IMG_iHeight,
                                     iChannels, axisTall, bPremultiply,
                                     iAlphaChannel, fAlphaMax,
                                     imgNew.IMG_arrSamples8);
            }
            else
            {   /* Sixteen-bit output, clamped to 0..65535. */
                RESAMP_PassVertical16(arrMiddle, iNewWidth, imgIn.IMG_iHeight,
                                      iChannels, axisTall, bPremultiply,
                                      iAlphaChannel, fAlphaMax,
                                      imgNew.IMG_arrSamples16);
            }

            imgOut = imgNew;

            /* A new buffer of exactly the requested geometry, with the same   */
            /* channel count and depth as the source.                          */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* RESAMP_PassHorizontal8:                                            */
        /*                                                                    */
        /* The first pass over an EIGHT-BIT source: resamples every row       */
        /* across, writing the weighted sums into the float intermediate      */
        /* UNROUNDED.                                                         */
        /*                                                                    */
        /* THE SUMS ARE LEFT AS FLOATS ON PURPOSE.  Rounding here and again   */
        /* in the second pass would throw away up to half a count twice, and  */
        /* on a gradient that shows as banding.  One rounding, at the very    */
        /* end, is the whole reason the intermediate is not a byte array.     */
        /*                                                                    */
        /* THE ROW COUNT DOES NOT CHANGE in this pass -- only the width does  */
        /* -- so the intermediate is iOutWidth by iSrcHeight.                 */
        /*                                                                    */
        /* ITS SIXTEEN-BIT TWIN IS THE NEXT FUNCTION and differs in exactly   */
        /* one token, the element type of arrSrc.  Keeping them apart rather  */
        /* than testing the depth inside the innermost loop is a deliberate   */
        /* trade, argued at the call site.                                    */
        /*                                                                    */
        /* PREMULTIPLICATION HAPPENS HERE, ON THE WAY IN, AND IS UNDONE ONLY  */
        /* AT THE FINAL WRITE.  The intermediate therefore carries colour     */
        /* already multiplied by coverage and alpha as itself, which is       */
        /* exactly the form in which both are linear -- so the vertical pass  */
        /* needs no knowledge of alpha at all and is a plain weighted sum.    */
        /*                                                                    */
        /* THE MODE IS NOT TESTED PER SAMPLE.  The coverage factor depends on */
        /* the contributing PIXEL, not on the channel, so it is folded into a */
        /* copy of the weights once per output pixel and the inner loop stays */
        /* a clean dot product.  Alpha itself uses the unfolded weights,      */
        /* because coverage is not scaled by coverage.                        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     arrSrc         : The source samples, row-major, channel-       */
        /*                      interleaved.                                  */
        /*     iSrcWidth      : Source pixels across.                         */
        /*     iSrcHeight     : Source rows, and the intermediate's too.      */
        /*     iChannels      : Samples per pixel, 1 to 4.                    */
        /*     axisIn         : The horizontal contributor table.             */
        /*     bPremultiply   : true to fold coverage into colour.  Already   */
        /*                      false when the image has no alpha channel.    */
        /*     iAlphaChannel  : Index of the alpha channel, or -1.            */
        /*     fAlphaMax      : Full scale for this depth, 255 or 65535.      */
        /*     arrDstIo       : Receives iOutWidth * iSrcHeight * iChannels   */
        /*                      floats; every element is written.             */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void           : The result is arrDstIo.                       */
        /*--------------------------------------------------------------------*/

        private static void RESAMP_PassHorizontal8(byte[] arrSrc, int iSrcWidth,
                                                   int iSrcHeight, int iChannels,
                                                   ResampleAxis axisIn,
                                                   bool bPremultiply,
                                                   int iAlphaChannel,
                                                   float fAlphaMax,
                                                   float[] arrDstIo)
        {
            float[] arrColourWeight;
            float   fSum;
            int     iOutWidth;
            int     iSrcRow;
            int     iDstRow;
            int     iWeightBase;
            int     iFirst;
            int     iCount;
            int     y;
            int     x;
            int     c;
            int     i;

            iOutWidth       = axisIn.RSMPA_iOutSize;
            arrColourWeight = new float[axisIn.RSMPA_iMaxCount];

            for (y = 0; y < iSrcHeight; y++)
            {   /* One source row per iteration; the table is the same for      */
                /* every one of them, which is the point of a separable pass.   */
                iSrcRow = y * iSrcWidth * iChannels;
                iDstRow = y * iOutWidth * iChannels;

                for (x = 0; x < iOutWidth; x++)
                {   /* One output pixel of this row per iteration. */
                    iFirst      = axisIn.RSMPA_arrFirst[x];
                    iCount      = axisIn.RSMPA_arrCount[x];
                    iWeightBase = x * axisIn.RSMPA_iMaxCount;

                    if (bPremultiply)
                    {   /* Fold each contributor's coverage into its weight,   */
                        /* once, for every colour channel to share.            */
                        for (i = 0; i < iCount; i++)
                        {   /* One contributor per iteration. */
                            arrColourWeight[i] =
                                axisIn.RSMPA_arrWeights[iWeightBase + i] *
                                ((float)arrSrc[iSrcRow +
                                               ((iFirst + i) * iChannels) +
                                               iAlphaChannel] / fAlphaMax);
                        }
                    }
                    else
                    {   /* Straight: colour weights are the plain weights. */
                        for (i = 0; i < iCount; i++)
                        {   /* One contributor per iteration. */
                            arrColourWeight[i] =
                                axisIn.RSMPA_arrWeights[iWeightBase + i];
                        }
                    }

                    for (c = 0; c < iChannels; c++)
                    {   /* One channel per iteration. */
                        fSum = 0.0f;

                        if (c == iAlphaChannel)
                        {   /* Coverage itself, summed with the plain weights   */
                            /* -- scaling coverage by coverage would square     */
                            /* it.  Unreachable when iAlphaChannel is -1.       */
                            for (i = 0; i < iCount; i++)
                            {   /* One contributing input pixel. */
                                fSum = fSum +
                                       (axisIn.RSMPA_arrWeights[iWeightBase + i] *
                                        (float)arrSrc[iSrcRow +
                                                      ((iFirst + i) * iChannels) +
                                                      c]);
                            }
                        }
                        else
                        {   /* A colour channel.  In straight mode the folded   */
                            /* factor is simply one.                            */
                            for (i = 0; i < iCount; i++)
                            {   /* One contributing input pixel. */
                                fSum = fSum +
                                       (arrColourWeight[i] *
                                        (float)arrSrc[iSrcRow +
                                                      ((iFirst + i) * iChannels) +
                                                      c]);
                            }
                        }

                        arrDstIo[iDstRow + (x * iChannels) + c] = fSum;
                    }
                }
            }
        }

        /*--------------------------------------------------------------------*/
        /* RESAMP_PassHorizontal16:                                           */
        /*                                                                    */
        /* The first pass over a SIXTEEN-BIT source.  Line for line the       */
        /* previous function with a ushort array in place of a byte one.      */
        /*                                                                    */
        /* THE FLOAT INTERMEDIATE IS WIDE ENOUGH AND THAT IS NOT LUCK.  A     */
        /* single-precision mantissa holds every integer to 16 777 216        */
        /* exactly, so a 65 535 sample is exact, and so is any weighted sum   */
        /* of a few hundred of them -- the sum cannot exceed the largest      */
        /* sample by more than the negative lobes overshoot, which is a few   */
        /* per cent.  There is no accumulation of rounding error to worry     */
        /* about here, which is why the same intermediate serves both depths. */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     arrSrc         : The source samples, row-major, channel-       */
        /*                      interleaved.                                  */
        /*     iSrcWidth      : Source pixels across.                         */
        /*     iSrcHeight     : Source rows, and the intermediate's too.      */
        /*     iChannels      : Samples per pixel, 1 to 4.                    */
        /*     axisIn         : The horizontal contributor table.             */
        /*     bPremultiply   : true to fold coverage into colour.            */
        /*     iAlphaChannel  : Index of the alpha channel, or -1.            */
        /*     fAlphaMax      : Full scale for this depth, 65535 here.        */
        /*     arrDstIo       : Receives iOutWidth * iSrcHeight * iChannels   */
        /*                      floats; every element is written.             */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void           : The result is arrDstIo.                       */
        /*--------------------------------------------------------------------*/

        private static void RESAMP_PassHorizontal16(ushort[] arrSrc, int iSrcWidth,
                                                    int iSrcHeight, int iChannels,
                                                    ResampleAxis axisIn,
                                                    bool bPremultiply,
                                                    int iAlphaChannel,
                                                    float fAlphaMax,
                                                    float[] arrDstIo)
        {
            float[] arrColourWeight;
            float   fSum;
            int     iOutWidth;
            int     iSrcRow;
            int     iDstRow;
            int     iWeightBase;
            int     iFirst;
            int     iCount;
            int     y;
            int     x;
            int     c;
            int     i;

            iOutWidth       = axisIn.RSMPA_iOutSize;
            arrColourWeight = new float[axisIn.RSMPA_iMaxCount];

            for (y = 0; y < iSrcHeight; y++)
            {   /* One source row per iteration; the table is the same for      */
                /* every one of them, which is the point of a separable pass.   */
                iSrcRow = y * iSrcWidth * iChannels;
                iDstRow = y * iOutWidth * iChannels;

                for (x = 0; x < iOutWidth; x++)
                {   /* One output pixel of this row per iteration. */
                    iFirst      = axisIn.RSMPA_arrFirst[x];
                    iCount      = axisIn.RSMPA_arrCount[x];
                    iWeightBase = x * axisIn.RSMPA_iMaxCount;

                    if (bPremultiply)
                    {   /* Fold each contributor's coverage into its weight. */
                        for (i = 0; i < iCount; i++)
                        {   /* One contributor per iteration. */
                            arrColourWeight[i] =
                                axisIn.RSMPA_arrWeights[iWeightBase + i] *
                                ((float)arrSrc[iSrcRow +
                                               ((iFirst + i) * iChannels) +
                                               iAlphaChannel] / fAlphaMax);
                        }
                    }
                    else
                    {   /* Straight: colour weights are the plain weights. */
                        for (i = 0; i < iCount; i++)
                        {   /* One contributor per iteration. */
                            arrColourWeight[i] =
                                axisIn.RSMPA_arrWeights[iWeightBase + i];
                        }
                    }

                    for (c = 0; c < iChannels; c++)
                    {   /* One channel per iteration. */
                        fSum = 0.0f;

                        if (c == iAlphaChannel)
                        {   /* Coverage itself, on the plain weights. */
                            for (i = 0; i < iCount; i++)
                            {   /* One contributing input pixel. */
                                fSum = fSum +
                                       (axisIn.RSMPA_arrWeights[iWeightBase + i] *
                                        (float)arrSrc[iSrcRow +
                                                      ((iFirst + i) * iChannels) +
                                                      c]);
                            }
                        }
                        else
                        {   /* A colour channel. */
                            for (i = 0; i < iCount; i++)
                            {   /* One contributing input pixel. */
                                fSum = fSum +
                                       (arrColourWeight[i] *
                                        (float)arrSrc[iSrcRow +
                                                      ((iFirst + i) * iChannels) +
                                                      c]);
                            }
                        }

                        arrDstIo[iDstRow + (x * iChannels) + c] = fSum;
                    }
                }
            }
        }

        /*--------------------------------------------------------------------*/
        /* RESAMP_PassVertical8:                                              */
        /*                                                                    */
        /* The second pass: resamples every intermediate column down, rounds  */
        /* once, and clamps into the output buffer's bytes.                   */
        /*                                                                    */
        /* THE CLAMP IS LOAD-BEARING, NOT DEFENSIVE.  Catmull-Rom and         */
        /* Mitchell have NEGATIVE outer lobes -- that is what makes them      */
        /* sharpen -- so across a hard edge the weighted sum genuinely        */
        /* undershoots below zero and overshoots above 255.  One of each is   */
        /* hand-computed and held to the exact endpoint, because              */
        /* a cast without the clamp does not fail loudly: it WRAPS, turning   */
        /* an overshoot of 273 into 17 and an undershoot of -18 into 238,     */
        /* which reads as speckle rather than as a missing bounds check.      */
        /*                                                                    */
        /* THE WIDTH DOES NOT CHANGE in this pass -- only the row count does. */
        /*                                                                    */
        /* THIS PASS DOES NOT PREMULTIPLY AND DOES NOT NEED TO.  The          */
        /* intermediate already carries colour times coverage, and that       */
        /* product is exactly the quantity that averages linearly -- which is */
        /* the whole reason premultiplication is done on the way IN.  All     */
        /* that happens here is a plain weighted sum, and then the product is */
        /* UNDONE at the final write.                                         */
        /*                                                                    */
        /* THE WHOLE PIXEL IS ACCUMULATED BEFORE ANY OF IT IS WRITTEN, which  */
        /* is why there is a small scratch array.  Un-premultiplying a colour */
        /* channel needs that pixel's resampled ALPHA, and alpha is the LAST  */
        /* channel -- so writing channels as they are computed would need the */
        /* answer before it existed.                                          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     arrSrc         : The float intermediate from the first pass.   */
        /*     iWidth         : Pixels across, the same for both.             */
        /*     iSrcHeight     : Rows in the intermediate.                     */
        /*     iChannels      : Samples per pixel, 1 to 4.                    */
        /*     axisIn         : The vertical contributor table.               */
        /*     bPremultiply   : true when the intermediate carries colour     */
        /*                      times coverage, to be undone here.            */
        /*     iAlphaChannel  : Index of the alpha channel, or -1.            */
        /*     fAlphaMax      : Full scale for this depth, 255 here.          */
        /*     arrDstIo       : Receives iWidth * iOutHeight * iChannels      */
        /*                      bytes; every element is written.              */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void           : The result is arrDstIo.                       */
        /*--------------------------------------------------------------------*/

        private static void RESAMP_PassVertical8(float[] arrSrc, int iWidth,
                                                 int iSrcHeight, int iChannels,
                                                 ResampleAxis axisIn,
                                                 bool bPremultiply,
                                                 int iAlphaChannel,
                                                 float fAlphaMax,
                                                 byte[] arrDstIo)
        {
            float[] arrPixel;
            float   fSum;
            float   fAlpha;
            float   fRecover;
            int     iOutHeight;
            int     iRowStride;
            int     iWeightBase;
            int     iFirst;
            int     iCount;
            int     y;
            int     x;
            int     c;
            int     i;

            iOutHeight = axisIn.RSMPA_iOutSize;
            iRowStride = iWidth * iChannels;
            arrPixel   = new float[iChannels];

            for (y = 0; y < iOutHeight; y++)
            {   /* One output row per iteration.  The contributor run is a    */
                /* property of the ROW here, so it is read once outside the   */
                /* column loop rather than per pixel.                         */
                iFirst      = axisIn.RSMPA_arrFirst[y];
                iCount      = axisIn.RSMPA_arrCount[y];
                iWeightBase = y * axisIn.RSMPA_iMaxCount;

                for (x = 0; x < iWidth; x++)
                {   /* One column per iteration. */
                    for (c = 0; c < iChannels; c++)
                    {   /* One channel per iteration, into the scratch. */
                        fSum = 0.0f;

                        for (i = 0; i < iCount; i++)
                        {   /* One contributing intermediate row per   */
                            /* iteration.                              */
                            fSum = fSum +
                                   (axisIn.RSMPA_arrWeights[iWeightBase + i] *
                                    arrSrc[((iFirst + i) * iRowStride) +
                                           (x * iChannels) + c]);
                        }

                        arrPixel[c] = fSum;
                    }

                    if (bPremultiply)
                    {   /* Undo the coverage factor, so what is stored is the   */
                        /* colour the pixel would be if fully opaque -- which   */
                        /* is what an RGBA buffer holds in this library.        */
                        fAlpha = arrPixel[iAlphaChannel];

                        if (fAlpha >= RESAMP_ALPHA_FLOOR)
                        {   /* Enough coverage for the division to mean   */
                            /* something and to be numerically safe.      */
                            fRecover = fAlphaMax / fAlpha;

                            for (c = 0; c < iChannels; c++)
                            {   /* Colour only; coverage keeps its own value. */
                                if (c != iAlphaChannel)
                                {   /* Recover the unpremultiplied colour. */
                                    arrPixel[c] = arrPixel[c] * fRecover;
                                }
                            }
                        }
                        else
                        {   /* The stored alpha will round to zero, so the    */
                            /* pixel is invisible and its colour is not       */
                            /* recoverable -- nor does it matter.  Zero is    */
                            /* the same answer GifCodec's round trip gives.   */
                            for (c = 0; c < iChannels; c++)
                            {   /* Colour only. */
                                if (c != iAlphaChannel)
                                {   /* Nothing to recover. */
                                    arrPixel[c] = 0.0f;
                                }
                            }
                        }
                    }

                    for (c = 0; c < iChannels; c++)
                    {   /* One channel per iteration, rounded and clamped. */
                        arrDstIo[(y * iRowStride) + (x * iChannels) + c] =
                            RESAMP_ClampToByte(arrPixel[c]);
                    }
                }
            }
        }

        /*--------------------------------------------------------------------*/
        /* RESAMP_PassVertical16:                                             */
        /*                                                                    */
        /* The second pass writing SIXTEEN-BIT samples.  Line for line the    */
        /* previous function with a ushort destination and the wider clamp.   */
        /*                                                                    */
        /* THE RINGING IS PROPORTIONALLY IDENTICAL AT THIS DEPTH, which is    */
        /* worth saying because the numbers look so different.  Catmull-Rom   */
        /* across a black-to-white step overshoots by the same FRACTION       */
        /* whatever the range: the 8-bit edge that reaches 272.93 out of 255  */
        /* becomes a 16-bit edge reaching 70 142.93 out of 65 535.  So the    */
        /* clamp is exactly as load-bearing here, and an unclamped cast wraps */
        /* exactly as badly -- 70 143 narrowed to a ushort is 4 607, a        */
        /* near-black speckle on the BRIGHT side of an edge.                  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     arrSrc         : The float intermediate from the first pass.   */
        /*     iWidth         : Pixels across, the same for both.             */
        /*     iSrcHeight     : Rows in the intermediate.                     */
        /*     iChannels      : Samples per pixel, 1 to 4.                    */
        /*     axisIn         : The vertical contributor table.               */
        /*     bPremultiply   : true when the intermediate carries colour     */
        /*                      times coverage, to be undone here.            */
        /*     iAlphaChannel  : Index of the alpha channel, or -1.            */
        /*     fAlphaMax      : Full scale for this depth, 65535 here.        */
        /*     arrDstIo       : Receives iWidth * iOutHeight * iChannels      */
        /*                      ushorts; every element is written.            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void           : The result is arrDstIo.                       */
        /*--------------------------------------------------------------------*/

        private static void RESAMP_PassVertical16(float[] arrSrc, int iWidth,
                                                  int iSrcHeight, int iChannels,
                                                  ResampleAxis axisIn,
                                                  bool bPremultiply,
                                                  int iAlphaChannel,
                                                  float fAlphaMax,
                                                  ushort[] arrDstIo)
        {
            float[] arrPixel;
            float   fSum;
            float   fAlpha;
            float   fRecover;
            int     iOutHeight;
            int     iRowStride;
            int     iWeightBase;
            int     iFirst;
            int     iCount;
            int     y;
            int     x;
            int     c;
            int     i;

            iOutHeight = axisIn.RSMPA_iOutSize;
            iRowStride = iWidth * iChannels;
            arrPixel   = new float[iChannels];

            for (y = 0; y < iOutHeight; y++)
            {   /* One output row per iteration.  The contributor run is a    */
                /* property of the ROW here, so it is read once outside the   */
                /* column loop rather than per pixel.                         */
                iFirst      = axisIn.RSMPA_arrFirst[y];
                iCount      = axisIn.RSMPA_arrCount[y];
                iWeightBase = y * axisIn.RSMPA_iMaxCount;

                for (x = 0; x < iWidth; x++)
                {   /* One column per iteration. */
                    for (c = 0; c < iChannels; c++)
                    {   /* One channel per iteration, into the scratch. */
                        fSum = 0.0f;

                        for (i = 0; i < iCount; i++)
                        {   /* One contributing intermediate row per   */
                            /* iteration.                              */
                            fSum = fSum +
                                   (axisIn.RSMPA_arrWeights[iWeightBase + i] *
                                    arrSrc[((iFirst + i) * iRowStride) +
                                           (x * iChannels) + c]);
                        }

                        arrPixel[c] = fSum;
                    }

                    if (bPremultiply)
                    {   /* Undo the coverage factor.  The floor is in SAMPLE    */
                        /* units and is the same 0.5 at either depth, because   */
                        /* both clamps round by adding a half.                  */
                        fAlpha = arrPixel[iAlphaChannel];

                        if (fAlpha >= RESAMP_ALPHA_FLOOR)
                        {   /* Enough coverage for the division to be safe. */
                            fRecover = fAlphaMax / fAlpha;

                            for (c = 0; c < iChannels; c++)
                            {   /* Colour only; coverage keeps its own value. */
                                if (c != iAlphaChannel)
                                {   /* Recover the unpremultiplied colour. */
                                    arrPixel[c] = arrPixel[c] * fRecover;
                                }
                            }
                        }
                        else
                        {   /* Invisible pixel; nothing to recover. */
                            for (c = 0; c < iChannels; c++)
                            {   /* Colour only. */
                                if (c != iAlphaChannel)
                                {   /* Zero, as GifCodec's round trip gives. */
                                    arrPixel[c] = 0.0f;
                                }
                            }
                        }
                    }

                    for (c = 0; c < iChannels; c++)
                    {   /* One channel per iteration, rounded and clamped. */
                        arrDstIo[(y * iRowStride) + (x * iChannels) + c] =
                            RESAMP_ClampToUshort(arrPixel[c]);
                    }
                }
            }
        }

        /*--------------------------------------------------------------------*/
        /* RESAMP_ClampToByte:                                                */
        /*                                                                    */
        /* Rounds one accumulated sample to nearest and brings it inside the  */
        /* eight-bit range.  This is the ONLY place in the pipeline where a   */
        /* value is rounded, which is what keeps a two-pass resize as         */
        /* accurate as a one-pass one would be.                               */
        /*                                                                    */
        /* THE ORDER MATTERS: the range tests come FIRST and the rounding     */
        /* only runs on a value already known to be inside.  Adding 0.5 to a  */
        /* large overshoot and then casting would be a second chance to go    */
        /* wrong, and testing afterwards would mean reasoning about what the  */
        /* cast did to a value that could not be represented.                 */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     fValue  : The weighted sum, which may legitimately fall        */
        /*               outside 0 to 255 wherever a sharpening kernel rings. */
        /*                                                                    */
        /* Returns:                                                           */
        /*     byte    : The sample, rounded to nearest and clamped.          */
        /*--------------------------------------------------------------------*/

        private static byte RESAMP_ClampToByte(float fValue)
        {
            if (fValue <= 0.0f)
            {   /* At or below black, which a negative lobe reaches on the   */
                /* dark side of a hard edge.                                 */
                return(0);
            }

            if (fValue >= 255.0f)
            {   /* At or above white, the same overshoot on the light side. */
                return(255);
            }

            /* Strictly inside, so adding a half and truncating rounds to   */
            /* nearest and cannot leave the byte range.                     */
            return((byte)(int)(fValue + 0.5f));
        }

        /*--------------------------------------------------------------------*/
        /* RESAMP_ClampToUshort:                                              */
        /*                                                                    */
        /* The same rounding and clamping against the sixteen-bit ceiling.    */
        /*                                                                    */
        /* WHY THIS IS A SECOND FUNCTION RATHER THAN ONE TAKING A MAXIMUM.    */
        /* A shared helper would have to return an int for the caller to cast */
        /* anyway, which puts an unchecked narrowing back at every call site  */
        /* -- exactly the thing the clamp exists to make impossible.  Two     */
        /* functions each returning their own type keep the narrowing inside  */
        /* the only code that has proved the value is in range.               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     fValue    : The weighted sum, which may legitimately fall      */
        /*                 outside 0 to 65535 wherever a kernel rings.        */
        /*                                                                    */
        /* Returns:                                                           */
        /*     ushort    : The sample, rounded to nearest and clamped.        */
        /*--------------------------------------------------------------------*/

        private static ushort RESAMP_ClampToUshort(float fValue)
        {
            if (fValue <= 0.0f)
            {   /* At or below black. */
                return(0);
            }

            if (fValue >= 65535.0f)
            {   /* At or above white. */
                return(65535);
            }

            /* Strictly inside.  The half is still exactly representable at     */
            /* this magnitude -- a float holds every integer to 16 777 216 --   */
            /* so rounding to nearest does not itself introduce an error.       */
            return((ushort)(int)(fValue + 0.5f));
        }
    }
}
