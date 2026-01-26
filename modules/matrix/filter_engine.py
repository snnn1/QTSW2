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
    """
    # Handle empty DataFrame explicitly
    if df.empty:
        logger.info("Empty DataFrame - initializing required columns and returning")
        from .config import (
            STREAM_HEALTH_SUSPEND_THRESHOLD,
            STREAM_HEALTH_RESUME_THRESHOLD
        )
        df['stream_rolling_sum'] = 0.0
        df['stream_health_state'] = 'unknown'  # Use 'unknown', not 'recovering'
        df['health_suspend_threshold'] = STREAM_HEALTH_SUSPEND_THRESHOLD
        df['health_resume_threshold'] = STREAM_HEALTH_RESUME_THRESHOLD
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
            "Stream column missing - required for stream health gate. "
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
    
    # ========================================================================
    # STREAM HEALTH GATE: Compute rolling performance and apply health gate
    # ========================================================================
    # Check if health gate is enabled for any stream (default: enabled)
    # If all streams have health_gate_enabled=False, skip health gate entirely
    health_gate_enabled_for_any_stream = False
    for stream_id, filters in stream_filters.items():
        if stream_id == 'master':
            continue
        # Default to True if not specified (backward compatibility)
        if filters.get('health_gate_enabled', True):
            health_gate_enabled_for_any_stream = True
            break
    
    # Skip health gate entirely if disabled for all streams
    if not health_gate_enabled_for_any_stream:
        logger.info("Stream health gate disabled for all streams - skipping health gate calculation")
        # Still initialize stream_rolling_sum column with zeros for consistency
        df['stream_rolling_sum'] = 0.0
        return df
    
    from .config import (
        STREAM_HEALTH_ROLLING_WINDOW,
        STREAM_HEALTH_SUSPEND_THRESHOLD,
        STREAM_HEALTH_RESUME_THRESHOLD
    )
    
    # CONTRACT: trade_date must exist for sorting
    if 'trade_date' not in df.columns:
        raise ValueError(
            "trade_date column missing - required for stream health gate rolling calculation. "
            "This is a contract violation."
        )
    
    # Create stable row identity before sorting (preserve original index)
    # Reset index to ensure _row_id is a simple integer sequence
    df = df.reset_index(drop=True)
    df['_row_id'] = range(len(df))
    
    # Determine rolling window size per stream (default or override)
    # Build a dict: stream_id -> window_size
    stream_window_sizes = {}
    for stream_id in df['Stream'].unique():
        # Default window size
        window_size = STREAM_HEALTH_ROLLING_WINDOW
        
        # Check for override in stream_filters
        if stream_id in stream_filters and stream_id != 'master':
            filters = stream_filters[stream_id]
            if 'health_rolling_window' in filters:
                override_window = int(filters['health_rolling_window'])
                if override_window <= 0:
                    raise ValueError(
                        f"Stream {stream_id}: health_rolling_window must be > 0, got {override_window}"
                    )
                window_size = override_window
                logger.debug(f"Stream {stream_id}: Override health_rolling_window = {window_size}")
        
        stream_window_sizes[stream_id] = window_size
    
    # Sort by Stream, then trade_date, then _row_id (for stable sort)
    # This ensures rolling window is computed chronologically per stream
    df_sorted = df.sort_values(
        by=['Stream', 'trade_date', '_row_id'],
        ascending=[True, True, True],
        na_position='last'
    ).copy()
    # DO NOT reset_index - preserve _row_id for alignment
    
    # Compute rolling sum of ProfitDollars per stream
    # Window size varies per stream (from stream_window_sizes dict)
    # Include all trades (blocked and allowed) in rolling calculation
    rolling_sums = []
    rolling_indices = []
    for stream_id, stream_group in df_sorted.groupby('Stream'):
        window_size = stream_window_sizes.get(stream_id, STREAM_HEALTH_ROLLING_WINDOW)
        # Ensure ProfitDollars is numeric and fill NaN with 0 for rolling calculation
        profit_values = pd.to_numeric(stream_group['ProfitDollars'], errors='coerce').fillna(0.0)
        stream_rolling = profit_values.rolling(window=window_size, min_periods=1).sum()
        rolling_sums.append(stream_rolling.values)  # Get values array
        rolling_indices.append(stream_group['_row_id'].values)  # Get corresponding _row_id values
    
    # Create a mapping from _row_id to rolling_sum
    rolling_sum_map = {}
    for indices, sums in zip(rolling_indices, rolling_sums):
        for idx, sum_val in zip(indices, sums):
            rolling_sum_map[idx] = sum_val
    
    # Map stream_rolling_sum back to original df using _row_id
    df['stream_rolling_sum'] = df['_row_id'].map(rolling_sum_map)
    
    # Verify mapping worked correctly
    if df['stream_rolling_sum'].isna().all():
        logger.error("stream_rolling_sum is all NaN after mapping - mapping may have failed")
        logger.error(f"Sample _row_id values: {df['_row_id'].head(10).tolist()}")
        logger.error(f"Sample rolling_sum_map keys: {list(rolling_sum_map.keys())[:10]}")
    else:
        mapped_non_null = df['stream_rolling_sum'].notna().sum()
        logger.debug(f"After mapping: {mapped_non_null}/{len(df)} non-null stream_rolling_sum values")
        # Log sample values for debugging
        sample_values = df[df['stream_rolling_sum'].notna()]['stream_rolling_sum'].head(5).tolist()
        logger.debug(f"Sample stream_rolling_sum values: {sample_values}")
    
    # Remove temporary _row_id column
    df = df.drop(columns=['_row_id'])
    
    # Sanity check: stream_rolling_sum should never be object dtype
    if df['stream_rolling_sum'].dtype == 'object':
        raise ValueError(
            "stream_rolling_sum is object dtype - this indicates a calculation error. "
            "Expected numeric dtype."
        )
    
    # Fill any remaining NaN values with 0 (shouldn't happen, but defensive)
    df['stream_rolling_sum'] = df['stream_rolling_sum'].fillna(0.0)
    
    # Final verification: ensure we have numeric values
    df['stream_rolling_sum'] = pd.to_numeric(df['stream_rolling_sum'], errors='coerce').fillna(0.0)
    
    # Initialize threshold columns with defaults
    df['health_suspend_threshold'] = STREAM_HEALTH_SUSPEND_THRESHOLD
    df['health_resume_threshold'] = STREAM_HEALTH_RESUME_THRESHOLD
    
    # Apply per-stream threshold overrides from stream_filters
    for stream_id, filters in stream_filters.items():
        if stream_id == 'master':
            continue  # Skip master filters (they don't apply to health gate)
        
        stream_mask = df['Stream'] == stream_id
        
        # Override suspend threshold if provided
        if 'health_suspend_threshold' in filters:
            suspend_threshold = float(filters['health_suspend_threshold'])
            df.loc[stream_mask, 'health_suspend_threshold'] = suspend_threshold
            logger.debug(f"Stream {stream_id}: Override health_suspend_threshold = {suspend_threshold}")
        
        # Override resume threshold if provided
        if 'health_resume_threshold' in filters:
            resume_threshold = float(filters['health_resume_threshold'])
            df.loc[stream_mask, 'health_resume_threshold'] = resume_threshold
            logger.debug(f"Stream {stream_id}: Override health_resume_threshold = {resume_threshold}")
    
    # Derive health state from rolling sum and thresholds (vectorized, no apply)
    # State is recomputed row-by-row from history (no persistence)
    # Precedence: unknown → suspended → healthy → recovering
    
    # Initialize with 'recovering' (default state)
    df['stream_health_state'] = 'recovering'
    
    # Create masks for each state (order matters - check unknown first, then suspended, then healthy)
    unknown_mask = df['stream_rolling_sum'].isna()
    suspended_mask = (~unknown_mask) & (df['stream_rolling_sum'] <= df['health_suspend_threshold'])
    healthy_mask = (~unknown_mask) & (~suspended_mask) & (df['stream_rolling_sum'] >= df['health_resume_threshold'])
    
    # Assign states by precedence
    df.loc[unknown_mask, 'stream_health_state'] = 'unknown'
    df.loc[suspended_mask, 'stream_health_state'] = 'suspended'
    df.loc[healthy_mask, 'stream_health_state'] = 'healthy'
    # 'recovering' is already the default, no assignment needed
    
    # Sanity check: stream_health_state must only contain valid values
    valid_states = {'healthy', 'suspended', 'recovering', 'unknown'}
    actual_states = set(df['stream_health_state'].unique())
    if not actual_states.issubset(valid_states):
        invalid_states = actual_states - valid_states
        raise ValueError(
            f"stream_health_state contains invalid values: {invalid_states}. "
            f"Expected only: {valid_states}"
        )
    
    # ========================================================================
    # PHASE 2.2: Apply one-trade lag for health gate (causality fix)
    # Health state at trade T applies to trade T+1
    # ========================================================================
    # Add stream_health_state to df_sorted for lagging
    # Map health state from df to df_sorted using _row_id
    health_state_map = dict(zip(df['_row_id'], df['stream_health_state']))
    df_sorted['stream_health_state'] = df_sorted['_row_id'].map(health_state_map)
    
    # Create lagged_health_state using pure vectorized shift
    df_sorted['lagged_health_state'] = (
        df_sorted.groupby('Stream')['stream_health_state']
        .shift(1)
        .fillna('healthy')  # First trade per stream defaults to 'healthy' (not 'recovering')
    )
    
    # Create lagged_suspended_mask on df_sorted
    lagged_suspended_mask_sorted = df_sorted['lagged_health_state'] == 'suspended'
    
    # Map lagged_suspended_mask back to original df by _row_id (not by .values)
    # Create mapping from _row_id to lagged_suspended boolean
    lagged_suspended_map = dict(zip(df_sorted['_row_id'], lagged_suspended_mask_sorted))
    
    # Apply to original df using map
    df['lagged_suspended'] = df['_row_id'].map(lagged_suspended_map).fillna(False)
    
    # Remove temporary _row_id column (no longer needed)
    df = df.drop(columns=['_row_id'])
    
    # Use lagged_suspended as the health_suspended_mask
    health_suspended_mask = df['lagged_suspended']
    
    # Ensure filter_reasons exists and fill NaN with empty string
    if 'filter_reasons' not in df.columns:
        df['filter_reasons'] = ''
    else:
        df['filter_reasons'] = df['filter_reasons'].fillna('').astype(str)
    
    # Apply health gate: suspend streams where lagged_suspended == True
    # This happens AFTER existing filters (DOW/DOM/time) so final_allowed is logical AND
    # Only apply health gate to streams where health_gate_enabled=True
    
    # Build enabled_mask vectorized (can loop over streams, not trades)
    # Default True for streams not in stream_filters (backwards compatible)
    enabled_mask = pd.Series(True, index=df.index)  # Default all enabled
    
    # Override for streams in stream_filters dict
    for stream_id, filters in stream_filters.items():
        if stream_id == 'master':
            continue
        stream_mask = df['Stream'] == stream_id
        # Default to True if key missing (backward compatibility)
        health_gate_enabled = filters.get('health_gate_enabled', True)
        if not health_gate_enabled:
            enabled_mask.loc[stream_mask] = False
    
    # Final suspended mask: lagged_suspended AND enabled
    suspended_mask = health_suspended_mask & enabled_mask
    
    if suspended_mask.any():
        suspended_count = suspended_mask.sum()
        suspended_streams = df[suspended_mask]['Stream'].unique().tolist()
        
        # Set final_allowed = False for suspended streams (logical AND with existing filters)
        df.loc[suspended_mask, 'final_allowed'] = False
        
        # Append health gate reason using vectorized string operations (no apply)
        # If empty => "health_gate_suspended", else => append ", health_gate_suspended"
        empty_reasons = df.loc[suspended_mask, 'filter_reasons'].str.strip() == ''
        df.loc[suspended_mask & empty_reasons, 'filter_reasons'] = 'health_gate_suspended'
        df.loc[suspended_mask & ~empty_reasons, 'filter_reasons'] = (
            df.loc[suspended_mask & ~empty_reasons, 'filter_reasons'] + ', health_gate_suspended'
        )
        
        logger.info(
            f"Stream health gate (lagged): Suspended {suspended_count} trades across streams: {suspended_streams}"
        )
    
    # Sanity check: final_allowed must remain boolean dtype
    if df['final_allowed'].dtype != 'bool':
        raise ValueError(
            f"final_allowed is {df['final_allowed'].dtype}, expected bool. "
            f"This indicates a type mutation error."
        )
    
    # ========================================================================
    # PHASE 5.1: Causality Sanity Check (DEBUG level only)
    # ========================================================================
    # Verify no trade is blocked by its own ProfitDollars
    # Key invariant: For any stream, first suspended row must occur strictly after first crossing row
    if logger.isEnabledFor(logging.DEBUG):
        # Use df_sorted (already sorted by Stream, trade_date, _row_id)
        # Compute crossing rows where rolling_sum first drops to <= suspend_threshold
        prev_rolling_sum = df_sorted.groupby('Stream')['stream_rolling_sum'].shift(1)
        prev_suspend_threshold = df_sorted.groupby('Stream')['health_suspend_threshold'].shift(1)
        
        crossing_mask = (
            (df_sorted['stream_rolling_sum'] <= df_sorted['health_suspend_threshold']) &
            (prev_rolling_sum > prev_suspend_threshold)
        )
        
        # Identify suspended rows from lagged state
        suspended_mask_sorted = df_sorted['lagged_health_state'] == 'suspended'
        
        # For each stream, compute first crossing and first suspended positions
        for stream_id, stream_group in df_sorted.groupby('Stream'):
            if len(stream_group) < 2:
                continue  # Need at least 2 trades to check lag
            
            stream_crossings = stream_group[crossing_mask.loc[stream_group.index]]
            stream_suspended = stream_group[suspended_mask_sorted.loc[stream_group.index]]
            
            if stream_crossings.empty or stream_suspended.empty:
                continue  # No crossings or suspensions to check
            
            # Get first crossing and first suspended positions (using positional index in sorted view)
            first_crossing_pos = stream_crossings.index[0]
            first_suspended_pos = stream_suspended.index[0]
            
            # Check invariant: first_suspended_pos must be > first_crossing_pos
            if first_suspended_pos <= first_crossing_pos:
                logger.error(
                    f"CAUSALITY VIOLATION: Stream {stream_id}: "
                    f"First suspended row position ({first_suspended_pos}) <= "
                    f"first threshold crossing position ({first_crossing_pos}). "
                    f"This indicates a bug in lag application."
                )
            else:
                logger.debug(
                    f"Stream {stream_id}: Causality check passed - "
                    f"first suspended ({first_suspended_pos}) > first crossing ({first_crossing_pos})"
                )
    
    # Log health gate summary
    if 'stream_health_state' in df.columns:
        health_summary = df['stream_health_state'].value_counts().to_dict()
        logger.info(f"Stream health gate summary: {health_summary}")
        
        # Log per-stream health states (DEBUG level)
        if logger.isEnabledFor(logging.DEBUG):
            for stream_id in df['Stream'].unique():
                stream_df = df[df['Stream'] == stream_id]
                if len(stream_df) > 0:
                    latest_state = stream_df.iloc[-1]['stream_health_state']
                    latest_sum = stream_df.iloc[-1]['stream_rolling_sum']
                    logger.debug(
                        f"Stream {stream_id}: health_state={latest_state}, "
                        f"rolling_sum={latest_sum:.2f}"
                    )
    
    return df



