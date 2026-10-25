#!/bin/sh
# =============================================================================
# compile-lib.sh -- build GraphicsEncDecLib.dll under Mono, and nothing else
# =============================================================================
# The Linux half of compile-lib.bat: one target, the library itself,
# referencing System.dll and nothing more.  It needs only the src\ tree -- no
# test harness, no fixtures, no tooling.
#
# THIS SCRIPT IS THE POINT OF THE LIBRARY, not an afterthought.  A managed
# codec library that only builds on Windows would have no reason to exist;
# this is what turns "should be portable" into "is portable".  If it fails,
# something platform-specific has reached src\ and must come back out.
#
# Usage, from a shell whose working directory is this folder:
#     ./compile-lib.sh              release build
#     ./compile-lib.sh DEBUG=1      debug build (defines DEBUG)
#
# Requires mcs (the Mono C# compiler).  Output goes to build-mono/ so a Linux
# build can never overwrite a Windows one sitting beside it.
# =============================================================================

set -u

cd "$(dirname "$0")" || exit 1

# -----------------------------------------------------------------------------
# Debug switch.  Unlike cmd.exe, a POSIX shell passes DEBUG=1 through intact,
# so only the one spelling needs checking.
# -----------------------------------------------------------------------------
DEFINES=""
if [ "${1:-}" = "DEBUG=1" ]; then
    DEFINES="-define:DEBUG"
    echo " *** DEBUG BUILD ***"
fi

if ! command -v mcs >/dev/null 2>&1; then
    echo "ERROR: mcs not found.  Install the Mono C# compiler."
    exit 1
fi

echo ""
echo "============================================"
echo " GraphicsEncDecLib -- library only (Mono)"
echo "============================================"
echo ""
echo "Using compiler: $(command -v mcs)"
echo ""

mkdir -p build-mono || exit 1

# -----------------------------------------------------------------------------
# THE SOURCE LIST -- EVERY FILE NAMED EXPLICITLY, NO WILDCARDS.
#
# A new source file does NOT build until its name is added here.  That is the
# point rather than a side effect: the compiler then says the type is missing,
# which is a loud and immediate failure rather than a stray or half-finished
# .cs being swept silently into the build by a wildcard.
#
# THIS LIST MUST MATCH compile-lib.bat's, and both must match any other
# build that compiles these sources.  A file
# added to one and not the others is the one failure mode this arrangement
# has; the counts are verified against each other rather than trusted.
# -----------------------------------------------------------------------------
SRCLIB="src/Core/Adler32.cs"
SRCLIB="$SRCLIB src/Core/ByteReader.cs"
SRCLIB="$SRCLIB src/Core/ByteWriter.cs"
SRCLIB="$SRCLIB src/Core/Crc32.cs"
SRCLIB="$SRCLIB src/Core/ImageBuffer.cs"
SRCLIB="$SRCLIB src/Core/ImageDecoderDispatch.cs"
SRCLIB="$SRCLIB src/Core/ImageFrameSet.cs"
SRCLIB="$SRCLIB src/Core/ZlibStreamCodec.cs"
SRCLIB="$SRCLIB src/Codecs/BmpCodec.cs"
SRCLIB="$SRCLIB src/Codecs/GifCodec.cs"
SRCLIB="$SRCLIB src/Codecs/JpegCodec.cs"
SRCLIB="$SRCLIB src/Codecs/PngCodec.cs"
SRCLIB="$SRCLIB src/Codecs/QoiCodec.cs"
SRCLIB="$SRCLIB src/Resample/ImageResampler.cs"

# -----------------------------------------------------------------------------
# -unsafe is on so a codec inner loop can take a fixed pointer into a managed
# array without a build change.  That is the only sanctioned use of unsafe
# here; Marshal and P/Invoke stay barred, because they are what would tie the
# library to one platform.
#
# NOTE the reference list: System.dll and nothing more.  Do not add
# System.Drawing.dll to this target under any circumstance -- its absence is
# the proof the library is portable, not merely the intention.
# -----------------------------------------------------------------------------
echo "Building build-mono/GraphicsEncDecLib.dll"
# shellcheck disable=SC2086
mcs \
    -target:library \
    -out:build-mono/GraphicsEncDecLib.dll \
    -r:System.dll \
    -unsafe \
    -optimize+ \
    -nologo \
    $DEFINES \
    $SRCLIB
ERR_LIB=$?

if [ "$ERR_LIB" -ne 0 ]; then
    echo ""
    echo "============================================"
    echo " BUILD FAILED"
    echo " Check the errors above and fix them."
    echo "============================================"
    echo ""
    exit 1
fi

echo ""
echo "============================================"
echo " BUILD SUCCESSFUL"
echo " Output: build-mono/GraphicsEncDecLib.dll"
echo "============================================"
echo ""
echo "Verify by the OUTPUT TIMESTAMP, not by this banner."
exit 0
