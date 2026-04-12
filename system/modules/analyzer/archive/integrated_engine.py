"""
Integrated Engine Example
Shows how to use the modular logic components together
"""

import pandas as pd
from typing import List, Dict, Any
from dataclasses import dataclass

# Import all logic modules
from .range_logic import RangeDetector, RangeResult
# MFE and break even logic now integrated into PriceTracker
from .entry_logic import EntryDetector, EntryResult
from .price_tracking_logic import PriceTracker, TradeExecution
from logic.config_logic import RunParams

@dataclass
class TradeResult:
    """Complete trade result"""
    date: pd.Timestamp
    time: str
    target: float
    peak: float
    direction: str
    result: str
    range_size: float
    stream: str
    instrument: str
    session: str
    profit: float

class IntegratedTradingEngine:
    """Integrated trading engine using modular components"""
    
    def __init__(self, slot_config: Dict):
        """
        Initialize integrated engine with all components
        
        Args:
            slot_config: Slot configuration dictionary
        """
        self.range_detector = RangeDetector(slot_config)
        self.entry_detector = EntryDetector()
        self.price_tracker = PriceTracker()
        self.slot_config = slot_config
    
    def run_strategy(self, df: pd.DataFrame, params: RunParams) -> List[TradeResult]:
        """
        Run the complete trading strategy using modular components
        
        Args:
            df: DataFrame with OHLCV data
            params: Run parameters
            
        Returns:
            List of TradeResult objects
        """
        results = []
        
        # Get unique dates
        dates = df["timestamp"].dt.date.unique()
        
        for date in dates:
            date_ts = pd.Timestamp(date)
            day_data = df[df["timestamp"].dt.date == date_ts.date].copy()
            
            # Process each enabled slot
            for session in params.enabled_sessions:
                if session not in params.enabled_slots:
                    continue
                
                for time_label in params.enabled_slots[session]:
                    # Calculate range
                    range_result = self.range_detector.calculate_range(
                        day_data, date_ts, time_label, session
                    )
                    
                    if not range_result:
                        continue
                    
                    # Calculate breakout levels (always 1 tick above/below range)
                    brk_long, brk_short = self.range_detector.calculate_breakout_levels(
                        range_result
                    )
                    
                    # Detect entry
                    entry_result = self.entry_detector.detect_entry(
                        day_data, range_result, brk_long, brk_short,
                        range_result.freeze_close, range_result.end_time
                    )
                    
                    if not entry_result.entry_direction:
                        continue
                    
                    # Get base target points
                    from logic.config_logic import ConfigManager
                    config_manager = ConfigManager()
                    target_pts = config_manager.get_base_target(params.instrument)
                    
                    # Calculate target level and stop loss
                    target_level = self.entry_detector.calculate_target_level(
                        entry_result.entry_price, entry_result.entry_direction, target_pts
                    )
                    stop_loss = self.entry_detector.calculate_stop_loss(
                        entry_result.entry_price, entry_result.entry_direction, target_pts,
                        "ES", range_result.range_size, range_result.range_high, range_result.range_low
                    )
                    
                    # Get expiry time
                    expiry_time = self.price_tracker.get_expiry_time(
                        date_ts, time_label, session
                    )
                    
                    # Execute trade
                    entry_time = self.entry_detector.get_entry_time(
                        entry_result, range_result.end_time
                    )
                    
                    # Execute trade with integrated MFE and break even logic
                    trade_execution = self.price_tracker.execute_trade(
                        day_data, entry_time, entry_result.entry_price,
                        entry_result.entry_direction, target_level, stop_loss, expiry_time,
                        target_pts, 0, params.instrument, time_label, date_ts,
                        False, params.cap_t1_trigger_to_base, params.custom_trigger_config
                    )
                    
                    
                    # Calculate profit using the integrated logic
                    profit = self.price_tracker.calculate_profit(
                        entry_result.entry_price, trade_execution.exit_price,
                        entry_result.entry_direction, trade_execution.result_classification,
                        trade_execution.t1_triggered, 
                        target_pts, 0, params.instrument, False
                    )
                    
                    # Create result
                    trade_result = TradeResult(
                        date=date_ts,
                        time=time_label,
                        target=target_pts,
                        peak=trade_execution.peak,
                        direction=entry_result.entry_direction,
                        result=trade_execution.result_classification,
                        range_size=range_result.range_size,
                        stream=params.instrument,
                        instrument=params.instrument,
                        session=session,
                        profit=profit
                    )
                    
                    results.append(trade_result)
        
        return results
    

# Example usage
if __name__ == "__main__":
    # This would be used to replace the current engine.py
    # with a more modular approach
    pass


