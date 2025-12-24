"""
Trade selection for sequencer.

RESPONSIBILITY: Selects execution rows ONLY.
MUST NEVER infer, decide, or mutate time intent.
Time decisions are OWNED by sequencer_logic.py.

CONTRACT: Returns the trade for current_time ONLY, or None if no trade exists.
NO FALLBACK LOGIC - if current_time has no trade, return None.
"""

import logging
import sys
from typing import List, Optional, Dict
import pandas as pd

from .utils import get_session_for_time, normalize_time

logger = logging.getLogger(__name__)

# Session configuration - MUST match sequencer_logic.py (single source of truth)
# This is a read-only reference for trade selection, not for time decisions
SLOT_ENDS = {
    "S1": ["07:30", "08:00", "09:00"],
    "S2": ["09:30", "10:00", "10:30", "11:00"],
}


def filter_excluded_times(date_df: pd.DataFrame, exclude_times_str: List[str], stream_id: str, date) -> pd.DataFrame:
    """
    Filter out trades at excluded times from a day's DataFrame.
    
    Args:
        date_df: DataFrame for a single day
        exclude_times_str: List of excluded time strings
        stream_id: Stream ID for logging
        date: Date for logging
        
    Returns:
        Filtered DataFrame with excluded times removed
    """
    if not exclude_times_str:
        return date_df
    
    exclude_times_normalized = [str(t).strip() for t in exclude_times_str]
    date_df = date_df.copy()
    date_df['Time_str'] = date_df['Time'].apply(str).str.strip()
    
    # Remove ALL trades at excluded times
    before_count = len(date_df)
    date_df = date_df[~date_df['Time_str'].isin(exclude_times_normalized)].copy()
    after_count = len(date_df)
    
    if before_count != after_count:
        # Verify they're actually gone (safety check)
        remaining_excluded = date_df[date_df['Time_str'].isin(exclude_times_normalized)]
        if len(remaining_excluded) > 0:
            msg = f"[ERROR] Stream {stream_id} {date}: {len(remaining_excluded)} excluded trades still present after filtering!"
            logger.error(msg)
            logger.error(f"  Excluded times: {exclude_times_normalized}")
            logger.error(f"  Remaining times: {remaining_excluded['Time_str'].unique().tolist()}")
    
    return date_df


def get_available_times(stream_df: pd.DataFrame, exclude_times_str: List[str], slot_ends: Dict[str, List[str]]) -> List[str]:
    """
    Get list of available times for a stream (excluding excluded times).
    
    CONTRACT: Returns ALL canonical times for the stream's session, NOT derived from stream_df['Time'].unique().
    This is the SINGLE SOURCE OF TRUTH for available times.
    
    Session canonical slots:
    - S1: ["07:30", "08:00", "09:00"]
    - S2: ["09:30", "10:00", "10:30", "11:00"]
    
    Args:
        stream_df: DataFrame for the stream (used only to infer session, NOT to derive times)
        exclude_times_str: List of excluded time strings (normalized to HH:MM)
        slot_ends: Dictionary mapping session names to canonical time lists
        
    Returns:
        Sorted list of available time strings (all canonical times in the stream's session, minus excluded)
        Times are normalized to HH:MM format.
    """
    from .utils import normalize_time
    
    # Determine session from stream data
    if 'Session' in stream_df.columns and not stream_df.empty:
        session = str(stream_df['Session'].iloc[0]).strip()
    else:
        # Fallback: try to infer from times present (but we don't use these times for available_times)
        # This is only for session inference
        all_times_in_data = [normalize_time(str(t)) for t in stream_df['Time'].unique()]
        # If we see S2 times, assume S2; otherwise S1
        s2_times = ['09:30', '10:00', '10:30', '11:00']
        if any(t in s2_times for t in all_times_in_data):
            session = 'S2'
        else:
            session = 'S1'
    
    # Get ALL canonical times for this session (from slot_ends, NOT from data)
    all_canonical_times = slot_ends.get(session, [])
    
    # Normalize exclude_times_str for comparison
    exclude_times_normalized = [normalize_time(str(t)) for t in exclude_times_str]
    
    # Filter out excluded times (using normalized comparison)
    available_times = []
    for canonical_time in all_canonical_times:
        normalized_canonical = normalize_time(canonical_time)
        if normalized_canonical not in exclude_times_normalized:
            available_times.append(normalized_canonical)
    
    # Sort chronologically (should already be sorted in slot_ends, but ensure it)
    def time_sort_key(time_str: str) -> tuple:
        """Convert time string to tuple for proper chronological sorting"""
        normalized = normalize_time(time_str)
        parts = normalized.split(':')
        return (int(parts[0]), int(parts[1]))  # (hour, minute)
    
    available_times = sorted(available_times, key=time_sort_key)
    
    return available_times


def select_trade_for_time(
    date_df: pd.DataFrame,
    current_time: str,
    current_session: str
) -> Optional[pd.Series]:
    """
    Select a trade for the current time slot ONLY.
    
    CONTRACT: Returns the trade at current_time, or None if no trade exists.
    NO FALLBACK - if current_time has no trade, return None.
    
    NOTE: Excluded times should be filtered from date_df BEFORE calling this function.
    This function assumes date_df already has excluded times removed.
    
    Args:
        date_df: DataFrame for a single day (should already have excluded times filtered out)
        current_time: Current time slot to look for (should be in selectable_times)
        current_session: Current session (S1 or S2)
        
    Returns:
        Selected trade as Series if found at current_time, or None if no trade exists
    """
    # Ensure Time_str exists and is normalized to HH:MM format
    if 'Time_str' not in date_df.columns:
        date_df = date_df.copy()
        date_df['Time_str'] = date_df['Time'].apply(lambda t: normalize_time(str(t)))
    else:
        # Re-normalize existing Time_str to ensure HH:MM format
        date_df = date_df.copy()
        date_df['Time_str'] = date_df['Time_str'].apply(lambda t: normalize_time(str(t)))
    
    # Normalize current_time for comparison
    current_time_normalized = normalize_time(str(current_time))
    
    # Look ONLY for trade at current_time (using normalized comparison)
    trade = date_df[
        (date_df['Time_str'] == current_time_normalized) & 
        (date_df['Session'] == current_session)
    ]
    
    if not trade.empty:
        trade_row = trade.iloc[0].copy()
        # Ensure Time_str is in the returned Series
        if 'Time_str' not in trade_row.index:
            trade_row['Time_str'] = str(trade_row.get('Time', '')).strip()
        logger.debug(f"Found trade at {current_time_normalized}")
        return trade_row
    else:
        # No trade at current_time - return None (will be recorded as NoTrade)
        logger.debug(f"No trade found at {current_time_normalized} - returning None (will be NoTrade)")
        return None

