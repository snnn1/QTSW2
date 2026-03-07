"""
Caching utilities for Master Matrix.

This module provides caching functionality for expensive operations like
stream discovery, time normalization, and RS calculations.
"""

import logging
from typing import Dict, Optional, Any, Callable, Tuple, List
from functools import lru_cache
from pathlib import Path
from datetime import datetime, timedelta

logger = logging.getLogger(__name__)

# Global cache for time normalization (small, frequently used)
_time_normalization_cache: Dict[str, str] = {}

# Global cache for stream discovery (keyed by directory path and mtime)
# Using Tuple for Python 3.8+ compatibility (instead of tuple[])
_stream_discovery_cache: Dict[str, Tuple[List[str], float]] = {}  # path -> (streams, mtime)

# Global cache for parquet file discovery per stream (keyed by stream_dir + stream_id)
_parquet_files_cache: Dict[Tuple[str, str], Tuple[List[Path], float]] = {}  # (dir, stream_id) -> (files, mtime)


def normalize_time_cached(time_str: str) -> str:
    """
    Normalize time with caching.
    
    Args:
        time_str: Time string to normalize
        
    Returns:
        Normalized time string
    """
    if not time_str:
        return str(time_str)
    
    # Check cache first
    if time_str in _time_normalization_cache:
        return _time_normalization_cache[time_str]
    
    # Normalize
    time_str_clean = str(time_str).strip()
    parts = time_str_clean.split(':')
    if len(parts) == 2:
        hours = parts[0].zfill(2)
        minutes = parts[1].zfill(2)
        normalized = f"{hours}:{minutes}"
    else:
        normalized = time_str_clean
    
    # Cache result
    _time_normalization_cache[time_str] = normalized
    
    return normalized


def clear_time_cache():
    """Clear the time normalization cache."""
    global _time_normalization_cache
    _time_normalization_cache.clear()


def get_cached_streams(analyzer_runs_dir: Path, discover_func: Callable) -> List[str]:
    """
    Get cached stream discovery results, or discover if cache is invalid.
    
    Args:
        analyzer_runs_dir: Directory to check
        discover_func: Function to call if cache is invalid
        
    Returns:
        List of stream IDs
    """
    cache_key = str(analyzer_runs_dir.resolve())
    
    # Check if directory modification time changed (cache invalidation)
    try:
        current_mtime = analyzer_runs_dir.stat().st_mtime
    except (OSError, FileNotFoundError):
        # Directory doesn't exist or can't be accessed - discover fresh
        return discover_func(analyzer_runs_dir)
    
    # Check cache
    if cache_key in _stream_discovery_cache:
        cached_streams, cached_mtime = _stream_discovery_cache[cache_key]
        if cached_mtime == current_mtime:
            logger.debug(f"Using cached stream discovery for {analyzer_runs_dir}")
            return cached_streams
    
    # Cache miss or invalid - discover fresh
    streams = discover_func(analyzer_runs_dir)
    
    # Update cache
    _stream_discovery_cache[cache_key] = (streams, current_mtime)
    
    return streams


def clear_stream_cache():
    """Clear the stream discovery cache."""
    global _stream_discovery_cache
    _stream_discovery_cache.clear()


def get_cached_parquet_files(
    stream_dir: Path,
    stream_id: str,
    discover_func: Callable[[Path, str], List[Path]],
) -> List[Path]:
    """
    Get cached parquet file list for a stream, or discover if cache is invalid.
    
    Args:
        stream_dir: Directory for the stream (e.g., analyzer_runs/ES1)
        stream_id: Stream ID (e.g., "ES1")
        discover_func: Function to call if cache is invalid (stream_dir, stream_id) -> List[Path]
        
    Returns:
        List of parquet file paths
    """
    cache_key = (str(stream_dir.resolve()), stream_id)
    
    try:
        current_mtime = stream_dir.stat().st_mtime
    except (OSError, FileNotFoundError):
        return discover_func(stream_dir, stream_id)
    
    if cache_key in _parquet_files_cache:
        cached_files, cached_mtime = _parquet_files_cache[cache_key]
        if cached_mtime == current_mtime:
            logger.debug(f"Using cached parquet files for {stream_id}")
            return cached_files
    
    files = discover_func(stream_dir, stream_id)
    _parquet_files_cache[cache_key] = (files, current_mtime)
    return files


def clear_parquet_files_cache():
    """Clear the parquet file discovery cache."""
    global _parquet_files_cache
    _parquet_files_cache.clear()


def clear_all_caches():
    """Clear all caches."""
    clear_time_cache()
    clear_stream_cache()
    clear_parquet_files_cache()

