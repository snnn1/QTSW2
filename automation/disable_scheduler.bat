@echo off
echo Disabling Pipeline Runner scheduler...
schtasks /change /tn "Pipeline Runner" /disable
if %errorlevel% equ 0 (
    echo.
    echo SUCCESS: Scheduler has been DISABLED
    echo The pipeline will NOT run automatically anymore.
) else (
    echo.
    echo ERROR: Could not disable scheduler
    echo You may need to run this as Administrator.
    echo.
    echo ALTERNATIVE: Open Task Scheduler manually:
    echo 1. Search for "Task Scheduler" in Windows Start menu
    echo 2. Find "Pipeline Runner" task
    echo 3. Right-click -^> Disable
)
pause

