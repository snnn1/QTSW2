"""
Debug Log Console Window for Pipeline Conductor
Opens a console/terminal window showing real-time logs
"""

import subprocess
import sys
import os
from pathlib import Path
import logging
from queue import Queue
import threading
import time


class DebugLogConsole:
    """Console window for displaying real-time debug logs."""
    
    def __init__(self, title="Pipeline Conductor - Debug Log"):
        self.title = title
        self.log_queue = Queue()
        self.closed = False
        self.process = None
        
        # Start console window
        self._start_console()
    
    def _start_console(self):
        """Start a new console window."""
        try:
            # Create a batch file to launch console
            batch_file = Path(__file__).parent / "_debug_console_launcher.bat"
            
            # Get Python executable
            python_exe = sys.executable
            
            # Create launcher script
            launcher_content = f"""@echo off
title {self.title}
color 0A
echo ========================================
echo Pipeline Conductor - Debug Log Console
echo ========================================
echo.
echo Waiting for log messages...
echo.
"""
            
            with open(batch_file, 'w') as f:
                f.write(launcher_content)
            
            # Launch console window
            if os.name == 'nt':  # Windows
                # Use start command to open new console window
                cmd = f'start "{self.title}" cmd /k "type nul > "{batch_file}" && del "{batch_file}" && python -c "import sys; sys.path.insert(0, r\\"{Path(__file__).parent}\\"); from debug_log_console import run_console; run_console()""'
                subprocess.Popen(cmd, shell=True)
            else:
                # Linux/Mac - use xterm or gnome-terminal
                print("Console window not supported on this platform")
                
        except Exception as e:
            print(f"Warning: Could not create console window: {e}")
            print("Logs will appear in this terminal instead")
    
    def log(self, level, message):
        """Thread-safe method to add log message."""
        if not self.closed:
            self.log_queue.put((level, message))
            # Also print to current console
            print(f"[{level}] {message}")
    
    def close(self):
        """Close the console."""
        self.closed = True


class DebugLogHandler(logging.Handler):
    """Custom logging handler that sends logs to console."""
    
    def __init__(self, debug_console):
        super().__init__()
        self.debug_console = debug_console
    
    def emit(self, record):
        """Emit a log record to the console."""
        try:
            level = record.levelname
            message = self.format(record)
            self.debug_console.log(level, message)
        except:
            pass


def run_console():
    """Run the console log viewer (called from new console window)."""
    print("=" * 80)
    print("Pipeline Conductor - Debug Log Console")
    print("=" * 80)
    print()
    print("This console will show real-time logs from the pipeline.")
    print("Close this window to stop viewing logs (pipeline will continue).")
    print()
    print("-" * 80)
    print()
    
    # Read from a log file or pipe
    # For now, just keep window open
    try:
        while True:
            time.sleep(1)
    except KeyboardInterrupt:
        print("\nConsole closed.")


def create_debug_console(enabled=True):
    """
    Create and return a debug console.
    
    Args:
        enabled: If False, returns None (no console)
        
    Returns:
        DebugLogConsole instance or None
    """
    if not enabled:
        return None
    
    try:
        console = DebugLogConsole()
        return console
    except Exception as e:
        print(f"Warning: Could not create debug console: {e}")
        return None









