#!/usr/bin/env python3
"""
Sequential Target Change and Time Change Processor V2
Processes historical data line by line with real-time logic application
"""

import pandas as pd
import numpy as np
import os
from datetime import datetime, timedelta
from typing import Dict, List, Tuple, Optional

class SequentialProcessorV2:
    def __init__(self, data_file: str, start_time: str = "08:00", time_mode: str = "normal", exclude_days_of_week: list = None, exclude_days_of_month: list = None, loss_recovery_mode: bool = False):
        """Initialize with historical data file"""
        self.data = pd.read_parquet(data_file)
        self.data['Date'] = pd.to_datetime(self.data['Date'])
        
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
            print("⚠️ Could not auto-detect instrument from targets, defaulting to ES")
            return "ES"
    
    def _get_session_for_time(self, time_str: str) -> str:
        """Get session (S1 or S2) for a given time slot"""
        for session, times in self.SLOT_ENDS.items():
            if time_str in times:
                return session
        # Default to S1 if not found (shouldn't happen with correct configuration)
        return "S1"
        
    def get_ladder(self, base: float) -> List[float]:
        """Get target ladder for instrument with invisible cap rung"""
        return [base * (1 + 0.5 * i) for i in range(8)]  # 8 rungs instead of 7
    
    def round_down_to_tick(self, value: float, tick: float) -> float:
        """Round down to nearest tick"""
        return (value // tick) * tick
    
    def round_down_to_nearest_increment(self, value: float, instrument: str = "ES") -> float:
        """Round down to nearest increment based on instrument"""
        # Define rounding increments for each instrument
        increments = {
            "ES": 5,      # ES targets: 10, 15, 20, 25, 30, 35, 40
            "NQ": 25,     # NQ targets: 50, 75, 100, 125, 150, 175, 200
            "YM": 50,     # YM targets: 100, 150, 200, 250, 300, 350, 400
            "CL": 0.25,   # CL targets: 0.5, 0.75, 1.0, 1.25, 1.5, 1.75, 2.0
            "GC": 2.5,    # GC targets: 5, 7.5, 10, 12.5, 15, 17.5, 20
            "NG": 0.025,  # NG targets: 0.05, 0.075, 0.1, 0.125, 0.15
        }
        
        increment = increments.get(instrument, 25)  # Default to 25 for unknown instruments
        return (value // increment) * increment
    
    def calculate_rolling_median_target(self, peak: float, instrument: str = "ES") -> Tuple[float, str]:
        """
        Calculate rolling median target based on last 13 peaks
        
        Args:
            peak: Current trade peak
            instrument: Trading instrument to get base target
            
        Returns:
            Tuple of (target, reason)
        """
        # Get base target for this instrument
        base_target = self.BASE_TARGETS.get(instrument, 10)
        
        if peak == 'NO DATA':
            # No valid data - don't modify window or flag
            # Return current target to maintain progression, not base target
            current = self.rolling_median_target if hasattr(self, 'rolling_median_target') else base_target
            return current, "NO DATA - No target change"
        
        # Valid peak data (including 0 for no-trade days) - proceed with calculation
        
        # Add peak to rolling window (keep last 13)
        self.rolling_median_window.append(peak)
        if len(self.rolling_median_window) > 13:
            self.rolling_median_window.pop(0)
        
        # Calculate median
        if len(self.rolling_median_window) < 13:
            # Startup phase - use instrument-specific base target
            median = float(base_target)
            reason = f"Startup ({len(self.rolling_median_window)}/13 peaks) - using base {base_target}"
        else:
            # Calculate median position value of last 13 peaks
            median = self.get_median_position_value(self.rolling_median_window)
            reason = f"Rolling median (position {self.median_position}): {median:.1f} (last 13 peaks)"
        
        # Round down to nearest increment based on instrument
        rounded_target = self.round_down_to_nearest_increment(median, instrument)
        
        # If rounded target is below base target, use base target
        if rounded_target < base_target:
            rounded_target = base_target
            reason += f" - using base target (rounded < base={base_target})"
        
        # Cap based on instrument and max multiplier
        max_multiplier = self.max_target_percentage / 100.0  # Convert percentage to multiplier
        max_target = base_target * max_multiplier
        
        if rounded_target > max_target:
            rounded_target = max_target
            reason += f" - capped at {max_target} ({max_multiplier*100:.0f}% of base)"
        
        # Check for no-trade condition (use instrument-specific base)
        # This sets the no-trade flag for the NEXT day
        if self.enable_no_trade_on_low_median and median < base_target:
            self.next_day_no_trade = True
            reason += f" - NO TRADE NEXT DAY (median < base={base_target})"
        else:
            self.next_day_no_trade = False
        
        return rounded_target, reason
    
    def get_median_position_value(self, values: List[float]) -> float:
        """Get the value at the specified position in sorted list (1-13)"""
        if not values:
            return 0.0
        
        sorted_values = sorted(values)
        n = len(sorted_values)
        
        # Clamp position to available range
        position = max(1, min(self.median_position, n))
        
        # Convert 1-based position to 0-based index
        index = position - 1
        return sorted_values[index]
    
    def get_median_ladder_steps(self, base: float, instrument: str = "ES") -> List[float]:
        """Get median ladder steps for instrument"""
        # Create ladder steps: base, base*1.5, base*2, base*2.5, base*3, base*3.5, base*4, etc.
        # But use instrument-specific increments for better granularity
        if instrument == "NQ":
            # NQ: 50, 75, 100, 125, 150, 175, 200, 225, 250, 275, 300, 325, 350, 375, 400
            return [base * (1 + 0.5 * i) for i in range(15)]
        elif instrument == "ES":
            # ES: 10, 15, 20, 25, 30, 35, 40, 45, 50, 55, 60, 65, 70, 75, 80
            return [base * (1 + 0.5 * i) for i in range(15)]
        elif instrument == "YM":
            # YM: 100, 150, 200, 250, 300, 350, 400, 450, 500, 550, 600, 650, 700, 750, 800
            return [base * (1 + 0.5 * i) for i in range(15)]
        elif instrument == "CL":
            # CL: 0.5, 0.75, 1.0, 1.25, 1.5, 1.75, 2.0, 2.25, 2.5, 2.75, 3.0, 3.25, 3.5, 3.75, 4.0
            return [base * (1 + 0.5 * i) for i in range(15)]
        elif instrument == "GC":
            # GC: 50, 75, 100, 125, 150, 175, 200, 225, 250, 275, 300, 325, 350, 375, 400
            return [base * (1 + 0.5 * i) for i in range(15)]
        elif instrument == "NG":
            # NG: 50, 75, 100, 125, 150, 175, 200, 225, 250, 275, 300, 325, 350, 375, 400
            return [base * (1 + 0.5 * i) for i in range(15)]
        else:
            # Default: use standard ladder
            return [base * (1 + 0.5 * i) for i in range(15)]
    
    def calculate_median_ladder_target(self, peak: float, instrument: str = "ES") -> Tuple[float, str]:
        """
        Calculate median ladder target based on last 13 peaks with progressive ladder steps
        
        Args:
            peak: Current trade peak
            instrument: Trading instrument to get base target
            
        Returns:
            Tuple of (target, reason)
        """
        # Get base target for this instrument
        base_target = self.BASE_TARGETS.get(instrument, 10)
        
        if peak == 'NO DATA':
            # No valid data - don't modify window or flag
            # Return current target to maintain progression, not base target
            current = self.median_ladder_target if hasattr(self, 'median_ladder_target') else base_target
            return current, "NO DATA - No target change"
        
        # Valid peak data (including 0 for no-trade days) - proceed with calculation
        
        # Add peak to rolling window (keep last 13)
        self.median_ladder_window.append(peak)
        if len(self.median_ladder_window) > 13:
            self.median_ladder_window.pop(0)
        
        # Calculate median
        if len(self.median_ladder_window) < 13:
            # Startup phase - use instrument-specific base target
            median = float(base_target)
            reason = f"Startup ({len(self.median_ladder_window)}/13 peaks) - using base {base_target}"
            target = base_target
            # Increment days counter during startup phase
            self.median_ladder_days_at_level += 1
        else:
            # Calculate median position value of last 13 peaks
            median = self.get_median_position_value(self.median_ladder_window)
            
            # Get ladder steps for this instrument
            ladder_steps = self.get_median_ladder_steps(base_target, instrument)
            
            # Find which step the median should map to
            target_step_index = 0
            for i, step_value in enumerate(ladder_steps):
                if median >= step_value:
                    target_step_index = i
                else:
                    break
            
            # Determine target based on current position and median
            current_step_index = self.median_ladder_current_step
            current_target = ladder_steps[current_step_index] if current_step_index < len(ladder_steps) else ladder_steps[-1]
            
            # Check for promotion (median suggests higher step)
            if target_step_index > current_step_index:
                # Check if we've been at current level long enough
                if self.median_ladder_days_at_level >= self.median_ladder_promotion_days:
                    # Promote to next step
                    new_step_index = current_step_index + 1
                    if new_step_index < len(ladder_steps):
                        target = ladder_steps[new_step_index]
                        self.median_ladder_current_step = new_step_index
                        self.median_ladder_days_at_level = 1  # Reset counter
                        reason = f"Promotion → {target} (median={median:.1f}, {self.median_ladder_promotion_days} days at {current_target})"
                    else:
                        target = current_target
                        reason = f"At max step (median={median:.1f})"
                else:
                    # Not enough days yet - increment counter and stay at current level
                    target = current_target
                    self.median_ladder_days_at_level += 1
                    reason = f"Median={median:.1f} suggests step {target_step_index}, need {self.median_ladder_promotion_days - self.median_ladder_days_at_level} more days at {current_target} (day {self.median_ladder_days_at_level})"
            # Check for demotion (median suggests lower step)
            elif target_step_index < current_step_index:
                # Immediate demotion
                target = ladder_steps[target_step_index]
                self.median_ladder_current_step = target_step_index
                self.median_ladder_days_at_level = 1  # Reset counter
                reason = f"Demotion → {target} (median={median:.1f} suggests step {target_step_index})"
            else:
                # Same step level - increment days counter
                target = current_target
                self.median_ladder_days_at_level += 1
                reason = f"Median={median:.1f} at step {target_step_index}, day {self.median_ladder_days_at_level}"
        
        # Cap based on instrument and max multiplier
        max_multiplier = self.max_target_percentage / 100.0  # Convert percentage to multiplier
        max_target = base_target * max_multiplier
        
        if target > max_target:
            target = max_target
            reason += f" - capped at {max_target} ({max_multiplier*100:.0f}% of base)"
        
        # Check for no-trade condition (median < base target)
        # This sets the no-trade flag for the NEXT day
        if self.enable_no_trade_on_low_median and len(self.median_ladder_window) >= 13:
            median = self.get_median_position_value(self.median_ladder_window)
            if median < base_target:
                self.median_ladder_next_day_no_trade = True
                reason += f" - NO TRADE NEXT DAY (median={median:.1f} < base={base_target})"
            else:
                self.median_ladder_next_day_no_trade = False
        else:
            self.median_ladder_next_day_no_trade = False
        
        return target, reason
    
    def get_rolling_median_display(self, instrument: str = "ES") -> str:
        """
        Get the rolling median display string - shows exact median value
        
        Args:
            instrument: Trading instrument to get base target
            
        Returns:
            Display string for Rolling Median column
        """
        if self.enable_rolling_median_mode:
            # In rolling median mode, show the actual median position value
            if len(self.rolling_median_window) >= 13:
                median = self.get_median_position_value(self.rolling_median_window)
                return f"{median:.1f}"
            else:
                # Show instrument-specific base target during startup
                base_target = self.BASE_TARGETS.get(instrument, 10)
                return f"{base_target:.1f}"
        elif self.enable_median_ladder_mode:
            # In median ladder mode, show the actual median position value
            if len(self.median_ladder_window) >= 13:
                median = self.get_median_position_value(self.median_ladder_window)
                return f"{median:.1f}"
            else:
                # Show instrument-specific base target during startup
                base_target = self.BASE_TARGETS.get(instrument, 10)
                return f"{base_target:.1f}"
        else:
            # Normal mode - show empty
            return ""
    
    
    def get_target_change_display(self, old_target: float, target_reason: str, instrument: str = "ES") -> str:
        """
        Get the target change display string
        
        Args:
            old_target: Previous target
            target_reason: Reason for target change
            instrument: Trading instrument to get base target
            
        Returns:
            Display string for Target Change column
        """
        if self.enable_rolling_median_mode:
            # In rolling median mode, show the rolling median target for NEXT day
            return f"{self.rolling_median_target:.0f}"
        elif self.enable_median_ladder_mode:
            # In median ladder mode, show the median ladder target for NEXT day
            return f"{self.median_ladder_target:.0f}"
        else:
            # Normal mode - show target change
            return f"{old_target}→{self.current_target}" if old_target != self.current_target else ""
    
    
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
            'Target Change': data_row.get('Target Change', '')
        }
    
    def update_target_change(self, peak: float, instrument: str = "ES", current_date = None) -> Tuple[int, str]:
        """Apply target change logic and return new target and reason"""
        if peak == 'NO DATA' or peak == 'NO DATA':
            return self.current_target, "NO DATA - No target change"
        
        # Check for consecutive target changes if enabled
        if self.no_consecutive_target_changes and self.last_target_change_date is not None and current_date is not None:
            # Check if current date is consecutive to last target change date
            if hasattr(self, 'last_processed_date') and self.last_processed_date is not None:
                days_diff = (self.last_processed_date - self.last_target_change_date).days
                if days_diff == 1:  # Consecutive day
                    return self.current_target, f"No consecutive change (last change: {self.last_target_change_date.strftime('%Y-%m-%d')})"
        
        base = self.BASE_TARGETS[instrument]
        tick = self.TICK_SIZE[instrument]
        ladder = self.get_ladder(base)
        
        # Round peak to tick
        peak = self.round_down_to_tick(peak, tick)
        ignore_threshold = self.round_down_to_tick(base * 0.95, tick)
        
        # Ignore rule
        if peak < ignore_threshold:
            return self.current_target, f"Ignored (<95% base={ignore_threshold})"
        
        # Add to rolling window if enabled
        if self.enable_rolling_mode:
            self.rolling_window.append(peak)
            # Keep only last 3 peaks
            if len(self.rolling_window) > 3:
                self.rolling_window = self.rolling_window[-3:]
        
        # Startup rule (ONLY when at base)
        if self.current_target == base:
            if peak >= 2 * base:
                self.target_streak += 1
                if self.target_streak >= 3:
                    # Find next target in ladder
                    try:
                        current_idx = ladder.index(self.current_target)
                        if current_idx + 1 < len(ladder):
                            new_target = int(ladder[current_idx + 1])
                            self.target_streak = 0
                            self.current_target = new_target
                            return new_target, f"Startup promotion → {new_target}"
                    except ValueError:
                        pass
                return self.current_target, f"Startup streak {self.target_streak}/3"
            else:
                self.target_streak = 0
                return self.current_target, f"Reset streak (<2× base)"
        
        # Demotion rule - check if peak is less than next rung
        try:
            current_idx = ladder.index(self.current_target)
            
            # Determine the threshold for demotion
            if current_idx + 1 < len(ladder):
                # Not at top rung - use next rung as threshold
                next_rung = ladder[current_idx + 1]
                demotion_threshold = next_rung
            else:
                # At top rung - use current rung as threshold (any peak < current should demote)
                demotion_threshold = self.current_target
            
            if peak < demotion_threshold:
                if peak < 2 * base:
                    # Demote to base
                    self.target_streak = 0
                    self.current_target = base
                    return base, f"Demotion to base (peak < 2×base)"
                else:
                    # Demote one level
                    if current_idx > 0:
                        new_target = int(ladder[current_idx - 1])
                    else:
                        new_target = base
                    self.target_streak = 0
                    self.current_target = new_target
                    return new_target, f"Demotion → {new_target}"
        except ValueError:
            pass
        
        # Promotion rule
        try:
            current_idx = ladder.index(self.current_target)
            
            # Check if we're at the max target percentage cap
            max_target = base * (self.max_target_percentage / 100.0)
            if self.current_target >= max_target:
                self.target_streak = 0
                return self.current_target, f"At max cap ({self.max_target_percentage}% = {max_target})"
            
            # Check if we're at the cap (second to last rung - invisible rung is last)
            if current_idx >= len(ladder) - 2:
                self.target_streak = 0
                return self.current_target, f"At cap (no promotion)"
            
            if current_idx + 1 < len(ladder):
                next_rung = ladder[current_idx + 1]
                # Need 3 peaks ≥ two rungs above current (next_rung + 1)
                if current_idx + 2 < len(ladder):
                    required_peak = ladder[current_idx + 2]  # Two rungs above current
                else:
                    required_peak = next_rung  # If only one rung above, use that
                
                # Check if next rung would exceed max target percentage cap
                if int(next_rung) > max_target:
                    self.target_streak = 0
                    return self.current_target, f"Next rung {int(next_rung)} exceeds max cap ({self.max_target_percentage}% = {max_target})"
                
                if self.enable_rolling_mode:
                    # Rolling mode: check if all 3 peaks in window meet requirement
                    if len(self.rolling_window) >= 3:
                        qualifying_peaks = [p for p in self.rolling_window if p >= required_peak]
                        if len(qualifying_peaks) >= 3:
                            self.target_streak = 0
                            self.current_target = int(next_rung)
                            return int(next_rung), f"Rolling promotion → {int(next_rung)} (3/3 peaks ≥ {int(required_peak)})"
                        else:
                            return self.current_target, f"Rolling window: {len(qualifying_peaks)}/3 peaks ≥ {int(required_peak)}"
                    else:
                        return self.current_target, f"Rolling window: {len(self.rolling_window)}/3 peaks collected"
                else:
                    # Standard mode: individual streak
                    if peak >= required_peak:
                        self.target_streak += 1
                        if self.target_streak >= 3:
                            self.target_streak = 0
                            self.current_target = int(next_rung)
                            return int(next_rung), f"Promotion → {int(next_rung)}"
                        return self.current_target, f"Streak {self.target_streak}/3 (need ≥ {int(required_peak)})"
                    else:
                        self.target_streak = 0
                        return self.current_target, f"Reset streak (< {int(required_peak)})"
            else:
                self.target_streak = 0
                return self.current_target, f"At cap (no promotion)"
        except ValueError:
            self.target_streak = 0
            return self.current_target, f"Error in ladder lookup"
    
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
        
        # Calculate rolling sums for all time slots
        current_sum = sum(self.time_slot_histories[self.current_time])
        
        # Find the best performing "other" time slot (highest rolling sum among non-current slots)
        other_times = [t for t in self.available_times if t != self.current_time]
        if not other_times:
            # Only one time slot available, no time change possible
            return self.current_time, f"Stay {self.current_time} (only one time slot available)"
        
        # Get the best other time slot
        other_time_sums = {t: sum(self.time_slot_histories.get(t, [])) for t in other_times}
        other_time = max(other_time_sums, key=other_time_sums.get)
        other_sum = other_time_sums[other_time]
        
        # Update time slot tracking for current time
        self.time_slot_points[self.current_time] = current_score
        self.time_slot_rolling[self.current_time] = current_sum
        
        # Debug output for CL data after 2020 - disabled once issue is resolved
        # if hasattr(self, 'last_processed_date') and self.last_processed_date and self.last_processed_date >= pd.Timestamp('2021-01-01'):
        #     print(f"   🎯 Time Change - Current slot {self.current_time} updated:")
        #     print(f"      Result: {result}")
        #     print(f"      Score: {current_score}")
        #     print(f"      Rolling sum: {current_sum}")
        #     print(f"      Points set to: {current_score}")
        
        # Update tracking for all other time slots
        for t in other_times:
            self.time_slot_rolling[t] = other_time_sums[t]
        
        # For the best other time slot, get the SAME DATE data (like the main analyzer does)
        # This gives us a proper comparison of performance on the same date
        current_date = self.last_processed_date
        # Determine the correct session for the other time slot
        other_session = self._get_session_for_time(other_time)
        other_data = self.get_data_for_date_and_time(current_date, other_time, other_session)
        
        if other_data and other_data['Date'] != 'NO DATA':
            # Use actual other time slot data
            other_result = other_data['Result']
            other_score = self._calculate_time_score(other_result)
            
            # Add to other time slot's history
            self.time_slot_histories[other_time].append(other_score)
            
            # Keep only last 13 trades for other slot
            if len(self.time_slot_histories[other_time]) > 13:
                self.time_slot_histories[other_time] = self.time_slot_histories[other_time][-13:]
            
            # Recalculate other slot's rolling sum
            other_sum = sum(self.time_slot_histories[other_time])
            self.time_slot_rolling[other_time] = other_sum
            self.time_slot_points[other_time] = other_score
            
            # Debug output for CL data after 2020 - disabled once issue is resolved
            # if hasattr(self, 'last_processed_date') and self.last_processed_date and self.last_processed_date >= pd.Timestamp('2021-01-01'):
            #     print(f"   ✅ Time Change - Other slot {other_time} calculated:")
            #     print(f"      Result: {other_result}")
            #     print(f"      Score: {other_score}")
            #     print(f"      Rolling sum: {other_sum}")
            #     print(f"      Points set to: {other_score}")
        else:
            # No data for other time slot - keep current score
            other_sum = sum(self.time_slot_histories[other_time]) if other_time in self.time_slot_histories else 0
            self.time_slot_rolling[other_time] = other_sum
            # print(f"   ❌ No data found for other time slot - keeping rolling sum: {other_sum}")
        
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
    
    def _calculate_target_adjusted_score(self, result: str, time_slot: str) -> float:
        """Calculate target-adjusted score for time change logic"""
        # Get base result score
        base_score = self._calculate_time_score(result)
        
        # Get current target for this time slot
        current_target = self.time_slot_targets[time_slot]
        base_target = self.BASE_TARGETS.get("ES", 10)
        
        # Calculate target multiplier
        target_multiplier = current_target / base_target
        
        # Return adjusted score
        return base_score * target_multiplier
    
    def update_time_slot_target(self, time_slot: str, peak: float) -> Tuple[int, str]:
        """Update target for a specific time slot (for target_adjusted mode)"""
        if time_slot not in self.time_slot_targets:
            return self.time_slot_targets[time_slot], "Invalid time slot"
        
        base = self.BASE_TARGETS.get("ES", 10)
        tick = self.TICK_SIZE.get("ES", 0.25)
        ladder = self.get_ladder(base)
        
        # Round peak to tick
        peak = self.round_down_to_tick(peak, tick)
        ignore_threshold = self.round_down_to_tick(base * 0.95, tick)
        
        current_target = self.time_slot_targets[time_slot]
        current_streak = self.time_slot_streaks[time_slot]
        
        # Ignore rule
        if peak < ignore_threshold:
            return current_target, f"Ignored (<95% base={ignore_threshold})"
        
        # Startup rule (ONLY when at base)
        if current_target == base:
            if peak >= 2 * base:
                current_streak += 1
                if current_streak >= 3:
                    try:
                        current_idx = ladder.index(current_target)
                        if current_idx + 1 < len(ladder):
                            new_target = int(ladder[current_idx + 1])
                            current_streak = 0
                            self.time_slot_targets[time_slot] = new_target
                            self.time_slot_streaks[time_slot] = 0
                            return new_target, f"Startup promotion → {new_target}"
                    except ValueError:
                        pass
                self.time_slot_streaks[time_slot] = current_streak
                return current_target, f"Startup streak {current_streak}/3"
            else:
                self.time_slot_streaks[time_slot] = 0
                return current_target, f"Reset streak (<2× base)"
        
        # Demotion rule
        try:
            current_idx = ladder.index(current_target)
            if current_idx + 1 < len(ladder):
                next_rung = ladder[current_idx + 1]
                if peak < next_rung:
                    if peak < 2 * base:
                        # Demote to base
                        self.time_slot_streaks[time_slot] = 0
                        self.time_slot_targets[time_slot] = base
                        return base, f"Demotion to base (peak < 2×base)"
                    else:
                        # Demote one level
                        if current_idx > 0:
                            new_target = int(ladder[current_idx - 1])
                        else:
                            new_target = base
                        self.time_slot_streaks[time_slot] = 0
                        self.time_slot_targets[time_slot] = new_target
                        return new_target, f"Demotion → {new_target}"
        except ValueError:
            pass
        
        # Promotion rule
        try:
            current_idx = ladder.index(current_target)
            if current_idx >= len(ladder) - 2:
                self.time_slot_streaks[time_slot] = 0
                return current_target, f"At cap (no promotion)"
            
            if current_idx + 1 < len(ladder):
                next_rung = ladder[current_idx + 1]
                if current_idx + 2 < len(ladder):
                    required_peak = ladder[current_idx + 2]
                else:
                    required_peak = next_rung
                
                if peak >= required_peak:
                    current_streak += 1
                    if current_streak >= 3:
                        self.time_slot_streaks[time_slot] = 0
                        self.time_slot_targets[time_slot] = int(next_rung)
                        return int(next_rung), f"Promotion → {int(next_rung)}"
                    self.time_slot_streaks[time_slot] = current_streak
                    return current_target, f"Streak {current_streak}/3 (need ≥ {int(required_peak)})"
                else:
                    self.time_slot_streaks[time_slot] = 0
                    return current_target, f"Reset streak (< {int(required_peak)})"
            else:
                self.time_slot_streaks[time_slot] = 0
                return current_target, f"At cap (no promotion)"
        except ValueError:
            self.time_slot_streaks[time_slot] = 0
            return current_target, f"Error in ladder lookup"
    
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
    
    def process_sequential(self, max_days: int = 10000, enable_time_change: bool = True):
        """Process data sequentially with time change logic"""
        print("=" * 100)
        print("SEQUENTIAL TIME CHANGE PROCESSOR")
        print("=" * 100)
        print(f"Starting with: Time={self.current_time}")
        print(f"Time Change Mode: {'ENABLED' if enable_time_change else 'DISABLED'}")
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
                print(f"⚠️ No more data available. Stopping at day {day-1}")
                break
            
            # Store current state before changes
            old_time = self.current_time
            
            # Apply time change logic
            if enable_time_change and data_row['Result'] != 'NO DATA':
                new_time, time_reason = self.update_time_change(data_row['Result'])
                if new_time != old_time:
                    self.current_time = new_time
                    self.current_session = self._get_session_for_time(new_time)  # Update session to match new time
                    print(f"⏰ TIME CHANGE: {old_time} → {new_time} ({time_reason})")
                else:
                    print(f"⏰ Time: {time_reason}")
            else:
                time_reason = "Time change disabled"
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
    parser.add_argument('--time-change', action='store_true', default=True, help='Enable time change mode')
    parser.add_argument('--no-time-change', action='store_true', help='Disable time change mode')
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
    
    # Determine modes
    enable_time_change = args.time_change and not args.no_time_change
    
    # Process with specified modes
    results = processor.process_sequential(
        max_days=args.max_days, 
        enable_time_change=enable_time_change
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
    
    # Save results with timestamp
    timestamp = datetime.now().strftime('%Y%m%d_%H%M%S')
    filename = f'{args.output_folder}/sequential_run_{timestamp}.csv'
    
    # Ensure directory exists
    os.makedirs(args.output_folder, exist_ok=True)
    
    results.to_csv(filename, index=False)
    print(f"\nResults saved to: {filename}")
    
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
