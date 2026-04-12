"""
Matrix Feature Usage and Timing Instrumentation

Emits structured JSONL events for:
- Feature usage: logs/matrix_feature_usage.jsonl
- Timing/telemetry: logs/matrix_timing.jsonl (Phase 2 - cold vs warm, resequence vs rebuild)

Designed to be lightweight and non-intrusive. Safe in production.
"""

import json
import time
from pathlib import Path
from datetime import datetime
from functools import wraps
from typing import Dict, Any, Optional, Callable
import threading

# Thread-safe logging
_log_lock = threading.Lock()
LOG_FILE = Path("logs/matrix_feature_usage.jsonl")
TIMING_LOG_FILE = Path("logs/matrix_timing.jsonl")


def log_feature_usage(
    mode: str,
    subsystem: str,
    parameters: Optional[Dict[str, Any]] = None,
    metrics: Optional[Dict[str, Any]] = None
):
    """
    Log feature usage to JSONL file.
    
    Args:
        mode: Mode name (e.g., "build_master_matrix", "update_master_matrix")
        subsystem: Subsystem name (e.g., "MatrixBuilder.load_analyzer_data")
        parameters: Mode parameters (e.g., {"streams": None, "authoritative": False})
        metrics: Performance/metrics (e.g., {"rows_in": 1000, "rows_out": 950, "duration_ms": 1234})
    """
    event = {
        "timestamp": datetime.utcnow().isoformat() + "Z",
        "mode": mode,
        "subsystem": subsystem,
        "parameters": parameters or {},
        "metrics": metrics or {}
    }
    
    # Thread-safe write
    with _log_lock:
        LOG_FILE.parent.mkdir(parents=True, exist_ok=True)
        try:
            with open(LOG_FILE, "a", encoding="utf-8") as f:
                f.write(json.dumps(event) + "\n")
        except Exception as e:
            # Silently fail - don't break matrix operations if logging fails
            pass


def instrument(mode: str, subsystem: Optional[str] = None):
    """
    Decorator to instrument a method.
    
    Usage:
        @instrument("build_master_matrix", "MatrixBuilder")
        def load_analyzer_data(self, ...):
            ...
    
    Args:
        mode: Mode name
        subsystem: Optional subsystem name (defaults to function name)
    """
    def decorator(func: Callable) -> Callable:
        subsystem_name = subsystem or f"{func.__module__}.{func.__name__}"
        
        @wraps(func)
        def wrapper(*args, **kwargs):
            start_time = time.time()
            
            # Extract parameters for logging (sanitize for JSON)
            params = {}
            if kwargs:
                # Only log simple types
                for k, v in kwargs.items():
                    if v is None or isinstance(v, (str, int, float, bool, list)):
                        params[k] = v
                    elif isinstance(v, dict):
                        # Log dict keys only
                        params[k] = list(v.keys()) if v else []
            
            try:
                result = func(*args, **kwargs)
                
                # Compute metrics
                duration_ms = int((time.time() - start_time) * 1000)
                metrics = {"duration_ms": duration_ms}
                
                # Try to extract row counts if result is DataFrame
                if hasattr(result, '__len__'):
                    try:
                        metrics["rows_out"] = len(result)
                    except:
                        pass
                
                log_feature_usage(mode, subsystem_name, params, metrics)
                
                return result
            except Exception as e:
                # Log error but don't fail
                metrics = {
                    "duration_ms": int((time.time() - start_time) * 1000),
                    "error": str(type(e).__name__)
                }
                log_feature_usage(mode, subsystem_name, params, metrics)
                raise
        
        return wrapper
    return decorator


class InstrumentationContext:
    """
    Context manager for detailed instrumentation.
    
    Usage:
        with InstrumentationContext("build_master_matrix", "MatrixBuilder.load_analyzer_data") as ctx:
            ctx.set_parameter("streams", ["ES1"])
            ctx.set_metric("files_read", 5)
            # ... do work ...
            ctx.set_metric("rows_in", 1000)
            ctx.set_metric("rows_out", 950)
    """
    
    def __init__(self, mode: str, subsystem: str):
        self.mode = mode
        self.subsystem = subsystem
        self.start_time = None
        self.parameters = {}
        self.metrics = {}
    
    def __enter__(self):
        self.start_time = time.time()
        return self
    
    def __exit__(self, exc_type, exc_val, exc_tb):
        duration_ms = int((time.time() - self.start_time) * 1000)
        self.metrics["duration_ms"] = duration_ms
        
        if exc_type is not None:
            self.metrics["error"] = str(exc_type.__name__)
        
        log_feature_usage(self.mode, self.subsystem, self.parameters, self.metrics)
        return False  # Don't suppress exceptions
    
    def set_parameter(self, key: str, value: Any):
        """Set a parameter value."""
        if value is None or isinstance(value, (str, int, float, bool, list)):
            self.parameters[key] = value
        elif isinstance(value, dict):
            self.parameters[key] = list(value.keys()) if value else []
    
    def set_metric(self, key: str, value: Any):
        """Set a metric value."""
        if isinstance(value, (int, float, bool, str)):
            self.metrics[key] = value


def log_timing_event(
    phase: str,
    duration_ms: int,
    row_count: Optional[int] = None,
    stream_count: Optional[int] = None,
    date_min: Optional[str] = None,
    date_max: Optional[str] = None,
    mode: Optional[str] = None,
    cache_hit: Optional[bool] = None,
    file_path: Optional[str] = None,
    error: Optional[str] = None,
    **extra: Any,
):
    """
    Emit structured JSONL timing event for matrix/timetable phases.
    Used for cold vs warm load, resequence vs rebuild, API cache analysis.

    Phases: matrix_load, sequencer_processing, matrix_save, timetable_generation,
            api_matrix_load, rolling_resequence, full_rebuild
    """
    event = {
        "timestamp": datetime.utcnow().isoformat() + "Z",
        "phase": phase,
        "duration_ms": duration_ms,
    }
    if row_count is not None:
        event["row_count"] = row_count
    if stream_count is not None:
        event["stream_count"] = stream_count
    if date_min is not None:
        event["date_min"] = date_min
    if date_max is not None:
        event["date_max"] = date_max
    if mode is not None:
        event["mode"] = mode
    if cache_hit is not None:
        event["cache_hit"] = cache_hit
    if file_path is not None:
        event["file_path"] = file_path
    if error is not None:
        event["error"] = error
    event.update({k: v for k, v in extra.items() if v is not None and isinstance(v, (str, int, float, bool))})

    with _log_lock:
        TIMING_LOG_FILE.parent.mkdir(parents=True, exist_ok=True)
        try:
            with open(TIMING_LOG_FILE, "a", encoding="utf-8") as f:
                f.write(json.dumps(event) + "\n")
        except Exception:
            pass
