"""
Master Matrix - "All trades in order" across all streams

This module creates a unified master table that merges all trades from all streams
(ES1, ES2, GC1, GC2, CL1, CL2, NQ1, NQ2, NG1, NG2, etc.) into one sorted table.

Author: Quantitative Trading System
Date: 2025
"""

import pandas as pd
import numpy as np
from pathlib import Path
from typing import List, Optional, Dict, Tuple
from datetime import datetime, timedelta
import logging

# Configure logging - simple and clean like the dashboard
import sys
import os

# Create log file - same location as backend
LOG_FILE = Path(__file__).parent.parent / "logs" / "master_matrix.log"
LOG_FILE.parent.mkdir(parents=True, exist_ok=True)

# Set up logger - simple and clean
logger = logging.getLogger(__name__)
logger.setLevel(logging.INFO)

# Clear any existing handlers
logger.handlers.clear()

# Add file handler
file_handler = logging.FileHandler(LOG_FILE, mode='a', encoding='utf-8')
file_handler.setLevel(logging.INFO)
file_formatter = logging.Formatter('%(asctime)s - %(levelname)s - %(message)s')
file_handler.setFormatter(file_formatter)
logger.addHandler(file_handler)

# Add console handler (stderr)
console_handler = logging.StreamHandler(sys.stderr)
console_handler.setLevel(logging.INFO)
console_handler.setFormatter(file_formatter)
logger.addHandler(console_handler)

# Log module load
logger.info("Master Matrix module loaded")


class MasterMatrix:
    """
    Creates a master matrix by merging all trade files from all streams.
    """
    
    def __init__(self, analyzer_runs_dir: str = "data/analyzer_runs",
                 stream_filters: Optional[Dict[str, Dict]] = None,
                 sequencer_runs_dir: Optional[str] = None):
        """
        Initialize Master Matrix builder.
        Works like the sequencer: reads from analyzer_runs and applies time change logic
        to select chosen trades (one per day per stream).
        
        Args:
            analyzer_runs_dir: Directory containing analyzer output files (all trades)
            stream_filters: Dictionary mapping stream_id to filter config:
                {
                    "ES1": {
                        "exclude_days_of_week": ["Wednesday"],
                        "exclude_days_of_month": [4, 16, 30],
                        "exclude_times": ["07:30", "08:00"]
                    },
                    ...
                }
            sequencer_runs_dir: Deprecated alias for analyzer_runs_dir (for backward compatibility)
        """
        # Handle deprecated sequencer_runs_dir parameter
        if sequencer_runs_dir is not None:
            analyzer_runs_dir = sequencer_runs_dir
        
        self.analyzer_runs_dir = Path(analyzer_runs_dir)
        self.master_df: Optional[pd.DataFrame] = None
        
        # Auto-discover streams by scanning analyzer_runs directory
        self.streams = self._discover_streams()
        
        # Day-of-month blocked days for "2" streams (default)
        self.dom_blocked_days = {4, 16, 30}
        
        # Per-stream filters
        self.stream_filters = stream_filters or {}
        
        # Initialize default filters for each stream if not provided
        for stream in self.streams:
            if stream not in self.stream_filters:
                self.stream_filters[stream] = {
                    "exclude_days_of_week": [],
                    "exclude_days_of_month": [],
                    "exclude_times": []
                }
    
    def _discover_streams(self) -> List[str]:
        """
        Auto-discover streams by scanning analyzer_runs directory.
        Looks for subdirectories matching stream patterns (ES1, ES2, GC1, etc.)
        
        Returns:
            List of stream IDs found
        """
        streams = []
        if not self.analyzer_runs_dir.exists():
            logger.warning(f"Analyzer runs directory not found: {self.analyzer_runs_dir}")
            return streams
        
        import re
        # Pattern: ES1, GC2, CL1, etc. (2 letters + 1 or 2)
        stream_pattern = re.compile(r'^([A-Z]{2})([12])$')
        
        for item in self.analyzer_runs_dir.iterdir():
            if not item.is_dir():
                continue
            
            # Check if directory name matches stream pattern
            match = stream_pattern.match(item.name)
            if match:
                stream_id = item.name  # e.g., "ES1", "GC2"
                streams.append(stream_id)
        
        streams.sort()  # Sort alphabetically for consistency
        logger.info(f"Discovered {len(streams)} streams: {streams}")
        return streams
        
    def load_all_streams(self, start_date: Optional[str] = None, 
                         end_date: Optional[str] = None,
                         specific_date: Optional[str] = None,
                         wait_for_streams: bool = True,
                         max_retries: int = 3,
                         retry_delay_seconds: int = 2) -> pd.DataFrame:
        """
        Load all trades from analyzer_runs and apply sequencer logic to select chosen trades.
        Works like the sequencer: reads from analyzer_runs, applies time change logic,
        and selects one trade per day per stream.
        
        Args:
            start_date: Start date for backtest period (YYYY-MM-DD) or None for all
            end_date: End date for backtest period (YYYY-MM-DD) or None for all
            specific_date: Specific date to load (YYYY-MM-DD) for "today" mode, or None
            wait_for_streams: If True, retry loading streams that fail (default: True)
            max_retries: Maximum number of retry attempts for failed streams (default: 3)
            retry_delay_seconds: Seconds to wait between retries (default: 2)
            
        Returns:
            Merged DataFrame with CHOSEN trades from all streams (one per day per stream)
        """
        logger.info("=" * 80)
        logger.info("MASTER MATRIX - Loading from analyzer_runs (applying sequencer logic)")
        logger.info("=" * 80)
        
        # Re-discover streams in case new ones were added
        self.streams = self._discover_streams()
        
        if not self.streams:
            logger.warning("No streams discovered! Check analyzer_runs directory.")
            return pd.DataFrame()
        
        all_trades = []
        streams_loaded = []
        streams_failed = {}  # Track failed streams and reasons
        import re
        import time
        
        # Function to load a single stream
        def load_stream(stream_id: str) -> bool:
            """Load a single stream. Returns True if successful, False otherwise."""
            stream_dir = self.analyzer_runs_dir / stream_id
            
            if not stream_dir.exists():
                logger.warning(f"Stream directory not found: {stream_dir}")
                streams_failed[stream_id] = f"Directory not found: {stream_dir}"
                return False
            
            # Load monthly consolidated files from analyzer_runs (all trades)
            # Pattern: <stream>_an_<year>_<month>.parquet in year subdirectories
            # Example: ES1_an_2024_11.parquet in ES1/2024/
            # Skip daily temp files in date folders (YYYY-MM-DD/)
            parquet_files = []
            
            # Look for year subdirectories (e.g., ES1/2024/, ES1/2025/)
            for year_dir in sorted(stream_dir.iterdir()):
                if not year_dir.is_dir():
                    continue
                
                # Check if it's a year directory (4 digits) or skip date folders (YYYY-MM-DD)
                year_dir_name = year_dir.name
                if len(year_dir_name) == 4 and year_dir_name.isdigit():
                    # This is a year directory - look for monthly consolidated files
                    # Pattern: <stream>_an_<year>_<month>.parquet (analyzer output)
                    monthly_files = sorted(year_dir.glob(f"{stream_id}_an_*.parquet"))
                    parquet_files.extend(monthly_files)
                # Skip date folders (YYYY-MM-DD format) - these contain daily temp files
            
            if not parquet_files:
                logger.warning(f"No monthly consolidated files found for stream: {stream_id} (checked {stream_dir})")
                streams_failed[stream_id] = f"No parquet files found in {stream_dir}"
                return False
            
            logger.info(f"Loading stream: {stream_id} ({len(parquet_files)} monthly files)")
            
            stream_trades = []
            for file_path in parquet_files:
                try:
                    df = pd.read_parquet(file_path)
                    
                    if df.empty:
                        continue
                    
                    # Extract stream info from filename if Stream column missing (same logic as sequencer)
                    if 'Stream' not in df.columns or df['Stream'].isna().all():
                        filename_match = re.match(r'^([A-Z]{2})([12])_', file_path.name)
                        if filename_match:
                            instrument = filename_match.group(1).upper()
                            stream_num = filename_match.group(2)
                            df['Stream'] = f"{instrument}{stream_num}"
                            logger.debug(f"  Extracted stream '{df['Stream'].iloc[0]}' from filename: {file_path.name}")
                    
                    # Ensure Stream column matches expected stream_id
                    if 'Stream' in df.columns:
                        df['Stream'] = stream_id
                    else:
                        df['Stream'] = stream_id
                    
                    # Filter by date if specified
                    if specific_date:
                        if 'Date' in df.columns:
                            df['Date'] = pd.to_datetime(df['Date'])
                            df = df[df['Date'].dt.date == pd.to_datetime(specific_date).date()]
                    elif start_date or end_date:
                        if 'Date' in df.columns:
                            df['Date'] = pd.to_datetime(df['Date'])
                            if start_date:
                                df = df[df['Date'] >= pd.to_datetime(start_date)]
                            if end_date:
                                df = df[df['Date'] <= pd.to_datetime(end_date)]
                    
                    if not df.empty:
                        stream_trades.append(df)
                        logger.debug(f"  Loaded {len(df)} trades from {file_path.name}")
                        
                except Exception as e:
                    logger.error(f"Error loading {file_path}: {e}")
                    continue
            
            if not stream_trades:
                streams_failed[stream_id] = "No valid trade data found in files"
                return False
            
            # Add all trades from this stream
            all_trades.extend(stream_trades)
            streams_loaded.append(stream_id)
            logger.info(f"✓ Successfully loaded stream: {stream_id} ({sum(len(df) for df in stream_trades)} trades)")
            return True
        
        # Load all streams with retry logic if enabled
        streams_to_load = self.streams.copy()
        retry_count = 0
        
        while streams_to_load and retry_count <= max_retries:
            if retry_count > 0:
                logger.info(f"Retry attempt {retry_count}/{max_retries} for failed streams...")
                time.sleep(retry_delay_seconds)
            
            remaining_streams = []
            for stream_id in streams_to_load:
                success = load_stream(stream_id)
                if not success:
                    remaining_streams.append(stream_id)
            
            streams_to_load = remaining_streams
            retry_count += 1
            
            # If wait_for_streams is False, stop after first attempt
            if not wait_for_streams:
                break
        
        # Report results
        if streams_failed:
            logger.warning("=" * 80)
            logger.warning(f"FAILED TO LOAD {len(streams_failed)} STREAM(S):")
            for stream_id, reason in streams_failed.items():
                logger.warning(f"  - {stream_id}: {reason}")
            logger.warning("=" * 80)
        
        if not all_trades:
            logger.warning("No trade data found!")
            return pd.DataFrame()
        
        # Merge all DataFrames
        logger.info(f"Merging {len(all_trades)} data files...")
        master_df = pd.concat(all_trades, ignore_index=True)
        
        logger.info(f"Total trades loaded (before sequencer logic): {len(master_df)}")
        logger.info(f"Streams loaded successfully: {streams_loaded} ({len(streams_loaded)}/{len(self.streams)})")
        if streams_failed:
            logger.warning(f"Streams that failed to load: {list(streams_failed.keys())}")
        
        # Apply sequencer logic to select one trade per day per stream
        # CRITICAL: Process ALL historical data to build accurate time slot histories (13-trade rolling window)
        # Each stream needs at least 13 days of historical data for accurate time slot selection
        # Only return most recent year for display (but histories are built from ALL data)
        logger.info("Applying sequencer time-change logic to select chosen trades...")
        logger.info("Processing ALL historical data to build accurate time slot histories (requires 13+ days for rolling window)")
        logger.info("Only returning most recent year for display (histories built from all historical data)")
        master_df = self._apply_sequencer_logic(master_df, display_year=None)  # None = auto-detect most recent year
        
        logger.info(f"Total chosen trades (after sequencer logic): {len(master_df)}")
        
        return master_df
    
    def _apply_sequencer_logic(self, df: pd.DataFrame, display_year: Optional[int] = None) -> pd.DataFrame:
        """
        Apply sequencer logic to select one trade per day per stream.
        Uses time change logic similar to sequential processor.
        
        Processes ALL historical data to build accurate time slot histories,
        but only returns trades from the most recent year (or all if display_year is None).
        
        Args:
            df: DataFrame with all trades from analyzer_runs
            display_year: If provided, only return trades from this year (for display).
                         All data is still processed to build accurate histories.
            
        Returns:
            DataFrame with one chosen trade per day per stream (filtered by display_year if provided)
        """
        if df.empty:
            return df
        
        # Determine the most recent year if not specified
        if display_year is None:
            df_copy = df.copy()
            df_copy['Date'] = pd.to_datetime(df_copy['Date'])
            if not df_copy.empty:
                max_date = df_copy['Date'].max()
                display_year = max_date.year
                logger.info(f"Auto-detected most recent year: {display_year}")
            else:
                display_year = None
        
        chosen_trades = []
        
        # CRITICAL: Verify stream_filters are set
        if not self.stream_filters:
            msg = "[DEBUG] WARNING: self.stream_filters is empty! Filters may not be applied!"
            logger.info(msg)
            logger.warning(msg)
        else:
            msg = f"[DEBUG] self.stream_filters contains: {list(self.stream_filters.keys())}"
            logger.info(msg)
            logger.info(msg)
        
        # Group by stream and date
        for stream_id in df['Stream'].unique():
            stream_df = df[df['Stream'] == stream_id].copy()
            stream_df['Date'] = pd.to_datetime(stream_df['Date'])
            stream_df = stream_df.sort_values('Date')
            
            # Get stream filters (same as sequencer)
            stream_filters = self.stream_filters.get(stream_id, {})
            exclude_times = [str(t) for t in stream_filters.get('exclude_times', [])]
            
            # DEBUG: Log filter application - FORCE to log file
            msg = f"[DEBUG] Stream {stream_id}: stream_filters dict = {stream_filters}"
            logger.info(msg)
            logger.info(msg)
            
            if exclude_times:
                msg = f"[DEBUG] Stream {stream_id}: Applying exclude_times filter: {exclude_times}"
                logger.info(msg)
                logger.info(msg)
            else:
                msg = f"[DEBUG] Stream {stream_id}: No exclude_times filter (empty or not set). Available filters: {list(stream_filters.keys())}"
                logger.info(msg)
                logger.info(msg)
            
            # Filter out excluded time slots (EXACTLY like sequencer - use string comparison)
            exclude_times_str = [str(t) for t in exclude_times] if exclude_times else []
            
            # Determine available times for this stream (convert to strings for comparison, like sequencer)
            all_times = sorted([str(t) for t in stream_df['Time'].unique()])
            # Filter out excluded times - these are completely removed from the stream (same as sequencer line 138)
            available_times = [t for t in all_times if t not in exclude_times_str]
            
            if not available_times:
                logger.warning(f"No available times for stream {stream_id} after filtering. Excluded: {exclude_times_str}")
                continue
            
            logger.info(f"Stream {stream_id}: Available times: {available_times}, Excluded: {exclude_times_str}")
            if exclude_times_str:
                msg = f"[DEBUG] Stream {stream_id}: Will completely remove trades at times: {exclude_times_str}"
                print(msg, file=sys.stderr, flush=True)
                logger.info(msg)
            
            # Session configuration (same as sequencer)
            SLOT_ENDS = {
                "S1": ["07:30", "08:00", "09:00"],
                "S2": ["09:30", "10:00", "10:30", "11:00"],
            }
            
            # Start with first available time
            current_time = available_times[0]
            current_session = self._get_session_for_time(current_time, SLOT_ENDS)
            
            # Track previous time to detect time changes
            previous_time = None
            
            # Track time slot histories, points, and rolling sums (same as sequencer)
            time_slot_histories = {time: [] for time in available_times}
            time_slot_points = {time: 0 for time in available_times}
            time_slot_rolling = {time: 0 for time in available_times}
            
            # Process day by day (process ALL days, filters applied later for display)
            for date in sorted(stream_df['Date'].unique()):
                date_df = stream_df[stream_df['Date'] == date].copy()
                
                # CRITICAL: Filter out trades at excluded times FIRST - before any processing (EXACTLY like sequencer)
                # Convert Time column to strings for comparison (like sequencer line 137-138)
                if exclude_times_str:
                    date_df['Time_str'] = date_df['Time'].apply(str)
                    # Remove ALL trades at excluded times - they should never be considered
                    before_count = len(date_df)
                    date_df = date_df[~date_df['Time_str'].isin(exclude_times_str)].copy()
                    after_count = len(date_df)
                    if before_count != after_count:
                        excluded_count = before_count - after_count
                        excluded_times_found = date_df[date_df['Time_str'].isin(exclude_times_str)]['Time'].unique().tolist() if len(date_df) > 0 else []
                        msg = f"[DEBUG] Stream {stream_id} {date}: Removed {excluded_count} trades at excluded times {exclude_times_str}"
                        print(msg, file=sys.stderr, flush=True)
                        logger.info(msg)
                    if 'Time_str' in date_df.columns:
                        date_df = date_df.drop(columns=['Time_str'])
                    if date_df.empty:
                        msg = f"[DEBUG] Stream {stream_id} {date}: No trades available after excluding times {exclude_times_str}"
                        logger.info(msg)
                        logger.info(msg)
                        continue
                    
                    # If current_time is excluded, switch to first available time in same session (EXACTLY like sequencer lines 544-556)
                    if str(current_time) in exclude_times_str:
                        session_times = SLOT_ENDS.get(current_session, [])
                        available_in_session = [t for t in available_times if t in session_times and str(t) not in exclude_times_str]
                        if available_in_session:
                            current_time = available_in_session[0]
                            current_session = self._get_session_for_time(current_time, SLOT_ENDS)
                            msg = f"[DEBUG] Stream {stream_id} {date}: Current time was excluded. Switched to: {current_time}"
                            logger.info(msg)
                            logger.info(msg)
                        else:
                            logger.warning(f"Stream {stream_id} {date}: All times in {current_session} are excluded. Skipping day.")
                            continue
                
                # Get trade for current time/session
                trade = date_df[
                    (date_df['Time'] == current_time) & 
                    (date_df['Session'] == current_session)
                ]
                
                if trade.empty:
                    # Try to find any trade for this date (but ensure it's not at an excluded time)
                    if len(date_df) > 0:
                        # Double-check: ensure we don't pick a trade at an excluded time (like sequencer)
                        if exclude_times_str:
                            date_df_check = date_df.copy()
                            date_df_check['Time_str'] = date_df_check['Time'].apply(str)
                            valid_trades = date_df_check[~date_df_check['Time_str'].isin(exclude_times_str)]
                            if 'Time_str' in valid_trades.columns:
                                valid_trades = valid_trades.drop(columns=['Time_str'])
                            trade = valid_trades.iloc[0:1] if len(valid_trades) > 0 else pd.DataFrame()
                        else:
                            trade = date_df.iloc[0:1] if len(date_df) > 0 else pd.DataFrame()
                    else:
                        trade = pd.DataFrame()
                
                if not trade.empty:
                    # Get trade row as a Series - we'll convert to dict later
                    trade_row = trade.iloc[0].copy()
                    
                    # CRITICAL SAFETY CHECK: Ensure trade is not at an excluded time (like sequencer)
                    if exclude_times_str and str(trade_row['Time']) in exclude_times_str:
                        msg = f"[DEBUG] Stream {stream_id} {date}: ERROR - Selected trade at excluded time {trade_row['Time']}! Skipping this day."
                        logger.info(msg)
                        logger.error(msg)
                        continue
                    
                    # Track the time we're using for THIS day (before any changes)
                    old_time_for_today = current_time
                    
                    # Track the time we're using for THIS day (before any changes)
                    old_time_for_today = current_time
                    
                    # Initialize time_changed_to to None for this day
                    # It will be set if we decide to change time for the next day (on a loss)
                    time_changed_to = None
                    
                    # Initialize next_day_time (will be updated if time should change)
                    next_day_time = current_time  # Default: stay on current time
                    
                    # Apply points-based time change logic (same as sequencer)
                    # IMPORTANT: Time changes only happen at day boundaries, not during the same day
                    # Process the trade for the current day with current_time, then decide if time should change for NEXT day
                    if trade_row['Result'] != 'NO DATA' and trade_row['Result'] != 'NoTrade':
                        # Calculate score for current time slot
                        current_score = self._calculate_time_score(trade_row['Result'])
                        
                        # Calculate rolling sum BEFORE adding today's result (for fair comparison)
                        current_sum_before = sum(time_slot_histories.get(current_time, []))
                        
                        # Add current trade to history
                        if current_time not in time_slot_histories:
                            time_slot_histories[current_time] = []
                        time_slot_histories[current_time].append(current_score)
                        
                        # Keep only last 13 trades per slot
                        if len(time_slot_histories[current_time]) > 13:
                            time_slot_histories[current_time] = time_slot_histories[current_time][-13:]
                        
                        # Calculate rolling sum AFTER adding today's result
                        current_sum_after = sum(time_slot_histories[current_time])
                        
                        # Update time slot tracking (same as sequencer)
                        time_slot_points[current_time] = current_score
                        time_slot_rolling[current_time] = current_sum_after
                        
                        # Check if we should change time for NEXT day (only on Loss, like sequencer)
                        # This decision is made AFTER processing today's trade, affecting tomorrow
                        if str(trade_row['Result']).upper() == 'LOSS':
                            # Find best other time slot in same session (only from available_times, which already excludes excluded times)
                            session_times = SLOT_ENDS.get(current_session, [])
                            other_times = [t for t in available_times if t != current_time and t in session_times]
                            
                            if other_times:
                                # Get today's results for other time slots (for fair comparison)
                                other_slots_with_sums = {}
                                for t in other_times:
                                    t_sum_before = sum(time_slot_histories.get(t, []))
                                    # Get today's result for this other slot
                                    t_trade = date_df[
                                        (date_df['Time'] == t) & 
                                        (date_df['Session'] == current_session)
                                    ]
                                    if not t_trade.empty:
                                        t_result = t_trade.iloc[0]['Result']
                                        t_score = self._calculate_time_score(t_result)
                                        t_sum_after = t_sum_before + t_score
                                    else:
                                        t_sum_after = t_sum_before
                                    other_slots_with_sums[t] = t_sum_after
                                
                                if other_slots_with_sums:
                                    # Find best other time slot
                                    best_other_time = max(other_slots_with_sums, key=other_slots_with_sums.get)
                                    best_other_sum = other_slots_with_sums[best_other_time]
                                    
                                    logger.debug(f"Stream {stream_id} {date}: Loss on {current_time} (sum={current_sum_after:.1f}). Other slots: {dict(other_slots_with_sums)}")
                                    
                                    # Rule: Switch if other slot has higher rolling sum (after adding today)
                                    # This change will apply to NEXT day, not today
                                    # CRITICAL: Ensure best_other_time is not excluded (should already be filtered, but double-check)
                                    if best_other_sum > current_sum_after and best_other_time in available_times:
                                        next_day_time = best_other_time
                                        # Set time_changed_to on the LOSS day (when decision is made), showing the new time for tomorrow
                                        time_changed_to = str(next_day_time)
                                        logger.info(f"Stream {stream_id} {date}: Time will change for NEXT day: {current_time} → {next_day_time} (sums: {current_sum_after:.1f} vs {best_other_sum:.1f})")
                                    else:
                                        if best_other_time not in available_times:
                                            logger.warning(f"Stream {stream_id} {date}: best_other_time {best_other_time} is excluded! Staying on {current_time}")
                                        logger.debug(f"Stream {stream_id} {date}: Staying on {current_time} for next day (current={current_sum_after:.1f} >= best_other={best_other_sum:.1f})")
                        
                        # AFTER time change logic: Update time slot histories for OTHER slots in same session only
                        # This ensures slots in the same session have their today's result tracked for future comparisons
                        # Only update slots that could potentially be compared (same session)
                        # Note: available_times already excludes excluded times, so we don't need to check again
                        session_times = SLOT_ENDS.get(current_session, [])
                        for time_slot in available_times:
                            # Skip excluded times (shouldn't be in available_times, but double-check - like sequencer)
                            if str(time_slot) in exclude_times_str:
                                continue
                            # Skip the slot we're currently using (already updated above)
                            if time_slot == current_time:
                                continue
                            # Only update slots in the same session (they're the ones we compare against)
                            if time_slot not in session_times:
                                continue
                            # Get today's result for this time slot
                            slot_trade = date_df[
                                (date_df['Time'] == time_slot) & 
                                (date_df['Session'] == current_session)
                            ]
                            if not slot_trade.empty:
                                slot_result = slot_trade.iloc[0]['Result']
                                slot_score = self._calculate_time_score(slot_result)
                                # Add to history for future comparisons
                                if time_slot not in time_slot_histories:
                                    time_slot_histories[time_slot] = []
                                time_slot_histories[time_slot].append(slot_score)
                                if len(time_slot_histories[time_slot]) > 13:
                                    time_slot_histories[time_slot] = time_slot_histories[time_slot][-13:]
                                # Update tracking
                                time_slot_points[time_slot] = slot_score
                                time_slot_rolling[time_slot] = sum(time_slot_histories[time_slot])
                    
                    # Add time slot rolling columns to trade row (like sequencer)
                    # Only include columns for available times (excluded times are completely removed)
                    for time_slot in available_times:
                        rolling_sum = sum(time_slot_histories.get(time_slot, []))
                        trade_row[f"{time_slot} Rolling"] = round(rolling_sum, 2)
                        points = time_slot_points.get(time_slot, 0)
                        trade_row[f"{time_slot} Points"] = points
                    
                    # Calculate SL (Stop Loss): 3x Target, capped at Range
                    sl_value = 0
                    if trade_row['Target'] != 'NO DATA' and isinstance(trade_row['Target'], (int, float)):
                        sl_value = 3 * trade_row['Target']
                        if trade_row['Range'] != 'NO DATA' and isinstance(trade_row['Range'], (int, float)) and trade_row['Range'] > 0:
                            sl_value = min(sl_value, trade_row['Range'])
                    trade_row['SL'] = sl_value
                    
                    # Only add to chosen_trades if it's from the display year (or all if display_year is None)
                    # We still process ALL days to build accurate time slot histories
                    trade_date = pd.to_datetime(trade_row['Date'])
                    if display_year is None or trade_date.year == display_year:
                        # Convert Series to dict - SL is already in trade_row
                        trade_dict = trade_row.to_dict()
                        trade_dict['SL'] = sl_value  # Ensure SL is set
                        
                        # Format Time Change column like sequencer: "old_time→new_time" when time changes
                        # The sequencer shows the change on the day it happens (when we switch from old_time to new_time)
                        time_change_display = ''
                        
                        # Check if time changed from previous day (this is the day the change takes effect)
                        if previous_time is not None and str(old_time_for_today) != str(previous_time):
                            # Time changed from previous day - show the change (like sequencer)
                            time_change_display = f"{previous_time}→{old_time_for_today}"
                            logger.debug(f"Stream {stream_id} {date}: Time changed from previous day: {previous_time} → {old_time_for_today}")
                        elif time_changed_to:
                            # Time will change for next day (decision made on loss day) - show it now
                            # This matches sequencer behavior: show the change on the day the decision is made
                            time_change_display = f"{old_time_for_today}→{next_day_time}"
                            logger.info(f"Stream {stream_id} {date}: Time will change for NEXT day: {old_time_for_today} → {next_day_time}")
                        
                        trade_dict['Time Change'] = time_change_display
                        chosen_trades.append(trade_dict)
                    
                    # Update current_time for next day if a time change was decided (like sequencer)
                    # This applies the time change decision made after processing today's trade
                    # CRITICAL: Ensure next_day_time is not excluded (safety check - like sequencer)
                    if next_day_time != current_time:
                        if str(next_day_time) in exclude_times_str:
                            msg = f"[DEBUG] Stream {stream_id} {date}: ERROR - next_day_time {next_day_time} is excluded! Staying on {current_time}"
                            logger.info(msg)
                            logger.error(msg)
                            next_day_time = current_time  # Don't change to excluded time
                        else:
                            current_time = next_day_time
                            current_session = self._get_session_for_time(current_time, SLOT_ENDS)
                            logger.debug(f"Stream {stream_id}: Updated current_time to {current_time} for next day")
                    
                    # Update previous_time for next iteration
                    previous_time = current_time
        
        if not chosen_trades:
            return pd.DataFrame()
        
        result_df = pd.DataFrame(chosen_trades)
        
        # CRITICAL: Final cleanup - remove ANY remaining trades at excluded times (bulletproof check)
        # Collect all excluded times across all streams (using string comparison like sequencer)
        all_excluded_times = set()
        for stream_id, filters in self.stream_filters.items():
            exclude_times = [str(t) for t in filters.get('exclude_times', [])]
            all_excluded_times.update(exclude_times)
            
            # Remove trades at excluded times for this stream (using string comparison like sequencer)
            if exclude_times:
                stream_mask = result_df['Stream'] == stream_id
                result_df_check = result_df.copy()
                result_df_check['Time_str'] = result_df_check['Time'].apply(str)
                time_mask = result_df_check['Time_str'].isin(exclude_times)
                rows_to_remove = stream_mask & time_mask
                
                if rows_to_remove.any():
                    removed_count = rows_to_remove.sum()
                    msg = f"[DEBUG] FINAL CLEANUP: Removing {removed_count} trades at excluded times for stream {stream_id}: {exclude_times}"
                    logger.info(msg)
                    logger.warning(msg)
                    excluded_trades = result_df[rows_to_remove]
                    if len(excluded_trades) > 0:
                        msg = f"[DEBUG] Excluded trades being removed:\n{excluded_trades[['Date', 'Time', 'Result']].to_string()}"
                        logger.info(msg)
                        logger.info(msg)
                    result_df = result_df[~rows_to_remove].copy()
                
                if 'Time_str' in result_df.columns:
                    result_df = result_df.drop(columns=['Time_str'])
        
        # Remove rolling sum columns for ALL excluded times (globally, to keep schema consistent)
        cols_removed = []
        for excluded_time in all_excluded_times:
            rolling_col = f"{excluded_time} Rolling"
            points_col = f"{excluded_time} Points"
            if rolling_col in result_df.columns:
                result_df = result_df.drop(columns=[rolling_col])
                cols_removed.append(rolling_col)
                logger.debug(f"[DEBUG] Removed column: {rolling_col}")
            if points_col in result_df.columns:
                result_df = result_df.drop(columns=[points_col])
                cols_removed.append(points_col)
                logger.debug(f"[DEBUG] Removed column: {points_col}")
        if cols_removed:
            logger.info(f"[DEBUG] Removed {len(cols_removed)} rolling sum columns for excluded times: {cols_removed}")
            msg = f"[DEBUG] Removed {len(cols_removed)} rolling sum columns for excluded times: {cols_removed}"
            logger.info(msg)
            logger.info(msg)
        
        # Ensure SL column exists (should already be there from calculation above)
        if 'SL' not in result_df.columns:
            logger.warning("SL column missing, adding with 0 values")
            result_df['SL'] = 0
        
        return result_df.reset_index(drop=True)
    
    def _normalize_time(self, time_str: str) -> str:
        """Normalize time format to HH:MM (e.g., "7:30" -> "07:30")."""
        if not time_str:
            return str(time_str)
        time_str = str(time_str).strip()
        parts = time_str.split(':')
        if len(parts) == 2:
            hours = parts[0].zfill(2)  # Pad with leading zero if needed
            minutes = parts[1].zfill(2)
            return f"{hours}:{minutes}"
        return time_str
    
    def _get_session_for_time(self, time: str, slot_ends: Dict) -> str:
        """Get session (S1 or S2) for a given time."""
        normalized_time = self._normalize_time(time)
        for session, times in slot_ends.items():
            normalized_times = [self._normalize_time(t) for t in times]
            if normalized_time in normalized_times:
                return session
        return "S1"  # Default
    
    
    def _calculate_time_score(self, result: str) -> int:
        """Calculate score for time change logic (same as sequencer - points-based system)."""
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
    
    def normalize_schema(self, df: pd.DataFrame) -> pd.DataFrame:
        """
        Normalize fields so every stream has the same schema.
        Adds missing columns with default values.
        
        Args:
            df: Input DataFrame
            
        Returns:
            Normalized DataFrame with consistent schema
        """
        logger.info("Normalizing schema...")
        
        # Required columns from analyzer output
        required_columns = {
            'Date': 'object',
            'Time': 'object',
            'Target': 'float64',
            'Peak': 'float64',
            'Direction': 'object',
            'Result': 'object',
            'Range': 'float64',
            'Stream': 'object',
            'Instrument': 'object',
            'Session': 'object',
            'Profit': 'float64',
            'SL': 'float64',  # Stop Loss - calculated by sequencer logic
        }
        
        # Optional columns (may not exist in all files)
        optional_columns = {
            'scf_s1': 'float64',
            'scf_s2': 'float64',
            'onr': 'float64',
            'onr_high': 'float64',
            'onr_low': 'float64',
        }
        
        # Ensure Date is datetime
        if 'Date' in df.columns:
            if df['Date'].dtype == 'object':
                df['Date'] = pd.to_datetime(df['Date'], errors='coerce')
            elif not pd.api.types.is_datetime64_any_dtype(df['Date']):
                df['Date'] = pd.to_datetime(df['Date'], errors='coerce')
        
        # Add missing required columns
        for col, dtype in required_columns.items():
            if col not in df.columns:
                if col == 'Date':
                    df[col] = pd.NaT
                elif dtype == 'float64':
                    df[col] = np.nan
                elif dtype == 'object':
                    df[col] = ''
                else:
                    df[col] = None
        
        # Add missing optional columns with NaN
        for col, dtype in optional_columns.items():
            if col not in df.columns:
                df[col] = np.nan
        
        # Create derived columns that may not exist
        # entry_time, exit_time (using Time as entry_time, exit_time would be calculated)
        if 'entry_time' not in df.columns:
            df['entry_time'] = df['Time']
        
        if 'exit_time' not in df.columns:
            # For now, set exit_time same as entry_time (would need actual exit logic)
            df['exit_time'] = df['Time']
        
        # entry_price, exit_price (not in analyzer output, create placeholder)
        if 'entry_price' not in df.columns:
            df['entry_price'] = np.nan
        
        if 'exit_price' not in df.columns:
            df['exit_price'] = np.nan
        
        # R (Risk-Reward ratio) - calculate from Profit/Target
        if 'R' not in df.columns:
            df['R'] = df.apply(
                lambda row: row['Profit'] / row['Target'] if pd.notna(row['Target']) and row['Target'] != 0 else np.nan,
                axis=1
            )
        
        # pnl (same as Profit for now)
        if 'pnl' not in df.columns:
            df['pnl'] = df['Profit']
        
        # rs_value (Rolling Sum value - would need to calculate from sequential processor)
        if 'rs_value' not in df.columns:
            df['rs_value'] = np.nan
        
        # selected_time (same as Time for now)
        if 'selected_time' not in df.columns:
            df['selected_time'] = df['Time']
        
        # time_bucket (same as Time for now)
        if 'time_bucket' not in df.columns:
            df['time_bucket'] = df['Time']
        
        # trade_date (same as Date)
        if 'trade_date' not in df.columns:
            if 'Date' in df.columns:
                df['trade_date'] = pd.to_datetime(df['Date']).dt.date
            else:
                df['trade_date'] = None
        
        logger.info(f"Schema normalized. Columns: {list(df.columns)}")
        
        return df
    
    def add_global_columns(self, df: pd.DataFrame) -> pd.DataFrame:
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
            
        Returns:
            DataFrame with global columns added
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
        df['session_index'] = df['Session'].apply(
            lambda x: 1 if str(x).upper() == 'S1' else 2 if str(x).upper() == 'S2' else None
        )
        
        # is_two_stream (true for *2 streams)
        df['is_two_stream'] = df['Stream'].str.endswith('2')
        
        # dom_blocked (true if day is 4/16/30 and stream is a "2")
        df['dom_blocked'] = (
            df['is_two_stream'] & 
            df['day_of_month'].isin(self.dom_blocked_days)
        )
        
        # Initialize filter reasons and final_allowed
        df['filter_reasons'] = ''
        df['final_allowed'] = True
        
        # Apply per-stream filters
        for stream_id, filters in self.stream_filters.items():
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
            if filters.get('exclude_times'):
                exclude_times = [str(t) for t in filters['exclude_times']]
                time_mask = stream_mask & df['Time'].isin(exclude_times)
                df.loc[time_mask, 'final_allowed'] = False
                df.loc[time_mask, 'filter_reasons'] = df.loc[time_mask, 'filter_reasons'].apply(
                    lambda x: f"{x}, " if x else ""
                ) + f"time_filter({','.join(exclude_times)})"
        
        # Apply default filters to final_allowed
        # NOTE: Automatic filters removed - only user-defined filters apply now
        # If you want DOM blocking or SCF filtering, add them via stream_filters
        
        # 1. dom_blocked filter (for *2 streams) - REMOVED (only apply if user sets it)
        # df.loc[df['dom_blocked'], 'final_allowed'] = False
        
        # 2. SCF filters - REMOVED (only apply if user sets it)
        # scf_threshold = 0.5
        # s1_blocked = (df['Session'] == 'S1') & (df['scf_s1'] >= scf_threshold)
        # s2_blocked = (df['Session'] == 'S2') & (df['scf_s2'] >= scf_threshold)
        # df.loc[s1_blocked | s2_blocked, 'final_allowed'] = False
        
        # Clean up filter_reasons (remove leading comma/space)
        df['filter_reasons'] = df['filter_reasons'].str.strip().str.rstrip(',')
        
        logger.info(f"Global columns added. Final allowed trades: {df['final_allowed'].sum()} / {len(df)}")
        
        return df
    
    def _log_summary_stats(self, df: pd.DataFrame) -> Dict:
        """
        Calculate and log summary statistics similar to sequential processor.
        
        Args:
            df: Master matrix DataFrame
            
        Returns:
            Dictionary with summary statistics
        """
        if df.empty:
            logger.warning("No data for summary statistics")
            return {}
        
        # Overall stats
        total_trades = len(df)
        wins = len(df[df['Result'] == 'Win'])
        losses = len(df[df['Result'] == 'Loss'])
        break_even = len(df[df['Result'] == 'BE'])
        no_trade = len(df[df['Result'] == 'NoTrade'])
        
        # Win rate (excludes BE trades, only wins vs losses)
        win_loss_trades = wins + losses
        win_rate = (wins / win_loss_trades * 100) if win_loss_trades > 0 else 0
        
        # Profit stats
        total_profit = df['Profit'].sum()
        avg_profit = df['Profit'].mean()
        
        # Risk-Reward ratio
        winning_trades = df[df['Result'] == 'Win']
        losing_trades = df[df['Result'] == 'Loss']
        avg_win = winning_trades['Profit'].mean() if len(winning_trades) > 0 else 0
        avg_loss = abs(losing_trades['Profit'].mean()) if len(losing_trades) > 0 else 0
        rr_ratio = avg_win / avg_loss if avg_loss > 0 else float('inf') if avg_win > 0 else 0
        
        # Filtered trades stats
        allowed_trades = df['final_allowed'].sum()
        blocked_trades = total_trades - allowed_trades
        
        # Per-stream stats
        stream_stats = {}
        for stream in sorted(df['Stream'].unique()):
            stream_df = df[df['Stream'] == stream]
            stream_wins = len(stream_df[stream_df['Result'] == 'Win'])
            stream_losses = len(stream_df[stream_df['Result'] == 'Loss'])
            stream_win_loss = stream_wins + stream_losses
            stream_win_rate = (stream_wins / stream_win_loss * 100) if stream_win_loss > 0 else 0
            stream_profit = stream_df['Profit'].sum()
            stream_allowed = stream_df['final_allowed'].sum()
            
            stream_stats[stream] = {
                'trades': len(stream_df),
                'wins': stream_wins,
                'losses': stream_losses,
                'win_rate': round(stream_win_rate, 1),
                'profit': round(stream_profit, 2),
                'allowed': int(stream_allowed)
            }
        
        # Log summary
        logger.info("=" * 80)
        logger.info("MASTER MATRIX SUMMARY STATISTICS")
        logger.info("=" * 80)
        logger.info(f"Total Trades: {total_trades}")
        logger.info(f"  Wins: {wins} | Losses: {losses} | Break-Even: {break_even} | No Trade: {no_trade}")
        logger.info(f"Win Rate: {win_rate:.1f}% (excluding BE)")
        logger.info(f"Total Profit: {total_profit:.2f}")
        logger.info(f"Average Profit per Trade: {avg_profit:.2f}")
        logger.info(f"Risk-Reward Ratio: {rr_ratio:.2f} (Avg Win: {avg_win:.1f} / Avg Loss: {avg_loss:.1f})")
        logger.info(f"Allowed Trades: {int(allowed_trades)} | Blocked Trades: {int(blocked_trades)}")
        logger.info("")
        logger.info("Per-Stream Statistics:")
        for stream, stats in stream_stats.items():
            logger.info(f"  {stream}: {stats['trades']} trades | "
                       f"Win Rate: {stats['win_rate']:.1f}% | "
                       f"Profit: {stats['profit']:.2f} | "
                       f"Allowed: {stats['allowed']}")
        logger.info("=" * 80)
        
        return {
            'total_trades': total_trades,
            'wins': wins,
            'losses': losses,
            'break_even': break_even,
            'no_trade': no_trade,
            'win_rate': round(win_rate, 1),
            'total_profit': round(total_profit, 2),
            'avg_profit': round(avg_profit, 2),
            'rr_ratio': round(rr_ratio, 2),
            'avg_win': round(avg_win, 2),
            'avg_loss': round(avg_loss, 2),
            'allowed_trades': int(allowed_trades),
            'blocked_trades': int(blocked_trades),
            'stream_stats': stream_stats
        }
    
    def build_master_matrix(self, start_date: Optional[str] = None,
                           end_date: Optional[str] = None,
                           specific_date: Optional[str] = None,
                           output_dir: str = "data/master_matrix",
                           stream_filters: Optional[Dict[str, Dict]] = None,
                           analyzer_runs_dir: Optional[str] = None,
                           streams: Optional[List[str]] = None) -> pd.DataFrame:
        """
        Build the master matrix by loading, normalizing, and merging all streams.
        Works like sequencer: reads from analyzer_runs and applies time change logic
        to select chosen trades (one per day per stream).
        
        Args:
            start_date: Start date for backtest period (YYYY-MM-DD) or None for all
            end_date: End date for backtest period (YYYY-MM-DD) or None for all
            specific_date: Specific date to load (YYYY-MM-DD) for "today" mode, or None
            output_dir: Directory to save master matrix files
            stream_filters: Per-stream filter configuration
            analyzer_runs_dir: Override analyzer runs directory (optional)
            
        Returns:
            Master matrix DataFrame sorted by trade_date, entry_time, symbol, stream_id
        """
        # Force output to stderr immediately AND log file
        import sys
        debug_start = "=" * 80 + "\nMASTER MATRIX: build_master_matrix() called\n"
        debug_start += f"Streams: {streams}\n"
        debug_start += f"Output dir: {output_dir}\n"
        debug_start += f"Stream filters: {stream_filters}\n"
        debug_start += "=" * 80 + "\n"
        
        logger.info("=" * 80)
        logger.info("MASTER MATRIX: build_master_matrix() called")
        logger.info(f"Streams: {streams}")
        logger.info(f"Output dir: {output_dir}")
        logger.info(f"Stream filters: {stream_filters}")
        logger.info("=" * 80)
        
        logger.info("=" * 80)
        logger.info("BUILDING MASTER MATRIX (Applying Sequencer Logic)")
        logger.info(f"Streams: {streams}")
        logger.info(f"Output dir: {output_dir}")
        logger.info("=" * 80)
        
        # Override analyzer_runs_dir if provided
        if analyzer_runs_dir:
            self.analyzer_runs_dir = Path(analyzer_runs_dir)
            # Re-discover streams after changing directory
            self.streams = self._discover_streams()
        
        # CRITICAL: Update stream filters BEFORE loading data
        # load_all_streams calls _apply_sequencer_logic which needs these filters
        # ALWAYS update filters, even if stream_filters is None (use existing self.stream_filters)
        if stream_filters:
            msg = f"[DEBUG] Received stream_filters parameter: {stream_filters}"
            logger.info(msg)
            logger.info(msg)
            # Ensure all exclude_times are strings (like sequencer expects)
            normalized_filters = {}
            for stream_id, filters in stream_filters.items():
                normalized_filters[stream_id] = {
                    "exclude_days_of_week": filters.get('exclude_days_of_week', []),
                    "exclude_days_of_month": filters.get('exclude_days_of_month', []),
                    "exclude_times": [str(t) for t in filters.get('exclude_times', [])]  # Ensure strings
                }
            self.stream_filters = normalized_filters
        else:
            msg = f"[DEBUG] No stream_filters parameter provided, using existing: {self.stream_filters}"
            print(msg, file=sys.stderr, flush=True)
            logger.info(msg)
        
        # ALWAYS ensure all streams have filter entries (even if empty)
        for stream in self.streams:
            if stream not in self.stream_filters:
                self.stream_filters[stream] = {
                    "exclude_days_of_week": [],
                    "exclude_days_of_month": [],
                    "exclude_times": []
                }
        
            msg = f"[DEBUG] Stream filters set BEFORE load_all_streams: {list(self.stream_filters.keys())}"
            logger.info(msg)
            logger.info(msg)
            for stream_id, filters in self.stream_filters.items():
                exclude_times = filters.get('exclude_times', [])
                if exclude_times:
                    msg = f"[DEBUG]   {stream_id}: exclude_times = {exclude_times}"
                    logger.info(msg)
                    logger.info(msg)
                else:
                    msg = f"[DEBUG]   {stream_id}: exclude_times = [] (no filter)"
                    logger.info(msg)
                    logger.info(msg)
        
        # If rebuilding specific streams, we need to merge with existing master matrix
        # Otherwise, rebuild everything from scratch
        if streams:
            logger.info(f"Rebuilding only streams: {streams}")
            # Load existing master matrix if it exists
            existing_df = pd.DataFrame()
            output_path = Path(output_dir)
            if output_path.exists():
                # Find the most recent master matrix file
                parquet_files = sorted(output_path.glob("master_matrix_*.parquet"), reverse=True)
                if parquet_files:
                    try:
                        existing_df = pd.read_parquet(parquet_files[0])
                        logger.info(f"Loaded existing master matrix: {len(existing_df)} trades")
                        # Remove old data for streams being rebuilt
                        existing_df = existing_df[~existing_df['Stream'].isin(streams)]
                        logger.info(f"After removing rebuilt streams: {len(existing_df)} trades")
                    except Exception as e:
                        logger.warning(f"Could not load existing master matrix: {e}")
            
            # Update stream filters BEFORE loading (critical for time filtering)
            if stream_filters:
                self.stream_filters = stream_filters
                # Initialize defaults for streams not in provided filters
                for stream in self.streams:
                    if stream not in self.stream_filters:
                        self.stream_filters[stream] = {
                            "exclude_days_of_week": [],
                            "exclude_days_of_month": [],
                            "exclude_times": []
                        }
                logger.info(f"Stream filters updated (partial rebuild): {list(self.stream_filters.keys())}")
                for stream_id, filters in self.stream_filters.items():
                    if filters.get('exclude_times'):
                        logger.info(f"  {stream_id}: exclude_times = {filters.get('exclude_times')}")
            
            # Only process requested streams
            original_streams = self.streams.copy()
            self.streams = [s for s in self.streams if s in streams]
            
            # Load only the requested streams
            # IMPORTANT: Always load ALL historical data (ignore date filters) to build accurate time slot histories
            # The display_year filtering happens in _apply_sequencer_logic
            # NOTE: stream_filters are already set above, so they will be used in _apply_sequencer_logic
            new_df = self.load_all_streams(start_date=None, end_date=None, specific_date=None)
            
            # Restore original streams list
            self.streams = original_streams
            
            # Merge with existing data
            if not existing_df.empty:
                df = pd.concat([existing_df, new_df], ignore_index=True)
                logger.info(f"Merged with existing data: {len(df)} total trades")
            else:
                df = new_df
        else:
            # Rebuild everything from scratch
            # IMPORTANT: Always load ALL historical data (ignore date filters) to build accurate time slot histories
            # The display_year filtering happens in _apply_sequencer_logic
            # NOTE: stream_filters are already set above (before this if/else), so they will be used in _apply_sequencer_logic
            df = self.load_all_streams(start_date=None, end_date=None, specific_date=None)
        
        if df.empty:
            logger.warning("No data loaded!")
            return pd.DataFrame()
        
        # NOTE: stream_filters are already set above (before load_all_streams)
        # They were used in _apply_sequencer_logic during load_all_streams
        
        # Normalize schema
        df = self.normalize_schema(df)
        
        # Add global columns (applies filters)
        df = self.add_global_columns(df)
        
        # SL is already calculated in _apply_sequencer_logic, just ensure it exists
        if 'SL' not in df.columns:
            df['SL'] = 0
        
        # Ensure Time Change column exists (should already be there from _apply_sequencer_logic)
        if 'Time Change' not in df.columns:
            logger.warning("Time Change column missing, adding with empty values")
            df['Time Change'] = ''
        
        # Ensure Time Change column exists (should already be there from _apply_sequencer_logic)
        if 'Time Change' not in df.columns:
            logger.warning("Time Change column missing, adding with empty values")
            df['Time Change'] = ''
        
        # Sort by: trade_date, then entry_time, then symbol, then stream_id
        df = df.sort_values(
            by=['trade_date', 'entry_time', 'Instrument', 'Stream'],
            ascending=[True, True, True, True]
        ).reset_index(drop=True)
        
        # Update global_trade_id after sorting
        df['global_trade_id'] = range(1, len(df) + 1)
        
        self.master_df = df
        
        logger.info(f"Master matrix built: {len(df)} trades")
        logger.info(f"Date range: {df['trade_date'].min()} to {df['trade_date'].max()}")
        logger.info(f"Streams: {sorted(df['Stream'].unique())}")
        logger.info(f"Instruments: {sorted(df['Instrument'].unique())}")
        
        # Calculate and log summary statistics
        self._log_summary_stats(df)
        
        # Save master matrix
        output_path = Path(output_dir)
        output_path.mkdir(parents=True, exist_ok=True)
        
        timestamp = datetime.now().strftime('%Y%m%d_%H%M%S')
        
        if specific_date:
            # Save as "today" file
            parquet_file = output_path / f"master_matrix_today_{specific_date.replace('-', '')}.parquet"
            json_file = output_path / f"master_matrix_today_{specific_date.replace('-', '')}.json"
        else:
            # Save as full backtest file
            parquet_file = output_path / f"master_matrix_{timestamp}.parquet"
            json_file = output_path / f"master_matrix_{timestamp}.json"
        
        # SL should already be calculated in _apply_sequencer_logic
        if 'SL' not in df.columns:
            df['SL'] = 0
        
        # Save as Parquet
        df.to_parquet(parquet_file, index=False, compression='snappy')
        logger.info(f"Saved: {parquet_file} (columns: {list(df.columns)})")
        
        # Save as JSON (for easy inspection)
        df.to_json(json_file, orient='records', date_format='iso', indent=2)
        logger.info(f"Saved: {json_file}")
        
        return df
    
    def get_master_matrix(self) -> pd.DataFrame:
        """
        Get the current master matrix DataFrame.
        
        Returns:
            Master matrix DataFrame or empty DataFrame if not built yet
        """
        if self.master_df is None:
            logger.warning("Master matrix not built yet. Call build_master_matrix() first.")
            return pd.DataFrame()
        
        return self.master_df.copy()


def main():
    """Main function for command-line usage"""
    import argparse
    
    parser = argparse.ArgumentParser(description='Build Master Matrix from all streams')
    parser.add_argument('--start-date', type=str, help='Start date (YYYY-MM-DD)')
    parser.add_argument('--end-date', type=str, help='End date (YYYY-MM-DD)')
    parser.add_argument('--today', type=str, help='Specific date for today mode (YYYY-MM-DD)')
    parser.add_argument('--analyzer-runs-dir', type=str, default='data/analyzer_runs',
                       help='Directory containing analyzer output files (all trades)')
    parser.add_argument('--output-dir', type=str, default='data/master_matrix',
                       help='Output directory for master matrix files')
    
    args = parser.parse_args()
    
    matrix = MasterMatrix(analyzer_runs_dir=args.analyzer_runs_dir)
    master_df = matrix.build_master_matrix(
        start_date=args.start_date,
        end_date=args.end_date,
        specific_date=args.today,
        output_dir=args.output_dir,
        analyzer_runs_dir=args.analyzer_runs_dir
    )
    
    if not master_df.empty:
        # Summary stats are already logged by build_master_matrix
        print("\n" + "=" * 80)
        print("MASTER MATRIX SUMMARY")
        print("=" * 80)
        print(f"Total trades: {len(master_df)}")
        print(f"Date range: {master_df['trade_date'].min()} to {master_df['trade_date'].max()}")
        print(f"Streams: {sorted(master_df['Stream'].unique())}")
        print(f"Instruments: {sorted(master_df['Instrument'].unique())}")
        
        # Calculate and display stats
        stats = matrix._log_summary_stats(master_df)
        
        print(f"\nFirst 5 trades:")
        print(master_df[['global_trade_id', 'trade_date', 'entry_time', 'Instrument', 'Stream', 
                         'Session', 'Result', 'Profit', 'final_allowed']].head().to_string(index=False))


if __name__ == "__main__":
    main()

