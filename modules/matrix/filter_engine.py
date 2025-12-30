"""
Filter engine for Master Matrix.

This module handles adding global columns and applying per-stream filters
to determine which trades are allowed or blocked.

IMPORTANT: This module READS the Time column but NEVER mutates it.
The Time column is OWNED by sequencer_logic.py and represents the sequencer's
intended trading slot. This module uses 'actual_trade_time' (if available) or
Time column for filtering, but never changes Time.
"""

import logging
from typing import Dict
import pandas as pd

logger = logging.getLogger(__name__)


def add_global_columns(
    df: pd.DataFrame,
    stream_filters: Dict[str, Dict],
    dom_blocked_days: set
) -> pd.DataFrame:
    """
    Add global columns for each trade:
    - global_trade_id (unique id)
    - day_of_month (1-31)
    - dow (Mon-Fri)
    - session_index (1 or 2)
    - is_two_stream (true for *2 streams)
    - dom_blocked (true if day is 4/16/30 and stream is a "2")
    - filter_reasons (list of reasons why trade was filtered)
    - final_allowed (boolean after all filters)
    
    Args:
        df: Input DataFrame
        stream_filters: Per-stream filter configuration
        dom_blocked_days: Set of day-of-month values that are blocked for "2" streams
        
    Returns:
        DataFrame with global columns added and filters applied
    """
    logger.info("Adding global columns...")
    
    # global_trade_id
    df['global_trade_id'] = range(1, len(df) + 1)
    
    # day_of_month
    df['day_of_month'] = df['Date'].dt.day
    
    # dow (day of week) - full name for filtering
    df['dow'] = df['Date'].dt.strftime('%a')
    df['dow_full'] = df['Date'].dt.strftime('%A')  # Full name: Monday, Tuesday, etc.
    
    # month (1-12)
    df['month'] = df['Date'].dt.month
    
    # session_index (1 for S1, 2 for S2)
    def get_session_index(session):
        session_upper = str(session).upper()
        if session_upper == 'S1':
            return 1
        elif session_upper == 'S2':
            return 2
        return None
    
    df['session_index'] = df['Session'].apply(get_session_index)
    
    # is_two_stream (true for *2 streams)
    df['is_two_stream'] = df['Stream'].str.endswith('2')
    
    # dom_blocked (true if day is 4/16/30 and stream is a "2")
    df['dom_blocked'] = (
        df['is_two_stream'] & 
        df['day_of_month'].isin(dom_blocked_days)
    )
    
    # Initialize filter reasons and final_allowed
    df['filter_reasons'] = ''
    df['final_allowed'] = True
    
    # Apply per-stream filters
    df = apply_stream_filters(df, stream_filters)
    
    # Clean up filter_reasons (remove leading comma/space)
    df['filter_reasons'] = df['filter_reasons'].str.strip().str.rstrip(',')
    
    logger.info(f"Global columns added. Final allowed trades: {df['final_allowed'].sum()} / {len(df)}")
    
    return df


def apply_stream_filters(df: pd.DataFrame, stream_filters: Dict[str, Dict]) -> pd.DataFrame:
    """
    Apply per-stream filters to the DataFrame.
    
    Args:
        df: Input DataFrame
        stream_filters: Per-stream filter configuration
        
    Returns:
        DataFrame with filters applied (final_allowed and filter_reasons updated)
    """
    # Validate that filter keys match actual streams
    if not df.empty and 'Stream' in df.columns:
        valid_streams = set(df['Stream'].unique())
        filter_streams = set(stream_filters.keys())
        invalid_filters = filter_streams - valid_streams
        if invalid_filters:
            logger.warning(
                f"Filters provided for non-existent streams: {sorted(invalid_filters)}. "
                f"These filters will be ignored. Valid streams: {sorted(valid_streams)}"
            )
    
    for stream_id, filters in stream_filters.items():
        stream_mask = df['Stream'] == stream_id
        
        # Day of week filter
        if filters.get('exclude_days_of_week'):
            exclude_dows = [d.lower() for d in filters['exclude_days_of_week']]
            dow_mask = stream_mask & df['dow_full'].str.lower().isin(exclude_dows)
            df.loc[dow_mask, 'final_allowed'] = False
            df.loc[dow_mask, 'filter_reasons'] = df.loc[dow_mask, 'filter_reasons'].apply(
                lambda x: f"{x}, " if x else ""
            ) + f"dow_filter({','.join(exclude_dows)})"
        
        # Day of month filter
        if filters.get('exclude_days_of_month'):
            exclude_doms = filters['exclude_days_of_month']
            dom_mask = stream_mask & df['day_of_month'].isin(exclude_doms)
            df.loc[dom_mask, 'final_allowed'] = False
            df.loc[dom_mask, 'filter_reasons'] = df.loc[dom_mask, 'filter_reasons'].apply(
                lambda x: f"{x}, " if x else ""
            ) + f"dom_filter({','.join(map(str, exclude_doms))})"
        
        # Time filter
        # CRITICAL: Check actual_trade_time if it exists (from sequencer), otherwise check Time column
        # This is needed because sequencer sets Time to intended slot, not actual trade time
        if filters.get('exclude_times'):
            exclude_times = [str(t).strip() for t in filters['exclude_times']]  # Normalize exclude_times
            from .utils import normalize_time
            
            # Normalize exclude_times for consistent comparison
            exclude_times_normalized = [normalize_time(str(t)) for t in exclude_times]
            
            # Check actual_trade_time first (if sequencer preserved it), then fall back to Time
            if 'actual_trade_time' in df.columns:
                # Normalize actual_trade_time values for comparison (handle potential whitespace/format issues)
                actual_times_normalized = df['actual_trade_time'].astype(str).str.strip().apply(normalize_time)
                time_mask = stream_mask & actual_times_normalized.isin(exclude_times_normalized)
            else:
                # Fallback: normalize Time values for comparison (shouldn't happen if sequencer is working correctly)
                logger.warning(f"Stream {stream_id}: actual_trade_time column missing, falling back to Time column for filtering")
                time_values_normalized = df['Time'].astype(str).str.strip().apply(normalize_time)
                time_mask = stream_mask & time_values_normalized.isin(exclude_times_normalized)
            
            if time_mask.any():
                df.loc[time_mask, 'final_allowed'] = False
                df.loc[time_mask, 'filter_reasons'] = df.loc[time_mask, 'filter_reasons'].apply(
                    lambda x: f"{x}, " if x else "" 
                ) + f"time_filter({','.join(exclude_times)})"
                logger.info(f"Stream {stream_id}: Filtered {time_mask.sum()} trades at excluded times: {exclude_times}")
    
    return df



