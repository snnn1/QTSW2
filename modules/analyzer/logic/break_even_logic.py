"""
Break-Even Logic Module
Handles T1 trigger system and stop loss adjustments
"""

from typing import Tuple, Optional
from dataclasses import dataclass

@dataclass
class BreakEvenResult:
    """Result of break-even logic"""
    t1_triggered: bool
    stop_loss_adjusted: bool
    new_stop_loss: Optional[float]
    result_classification: str
    profit_adjustment: float

class BreakEvenManager:
    """Handles break-even trigger system and stop loss management"""
    
    def __init__(self):
        """Initialize break-even manager"""
        pass
    
    def check_trigger(self, peak: float, t1_threshold: float) -> bool:
        """
        Check if T1 trigger is activated
        
        Args:
            peak: Current peak value
            t1_threshold: T1 trigger threshold
            
        Returns:
            Whether T1 is triggered
        """
        t1_triggered = peak >= t1_threshold
        
        return t1_triggered
    
    def adjust_stop_loss(self, entry_price: float, direction: str, 
                        t1_triggered: bool,
                        target_pts: float, instrument: str = "ES") -> Tuple[float, str]:
        """
        Adjust stop loss based on trigger
        
        Args:
            entry_price: Trade entry price
            direction: Trade direction ("Long" or "Short")
            t1_triggered: Whether T1 trigger is activated
            target_pts: Target points
            instrument: Trading instrument (for special cases like GC)
            
        Returns:
            Tuple of (new_stop_loss, adjustment_reason)
        """
        if t1_triggered:
            # T1 triggered: Move stop loss to break-even for all instruments
            # All instruments now use normal break-even behavior (including GC)
            from breakout_core.config import TICK_SIZE
            tick_size = TICK_SIZE.get(instrument.upper(), 0.25)  # Default to ES tick size
            
            if direction == "Long":
                new_sl = entry_price - tick_size  # 1 tick below entry for long trades
            else:
                new_sl = entry_price + tick_size  # 1 tick above entry for short trades
            
            return new_sl, "T1_BREAK_EVEN"
        
        else:
            # No triggers: Keep original stop loss
            return None, "NO_TRIGGER"
    
    def classify_result(self, t1_triggered: bool,
                       exit_reason: str, target_hit: bool = False) -> str:
        """
        Classify trade result based on trigger and exit reason
        
        Args:
            t1_triggered: Whether T1 trigger is activated
            exit_reason: Reason for trade exit ("Win", "Loss", "TIME")
            target_hit: Whether target was hit
            
        Returns:
            Result classification ("Win", "BE", "Loss")
        """
        if target_hit or exit_reason == "Win":
            return "Win"
        
        # Handle time expiry first - this should override trigger status
        if exit_reason == "TIME":
            return "TIME"
        
        if t1_triggered:
            return "BE"   # T1 triggered = Break Even
        else:
            return "Loss" # No triggers hit = Loss
    
    def calculate_profit(self, entry_price: float, exit_price: float,
                        direction: str, result: str, 
                        t1_triggered: bool,
                        target_pts: float) -> float:
        """
        Calculate profit based on result and trigger
        
        Args:
            entry_price: Trade entry price
            exit_price: Trade exit price
            direction: Trade direction ("Long" or "Short")
            result: Trade result ("Win", "BE", "Loss")
            t1_triggered: Whether T1 trigger is activated
            target_pts: Target points
            
        Returns:
            Calculated profit
        """
        if result == "Win":
            # Win trades: Use target profit
            return target_pts
        elif result == "BE":
            # Break-even trades: 0 profit
            return 0.0
        else:
            # Loss trades: Calculate actual PnL
            if direction == "Long":
                pnl_pts = exit_price - entry_price
            else:
                pnl_pts = entry_price - exit_price
            
            # For MES display purposes, multiply losses by 10 to show ES equivalent
            if instrument.startswith("M") and pnl_pts < 0:
                return pnl_pts * 10.0
            else:
                return pnl_pts
    
    # get_trigger_thresholds moved to price_tracking_logic.py with cap functionality
