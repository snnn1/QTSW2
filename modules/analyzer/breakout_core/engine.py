from __future__ import annotations
import pandas as pd
import numpy as np
from dataclasses import dataclass, asdict
from typing import List, Dict, Optional

# Import new logic modules
from logic.config_logic import ConfigManager, RunParams
from logic.utility_logic import UtilityManager
from logic.validation_logic import ValidationManager
from logic.instrument_logic import InstrumentManager
from logic.time_logic import TimeManager
from logic.debug_logic import DebugManager
from logic.range_logic import RangeDetector, SlotRange
from logic.entry_logic import EntryDetector, EntryResult
from logic.price_tracking_logic import PriceTracker, TradeExecution
from logic.result_logic import ResultProcessor
from logic.loss_logic import LossManager, StopLossConfig


def _process_single_range(
    df: pd.DataFrame,
    R: SlotRange,
    rp: RunParams,
    config_manager: ConfigManager,
    instrument_manager: InstrumentManager,
    utility_manager: UtilityManager,
    entry_detector: EntryDetector,
    price_tracker: PriceTracker,
    result_processor: ResultProcessor,
    time_manager: TimeManager,
    streamS1: str,
    streamS2: str,
    inst: str,
    ticksz: float,
    debug: bool
) -> Optional[Dict[str, object]]:
    """
    Process a single range - thread-safe, no shared state
    
    Args:
        df: Full market data DataFrame
        R: SlotRange to process
        rp: Run parameters
        config_manager: Configuration manager
        instrument_manager: Instrument manager
        utility_manager: Utility manager
        entry_detector: Entry detector
        price_tracker: Price tracker
        result_processor: Result processor
        time_manager: Time manager
        streamS1: Stream tag for S1
        streamS2: Stream tag for S2
        inst: Instrument code
        ticksz: Tick size
        debug: Debug flag
        
    Returns:
        Result row dictionary or None (if NoTrade and write_no_trade_rows=False)
    """
    sess = R.session
    stream = streamS1 if sess == "S1" else streamS2
    time_label = R.end_label
    
    # Validate end_label - ensure it exists and is not empty
    if not time_label or time_label == "":
        if debug:
            print(f"WARNING: Missing end_label for range {R.date} {R.session}, skipping")
        return None
    
    # Validate time_label format (should be HH:MM)
    if not isinstance(time_label, str) or ":" not in time_label:
        if debug:
            print(f"WARNING: Invalid time_label format '{time_label}' for range {R.date} {R.session}, skipping")
        return None
    
    # Validate HH:MM format
    try:
        parts = time_label.split(":")
        if len(parts) != 2:
            raise ValueError("Invalid format")
        hour = int(parts[0])
        minute = int(parts[1])
        if hour < 0 or hour > 23 or minute < 0 or minute > 59:
            raise ValueError("Invalid time values")
    except (ValueError, IndexError):
        if debug:
            print(f"WARNING: Invalid time_label format '{time_label}' for range {R.date} {R.session}, skipping")
        return None
    
    # Get data for trade execution (24 hours) and MFE calculation (until next day same slot)
    day_df = df[(df["timestamp"] >= R.end_ts) & (df["timestamp"] < R.end_ts + pd.Timedelta(hours=24))].copy()
    
    # For MFE calculation, we need data until next day same slot
    # Calculate MFE end time
    if time_label:
        if R.date.weekday() == 4:  # Friday
            mfe_end_date = R.date + pd.Timedelta(days=3)  # Friday to Monday
        else:
            mfe_end_date = R.date + pd.Timedelta(days=1)  # Regular day
        
        hour_part = int(time_label.split(":")[0])
        minute_part = int(time_label.split(":")[1])
        # Slot times are Chicago trading hours (e.g., 07:30 = 7:30 AM Chicago time)
        # Create MFE end time directly in Chicago time
        if mfe_end_date.tz is not None:
            # Timezone-aware: create timestamp in same timezone (Chicago)
            mfe_end_time = mfe_end_date.replace(
                hour=hour_part, 
                minute=minute_part, 
                second=0
            )
        else:
            # Naive timestamp (shouldn't happen, but handle it)
            mfe_end_time = mfe_end_date.replace(
                hour=hour_part, 
                minute=minute_part, 
                second=0
            )
        
        # Get extended data for MFE calculation
        mfe_df = df[(df["timestamp"] >= R.end_ts) & (df["timestamp"] < mfe_end_time)].copy()
    else:
        mfe_df = day_df.copy()
    
    # Use base target only (no levels)
    target_pts = instrument_manager.get_base_target(inst)
    
    brk_long = utility_manager.round_to_tick(R.range_high + ticksz, ticksz)
    brk_short = utility_manager.round_to_tick(R.range_low - ticksz, ticksz)
    
    # Use entry detection logic
    entry_result = entry_detector.detect_entry(day_df, R, brk_long, brk_short, R.freeze_close, R.end_ts)
    
    if entry_result.entry_direction is None or entry_result.entry_direction == "NoTrade":
        # NoTrade - create NoTrade entry if enabled
        if entry_result.entry_direction == "NoTrade" and rp.write_no_trade_rows:
            return result_processor.create_result_row(
                R.date, time_label, target_pts, 0.0, "NA", "NoTrade", R.range_size, 
                stream, inst, sess, 0.0,
                entry_price=0.0,
                exit_price=0.0,
                stop_loss=0.0
            )
        return None
    
    entry_dir = entry_result.entry_direction
    entry_px = entry_result.entry_price
    entry_time = entry_result.entry_time
    
    # Calculate target and stop loss
    target_level = entry_detector.calculate_target_level(entry_px, entry_dir, target_pts)
    initial_sl = entry_detector.calculate_stop_loss(entry_px, entry_dir, target_pts, inst, R.range_size, R.range_high, R.range_low)
    
    # Calculate expiry time
    expiry_time = time_manager.get_expiry_time(R.date, time_label, sess)
    
    # Execute trade with integrated MFE and break even logic
    trade_execution = price_tracker.execute_trade(
        mfe_df, entry_time, entry_px, entry_dir, 
        target_level, initial_sl, expiry_time,
        target_pts, inst, time_label, R.date, debug
    )
    
    # Calculate profit using the integrated logic
    display_profit = price_tracker.calculate_profit(
        entry_px, trade_execution.exit_price, entry_dir, 
        trade_execution.result_classification,
        trade_execution.t1_triggered, 
        target_pts, inst, trade_execution.target_hit,
    )
    
    if debug:
        try:
            print(f"  FINAL RESULT: {display_profit} profit ({trade_execution.result_classification})")
        except Exception:
            # Silently skip if encoding error
            pass
    
    # Return result row
    # Store initial stop loss (before T1 adjustment) for analysis
    return result_processor.create_result_row(
        R.date, time_label, target_pts, trade_execution.peak, 
        entry_dir, trade_execution.result_classification, R.range_size, stream, inst, sess, display_profit,
        entry_time=entry_time,
        exit_time=trade_execution.exit_time,
        entry_price=entry_px,
        exit_price=trade_execution.exit_price,
        stop_loss=initial_sl  # Store initial stop loss (before T1 adjustment)
    )


def _process_single_range_dict(
    df: pd.DataFrame,
    range_dict: Dict,
    rp: RunParams,
    config_manager: ConfigManager,
    instrument_manager: InstrumentManager,
    utility_manager: UtilityManager,
    entry_detector: EntryDetector,
    price_tracker: PriceTracker,
    result_processor: ResultProcessor,
    time_manager: TimeManager,
    streamS1: str,
    streamS2: str,
    inst: str,
    ticksz: float,
    debug: bool
) -> Optional[Dict[str, object]]:
    """
    Wrapper that converts dict back to SlotRange for parallel processing
    
    Args:
        range_dict: Dictionary representation of SlotRange
        ... (other args same as _process_single_range)
        
    Returns:
        Result row dictionary or None
    """
    # Reconstruct SlotRange from dict
    # Handle timestamp conversion (may be ISO string or Timestamp)
    def to_timestamp(ts):
        if isinstance(ts, str):
            return pd.Timestamp(ts)
        elif isinstance(ts, pd.Timestamp):
            return ts
        else:
            return pd.Timestamp(ts)
    
    R = SlotRange(
        date=to_timestamp(range_dict['date']),
        session=range_dict['session'],
        end_label=range_dict['end_label'],
        start_ts=to_timestamp(range_dict['start_ts']),
        end_ts=to_timestamp(range_dict['end_ts']),
        range_high=range_dict['range_high'],
        range_low=range_dict['range_low'],
        range_size=range_dict['range_size'],
        freeze_close=range_dict['freeze_close']
    )
    
    return _process_single_range(
        df, R, rp, config_manager, instrument_manager, utility_manager,
        entry_detector, price_tracker, result_processor, time_manager,
        streamS1, streamS2, inst, ticksz, debug
    )


def _add_no_trade_by_market_close(results_df: pd.DataFrame, ranges, rp, debug: bool) -> pd.DataFrame:
    """
    Add NoTrade entries for days with no entries by market close
    
    Args:
        results_df: Current results DataFrame
        ranges: List of range objects
        rp: Run parameters
        debug: Debug flag
        
    Returns:
        Updated results DataFrame with no-trade entries
    """
    if results_df.empty:
        return results_df
    
    # Get all range combinations (date + session + time)
    range_combinations = set()
    for R in ranges:
        range_combinations.add((R.date.date(), R.session, R.end_label))
    
    # Get all result combinations (date + session + time)
    result_combinations = set()
    if 'Date' in results_df.columns and 'Session' in results_df.columns and 'Time' in results_df.columns:
        for _, row in results_df.iterrows():
            result_combinations.add((
                pd.to_datetime(row['Date']).date(),
                row['Session'],
                row['Time']
            ))
    
    # Find range combinations that have no corresponding results
    no_trade_combinations = range_combinations - result_combinations
    
    if debug and no_trade_combinations:
        print(f"\n=== NO TRADE BY MARKET CLOSE ===")
        print(f"Time slots with no entries: {sorted(no_trade_combinations)}")
    
    # Add no-trade entries for these combinations
    no_trade_rows = []
    for no_trade_date, no_trade_session, no_trade_time in no_trade_combinations:
        # Find the specific range for this combination
        matching_ranges = [R for R in ranges if (
            R.date.date() == no_trade_date and 
            R.session == no_trade_session and 
            R.end_label == no_trade_time
        )]
        
        for R in matching_ranges:
            sess = R.session
            # Get stream tag based on session
            stream = f"{rp.instrument.upper()}{'1' if sess == 'S1' else '2'}"
            time_label = R.end_label
            
            # Use base target only (no levels)
            config_manager = ConfigManager()
            target_pts = instrument_manager.get_base_target(rp.instrument)
            
            if rp.write_no_trade_rows:
                no_trade_row = {
                    "Date": R.date.date().isoformat(),
                    "Time": time_label,
                    "EntryTime": "",
                    "ExitTime": "",
                    "EntryPrice": 0.0,
                    "ExitPrice": 0.0,
                    "StopLoss": 0.0,
                    "Target": target_pts,
                    "Peak": 0.0,
                    "Direction": "NA",
                    "Result": "NoTrade",
                    "Range": R.range_size,
                    "Stream": stream,
                    "Instrument": rp.instrument.upper(),
                    "Session": sess,
                    "Profit": 0.0,
                    "_sortTime": int(time_label.replace(":", ""))
                }
                no_trade_rows.append(no_trade_row)
    
    # Add no-trade rows to results
    if no_trade_rows:
        no_trade_df = pd.DataFrame(no_trade_rows)
        
        # Ensure both DataFrames have Date as datetime and _sortTime
        if not results_df.empty:
            results_df["Date"] = pd.to_datetime(results_df["Date"])
            if "_sortTime" not in results_df.columns and "Time" in results_df.columns:
                from breakout_core.utils import hhmm_to_sort_int
                results_df["_sortTime"] = results_df["Time"].apply(hhmm_to_sort_int)
        
        no_trade_df["Date"] = pd.to_datetime(no_trade_df["Date"])
        
        results_df = pd.concat([results_df, no_trade_df], ignore_index=True)
        
        # Sort by date and time (earliest first)
        results_df = results_df.sort_values(['Date', '_sortTime'], ascending=[True, True]).reset_index(drop=True)
        
        # Convert Date back to string format
        results_df["Date"] = results_df["Date"].dt.strftime("%Y-%m-%d")
    
    return results_df





def run_strategy(df: pd.DataFrame, rp: RunParams, debug: bool = False) -> pd.DataFrame:
    """
    Run the breakout trading strategy using modular logic components
    
    Args:
        df: Market data DataFrame (must have timestamp, open, high, low, close, instrument columns)
        rp: Run parameters (instrument, sessions, slots, trade days, etc.)
        debug: Enable debug output for detailed logging
        
    Returns:
        DataFrame with trade results (Date, Time, Target, Peak, Direction, Result, Range, Stream, Instrument, Session, Profit)
        
    Note:
        - Data must be in America/Chicago timezone (handled by translator)
        - All enabled slots are processed independently
        - Uses base target only (first level of target ladder)
    """
    # Initialize logic components
    config_manager = ConfigManager()
    utility_manager = UtilityManager()
    validation_manager = ValidationManager()
    instrument_manager = InstrumentManager()
    time_manager = TimeManager()
    debug_manager = DebugManager(debug)
    range_detector = RangeDetector(config_manager.get_slot_config())
    
    # Initialize components
    entry_detector = EntryDetector(config_manager=config_manager, instrument_manager=instrument_manager)
    price_tracker = PriceTracker(debug_manager=debug_manager, 
                                 instrument_manager=instrument_manager,
                                 config_manager=config_manager)
    result_processor = ResultProcessor(instrument_manager=instrument_manager)
    
    # Validate inputs
    validation_result = validation_manager.validate_dataframe(df)
    if not validation_result.is_valid:
        raise ValueError(f"Data validation failed: {validation_result.errors}")
    
    validation_result = validation_manager.validate_run_params(rp)
    if not validation_result.is_valid:
        raise ValueError(f"Parameter validation failed: {validation_result.errors}")
    
    # Get instrument-specific configuration
    inst = rp.instrument
    ticksz = instrument_manager.get_tick_size(inst)
    ladder = instrument_manager.get_target_ladder(inst)
    streamS1 = instrument_manager.get_stream_tag(inst, "S1")
    streamS2 = instrument_manager.get_stream_tag(inst, "S2")
    
    # Start performance monitoring
    debug_manager.start_timer()

    df = df[df["instrument"].str.upper() == inst.upper()].copy()
    if df.empty:
        if debug:
            print(f"DEBUG: No data found for instrument {inst.upper()}")
        return pd.DataFrame(columns=["Date","Time","EntryTime","ExitTime","EntryPrice","ExitPrice","StopLoss","Target","Peak","Direction","Result","Range","Stream","Instrument","Session","Profit"])

    # Data is already in correct timezone (America/Chicago) - handled by translator

    if debug:
        print(f"DEBUG: Running strategy for {inst.upper()}")
        print(f"DEBUG: Data shape: {df.shape}")
        print(f"DEBUG: Date range: {df['timestamp'].min()} to {df['timestamp'].max()}")
        print(f"DEBUG: Enabled sessions: {rp.enabled_sessions}")
        print(f"DEBUG: Enabled slots: {rp.enabled_slots}")
        print(f"DEBUG: Trade days: {rp.trade_days}")

    ranges = range_detector.build_slot_ranges(df, rp, debug)
    
    print(f"Found {len(ranges)} slot ranges")
    if len(ranges) == 0:
        print(f"ERROR: No ranges found - this is why no results will be generated")
        print(f"  Check if data contains valid trading days and session times")
        return pd.DataFrame(columns=["Date","Time","EntryTime","ExitTime","EntryPrice","ExitPrice","StopLoss","Target","Peak","Direction","Result","Range","Stream","Instrument","Session","Profit"])
    
    print(f"Starting trade execution for {len(ranges)} ranges...")
    
    # Determine if we should use parallel processing
    # Use parallel for large datasets (> 100 ranges) and when not in debug mode
    # (debug mode has interleaved output issues with parallel processing)
    use_parallel = len(ranges) > 100 and not debug
    
    if use_parallel:
        try:
            # Import parallel processor (located in modules/analyzer/parallel_processor.py)
            import sys
            from pathlib import Path
            # Add analyzer root to path for parallel_processor import
            analyzer_root = Path(__file__).parent.parent
            if str(analyzer_root) not in sys.path:
                sys.path.insert(0, str(analyzer_root))
            from parallel_processor import ParallelProcessor
            
            print(f"Using parallel processing for {len(ranges)} ranges...")
            processor = ParallelProcessor(enable_parallel=True)
            
            # Convert SlotRange objects to dicts for parallel processor
            # Convert timestamps to ISO strings for serialization
            range_dicts = []
            for R in ranges:
                range_dict = {
                    'date': R.date.isoformat() if isinstance(R.date, pd.Timestamp) else str(R.date),
                    'session': R.session,
                    'end_label': R.end_label,
                    'start_ts': R.start_ts.isoformat() if isinstance(R.start_ts, pd.Timestamp) else str(R.start_ts),
                    'end_ts': R.end_ts.isoformat() if isinstance(R.end_ts, pd.Timestamp) else str(R.end_ts),
                    'range_high': float(R.range_high),
                    'range_low': float(R.range_low),
                    'range_size': float(R.range_size),
                    'freeze_close': float(R.freeze_close)
                }
                range_dicts.append(range_dict)
            
            # Process ranges in parallel
            results = processor.process_dataframe_parallel(
                df, range_dicts, _process_single_range_dict,
                rp, config_manager, instrument_manager, utility_manager,
                entry_detector, price_tracker, result_processor, time_manager,
                streamS1, streamS2, inst, ticksz, debug
            )
            
            # Filter out None results and collect rows
            rows = [r for r in results if r is not None]
            
        except ImportError:
            # Fall back to sequential if parallel processor not available
            print("Parallel processor not available, using sequential processing")
            use_parallel = False
    
    if not use_parallel:
        # Sequential processing (original code path)
        rows: List[Dict[str,object]] = []
        
        ranges_processed = 0
        progress_interval = 500  # Log every 500 ranges
        last_logged_date = None  # Track last logged date to only log once per day
        slots_per_day = {}  # Track slots processed per day: {date: set(slots)}
        
        for R in ranges:
            ranges_processed += 1
            
            # Log progress periodically
            if ranges_processed == 1 or ranges_processed % progress_interval == 0 or ranges_processed == len(ranges):
                print(f"Processing range {ranges_processed}/{len(ranges)}: {len(rows)} trades generated")
            
            # Also log every 1000 ranges for very large datasets
            if ranges_processed % 1000 == 0:
                print(f"Progress: {ranges_processed}/{len(ranges)} ranges ({ranges_processed*100//len(ranges)}%), {len(rows)} trades")
            
            # Debug: Track slots per day and log summary when date changes
            if debug:
                try:
                    current_date = R.date.date()
                    time_label = R.end_label
                    
                    # Track slots for current date
                    if current_date not in slots_per_day:
                        slots_per_day[current_date] = set()
                    if time_label:
                        slots_per_day[current_date].add(time_label)
                    
                    # Log when date changes (once per day)
                    if last_logged_date != current_date:
                        # Log summary for previous day if it exists
                        if last_logged_date is not None and last_logged_date in slots_per_day:
                            prev_slots = sorted(slots_per_day[last_logged_date])
                            slots_str = ", ".join(prev_slots) if prev_slots else "none"
                            print(f"Completed date {last_logged_date}: processed slots {slots_str}")
                        
                        # Start new day
                        print(f"\nProcessing date: {current_date}")
                        last_logged_date = current_date
                except Exception:
                    # Silently skip if encoding error
                    pass
            
            # Process single range
            result = _process_single_range(
                df, R, rp, config_manager, instrument_manager, utility_manager,
                entry_detector, price_tracker, result_processor, time_manager,
                streamS1, streamS2, inst, ticksz, debug
            )
            
            if result is not None:
                rows.append(result)
        
        # Log final day's summary if debug is enabled
        if debug and last_logged_date is not None and last_logged_date in slots_per_day:
            try:
                final_slots = sorted(slots_per_day[last_logged_date])
                slots_str = ", ".join(final_slots) if final_slots else "none"
                print(f"Completed date {last_logged_date}: processed slots {slots_str}")
            except Exception:
                pass

    # End performance monitoring
    total_time = debug_manager.end_timer()
    
    # Process and return results
    results_df = result_processor.process_results(rows)
    
    # Recreate _sortTime from Time column for proper sorting (process_results drops it)
    if not results_df.empty and "Time" in results_df.columns:
        from breakout_core.utils import hhmm_to_sort_int
        # Only calculate _sortTime for valid Time values, use 0 for invalid/empty
        results_df["_sortTime"] = results_df["Time"].apply(
            lambda x: hhmm_to_sort_int(str(x)) if pd.notna(x) and str(x) and ":" in str(x) else 0
        )
    
    # Ensure Date is datetime for proper sorting (process_results converts it back to string)
    if not results_df.empty and "Date" in results_df.columns:
        results_df["Date"] = pd.to_datetime(results_df["Date"])
    
    # Add no-trade entries for days with no entries by market close
    results_df = _add_no_trade_by_market_close(results_df, ranges, rp, debug)
    
    # Final sort by Date and Time (earliest first)
    if not results_df.empty and "Date" in results_df.columns and "_sortTime" in results_df.columns:
        # Ensure Date is datetime for sorting
        if not pd.api.types.is_datetime64_any_dtype(results_df["Date"]):
            results_df["Date"] = pd.to_datetime(results_df["Date"])
        
        results_df = results_df.sort_values(['Date', '_sortTime'], ascending=[True, True]).reset_index(drop=True)
        
        # Drop _sortTime column before returning
        if "_sortTime" in results_df.columns:
            results_df = results_df.drop(columns=["_sortTime"])
        
        # Convert Date back to string format for display
        if pd.api.types.is_datetime64_any_dtype(results_df["Date"]):
            results_df["Date"] = results_df["Date"].dt.strftime("%Y-%m-%d")
    
    # Print performance summary if debug enabled
    if debug:
        debug_manager.print_performance_summary()
    
    print(f"Trade execution completed: {len(results_df)} trades generated from {len(ranges)} ranges")
    
    return results_df
