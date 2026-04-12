"""
Error Logger - Centralized error logging for monitoring and debugging
"""

import logging
import sys
import traceback
from pathlib import Path
from datetime import datetime
from typing import Optional, Dict, Any
import json

# Add project root to path
qtsw2_root = Path(__file__).parent.parent.parent.parent
if str(qtsw2_root) not in sys.path:
    sys.path.insert(0, str(qtsw2_root))

# Error log file
ERROR_LOG_FILE = qtsw2_root / "logs" / "error_log.jsonl"
ERROR_LOG_FILE.parent.mkdir(parents=True, exist_ok=True)


class ErrorLogger:
    """Centralized error logging with structured output"""
    
    def __init__(self, log_file: Path = ERROR_LOG_FILE):
        self.log_file = log_file
        self.log_file.parent.mkdir(parents=True, exist_ok=True)
    
    def log_error(
        self,
        error_type: str,
        error_message: str,
        component: str = "unknown",
        context: Optional[Dict[str, Any]] = None,
        exception: Optional[Exception] = None,
        traceback_str: Optional[str] = None
    ):
        """
        Log an error with full context.
        
        Args:
            error_type: Type of error (e.g., "PipelineError", "StateTransitionError")
            error_message: Human-readable error message
            component: Component where error occurred (e.g., "orchestrator", "runner")
            context: Additional context dictionary
            exception: Exception object if available
            traceback_str: Traceback string if available
        """
        error_entry = {
            "timestamp": datetime.now().isoformat(),
            "error_type": error_type,
            "error_message": error_message,
            "component": component,
            "context": context or {},
        }
        
        if exception:
            error_entry["exception"] = {
                "type": type(exception).__name__,
                "message": str(exception),
                "args": [str(arg) for arg in exception.args] if exception.args else []
            }
        
        if traceback_str:
            error_entry["traceback"] = traceback_str
        elif exception:
            error_entry["traceback"] = "".join(traceback.format_exception(
                type(exception), exception, exception.__traceback__
            ))
        
        # Write to JSONL file
        try:
            with open(self.log_file, "a", encoding="utf-8") as f:
                f.write(json.dumps(error_entry) + "\n")
                f.flush()
        except Exception as e:
            # Fallback to stderr if file write fails
            print(f"ERROR: Failed to write error log: {e}", file=sys.stderr)
            print(json.dumps(error_entry), file=sys.stderr)
    
    def get_recent_errors(self, limit: int = 50) -> list:
        """Get recent errors from log file"""
        errors = []
        if not self.log_file.exists():
            return errors
        
        try:
            with open(self.log_file, "r", encoding="utf-8") as f:
                lines = f.readlines()
                for line in lines[-limit:]:
                    try:
                        errors.append(json.loads(line.strip()))
                    except json.JSONDecodeError:
                        continue
        except Exception as e:
            print(f"Error reading error log: {e}", file=sys.stderr)
        
        return errors
    
    def get_errors_by_component(self, component: str, limit: int = 20) -> list:
        """Get recent errors for a specific component"""
        all_errors = self.get_recent_errors(limit=limit * 10)
        return [e for e in all_errors if e.get("component") == component][-limit:]
    
    def get_errors_by_type(self, error_type: str, limit: int = 20) -> list:
        """Get recent errors of a specific type"""
        all_errors = self.get_recent_errors(limit=limit * 10)
        return [e for e in all_errors if e.get("error_type") == error_type][-limit:]


# Global error logger instance
error_logger = ErrorLogger()


def setup_error_logging():
    """Setup error logging for the application"""
    # Create custom logging handler that also writes to error log
    class ErrorLogHandler(logging.Handler):
        def emit(self, record):
            if record.levelno >= logging.ERROR:
                error_logger.log_error(
                    error_type=record.levelname,
                    error_message=record.getMessage(),
                    component=record.name,
                    context={
                        "level": record.levelname,
                        "module": record.module,
                        "funcName": record.funcName,
                        "lineno": record.lineno,
                    },
                    traceback_str=record.exc_text if record.exc_info else None
                )
    
    # Add handler to root logger
    root_logger = logging.getLogger()
    error_handler = ErrorLogHandler()
    error_handler.setLevel(logging.ERROR)
    root_logger.addHandler(error_handler)
    
    return error_logger


if __name__ == "__main__":
    # Test the error logger
    logger = setup_error_logging()
    
    # Test logging
    try:
        raise ValueError("Test error")
    except Exception as e:
        error_logger.log_error(
            error_type="TestError",
            error_message="This is a test error",
            component="error_logger",
            exception=e
        )
    
    print(f"Error log file: {ERROR_LOG_FILE}")
    print(f"Recent errors: {len(error_logger.get_recent_errors())}")













