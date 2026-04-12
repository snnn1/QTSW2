@echo off
REM Reset run health - removes failed runs from history so scheduled runs can proceed
cd /d "%~dp0.."
python automation/reset_run_health.py %*
pause
