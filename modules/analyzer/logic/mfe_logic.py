"""
MFE (Maximum Favorable Excursion) Calculation Logic Module
Handles calculation of maximum favorable movement for trades
"""

import pandas as pd
from typing import Optional, Tuple
from dataclasses import dataclass

@dataclass
class MFEResult:
    """Result of MFE calculation"""
    peak: float
    peak_time: pd.Timestamp
    peak_price: float
    t1_triggered: bool

class MFECalculator:
    """Handles MFE calculation for trades"""
    
    def __init__(self, t1_threshold: float):
        """
        Initialize MFE calculator with trigger threshold
        
        Args:
            t1_threshold: T1 trigger threshold (e.g., 6.5 for 65% of 10-point target)
        """
        self.t1_threshold = t1_threshold
    
    def calculate_mfe(self, df: pd.DataFrame, entry_time: pd.Timestamp, 
                     entry_price: float, direction: str, 
                     time_label: str, date: pd.Timestamp, 
                     original_stop_loss: float = None) -> MFEResult:
        """
        Calculate MFE from entry until next time slot OR until original stop loss is hit (whichever comes first)
        
        Args:
            df: DataFrame with OHLCV data
            entry_time: Trade entry time
            entry_price: Trade entry price
            direction: Trade direction ("Long" or "Short")
            time_label: Time slot (e.g., "08:00")
            date: Trading date
            original_stop_loss: Original stop loss level (optional)
            
        Returns:
            MFEResult object with MFE details
        """
        try:
            # Determine peak calculation end time (next time slot)
            peak_end_time = self._get_peak_end_time(date, time_label)
            
            # Get bars for MFE calculation
            peak_bars = df[(df["timestamp"] >= entry_time) & 
                          (df["timestamp"] < peak_end_time)].copy()
            
            if peak_bars.empty:
                return MFEResult(0.0, entry_time, entry_price, False, False)
            
            # Calculate MFE (Maximum Favorable Excursion)
            max_favorable = 0.0
            peak_time = entry_time
            peak_price = entry_price
            
            for _, bar in peak_bars.iterrows():
                high, low = float(bar["high"]), float(bar["low"])
                
                # Check if original stop loss was hit (if provided)
                if original_stop_loss is not None:
                    stop_hit = False
                    if direction == "Long":
                        # For long trades, stop loss is below entry
                        stop_hit = low <= original_stop_loss
                    else:
                        # For short trades, stop loss is above entry
                        stop_hit = high >= original_stop_loss
                    
                    if stop_hit:
                        # Stop MFE calculation when original stop loss is hit
                        break
                
                if direction == "Long":
                    # Long trade: favorable = high - entry_price
                    current_favorable = high - entry_price
                    if current_favorable > max_favorable:
                        max_favorable = current_favorable
                        peak_time = bar["timestamp"]
                        peak_price = high
                else:
                    # Short trade: favorable = entry_price - low
                    current_favorable = entry_price - low
                    if current_favorable > max_favorable:
                        max_favorable = current_favorable
                        peak_time = bar["timestamp"]
                        peak_price = low
            
            # Check T1 trigger
            t1_triggered = max_favorable >= self.t1_threshold
            
            return MFEResult(
                peak=max_favorable,
                peak_time=peak_time,
                peak_price=peak_price,
                t1_triggered=t1_triggered
            )
            
        except Exception as e:
            print(f"Error calculating MFE: {e}")
            return MFEResult(0.0, entry_time, entry_price, False)
    
    def _get_peak_end_time(self, date: pd.Timestamp, time_label: str) -> pd.Timestamp:
        """
        Calculate peak end time (next time slot)
        
        Args:
            date: Trading date
            time_label: Time slot (e.g., "08:00")
            
        Returns:
            Peak end timestamp
        """
        if date.weekday() == 4:  # Friday
            # Peak continues to Monday same slot
            days_ahead = 3  # Friday to Monday
            peak_end_time = date + pd.Timedelta(days=days_ahead)
        else:
            # Regular day - peak continues to next day same slot
            peak_end_time = date + pd.Timedelta(days=1)
        
        # Set the time to the same slot next day
        hour_part = int(time_label.split(":")[0])
        minute_part = int(time_label.split(":")[1])
        peak_end_time = peak_end_time.replace(
            hour=hour_part, 
            minute=minute_part, 
            second=0
        )
        
        return peak_end_time
    
    def get_trigger_threshold(self, target_pts: float, instrument: str = "ES") -> float:
        """
        Get T1 trigger threshold (always base level - 65% of target)
        
        Args:
            target_pts: Target points
            instrument: Trading instrument to get base target
            
        Returns:
            T1 threshold
        """
        # Base level: Always 65% of target
        t1_threshold = target_pts * 0.65  # 65% of target
        
        return t1_threshold
