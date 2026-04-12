@echo off
title QTSW2 Translator - GC
color 0A

echo.
echo ================================================
echo        QTSW2 TRANSLATOR (GC)
echo ================================================
echo.

cd /d C:\Users\jakej\QTSW2

python -m modules.translator.cli rebuild-missing ^
  --instrument GC ^
  --from 2025-01-01 ^
  --to 2025-12-31 ^
  --raw-root data\raw ^
  --output-root data\translated

if errorlevel 1 (
    echo.
    echo Translator FAILED for GC
    echo Exit code: %ERRORLEVEL%
    echo.
    pause
    exit /b %ERRORLEVEL%
)

echo.
echo Translator completed successfully for GC
echo.
pause
exit /b 0
