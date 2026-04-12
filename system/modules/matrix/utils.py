"""
Utility functions for Master Matrix processing.

This module contains helper functions for time normalization, session detection,
and score calculation used throughout the master matrix processing pipeline.
"""

from typing import Dict, Optional
import pandas as pd
import logging

logger = logging.getLogger(__name__)

def time_sort_key(time_str: str) -> tuple:
    """
    Convert time string to tuple for proper chronological sorting.
    
    Args:
        time_str: Time string in various formats (e.g., "7:30", "07:30")
        
    Returns:
        Tuple of (hour, minute) as integers for sorting
    """
    normalized = normalize_time(time_str)
    parts = normalized.split(':')
    return (int(parts[0]), int(parts[1]))  # (hour, minute)


def normalize_date(date_value, errors: str = 'coerce') -> Optional[pd.Timestamp]:
    """
    Normalize date value to pandas Timestamp.
    
    Handles various date formats consistently:
    - String dates (DD/MM/YYYY, YYYY-MM-DD, etc.)
    - Already datetime objects
    - NaN/None values
    
    Args:
        date_value: Date value in various formats
        errors: How to handle errors ('coerce', 'raise', 'ignore')
                Default: 'coerce' (returns NaT for invalid dates)
        
    Returns:
        pandas Timestamp or NaT if invalid/coerced
    """
    if pd.isna(date_value) or date_value is None:
        return pd.NaT
    
    # If already a Timestamp, return as-is
    if isinstance(date_value, pd.Timestamp):
        return date_value
    
    # Convert to datetime
    return pd.to_datetime(date_value, errors=errors)


def normalize_time(time_str: str) -> str:
    """
    Normalize time format to HH:MM (e.g., "7:30" -> "07:30").
    
    Uses cached version if available for better performance.
    
    Args:
        time_str: Time string in various formats
        
    Returns:
        Normalized time string in HH:MM format
    """
    # Try to use cached version if available
    try:
        from .cache import normalize_time_cached
        return normalize_time_cached(time_str)
    except (ImportError, AttributeError):
        # Fallback to direct implementation if cache module not available
        if not time_str:
            return str(time_str)
        time_str = str(time_str).strip()
        parts = time_str.split(':')
        if len(parts) == 2:
            hours = parts[0].zfill(2)  # Pad with leading zero if needed
            minutes = parts[1].zfill(2)
            return f"{hours}:{minutes}"
        return time_str


def get_session_for_time(time: str, slot_ends: Dict) -> str:
    """
    Get session (S1 or S2) for a given time.
    
    Args:
        time: Time string (e.g., "07:30")
        slot_ends: Dictionary mapping session names to lists of time strings
                   Example: {"S1": ["07:30", "08:00", "09:00"], ...}
        
    Returns:
        Session name ("S1" or "S2"), defaults to "S1" if not found
    """
    normalized_time = normalize_time(time)
    for session, times in slot_ends.items():
        normalized_times = [normalize_time(t) for t in times]
        if normalized_time in normalized_times:
            return session
    return "S1"  # Default


def calculate_time_score(result: str) -> int:
    """
    Calculate score for time change logic (points-based system).
    
    Scoring system:
    - WIN: +1 point
    - LOSS: -2 points
    - BE/Break-Even: 0 points
    - NoTrade: 0 points
    - TIME: 0 points
    - Other: 0 points
    
    Args:
        result: Trade result string (Win, Loss, BE, NoTrade, etc.)
        
    Returns:
        Integer score for the result
    """
    result_upper = str(result).upper()
    if result_upper == "WIN":
        return 1
    elif result_upper == "LOSS":
        return -2  # Loss is -2 points (not -1)
    elif result_upper in ["BE", "BREAK_EVEN", "BREAKEVEN"]:
        return 0
    elif result_upper in ["NOTRADE", "NO_TRADE"]:
        return 0
    elif result_upper == "TIME":
        return 0
    else:
        return 0


def _enforce_trade_date_invariants(df: pd.DataFrame, context: str) -> pd.DataFrame:
    """
    Enforce trade_date invariants: canonical datetime column, Date synced from trade_date.
    
    INVARIANT MODEL:
    - trade_date is the canonical datetime-like column (source of truth; datetime64 with or without timezone)
    - Date is legacy-derived only: if present, always set Date = trade_date.copy(); never use Date as source-of-truth
    
    FAIL-CLOSED BEHAVIOR:
    - Missing trade_date → ValueError (contract violation)
    - trade_date exists but not datetime-like → attempt repair with pd.to_datetime(errors='raise')
    - If repair fails or dtype still not datetime-like → ValueError (fail-closed)
    - Warnings logged for repair attempts (visibility), but failures must hard error
    
    Args:
        df: DataFrame to enforce invariants on (mutated in place)
        context: Context string for error messages (e.g., "rolling_resequence_pre_concat")
        
    Returns:
        DataFrame (same reference, mutated in place)
        
    Raises:
        ValueError: If trade_date is missing (contract violation) or repair fails (fail-closed)
    """
    # Contract check: trade_date must exist
    if 'trade_date' not in df.columns:
        raise ValueError(
            f"{context}: trade_date column missing (contract violation). "
            f"trade_date is the canonical datetime column and must exist."
        )
    
    # Repair attempt: if trade_date not datetime-like, attempt conversion
    if not pd.api.types.is_datetime64_any_dtype(df['trade_date']):
        logger.warning(
            f"{context}: trade_date is {df['trade_date'].dtype}, attempting repair to datetime64"
        )
        df['trade_date'] = pd.to_datetime(df['trade_date'], errors='raise')
        
        # Verify repair succeeded (fail-closed)
        if not pd.api.types.is_datetime64_any_dtype(df['trade_date']):
            raise ValueError(
                f"{context}: Failed to repair trade_date dtype after conversion. "
                f"Current dtype: {df['trade_date'].dtype}. This is a fail-closed invariant violation."
            )
    
    # Date sync: always derive Date from trade_date (never parse Date into trade_date)
    # trade_date is canonical, Date is legacy-derived only
    if 'Date' in df.columns:
        df['Date'] = df['trade_date'].copy()
        
        # Verify Date dtype matches trade_date (fail-closed)
        if not pd.api.types.is_datetime64_any_dtype(df['Date']):
            raise ValueError(
                f"{context}: Date column dtype mismatch after sync from trade_date. "
                f"Date dtype: {df['Date'].dtype}, trade_date dtype: {df['trade_date'].dtype}. "
                f"This is a fail-closed invariant violation."
            )
    
    return df

