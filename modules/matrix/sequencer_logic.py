"""
Sequencer logic for Master Matrix.

THIS IS THE SINGLE AUTHORITATIVE SOURCE for all sequencing decisions:
- current_time state
- rolling histories
- loss-triggered time changes
- next-day time intent
- what the Time column represents

All other modules MUST defer to sequencer_logic.py for time decisions.

TIME COLUMN AUTHORITY (CRITICAL):
==================================
The Time column is OWNED by sequencer_logic.py and means:
"The sequencer's intended trading slot for that day."

HARD RULE: Time must NEVER be re-derived or mutated downstream.
- Downstream modules (Merger, Master Matrix, Timetable) must NOT overwrite Time
- If analyzer's original time is needed, it should be stored as "AnalyzerTime" upstream
- The sequencer overwrites Time with current_time regardless of trade row's original Time
- This ensures Time always reflects sequencer intent, not analyzer output

SEQUENCING CONTRACT - FILTERED TIMES:
=====================================
Matrix filtering affects time SELECTION only. It does NOT affect:
- Rolling history updates (all canonical times advance every day)
- Scoring (all canonical times are scored)

TIME SETS:
- canonical_times: All session times (always scored, always in rolling histories)
- selectable_times: canonical_times minus matrix-filtered times (for time selection only)

INVARIANTS:
1. All canonical times advance rolling history every day (including filtered)
2. Time selection compares ONLY selectable_times (filtered times excluded)
3. current_time must always be in selectable_times (never switch into filtered)
4. Time changes only occur after LOSS
5. Filtered times are still scored (they just can't be selected)

HARD RULES:
- Matrix filtering affects selection only (not scoring, not execution)
- Sequencer must never switch into a filtered time
- Execution must never depend on filtering (selection already handled it)
"""

import logging
from typing import Dict, List, Optional
import pandas as pd

from .utils import calculate_time_score, get_session_for_time
from .logging_config import setup_matrix_logger
from .history_manager import update_time_slot_history
from .trade_selector import select_trade_for_time

# Set up logger using centralized configuration
logger = setup_matrix_logger(__name__, console=True, level=logging.INFO)

# Session configuration - OWNED BY sequencer_logic.py
SLOT_ENDS = {
    "S1": ["07:30", "08:00", "09:00"],
    "S2": ["09:30", "10:00", "10:30", "11:00"],
}

__all__ = ['apply_sequencer_logic', 'process_stream_daily', 'SLOT_ENDS']


def decide_time_change(
    current_time: str,
    current_result: str,
    current_sum_after: float,
    time_slot_histories: Dict[str, List[int]],
    selectable_times: List[str],
    current_session: str
) -> Optional[str]:
    """
    Decide if time should change for the next day based on loss and rolling sums.
    
    Pure function: Takes only what's needed, returns best time or None.
    Does not know about trades, dates, or filtering - only rolling scores.
    
    Args:
        current_time: Current time slot being used
        current_result: Result at current_time (WIN/LOSS/BE/NoTrade)
        current_sum_after: Rolling sum for current time slot after today
        time_slot_histories: Dictionary of time slot histories (already updated)
        selectable_times: List of selectable time slots (canonical minus filtered)
        current_session: Current session (S1 or S2)
        
    Returns:
        Best other time if change needed, or None if no change
    """
    from .utils import normalize_time
    
    # Only evaluate time change on LOSS
    if str(current_result).upper() != 'LOSS':
        return None
    
    # Get other selectable times in same session
    session_times = SLOT_ENDS.get(current_session, [])
    current_time_normalized = normalize_time(str(current_time))
    other_selectable = [normalize_time(str(t)) for t in session_times 
                       if normalize_time(str(t)) != current_time_normalized 
                       and normalize_time(str(t)) in selectable_times]
    
    if not other_selectable:
        return None
    
    # Calculate rolling sums for other selectable times
    other_sums = {}
    for other_time in other_selectable:
        other_sums[other_time] = sum(time_slot_histories.get(other_time, []))
    
    if not other_sums:
        return None
    
    # Find best other time (highest sum, tie-break to earliest)
    def time_sort_key(time_str: str) -> tuple:
        parts = time_str.split(':')
        return (int(parts[0]), int(parts[1]))
    
    sorted_others = sorted(
        other_sums.items(),
        key=lambda x: (-x[1], time_sort_key(x[0]))  # Descending sum, ascending time
    )
    
    best_other_time, best_other_sum = sorted_others[0]
    
    # Switch only if strictly greater (not equal)
    if best_other_sum > current_sum_after:
        return best_other_time
    
    return None


def process_stream_daily(
    stream_df: pd.DataFrame,
    stream_id: str,
    stream_filters: Dict,
    display_year: Optional[int] = None
) -> List[Dict]:
    """
    Process a single stream day by day, applying sequencer logic.
    
    REFACTORED: Deterministic, calendar-driven, canonical-time-based.
    
    Args:
        stream_df: DataFrame for the stream
        stream_id: Stream ID
        stream_filters: Filter configuration for this stream
        display_year: If provided, only return trades from this year
        
    Returns:
        List of chosen trade dictionaries
    """
    from .utils import normalize_time
    
    # ============================================================================
    # STEP 1: DEFINE CANONICAL TIME SET (MANDATORY)
    # ============================================================================
    # Determine session (use Session column if present, otherwise default to S1)
    if 'Session' in stream_df.columns and not stream_df.empty:
        session = str(stream_df['Session'].iloc[0]).strip()
    else:
        # Default to S1 if Session column missing
        session = 'S1'
    
    # CANONICAL TIMES: Full fixed set for this session (never changes, never filtered)
    canonical_times = [normalize_time(str(t)) for t in SLOT_ENDS.get(session, [])]
    
    if not canonical_times:
        logger.error(f"Stream {stream_id}: No canonical times for session {session}! Skipping stream.")
        return []
    
    # Filtered times (matrix filtering - affects selection, NOT scoring)
    filtered_times_str = [normalize_time(str(t)) for t in stream_filters.get('exclude_times', [])]
    filtered_times_normalized = set(filtered_times_str)
    
    # Selectable times: canonical_times minus filtered_times
    selectable_times = [t for t in canonical_times if t not in filtered_times_normalized]
    
    if not selectable_times:
        logger.error(
            f"Stream {stream_id}: No selectable times! All canonical times are filtered. "
            f"Canonical times: {canonical_times}, Filtered times: {sorted(filtered_times_normalized)}"
        )
        return []
    
    # Initialize current_time to first selectable time
    current_time = normalize_time(str(selectable_times[0]))
    current_session = session
    previous_time = None
    
    # ============================================================================
    # INITIALIZE ROLLING HISTORIES FOR ALL CANONICAL TIMES
    # ============================================================================
    time_slot_histories = {t: [] for t in canonical_times}
    
    chosen_trades = []
    
    # ============================================================================
    # DATA-DRIVEN DATE ITERATION (NO CALENDAR)
    # ============================================================================
    if stream_df.empty:
        return []
    
    # Normalize dates for comparison
    stream_df = stream_df.copy()
    if not pd.api.types.is_datetime64_any_dtype(stream_df['Date']):
        stream_df['Date'] = pd.to_datetime(stream_df['Date'])
    
    # CRITICAL: Iterate only over dates present in analyzer data (data-driven, not calendar)
    # No weekends, no holidays unless present in data
    unique_dates = stream_df['Date'].dt.normalize().unique()
    trading_dates = sorted(unique_dates)
    
    logger.debug(f"Stream {stream_id}: Processing {len(trading_dates)} trading days (from data, not calendar)")
    
    # ============================================================================
    # DAILY PROCESSING LOOP (ITERATE ONLY OVER TRADING DAYS IN DATA)
    # ============================================================================
    for date in trading_dates:
        # date is already normalized from unique() above
        date_normalized = date
        date_df = stream_df[stream_df['Date'].dt.normalize() == date_normalized].copy()
        
        # Normalize Time_str for comparisons (once per date_df)
        if 'Time_str' not in date_df.columns:
            date_df = date_df.copy()
            date_df['Time_str'] = date_df['Time'].apply(lambda t: normalize_time(str(t)))
        
        # ============================================================================
        # CENTRALIZED DAILY SCORING (ONE BLOCK)
        # ============================================================================
        # Score all canonical_times: build results, calculate scores, update histories
        daily_results = {}  # {time_normalized: result_string}
        daily_scores = {}   # {time_normalized: score_int}
        
        for canonical_time in canonical_times:
            canonical_time_normalized = normalize_time(str(canonical_time))
            
            # Determine result
            if date_df.empty:
                result = 'NoTrade'
            else:
                slot_trade = date_df[date_df['Time_str'] == canonical_time_normalized]
                result = slot_trade.iloc[0]['Result'] if not slot_trade.empty else 'NoTrade'
            
            # Calculate score and update history
            score = calculate_time_score(result)
            update_time_slot_history(time_slot_histories, canonical_time_normalized, score)
            
            daily_results[canonical_time_normalized] = result
            daily_scores[canonical_time_normalized] = score
        
        # ============================================================================
        # TIME CHANGE DECISION (PURE FUNCTION)
        # ============================================================================
        current_time_normalized = normalize_time(str(current_time))
        current_time_result = daily_results.get(current_time_normalized, 'NoTrade')
        current_sum_after = sum(time_slot_histories.get(current_time_normalized, []))
        
        next_time = decide_time_change(
            current_time,
            current_time_result,
            current_sum_after,
            time_slot_histories,
            selectable_times,
            current_session
        )
        
        old_time_for_today = str(current_time).strip()
        
        # ============================================================================
        # TRADE SELECTION (PURE LOOKUP)
        # ============================================================================
        # CRITICAL: Filter out excluded times from date_df BEFORE selection
        # This ensures we never accidentally select a trade at an excluded time
        # even if current_time somehow became an excluded time (shouldn't happen, but defensive)
        if not date_df.empty and filtered_times_normalized:
            date_df_filtered = date_df[~date_df['Time_str'].isin(filtered_times_normalized)].copy()
            # Log if we filtered out any rows (shouldn't happen if logic is correct, but good to know)
            filtered_out_count = len(date_df) - len(date_df_filtered)
            if filtered_out_count > 0:
                logger.warning(
                    f"Stream {stream_id} {date}: Filtered out {filtered_out_count} trades at excluded times: "
                    f"{sorted(date_df[date_df['Time_str'].isin(filtered_times_normalized)]['Time_str'].unique())}"
                )
            date_df = date_df_filtered
        
        # DIAGNOSTIC: Log selection context for debugging
        current_time_normalized = normalize_time(str(current_time))
        available_times_in_data = sorted(date_df['Time_str'].unique().tolist()) if not date_df.empty else []
        row_exists = not date_df.empty and (date_df['Time_str'] == current_time_normalized).any()
        
        if logger.isEnabledFor(logging.DEBUG):
            logger.debug(
                f"Stream {stream_id} {date}: current_time={current_time_normalized}, "
                f"row_exists={row_exists}, available_times={available_times_in_data}"
            )
        
        # Verify current_time is actually selectable (safeguard)
        if current_time_normalized not in selectable_times:
            logger.error(
                f"Stream {stream_id} {date}: CRITICAL - current_time '{current_time_normalized}' is not selectable! "
                f"Selectable: {selectable_times}, Filtered: {sorted(filtered_times_normalized)}"
            )
            # Don't select a trade at an excluded time - return NoTrade instead
            trade_row = None
        else:
            trade_row = select_trade_for_time(date_df, current_time, current_session)
        
        if trade_row is not None:
            trade_dict = trade_row.to_dict()
            # CRITICAL: Time column is sequencer's authority - overwrite analyzer's Time
            # This ensures Time always reflects sequencer's intended slot, not trade's original time
            trade_dict['Time'] = str(current_time).strip()
            trade_dict['RowSource'] = 'Analyzer'  # This row came from analyzer output
        else:
            # Sequencer NoTrade: sequencer chose current_time but no trade exists at that slot
            # This is structural - no analyzer data at this time slot
            trade_dict = {
                'Stream': stream_id,
                'Date': date,
                'Time': str(current_time).strip(),  # Still set Time to current_time (sequencer's intent)
                'Result': 'NoTrade',
                'Session': current_session,
                'RowSource': 'Sequencer',  # This row was created by sequencer (no analyzer data)
            }
        
        # Add rolling columns for ALL canonical times (from daily_scores)
        for canonical_time in canonical_times:
            canonical_time_normalized = normalize_time(str(canonical_time))
            rolling_sum = sum(time_slot_histories.get(canonical_time_normalized, []))
            trade_dict[f"{canonical_time} Rolling"] = round(rolling_sum, 2)
            trade_dict[f"{canonical_time} Points"] = daily_scores.get(canonical_time_normalized, 0)
        
        # NOTE: SL, R, Time Change formatting removed - these are downstream concerns
        # Sequencer only outputs raw data with rolling columns
        
        # Add trade_date (copy from Date)
        if 'Date' in trade_dict:
            trade_dict['trade_date'] = pd.to_datetime(trade_dict['Date'], errors='coerce')
        else:
            trade_dict['trade_date'] = pd.to_datetime(date, errors='coerce')
        
        # Filter by display_year if specified
        if display_year is None or (pd.notna(trade_dict['trade_date']) and trade_dict['trade_date'].year == display_year):
            chosen_trades.append(trade_dict)
        
        # ============================================================================
        # MUTATE current_time EXACTLY ONCE (at end of loop)
        # ============================================================================
        if next_time is not None:
            current_time = next_time
            current_session = get_session_for_time(current_time, SLOT_ENDS)
        
        previous_time = old_time_for_today
        
        # ============================================================================
        # INVARIANT CHECK
        # ============================================================================
        history_lengths = [len(hist) for hist in time_slot_histories.values()]
        if history_lengths and len(set(history_lengths)) != 1:
            raise AssertionError(f"Stream {stream_id} {date}: History length mismatch: {dict(zip(canonical_times, history_lengths))}")
    
    return chosen_trades


def apply_sequencer_logic(
    df: pd.DataFrame,
    stream_filters: Dict[str, Dict],
    display_year: Optional[int] = None
) -> pd.DataFrame:
    """
    Apply sequencer logic to select one trade per day per stream.
    Uses time change logic similar to sequential processor.
    
    Processes ALL historical data to build accurate time slot histories,
    and returns trades from all years (or filtered by display_year if specified).
    
    Args:
        df: DataFrame with all trades from analyzer_runs
        stream_filters: Per-stream filter configuration
        display_year: If provided, only return trades from this year (for display).
                     If None, return trades from ALL years.
                     All data is still processed to build accurate histories.
        
    Returns:
        DataFrame with one chosen trade per day per stream
    """
    if df.empty:
        return df
    
    # Pre-convert Date column once for all streams
    if 'Date' in df.columns and not pd.api.types.is_datetime64_any_dtype(df['Date']):
        df['Date'] = pd.to_datetime(df['Date'])
    
    # Sort once by Stream and Date for better cache locality
    # Debug: Check for None values before sorting
    for col in ['Stream', 'Date']:
        if col in df.columns:
            none_count = df[col].isna().sum() if hasattr(df[col], 'isna') else (df[col] == None).sum()
            if none_count > 0:
                logger.warning(f"[apply_sequencer_logic] Column '{col}' has {none_count} None/NaN values before sorting")
                # Log sample of problematic rows
                problematic = df[df[col].isna()] if hasattr(df[col], 'isna') else df[df[col] == None]
                if len(problematic) > 0 and len(problematic) <= 10:
                    logger.debug(f"[apply_sequencer_logic] Rows with None in '{col}': {problematic[['Stream', 'Date']].head(5).to_dict('records') if all(c in problematic.columns for c in ['Stream', 'Date']) else 'N/A'}")
            # Replace None with empty string for string columns to avoid comparison errors
            if col == 'Stream' and df[col].dtype == 'object':
                df[col] = df[col].fillna('')
                logger.debug(f"[apply_sequencer_logic] Filled None values in 'Stream' with empty string for sorting")
    
    df = df.sort_values(['Stream', 'Date'], kind='mergesort').reset_index(drop=True)
    
    chosen_trades = []
    
    # Group by stream and process
    for stream_id in df['Stream'].unique():
        stream_mask = df['Stream'] == stream_id
        stream_df = df[stream_mask].copy()
        stream_filters_for_stream = stream_filters.get(stream_id, {})
        
        stream_chosen_trades = process_stream_daily(stream_df, stream_id, stream_filters_for_stream, display_year)
        chosen_trades.extend(stream_chosen_trades)
    
    if not chosen_trades:
        return pd.DataFrame()
    
    result_df = pd.DataFrame(chosen_trades)
    
    # ============================================================================
    # INVARIANT CHECK: Time must always be in selectable_times
    # ============================================================================
    # Verify that Time column always contains selectable times (not filtered)
    # This checks the real invariant: "current_time must always be selectable"
    from .utils import normalize_time
    
    for stream_id, filters in stream_filters.items():
        stream_mask = result_df['Stream'] == stream_id
        if not stream_mask.any():
            continue
        
        stream_rows = result_df[stream_mask]
        
        # Get session from result_df (should be stream-constant)
        # INVARIANT: Session should not vary within a stream. If it does, this check may be unstable.
        if 'Session' in stream_rows.columns and not stream_rows.empty:
            session = str(stream_rows['Session'].iloc[0]).strip()
            # Verify session consistency across stream (warn if inconsistent)
            unique_sessions = stream_rows['Session'].apply(str).str.strip().unique()
            if len(unique_sessions) > 1:
                # This is a data quality issue - log it but use the most common session
                session_counts = stream_rows['Session'].apply(str).str.strip().value_counts()
                most_common_session = session_counts.index[0]
                logger.warning(
                    f"Stream {stream_id}: Session varies within stream: {unique_sessions.tolist()}. "
                    f"Counts: {dict(session_counts)}. Using most common session '{most_common_session}' for invariant check."
                )
                session = most_common_session
        else:
            logger.warning(f"Stream {stream_id}: No 'Session' column found, defaulting to 'S1'")
            session = 'S1'
        
        # Compute selectable_times for this stream (same logic as process_stream_daily)
        # CRITICAL: Normalization must be consistent across:
        # - SLOT_ENDS values
        # - stream_filters exclude_times
        # - row Time values
        canonical_times = [normalize_time(str(t)) for t in SLOT_ENDS.get(session, [])]
        filtered_times = [normalize_time(str(t)) for t in filters.get('exclude_times', [])]
        filtered_times_set = set(filtered_times)
        selectable_times = [t for t in canonical_times if t not in filtered_times_set]
        selectable_times_set = set(selectable_times)
        
        # Check that all Time values are in selectable_times
        # This is a critical invariant: sequencer should never select a filtered time
        invalid_times = []
        for idx, row in stream_rows.iterrows():
            time_value = normalize_time(str(row['Time']))
            if time_value not in selectable_times_set:
                date = row.get('Date', 'unknown')
                invalid_times.append((idx, date, time_value))
        
        if invalid_times:
            # Log all invalid times before raising error
            for idx, date, time_value in invalid_times:
                logger.error(
                    f"Stream {stream_id} {date}: CRITICAL INVARIANT VIOLATION - "
                    f"Time '{time_value}' is not selectable. Selectable: {selectable_times}, Filtered: {sorted(filtered_times)}"
                )
            raise AssertionError(
                f"Stream {stream_id}: Found {len(invalid_times)} trades with non-selectable times. "
                    f"Selectable times: {selectable_times}, Filtered times: {sorted(filtered_times)}"
                )
    
    # NOTE: SL column not added here - that's a downstream concern (schema_normalizer or filter_engine)
    # Sequencer only outputs raw data with rolling columns and RowSource
    
    return result_df.reset_index(drop=True)

