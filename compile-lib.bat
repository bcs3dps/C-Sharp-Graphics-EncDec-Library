@echo off
REM =============================================================================
REM compile-lib.bat -- build GraphicsEncDecLib.dll and nothing else
REM =============================================================================
REM This is the build a CONSUMER of the library wants: one target, the library
REM itself, referencing System.dll and nothing more.  It needs only the src\
REM tree -- no test harness, no fixtures, no tooling.
REM
REM   GraphicsEncDecLib.dll    pure managed.  References System.dll and NOTHING
REM                            else.  A stray System.Drawing or P/Invoke is a
REM                            COMPILE ERROR here rather than a runtime surprise
REM                            on a platform that has no GDI+, which is the
REM                            whole reason this target is built on its own.
REM
REM Most consumers will not use this script at all -- the library is fourteen
REM .cs files and drops straight into an existing project.  It is supplied so
REM that "does this still build clean, standalone, with no GDI+" has a
REM one-command answer.
REM
REM Usage:  compile-lib.bat            release build
REM         compile-lib.bat DEBUG=1    debug build (defines DEBUG)
REM
REM A DEBUG=1 argument does NOT survive being passed from a POSIX shell
REM (Git Bash, WSL): cmd.exe treats '=' as an argument delimiter, so it
REM arrives split into two arguments.  Invoke this script from a native cmd
REM prompt, or from a small wrapper that reads the argument out of a file.
REM
REM DO NOT trust the "BUILD SUCCESSFUL" banner on its own.  An intermediate
REM command can zero %errorlevel% after csc has failed, which is why the csc
REM result is captured into a named variable the instant it returns, and why
REM the caller should still check the output timestamp.
REM =============================================================================

echo.
echo ============================================
echo  GraphicsEncDecLib -- library only
echo ============================================
echo.

REM Ensure we are in the project root regardless of how this was invoked.
cd /d "%~dp0"

REM ---------------------------------------------------------------------------
REM Debug switch.  Both spellings are checked: cmd.exe passes DEBUG=1 as ONE
REM token, but a shell wrapper may split it into two.
REM ---------------------------------------------------------------------------
set DEFINES=
if "%1"=="DEBUG" if "%2"=="1" set DEFINES=/define:DEBUG
if "%1"=="DEBUG=1" set DEFINES=/define:DEBUG
if defined DEFINES echo  *** DEBUG BUILD ***

REM ---------------------------------------------------------------------------
REM Locate csc.exe.  64-bit first, then 32-bit, so a machine with both uses the
REM one matching the OS.  Every Windows with .NET Framework 4.x has one of them.
REM ---------------------------------------------------------------------------
set CSC=
if exist "%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" (
    set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
) else if exist "%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe" (
    set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
) else (
    echo ERROR: Could not find csc.exe
    echo Make sure .NET Framework 4.5+ is installed.
    exit /b 1
)

echo Using compiler: %CSC%
echo.

REM ---------------------------------------------------------------------------
REM THE SOURCE LIST -- EVERY FILE NAMED EXPLICITLY, NO WILDCARDS.
REM
REM A new source file does NOT build until its name is added here.  That is
REM the point rather than a side effect: the compiler then says the type is
REM missing, which is a loud and immediate failure rather than a stray or
REM half-finished .cs being swept silently into the build by a wildcard.
REM
REM THIS LIST MUST MATCH compile-lib.sh's, and both must match any other
REM build that compiles these sources.  A file
REM added to one and not the others is the one failure mode this arrangement
REM has; the counts are verified against each other rather than trusted.
REM ---------------------------------------------------------------------------
set SRCLIB=src\Core\Adler32.cs
set SRCLIB=%SRCLIB% src\Core\ByteReader.cs
set SRCLIB=%SRCLIB% src\Core\ByteWriter.cs
set SRCLIB=%SRCLIB% src\Core\Crc32.cs
set SRCLIB=%SRCLIB% src\Core\ImageBuffer.cs
set SRCLIB=%SRCLIB% src\Core\ImageDecoderDispatch.cs
set SRCLIB=%SRCLIB% src\Core\ImageFrameSet.cs
set SRCLIB=%SRCLIB% src\Core\ZlibStreamCodec.cs
set SRCLIB=%SRCLIB% src\Codecs\BmpCodec.cs
set SRCLIB=%SRCLIB% src\Codecs\GifCodec.cs
set SRCLIB=%SRCLIB% src\Codecs\JpegCodec.cs
set SRCLIB=%SRCLIB% src\Codecs\PngCodec.cs
set SRCLIB=%SRCLIB% src\Codecs\QoiCodec.cs
set SRCLIB=%SRCLIB% src\Resample\ImageResampler.cs

REM ---------------------------------------------------------------------------
REM /unsafe is on so a codec inner loop can take a fixed pointer into a managed
REM array without a build change.  That is the only sanctioned use of unsafe
REM here; Marshal and P/Invoke stay barred, because they are what would tie the
REM library to one platform.
REM
REM NOTE the reference list: System.dll and nothing more.  Do not add
REM System.Drawing.dll to this target under any circumstance -- its absence is
REM the proof the library is portable, not merely the intention.
REM ---------------------------------------------------------------------------
echo Building GraphicsEncDecLib.dll
"%CSC%" ^
    /target:library ^
    /out:GraphicsEncDecLib.dll ^
    /reference:System.dll ^
    /unsafe ^
    /optimize ^
    /nologo ^
    %DEFINES% ^
    %SRCLIB%
set ERR_LIB=%errorlevel%
if not "%ERR_LIB%"=="0" goto :error

echo.
echo ============================================
echo  BUILD SUCCESSFUL
echo  Output: GraphicsEncDecLib.dll
echo ============================================
echo.
echo Verify by the OUTPUT TIMESTAMP, not by this banner.
exit /b 0

:error
echo.
echo ============================================
echo  BUILD FAILED
echo  Check the errors above and fix them.
echo ============================================
echo.
exit /b 1
