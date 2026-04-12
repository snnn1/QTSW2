"""
Robot log file loader — shared logic for ledger builder and fill metrics.

Scans both logs/robot/ and logs/robot/archive/ for robot_*.jsonl files.
Ensures historical trades remain retrievable after log rotation and archiving.
"""
from pathlib import Path
from typing import List

from ..config import ROBOT_LOGS_DIR, ROBOT_LOGS_ARCHIVE_DIR


def get_robot_log_files_for_date(trading_date: str, max_files: int | None = None) -> List[Path]:
    """
    Get robot log files that may contain events for the given trading_date.

    Scans root (logs/robot/) first, then archive. Files sorted by mtime (newest first).
    For status API, use max_files to avoid slow scans over hundreds of MB.

    Args:
        trading_date: Trading date (YYYY-MM-DD) for context.
        max_files: If set, return only the N most recently modified files (for fast status).

    Returns:
        Sorted list of Path objects to robot_*.jsonl files.
    """
    log_files: List[Path] = []
    if ROBOT_LOGS_DIR.exists():
        log_files.extend(ROBOT_LOGS_DIR.glob("robot_*.jsonl"))
    if ROBOT_LOGS_ARCHIVE_DIR.exists():
        log_files.extend(ROBOT_LOGS_ARCHIVE_DIR.glob("robot_*.jsonl"))
    log_files = sorted(set(log_files), key=lambda p: p.stat().st_mtime if p.exists() else 0, reverse=True)
    if max_files is not None and len(log_files) > max_files:
        log_files = log_files[:max_files]
    return log_files
