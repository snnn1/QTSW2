@echo off
title Sequential Processor App
color 0A
echo.
echo ================================================
echo       🎯 SEQUENTIAL PROCESSOR APP
echo ================================================
echo.
echo Starting the web-based GUI application...
echo.

cd /d "%~dp0\.."

REM Start Streamlit in background and wait for it to start
start "Sequential Processor Streamlit" cmd /k "cd /d %~dp0.. && streamlit run sequential_processor\sequential_processor_app.py"

REM Wait for Streamlit to start
echo Waiting for Streamlit to start...
timeout /t 5 /nobreak >nul

REM Open browser automatically
echo Opening browser...
start http://localhost:8501

echo.
echo ================================================
echo Sequential Processor App started!
echo Browser should open automatically at:
echo    http://localhost:8501
echo.
echo To stop the app, close the Streamlit window.
echo ================================================
echo.
echo Press any key to exit this window (app will keep running)
pause >nul



