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
    
    def __init__(self, instrument_manager=None):
        """
        Initialize break-even manager
        
        Args:
            instrument_manager: InstrumentManager instance for profit calculations
        """
        self.instrument_manager = instrument_manager
    
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
            if not self.instrument_manager:
                raise ValueError("InstrumentManager required for stop loss adjustment")
            
            tick_size = self.instrument_manager.get_tick_size(instrument.upper())
            
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
                        target_pts: float, instrument: str = "ES",
                        use_display_profit: bool = False) -> float:
        """
        Calculate profit based on result and trigger
        
        This method now delegates to InstrumentManager.calculate_profit() for unified logic.
        
        Args:
            entry_price: Trade entry price
            exit_price: Trade exit price
            direction: Trade direction ("Long" or "Short")
            result: Trade result ("Win", "BE", "Loss")
            t1_triggered: Whether T1 trigger is activated
            target_pts: Target points (unscaled)
            instrument: Trading instrument
            use_display_profit: If True, returns display profit (ES equivalent for micro-futures)
            
        Returns:
            Calculated profit
        """
        if not self.instrument_manager:
            raise ValueError("InstrumentManager required for profit calculation")
        
        # Use unified profit calculation from InstrumentManager
        # Note: BreakEvenManager uses BE=0 logic, not BE=-tick_size
        if result == "BE":
            return 0.0
        
        return self.instrument_manager.calculate_profit(
            entry_price=entry_price,
            exit_price=exit_price,
            direction=direction,
            result=result,
            t1_triggered=t1_triggered,
            target_pts=target_pts,
            instrument=instrument.upper(),
            use_display_profit=use_display_profit
        )
    
    # get_trigger_thresholds moved to price_tracking_logic.py with cap functionality
