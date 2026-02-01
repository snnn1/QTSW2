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
    
    # DATE OWNERSHIP: Use trade_date column (normalized by DataLoader)
    # Date column may not exist or may not be datetime dtype
    if 'trade_date' not in df.columns:
        raise ValueError(
            "trade_date column missing - DataLoader must normalize dates before filter_engine. "
            "This is a contract violation."
        )
    
    # Ensure we have a copy before modifying (avoid SettingWithCopyWarning)
    df = df.copy()
    
    # Ensure trade_date is datetime dtype before using .dt accessor
    # CRITICAL: Convert to datetime if not already datetime dtype
    if not pd.api.types.is_datetime64_any_dtype(df['trade_date']):
        logger.warning(
            f"trade_date column is {df['trade_date'].dtype} in filter_engine, "
            f"converting to datetime64. This should not happen - check data processing pipeline."
        )
        # Convert to datetime - handle both string and other types
        df['trade_date'] = pd.to_datetime(df['trade_date'], errors='raise')
    
    # Double-check: Verify .dt accessor works (safety check)
    try:
        _test = df['trade_date'].dt.day
    except AttributeError as e:
        raise ValueError(
            f"trade_date column cannot use .dt accessor after conversion. "
            f"Dtype: {df['trade_date'].dtype}, Error: {e}. "
            f"This indicates a serious data processing issue."
        ) from e
    
    # day_of_month
    df['day_of_month'] = df['trade_date'].dt.day
    
    # dow (day of week) - full name for filtering
    df['dow'] = df['trade_date'].dt.strftime('%a')
    df['dow_full'] = df['trade_date'].dt.strftime('%A')  # Full name: Monday, Tuesday, etc.
    
    # month (1-12)
    df['month'] = df['trade_date'].dt.month
    
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
    
    Note:
        WOY filtering is NOT supported - WOY is analysis-only diagnostic tool.
        StreamFilterConfig does NOT include exclude_weeks_of_year field.
        WOY breakdowns are for regime discovery, not execution tuning.
    """
    # Handle empty DataFrame explicitly
    if df.empty:
        logger.info("Empty DataFrame - returning empty")
        return df
    
    # CONTRACT: ProfitDollars must exist and be computed upstream
    if 'ProfitDollars' not in df.columns:
        raise ValueError(
            "ProfitDollars column missing - must be computed upstream before filter_engine. "
            "This is a contract violation. ProfitDollars should be created by statistics module "
            "or data loader, not synthesized here."
        )
    
    # After ProfitDollars existence check, validate NaN on executed trades
    # Use vectorized string operations - no imports from statistics module
    if 'Result' in df.columns:
        # Normalize Result via uppercase string (vectorized)
        result_norm = df['Result'].astype(str).str.upper().str.strip()
        
        # Define executed_mask using vectorized operations
        # Executed: WIN, LOSS, BE, BREAKEVEN, TIME
        # Non-executed: NoTrade, empty, or other
        executed_mask = (
            result_norm.isin(['WIN', 'LOSS', 'BE', 'BREAKEVEN', 'TIME'])
        )
        
        # Check for NaN ProfitDollars on executed trades
        executed_with_nan = executed_mask & df['ProfitDollars'].isna()
        if executed_with_nan.any():
            nan_count = executed_with_nan.sum()
            sample_rows = df[executed_with_nan][['Stream', 'trade_date', 'Result', 'ProfitDollars']].head(5)
            raise ValueError(
                f"ProfitDollars contains NaN for {nan_count} executed trades. "
                f"NaN is only allowed for non-executed rows (NoTrade). "
                f"Sample rows:\n{sample_rows.to_string()}"
            )
    
    # CONTRACT: Stream column must exist
    if 'Stream' not in df.columns:
        raise ValueError(
            "Stream column missing - required for stream filtering. "
            "This is a contract violation."
        )
    
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
            # Append filter reason using vectorized string operations (no apply)
            reason_text = f"dow_filter({','.join(exclude_dows)})"
            empty_reasons = df.loc[dow_mask, 'filter_reasons'].str.strip() == ''
            df.loc[dow_mask & empty_reasons, 'filter_reasons'] = reason_text
            df.loc[dow_mask & ~empty_reasons, 'filter_reasons'] = (
                df.loc[dow_mask & ~empty_reasons, 'filter_reasons'] + ', ' + reason_text
            )
        
        # Day of month filter
        if filters.get('exclude_days_of_month'):
            exclude_doms = filters['exclude_days_of_month']
            dom_mask = stream_mask & df['day_of_month'].isin(exclude_doms)
            df.loc[dom_mask, 'final_allowed'] = False
            # Append filter reason using vectorized string operations (no apply)
            reason_text = f"dom_filter({','.join(map(str, exclude_doms))})"
            empty_reasons = df.loc[dom_mask, 'filter_reasons'].str.strip() == ''
            df.loc[dom_mask & empty_reasons, 'filter_reasons'] = reason_text
            df.loc[dom_mask & ~empty_reasons, 'filter_reasons'] = (
                df.loc[dom_mask & ~empty_reasons, 'filter_reasons'] + ', ' + reason_text
            )
        
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
                # Append filter reason using vectorized string operations (no apply)
                reason_text = f"time_filter({','.join(exclude_times)})"
                empty_reasons = df.loc[time_mask, 'filter_reasons'].str.strip() == ''
                df.loc[time_mask & empty_reasons, 'filter_reasons'] = reason_text
                df.loc[time_mask & ~empty_reasons, 'filter_reasons'] = (
                    df.loc[time_mask & ~empty_reasons, 'filter_reasons'] + ', ' + reason_text
                )
                logger.info(f"Stream {stream_id}: Filtered {time_mask.sum()} trades at excluded times: {exclude_times}")
    
    return df



