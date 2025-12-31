"""
Stream discovery and filter management for Master Matrix.

This module handles discovering streams from the analyzer_runs directory
and managing per-stream filter configurations.
"""

import re
import logging
from pathlib import Path
from typing import List, Dict, Optional

logger = logging.getLogger(__name__)

# Try to use cached version if available
try:
    from .cache import get_cached_streams
    _use_cache = True
except ImportError:
    _use_cache = False


def _discover_streams_impl(analyzer_runs_dir: Path) -> List[str]:
    """
    Internal implementation of stream discovery (without caching).
    
    Args:
        analyzer_runs_dir: Path to analyzer_runs directory
        
    Returns:
        List of stream IDs found (e.g., ["ES1", "ES2", "GC1", ...])
    """
    streams = []
    if not analyzer_runs_dir.exists():
        logger.warning(f"Analyzer runs directory not found: {analyzer_runs_dir}")
        return streams
    
    # Pattern: ES1, GC2, CL1, RTY1, RTY2, etc. (2-3 letters + 1 or 2)
    stream_pattern = re.compile(r'^([A-Z]{2,3})([12])$')
    
    for item in analyzer_runs_dir.iterdir():
        if not item.is_dir():
            continue
        
        # Check if directory name matches stream pattern
        match = stream_pattern.match(item.name)
        if match:
            stream_id = item.name  # e.g., "ES1", "GC2"
            streams.append(stream_id)
    
    streams.sort()  # Sort alphabetically for consistency
    return streams


def discover_streams(analyzer_runs_dir: Path) -> List[str]:
    """
    Auto-discover streams by scanning analyzer_runs directory.
    Looks for subdirectories matching stream patterns (ES1, ES2, GC1, RTY1, RTY2, etc.).
    
    Uses caching if available to avoid repeated filesystem scans.
    
    Args:
        analyzer_runs_dir: Path to analyzer_runs directory
        
    Returns:
        List of stream IDs found (e.g., ["ES1", "ES2", "GC1", "RTY1", "RTY2", ...])
    """
    if _use_cache:
        streams = get_cached_streams(analyzer_runs_dir, _discover_streams_impl)
    else:
        streams = _discover_streams_impl(analyzer_runs_dir)
    
    logger.info(f"Discovered {len(streams)} streams: {streams}")
    return streams


def normalize_filter(filters: Dict) -> Dict:
    """
    Normalize a single stream filter (ensure exclude_times are strings).
    
    Args:
        filters: Filter dictionary with potentially mixed types
        
    Returns:
        Normalized filter dictionary with all exclude_times as strings
    """
    return {
        "exclude_days_of_week": filters.get('exclude_days_of_week', []),
        "exclude_days_of_month": filters.get('exclude_days_of_month', []),
        "exclude_times": [str(t) for t in filters.get('exclude_times', [])]  # Ensure strings
    }


def ensure_default_filters(streams: List[str], stream_filters: Dict[str, Dict]) -> Dict[str, Dict]:
    """
    Ensure all streams have filter entries (even if empty).
    
    Args:
        streams: List of stream IDs
        stream_filters: Existing filter dictionary
        
    Returns:
        Updated filter dictionary with defaults for all streams
    """
    updated_filters = stream_filters.copy()
    for stream in streams:
        if stream not in updated_filters:
            updated_filters[stream] = {
                "exclude_days_of_week": [],
                "exclude_days_of_month": [],
                "exclude_times": []
            }
    return updated_filters


def update_stream_filters(
    existing_filters: Dict[str, Dict],
    new_filters: Optional[Dict[str, Dict]],
    streams: List[str],
    merge: bool = False
) -> Dict[str, Dict]:
    """
    Update stream filters.
    
    Args:
        existing_filters: Current filter dictionary
        new_filters: New filters to apply (can be None)
        streams: List of all stream IDs (for ensuring defaults)
        merge: If True, merge with existing filters. If False, replace all filters.
        
    Returns:
        Updated filter dictionary
    """
    if new_filters:
        if merge:
            # Merge provided filters with existing filters (preserve other streams' filters)
            updated_filters = existing_filters.copy()
            for stream_id, filters in new_filters.items():
                updated_filters[stream_id] = normalize_filter(filters)
        else:
            # Replace all filters
            updated_filters = {}
            for stream_id, filters in new_filters.items():
                updated_filters[stream_id] = normalize_filter(filters)
    else:
        updated_filters = existing_filters.copy()
    
    # Always ensure defaults for all streams
    updated_filters = ensure_default_filters(streams, updated_filters)
    
    return updated_filters



