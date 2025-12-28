"""
Utility functions for Master Matrix processing.

This module contains helper functions for time normalization, session detection,
and score calculation used throughout the master matrix processing pipeline.
"""

from typing import Dict

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



