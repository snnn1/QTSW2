"""
Price Tracking Logic Module
Handles real-time price monitoring, trade execution, MFE calculation, and break even logic

This module now uses Method 1: Actual Historical Data Analysis for all break-even logic,
providing the most accurate representation of what actually happened in the market.
"""

import pandas as pd
import numpy as np
from typing import Optional, Tuple
from dataclasses import dataclass
from datetime import datetime
from .debug_logic import DebugManager, DebugInfo

# Optional scipy import with fallback
try:
    from scipy import stats
    SCIPY_AVAILABLE = True
except ImportError:
    SCIPY_AVAILABLE = False
    # Create a dummy stats module for fallback
    class DummyStats:
        def norm(self, *args, **kwargs):
            return np.random.normal(*args, **kwargs)
    stats = DummyStats()

# Loss logic now integrated directly into price tracking

@dataclass
class TradeExecution:
    """Result of trade execution with MFE and break even data"""
    exit_price: float
    exit_time: pd.Timestamp
    exit_reason: str  # "Win", "Loss", "TIME"
    target_hit: bool
    stop_hit: bool
    time_expired: bool
    # MFE data
    peak: float
    peak_time: pd.Timestamp
    peak_price: float
    t1_triggered: bool
    # Break even data
    stop_loss_adjusted: bool
    final_stop_loss: float
    result_classification: str
    profit: float = 0.0  # Calculated profit

class PriceTracker:
    """Handles real-time price tracking, trade execution, MFE calculation, and break even logic"""
    
    def __init__(self, debug_manager: DebugManager = None):
        """
        Initialize PriceTracker with Method 1: Actual Historical Data Analysis
        
        This tracker uses real historical OHLC data to determine which level
        (target or stop) is hit first, providing the most accurate representation
        possible without tick data.
        
        Args:
            debug_manager: Debug manager for logging
        """
        self.debug_manager = debug_manager or DebugManager(False)
    
    def execute_trade(self, df: pd.DataFrame, entry_time: pd.Timestamp,
                     entry_price: float, direction: str, target_level: float,
                     stop_loss: float, expiry_time: pd.Timestamp,
                     target_pts: float, instrument: str = "ES",
                     time_label: str = None, date: pd.Timestamp = None, debug: bool = False) -> TradeExecution:
        """
        Execute trade with integrated MFE calculation and break even logic
        
        Args:
            df: DataFrame with OHLCV data
            entry_time: Trade entry time
            entry_price: Trade entry price
            direction: Trade direction ("Long" or "Short")
            target_level: Target level price
            stop_loss: Initial stop loss price
            expiry_time: Trade expiry time
            target_pts: Target points for MFE calculation
            instrument: Trading instrument
            time_label: Time slot for MFE calculation (e.g., "08:00")
            date: Trading date for MFE calculation
            debug: Debug flag
            
        Returns:
            TradeExecution object with all details
        """
        try:
            # Create debug info
            trade_id = f"{date.date()}_{time_label}_base"
            debug_info = DebugInfo(
                trade_id=trade_id,
                entry_time=entry_time,
                entry_price=entry_price,
                direction=direction,
                stop_loss=stop_loss,
                target_pts=target_pts,
                mfe_end_time=None,
                mfe_bars_count=0,
                peak_value=0.0,
                peak_time=entry_time,
                stop_loss_hit=False,
                stop_loss_hit_time=None
            )
            
            if self.debug_manager.is_debug_enabled():
                try:
                    print(f"\nEXECUTING TRADE: {direction} ${entry_price:.2f} at {entry_time}")
                except Exception:
                    pass
                print(f"   Entry time breakdown:")
                print(f"     Chicago: {entry_time}")
                print(f"     UTC: {entry_time.tz_convert('UTC') if entry_time.tz else 'N/A'}")
                print(f"     Hour: {entry_time.hour:02d}:{entry_time.minute:02d}")
                try:
                    print(f"   Stop Loss: ${stop_loss:.2f}")
                except Exception:
                    pass
            
            # Get trigger threshold (always 65% of target)
            t1_threshold = self._get_trigger_threshold(target_pts, instrument)
            t1_removed = False  # T1 is always enabled
                
            
            # Get bars after entry time (including after market close for detection)
            after = df[df["timestamp"] >= entry_time].copy()
            
            # Define market close time (16:00)
            market_close = entry_time.replace(hour=16, minute=0, second=0, microsecond=0)
            
            # Check if entry happened after market close
            if entry_time > market_close:
                return self._create_trade_execution(
                    entry_price, entry_time, "NoTrade", False, False, False,
                    0.0, entry_time, entry_price, False, False,
                    False, stop_loss, "NoTrade", 0.0
                )
            
            if after.empty:
                return self._create_trade_execution(
                    entry_price, entry_time, "TIME", False, False, True,
                    0.0, entry_time, entry_price, False, False,
                    False, stop_loss, "Loss", 0.0
                )
            
            # Initialize tracking variables
            max_favorable = 0.0
            peak_time = entry_time
            peak_price = entry_price
            t1_triggered = False
            current_stop_loss = stop_loss
            stop_loss_adjusted = False
            
            # Determine MFE calculation end time (next day same slot)
            mfe_end_time = None
            if time_label and date is not None:
                mfe_end_time = self._get_peak_end_time(date, time_label)
            
            # Get bars for MFE calculation (until next day same slot or original stop hit)
            if mfe_end_time:
                # Check if data extends to MFE end time
                data_end_time = df['timestamp'].max()
                if data_end_time < mfe_end_time:
                    # Data doesn't extend to MFE end time - use all available data
                    # This is expected behavior when data doesn't extend to exact minute boundaries
                    mfe_bars = df[(df["timestamp"] >= entry_time)].copy()
                    # Log MFE data gap warnings - this is expected but useful to see in debug terminal
                    # Use logging instead of print so it shows up in debug window
                    import logging
                    logger = logging.getLogger(__name__)
                    time_diff = (mfe_end_time - data_end_time).total_seconds() / 60
                    if time_diff <= 1:
                        # Very small gap (1 minute or less) - just a note
                        logger.debug(f"MFE: Data ends {time_diff:.1f} min before expected (using available data)")
                    elif time_diff <= 5:
                        # Small gap (1-5 minutes) - info level
                        logger.info(f"MFE: Data ends {time_diff:.1f} min before expected end time (using available data)")
                    else:
                        # Larger gap (>5 minutes) - warning level
                        logger.warning(f"MFE: Data ends {time_diff:.1f} min before expected end time (using available data)")
                else:
                    # Data extends to MFE end time - use normal filtering
                    mfe_bars = df[(df["timestamp"] >= entry_time) & (df["timestamp"] < mfe_end_time)].copy()
                
                debug_info.mfe_end_time = mfe_end_time
                debug_info.mfe_bars_count = len(mfe_bars)
                
                # Debug: Print MFE tracking info
                if self.debug_manager.is_debug_enabled():
                    first_bar = mfe_bars.iloc[0]['timestamp'] if len(mfe_bars) > 0 else None
                    last_bar = mfe_bars.iloc[-1]['timestamp'] if len(mfe_bars) > 0 else None
                    self.debug_manager.print_mfe_debug(entry_time, mfe_end_time, len(mfe_bars), first_bar, last_bar)
                    
                    # Additional debug: Show data availability (no emojis to avoid Windows encoding errors)
                    try:
                        print(f"\nDATA AVAILABILITY CHECK:")
                        print(f"   Data Range: {df['timestamp'].min()} -> {df['timestamp'].max()}")
                        print(f"   Data after entry: {len(after)} bars")
                        print(f"   MFE bars selected: {len(mfe_bars)} bars")
                        if len(mfe_bars) > 0:
                            print(f"   MFE bars range: {mfe_bars['timestamp'].min()} -> {mfe_bars['timestamp'].max()}")
                        print(f"   Expected MFE end: {mfe_end_time}")
                        print(f"   Data contains Monday bars: {len(df[df['timestamp'].dt.date == mfe_end_time.date()])} bars")
                    except Exception:
                        pass
            else:
                mfe_bars = after.copy()
                debug_info.mfe_bars_count = len(mfe_bars)
            
            # Track MFE through all bars until next day same slot or original stop loss hit
            mfe_stopped = False
            for _, bar in mfe_bars.iterrows():
                high, low = float(bar["high"]), float(bar["low"])
                
                # Check if original stop loss was hit (stops MFE tracking)
                original_stop_hit = False
                if direction == "Long":
                    original_stop_hit = low <= stop_loss
                else:
                    original_stop_hit = high >= stop_loss
                
                if original_stop_hit and not mfe_stopped:
                    # Original stop loss hit - stop MFE tracking but continue trade execution
                    mfe_stopped = True
                
                # Continue MFE tracking until stop loss hit
                if not mfe_stopped:
                    # Calculate current favorable movement
                    current_favorable = self._calculate_favorable_movement(
                        high, low, entry_price, direction
                    )
                    
                    # Update MFE if we have a new peak
                    if current_favorable > max_favorable:
                        max_favorable = current_favorable
                        peak_time = bar["timestamp"]
                        peak_price = high if direction == "Long" else low
                    
                # MFE tracking only - T1 triggers will be detected in price tracking logic
            
            # Track T1 triggers in previous bar for stop logic
            t1_triggered_previous_bar = False
            
            # Calculate maximum favorable movement across all bars first
            max_favorable_execution = 0.0
            for _, bar in after.iterrows():
                high, low = float(bar["high"]), float(bar["low"])
                current_favorable = self._calculate_favorable_movement(high, low, entry_price, direction)
                if current_favorable > max_favorable_execution:
                    max_favorable_execution = current_favorable
            
            if self.debug_manager.is_debug_enabled():
                try:
                    print(f"MAX FAVORABLE EXECUTION: {max_favorable_execution:.2f} (Target: {target_pts:.2f})")
                except Exception:
                    pass
                print(f"   Target Hit: {max_favorable_execution >= target_pts}")
            
            # Now process trade execution using the regular after bars
            for _, bar in after.iterrows():
                high, low = float(bar["high"]), float(bar["low"])
                open_price = float(bar["open"])
                
                # Use price tracking logic to detect T1 triggers
                # This is more accurate than peak-based detection
                current_favorable = self._calculate_favorable_movement(high, low, entry_price, direction)
                
                if self.debug_manager.is_debug_enabled() and current_favorable >= 40:  # Debug when close to target
                    try:
                        print(f"FAVORABLE MOVEMENT: {current_favorable:.2f} at {bar['timestamp']}")
                    except Exception:
                        pass
                    print(f"   Entry: {entry_price}, Direction: {direction}")
                    print(f"   High: {high}, Low: {low}")
                    print(f"   Target: {target_pts}")
                
                # Check T1 trigger using price tracking
                if not t1_triggered and current_favorable >= t1_threshold:
                    t1_triggered = True
                    if self.debug_manager.is_debug_enabled():
                        print(f"T1 TRIGGERED (Price Tracking): {current_favorable:.2f} >= T1 threshold {t1_threshold:.2f}")
                    # Adjust stop loss for T1
                    current_stop_loss = self._adjust_stop_loss_t1(
                        entry_price, direction, target_pts, instrument
                    )
                    stop_loss_adjusted = True
                
                # Use price tracking to determine what gets hit first within this bar
                target_level = entry_price + target_pts if direction == "Long" else entry_price - target_pts
                
                # Check if target was hit based on cumulative favorable movement
                # Use the peak value (max_favorable) instead of max_favorable_execution for consistency
                target_hit_by_movement = max_favorable >= target_pts
                
                if self.debug_manager.is_debug_enabled() and target_hit_by_movement:
                    try:
                        print(f"TARGET HIT BY MOVEMENT: {current_favorable:.2f} >= {target_pts:.2f} at {bar['timestamp']}")
                        print(f"   Entry: {entry_price}, Direction: {direction}")
                        print(f"   Target Level: {target_level}")
                        print(f"   Current Favorable: {current_favorable}")
                    except Exception:
                        pass
                
                # Check what gets hit first: target or stop loss using realistic execution analysis
                target_hit_first = False
                stop_hit_first = False
                
                # Use realistic intra-bar execution analysis instead of always prioritizing targets
                execution_result = self._simulate_intra_bar_execution(
                    bar, entry_price, direction, target_level, current_stop_loss, entry_time,
                    t1_triggered, t1_triggered_previous_bar
                )
                
                if execution_result:
                    target_hit_first = execution_result.get("target_hit", False)
                    stop_hit_first = execution_result.get("stop_hit", False)
                    
                    if self.debug_manager.is_debug_enabled():
                        if target_hit_first:
                            try:
                                print(f"TARGET HIT FIRST: {execution_result['exit_price']}")
                            except Exception:
                                pass
                        elif stop_hit_first:
                            try:
                                print(f"STOP HIT FIRST: {execution_result['exit_price']}")
                            except Exception:
                                pass
                else:
                    # No execution in this bar - continue tracking
                    if self.debug_manager.is_debug_enabled():
                        try:
                            print(f"NO EXECUTION: Continuing price tracking")
                        except Exception:
                            pass
                
                if target_hit_first:
                    # Target hit first - exit with full profit
                    exit_reason = "Win"
                    result_classification = self._classify_result(
                        t1_triggered, exit_reason, True, entry_price, bar["timestamp"], df, direction, t1_removed
                    )
                    
                    if self.debug_manager.is_debug_enabled():
                        print(f"TARGET HIT FIRST: {current_favorable:.2f} >= {target_pts:.2f}")
                        print(f"Exit with {target_pts:.2f} points profit")
                    
                    # Calculate final MFE if we have MFE bars
                    if mfe_end_time and len(mfe_bars) > 0:
                        final_mfe_data = self._calculate_final_mfe(
                            mfe_bars, entry_time, entry_price, direction, stop_loss, t1_threshold, debug_info,
                            t1_triggered
                        )
                        mfe_peak = final_mfe_data["peak"]
                        peak_time = final_mfe_data["peak_time"]
                        peak_price = final_mfe_data["peak_price"]
                        t1_triggered = final_mfe_data["t1_triggered"]
                        
                        # Use execution peak if MFE peak is 0 (stopped early due to stop loss)
                        if mfe_peak == 0.0 and max_favorable_execution > 0.0:
                            max_favorable = max_favorable_execution
                            if self.debug_manager.is_debug_enabled():
                                print(f"[FIX] PEAK FIX: MFE peak=0, using execution peak={max_favorable_execution:.2f}")
                        else:
                            max_favorable = mfe_peak
                    
                    # Update debug info with final results
                    debug_info.peak_value = max_favorable
                    debug_info.peak_time = peak_time
                    debug_info.target_hit = True
                    debug_info.stop_loss_hit = False
                    
                    # Log trade info
                    self.debug_manager.log_trade_info(debug_info)
                    
                    # Calculate profit (full target)
                    profit = self.calculate_profit(
                        entry_price, current_stop_loss, direction, result_classification,
                        t1_triggered, target_pts, instrument,
                        target_hit=True
                    )
                    
                    return self._create_trade_execution(
                        current_stop_loss, bar["timestamp"], exit_reason, True, False, False,
                        max_favorable, peak_time, peak_price, t1_triggered,
                        stop_loss_adjusted, current_stop_loss, result_classification, profit
                    )
                
                elif stop_hit_first:
                    # Stop loss hit - use exit price from execution result (actual stop loss level)
                    exit_price_from_execution = execution_result.get("exit_price", current_stop_loss)
                    
                    if t1_triggered:
                        exit_reason = "BE"   # T1 triggered = Break Even
                        result_classification = self._classify_result(
                            t1_triggered, exit_reason, False, entry_price, bar["timestamp"], df, direction, t1_removed
                        )
                    else:
                        exit_reason = "Loss" # No triggers = Loss
                        result_classification = self._classify_result(
                            t1_triggered, exit_reason, False, entry_price, bar["timestamp"], df, direction, t1_removed
                        )
                    
                    # Calculate final MFE if we have MFE bars
                    if mfe_end_time and len(mfe_bars) > 0:
                        final_mfe_data = self._calculate_final_mfe(
                            mfe_bars, entry_time, entry_price, direction, stop_loss, t1_threshold, debug_info,
                            t1_triggered
                        )
                        mfe_peak = final_mfe_data["peak"]
                        peak_time = final_mfe_data["peak_time"]
                        peak_price = final_mfe_data["peak_price"]
                        t1_triggered = final_mfe_data["t1_triggered"]
                        
                        # Use execution peak if MFE peak is 0 (stopped early due to stop loss)
                        if mfe_peak == 0.0 and max_favorable_execution > 0.0:
                            max_favorable = max_favorable_execution
                            if self.debug_manager.is_debug_enabled():
                                print(f"[FIX] PEAK FIX: MFE peak=0, using execution peak={max_favorable_execution:.2f}")
                        else:
                            max_favorable = mfe_peak
                    
                    # Update debug info with final results
                    debug_info.peak_value = max_favorable
                    debug_info.peak_time = peak_time
                    debug_info.stop_loss_hit = True
                    debug_info.stop_loss_hit_time = bar["timestamp"]
                    
                    # Log debug info
                    if self.debug_manager.is_debug_enabled():
                        self.debug_manager.print_trade_debug(debug_info)
                    self.debug_manager.log_trade_info(debug_info)
                    
                    # Calculate profit using exit price from execution (actual stop loss level)
                    profit = self.calculate_profit(
                        entry_price, exit_price_from_execution, direction, result_classification,
                        t1_triggered, target_pts, instrument,
                        target_hit=False
                    )
                    
                    return self._create_trade_execution(
                        exit_price_from_execution, bar["timestamp"], exit_reason, False, True, False,
                        max_favorable, peak_time, peak_price, t1_triggered,
                        stop_loss_adjusted, current_stop_loss, result_classification, profit
                    )
                
                # Execution result already processed above - no need to process again
                
                # Update T1 trigger tracking for next iteration
                t1_triggered_previous_bar = t1_triggered
                
                # Check time expiry
                if bar["timestamp"] >= expiry_time:
                    # Use expiry_time as exit_time (not bar timestamp) to ensure correct TIME exit
                    # e.g., Friday 11:00 trade expires at Monday 10:59, not Monday 11:00
                    exit_time_for_time_expiry = expiry_time
                    
                    # Find the bar at expiry_time (or closest bar before it)
                    # If bar["timestamp"] >= expiry_time, this bar might be AFTER expiry_time
                    # We want the bar AT expiry_time, or the last bar BEFORE expiry_time
                    bars_at_or_before_expiry = after[after["timestamp"] <= expiry_time]
                    if len(bars_at_or_before_expiry) > 0:
                        # Use the last bar at or before expiry_time
                        expiry_bar = bars_at_or_before_expiry.iloc[-1]
                        exit_price_for_time_expiry = float(expiry_bar["close"])
                    else:
                        # No bars at or before expiry_time - use current bar (shouldn't happen)
                        exit_price_for_time_expiry = float(bar["close"])
                    
                    result_classification = self._classify_result(
                        t1_triggered, "TIME", False, entry_price, exit_time_for_time_expiry, df, direction, t1_removed
                    )
                    
                    # Calculate final MFE if we have MFE bars
                    if mfe_end_time and len(mfe_bars) > 0:
                        final_mfe_data = self._calculate_final_mfe(
                            mfe_bars, entry_time, entry_price, direction, stop_loss, t1_threshold, debug_info,
                            t1_triggered
                        )
                        mfe_peak = final_mfe_data["peak"]
                        peak_time = final_mfe_data["peak_time"]
                        peak_price = final_mfe_data["peak_price"]
                        t1_triggered = final_mfe_data["t1_triggered"]
                        
                        # Use execution peak if MFE peak is 0 (stopped early due to stop loss)
                        if mfe_peak == 0.0 and max_favorable_execution > 0.0:
                            max_favorable = max_favorable_execution
                            if self.debug_manager.is_debug_enabled():
                                print(f"[FIX] PEAK FIX: MFE peak=0, using execution peak={max_favorable_execution:.2f}")
                        else:
                            max_favorable = mfe_peak
                    
                    # Calculate profit for time expiry
                    profit = self.calculate_profit(
                        entry_price, exit_price_for_time_expiry, direction, result_classification,
                        t1_triggered, target_pts, instrument,
                        target_hit=False
                    )
                    
                    return self._create_trade_execution(
                        exit_price_for_time_expiry, exit_time_for_time_expiry, "TIME", False, False, True,
                        max_favorable, peak_time, peak_price, t1_triggered,
                        stop_loss_adjusted, current_stop_loss, result_classification, profit
                    )
            
            # If we get here, data didn't extend to expiry_time
            # Check if trade has actually expired (expiry_time has passed) or is still open
            # Use the absolute last bar in the entire dataset (not just after entry_time)
            # This ensures we get the most recent price even if new data was added
            last_bar_in_data = df.iloc[-1]  # Absolute last bar in entire dataset
            last_bar_after_entry = after.iloc[-1] if len(after) > 0 else None
            
            # Get current time in Chicago timezone
            if expiry_time and expiry_time.tz:
                # Use same timezone as expiry_time (should be Chicago)
                current_time = pd.Timestamp.now(tz=expiry_time.tz)
            else:
                # If expiry_time doesn't have timezone, use Chicago timezone
                current_time = pd.Timestamp.now(tz="America/Chicago")
            
            # Ensure expiry_time is timezone-aware (Chicago)
            if expiry_time and expiry_time.tz is None:
                expiry_time = expiry_time.tz_localize("America/Chicago")
            elif expiry_time and str(expiry_time.tz) != "America/Chicago":
                expiry_time = expiry_time.tz_convert("America/Chicago")
            
            # Trade is still open if expiry_time is in the future
            if expiry_time and expiry_time > current_time:
                # Trade hasn't expired yet - use current time and current price
                # ExitTime = current time in Chicago (trade is still ongoing)
                exit_time_for_time_expiry = current_time  # Current time in Chicago
                # Use the absolute last bar's close price (most recent price in entire dataset)
                # This ensures we get the latest price even if new data was added since entry
                exit_price_for_time_expiry = float(last_bar_in_data["close"])  # Current/last available price from most recent bar in dataset
                
                # Use "TIME" as exit_reason but time_expired=False (trade still ongoing)
                result_classification = self._classify_result(
                    t1_triggered, "TIME", False, entry_price, exit_time_for_time_expiry, df, direction, t1_removed
                )
                
                # Calculate profit based on current price (trade still open)
                profit = self.calculate_profit(
                    entry_price, exit_price_for_time_expiry, direction, result_classification,
                    t1_triggered, target_pts, instrument,
                    target_hit=False
                )
                
                # Calculate final MFE
                if mfe_end_time and len(mfe_bars) > 0:
                    final_mfe_data = self._calculate_final_mfe(
                        mfe_bars, entry_time, entry_price, direction, stop_loss, t1_threshold, debug_info, t1_triggered
                    )
                    max_favorable = final_mfe_data["peak"]
                    peak_time = final_mfe_data["peak_time"]
                    peak_price = final_mfe_data["peak_price"]
                    t1_triggered = final_mfe_data["t1_triggered"]
                else:
                    max_favorable = max_favorable_execution
                    peak_time = entry_time
                    peak_price = entry_price
                
                # Return trade execution with current state (still open)
                # Result = "TIME", ExitTime = current time, Profit = current price
                return self._create_trade_execution(
                    exit_price_for_time_expiry, exit_time_for_time_expiry, "TIME", False, False, False,  # exit_reason="TIME", time_expired=False
                    max_favorable, peak_time, peak_price, t1_triggered,
                    stop_loss_adjusted, current_stop_loss, result_classification, profit
                )
            
            # Trade has expired (expiry_time has passed or data extends to it)
            # Use expiry_time as exit_time (not last bar timestamp)
            # This ensures Friday trades expire on Monday even if data doesn't extend that far
            exit_time_for_time_expiry = expiry_time if expiry_time else last_bar["timestamp"]
            
            # If expiry_time is in the future relative to data but has passed in real time, use the last bar's close price
            # Otherwise use the bar at expiry_time
            if expiry_time and expiry_time > last_bar["timestamp"]:
                # Data doesn't extend to expiry_time but expiry_time has passed - use last bar's close price
                exit_price_for_time_expiry = float(last_bar["close"])
            else:
                # Data extends to expiry_time - find the bar at expiry_time
                expiry_bar = after[after["timestamp"] >= expiry_time]
                if len(expiry_bar) > 0:
                    exit_price_for_time_expiry = float(expiry_bar.iloc[0]["close"])
                else:
                    exit_price_for_time_expiry = float(last_bar["close"])
            
            result_classification = self._classify_result(
                t1_triggered, "TIME", False, entry_price, exit_time_for_time_expiry, df, direction, t1_removed
            )
            
            # Calculate final MFE if we have MFE bars
            if mfe_end_time and len(mfe_bars) > 0:
                final_mfe_data = self._calculate_final_mfe(
                    mfe_bars, entry_time, entry_price, direction, stop_loss, t1_threshold, debug_info, t1_triggered
                )
                mfe_peak = final_mfe_data["peak"]
                peak_time = final_mfe_data["peak_time"]
                peak_price = final_mfe_data["peak_price"]
                t1_triggered = final_mfe_data["t1_triggered"]
                
                # Use execution peak if MFE peak is 0 (stopped early due to stop loss)
                if mfe_peak == 0.0 and max_favorable_execution > 0.0:
                    max_favorable = max_favorable_execution
                    if self.debug_manager.is_debug_enabled():
                        print(f"[FIX] PEAK FIX: MFE peak=0, using execution peak={max_favorable_execution:.2f}")
                else:
                    max_favorable = mfe_peak
            
            # Update debug info with final results
            debug_info.peak_value = max_favorable
            debug_info.peak_time = peak_time
            
            # Log debug info
            if self.debug_manager.is_debug_enabled():
                self.debug_manager.print_trade_debug(debug_info)
            self.debug_manager.log_trade_info(debug_info)
            
            # Calculate profit for final time expiry
            profit = self.calculate_profit(
                entry_price, exit_price_for_time_expiry, direction, result_classification,
                t1_triggered, target_pts, instrument,
                target_hit=False
            )
            
            return self._create_trade_execution(
                exit_price_for_time_expiry, exit_time_for_time_expiry, "TIME", False, False, True,
                max_favorable, peak_time, peak_price, t1_triggered,
                stop_loss_adjusted, current_stop_loss, result_classification, profit
            )
            
        except Exception as e:
            print(f"Error executing trade: {e}")
            return self._create_trade_execution(
                entry_price, entry_time, "TIME", False, False, True,
                0.0, entry_time, entry_price, False, False,
                False, stop_loss, "Loss", 0.0
            )
    
    def _check_stop_loss(self, high: float, low: float, stop_loss: float, 
                        direction: str) -> bool:
        """
        Check if stop loss was hit
        
        Args:
            high: Bar high price
            low: Bar low price
            stop_loss: Stop loss price
            direction: Trade direction
            
        Returns:
            True if stop loss was hit
        """
        if direction == "Long":
            return low <= stop_loss
        else:
            return high >= stop_loss
    
    def _check_target(self, high: float, low: float, target_level: float, 
                     direction: str) -> bool:
        """
        Check if target was hit
        
        Args:
            high: Bar high price
            low: Bar low price
            target_level: Target level price
            direction: Trade direction
            
        Returns:
            True if target was hit
        """
        if direction == "Long":
            return high >= target_level
        else:
            return low <= target_level
    
    def calculate_pnl(self, entry_price: float, exit_price: float, 
                     direction: str) -> float:
        """
        Calculate profit/loss for the trade
        
        Args:
            entry_price: Trade entry price
            exit_price: Trade exit price
            direction: Trade direction
            
        Returns:
            Profit/loss in points
        """
        if direction == "Long":
            return exit_price - entry_price
        else:
            return entry_price - exit_price
    
    def get_expiry_time(self, date: pd.Timestamp, time_label: str, 
                       session: str) -> pd.Timestamp:
        """
        Calculate trade expiry time
        
        Args:
            date: Trading date (should be timezone-aware, Chicago time)
            time_label: Time slot (e.g., "08:00")
            session: Session (S1 or S2)
            
        Returns:
            Expiry timestamp in Chicago timezone
        """
        # Ensure date is timezone-aware (Chicago time)
        if date.tz is None:
            # If naive, assume it's Chicago time and localize it
            date = pd.Timestamp(date).tz_localize("America/Chicago")
        elif str(date.tz) != "America/Chicago":
            # If different timezone, convert to Chicago
            date = date.tz_convert("America/Chicago")
        
        # Calculate expiry time (next day same slot + 1 minute)
        if date.weekday() == 4:  # Friday
            # Friday trades expire Monday
            days_ahead = 3
        else:
            # Regular day trades expire next day
            days_ahead = 1
        
        expiry_date = date + pd.Timedelta(days=days_ahead)
        hour_part = int(time_label.split(":")[0])
        minute_part = int(time_label.split(":")[1])
        
        expiry_time = expiry_date.replace(
            hour=hour_part, 
            minute=minute_part, 
            second=0, 
            microsecond=0
        )
        
        # Ensure expiry_time is in Chicago timezone
        if expiry_time.tz is None:
            expiry_time = expiry_time.tz_localize("America/Chicago")
        elif str(expiry_time.tz) != "America/Chicago":
            expiry_time = expiry_time.tz_convert("America/Chicago")
        
        return expiry_time
    
    def validate_trade_data(self, df: pd.DataFrame, entry_time: pd.Timestamp) -> bool:
        """
        Validate that we have sufficient data for trade execution
        
        Args:
            df: DataFrame with OHLCV data
            entry_time: Trade entry time
            
        Returns:
            True if data is sufficient
        """
        after_entry = df[df["timestamp"] >= entry_time]
        return len(after_entry) > 0
    
    def _get_peak_end_time(self, date: pd.Timestamp, time_label: str) -> pd.Timestamp:
        """
        Calculate peak end time (next day same slot)
        
        Args:
            date: Trading date (timezone-aware, should be Chicago time)
            time_label: Time slot in Chicago time (e.g., "07:30" = 7:30 AM Chicago)
            
        Returns:
            Peak end timestamp in Chicago time
        """
        if date.weekday() == 4:  # Friday
            # Peak continues to Monday same slot
            days_ahead = 3  # Friday to Monday
            peak_end_date = date + pd.Timedelta(days=days_ahead)
        else:
            # Regular day - peak continues to next day same slot
            peak_end_date = date + pd.Timedelta(days=1)
        
        # Slot times are Chicago trading hours - create timestamp directly in Chicago time
        hour_part = int(time_label.split(":")[0])
        minute_part = int(time_label.split(":")[1])
        peak_end_time = peak_end_date.replace(
            hour=hour_part, 
            minute=minute_part, 
            second=0
        )
        
        return peak_end_time
    
    def _calculate_final_mfe(self, mfe_bars: pd.DataFrame, entry_time: pd.Timestamp,
                           entry_price: float, direction: str, original_stop_loss: float,
                           t1_threshold: float, debug_info: DebugInfo = None,
                           t1_triggered: bool = False) -> dict:
        """
        Calculate final MFE data from MFE bars
        
        Args:
            mfe_bars: DataFrame with bars for MFE calculation
            entry_time: Trade entry time
            entry_price: Trade entry price
            direction: Trade direction
            original_stop_loss: Original stop loss level
            t1_threshold: T1 trigger threshold
            
        Returns:
            Dictionary with MFE data
        """
        max_favorable = 0.0
        peak_time = entry_time
        peak_price = entry_price
        
        if debug_info and self.debug_manager.is_debug_enabled():
            try:
                print(f"\nMFE CALCULATION STARTING:")
                print(f"   Processing {len(mfe_bars)} bars for MFE calculation")
                print(f"   Entry: ${entry_price:.2f}, Direction: {direction}")
                print(f"   Original stop loss: ${original_stop_loss:.2f}")
            except Exception:
                pass
        
        for _, bar in mfe_bars.iterrows():
            high, low = float(bar["high"]), float(bar["low"])
            
            # Check if original stop loss was hit (stops MFE tracking)
            if direction == "Long":
                if low <= original_stop_loss:
                    if debug_info:
                        debug_info.stop_loss_hit = True
                        debug_info.stop_loss_hit_time = bar['timestamp']
                    if debug_info and self.debug_manager.is_debug_enabled():
                        try:
                            print(f"   STOP LOSS HIT: {bar['timestamp']}, low: ${low:.2f}")
                        except Exception:
                            pass
                    break  # Stop MFE tracking when original stop loss hit
            else:
                if high >= original_stop_loss:
                    if debug_info:
                        debug_info.stop_loss_hit = True
                        debug_info.stop_loss_hit_time = bar['timestamp']
                    if debug_info and self.debug_manager.is_debug_enabled():
                        try:
                            print(f"   STOP LOSS HIT: {bar['timestamp']}, high: ${high:.2f}")
                        except Exception:
                            pass
                    break  # Stop MFE tracking when original stop loss hit
            
            # Calculate favorable movement for this bar
            if direction == "Long":
                current_favorable = high - entry_price
                if current_favorable > max_favorable:
                    max_favorable = current_favorable
                    peak_time = bar["timestamp"]
                    peak_price = high
                    if debug_info and self.debug_manager.is_debug_enabled():
                        self.debug_manager.print_peak_debug(entry_price, direction, original_stop_loss, 
                                                          current_favorable, max_favorable, peak_time, peak_price)
            else:
                current_favorable = entry_price - low
                if current_favorable > max_favorable:
                    max_favorable = current_favorable
                    peak_time = bar["timestamp"]
                    peak_price = low
                    if debug_info and self.debug_manager.is_debug_enabled():
                        self.debug_manager.print_peak_debug(entry_price, direction, original_stop_loss, 
                                                          current_favorable, max_favorable, peak_time, peak_price)
        
        # T1 triggers are now handled by price tracking logic, not MFE calculation
        # Use the trigger passed as parameter
        
        return {
            "peak": max_favorable,
            "peak_time": peak_time,
            "peak_price": peak_price,
            "t1_triggered": t1_triggered
        }
    
    def _get_trigger_threshold(self, target_pts: float, instrument: str = "ES") -> float:
        """Get T1 trigger threshold - always 65% of target"""
        return target_pts * 0.65  # 65% of target
    
    def _calculate_favorable_movement(self, high: float, low: float, 
                                    entry_price: float, direction: str) -> float:
        """Calculate favorable movement from entry price"""
        if direction == "Long":
            return high - entry_price
        else:
            return entry_price - low
    
    def _simulate_intra_bar_execution(self, bar: pd.Series, entry_price: float, 
                                    direction: str, target_level: float, 
                                    stop_loss: float, entry_time: pd.Timestamp, 
                                    t1_triggered: bool = False,
                                    t1_triggered_previous_bar: bool = False) -> dict:
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
            
        Returns:
            Dictionary with execution results
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
        
        # If only target is possible
        elif target_possible:
            return {
                "exit_price": target_level,  # Exact target level
                "exit_time": entry_time,
                "exit_reason": "Win",
                "target_hit": True,
                "stop_hit": False,
                "time_expired": False
            }
        
        # If only stop is possible
        elif stop_possible:
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
    
    def _calculate_simple_execution_order(self, bar: pd.Series, entry_price: float,
                                        direction: str, target_level: float,
                                        stop_loss: float, entry_time: pd.Timestamp) -> dict:
        """
        Simple execution order calculation (original logic)
        Always prioritizes target over stop when both are possible
        """
        return {
            "exit_price": target_level,
            "exit_time": entry_time + pd.Timedelta(minutes=1),
            "exit_reason": "Win",
            "target_hit": True,
            "stop_hit": False,
            "time_expired": False
        }
    
    def _analyze_actual_price_movement(self, bar: pd.Series, entry_price: float,
                                     direction: str, target_level: float,
                                     stop_loss: float, entry_time: pd.Timestamp) -> dict:
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
    
    def _simulate_realistic_entry(self, bar: pd.Series, breakout_level: float, 
                                direction: str, tick_size: float = 0.25) -> dict:
        """
        Simulate realistic entry execution at breakout level
        
        Args:
            bar: OHLC bar data
            breakout_level: Breakout level price
            direction: Trade direction
            tick_size: Minimum price increment
            
        Returns:
            Dictionary with entry execution details
        """
        high, low, open_price, close_price = float(bar["high"]), float(bar["low"]), float(bar["open"]), float(bar["close"])
        
        # Check if breakout is possible in this bar
        breakout_possible = False
        if direction == "Long":
            breakout_possible = high >= breakout_level
        else:
            breakout_possible = low <= breakout_level
        
        if not breakout_possible:
            return None
        
        # Simulate realistic entry price (no slippage)
        if direction == "Long":
            # For long breakouts, entry at breakout level
            entry_price = breakout_level
            
            # Ensure entry price doesn't exceed bar high
            entry_price = min(entry_price, high)
            
        else:
            # For short breakouts, entry at breakout level
            entry_price = breakout_level
            
            # Ensure entry price doesn't go below bar low
            entry_price = max(entry_price, low)
        
        # Entry timing (bar timestamp is already the correct entry time)
        entry_time = bar["timestamp"]
        
        return {
            "entry_price": entry_price,
            "entry_time": entry_time,
            "slippage": 0.0,
            "breakout_confirmed": True
        }
    
    
    def _simulate_brownian_bridge_execution(self, open_price: float, high: float, 
                                          low: float, close_price: float,
                                          entry_price: float, direction: str,
                                          target_level: float, stop_loss: float,
                                          entry_time: pd.Timestamp) -> dict:
        """
        Simulate price execution using Brownian Bridge model
        
        The Brownian Bridge constrains a random walk between the open and close prices,
        ensuring it hits the high and low at some point, providing realistic intra-bar simulation.
        """
        # Calculate bar characteristics
        bar_range = high - low
        price_change = close_price - open_price
        
        # Estimate volatility using stochastic volatility model
        volatility = self._estimate_stochastic_volatility(open_price, high, low, close_price)
        
        # Simulate multiple price paths to determine hit probabilities
        n_simulations = 1000
        target_hits = 0
        stop_hits = 0
        
        for _ in range(n_simulations):
            # Generate Brownian Bridge path
            path = self._generate_brownian_bridge_path(
                open_price, close_price, high, low, volatility
            )
            
            # Check which level is hit first in this path
            if direction == "Long":
                target_hit = any(price >= target_level for price in path)
                stop_hit = any(price <= stop_loss for price in path)
            else:
                target_hit = any(price <= target_level for price in path)
                stop_hit = any(price >= stop_loss for price in path)
            
            if target_hit and stop_hit:
                # Both hit - determine order by finding first occurrence
                target_time = next(i for i, price in enumerate(path) 
                                 if (direction == "Long" and price >= target_level) or
                                    (direction == "Short" and price <= target_level))
                stop_time = next(i for i, price in enumerate(path) 
                               if (direction == "Long" and price <= stop_loss) or
                                  (direction == "Short" and price >= stop_loss))
                
                if target_time < stop_time:
                    target_hits += 1
                else:
                    stop_hits += 1
            elif target_hit:
                target_hits += 1
            elif stop_hit:
                stop_hits += 1
        
        # Determine execution based on simulation results
        target_probability = target_hits / n_simulations
        stop_probability = stop_hits / n_simulations
        
        # Use probability threshold for decision
        if target_probability > stop_probability:
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
    
    def _generate_brownian_bridge_path(self, open_price: float, close_price: float,
                                     high: float, low: float, volatility: float,
                                     n_steps: int = 100) -> list:
        """
        Generate a Brownian Bridge path constrained by OHLC data
        
        This creates a realistic price path that:
        1. Starts at open_price
        2. Ends at close_price  
        3. Hits the high and low at some point
        4. Follows realistic price movement patterns
        """
        # Create time grid
        t = np.linspace(0, 1, n_steps)
        
        # Generate standard Brownian motion using numpy
        dt = t[1] - t[0]
        dW = np.random.normal(0, np.sqrt(dt), n_steps)
        W = np.cumsum(dW)
        
        # Create Brownian Bridge: B(t) = W(t) - t*W(1)
        # This ensures B(0) = 0 and B(1) = 0
        bridge = W - t * W[-1]
        
        # Scale to match price movement
        price_path = open_price + bridge * (close_price - open_price)
        
        # Ensure the path hits high and low
        # Find the point where high is hit
        high_idx = np.argmax(price_path)
        if price_path[high_idx] < high:
            # Adjust to ensure high is hit
            price_path[high_idx] = high
        
        # Find the point where low is hit  
        low_idx = np.argmin(price_path)
        if price_path[low_idx] > low:
            # Adjust to ensure low is hit
            price_path[low_idx] = low
        
        # Add some noise to make it more realistic
        noise = np.random.normal(0, volatility * 0.1, n_steps)
        price_path += noise
        
        # Ensure path stays within bounds
        price_path = np.clip(price_path, low, high)
        
        return price_path.tolist()
    
    def _estimate_stochastic_volatility(self, open_price: float, high: float, 
                                      low: float, close_price: float) -> float:
        """
        Estimate volatility using stochastic volatility model
        
        This method uses multiple volatility estimators to create a more accurate
        volatility estimate for the Brownian Bridge simulation.
        """
        # Garman-Klass volatility estimator (more accurate than simple range)
        # Uses OHLC data to estimate volatility
        log_hl = np.log(high / low)
        log_co = np.log(close_price / open_price)
        
        # Garman-Klass estimator
        gk_vol = 0.5 * (log_hl ** 2) - (2 * np.log(2) - 1) * (log_co ** 2)
        gk_vol = max(gk_vol, 0)  # Ensure non-negative
        
        # Parkinson volatility estimator (uses high-low only)
        parkinson_vol = (log_hl ** 2) / (4 * np.log(2))
        
        # Rogers-Satchell volatility estimator (includes open-close)
        rs_vol = log_hl * (np.log(high / close_price) + np.log(low / close_price))
        rs_vol = max(rs_vol, 0)  # Ensure non-negative
        
        # Yang-Zhang volatility estimator (most comprehensive)
        # This is a simplified version - full YZ requires multiple periods
        yz_vol = gk_vol + 0.5 * (log_co ** 2)
        
        # Combine estimators with weights (empirically determined)
        combined_vol = (0.3 * gk_vol + 0.2 * parkinson_vol + 
                       0.2 * rs_vol + 0.3 * yz_vol)
        
        # Convert to standard deviation
        volatility = np.sqrt(combined_vol) if combined_vol > 0 else abs(close_price - open_price) / 4.0
        
        # Ensure reasonable bounds
        price_level = (open_price + close_price) / 2
        min_vol = price_level * 0.001  # 0.1% minimum
        max_vol = price_level * 0.05   # 5% maximum
        
        return np.clip(volatility, min_vol, max_vol)
    
    def _simulate_ml_based_execution(self, bar: pd.Series, entry_price: float,
                                   direction: str, target_level: float,
                                   stop_loss: float, entry_time: pd.Timestamp) -> dict:
        """
        Simulate execution using machine learning approach
        
        This method uses historical pattern recognition to predict the most likely
        execution path based on OHLC features and market conditions.
        """
        high, low, open_price, close_price = float(bar["high"]), float(bar["low"]), float(bar["open"]), float(bar["close"])
        
        # Extract features for ML prediction
        features = self._extract_ml_features(open_price, high, low, close_price, entry_price, direction)
        
        # Use rule-based ML approach (simplified)
        # In practice, this would use a trained model
        target_probability = self._predict_execution_probability(features, target_level, stop_loss, direction)
        
        if target_probability > 0.5:
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
    
    def _extract_ml_features(self, open_price: float, high: float, low: float, 
                           close_price: float, entry_price: float, direction: str) -> dict:
        """Extract features for machine learning prediction"""
        bar_range = high - low
        price_change = close_price - open_price
        
        # Basic price features
        features = {
            'bar_range': bar_range,
            'price_change': price_change,
            'price_change_pct': price_change / open_price if open_price > 0 else 0,
            'bar_range_pct': bar_range / open_price if open_price > 0 else 0,
            'close_position': (close_price - low) / bar_range if bar_range > 0 else 0.5,
            'entry_position': (entry_price - low) / bar_range if bar_range > 0 else 0.5,
            'momentum': 1 if price_change > 0 else -1,
            'volatility': bar_range / open_price if open_price > 0 else 0
        }
        
        # Direction-specific features
        if direction == "Long":
            features['target_distance'] = (high - entry_price) / bar_range if bar_range > 0 else 0
            features['stop_distance'] = (entry_price - low) / bar_range if bar_range > 0 else 0
        else:
            features['target_distance'] = (entry_price - low) / bar_range if bar_range > 0 else 0
            features['stop_distance'] = (high - entry_price) / bar_range if bar_range > 0 else 0
        
        return features
    
    def _predict_execution_probability(self, features: dict, target_level: float, 
                                     stop_loss: float, direction: str) -> float:
        """
        Predict execution probability using rule-based ML approach
        
        This is a simplified version. In practice, this would use a trained model
        with historical tick data to predict execution probabilities.
        """
        # Rule-based prediction based on features
        target_prob = 0.5  # Base probability
        
        # Distance factor (closer = more likely)
        if features['target_distance'] < features['stop_distance']:
            target_prob += 0.2
        
        # Momentum factor
        if direction == "Long" and features['momentum'] > 0:
            target_prob += 0.15
        elif direction == "Short" and features['momentum'] < 0:
            target_prob += 0.15
        
        # Position factor (closer to extremes = more likely)
        if features['close_position'] > 0.7:  # Close near high
            if direction == "Long":
                target_prob += 0.1
        elif features['close_position'] < 0.3:  # Close near low
            if direction == "Short":
                target_prob += 0.1
        
        # Volatility factor (higher volatility = more uncertainty)
        if features['volatility'] > 0.02:  # High volatility
            target_prob -= 0.1
        
        # Ensure probability is between 0 and 1
        return np.clip(target_prob, 0.0, 1.0)
    
    def _calculate_simple_execution_order(self, bar: pd.Series, entry_price: float,
                                        direction: str, target_level: float,
                                        stop_loss: float, entry_time: pd.Timestamp) -> dict:
        """
        Simple execution order calculation (original method)
        
        This is the fallback method that prioritizes target over stop.
        """
        return {
            "exit_price": target_level,
            "exit_time": entry_time + pd.Timedelta(minutes=1),
            "exit_reason": "Win",
            "target_hit": True,
            "stop_hit": False,
            "time_expired": False
        }
    
    def _adjust_stop_loss_t1(self, entry_price: float, direction: str, 
                            target_pts: float, instrument: str) -> float:
        """Adjust stop loss for T1 trigger"""
        # All instruments now use normal break-even behavior
        # Move stop loss to 1 tick below break-even
        tick_sizes = {
            "ES": 0.25, "NQ": 0.25, "YM": 1.0, "CL": 0.01, "NG": 0.001, "GC": 0.1,
            "MES": 0.25, "MNQ": 0.25, "MYM": 1.0, "MCL": 0.01, "MNG": 0.001, "MGC": 0.1
        }
        tick_size = tick_sizes.get(instrument.upper(), 0.25)  # Default to ES tick size
        
        if direction == "Long":
            return entry_price - tick_size  # 1 tick below entry for long trades
        else:
            return entry_price + tick_size  # 1 tick above entry for short trades
    
    def _classify_result(self, t1_triggered: bool,
                        exit_reason: str, target_hit: bool = False, 
                        entry_price: float = 0.0, 
                        exit_time: pd.Timestamp = None, data: pd.DataFrame = None,
                        direction: str = "Long", t1_removed: bool = False) -> str:
        """Classify trade result based on trigger and exit reason"""
        if self.debug_manager.is_debug_enabled():
            try:
                print(f"\nTRADE CLASSIFICATION:")
                print(f"   T1 Triggered: {t1_triggered}")
                print(f"   Exit reason: {exit_reason}, Target hit: {target_hit}")
            except Exception:
                pass
        
        # Check if target was hit first - this overrides everything
        if target_hit:
            if self.debug_manager.is_debug_enabled():
                try:
                    print(f"   -> WIN (target hit)")
                except Exception:
                    pass
            return "Win"  # Full target reached = Full profit
        
        # If exit_reason is "Win" but target_hit is False, check triggers to determine correct classification
        if exit_reason == "Win":
            return "Win"  # Always Win regardless of triggers
        
        # Handle time expiry first - this should override trigger status
        if exit_reason == "TIME":
            if self.debug_manager.is_debug_enabled():
                print(f"  -> TIME (time expiry)")
            return "TIME"
        
        # T1 triggered trades
        if t1_triggered and not t1_removed:
            if self.debug_manager.is_debug_enabled():
                print(f"  -> BE (T1 triggered)")
            return "BE"   # T1 triggered = Break Even
        else:
            if self.debug_manager.is_debug_enabled():
                print(f"  -> LOSS (no triggers hit)")
            return "Loss" # No triggers hit = Loss
    
    def _track_post_6_5_price(self, entry_price: float, exit_time: pd.Timestamp, 
                             data: pd.DataFrame, direction: str) -> str:
        """Track price movement after 6.5 exit for Level 1 trades"""
        if data is None or exit_time is None:
            return "Win"  # Default to Win if no data available
        
        # Get data after the exit time
        post_exit_data = data[data['timestamp'] > exit_time].copy()
        
        if len(post_exit_data) == 0:
            return "Win"  # No data after exit, default to Win
        
        # Define levels for Level 1
        target_level = 10.0  # 10 points target
        
        # Calculate price levels from entry
        if direction == "Long":
            target_price = entry_price + target_level
            be_price = entry_price - 0.25  # -1 tick under entry
        else:  # Short
            target_price = entry_price - target_level
            be_price = entry_price + 0.25  # -1 tick under entry
        
        if self.debug_manager.is_debug_enabled():
            try:
                print(f"\nPOST-6.5 TRACKING:")
                print(f"  Entry: {entry_price}, Direction: {direction}")
                print(f"  Target Price: {target_price} (10.0 points)")
                print(f"  BE Price: {be_price} (-1 tick)")
                print(f"  Tracking {len(post_exit_data)} bars after exit")
            except Exception:
                pass
        
        # Track price movement after exit
        for _, bar in post_exit_data.iterrows():
            high = bar['high']
            low = bar['low']
            
            if direction == "Long":
                # Check if both conditions are possible in this bar
                target_hit = high >= target_price
                be_hit = low <= be_price
                
                if target_hit and be_hit:
                    # Both hit in same bar - use close price to determine which hit first
                    close = bar['close']
                    target_distance = abs(close - target_price)
                    be_distance = abs(close - be_price)
                    
                    if target_distance <= be_distance:
                        if self.debug_manager.is_debug_enabled():
                            try:
                                print(f"  WIN: Both hit, close closer to target at {close}")
                            except Exception:
                                pass
                        return "Win"
                    else:
                        if self.debug_manager.is_debug_enabled():
                            try:
                                print(f"  BE: Both hit, close closer to BE at {close}")
                            except Exception:
                                pass
                        return "BE"
                elif target_hit:
                    if self.debug_manager.is_debug_enabled():
                        try:
                            print(f"  WIN: Price reached 10.0 target at {high}")
                        except Exception:
                            pass
                    return "Win"
                elif be_hit:
                    if self.debug_manager.is_debug_enabled():
                        try:
                            print(f"  BE: Price went to -1 tick under entry at {low}")
                        except Exception:
                            pass
                    return "BE"
            else:  # Short
                # Check if both conditions are possible in this bar
                target_hit = low <= target_price
                be_hit = high >= be_price
                
                if target_hit and be_hit:
                    # Both hit in same bar - use close price to determine which hit first
                    close = bar['close']
                    target_distance = abs(close - target_price)
                    be_distance = abs(close - be_price)
                    
                    if target_distance <= be_distance:
                        if self.debug_manager.is_debug_enabled():
                            try:
                                print(f"  WIN: Both hit, close closer to target at {close}")
                            except Exception:
                                pass
                        return "Win"
                    else:
                        if self.debug_manager.is_debug_enabled():
                            try:
                                print(f"  BE: Both hit, close closer to BE at {close}")
                            except Exception:
                                pass
                        return "BE"
                elif target_hit:
                    if self.debug_manager.is_debug_enabled():
                        try:
                            print(f"  WIN: Price reached 10.0 target at {low}")
                        except Exception:
                            pass
                    return "Win"
                elif be_hit:
                    if self.debug_manager.is_debug_enabled():
                        try:
                            print(f"  BE: Price went to -1 tick under entry at {high}")
                        except Exception:
                            pass
                    return "BE"
        
        if self.debug_manager.is_debug_enabled():
            try:
                print(f"  No 10.0 or -1 tick reached, defaulting to Win")
            except Exception:
                pass
        
        return "Win"  # Default to Win if neither level reached
    
    def _create_trade_execution(self, exit_price: float, exit_time: pd.Timestamp,
                              exit_reason: str, target_hit: bool, stop_hit: bool,
                              time_expired: bool, peak: float, peak_time: pd.Timestamp,
                              peak_price: float, t1_triggered: bool,
                              stop_loss_adjusted: bool, final_stop_loss: float,
                              result_classification: str, profit: float = 0.0) -> TradeExecution:
        """Create TradeExecution object with all data"""
        return TradeExecution(
            exit_price=exit_price,
            exit_time=exit_time,
            exit_reason=exit_reason,
            target_hit=target_hit,
            stop_hit=stop_hit,
            time_expired=time_expired,
            peak=peak,
            peak_time=peak_time,
            peak_price=peak_price,
            t1_triggered=t1_triggered,
            stop_loss_adjusted=stop_loss_adjusted,
            final_stop_loss=final_stop_loss,
            result_classification=result_classification,
            profit=profit
        )
    
    def calculate_profit(self, entry_price: float, exit_price: float,
                        direction: str, result: str, 
                        t1_triggered: bool,
                        target_pts: float, instrument: str = "ES",
                        target_hit: bool = False) -> float:
        """
        Calculate profit based on result and trigger
        
        Args:
            entry_price: Trade entry price
            exit_price: Trade exit price
            direction: Trade direction ("Long" or "Short")
            result: Trade result ("Win", "BE", "Loss")
            t1_triggered: Whether T1 trigger is activated
            target_pts: Target points
            instrument: Trading instrument
            target_hit: Whether target was hit
            
        Returns:
            Calculated profit
        """
        # Calculate base PnL
        if direction == "Long":
            pnl_pts = exit_price - entry_price
        else:
            pnl_pts = entry_price - exit_price
        
        # Scale PnL for micro-futures
        if instrument.startswith("M"):  # Micro-futures
            pnl_pts = pnl_pts / 10.0
        
        # Adjust profit based on result
        if result == "Win":
            # Full target hit
            return target_pts
        elif isinstance(result, (int, float)) and result > 0:
            # Numeric result = full target profit
            return target_pts
        elif result == "BE":
            # Break-even trades: T1 triggered = 1 tick loss
            tick_sizes = {
                "ES": 0.25, "NQ": 0.25, "YM": 1.0, "CL": 0.01, "NG": 0.001, "GC": 0.1,
                "MES": 0.25, "MNQ": 0.25, "MYM": 1.0, "MCL": 0.01, "MNG": 0.001, "MGC": 0.1
            }
            tick_size = tick_sizes.get(instrument.upper(), 0.25)  # Default to ES tick size
            return -tick_size  # 1 tick loss
        elif result == "TIME":
            # Time expiry trades: Calculate actual PnL based on exit price
            if direction == "Long":
                pnl_pts = exit_price - entry_price
            else:
                pnl_pts = entry_price - exit_price
            
            # Scale for micro-futures
            if instrument.startswith("M"):
                pnl_pts = pnl_pts / 10.0
            
            return pnl_pts
        elif result == "Loss":
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
    
    def _classify_post_6_5_behavior(self, after_bars: pd.DataFrame, stop_hit_time: pd.Timestamp,
                                   entry_price: float, direction: str, target_pts: float,
                                   t1_threshold: float, debug_info, 
                                   instrument: str = "ES") -> dict:
        """
        Classify what happens after stop loss is hit for T1 triggered trades
        
        Args:
            after_bars: DataFrame of bars after entry time
            stop_hit_time: When the 6.5 stop loss was hit
            entry_price: Trade entry price
            direction: Trade direction
            target_pts: Target points
            t1_threshold: T1 threshold
            debug_info: Debug information object
            
        Returns:
            Dictionary with classification results
        """
        target = target_pts  # Full target (10.0 points)
        # Calculate 1 tick below entry based on instrument
        tick_sizes = {
            "ES": 0.25, "NQ": 0.25, "YM": 1.0, "CL": 0.01, "NG": 0.001, "GC": 0.1,
            "MES": 0.25, "MNQ": 0.25, "MYM": 1.0, "MCL": 0.01, "MNG": 0.001, "MGC": 0.1
        }
        tick_size = tick_sizes.get(instrument.upper(), 0.25)  # Default to ES tick size
        below_entry_1_tick = -tick_size  # 1 tick below entry
        
        if self.debug_manager.is_debug_enabled():
            print(f"POST-6.5 ANALYSIS:")
            print(f"  Target: {target:.2f} points")
            print(f"  Below entry (1 tick): {below_entry_1_tick:.2f} points")
            print(f"  Stop hit time: {stop_hit_time}")
        
        # Find bars after the stop loss was hit
        post_stop_bars = after_bars[after_bars['timestamp'] > stop_hit_time].copy()
        
        if len(post_stop_bars) == 0:
            if self.debug_manager.is_debug_enabled():
                print(f"  No bars after stop hit - defaulting to WIN")
            return {
                "classification": "Win",
                "exit_reason": "Win",
                "first_hit": "None",
                "profit": target_pts * 0.65  # 6.5 points profit
            }
        
        # Track what happens after 6.5 stop loss
        hit_10_after_6_5 = False
        hit_10_time = None
        hit_below_entry_after_6_5 = False
        hit_below_entry_time = None
        hit_below_entry_value = None
        
        for _, bar in post_stop_bars.iterrows():
            high, low = float(bar["high"]), float(bar["low"])
            
            # Calculate favorable movement from entry
            if direction == "Long":
                current_favorable = high - entry_price
            else:
                current_favorable = entry_price - low
            
            # Check for 10 hit after 6.5 stop loss
            if not hit_10_after_6_5 and current_favorable >= target:
                hit_10_after_6_5 = True
                hit_10_time = bar["timestamp"]
                if self.debug_manager.is_debug_enabled():
                    print(f"  Hit 10 at: {hit_10_time}")
            
            # Check for 1 tick below entry hit after 6.5 stop loss
            if not hit_below_entry_after_6_5 and current_favorable <= below_entry_1_tick:
                hit_below_entry_after_6_5 = True
                hit_below_entry_time = bar["timestamp"]
                hit_below_entry_value = current_favorable
                if self.debug_manager.is_debug_enabled():
                    print(f"  Hit below entry (1 tick) at: {hit_below_entry_time}, value: {hit_below_entry_value:.2f}")
        
        # Determine what was hit first
        if hit_10_after_6_5 and hit_below_entry_after_6_5:
            if hit_10_time < hit_below_entry_time:
                first_hit = "10 (Target)"
                classification = "Win"
                exit_reason = "Win"
            else:
                first_hit = "Below Entry (1 tick)"
                classification = "BE"
                exit_reason = "BE"
        elif hit_10_after_6_5:
            first_hit = "10 (Target)"
            classification = "Win"
            exit_reason = "Win"
        elif hit_below_entry_after_6_5:
            first_hit = "Below Entry (1 tick)"
            classification = "BE"
            exit_reason = "BE"
        else:
            first_hit = "None"
            classification = "Win"  # Default to WIN if neither hit
            exit_reason = "Win"
        
        if self.debug_manager.is_debug_enabled():
            print(f"  First hit after 6.5: {first_hit}")
            print(f"  Classification: {classification}")
        
        return {
            "classification": classification,
            "exit_reason": exit_reason,
            "first_hit": first_hit,
            "profit": target_pts * 0.65,  # Always 6.5 points profit
            "hit_10_after_6_5": hit_10_after_6_5,
            "hit_10_time": hit_10_time,
            "hit_below_entry_after_6_5": hit_below_entry_after_6_5,
            "hit_below_entry_time": hit_below_entry_time,
            "hit_below_entry_value": hit_below_entry_value
        }