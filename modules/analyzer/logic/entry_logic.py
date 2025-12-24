"""
Entry Logic Module
Handles trade entry detection and validation
"""

import pandas as pd
from typing import Optional, Tuple
from dataclasses import dataclass
from .loss_logic import LossManager, StopLossConfig

@dataclass
class EntryResult:
    """Result of entry detection"""
    entry_direction: Optional[str]  # "Long", "Short", or None
    entry_price: Optional[float]
    entry_time: Optional[pd.Timestamp]
    immediate_entry: bool
    breakout_time: Optional[pd.Timestamp]

class EntryDetector:
    """Handles trade entry detection and validation"""
    
    def __init__(self, loss_config: StopLossConfig = None):
        """
        Initialize entry detector
        
        Args:
            loss_config: Loss management configuration
        """
        self.loss_manager = LossManager(loss_config)
    
    def detect_entry(self, df: pd.DataFrame, range_result, 
                    brk_long: float, brk_short: float,
                    freeze_close: float, end_ts: pd.Timestamp) -> EntryResult:
        """
        Detect trade entry using simple breakout detection
        
        Args:
            df: DataFrame with OHLCV data
            range_result: Range calculation result
            brk_long: Long breakout level
            brk_short: Short breakout level
            freeze_close: Freeze close price
            end_ts: Range end timestamp
            
        Returns:
            EntryResult object with entry details
        """
        try:
            # Get data after range period (including after market close for detection)
            post = df[df["timestamp"] >= end_ts].copy()
            
            if post.empty:
                return EntryResult(None, None, None, False, None)
            
            # Define market close time (16:00) for the same day as the slot
            market_close = end_ts.replace(hour=16, minute=0, second=0, microsecond=0)
            
            # Check for immediate entry conditions
            immediate_long = freeze_close >= brk_long
            immediate_short = freeze_close <= brk_short
            
            if immediate_long and immediate_short:
                # Both conditions met - use first breakout by timestamp
                return self._handle_dual_immediate_entry(freeze_close, brk_long, brk_short)
            elif immediate_long:
                return EntryResult("Long", brk_long, end_ts, True, end_ts)
            elif immediate_short:
                return EntryResult("Short", brk_short, end_ts, True, end_ts)
            
            # Find first breakout after range period
            long_breakout = post[post["high"] >= brk_long]
            short_breakout = post[post["low"] <= brk_short]
            
            long_time = long_breakout["timestamp"].min() if not long_breakout.empty else None
            short_time = short_breakout["timestamp"].min() if not short_breakout.empty else None
            
            # Check if breakouts happened after market close (16:00)
            long_after_close = long_time is not None and long_time > market_close
            short_after_close = short_time is not None and short_time > market_close
            
            # Filter out breakouts that happened after market close
            valid_long_time = long_time if not long_after_close else None
            valid_short_time = short_time if not short_after_close else None
            
            # If no valid breakouts before market close, return NoTrade
            if valid_long_time is None and valid_short_time is None:
                return EntryResult("NoTrade", None, None, False, None)
            
            # First valid breakout wins (only considering those before market close)
            if valid_long_time is not None and (valid_short_time is None or valid_long_time < valid_short_time):
                return EntryResult("Long", brk_long, valid_long_time, False, valid_long_time)
            elif valid_short_time is not None and (valid_long_time is None or valid_short_time < valid_long_time):
                return EntryResult("Short", brk_short, valid_short_time, False, valid_short_time)
            else:
                # No valid breakouts found - this is NoTrade
                return EntryResult("NoTrade", None, None, False, None)
                
        except Exception as e:
            print(f"Error detecting entry: {e}")
            return EntryResult(None, None, None, False, None)
    
    def _handle_dual_immediate_entry(self, freeze_close: float, 
                                   brk_long: float, brk_short: float) -> EntryResult:
        """
        Handle case where both immediate entry conditions are met
        
        Args:
            freeze_close: Freeze close price
            brk_long: Long breakout level
            brk_short: Short breakout level
            
        Returns:
            EntryResult for the chosen direction
        """
        # Calculate distances to breakout levels
        long_distance = abs(freeze_close - brk_long)
        short_distance = abs(freeze_close - brk_short)
        
        # Choose the closer breakout level
        if long_distance <= short_distance:
            return EntryResult("Long", brk_long, None, True, None)
        else:
            return EntryResult("Short", brk_short, None, True, None)
    
    def validate_entry(self, entry_result: EntryResult, 
                      min_range_size: float = 5.0) -> bool:
        """
        Validate if entry meets requirements
        
        Args:
            entry_result: Entry detection result
            min_range_size: Minimum range size requirement
            
        Returns:
            True if entry is valid, False otherwise
        """
        if not entry_result.entry_direction:
            return False
        
        # Add any additional validation logic here
        return True
    
    def get_entry_time(self, entry_result: EntryResult, 
                      end_ts: pd.Timestamp) -> pd.Timestamp:
        """
        Get the actual entry time for the trade
        
        Args:
            entry_result: Entry detection result
            end_ts: Range end timestamp
            
        Returns:
            Actual entry time
        """
        if entry_result.immediate_entry:
            return end_ts
        else:
            return entry_result.entry_time
    
    def calculate_target_level(self, entry_price: float, direction: str, 
                              target_pts: float) -> float:
        """
        Calculate target level for the trade
        
        Args:
            entry_price: Trade entry price
            direction: Trade direction ("Long" or "Short")
            target_pts: Target points
            
        Returns:
            Target level price
        """
        if direction == "Long":
            return entry_price + target_pts
        else:
            return entry_price - target_pts
    
    def calculate_stop_loss(self, entry_price: float, direction: str, 
                           target_pts: float, instrument: str = "ES", 
                           range_size: float = None, range_high: float = None, 
                           range_low: float = None) -> float:
        """
        Calculate initial stop loss for the trade
        
        Args:
            entry_price: Trade entry price
            direction: Trade direction ("Long" or "Short")
            target_pts: Target points
            instrument: Trading instrument
            range_size: Range size to limit stop loss distance (legacy)
            range_high: Range high price (preferred)
            range_low: Range low price (preferred)
            
        Returns:
            Initial stop loss price
        """
        return self.loss_manager.calculate_initial_stop_loss(
            entry_price, direction, target_pts, instrument, range_size, 
            range_high, range_low
        )
    
    def _should_not_trade(self, range_result, freeze_close: float, end_ts: pd.Timestamp) -> bool:
        """
        Check if trading should be avoided - only check for after-hours breakouts
        
        Args:
            range_result: Range calculation result
            freeze_close: Freeze close price
            end_ts: Range end timestamp
            
        Returns:
            True if should not trade, False if OK to trade
        """
        # This method is now only used for the simple no-trade logic
        # The main after-hours check is done in detect_entry method
        return False  # OK to trade
