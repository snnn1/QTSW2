"""
MatrixState - In-process state layer for API and other long-lived consumers.

Holds current matrix DataFrame, file path, mtime for invalidation, and optional
cached views. Eligibility builder, CLI, and subprocesses use disk directly.
MatrixState is NOT a distributed/shared-memory design.
"""

import threading
from pathlib import Path
from typing import Optional, Dict, Any, Tuple, List
import pandas as pd

logger = __import__("logging").getLogger(__name__)

_state_lock = threading.Lock()
_matrix_df: Optional[pd.DataFrame] = None
_last_path: Optional[Path] = None
_last_mtime: Optional[float] = None
_stats_cache: Optional[Dict[str, Any]] = None
_grouped_by_stream: Optional[Dict[str, pd.DataFrame]] = None


def get_matrix_state(
    matrix_dir: str = "data/master_matrix",
    file_path: Optional[Path] = None,
) -> Tuple[Optional[pd.DataFrame], Optional[Path], bool]:
    """
    Get current matrix from state or load from disk.
    Returns (df, path_loaded, from_cache).
    Invalidate when file mtime changes.
    """
    global _matrix_df, _last_path, _last_mtime, _grouped_by_stream, _stats_cache
    from .file_manager import get_best_matrix_file, load_existing_matrix

    path_to_check = file_path
    if path_to_check is None:
        path_to_check = get_best_matrix_file(matrix_dir)
    if path_to_check is None or not path_to_check.exists():
        return None, None, False

    mtime = path_to_check.stat().st_mtime
    with _state_lock:
        if _matrix_df is not None and _last_path == path_to_check and _last_mtime == mtime:
            return _matrix_df, _last_path, True
        # Invalidate stale state
        _matrix_df = None
        _last_path = None
        _last_mtime = None
        _grouped_by_stream = None
        _stats_cache = None

    df = pd.read_parquet(path_to_check)
    with _state_lock:
        _matrix_df = df
        _last_path = path_to_check
        _last_mtime = mtime
    return df, path_to_check, False


def invalidate_matrix_state() -> None:
    """Clear in-memory state. Call when a new matrix file is written."""
    global _matrix_df, _last_path, _last_mtime, _stats_cache, _grouped_by_stream
    with _state_lock:
        _matrix_df = None
        _last_path = None
        _last_mtime = None
        _stats_cache = None
        _grouped_by_stream = None
    logger.debug("MatrixState invalidated")


def get_grouped_by_stream(df: pd.DataFrame) -> Dict[str, pd.DataFrame]:
    """Lightweight stream lookup. Cached per MatrixState lifecycle."""
    global _grouped_by_stream
    with _state_lock:
        if _grouped_by_stream is not None:
            return _grouped_by_stream
    if "Stream" not in df.columns or df.empty:
        return {}
    grouped = {s: g for s, g in df.groupby("Stream")}
    with _state_lock:
        _grouped_by_stream = grouped
    return grouped


def cache_stats(stats: Dict[str, Any]) -> None:
    """Store stats for common requests. Cleared on invalidation."""
    global _stats_cache
    with _state_lock:
        _stats_cache = stats


def get_cached_stats() -> Optional[Dict[str, Any]]:
    """Retrieve cached stats if available."""
    with _state_lock:
        return _stats_cache
