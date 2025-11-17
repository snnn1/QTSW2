@echo off
echo ========================================
echo Tradovate API Test
echo ========================================
echo.

cd /d "%~dp0"

echo Running Tradovate fetch_data test...
echo.
echo Make sure you've updated YOUR_USERNAME and YOUR_PASSWORD in test_tradovate_example.py
echo.

python test_tradovate_example.py

echo.
echo ========================================
echo Test completed
echo ========================================
pause

