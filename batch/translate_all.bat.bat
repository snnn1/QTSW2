@echo off
title QTSW2 Translator - ALL
color 0A

echo.
echo =================================================
echo        QTSW2 TRANSLATOR - ALL INSTRUMENTS
echo =================================================
echo.

cd /d C:\Users\jakej\QTSW2

set FROM_DATE=2025-01-01
set TO_DATE=2025-12-31
set RAW_ROOT=data\raw
set OUT_ROOT=data\translated

set FAILED=0

call :RUN ES
call :RUN NQ
call :RUN YM
call :RUN CL
call :RUN NG
call :RUN GC

echo.
echo =================================================
if %FAILED%==0 (
    echo   ALL TRANSLATIONS COMPLETED SUCCESSFULLY
) else (
    echo   SOME TRANSLATIONS FAILED — CHECK OUTPUT
)
echo =================================================
echo.

pause
exit /b %FAILED%


:: =================================================
:: FUNCTION: RUN TRANSLATOR FOR ONE INSTRUMENT
:: =================================================
:RUN
set INST=%1

echo.
echo -------------------------------------------------
echo   TRANSLATING %INST%
echo -------------------------------------------------
echo.

python -m modules.translator.cli rebuild-missing ^
  --instrument %INST% ^
  --from %FROM_DATE% ^
  --to %TO_DATE% ^
  --raw-root %RAW_ROOT% ^
  --output-root %OUT_ROOT%

if errorlevel 1 (
    echo.
    echo *** FAILED: %INST% ***
    echo.
    set FAILED=1
) else (
    echo.
    echo OK: %INST%
    echo.
)

exit /b 0
