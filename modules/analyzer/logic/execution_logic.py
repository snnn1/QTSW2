"""
Execution Simulation Logic Module
Handles intra-bar execution simulation to determine which level (target/stop) is hit first
"""

import pandas as pd
import numpy as np
from typing import Optional, Dict


class ExecutionSimulator:
    """Handles execution simulation for trade execution"""
    
    def __init__(self):
        """Initialize execution simulator"""
        pass
    
    def simulate_intra_bar_execution(self, bar: pd.Series, entry_price: float, 
                                    direction: str, target_level: float, 
                                    stop_loss: float, entry_time: pd.Timestamp, 
                                    t1_triggered: bool = False,
                                    t1_triggered_previous_bar: bool = False) -> Optional[Dict]:
        """
        Use actual historical data to determine which level is hit first
        
        This method uses real historical price data to determine whether the target 
        or stop loss is hit first, providing the most accurate representation.
        
        Args:
            bar: OHLC bar data
            entry_price: Trade entry price
            direction: Trade direction
            target_level: Target level
            stop_loss: Stop loss level
            entry_time: Entry timestamp
            t1_triggered: Whether T1 is triggered in current bar
            t1_triggered_previous_bar: Whether T1 was triggered in previous bar
            
        Returns:
            Dictionary with execution results, or None if no execution
        """
        high, low, open_price, close_price = float(bar["high"]), float(bar["low"]), float(bar["open"]), float(bar["close"])
        
        # Determine if target and stop are both possible in this bar
        target_possible = False
        stop_possible = False
        
        if direction == "Long":
            target_possible = high >= target_level
            stop_possible = low <= stop_loss
        else:
            target_possible = low <= target_level
            stop_possible = high >= stop_loss
        
        # If target is possible, always use close price analysis to determine execution
        # This ensures realistic results even when stop doesn't reach the low
        if target_possible:
            # Always analyze actual price movement when target is possible
            # This ensures we get realistic execution results based on close price analysis
            return self._analyze_actual_price_movement(
                bar, entry_price, direction, target_level, stop_loss, entry_time
            )
        
        # If only stop is possible, check stop loss (unless T1 just triggered in this bar)
        elif stop_possible:
            if t1_triggered and not t1_triggered_previous_bar:
                # T1 just triggered in this bar - don't check stop in same bar
                return None
            else:
                # T1 triggered in previous bar or no triggers - check stop loss
                return {
                    "exit_price": stop_loss,  # Exact stop loss level
                    "exit_time": entry_time,
                    "exit_reason": "Loss",
                    "target_hit": False,
                    "stop_hit": True,
                    "time_expired": False
                }
        
        # Neither target nor stop hit in this bar
        return None
    
    def _analyze_actual_price_movement(self, bar: pd.Series, entry_price: float,
                                     direction: str, target_level: float,
                                     stop_loss: float, entry_time: pd.Timestamp) -> Dict:
        """
        Analyze actual historical price movement to determine which level is hit first
        
        This method uses real historical data to determine whether the target or stop loss
        is hit first, providing the most accurate representation of what actually happened.
        
        Args:
            bar: OHLC bar data
            entry_price: Trade entry price
            direction: Trade direction
            target_level: Target level
            stop_loss: Stop loss level
            entry_time: Entry timestamp
            
        Returns:
            Dictionary with execution results based on actual price movement
        """
        high, low, open_price, close_price = float(bar["high"]), float(bar["low"]), float(bar["open"]), float(bar["close"])
        
        if direction == "Long":
            # For long trades, check if target hits first
            if self._target_hits_first_long(open_price, high, low, close_price, target_level, stop_loss):
                return {
                    "exit_price": target_level,
                    "exit_time": entry_time,
                    "exit_reason": "Win",
                    "target_hit": True,
                    "stop_hit": False,
                    "time_expired": False
                }
            else:
                return {
                    "exit_price": stop_loss,
                    "exit_time": entry_time,
                    "exit_reason": "Loss",
                    "target_hit": False,
                    "stop_hit": True,
                    "time_expired": False
                }
        else:  # Short
            # For short trades, check if target hits first
            if self._target_hits_first_short(open_price, high, low, close_price, target_level, stop_loss):
                return {
                    "exit_price": target_level,
                    "exit_time": entry_time,
                    "exit_reason": "Win",
                    "target_hit": True,
                    "stop_hit": False,
                    "time_expired": False
                }
            else:
                return {
                    "exit_price": stop_loss,
                    "exit_time": entry_time,
                    "exit_reason": "Loss",
                    "target_hit": False,
                    "stop_hit": True,
                    "time_expired": False
                }
    
    def _target_hits_first_long(self, open_price: float, high: float, low: float, 
                              close: float, target_level: float, stop_loss: float) -> bool:
        """
        Determine if target hits first for long trades using actual price movement
        
        Args:
            open_price: Bar open price
            high: Bar high price
            low: Bar low price
            close: Bar close price
            target_level: Target level
            stop_loss: Stop loss level
            
        Returns:
            True if target hits first, False if stop hits first
        """
        # If price opens above target, target hits immediately
        if open_price >= target_level:
            return True
        
        # If price opens below stop, stop hits immediately
        if open_price <= stop_loss:
            return False
        
        # If price opens between target and stop, analyze movement
        if high >= target_level and low <= stop_loss:
            # Both are hit - use price tracking to determine which hits first
            # For long trades: if close is closer to target, target wins; if closer to stop, stop wins
            target_distance = abs(close - target_level)
            stop_distance = abs(close - stop_loss)
            
            if target_distance <= stop_distance:
                # Close is closer to target - target hits first
                return True
            else:
                # Close is closer to stop - stop hits first
                return False
        
        # If only target is hit
        if high >= target_level:
            return True
        
        # If only stop is hit
        if low <= stop_loss:
            return False
        
        # Neither hit
        return False
    
    def _target_hits_first_short(self, open_price: float, high: float, low: float, 
                               close: float, target_level: float, stop_loss: float) -> bool:
        """
        Determine if target hits first for short trades using actual price movement
        
        Args:
            open_price: Bar open price
            high: Bar high price
            low: Bar low price
            close: Bar close price
            target_level: Target level
            stop_loss: Stop loss level
            
        Returns:
            True if target hits first, False if stop hits first
        """
        # If price opens below target, target hits immediately
        if open_price <= target_level:
            return True
        
        # If price opens above stop, stop hits immediately
        if open_price >= stop_loss:
            return False
        
        # If price opens between target and stop, analyze movement
        if low <= target_level and high >= stop_loss:
            # Both are hit - use price tracking to determine which hits first
            # For short trades: if close is closer to target, target wins; if closer to stop, stop wins
            target_distance = abs(close - target_level)
            stop_distance = abs(close - stop_loss)
            
            if target_distance <= stop_distance:
                # Close is closer to target - target hits first
                return True
            else:
                # Close is closer to stop - stop hits first
                return False
        
        # If only target is hit
        if low <= target_level:
            return True
        
        # If only stop is hit
        if high >= stop_loss:
            return False
        
        # Neither hit
        return False

