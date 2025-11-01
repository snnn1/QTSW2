@echo off
title Raw Data Translator
color 0A
echo.
echo ================================================
echo          📊 RAW DATA TRANSLATOR APP
echo ================================================
echo.
echo Starting the web-based GUI application...
echo.
echo The app will open in your default web browser automatically.
echo.
echo If it doesn't open automatically, go to:
echo    http://localhost:8501
echo.
echo To stop the app, close this window or press Ctrl+C
echo.
echo ================================================
echo.

cd /d "%~dp0\.."
streamlit run scripts\translate_raw_app.py

echo.
echo App stopped. Press any key to exit.
pause >nul

