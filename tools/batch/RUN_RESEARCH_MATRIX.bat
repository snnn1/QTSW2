@echo off
title Research Matrix - Full Rebuild to data/_copy
color 0E
echo.
echo ================================================
echo       RESEARCH MATRIX - FULL REBUILD
echo ================================================
echo.
echo Writes ONLY to data/_copy/ (never touches production)
echo   - data/_copy/master_matrix/
echo   - data/_copy/timetable/timetable_copy.json
echo.
echo Optional: add --start-date YYYY-MM-DD --end-date YYYY-MM-DD
echo.
echo ================================================
echo.

cd /d "%~dp0\.."
python scripts\maintenance\run_matrix_copy.py %*

echo.
echo Press any key to exit.
pause >nul
