@echo off
title Sequential Processor App
color 0A
echo.
echo ================================================
echo       ⏰ SEQUENTIAL PROCESSOR APP
echo ================================================
echo.
echo Starting the web-based GUI application...
echo.

cd /d "%~dp0\.."

echo.
echo ================================================
echo Sequential Processor App starting!
echo Browser will open automatically at:
echo    http://localhost:8501
echo.
echo To stop the app, press Ctrl+C in this window.
echo ================================================
echo.

REM Run Streamlit in the same window (no new window)
REM Streamlit will automatically open the browser
streamlit run sequential_processor\sequential_processor_app.py --server.headless false



