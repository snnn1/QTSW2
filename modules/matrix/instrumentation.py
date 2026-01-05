"""
Matrix Feature Usage Instrumentation

Emits structured JSONL events to logs/matrix_feature_usage.jsonl for tracking
which features, modes, and subsystems are actually used.

This instrumentation is designed to be lightweight and non-intrusive.
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
