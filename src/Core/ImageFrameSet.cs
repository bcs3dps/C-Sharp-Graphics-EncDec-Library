/*----------------------------------------------------------------------------*/
/* SPDX-License-Identifier: MIT OR GPL-2.0-or-later                           */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* Dual-licensed at the recipient's choice -- see LICENSE in this folder for  */
/* the full terms of both licences.  GPL-2.0 full text in GPL-2.0.txt.        */
/*----------------------------------------------------------------------------*/
/* ImageFrameSet.cs -- an animation: several frames and their timing  (FRM_)  */
/*----------------------------------------------------------------------------*/
/* Only one format in this library is animated -- GIF -- so this type exists  */
/* solely for GIF_TryDecodeFrames and GIF_TryEncodeFrames.  It is defined     */
/* here with the core types rather than alongside the GIF codec because the   */
/* public surface is settled once, and a type that appears in two public      */
/* signatures is part of that surface whether or not anything implements      */
/* those signatures yet.                                                      */
/*                                                                            */
/* FRAMES ARE PRE-COMPOSITED, AND THAT IS THE IMPORTANT DESIGN DECISION HERE. */
/*                                                                            */
/* A GIF frame on the wire is not a picture.  It is a PATCH: a sub-rectangle  */
/* of pixels, plus a disposal method saying what to do with the previous      */
/* canvas before drawing it (leave it, restore it to the background colour,   */
/* or restore it to what was there before the last frame).  A decoder that    */
/* handed those patches straight to the caller would be handing over half a   */
/* GIF decoder's work along with them, and every consumer would have to       */
/* reimplement canvas composition -- badly, and differently each time.        */
/*                                                                            */
/* So the decoder resolves all of that and every element of FRM_arrFrames is  */
/* a COMPLETE image of identical dimensions, ready to blit.  This is what stb */
/* does, and it is the only shape a caller can actually use.                  */
/*                                                                            */
/* The cost is memory: an animation of N frames holds N full images rather    */
/* than N patches.  That is accepted deliberately.  A caller who genuinely    */
/* needs the patch stream is decoding GIF internals, not consuming images,    */
/* and is better served by reading the codec than by a leaky buffer type.     */
/*                                                                            */
/* A caller that does not care about animation never touches this type at     */
/* all: GIF_TryDecode returns frame 0 as a plain ImageBuffer.                 */
/*                                                                            */
/* PORTABILITY: nothing here is Windows-specific, and nothing in this library */
/* is -- that constraint is the reason it exists.                             */
/*----------------------------------------------------------------------------*/

using System;

namespace GraphicsEncDec
{
    /*------------------------------------------------------------------------*/
    /* ImageFrameSet:                                                         */
    /* A decoded animation -- the composited frames, the delay before each    */
    /* one, and how many times the whole thing should play.  Sealed for the   */
    /* same reason ImageBuffer is: the "all frames are the same size" and     */
    /* "the two arrays are the same length" invariants are checked by the     */
    /* helper below, and a subclass could break them unobserved.              */
    /*------------------------------------------------------------------------*/

    public sealed class ImageFrameSet
    {
        /*--------------------------------------------------------------------*/
        /* Loop-count sentinel.  GIF's NETSCAPE2.0 application extension      */
        /* encodes "forever" as a literal zero, so this constant is the       */
        /* format's own value rather than one this library invented -- which  */
        /* is why it is 0 and not something more obviously special.           */
        /*--------------------------------------------------------------------*/

        public const int FRM_LOOP_FOREVER = 0;           // play endlessly; GIF's own encoding

        /*--------------------------------------------------------------------*/
        /* A sane ceiling on frame count, for the same reason ImageBuffer     */
        /* caps its dimensions: a corrupt or hostile file must fail fast      */
        /* rather than make the decoder allocate until the process dies.      */
        /* Twenty thousand frames is far beyond any real animation and far    */
        /* below anything that could exhaust memory on its own.               */
        /*--------------------------------------------------------------------*/

        public const int FRM_MAX_FRAMES = 20000;         // refuse anything claiming more

        /*--------------------------------------------------------------------*/
        /* The animation itself.  Plain public fields, FRM_ group prefix and  */
        /* a Hungarian type tag on each, so every name is globally unique and */
        /* greps to exactly one definition.                                   */
        /*--------------------------------------------------------------------*/

        public ImageBuffer[] FRM_arrFrames;              // fully composited frames, all identical in size
        public int[]         FRM_arrDelaysMs;            // delay BEFORE showing frame i, in milliseconds
        public int           FRM_iLoopCount;             // FRM_LOOP_FOREVER, or a positive play count

        /*--------------------------------------------------------------------*/
        /* FRM_TryValidate:                                                   */
        /*                                                                    */
        /* Checks an animation handed in from outside for the invariants an   */
        /* encoder relies on: both arrays present and the same length, at     */
        /* least one frame, no null frame, every frame individually valid,    */
        /* and every frame the same size and shape as the first.              */
        /*                                                                    */
        /* The same-size check is the one that matters most.  GIF's own       */
        /* structure guarantees it after composition, but this type can also  */
        /* be built by hand by a caller assembling an animation to encode,    */
        /* and a mismatched frame would otherwise surface as a corrupt output */
        /* file rather than as a refusal.  Catching it here turns a puzzling  */
        /* bad GIF into a sentence.                                           */
        /*                                                                    */
        /* Delays are NOT validated beyond the array length.  A zero or       */
        /* negative delay is legal on the wire (viewers clamp it to their own */
        /* minimum), so refusing one would reject files that decode fine      */
        /* everywhere else.                                                   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     frmIn    : The animation to check.  A null reference is        */
        /*                reported rather than thrown, so an encoder can      */
        /*                answer with a reason like any other bad input.      */
        /*     sReason  : Receives "" when the animation is sound, or a short */
        /*                description of the first problem found, naming the  */
        /*                offending frame index where there is one.           */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool     : true when the animation is safe to encode; false    */
        /*                otherwise.                                          */
        /*--------------------------------------------------------------------*/

        public static bool FRM_TryValidate(ImageFrameSet frmIn, out string sReason)
        {
            ImageBuffer imgFirst;
            ImageBuffer imgThis;
            string      sFrameReason;
            int         i;

            sReason = "";

            if (frmIn == null)
            {   /* No animation at all -- a validator reports rather than throws. */
                sReason = "frame set is null";
                return(false);
            }

            if (frmIn.FRM_arrFrames == null)
            {   /* The frame array was never built, so there is nothing to encode. */
                sReason = "frame set has no frame array";
                return(false);
            }

            if (frmIn.FRM_arrDelaysMs == null)
            {   /* Timing is not optional: an animation without delays is not an animation. */
                sReason = "frame set has no delay array";
                return(false);
            }

            if (frmIn.FRM_arrFrames.Length < 1)
            {   /* Zero frames would produce a GIF no viewer can display. */
                sReason = "frame set holds no frames";
                return(false);
            }

            if (frmIn.FRM_arrFrames.Length > FRM_MAX_FRAMES)
            {   /* Absurd frame count -- treat as corruption rather than attempting it. */
                sReason = "frame set holds " + frmIn.FRM_arrFrames.Length.ToString() +
                          " frames, which exceeds the limit of " + FRM_MAX_FRAMES.ToString();
                return(false);
            }

            if (frmIn.FRM_arrDelaysMs.Length != frmIn.FRM_arrFrames.Length)
            {   /* Arrays out of step: some frame would have no delay or a delay would have no frame. */
                sReason = "frame set has " + frmIn.FRM_arrFrames.Length.ToString() +
                          " frames but " + frmIn.FRM_arrDelaysMs.Length.ToString() + " delays";
                return(false);
            }

            if (frmIn.FRM_iLoopCount < 0)
            {   /* Negative play counts have no encoding; zero already means "forever". */
                sReason = "loop count " + frmIn.FRM_iLoopCount.ToString() + " is negative";
                return(false);
            }

            /* Frame 0 sets the shape every other frame must match. */
            imgFirst = frmIn.FRM_arrFrames[0];

            for (i = 0; i < frmIn.FRM_arrFrames.Length; i++)
            {   /* Walk every frame: each must be a valid buffer in its own   */
                /* right AND agree with frame 0 on geometry.                  */
                imgThis = frmIn.FRM_arrFrames[i];

                if (imgThis == null)
                {   /* A hole in the frame array -- almost always a half-built caller-side set. */
                    sReason = "frame " + i.ToString() + " is null";
                    return(false);
                }

                if (!ImageBuffer.IMG_TryValidate(imgThis, out sFrameReason))
                {   /* The frame is individually malformed; pass its own reason up with the index. */
                    sReason = "frame " + i.ToString() + " is invalid: " + sFrameReason;
                    return(false);
                }

                if (imgThis.IMG_iWidth != imgFirst.IMG_iWidth ||
                    imgThis.IMG_iHeight != imgFirst.IMG_iHeight)
                {   /* Composition should have made every frame identical in size; this one is not. */
                    sReason = "frame " + i.ToString() + " is " +
                              imgThis.IMG_iWidth.ToString() + "x" + imgThis.IMG_iHeight.ToString() +
                              " but frame 0 is " +
                              imgFirst.IMG_iWidth.ToString() + "x" + imgFirst.IMG_iHeight.ToString();
                    return(false);
                }

                if (imgThis.IMG_iChannels != imgFirst.IMG_iChannels ||
                    imgThis.IMG_iBitsPerSample != imgFirst.IMG_iBitsPerSample)
                {   /* Mixed pixel formats across frames would need a per-frame conversion nobody asked for. */
                    sReason = "frame " + i.ToString() + " has " +
                              imgThis.IMG_iChannels.ToString() + " channels at " +
                              imgThis.IMG_iBitsPerSample.ToString() + " bits, but frame 0 has " +
                              imgFirst.IMG_iChannels.ToString() + " channels at " +
                              imgFirst.IMG_iBitsPerSample.ToString() + " bits";
                    return(false);
                }
            }

            /* Every frame is valid, present, and identical in shape to the   */
            /* first, and the delay array lines up with it: safe to encode.   */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* FRM_FrameCount:                                                    */
        /*                                                                    */
        /* Returns the number of frames in an animation.  Trivial, and named  */
        /* anyway so that call sites read as intent and so a caller never has */
        /* to decide between FRM_arrFrames.Length and FRM_arrDelaysMs.Length  */
        /* -- which are equal on a validated set, but only on a validated     */
        /* one, and picking the wrong one by habit is how that stops being    */
        /* true unnoticed.                                                    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     frmIn  : An animation that has already been validated.  Must   */
        /*              not be null.                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int    : Frame count, always at least 1 on a validated set.    */
        /*--------------------------------------------------------------------*/

        public static int FRM_FrameCount(ImageFrameSet frmIn)
        {
            if (frmIn == null)
            {   /* A null here is a caller bug, not file data, so it throws. */
                throw new ArgumentNullException("frmIn");
            }

            /* The frame array is the authority; the delay array only mirrors   */
            /* its length, and only once validation has said so.                */
            return(frmIn.FRM_arrFrames.Length);
        }
    }
}
