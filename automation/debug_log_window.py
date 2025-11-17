"""
Debug Log Window for Pipeline Conductor
Opens a GUI window showing real-time logs
"""

import tkinter as tk
from tkinter import scrolledtext, font
import logging
import threading
from queue import Queue
from datetime import datetime
from pathlib import Path


class DebugLogWindow:
    """GUI window for displaying real-time debug logs."""
    
    def __init__(self, title="Pipeline Conductor - Debug Log"):
        self.root = tk.Tk()
        self.root.title(title)
        self.root.geometry("1000x600")
        
        # Queue for thread-safe log messages
        self.log_queue = Queue()
        
        # Color scheme
        self.colors = {
            'DEBUG': '#888888',
            'INFO': '#000000',
            'WARNING': '#FF8800',
            'ERROR': '#FF0000',
            'CRITICAL': '#FF0000',
        }
        
        # Create UI
        self._create_ui()
        
        # Start processing log queue
        self.root.after(100, self._process_log_queue)
        
        # Handle window close
        self.root.protocol("WM_DELETE_WINDOW", self._on_close)
        
        self.closed = False
    
    def _create_ui(self):
        """Create the UI components."""
        # Top frame with controls
        control_frame = tk.Frame(self.root)
        control_frame.pack(fill=tk.X, padx=5, pady=5)
        
        # Title
        title_label = tk.Label(
            control_frame,
            text="🔍 Pipeline Conductor - Debug Log",
            font=font.Font(size=12, weight="bold")
        )
        title_label.pack(side=tk.LEFT)
        
        # Clear button
        clear_btn = tk.Button(
            control_frame,
            text="Clear",
            command=self._clear_logs,
            width=10
        )
        clear_btn.pack(side=tk.RIGHT, padx=5)
        
        # Auto-scroll checkbox
        self.auto_scroll_var = tk.BooleanVar(value=True)
        auto_scroll_cb = tk.Checkbutton(
            control_frame,
            text="Auto-scroll",
            variable=self.auto_scroll_var
        )
        auto_scroll_cb.pack(side=tk.RIGHT, padx=5)
        
        # Log text area
        self.log_text = scrolledtext.ScrolledText(
            self.root,
            wrap=tk.WORD,
            font=font.Font(family="Consolas", size=9),
            bg="#F5F5F5",
            fg="#000000",
            state=tk.DISABLED
        )
        self.log_text.pack(fill=tk.BOTH, expand=True, padx=5, pady=5)
        
        # Configure tags for colors
        for level, color in self.colors.items():
            self.log_text.tag_config(level, foreground=color)
        
        # Status bar
        self.status_bar = tk.Label(
            self.root,
            text="Ready",
            relief=tk.SUNKEN,
            anchor=tk.W,
            bg="#E0E0E0"
        )
        self.status_bar.pack(fill=tk.X, side=tk.BOTTOM)
        
        # Add welcome message
        self._add_log("INFO", "=" * 80)
        self._add_log("INFO", "Pipeline Conductor Debug Log Window")
        self._add_log("INFO", f"Started at: {datetime.now().strftime('%Y-%m-%d %H:%M:%S')}")
        self._add_log("INFO", "=" * 80)
    
    def _add_log(self, level, message):
        """Add a log message to the text widget."""
        if self.closed:
            return
        
        self.log_text.config(state=tk.NORMAL)
        
        # Format timestamp
        timestamp = datetime.now().strftime("%H:%M:%S")
        formatted_msg = f"[{timestamp}] {level:8s} | {message}\n"
        
        # Insert with color tag
        self.log_text.insert(tk.END, formatted_msg, level)
        
        # Auto-scroll if enabled
        if self.auto_scroll_var.get():
            self.log_text.see(tk.END)
        
        self.log_text.config(state=tk.DISABLED)
        
        # Update status bar
        self.status_bar.config(text=f"Last update: {timestamp} | Level: {level}")
    
    def _process_log_queue(self):
        """Process log messages from queue (called periodically)."""
        try:
            while True:
                try:
                    level, message = self.log_queue.get_nowait()
                    self._add_log(level, message)
                except:
                    break
        except:
            pass
        
        # Schedule next check
        if not self.closed:
            self.root.after(100, self._process_log_queue)
    
    def _clear_logs(self):
        """Clear all logs."""
        self.log_text.config(state=tk.NORMAL)
        self.log_text.delete(1.0, tk.END)
        self.log_text.config(state=tk.DISABLED)
        self._add_log("INFO", "Log cleared")
    
    def _on_close(self):
        """Handle window close."""
        self.closed = True
        self.root.destroy()
    
    def log(self, level, message):
        """Thread-safe method to add log message."""
        if not self.closed:
            self.log_queue.put((level, message))
    
    def show(self):
        """Show the window (non-blocking)."""
        self.root.update()
    
    def run(self):
        """Run the window (blocking)."""
        self.root.mainloop()
    
    def update(self):
        """Update the window (call periodically)."""
        if not self.closed:
            self.root.update_idletasks()


class DebugLogHandler(logging.Handler):
    """Custom logging handler that sends logs to debug window."""
    
    def __init__(self, debug_window):
        super().__init__()
        self.debug_window = debug_window
    
    def emit(self, record):
        """Emit a log record to the debug window."""
        try:
            level = record.levelname
            message = self.format(record)
            self.debug_window.log(level, message)
        except:
            pass


def create_debug_window(enabled=True):
    """
    Create and return a debug log window.
    
    Args:
        enabled: If False, returns None (no window)
        
    Returns:
        DebugLogWindow instance or None
    """
    if not enabled:
        return None
    
    try:
        window = DebugLogWindow()
        # Start window in separate thread to avoid blocking
        threading.Thread(target=window.run, daemon=True).start()
        # Give it a moment to initialize
        import time
        time.sleep(0.5)
        return window
    except Exception as e:
        print(f"Warning: Could not create debug window: {e}")
        return None



