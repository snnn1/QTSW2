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
from typing import Dict, List, Optional, Union, Tuple
import pandas as pd

# Import List from typing for type hints (Python 3.8+ compatibility)

from .utils import calculate_time_score, get_session_for_time, time_sort_key
from .logging_config import setup_matrix_logger
from .history_manager import update_time_slot_history, ROLLING_WINDOW_SIZE
from .trade_selector import select_trade_for_time
from .config import SLOT_ENDS

# Set up logger using centralized configuration
logger = setup_matrix_logger(__name__, console=True, level=logging.INFO)

def apply_sequencer_logic_with_state(
    df: pd.DataFrame,
    stream_filters: Dict[str, Dict],
    display_year: Optional[int] = None,
    parallel: bool = True,
    initial_states: Optional[Dict[str, Dict]] = None
) -> Tuple[pd.DataFrame, Dict[str, Dict]]:
    """
    Apply sequencer logic and return both DataFrame and final states for checkpointing.
    
    This is a wrapper around apply_sequencer_logic that also captures final sequencer state.
    
    Args:
        df: DataFrame with all trades from analyzer_runs
        stream_filters: Per-stream filter configuration
        display_year: If provided, only return trades from this year
        parallel: If True, process streams in parallel
        initial_states: Optional initial states for restoration
        
    Returns:
        Tuple of (DataFrame with chosen trades, final_states dict mapping stream_id to state)
    """
    if df.empty:
        return df, {}
    
    # Pre-convert Date column once for all streams (only if needed)
    if 'Date' in df.columns and not pd.api.types.is_datetime64_any_dtype(df['Date']):
        df = df.copy()
        df['Date'] = pd.to_datetime(df['Date'], errors='coerce')
    
    # Sort once by Stream and Date
    needs_sorting = True
    if len(df) > 1:
        try:
            if df['Stream'].is_monotonic_increasing and df['Date'].is_monotonic_increasing:
                needs_sorting = False
        except:
            pass
    
    if needs_sorting:
        for col in ['Stream', 'Date']:
            if col in df.columns:
                if col == 'Stream' and df[col].dtype == 'object':
                    df[col] = df[col].fillna('')
        df = df.sort_values(['Stream', 'Date'], kind='mergesort').reset_index(drop=True)
    
    unique_streams = df['Stream'].unique()
    logger.info(f"Processing {len(unique_streams)} streams with sequencer logic (capturing state)...")
    
    chosen_trades = []
    final_states = {}
    
    # Process streams sequentially to capture state (parallel processing makes state capture complex)
    for stream_id in unique_streams:
        stream_mask = df['Stream'] == stream_id
        stream_df = df[stream_mask].copy()
        stream_filters_for_stream = stream_filters.get(stream_id, {})
        stream_initial_state = initial_states.get(stream_id) if initial_states else None
        
        stream_result = process_stream_daily(
            stream_df, stream_id, stream_filters_for_stream, 
            display_year, stream_initial_state, return_state=True
        )
        
        if isinstance(stream_result, tuple) and len(stream_result) == 2:
            stream_chosen_trades, stream_final_state = stream_result
            final_states[stream_id] = stream_final_state
        else:
            stream_chosen_trades = stream_result
        
        chosen_trades.extend(stream_chosen_trades)
    
    if not chosen_trades:
        return pd.DataFrame(), {}
    
    result_df = pd.DataFrame(chosen_trades)
    return result_df, final_states


__all__ = ['apply_sequencer_logic', 'apply_sequencer_logic_with_state', 'process_stream_daily', 'SLOT_ENDS']


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
    display_year: Optional[int] = None,
    initial_state: Optional[Dict] = None,
    return_state: bool = False
) -> Union[List[Dict], Tuple[List[Dict], Dict]]:
    """
    Process a single stream day by day, applying sequencer logic.
    
    REFACTORED: Deterministic, calendar-driven, canonical-time-based.
    
    Args:
        stream_df: DataFrame for the stream
        stream_id: Stream ID
        stream_filters: Filter configuration for this stream
        display_year: If provided, only return trades from this year
        initial_state: Optional initial state dict for restoration:
            {
                "current_time": str,
                "current_session": str,
                "time_slot_histories": {
                    "07:30": [1, -1, 0, ...],
                    ...
                }
            }
        
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
    
    # Initialize current_time: use restored state or default to first selectable time
    if initial_state:
        restored_time = initial_state.get('current_time')
        restored_session = initial_state.get('current_session', session)
        if restored_time and normalize_time(str(restored_time)) in selectable_times:
            current_time = normalize_time(str(restored_time))
            current_session = restored_session
            logger.info(f"Stream {stream_id}: Restored initial state: current_time={current_time}, session={current_session}")
        else:
            logger.warning(
                f"Stream {stream_id}: Invalid restored current_time '{restored_time}', "
                f"using default first selectable time"
            )
            current_time = normalize_time(str(selectable_times[0]))
            current_session = session
    else:
        current_time = normalize_time(str(selectable_times[0]))
        current_session = session
    
    previous_time = None
    
    # ============================================================================
    # INITIALIZE ROLLING HISTORIES FOR ALL CANONICAL TIMES
    # ============================================================================
    if initial_state and 'time_slot_histories' in initial_state:
        # Restore histories from checkpoint
        restored_histories = initial_state['time_slot_histories']
        time_slot_histories = {}
        for canonical_time in canonical_times:
            canonical_time_normalized = normalize_time(str(canonical_time))
            # Restore if available, otherwise initialize empty
            if canonical_time_normalized in restored_histories:
                time_slot_histories[canonical_time_normalized] = list(restored_histories[canonical_time_normalized])
            else:
                time_slot_histories[canonical_time_normalized] = []
        logger.info(f"Stream {stream_id}: Restored time_slot_histories for {len(time_slot_histories)} canonical times")
    else:
        # Initialize empty histories
        time_slot_histories = {t: [] for t in canonical_times}
    
    chosen_trades = []
    
    # ============================================================================
    # CALENDAR-DRIVEN DATE ITERATION
    # ============================================================================
    if stream_df.empty:
        return []
    
    # OPTIMIZATION: Pre-normalize dates and create Time_str column once (not per-day)
    # This avoids repeated operations
    if not pd.api.types.is_datetime64_any_dtype(stream_df['Date']):
        stream_df = stream_df.copy()  # Only copy if we need to modify
        stream_df['Date'] = pd.to_datetime(stream_df['Date'], errors='coerce')
    
    # Pre-normalize all Time values once (vectorized operation)
    if 'Time_str' not in stream_df.columns:
        stream_df = stream_df.copy()  # Only copy if we need to add column
        stream_df['Time_str'] = stream_df['Time'].astype(str).str.strip().apply(normalize_time)
    
    # Pre-normalize dates for efficient filtering
    stream_df['Date_normalized'] = stream_df['Date'].dt.normalize()
    
    # CRITICAL: Iterate only over dates present in analyzer data (data-driven, not calendar)
    # No weekends, no holidays unless present in data
    unique_dates = stream_df['Date_normalized'].unique()
    trading_dates = sorted([d for d in unique_dates if pd.notna(d)])
    
    logger.debug(f"Stream {stream_id}: Processing {len(trading_dates)} trading days (from data, not calendar)")
    
    # ============================================================================
    # DAILY PROCESSING LOOP
    # ============================================================================
    for date in trading_dates:
        # OPTIMIZATION: Use boolean indexing instead of copy (faster)
        date_mask = stream_df['Date_normalized'] == date
        date_df = stream_df[date_mask]  # Use view, not copy
        
        # Time_str already normalized above, no need to do it per-day
        
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
        # OPTIMIZATION: Use boolean indexing instead of copy (faster)
        if not date_df.empty and filtered_times_normalized:
            time_mask = ~date_df['Time_str'].isin(filtered_times_normalized)
            date_df_filtered = date_df[time_mask]  # Use view, not copy
            # Log if we filtered out any rows (shouldn't happen if logic is correct, but good to know)
            filtered_out_count = len(date_df) - len(date_df_filtered)
            if filtered_out_count > 0:
                logger.warning(
                    f"Stream {stream_id} {date}: Filtered out {filtered_out_count} trades at excluded times: "
                    f"{sorted(date_df[~time_mask]['Time_str'].unique())}"
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
            # Get available_times (selectable_times) and exclude_times_str for the function call
            available_times = selectable_times
            exclude_times_str = stream_filters.get('exclude_times', [])
            trade_row = select_trade_for_time(
                date_df, 
                current_time, 
                current_session,
                available_times,
                exclude_times_str,
                stream_id,
                date
            )
        
        old_time_for_today = str(current_time).strip()
        
        if trade_row is not None:
            # Convert Series to dict more efficiently
            trade_dict = dict(trade_row)
            
            # CRITICAL: Ensure Date is in ISO string format (YYYY-MM-DD) to match analyzer output format
            # This prevents date formatting issues when Date is a Timestamp object
            if 'Date' in trade_dict:
                date_value = trade_dict['Date']
                if isinstance(date_value, pd.Timestamp):
                    trade_dict['Date'] = date_value.strftime('%Y-%m-%d')
                elif pd.notna(date_value):
                    # Try to parse and reformat if it's already a string in wrong format
                    try:
                        parsed_date = pd.to_datetime(date_value, errors='coerce')
                        if pd.notna(parsed_date):
                            trade_dict['Date'] = parsed_date.strftime('%Y-%m-%d')
                    except:
                        pass  # Keep original if parsing fails
            
            # CRITICAL: Preserve original analyzer time before overwriting Time column
            # This is needed for downstream filtering (exclude_times filter needs actual trade time)
            original_time = trade_dict.get('Time', '')
            if original_time:
                trade_dict['actual_trade_time'] = str(original_time).strip()
            else:
                trade_dict['actual_trade_time'] = ''
            
            # CRITICAL: Time column is sequencer's authority - overwrite analyzer's Time
            # This ensures Time always reflects sequencer's intended slot, not trade's original time
            trade_dict['Time'] = str(current_time).strip()
        else:
            # Sequencer NoTrade: sequencer chose current_time but no trade exists at that slot
            # This is structural - no analyzer data at this time slot
            # Format date as ISO string (YYYY-MM-DD) to match analyzer output format
            date_str = date.strftime('%Y-%m-%d') if isinstance(date, pd.Timestamp) else str(date)
            trade_dict = {
                'Stream': stream_id,
                'Date': date_str,
                'Time': str(current_time).strip(),  # Still set Time to current_time (sequencer's intent)
                'actual_trade_time': '',  # No actual trade, so no actual time
                'Result': 'NoTrade',
                'Session': current_session,
            }
        
        # Add rolling columns for ALL canonical times (from daily_scores)
        for canonical_time in canonical_times:
            canonical_time_normalized = normalize_time(str(canonical_time))
            rolling_sum = sum(time_slot_histories.get(canonical_time_normalized, []))
            trade_dict[f"{canonical_time} Rolling"] = round(rolling_sum, 2)
            trade_dict[f"{canonical_time} Points"] = daily_scores.get(canonical_time_normalized, 0)
        
        # Calculate SL (Stop Loss): 3x Target, capped at Range
        sl_value = 0
        if trade_dict.get('Target') != 'NO DATA' and isinstance(trade_dict.get('Target'), (int, float)):
            sl_value = 3 * trade_dict['Target']
            if trade_dict.get('Range') != 'NO DATA' and isinstance(trade_dict.get('Range'), (int, float)) and trade_dict.get('Range', 0) > 0:
                sl_value = min(sl_value, trade_dict['Range'])
        trade_dict['SL'] = sl_value
        
        # Format Time Change column - show only the time it's being changed to
        time_change_display = ''
        if previous_time is not None and str(old_time_for_today) != str(previous_time):
            time_change_display = str(old_time_for_today).strip()
        elif next_time is not None:
            time_change_display = str(next_time).strip()
        trade_dict['Time Change'] = time_change_display
        
        # Add to chosen_trades (filter by year if needed)
        trade_date = pd.to_datetime(date, errors='coerce')
        if display_year is None or (pd.notna(trade_date) and trade_date.year == display_year):
            if pd.notna(trade_date):
                trade_dict['trade_date'] = trade_date
            else:
                trade_dict['trade_date'] = pd.to_datetime(date)
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
    
    # Return final state along with trades if requested
    if return_state:
        final_state = {
            "current_time": current_time,
            "current_session": current_session,
            "time_slot_histories": {k: list(v) for k, v in time_slot_histories.items()}  # Copy lists
        }
        return chosen_trades, final_state
    
    return chosen_trades


def apply_sequencer_logic(
    df: pd.DataFrame,
    stream_filters: Dict[str, Dict],
    display_year: Optional[int] = None,
    parallel: bool = True,
    initial_states: Optional[Dict[str, Dict]] = None
) -> pd.DataFrame:
    """
    Apply sequencer logic to select one trade per day per stream.
    Uses time change logic similar to sequential processor.
    
    Processes ALL historical data to build accurate time slot histories,
    and returns trades from all years (or filtered by display_year if specified).
    
    OPTIMIZED: Can process streams in parallel for faster rebuilds.
    
    Args:
        df: DataFrame with all trades from analyzer_runs
        stream_filters: Per-stream filter configuration
        display_year: If provided, only return trades from this year (for display).
                     If None, return trades from ALL years.
                     All data is still processed to build accurate histories.
        parallel: If True (default), process streams in parallel. Set False for debugging.
        initial_states: Optional dict mapping stream_id to initial state for restoration:
            {
                "ES1": {
                    "current_time": "07:30",
                    "current_session": "S1",
                    "time_slot_histories": {...}
                },
                ...
            }
        
    Returns:
        DataFrame with one chosen trade per day per stream
    """
    if df.empty:
        return df
    
    # Pre-convert Date column once for all streams (only if needed)
    if 'Date' in df.columns and not pd.api.types.is_datetime64_any_dtype(df['Date']):
        df = df.copy()  # Only copy if we need to modify
        df['Date'] = pd.to_datetime(df['Date'], errors='coerce')
    
    # Sort once by Stream and Date for better cache locality
    # OPTIMIZED: Use mergesort for stability, but only sort if not already sorted
    # Debug: Check for None values before sorting
    needs_sorting = True
    if len(df) > 1:
        # Quick check: if Stream and Date are already sorted, skip sorting
        # This is a heuristic - if data comes pre-sorted, we save time
        try:
            if df['Stream'].is_monotonic_increasing and df['Date'].is_monotonic_increasing:
                needs_sorting = False
        except:
            pass  # If check fails, proceed with sorting
    
    if needs_sorting:
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
    
    # Get unique streams
    unique_streams = df['Stream'].unique()
    logger.info(f"Processing {len(unique_streams)} streams with sequencer logic...")
    
    # OPTIMIZATION: Process streams in parallel for faster rebuilds
    if parallel and len(unique_streams) > 1:
        import multiprocessing as mp
        from concurrent.futures import ThreadPoolExecutor, as_completed
        
        chosen_trades = []
        max_workers = min(len(unique_streams), mp.cpu_count())
        
        def process_single_stream(stream_id: str):
            """Process a single stream (for parallel execution)."""
            stream_mask = df['Stream'] == stream_id
            # CRITICAL: Must copy here because process_stream_daily modifies the DataFrame
            # (adds Time_str, Date_normalized columns). Views would cause issues in parallel execution.
            stream_df = df[stream_mask].copy()
            stream_filters_for_stream = stream_filters.get(stream_id, {})
            stream_initial_state = initial_states.get(stream_id) if initial_states else None
            return process_stream_daily(stream_df, stream_id, stream_filters_for_stream, display_year, stream_initial_state, return_state=False)
        
        logger.info(f"Processing {len(unique_streams)} streams in parallel ({max_workers} workers)...")
        with ThreadPoolExecutor(max_workers=max_workers) as executor:
            future_to_stream = {
                executor.submit(process_single_stream, stream_id): stream_id
                for stream_id in unique_streams
            }
            
            # Collect results as they complete
            for future in as_completed(future_to_stream):
                stream_id = future_to_stream[future]
                try:
                    stream_chosen_trades = future.result()
                    chosen_trades.extend(stream_chosen_trades)
                    logger.debug(f"Completed sequencer processing for stream {stream_id}: {len(stream_chosen_trades)} trades")
                except Exception as e:
                    logger.error(f"Error processing stream {stream_id} in sequencer: {e}")
                    import traceback
                    logger.debug(f"Traceback: {traceback.format_exc()}")
    else:
        # Sequential processing (for debugging or single stream)
        chosen_trades = []
        for stream_id in unique_streams:
            stream_mask = df['Stream'] == stream_id
            stream_df = df[stream_mask]  # Use view, not copy (faster)
            stream_filters_for_stream = stream_filters.get(stream_id, {})
            stream_initial_state = initial_states.get(stream_id) if initial_states else None
            
            stream_chosen_trades = process_stream_daily(stream_df, stream_id, stream_filters_for_stream, display_year, stream_initial_state, return_state=False)
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
        
        # CRITICAL: Also check actual_trade_time to ensure excluded times don't appear
        # This catches cases where Time was overwritten but actual_trade_time contains excluded time
        excluded_times_in_output = []
        if 'actual_trade_time' in stream_rows.columns:
            for idx, row in stream_rows.iterrows():
                actual_time = row.get('actual_trade_time', '')
                if actual_time:
                    actual_time_normalized = normalize_time(str(actual_time))
                    if actual_time_normalized in filtered_times_set:
                        date = row.get('Date', 'unknown')
                        excluded_times_in_output.append((idx, date, actual_time_normalized))
        
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
        
        if excluded_times_in_output:
            # Log excluded times that appear in output (should be filtered by filter_engine)
            for idx, date, actual_time in excluded_times_in_output:
                logger.error(
                    f"Stream {stream_id} {date}: CRITICAL - Excluded time '{actual_time}' appears in output! "
                    f"This should have been filtered by filter_engine. Filtered times: {sorted(filtered_times)}"
                )
            # Don't raise error here - filter_engine should handle this, but log it as a warning
            logger.warning(
                f"Stream {stream_id}: Found {len(excluded_times_in_output)} trades with excluded times in output. "
                f"These should be marked as final_allowed=False by filter_engine."
            )
    
    # Ensure SL column exists
    if 'SL' not in result_df.columns:
        logger.warning("SL column missing, adding with 0 values")
        result_df['SL'] = 0
    
    return result_df.reset_index(drop=True)

