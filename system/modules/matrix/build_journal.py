"""
Matrix Build Journal - Append-only audit trail for matrix and resequence activity.

Similar in spirit to robot-side journaling. Records build/resequence/timetable
events with metadata for operational reconstruction and failure correlation.
"""

import json
import threading
from pathlib import Path
from datetime import datetime
from typing import Optional, Dict, Any

_log_lock = threading.Lock()
JOURNAL_FILE = Path("logs/matrix_build_journal.jsonl")


def journal_event(
    event_type: str,
    timestamp: Optional[str] = None,
    mode: Optional[str] = None,
    rows_written: Optional[int] = None,
    streams_processed: Optional[int] = None,
    date_window_min: Optional[str] = None,
    date_window_max: Optional[str] = None,
    matrix_file_path: Optional[str] = None,
    duration_ms: Optional[int] = None,
    error: Optional[str] = None,
    **extra: Any,
) -> None:
    """
    Append a journal event. Event types: build_start, build_complete, resequence_start,
    resequence_complete, timetable_start, timetable_complete, failure.
    """
    event = {
        "timestamp": timestamp or datetime.utcnow().isoformat() + "Z",
        "event": event_type,
    }
    if mode is not None:
        event["mode"] = mode
    if rows_written is not None:
        event["rows_written"] = rows_written
    if streams_processed is not None:
        event["streams_processed"] = streams_processed
    if date_window_min is not None:
        event["date_window_min"] = date_window_min
    if date_window_max is not None:
        event["date_window_max"] = date_window_max
    if matrix_file_path is not None:
        event["matrix_file_path"] = matrix_file_path
    if duration_ms is not None:
        event["duration_ms"] = duration_ms
    if error is not None:
        event["error"] = error
    event.update({k: v for k, v in extra.items() if v is not None})

    with _log_lock:
        JOURNAL_FILE.parent.mkdir(parents=True, exist_ok=True)
        try:
            with open(JOURNAL_FILE, "a", encoding="utf-8") as f:
                f.write(json.dumps(event) + "\n")
        except Exception:
            pass
