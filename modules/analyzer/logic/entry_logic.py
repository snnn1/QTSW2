"""
Entry Logic Module
Handles trade entry detection and validation
"""

import pandas as pd
import logging
import sys
from typing import Optional, Tuple
from dataclasses import dataclass
from .loss_logic import LossManager, StopLossConfig
from .config_logic import ConfigManager

logger = logging.getLogger(__name__)

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
    
    def __init__(self, loss_config: StopLossConfig = None, debug: bool = False,
                 config_manager: ConfigManager = None, instrument_manager=None):
        """
        Initialize entry detector
        
        Args:
            loss_config: Loss management configuration
            debug: Enable debug output for entry detection
            config_manager: Configuration manager for market close time
            instrument_manager: InstrumentManager instance for instrument-specific calculations
        """
        self.loss_manager = LossManager(loss_config, instrument_manager=instrument_manager)
        self.debug = debug
        self.config_manager = config_manager or ConfigManager()
        self.instrument_manager = instrument_manager
    
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
            # Convert end_ts to pandas Timestamp if it's not already
            end_ts_pd = pd.Timestamp(end_ts) if not isinstance(end_ts, pd.Timestamp) else end_ts
            
            # Check for immediate entry conditions FIRST (before checking post data)
            # This allows immediate entries even when post data is empty
            immediate_long = freeze_close >= brk_long
            immediate_short = freeze_close <= brk_short
            
            if immediate_long and immediate_short:
                # Both conditions met - use first breakout by timestamp
                result = self._handle_dual_immediate_entry(freeze_close, brk_long, brk_short, end_ts_pd)
                # Set entry_time and breakout_time to end_ts for immediate entries
                return EntryResult(result.entry_direction, result.entry_price, end_ts_pd, True, end_ts_pd)
            elif immediate_long:
                return EntryResult("Long", brk_long, end_ts_pd, True, end_ts_pd)
            elif immediate_short:
                return EntryResult("Short", brk_short, end_ts_pd, True, end_ts_pd)
            
            # Get data after range period (including after market close for detection)
            # Ensure timestamp column is pandas Timestamp type for comparison
            if len(df) > 0 and 'timestamp' in df.columns:
                # Convert timestamp column to pandas Timestamp if needed
                df_timestamps = pd.to_datetime(df["timestamp"])
                post = df[df_timestamps >= end_ts_pd].copy()
            else:
                post = pd.DataFrame(columns=df.columns if not df.empty else ['timestamp', 'open', 'high', 'low', 'close'])
            
            if post.empty:
                return EntryResult("NoTrade", None, None, False, None)
            
            # Define market close time (configurable via ConfigManager) for the same day as the slot
            # CRITICAL: Create market_close timestamp properly preserving timezone
            market_close_time_str = self.config_manager.get_market_close_time()  # e.g., "16:00"
            market_close_hour, market_close_minute = map(int, market_close_time_str.split(":"))
            if end_ts_pd.tz is not None:
                # Timezone-aware: create market close timestamp in same timezone
                date_str = end_ts_pd.strftime('%Y-%m-%d')
                market_close = pd.Timestamp(f"{date_str} {market_close_hour:02d}:{market_close_minute:02d}:00", tz=end_ts_pd.tz)
            else:
                # Naive timestamp: use replace directly
                market_close = end_ts_pd.replace(hour=market_close_hour, minute=market_close_minute, second=0, microsecond=0)
            
            # Find first breakout after range period
            long_breakout = post[post["high"] >= brk_long]
            short_breakout = post[post["low"] <= brk_short]
            
            long_time = long_breakout["timestamp"].min() if not long_breakout.empty else None
            short_time = short_breakout["timestamp"].min() if not short_breakout.empty else None
            
            # Check if breakouts happened after market close
            long_after_close = long_time is not None and long_time > market_close
            short_after_close = short_time is not None and short_time > market_close
            
            # Filter out breakouts that happened after market close
            valid_long_time = long_time if not long_after_close else None
            valid_short_time = short_time if not short_after_close else None
            
            # DEBUG: Log entry detection details for troubleshooting (when debug enabled)
            # Note: Detailed debug output can be enabled via debug parameter in EntryDetector
            # This debug block has been removed - use debug logging instead of hardcoded date checks
            
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
            error_msg = f"Error detecting entry: {e}"
            logger.error(error_msg, exc_info=True)
            print(error_msg, file=sys.stderr, flush=True)
            return EntryResult(None, None, None, False, None)
    
    def _handle_dual_immediate_entry(self, freeze_close: float, 
                                   brk_long: float, brk_short: float,
                                   end_ts_pd: pd.Timestamp) -> EntryResult:
        """
        Handle case where both immediate entry conditions are met
        
        Args:
            freeze_close: Freeze close price
            brk_long: Long breakout level
            brk_short: Short breakout level
            end_ts_pd: Range end timestamp (pandas Timestamp)
            
        Returns:
            EntryResult for the chosen direction with valid timestamps
        """
        # Calculate distances to breakout levels
        long_distance = abs(freeze_close - brk_long)
        short_distance = abs(freeze_close - brk_short)
        
        # Choose the closer breakout level
        if long_distance <= short_distance:
            return EntryResult("Long", brk_long, end_ts_pd, True, end_ts_pd)
        else:
            return EntryResult("Short", brk_short, end_ts_pd, True, end_ts_pd)
    
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
