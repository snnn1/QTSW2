from __future__ import annotations
import pandas as pd
import numpy as np
from dataclasses import dataclass
from typing import List, Dict

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
from logic.analyzer_time_slot_integration import AnalyzerTimeSlotIntegration
from logic.target_change_logic import TargetChangeManager
from logic.rolling_target_change_logic_fixed import RollingTargetChangeManager, TradeRecord


def _add_no_trade_by_market_close(results_df: pd.DataFrame, ranges, rp, debug: bool, 
                                target_change_manager: TargetChangeManager,
                                enable_dynamic_targets: bool = False,
                                target_changes: dict = None,
                                active_slots: dict = None, enable_slot_switching: bool = False) -> pd.DataFrame:
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
        # If slot switching is enabled, only add NoTrade for active slots
        if enable_slot_switching and active_slots:
            active_slot = active_slots.get((no_trade_session, no_trade_date))
            if active_slot != no_trade_time:
                continue  # Skip non-active slots
        
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
            target_pts = config_manager.get_base_target(rp.instrument)
            
            if rp.write_no_trade_rows:
                no_trade_rows.append({
                    "Date": R.date.date().isoformat(),
                    "Time": time_label,
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
                })
    
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





def run_strategy(df: pd.DataFrame, rp: RunParams, debug: bool = False, 
                historical_results: pd.DataFrame = None, enable_slot_switching: bool = False,
                include_simulated_results: bool = False, enable_dynamic_targets: bool = False) -> pd.DataFrame:
    """
    Run the breakout trading strategy using modular logic components
    
    Args:
        df: Market data DataFrame
        rp: Run parameters
        debug: Enable debug output
        historical_results: Historical trade results for slot switching (optional)
        enable_slot_switching: Enable dynamic time slot switching (default: False)
        include_simulated_results: Include simulated (non-active) slot results in output (default: False)
        enable_dynamic_targets: Enable dynamic target progression based on trade performance (default: False)
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
    entry_detector = EntryDetector()
    price_tracker = PriceTracker(debug_manager=debug_manager)
    result_processor = ResultProcessor()
    
    # Initialize time slot switching system (if enabled)
    slot_integration = None
    
    # Target change and time slot switching disabled - only available in sequential processor
    target_change_manager = None
    slot_integration = None
    # Target change and time slot switching disabled in analyzer core
    
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
        return pd.DataFrame(columns=["Date","Time","Target","Peak","Direction","Result","Range","Stream","Instrument","Session","Profit"])

    # Data is already in correct timezone from NinjaTrader export with bar time fix
    # No timezone validation needed

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
        return pd.DataFrame(columns=["Date","Time","Target","Peak","Direction","Result","Range","Stream","Instrument","Session","Profit"])
    
    print(f"Starting trade execution for {len(ranges)} ranges...")
    
    # Determine active slots for each session if slot switching is enabled
    active_slots = {}
    if False:  # Slot switching disabled in analyzer core
        # Group ranges by session and date to determine active slots
        session_dates = {}
        for R in ranges:
            key = (R.session, R.date.date())
            if key not in session_dates:
                session_dates[key] = []
            session_dates[key].append(R)
        
        # Initialize active slots with defaults for each session/date combination
        # But we need to track the current active slot across days
        current_active_slot = {}  # session -> current_slot
        for (session, date), session_ranges in session_dates.items():
            # Start with default slot for each session if not already set
            if session not in current_active_slot:
                current_active_slot[session] = slot_integration.session_defaults.get(session, "08:00")
            
            # Use the current active slot for this session
            active_slots[(session, date)] = current_active_slot[session]
            # Also initialize the slot integration's current_active_slots
            slot_integration.current_active_slots[(session, date)] = current_active_slot[session]
        
        # Update active slots for future dates as switches happen
        def update_future_dates(session, new_slot):
            for (s, d), _ in session_dates.items():
                if s == session and d > R.date.date():
                    active_slots[(s, d)] = new_slot
                    slot_integration.current_active_slots[(s, d)] = new_slot
        
        
        # Don't filter ranges upfront - we need to process them dynamically
        # to handle slot switching during processing
    
    rows: List[Dict[str,object]] = []
    
    ranges_processed = 0
    last_progress_log = 0
    progress_interval = 500  # Log every 500 ranges
    
    for R in ranges:
        ranges_processed += 1
        
        # Log progress periodically
        if ranges_processed == 1 or ranges_processed % progress_interval == 0 or ranges_processed == len(ranges):
            print(f"Processing range {ranges_processed}/{len(ranges)}: {len(rows)} trades generated")
        
        # Also log every 1000 ranges for very large datasets
        if ranges_processed % 1000 == 0:
            print(f"Progress: {ranges_processed}/{len(ranges)} ranges ({ranges_processed*100//len(ranges)}%), {len(rows)} trades")
        sess = R.session
        stream = streamS1 if sess=="S1" else streamS2
        time_label = R.end_label

        # Debug: Show range end time (no emojis to avoid Windows encoding errors)
        if debug:
            try:
                print(f"\nProcessing range for date: {R.date.date()}, slot: {time_label}")
                print(f"   Range end_ts: {R.end_ts}")
                print(f"   Range end_ts UTC: {R.end_ts.tz_convert('UTC') if R.end_ts.tz else 'N/A'}")
            except Exception:
                # Silently skip if encoding error
                pass

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
        target_pts = config_manager.get_base_target(inst)
        analysis_target = target_pts
        
        actual_target_profit = instrument_manager.get_target_profit(inst, target_pts)
        brk_long  = utility_manager.round_to_tick(R.range_high + ticksz, ticksz)
        brk_short = utility_manager.round_to_tick(R.range_low  - ticksz, ticksz)

        # Use entry detection logic
        entry_result = entry_detector.detect_entry(day_df, R, brk_long, brk_short, R.freeze_close, R.end_ts)
        
        if entry_result.entry_direction is None or entry_result.entry_direction == "NoTrade":
            result_type = "NoTrade" if entry_result.entry_direction == "NoTrade" else "NoTrade"  # Treat None as NoTrade for performance tracking
            
            # Always track performance for all enabled slots (for slot switching logic)
            if False:  # Slot switching disabled in analyzer core
                slot_integration.slot_manager.add_trade_result(
                    R.date.date(), time_label, result_type
                )
            
                
                if entry_result.entry_direction == "NoTrade":
                    # NoTrade - create NoTrade entry for active slot
                    if rp.write_no_trade_rows:
                        rows.append(result_processor.create_result_row(R.date, time_label, target_pts, 0.0, "NA", "NoTrade", R.range_size, stream, inst, sess, 0.0))
                else:
                    # None - create Setup entry for active slot
                    if rp.write_setup_rows:
                        rows.append(result_processor.create_result_row(R.date, time_label, target_pts, 0.0, "NA", "Setup", R.range_size, stream, inst, sess, 0.0))
            continue

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
        
        # Always track performance for all slots (for battle mode logic)
        if False:  # Slot switching disabled in analyzer core
            slot_integration.slot_manager.add_trade_result(
                R.date.date(), time_label, trade_execution.result_classification
            )
        
        # Update target based on trade peak performance (only if dynamic targets enabled)
        if enable_dynamic_targets:
            # Store the current target before updating
            # Target changes disabled in analyzer core - only available in sequential processor
            if debug:
                print(f"Target change disabled - peak: {trade_execution.peak}")
        
        # Add result row
        rows.append(result_processor.create_result_row(
            R.date, time_label, target_pts, trade_execution.peak, 
            entry_dir, trade_execution.result_classification, R.range_size, stream, inst, sess, display_profit
        ))

    # End performance monitoring
    total_time = debug_manager.end_timer()
    
    # Process and return results
    results_df = result_processor.process_results(rows)
    
    # Recreate _sortTime from Time column for proper sorting (process_results drops it)
    if not results_df.empty and "Time" in results_df.columns:
        from breakout_core.utils import hhmm_to_sort_int
        results_df["_sortTime"] = results_df["Time"].apply(hhmm_to_sort_int)
    
    # Ensure Date is datetime for proper sorting (process_results converts it back to string)
    if not results_df.empty and "Date" in results_df.columns:
        results_df["Date"] = pd.to_datetime(results_df["Date"])
    
    # Add no-trade entries for days with no entries by market close
    results_df = _add_no_trade_by_market_close(results_df, ranges, rp, debug, None, False, None, None, False)
    
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
