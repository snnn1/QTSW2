#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Sequential Time Change Processor V2
Processes historical data line by line with real-time time change logic application
"""

import sys
import io
# Set UTF-8 encoding for stdout/stderr to handle Unicode characters
if sys.platform == 'win32':
    sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8', errors='replace')
    sys.stderr = io.TextIOWrapper(sys.stderr.buffer, encoding='utf-8', errors='replace')

import pandas as pd
import numpy as np
import os
from datetime import datetime, timedelta
from typing import Dict, List, Tuple, Optional

class SequentialProcessorV2:
    def __init__(self, data_file: str, start_time: str = "08:00", time_mode: str = "normal", exclude_days_of_week: list = None, exclude_days_of_month: list = None, exclude_times: list = None, loss_recovery_mode: bool = False, data_files: list = None, rolling_sum_threshold: float = None, enable_5_point_bypass: bool = False, bypass_point_threshold: float = 5.0):
        """Initialize with historical data file(s)"""
        # If multiple files provided, load and concatenate them
        if data_files and len(data_files) > 1:
            dataframes = []
            instruments_found = set()
            file_instrument_map = {}  # Track which instrument each file contains
            
            print(f"Loading {len(data_files)} files...")
            for file_path in data_files:
                df = pd.read_parquet(file_path)
                file_name = os.path.basename(file_path)
                
                # Detect instrument from this file
                file_instruments = set()
                if 'Instrument' in df.columns:
                    file_instruments_raw = df['Instrument'].unique()
                    file_instruments = {str(inst).upper() for inst in file_instruments_raw if pd.notna(inst)}
                    instruments_found.update(file_instruments)
                elif 'Stream' in df.columns:
                    # Try to detect from Stream column
                    streams = df['Stream'].unique()
                    for stream in streams:
                        if pd.notna(stream) and isinstance(stream, str):
                            if stream.startswith('ES'):
                                file_instruments.add('ES')
                                instruments_found.add('ES')
                            elif stream.startswith('CL'):
                                file_instruments.add('CL')
                                instruments_found.add('CL')
                            elif stream.startswith('NQ'):
                                file_instruments.add('NQ')
                                instruments_found.add('NQ')
                            elif stream.startswith('NG'):
                                file_instruments.add('NG')
                                instruments_found.add('NG')
                
                file_instrument_map[file_name] = sorted(file_instruments) if file_instruments else ["UNKNOWN"]
                dataframes.append(df)
                print(f"  - {file_name}: {sorted(file_instruments) if file_instruments else ['UNKNOWN']}")
            
            # Validate all files contain the same instrument(s)
            if len(instruments_found) > 1:
                # Find the most common instrument (expected instrument)
                instrument_counts = {}
                for file_name, insts in file_instrument_map.items():
                    for inst in insts:
                        instrument_counts[inst] = instrument_counts.get(inst, 0) + 1
                
                expected_instrument = max(instrument_counts, key=instrument_counts.get)
                mismatched_files = [fname for fname, insts in file_instrument_map.items() 
                                   if expected_instrument not in insts]
                
                error_msg = f"\nERROR: Mixed instruments detected in files!\n"
                error_msg += f"Expected instrument: {expected_instrument} (found in {instrument_counts[expected_instrument]} files)\n"
                error_msg += f"Found instruments: {sorted(instruments_found)}\n\n"
                
                if mismatched_files:
                    error_msg += f"Files with mismatched instruments (will be skipped):\n"
                    for file_name in mismatched_files:
                        error_msg += f"  - {file_name}: {file_instrument_map[file_name]}\n"
                    error_msg += f"\nThese files contain different instrument data and will be excluded from processing.\n"
                    error_msg += f"Please verify these files are correct or remove them from the selection.\n\n"
                
                error_msg += "Files and their instruments:\n"
                for file_name, insts in file_instrument_map.items():
                    marker = " [SKIPPED]" if file_name in mismatched_files else ""
                    error_msg += f"  - {file_name}: {insts}{marker}\n"
                
                # Filter out mismatched files and continue with matching ones
                print(error_msg)
                print(f"\nFiltering out {len(mismatched_files)} mismatched file(s) and continuing with {len(data_files) - len(mismatched_files)} file(s)...")
                
                # Rebuild dataframes list without mismatched files
                filtered_dataframes = []
                filtered_file_paths = []
                for i, file_path in enumerate(data_files):
                    file_name = os.path.basename(file_path)
                    if file_name not in mismatched_files:
                        filtered_dataframes.append(dataframes[i])
                        filtered_file_paths.append(file_path)
                
                if not filtered_dataframes:
                    raise ValueError(f"ERROR: All files were filtered out due to instrument mismatches! "
                                   f"Please check your file selection.")
                
                dataframes = filtered_dataframes
                instruments_found = {expected_instrument}  # Reset to only expected instrument
                print(f"Continuing with {len(filtered_dataframes)} file(s) containing {expected_instrument} data.")
            
            self.data = pd.concat(dataframes, ignore_index=True)
            detected_instrument = list(instruments_found)[0] if instruments_found else "UNKNOWN"
            files_loaded = len(dataframes)  # Use filtered count if filtering occurred
            print(f"\nSuccessfully loaded {files_loaded} file(s) and combined into {len(self.data)} rows")
            print(f"Detected instrument in all files: {detected_instrument}")
        else:
            # Single file (backward compatible)
            self.data = pd.read_parquet(data_file)
        
        self.data['Date'] = pd.to_datetime(self.data['Date'])
        # Sort by date to ensure chronological order
        self.data = self.data.sort_values('Date').reset_index(drop=True)
        
        # Validate start_time is available in data
        available_times = sorted([str(t) for t in self.data['Time'].unique()])
        if start_time not in available_times:
            raise ValueError(f"Start time '{start_time}' not found in data. Available times: {available_times}. Please select a different data file or start time.")
        
        # Store available times for dynamic initialization
        self.available_times = available_times
        
        # Filter out excluded time slots
        self.exclude_times = exclude_times or []
        if self.exclude_times:
            # Convert to strings for comparison
            exclude_times_str = [str(t) for t in self.exclude_times]
            self.available_times = [t for t in self.available_times if str(t) not in exclude_times_str]
            if not self.available_times:
                raise ValueError(f"All time slots are excluded. Cannot proceed with processing.")
            # If start_time is excluded, use first available time
            if start_time in exclude_times_str:
                start_time = self.available_times[0]
                print(f"Warning: Start time was excluded. Using first available time: {start_time}")
        
        # Auto-detect instrument from data file
        self.instrument = self._detect_instrument_from_data()
        print(f"Auto-detected instrument: {self.instrument}")
        
        # Session and slot configuration
        self.SLOT_ENDS = {
            "S1": ["07:30","08:00","09:00"],
            "S2": ["09:30","10:00","10:30","11:00"],
        }
        
        # Current state
        self.current_time = start_time  # Start with custom time
        self.current_session = self._get_session_for_time(start_time)  # Determine session based on time slot
        
        # Time change mode
        self.time_mode = time_mode  # "normal" mode only now
        self.exclude_days_of_week = exclude_days_of_week or []  # Days of week to exclude from revised calculations
        self.exclude_days_of_month = exclude_days_of_month or []  # Days of month to exclude from revised calculations
        self.loss_recovery_mode = loss_recovery_mode  # True = only count trades after wins (exclude trades following losses)
        
        # Track loss recovery mode state
        self.last_result_was_win = True  # Start assuming we can count trades
        
        # Rolling sum threshold for revised calculations
        self.rolling_sum_threshold = rolling_sum_threshold  # None = disabled, otherwise minimum rolling sum required
        self.skip_revised_calc_today = False  # Flag to skip revised profit/score calculation for today (set based on yesterday's rolling sums)
        
        # 5-point bypass rule
        self.enable_5_point_bypass = enable_5_point_bypass  # If enabled, switch to any time slot that is X+ points higher, bypassing all other rules
        self.bypass_point_threshold = bypass_point_threshold  # Minimum point difference required to trigger bypass (default: 5.0)
        
        # Data tracking
        self.processed_days = 0
        self.data_usage_count = {}  # Track how many times we've used each combination
        
        # Results
        self.results = []
        
        # Time slot tracking for dynamic columns - dynamically initialize based on available times
        self.time_slot_points = {time: 0 for time in available_times}
        self.time_slot_rolling = {time: 0 for time in available_times}
        self.time_slot_histories = {time: [] for time in available_times}
        
        print(f"Data loaded: {len(self.data)} rows")
        print(f"Date range: {self.data['Date'].min()} to {self.data['Date'].max()}")
        print(f"Available times: {sorted(self.data['Time'].unique())}")
        print(f"Available targets: {sorted(self.data['Target'].unique())}")
    
    def _detect_instrument_from_data(self) -> str:
        """Auto-detect instrument from data file by looking at available targets"""
        # Get unique targets from the data
        available_targets = sorted(self.data['Target'].unique())
        print(f"Available targets in data: {available_targets}")
        
        # Check if targets are decimals (< 10) - likely CL, NG, or other decimal-based instruments
        if len(available_targets) > 0:
            max_target = max(available_targets)
            min_target = min(available_targets)
            
            # CL detection: targets like 0.5, 0.75, 1.0, 1.25, etc.
            if min_target < 5 and max_target < 5:
                if 0.5 in available_targets or any(abs(t - 0.5) < 0.01 for t in available_targets):
                    return "CL"
            
            # NG detection: very small targets like 0.05, 0.075, 0.1, etc.
            if min_target < 0.5 and max_target < 0.5:
                if any(t < 0.5 for t in available_targets):
                    return "NG"
            
            # GC detection: targets like 5, 7.5, 10, etc.
            if min_target >= 5 and max_target <= 20:
                if 5 in available_targets or any(abs(t - 5) < 0.1 for t in available_targets):
                    return "GC"
        
        # Integer-based detection
        if 50 in available_targets and 75 in available_targets:
            return "NQ"  # NQ typically has 50, 75, 100, 125, etc.
        elif 10 in available_targets and 15 in available_targets:
            return "ES"  # ES typically has 10, 15, 20, 25, etc.
        elif 100 in available_targets and 150 in available_targets:
            return "YM"  # YM typically has 100, 150, 200, etc.
        else:
            # Default fallback - try to detect from filename or use ES
            print("Could not auto-detect instrument from targets, defaulting to ES")
            return "ES"
    
    def _get_session_for_time(self, time_str: str) -> str:
        """Get session (S1 or S2) for a given time slot"""
        for session, times in self.SLOT_ENDS.items():
            if time_str in times:
                return session
        # Default to S1 if not found (shouldn't happen with correct configuration)
        return "S1"
    
    def get_next_data(self, time: str, session: str) -> Optional[Dict]:
        """Get next day's data for time/session combination"""
        # Find the next day's data that matches the time/session
        # We need to look for data that comes after the last processed date
        
        if not hasattr(self, 'last_processed_date'):
            self.last_processed_date = None
        
        # Filter data for this combination (use any target since we don't change targets)
        filtered = self.data[
            (self.data['Time'] == time) & 
            (self.data['Session'] == session)
        ].sort_values('Date')
        
        if len(filtered) == 0:
            return {
                'Date': 'NO DATA',
                'Time': time,
                'Target': 'NO DATA',
                'Peak': 'NO DATA',
                'Direction': 'NO DATA',
                'Result': 'NO DATA',
                'Range': 'NO DATA',
                'Stream': 'NO DATA',
                'Instrument': 'NO DATA',
                'Session': session,
                'Profit': 'NO DATA',
                'Time Change': 'NO DATA'
            }
        
        # If we have a last processed date, find the next day's data
        if self.last_processed_date is not None:
            # Find data that comes after the last processed date
            next_data = filtered[filtered['Date'] > self.last_processed_date]
            if len(next_data) > 0:
                # Use the first occurrence after the last processed date
                selected_data = next_data.iloc[0]
            else:
                # No data after last processed date - return NO DATA
                return {
                    'Date': 'NO DATA',
                    'Time': time,
                    'Target': 'NO DATA',
                    'Peak': 'NO DATA',
                    'Direction': 'NO DATA',
                    'Result': 'NO DATA',
                    'Range': 'NO DATA',
                    'Stream': 'NO DATA',
                    'Instrument': 'NO DATA',
                    'Session': session,
                    'Profit': 'NO DATA',
                    'Time Change': 'NO DATA'
                }
        else:
            # First time, use the first available data
            selected_data = filtered.iloc[0]
        
        # Update last processed date
        self.last_processed_date = selected_data['Date']
        
        return selected_data.to_dict()
    
    def get_data_for_date_and_time(self, date, time: str, session: str) -> dict:
        """Get data for specific date, time, session (like main analyzer)"""
        # Filter data for the specific combination on the specific date
        # Use any target since we don't change targets
        
        filtered = self.data[
            (self.data['Date'] == date) &
            (self.data['Time'] == time) & 
            (self.data['Session'] == session)
        ].copy()
        
        if len(filtered) == 0:
            return {
                'Date': 'NO DATA',
                'Time': time,
                'Target': 'NO DATA',
                'Peak': 0,
                'Direction': 'N/A',
                'Result': 'NO DATA',
                'Session': session,
                'Profit': 0,
                'Time Change': ''
            }
        
        # Get the data for this date/time/target/session
        data_row = filtered.iloc[0]
        
        return {
            'Date': data_row['Date'],
            'Time': data_row['Time'],
            'Target': data_row['Target'],
            'Peak': data_row['Peak'],
            'Direction': data_row['Direction'],
            'Result': data_row['Result'],
            'Session': data_row['Session'],
            'Profit': data_row['Profit'],
            'Time Change': data_row.get('Time Change', ''),
        }
    
    def update_time_change(self, result: str) -> Tuple[str, str]:
        """Apply time change logic using 13-trade rolling points system"""
        if result == 'NO DATA':
            return self.current_time, "NO DATA - No time change"
        
        # Initialize time slot histories if not exists (should already be initialized in __init__)
        if not hasattr(self, 'time_slot_histories'):
            self.time_slot_histories = {time: [] for time in self.available_times}
        
        # Calculate score for current time slot
        current_score = self._calculate_time_score(result)
        
        # Calculate rolling sum BEFORE adding today's result (for fair comparison)
        current_sum_before = sum(self.time_slot_histories[self.current_time])
        
        # Add current trade to history
        self.time_slot_histories[self.current_time].append(current_score)
        
        # Keep only last 13 trades per slot
        if len(self.time_slot_histories[self.current_time]) > 13:
            self.time_slot_histories[self.current_time] = self.time_slot_histories[self.current_time][-13:]
        
        # Calculate rolling sum AFTER adding today's result
        current_sum_after = sum(self.time_slot_histories[self.current_time])
        
        # Find the best performing "other" time slot (highest rolling sum among non-current slots)
        # IMPORTANT: Only consider time slots in the SAME SESSION and NOT excluded
        current_session = self.current_session
        session_times = self.SLOT_ENDS.get(current_session, [])
        exclude_times_str = [str(t) for t in self.exclude_times] if self.exclude_times else []
        other_times = [t for t in self.available_times if t != self.current_time and t in session_times and str(t) not in exclude_times_str]
        
        # 5-POINT BYPASS RULE: Check if any time slot is 5+ points higher (bypasses all other rules)
        if self.enable_5_point_bypass and other_times:
            current_date = self.last_processed_date
            
            # Calculate rolling sums for all other time slots (including today's result)
            other_slots_with_sums = []
            for t in other_times:
                t_sum_before = sum(self.time_slot_histories.get(t, []))
                # Get today's result for this other slot
                t_session = self._get_session_for_time(t)
                t_data = self.get_data_for_date_and_time(current_date, t, t_session)
                if t_data and t_data['Date'] != 'NO DATA':
                    t_score = self._calculate_time_score(t_data['Result'])
                    t_sum_after = t_sum_before + t_score
                else:
                    t_sum_after = t_sum_before
                
                # Check if this slot is X+ points higher than current (where X is bypass_point_threshold)
                if t_sum_after >= current_sum_after + self.bypass_point_threshold:
                    other_slots_with_sums.append((t, t_sum_after))
            
            # If any slots are X+ points higher, switch to the highest one (or earliest if tied)
            if other_slots_with_sums:
                # Sort by sum (descending), then by time (ascending for earliest)
                other_slots_with_sums.sort(key=lambda x: (-x[1], x[0]))
                best_time, best_sum = other_slots_with_sums[0]
                
                self.current_time = best_time
                self.current_session = self._get_session_for_time(best_time)
                return self.current_time, f"Time change → {self.current_time} ({self.bypass_point_threshold:.1f}-point bypass: {best_sum:.2f} vs {current_sum_after:.2f}, bypasses all rules)"
        
        if not other_times:
            # Only one time slot available in this session, no time change possible
            return self.current_time, f"Stay {self.current_time} (only one time slot in {current_session})"
        
        # Get the best other time slot (using historical sums only, before adding today)
        other_time_sums = {t: sum(self.time_slot_histories.get(t, [])) for t in other_times}
        other_time = max(other_time_sums, key=other_time_sums.get)
        other_sum_before = other_time_sums[other_time]
        
        # For the best other time slot, get the SAME DATE data to calculate today's result
        current_date = self.last_processed_date
        other_session = self._get_session_for_time(other_time)
        other_data = self.get_data_for_date_and_time(current_date, other_time, other_session)
        
        if other_data and other_data['Date'] != 'NO DATA':
            # Use actual other time slot data for today
            other_result = other_data['Result']
            other_score = self._calculate_time_score(other_result)
            # Calculate other slot's sum AFTER adding today's result (for fair comparison)
            other_sum_after = other_sum_before + other_score
        else:
            # No data for other time slot today - use historical sum only
            other_score = 0
            other_sum_after = other_sum_before
        
        # Update time slot tracking for current time (AFTER adding today's result)
        self.time_slot_points[self.current_time] = current_score
        self.time_slot_rolling[self.current_time] = current_sum_after
        
        # Update tracking for all other time slots (using historical sums, today will be added in process_sequential)
        for t in other_times:
            self.time_slot_rolling[t] = other_time_sums[t]
        
        # Update the best other slot's points (but don't add to history yet - that happens in process_sequential)
        self.time_slot_points[other_time] = other_score
        
        # Use the "after" sums for comparison (both slots include today's result)
        current_sum = current_sum_after
        other_sum = other_sum_after
        
        # Build display string showing all other slots for clarity
        other_slots_display = []
        for t in sorted(other_times):
            t_sum_before = other_time_sums.get(t, 0)
            # Get today's result for this other slot
            t_data = self.get_data_for_date_and_time(current_date, t, self._get_session_for_time(t))
            if t_data and t_data['Date'] != 'NO DATA':
                t_score = self._calculate_time_score(t_data['Result'])
                t_sum_after = t_sum_before + t_score
            else:
                t_sum_after = t_sum_before
            other_slots_display.append(f"{t}={t_sum_after:.2f}")
        other_display = ", ".join(other_slots_display) if other_slots_display else "none"
        
        # Time change rules (only switch on Loss)
        if result.upper() == 'LOSS':
            # Rule 1: Switch if other slot has higher rolling sum
            if other_sum > current_sum:
                self.current_time = other_time
                self.current_session = self._get_session_for_time(other_time)  # Update session to match new time
                return self.current_time, f"Time change → {self.current_time} (other slot higher: {other_sum:.2f} vs {current_sum:.2f})"
            
            # Rule 2: Switch if other slot is ≥5 points higher
            if other_sum >= current_sum + 5:
                self.current_time = other_time
                self.current_session = self._get_session_for_time(other_time)  # Update session to match new time
                return self.current_time, f"Time change → {self.current_time} (other slot ≥5 higher: {other_sum:.2f} vs {current_sum:.2f})"
        
        return self.current_time, f"Stay {self.current_time} (current: {current_sum:.2f}, others: {other_display})"
    
    def _calculate_time_score(self, result: str) -> int:
        """Calculate score for time change logic (normal mode)"""
        result_upper = result.upper()
        if result_upper == "WIN":
            return 1
        elif result_upper == "LOSS":
            return -2
        elif result_upper in ["BE", "BREAK_EVEN", "BREAKEVEN"]:
            return 0
        elif result_upper in ["NOTRADE", "NO_TRADE"]:
            return 0
        elif result_upper == "TIME":
            return 0
        else:
            return 0
    
    def get_time_slot_columns(self) -> Dict[str, str]:
        """Get dynamic time slot columns for all available time slots"""
        columns = {}
        
        # Get all available time slots from data
        available_times = sorted(self.data['Time'].unique())
        
        for time_slot in available_times:
            time_str = str(time_slot)
            
            # Initialize if not exists
            if time_str not in self.time_slot_points:
                self.time_slot_points[time_str] = 0
            if time_str not in self.time_slot_rolling:
                self.time_slot_rolling[time_str] = 0
            if time_str not in self.time_slot_histories:
                self.time_slot_histories[time_str] = []
            
            # Calculate rolling sum for this time slot
            rolling_sum = sum(self.time_slot_histories[time_str])
            self.time_slot_rolling[time_str] = rolling_sum
            
            # Add columns
            columns[f"{time_str} Points"] = f"{self.time_slot_points[time_str]:.2f}"
            columns[f"{time_str} Rolling"] = f"{self.time_slot_rolling[time_str]:.2f}"
        
        return columns
    
    def process_sequential(self, max_days: int = 10000):
        """Process data sequentially with time change logic (always enabled)"""
        print("=" * 100)
        print("SEQUENTIAL TIME CHANGE PROCESSOR")
        print("=" * 100)
        print(f"Starting with: Time={self.current_time}")
        print(f"Max Days: {'ALL AVAILABLE DATA' if max_days >= 10000 else max_days}")
        
        # Get date range from data
        if len(self.data) > 0:
            min_date = self.data['Date'].min()
            max_date = self.data['Date'].max()
            print(f"Data range: {min_date.strftime('%Y-%m-%d')} to {max_date.strftime('%Y-%m-%d')}")
            print(f"Total rows: {len(self.data):,}")
        print("=" * 100)
        
        # Process all available data if max_days is very high
        if max_days >= 10000:
            max_days = 999999  # Effectively unlimited
        
        # Track current month for progress logging
        current_month = None
        days_in_month = 0
        
        for day in range(1, max_days + 1):
            # Check if current time is excluded
            exclude_times_str = [str(t) for t in self.exclude_times] if self.exclude_times else []
            if str(self.current_time) in exclude_times_str:
                # Current time is excluded - skip to next available time in same session
                current_session = self.current_session
                session_times = self.SLOT_ENDS.get(current_session, [])
                available_in_session = [t for t in self.available_times if t in session_times and str(t) not in exclude_times_str]
                if available_in_session:
                    self.current_time = available_in_session[0]
                    self.current_session = self._get_session_for_time(self.current_time)
                    print(f"Current time was excluded. Switched to: {self.current_time}")
                else:
                    print(f"Warning: All times in {current_session} are excluded. Skipping day.")
                    continue
            
            # Get data for current time/session combination
            data_row = self.get_next_data(self.current_time, self.current_session)
            
            # Check if we've run out of data
            if data_row['Date'] == 'NO DATA':
                # Log final month summary if we were processing a month
                if current_month is not None:
                    print(f"\n✓ Completed month {current_month}: {days_in_month} days processed")
                print(f"\nNo more data available. Stopping at day {day-1}")
                break
            
            # Track month changes for progress logging
            if data_row['Date'] != 'NO DATA':
                data_date = pd.to_datetime(data_row['Date'])
                month_key = f"{data_date.year}-{data_date.month:02d}"
                
                # If month changed, log completion of previous month
                if current_month is not None and month_key != current_month:
                    print(f"\n✓ Completed month {current_month}: {days_in_month} days processed")
                    days_in_month = 0
                
                # If starting a new month, log it
                if month_key != current_month:
                    current_month = month_key
                    month_name = data_date.strftime('%B %Y')
                    print(f"\n{'='*100}")
                    print(f"📅 Processing month: {month_name} ({current_month})")
                    print(f"{'='*100}")
                    days_in_month = 0
                
                days_in_month += 1
            
            print(f"\n--- DAY {day} ({data_row['Date']}) ---")
            print(f"Looking for: Time={self.current_time}, Session={self.current_session}")
            print(f"Data found: {data_row['Date']} | Peak={data_row['Peak']} | Result={data_row['Result']}")
            
            # Store current state before changes
            old_time = self.current_time
            
            # Track which slot's history was already updated in update_time_change
            slot_already_updated = None
            
            # Apply time change logic (always enabled)
            if data_row['Result'] != 'NO DATA':
                new_time, time_reason = self.update_time_change(data_row['Result'])
                # The slot that was current when update_time_change ran has already been added to history
                slot_already_updated = old_time
                if new_time != old_time:
                    self.current_time = new_time
                    self.current_session = self._get_session_for_time(new_time)  # Update session to match new time
                    print(f"TIME CHANGE: {old_time} → {new_time} ({time_reason})")
                else:
                    print(f"Time: {time_reason}")
            else:
                time_reason = "NO DATA - No time change"
                print(f"Time: {time_reason}")
            
            # Update all time slot points to show actual daily win/loss for each time slot (AFTER time change logic)
            if data_row['Result'] != 'NO DATA':
                current_date = data_row['Date']
                
                # Update ALL time slot points, histories, and rolling sums to show their actual daily results
                # Skip excluded time slots
                exclude_times_str = [str(t) for t in self.exclude_times] if self.exclude_times else []
                debug_points_updates = []
                for time_slot in self.available_times:
                    # Skip excluded time slots
                    if str(time_slot) in exclude_times_str:
                        continue
                    time_str = str(time_slot)
                    session = self._get_session_for_time(time_str)
                    slot_data = self.get_data_for_date_and_time(current_date, time_str, session)
                    
                    if slot_data and slot_data['Date'] != 'NO DATA':
                        slot_result = slot_data['Result']
                        old_score = self.time_slot_points.get(time_str, "N/A")
                        slot_score = self._calculate_time_score(slot_result)
                        
                        # Update points
                        self.time_slot_points[time_str] = slot_score
                        
                        # Only add to history if this slot's history was NOT already updated in update_time_change
                        if time_str != str(slot_already_updated):
                            # Initialize history if not exists
                            if time_str not in self.time_slot_histories:
                                self.time_slot_histories[time_str] = []
                            # Add score to history
                            self.time_slot_histories[time_str].append(slot_score)
                            # Keep only last 13 trades per slot
                            if len(self.time_slot_histories[time_str]) > 13:
                                self.time_slot_histories[time_str] = self.time_slot_histories[time_str][-13:]
                        
                        # Update rolling sum for ALL time slots (including current)
                        rolling_sum = sum(self.time_slot_histories.get(time_str, []))
                        self.time_slot_rolling[time_str] = rolling_sum
                        
                        debug_points_updates.append(f"{time_str}: {old_score}→{slot_score} ({slot_result})")
                    else:
                        debug_points_updates.append(f"{time_str}: NO DATA found")
                
                # Debug output for CL data after 2020 - disabled once issue is resolved
                # if hasattr(self, 'last_processed_date') and self.last_processed_date and self.last_processed_date >= pd.Timestamp('2021-01-01'):
                #     print(f"🔍 DEBUG FINAL Points Update {current_date.strftime('%Y-%m-%d')}: {', '.join(debug_points_updates)}")
            
            # Get dynamic time slot columns
            time_slot_columns = self.get_time_slot_columns()
            
            # Debug output for CL data after 2020 - show final time slot points values - disabled once issue is resolved
            # if hasattr(self, 'last_processed_date') and self.last_processed_date and self.last_processed_date >= pd.Timestamp('2021-01-01'):
            #     points_debug = []
            #     for time_slot in sorted(self.available_times):
            #         time_str = str(time_slot)
            #         points_val = time_slot_columns.get(f"{time_str} Points", "N/A")
            #         rolling_val = time_slot_columns.get(f"{time_str} Rolling", "N/A")
            #         points_debug.append(f"{time_str}: Points={points_val}, Rolling={rolling_val}")
            #     print(f"📊 DEBUG Final Values {data_row['Date'].strftime('%Y-%m-%d')}: {', '.join(points_debug)}")
            
            # Store result
            # Use profit directly from analyzer (stop loss capping already applied there)
            adjusted_profit = data_row['Profit']

            # Get day of week (3-letter format)
            day_of_week = 'N/A'
            if data_row['Date'] != 'NO DATA':
                try:
                    day_of_week = data_row['Date'].strftime('%a').upper()
                except:
                    day_of_week = 'N/A'
            
            # Calculate profit in dollars
            profit_dollars = 0.0
            if isinstance(adjusted_profit, (int, float)):
                instrument = data_row.get('Instrument', 'ES')
                contract_values = {"ES": 50, "NQ": 10, "YM": 5, "CL": 1000, "NG": 10000, "GC": 100}  # NQ halved to 10
                contract_value = contract_values.get(instrument, 50)
                profit_dollars = adjusted_profit * contract_value
            
            # Calculate revised result and profit (excluding filtered days and loss recovery mode)
            revised_result = data_row['Result']
            revised_profit_dollars = profit_dollars
            
            # Check if this day should be excluded
            if data_row['Date'] != 'NO DATA':
                try:
                    # Check rolling sum threshold first (applies to day after threshold check)
                    if self.skip_revised_calc_today:
                        revised_result = ""
                        revised_profit_dollars = 0
                    
                    # Check day of week exclusion
                    day_name = data_row['Date'].strftime('%A')
                    if day_name in self.exclude_days_of_week:
                        revised_result = ""
                        revised_profit_dollars = 0
                    
                    # Check day of month exclusion
                    day_of_month = data_row['Date'].day
                    if day_of_month in self.exclude_days_of_month:
                        revised_result = ""
                        revised_profit_dollars = 0
                    
                    # Check loss recovery mode - this should work on ALL trades regardless of day filtering
                    if self.loss_recovery_mode:
                        # Check if we're in a loss streak (previous trade was a loss)
                        if not self.last_result_was_win:
                            # We're in a loss streak, don't count this trade in revised data
                            revised_result = ""
                            revised_profit_dollars = 0
                        
                        # Update the state for the NEXT trade based on current result (regardless of filtering)
                        current_result = data_row['Result']
                        if current_result == 'Loss':
                            self.last_result_was_win = False
                        elif current_result in ['Win', 'BE', 'NO TRADE', 'TIME']:
                            # Win, Break Even, No Trade, and Time all reset the loss streak
                            self.last_result_was_win = True
                except:
                    pass

            # Calculate SL (Stop Loss) - 3x target, capped at Range if available
            sl_value = 0
            if data_row['Target'] != 'NO DATA' and isinstance(data_row['Target'], (int, float)):
                target_for_trade = data_row['Target']
                sl_value = 3 * target_for_trade
                
                # Cap SL at Range if Range is available and valid
                if data_row['Range'] != 'NO DATA' and isinstance(data_row['Range'], (int, float)) and data_row['Range'] > 0:
                    sl_value = min(sl_value, data_row['Range'])

            # Get instrument and session from data_row or processor
            instrument = data_row.get('Instrument', self.instrument if hasattr(self, 'instrument') else 'UNKNOWN')
            session = data_row.get('Session', self.current_session if hasattr(self, 'current_session') else 'UNKNOWN')
            
            result_row = {
                'Date': data_row['Date'].strftime('%Y-%m-%d') if data_row['Date'] != 'NO DATA' else 'NO DATA',
                'Day of Week': day_of_week,
                'Stream': data_row['Stream'],
                'Time': data_row['Time'],
                'Target': data_row['Target'],
                'Range': data_row['Range'],
                'SL': sl_value,
                'Profit': adjusted_profit,
                'Peak': data_row['Peak'],
                'Direction': data_row['Direction'],
                'Result': data_row['Result'],
                'Revised Score': revised_result,
                'Day': day,
                'Time Reason': time_reason,
                'Time Change': f"{old_time}→{self.current_time}" if old_time != self.current_time else "",
                'Profit ($)': round(profit_dollars, 2),
                'Revised Profit ($)': round(revised_profit_dollars, 2),
                'Instrument': instrument,
                'Session': session
            }
            
            # Add dynamic time slot columns
            result_row.update(time_slot_columns)
            
            self.results.append(result_row)
            
            # Check rolling sum threshold for NEXT day (after all rolling sums are updated and result is stored)
            # This check happens at the END of processing day N, and sets the flag for day N+1
            if self.rolling_sum_threshold is not None and data_row['Result'] != 'NO DATA':
                # Get all rolling sums (excluding excluded time slots)
                all_rolling_sums = []
                exclude_times_str = [str(t) for t in self.exclude_times] if self.exclude_times else []
                for time_slot in self.available_times:
                    if str(time_slot) not in exclude_times_str:
                        time_str = str(time_slot)
                        rolling_sum = self.time_slot_rolling.get(time_str, 0)
                        all_rolling_sums.append(rolling_sum)
                
                # Check if ALL rolling sums are BELOW threshold
                # If ALL are below, skip revised calc. If at least ONE is above/equal, allow revised calc.
                if all_rolling_sums and all(rs < self.rolling_sum_threshold for rs in all_rolling_sums):
                    # ALL time slots are below threshold - skip revised calc for NEXT day
                    self.skip_revised_calc_today = True
                    print(f"Rolling sum threshold check: ALL time slots below {self.rolling_sum_threshold}. Skipping revised calc for next day. Rolling sums: {all_rolling_sums}")
                else:
                    # At least one time slot is above/equal to threshold - allow revised calc for NEXT day
                    self.skip_revised_calc_today = False
                    if all_rolling_sums:
                        above_threshold = [rs for rs in all_rolling_sums if rs >= self.rolling_sum_threshold]
                        print(f"Rolling sum threshold check: At least one time slot >= {self.rolling_sum_threshold}. Revised calc enabled for next day. Above threshold: {above_threshold}")
            
            print(f"Next day will use: Time={self.current_time}")
        
        return pd.DataFrame(self.results)

    def get_day_of_week_analysis(self, results_df: pd.DataFrame) -> Dict:
        """Compute profit and win-rate statistics by day of week from results."""
        try:
            if results_df is None or len(results_df) == 0:
                return {}
            if 'Date' not in results_df.columns:
                return {}
            df = results_df.copy()
            # Parse dates safely
            df['Date'] = pd.to_datetime(df['Date'], errors='coerce')
            df = df.dropna(subset=['Date'])
            if len(df) == 0:
                return {}
            # Day name and ordering
            df['DayName'] = df['Date'].dt.day_name()
            order = ['Monday','Tuesday','Wednesday','Thursday','Friday','Saturday','Sunday']
            # Profit and results
            has_profit = 'Profit' in df.columns
            has_result = 'Result' in df.columns
            # Aggregations
            summary = {}
            contract_values = {"ES": 50, "NQ": 10, "YM": 5, "CL": 1000, "NG": 10000, "GC": 100}  # NQ halved to 10
            # Get instrument from first row of data
            instrument = df['Instrument'].iloc[0] if 'Instrument' in df.columns else 'ES'
            contract_value = contract_values.get(instrument, 50)
            total_profit = 0.0
            total_wins = 0
            total_losses = 0
            for day in order:
                day_df = df[df['DayName'] == day]
                if len(day_df) == 0:
                    continue
                trade_count = len(day_df)
                win_count = int((day_df['Result'] == 'Win').sum()) if has_result else 0
                loss_count = int((day_df['Result'] == 'Loss').sum()) if has_result else 0
                profit_sum = float(day_df['Profit'].sum()) if has_profit else 0.0
                avg_profit = float(day_df['Profit'].mean()) if has_profit else 0.0
                win_rate = (win_count / (win_count + loss_count) * 100.0) if (win_count + loss_count) > 0 else 0.0
                summary[day] = {
                    'Trade_Count': trade_count,
                    'Win_Count': win_count,
                    'Loss_Count': loss_count,
                    'Win_Rate': round(win_rate, 1),
                    'Total_Profit': round(profit_sum, 2),
                    'Avg_Profit': round(avg_profit, 2),
                    'Total_Profit_Dollars': round(profit_sum * contract_value, 2),
                    'Avg_Profit_Dollars': round(avg_profit * contract_value, 2),
                }
                total_profit += profit_sum
                total_wins += win_count
                total_losses += loss_count
            if not summary:
                return {}
            # Best/worst day by Total_Profit_Dollars
            best_day = max(summary.items(), key=lambda kv: kv[1]['Total_Profit_Dollars'])[0]
            worst_day = min(summary.items(), key=lambda kv: kv[1]['Total_Profit_Dollars'])[0]
            overall_win_rate = (total_wins / (total_wins + total_losses) * 100.0) if (total_wins + total_losses) > 0 else 0.0
            return {
                'summary': summary,
                'best_day': best_day,
                'worst_day': worst_day,
                'total_profit': round(total_profit, 2),
                'overall_win_rate': round(overall_win_rate, 1),
            }
        except Exception:
            return {}

def main():
    """Main function to run the sequential processor"""
    import argparse
    
    # Parse command line arguments
    parser = argparse.ArgumentParser(description='Sequential Time Change Processor')
    parser.add_argument('--data-file', default="time_target_change_data/ES_2025-01-02_to_2025-09-19_2604trades_38winrate_2897profit_20250920_101405.parquet", help='Path to data file')
    parser.add_argument('--start-time', default="08:00", help='Starting time slot (will be validated against available times in data)')
    parser.add_argument('--max-days', type=int, default=10000, help='Maximum days to process (set to 10000 to process all data)')
    parser.add_argument('--output-folder', default="data/sequencer_runs", help='Output folder for results (default: data/sequencer_runs)')
    
    args = parser.parse_args()
    
    # Load data
    if not os.path.exists(args.data_file):
        print(f"Error: Data file not found: {args.data_file}")
        return
    
    # Initialize processor
    processor = SequentialProcessorV2(
        args.data_file, 
        args.start_time
    )
    
    # Process with time change always enabled
    results = processor.process_sequential(
        max_days=args.max_days
    )
    
    print("\n" + "=" * 100)
    print("FINAL RESULTS")
    print("=" * 100)
    
    # Auto-size columns for better display
    pd.set_option('display.max_columns', None)
    pd.set_option('display.width', None)
    pd.set_option('display.max_colwidth', None)
    pd.set_option('display.expand_frame_repr', False)
    
    # Display results with auto-sized columns
    if len(results) > 0:
        display_columns = ['Day', 'Date', 'Time', 'Target', 'Peak', 'Result', 'Time Change']
        if '09:00 Points' in results.columns:
            display_columns.extend(['09:00 Points', '09:00 Rolling'])
        
        print(results[display_columns].to_string(index=False))
    else:
        print("No results to display (no data found for the specified parameters)")
    
    # Reset pandas display options
    pd.reset_option('display.max_columns')
    pd.reset_option('display.width')
    pd.reset_option('display.max_colwidth')
    pd.reset_option('display.expand_frame_repr')
    
    # Save results with timestamp - manual runs go to manual_sequencer_runs, automatic runs go to sequencer_temp
    timestamp = datetime.now().strftime('%Y%m%d_%H%M%S')
    today = datetime.now().strftime('%Y-%m-%d')
    
    # Determine output folder based on whether it's a pipeline run or manual run
    is_pipeline_run = os.getenv("PIPELINE_RUN", "0") == "1"
    
    # Use sequencer_temp if default output folder, otherwise use custom
    if args.output_folder == "data/sequencer_runs":
        if is_pipeline_run:
            # Automatic pipeline run - use sequencer_temp (for data merger)
            output_folder = f'data/sequencer_temp/{today}'
        else:
            # Manual run - use manual_sequencer_runs folder
            output_folder = f'data/manual_sequencer_runs/{today}'
    else:
        output_folder = args.output_folder
    
    # Ensure directory exists
    os.makedirs(output_folder, exist_ok=True)
    
    # Extract instrument and session from input filename first (most reliable)
    # Pattern: CL2_an_2025_09.parquet -> CL, S2
    import re
    input_filename = os.path.basename(args.data_file)
    filename_match = re.match(r'^([A-Z]{2})([12])_', input_filename)
    if filename_match:
        instrument = filename_match.group(1).upper()
        session = 'S1' if filename_match.group(2) == '1' else 'S2'
        print(f"Detected instrument '{instrument}' and session '{session}' from input filename: {input_filename}")
    else:
        instrument = "UNKNOWN"
        session = "UNKNOWN"
        
        # Try to get instrument from results DataFrame
        if 'Instrument' in results.columns:
            instruments = results['Instrument'].dropna().unique()
            if len(instruments) > 0:
                instrument = str(instruments[0]).upper().strip()
        
        # Try to get session from results DataFrame
        if 'Session' in results.columns:
            sessions = results['Session'].dropna().unique()
            if len(sessions) > 0:
                session_str = str(sessions[0]).strip().upper()
                if session_str in ['S1', 'S2']:
                    session = session_str
        
        # Try to extract from Stream column (e.g., "ES1", "GC2")
        if (instrument == "UNKNOWN" or session == "UNKNOWN") and 'Stream' in results.columns and len(results) > 0:
            stream_value = str(results['Stream'].iloc[0]).strip()
            # Pattern: ES1, GC2, CL1, etc.
            stream_match = re.match(r'^([A-Z]{2})([12])', stream_value)
            if stream_match:
                if instrument == "UNKNOWN":
                    instrument = stream_match.group(1).upper()
                if session == "UNKNOWN":
                    session = 'S1' if stream_match.group(2) == '1' else 'S2'
        
        # Infer session from Time column if Session column doesn't exist
        if session == "UNKNOWN" and 'Time' in results.columns and len(results) > 0:
            time_slots_s1 = ['07:30', '08:00', '09:00']
            time_slots_s2 = ['09:30', '10:00', '10:30', '11:00']
            first_time = str(results['Time'].iloc[0]).strip()
            if first_time in time_slots_s1:
                session = 'S1'
            elif first_time in time_slots_s2:
                session = 'S2'
        
        # Fallback: try to get from processor if available
        if instrument == "UNKNOWN" and hasattr(processor, 'instrument'):
            instrument = processor.instrument.upper()
        if session == "UNKNOWN" and hasattr(processor, 'current_session'):
            session = processor.current_session
        
        # Final fallback: try to extract from input data file
        if instrument == "UNKNOWN" or session == "UNKNOWN":
            try:
                # Load input data to check for Instrument/Session columns
                input_df = pd.read_parquet(args.data_file)
                if instrument == "UNKNOWN" and 'Instrument' in input_df.columns:
                    instruments = input_df['Instrument'].dropna().unique()
                    if len(instruments) > 0:
                        instrument = str(instruments[0]).upper().strip()
                if session == "UNKNOWN" and 'Session' in input_df.columns:
                    sessions = input_df['Session'].dropna().unique()
                    if len(sessions) > 0:
                        session_str = str(sessions[0]).strip().upper()
                        if session_str in ['S1', 'S2']:
                            session = session_str
            except:
                pass
    
    # Convert session to suffix: S1 -> 1, S2 -> 2
    session_suffix = "1" if session == "S1" else "2" if session == "S2" else ""
    
    # Build filename with instrument and session
    if instrument != "UNKNOWN" and session_suffix:
        filename_base = f'{instrument}{session_suffix}_seq_{timestamp}'
    else:
        # Fallback to old naming if we can't determine instrument/session
        filename_base = f'sequential_run_{timestamp}'
        if instrument != "UNKNOWN":
            print(f"Warning: Could not determine session, using fallback filename")
        elif session_suffix:
            print(f"Warning: Could not determine instrument, using fallback filename")
        else:
            print(f"Warning: Could not determine instrument or session, using fallback filename")
    
    # Save as Parquet (primary format, like analyzer)
    parquet_filename = f'{output_folder}/{filename_base}.parquet'
    results.to_parquet(parquet_filename, index=False, compression='snappy')
    print(f"\nResults saved to: {parquet_filename}")
    
    # Also save as CSV for compatibility
    csv_filename = f'{output_folder}/{filename_base}.csv'
    results.to_csv(csv_filename, index=False)
    print(f"Results also saved to: {csv_filename}")
    
    # Show summary
    print("\n" + "=" * 50)
    print("SUMMARY")
    print("=" * 50)
    
    if len(results) > 0:
        time_changes = len(results[results['Time Change'] != '']) if 'Time Change' in results.columns else 0
    
        # Calculate win rate and profit
        total_trades = len(results)
        wins = len(results[results['Result'] == 'Win'])
        losses = len(results[results['Result'] == 'Loss'])
        break_even = len(results[results['Result'] == 'BE'])
        # Win rate excludes BE trades (only wins vs losses)
        win_loss_trades = wins + losses
        win_rate = (wins / win_loss_trades * 100) if win_loss_trades > 0 else 0
        
        total_profit = results['Profit'].sum()
        
        # Calculate Risk-Reward ratio
        # RR = Average Win / Average Loss (absolute values)
        winning_trades = results[results['Result'] == 'Win']
        losing_trades = results[results['Result'] == 'Loss']
        
        avg_win = winning_trades['Profit'].mean() if len(winning_trades) > 0 else 0
        avg_loss = abs(losing_trades['Profit'].mean()) if len(losing_trades) > 0 else 0
        
        rr_ratio = avg_win / avg_loss if avg_loss > 0 else float('inf') if avg_win > 0 else 0
        
        print(f"Total Days: {total_trades}")
        print(f"Wins: {wins} | Losses: {losses} | Break-Even: {break_even}")
        print(f"Win Rate: {win_rate:.1f}%")
        print(f"Total Profit: {total_profit:.2f}")
        print(f"Risk-Reward: {rr_ratio:.2f} ({avg_win:.1f}/{avg_loss:.1f})")
        print(f"Time Changes: {time_changes}")
        print(f"Final State: Time={results.iloc[-1]['Time']}")
    else:
        print("No data processed - check your parameters and data file")
        print(f"Requested time: {processor.current_time}")
        print(f"Available times in data: {sorted(processor.data['Time'].unique())}")

if __name__ == "__main__":
    main()
