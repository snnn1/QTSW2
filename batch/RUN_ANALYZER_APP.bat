@echo off
title Breakout Analyzer App
color 0A
echo.
echo ================================================
echo       📊 BREAKOUT ANALYZER APP
echo ================================================
echo.
echo Starting the web-based GUI application...
echo.
echo The app will open in your default web browser automatically.
echo.
echo If it doesn't open automatically, go to:
echo    http://localhost:8502
echo.
echo To stop the app, close this window or press Ctrl+C
echo.
echo ================================================
echo.

cd /d "%~dp0\.."
streamlit run scripts\breakout_analyzer\analyzer_app\app.py --server.port 8502

echo.
echo App stopped. Press any key to exit.
pause >nul
