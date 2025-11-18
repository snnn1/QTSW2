#!/usr/bin/env python3
"""
Sequential Time Change Processor V2
Processes historical data line by line with real-time time change logic application
"""

import pandas as pd
import numpy as np
import os
from datetime import datetime, timedelta
from typing import Dict, List, Tuple, Optional

class SequentialProcessorV2:
    def __init__(self, data_file: str, start_time: str = "08:00", time_mode: str = "normal", exclude_days_of_week: list = None, exclude_days_of_month: list = None, loss_recovery_mode: bool = False, data_files: list = None):
        """Initialize with historical data file(s)"""
        # If multiple files provided, load and concatenate them
        if data_files and len(data_files) > 1:
            dataframes = []
            for file_path in data_files:
                df = pd.read_parquet(file_path)
                dataframes.append(df)
            self.data = pd.concat(dataframes, ignore_index=True)
            print(f"Loaded {len(data_files)} files and combined into {len(self.data)} rows")
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
            
        
        # Add current trade to history
        self.time_slot_histories[self.current_time].append(current_score)
        
        # Keep only last 13 trades per slot
        if len(self.time_slot_histories[self.current_time]) > 13:
            self.time_slot_histories[self.current_time] = self.time_slot_histories[self.current_time][-13:]
        
        # Calculate rolling sums for all time slots BEFORE adding today's result
        # This ensures fair comparison - we compare historical performance, then add today's result
        current_sum_before = sum(self.time_slot_histories[self.current_time])
        current_sum_after = current_sum_before + current_score  # After adding today's result
        
        # Find the best performing "other" time slot (highest rolling sum among non-current slots)
        # IMPORTANT: Only consider time slots in the SAME SESSION
        current_session = self.current_session
        session_times = self.SLOT_ENDS.get(current_session, [])
        other_times = [t for t in self.available_times if t != self.current_time and t in session_times]
        
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
        
        return self.current_time, f"Stay {self.current_time} (current: {current_sum:.2f}, other: {other_sum:.2f})"
    
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
        print("=" * 100)
        
        # Process all available data if max_days is very high
        if max_days >= 10000:
            max_days = 999999  # Effectively unlimited
        
        for day in range(1, max_days + 1):
            print(f"\n--- DAY {day} ---")
            
            # Get data for current time/session combination
            data_row = self.get_next_data(self.current_time, self.current_session)
            
            print(f"Looking for: Time={self.current_time}, Session={self.current_session}")
            print(f"Data found: {data_row['Date']} | Peak={data_row['Peak']} | Result={data_row['Result']}")
            
            # Check if we've run out of data
            if data_row['Date'] == 'NO DATA':
                print(f"No more data available. Stopping at day {day-1}")
                break
            
            # Store current state before changes
            old_time = self.current_time
            
            # Apply time change logic (always enabled)
            if data_row['Result'] != 'NO DATA':
                new_time, time_reason = self.update_time_change(data_row['Result'])
                if new_time != old_time:
                    self.current_time = new_time
                    self.current_session = self._get_session_for_time(new_time)  # Update session to match new time
                    print(f"TIME CHANGE: {old_time} → {new_time} ({time_reason})")
                else:
                    print(f"Time: {time_reason}")
            else:
                time_reason = "NO DATA - No time change"
                print(f"⏰ Time: {time_reason}")
            
            # Update all time slot points to show actual daily win/loss for each time slot (AFTER time change logic)
            if data_row['Result'] != 'NO DATA':
                current_date = data_row['Date']
                
                # Update ALL time slot points, histories, and rolling sums to show their actual daily results
                debug_points_updates = []
                for time_slot in self.available_times:
                    time_str = str(time_slot)
                    session = self._get_session_for_time(time_str)
                    slot_data = self.get_data_for_date_and_time(current_date, time_str, session)
                    
                    if slot_data and slot_data['Date'] != 'NO DATA':
                        slot_result = slot_data['Result']
                        old_score = self.time_slot_points.get(time_str, "N/A")
                        slot_score = self._calculate_time_score(slot_result)
                        
                        # Update points
                        self.time_slot_points[time_str] = slot_score
                        
                        # Only add to history if this is NOT the current time slot (current slot history already updated in time change logic)
                        if time_str != str(self.current_time):
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
                'Revised Profit ($)': round(revised_profit_dollars, 2)
            }
            
            # Add dynamic time slot columns
            result_row.update(time_slot_columns)
            
            self.results.append(result_row)
            
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
    
    # Save results with timestamp to sequencer_temp with date-based folder structure
    timestamp = datetime.now().strftime('%Y%m%d_%H%M%S')
    today = datetime.now().strftime('%Y-%m-%d')
    
    # Use sequencer_temp if default output folder, otherwise use custom
    if args.output_folder == "data/sequencer_runs":
        output_folder = f'data/sequencer_temp/{today}'
    else:
        output_folder = args.output_folder
    
    # Ensure directory exists
    os.makedirs(output_folder, exist_ok=True)
    
    # Save as Parquet (primary format, like analyzer)
    parquet_filename = f'{output_folder}/sequential_run_{timestamp}.parquet'
    results.to_parquet(parquet_filename, index=False, compression='snappy')
    print(f"\nResults saved to: {parquet_filename}")
    
    # Also save as CSV for compatibility
    csv_filename = f'{output_folder}/sequential_run_{timestamp}.csv'
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
