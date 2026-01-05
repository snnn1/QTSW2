"""
Price Tracking Logic Module
Handles real-time price monitoring, trade execution, MFE calculation, and break even logic

IMPORTANT: Intra-bar execution order is unknowable with OHLC data alone.
This module uses a deterministic, conservative proxy rule to resolve intra-bar execution.
Auditability and reproducibility are prioritized over simulated realism.

The system does not claim to represent "what actually happened" or provide "realistic execution."
Instead, it provides a consistent, auditable resolution rule that favors conservative outcomes.
"""

import pandas as pd
import numpy as np
from typing import Optional, Tuple
from dataclasses import dataclass
from datetime import datetime
from .debug_logic import DebugManager, DebugInfo
from .instrument_logic import InstrumentManager
from .config_logic import ConfigManager

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
    exit_time: Optional[pd.Timestamp]  # None/NaT for open trades
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
    
    def __init__(self, debug_manager: DebugManager = None, 
                 instrument_manager: InstrumentManager = None,
                 config_manager: ConfigManager = None):
        """
        Initialize PriceTracker with deterministic intra-bar execution rule.
        
        This tracker uses OHLC data with a deterministic, conservative proxy rule
        to resolve intra-bar execution order. The rule prioritizes auditability
        and reproducibility over simulated realism.
        
        Args:
            debug_manager: Debug manager for logging
            instrument_manager: Instrument manager for tick sizes and scaling
            config_manager: Configuration manager for market close time
        """
        self.debug_manager = debug_manager or DebugManager(False)
        self.instrument_manager = instrument_manager or InstrumentManager()
        self.config_manager = config_manager or ConfigManager()
    
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
            
            # Define market close time (configurable via ConfigManager)
            # CRITICAL: Create market_close timestamp properly preserving timezone
            market_close_time_str = self.config_manager.get_market_close_time()  # e.g., "16:00"
            market_close_hour, market_close_minute = map(int, market_close_time_str.split(":"))
            # Convert entry_time to pandas Timestamp if it's not already
            entry_time_pd = pd.Timestamp(entry_time) if not isinstance(entry_time, pd.Timestamp) else entry_time
            if entry_time_pd.tz is not None:
                # Timezone-aware: create market close timestamp in same timezone
                date_str = entry_time_pd.strftime('%Y-%m-%d')
                market_close = pd.Timestamp(f"{date_str} {market_close_hour:02d}:{market_close_minute:02d}:00", tz=entry_time_pd.tz)
            else:
                # Naive timestamp: use replace directly
                market_close = entry_time_pd.replace(hour=market_close_hour, minute=market_close_minute, second=0, microsecond=0)
            
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
                    last_bar_timestamp = mfe_bars.iloc[-1]['timestamp'] if len(mfe_bars) > 0 else None
                    last_bar = mfe_bars.iloc[-1] if len(mfe_bars) > 0 else None
                    self.debug_manager.print_mfe_debug(entry_time, mfe_end_time, len(mfe_bars), first_bar, last_bar_timestamp)
                    
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
                
                # Check what gets hit first: target or stop loss using deterministic intra-bar execution rule
                target_hit_first = False
                stop_hit_first = False
                
                # Use deterministic intra-bar execution rule to resolve which level is hit first
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
                    
                    # Determine correct exit price for target hit
                    # Precedence: execution_result.exit_price if exists, else target_level
                    if execution_result and "exit_price" in execution_result:
                        target_exit_price = float(execution_result["exit_price"])
                    else:
                        target_exit_price = target_level
                    
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
                    
                    # Calculate profit (full target) using correct exit price
                    profit = self.calculate_profit(
                        entry_price, target_exit_price, direction, result_classification,
                        t1_triggered, target_pts, instrument,
                        target_hit=True
                    )
                    
                    return self._create_trade_execution(
                        target_exit_price, bar["timestamp"], exit_reason, True, False, False,
                        max_favorable, peak_time, peak_price, t1_triggered,
                        stop_loss_adjusted, current_stop_loss, result_classification, profit
                    )
                
                elif stop_hit_first:
                    # Stop loss hit - use exit price from execution result (stop loss level)
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
                    
                    # Calculate profit using exit price from execution (stop loss level)
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
                    # Before exiting with TIME, check if break-even stop loss was hit in any bar before or at expiry
                    # This handles cases where break-even stop was hit but execution_result was None
                    if t1_triggered and stop_loss_adjusted:
                        # Check all bars at or before expiry time to see if break-even stop loss was hit
                        # Include current bar in case break-even stop was hit exactly at expiry
                        bars_at_or_before_expiry = after[after["timestamp"] <= expiry_time]
                        be_stop_hit = False
                        be_stop_hit_time = None
                        
                        for _, prev_bar in bars_at_or_before_expiry.iterrows():
                            prev_low = float(prev_bar["low"])
                            prev_high = float(prev_bar["high"])
                            
                            if direction == "Long":
                                if prev_low <= current_stop_loss:
                                    be_stop_hit = True
                                    be_stop_hit_time = prev_bar["timestamp"]
                                    break
                            else:
                                if prev_high >= current_stop_loss:
                                    be_stop_hit = True
                                    be_stop_hit_time = prev_bar["timestamp"]
                                    break
                        
                        if be_stop_hit:
                            # Break-even stop loss was hit before expiry - exit with BE instead of TIME
                            exit_price_from_execution = current_stop_loss
                            exit_reason = "BE"
                            result_classification = self._classify_result(
                                t1_triggered, exit_reason, False, entry_price, be_stop_hit_time, df, direction, t1_removed
                            )
                            
                            if self.debug_manager.is_debug_enabled():
                                print(f"[FIX] Break-even stop loss was hit at {be_stop_hit_time} before time expiry - exiting with BE instead of TIME")
                            
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
                                
                                if mfe_peak == 0.0 and max_favorable_execution > 0.0:
                                    max_favorable = max_favorable_execution
                                    if self.debug_manager.is_debug_enabled():
                                        print(f"[FIX] PEAK FIX: MFE peak=0, using execution peak={max_favorable_execution:.2f}")
                                else:
                                    max_favorable = mfe_peak
                            
                            # Update debug info
                            debug_info.peak_value = max_favorable
                            debug_info.peak_time = peak_time
                            debug_info.stop_loss_hit = True
                            debug_info.stop_loss_hit_time = be_stop_hit_time
                            
                            if self.debug_manager.is_debug_enabled():
                                self.debug_manager.print_trade_debug(debug_info)
                            self.debug_manager.log_trade_info(debug_info)
                            
                            # Calculate profit
                            profit = self.calculate_profit(
                                entry_price, exit_price_from_execution, direction, result_classification,
                                t1_triggered, target_pts, instrument,
                                target_hit=False
                            )
                            
                            return self._create_trade_execution(
                                exit_price_from_execution, be_stop_hit_time, exit_reason, False, True, False,
                                max_favorable, peak_time, peak_price, t1_triggered,
                                stop_loss_adjusted, current_stop_loss, result_classification, profit
                            )
                
                # Check time expiry (original logic continues if BE stop wasn't hit)
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
            # Check if trade has expired (expiry_time has passed) or is still open
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
                # Trade hasn't expired yet - exit time should be empty (trade is still ongoing)
                # ExitTime = empty/None (trade is still open)
                exit_time_for_time_expiry = pd.NaT  # Use NaT (Not a Time) for open trades
                # Use the absolute last bar's close price (most recent price in entire dataset)
                # This ensures we get the latest price even if new data was added since entry
                exit_price_for_time_expiry = float(last_bar_in_data["close"])  # Current/last available price from most recent bar in dataset
                
                # Use "TIME" as exit_reason but time_expired=False (trade still ongoing)
                # Pass None for exit_time to _classify_result since trade is still open
                result_classification = self._classify_result(
                    t1_triggered, "TIME", False, entry_price, None, df, direction, t1_removed
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
                # Result = "TIME", ExitTime = empty/None (trade still open), Profit = current price
                return self._create_trade_execution(
                    exit_price_for_time_expiry, exit_time_for_time_expiry, "TIME", False, False, False,  # exit_reason="TIME", time_expired=False, exit_time=NaT
                    max_favorable, peak_time, peak_price, t1_triggered,
                    stop_loss_adjusted, current_stop_loss, result_classification, profit
                )
            
            # Trade has expired (expiry_time has passed or data extends to it)
            # Before exiting with TIME, check if break-even stop loss was hit in any bar before expiry
            # This handles cases where break-even stop was hit but execution_result was None
            if t1_triggered and stop_loss_adjusted:
                # Check all bars at or before expiry time to see if break-even stop loss was hit
                bars_at_or_before_expiry = after[after["timestamp"] <= expiry_time]
                be_stop_hit = False
                be_stop_hit_time = None
                
                for _, prev_bar in bars_at_or_before_expiry.iterrows():
                    prev_low = float(prev_bar["low"])
                    prev_high = float(prev_bar["high"])
                    
                    if direction == "Long":
                        if prev_low <= current_stop_loss:
                            be_stop_hit = True
                            be_stop_hit_time = prev_bar["timestamp"]
                            break
                    else:
                        if prev_high >= current_stop_loss:
                            be_stop_hit = True
                            be_stop_hit_time = prev_bar["timestamp"]
                            break
                
                if be_stop_hit:
                    # Break-even stop loss was hit before expiry - exit with BE instead of TIME
                    exit_price_from_execution = current_stop_loss
                    exit_reason = "BE"
                    result_classification = self._classify_result(
                        t1_triggered, exit_reason, False, entry_price, be_stop_hit_time, df, direction, t1_removed
                    )
                    
                    if self.debug_manager.is_debug_enabled():
                        print(f"[FIX] Break-even stop loss was hit at {be_stop_hit_time} before time expiry (after loop path) - exiting with BE instead of TIME")
                    
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
                        
                        if mfe_peak == 0.0 and max_favorable_execution > 0.0:
                            max_favorable = max_favorable_execution
                            if self.debug_manager.is_debug_enabled():
                                print(f"[FIX] PEAK FIX: MFE peak=0, using execution peak={max_favorable_execution:.2f}")
                        else:
                            max_favorable = mfe_peak
                    else:
                        max_favorable = max_favorable_execution
                        peak_time = entry_time
                        peak_price = entry_price
                    
                    # Update debug info
                    debug_info.peak_value = max_favorable
                    debug_info.peak_time = peak_time
                    debug_info.stop_loss_hit = True
                    debug_info.stop_loss_hit_time = be_stop_hit_time
                    
                    if self.debug_manager.is_debug_enabled():
                        self.debug_manager.print_trade_debug(debug_info)
                    self.debug_manager.log_trade_info(debug_info)
                    
                    # Calculate profit
                    profit = self.calculate_profit(
                        entry_price, exit_price_from_execution, direction, result_classification,
                        t1_triggered, target_pts, instrument,
                        target_hit=False
                    )
                    
                    return self._create_trade_execution(
                        exit_price_from_execution, be_stop_hit_time, exit_reason, False, True, False,
                        max_favorable, peak_time, peak_price, t1_triggered,
                        stop_loss_adjusted, current_stop_loss, result_classification, profit
                    )
            
            # Use expiry_time as exit_time (not last bar timestamp)
            # This ensures Friday trades expire on Monday even if data doesn't extend that far
            # Get last bar from after (bars after entry time) for fallback
            last_bar_after = after.iloc[-1] if len(after) > 0 else None
            if last_bar_after is None:
                # Fallback: use entry time and price if no after bars
                exit_time_for_time_expiry = expiry_time if expiry_time else entry_time
                exit_price_for_time_expiry = entry_price
            else:
                exit_time_for_time_expiry = expiry_time if expiry_time else last_bar_after["timestamp"]
                
                # If expiry_time is in the future relative to data but has passed in real time, use the last bar's close price
                # Otherwise use the bar at expiry_time
                if expiry_time and expiry_time > last_bar_after["timestamp"]:
                    # Data doesn't extend to expiry_time but expiry_time has passed - use last bar's close price
                    exit_price_for_time_expiry = float(last_bar_after["close"])
                else:
                    # Data extends to expiry_time - find the bar at expiry_time
                    expiry_bar = after[after["timestamp"] >= expiry_time]
                    if len(expiry_bar) > 0:
                        exit_price_for_time_expiry = float(expiry_bar.iloc[0]["close"])
                    else:
                        exit_price_for_time_expiry = float(last_bar_after["close"])
            
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
                                    t1_triggered_previous_bar: bool = False) -> Optional[dict]:
        """
        ARCHITECTURAL INVARIANT:
        Intra-bar execution order is unknowable with OHLC data alone.
        This function must remain deterministic and conservative.
        Tie-break behavior is intentional and documented.
        
        Deterministic intra-bar execution using close-distance rule.
        
        SINGLE CANONICAL RULE: When both target and stop are hit within the same OHLC bar,
        compute absolute distance from bar close price to target_level and stop_loss.
        Whichever distance is smaller is considered hit first.
        If distances are exactly equal, break ties deterministically in favor of STOP (conservative bias).
        
        This rule is deterministic, reproducible, and auditable. No randomness, simulation, or volatility modeling.
        This is a conservative proxy rule, not a claim about actual execution order.
        
        Args:
            bar: OHLC bar data
            entry_price: Trade entry price (not used in rule, kept for compatibility)
            direction: Trade direction ("Long" or "Short")
            target_level: Target level price
            stop_loss: Stop loss level price
            entry_time: Entry timestamp
            t1_triggered: Whether T1 is triggered in current bar
            t1_triggered_previous_bar: Whether T1 was triggered in previous bar
            
        Returns:
            Dictionary with execution results, or None if no execution
        """
        high, low, close_price = float(bar["high"]), float(bar["low"]), float(bar["close"])
        
        # Determine if target and stop are both possible in this bar
        target_possible = False
        stop_possible = False
        
        if direction == "Long":
            target_possible = high >= target_level
            stop_possible = low <= stop_loss
        else:  # Short
            target_possible = low <= target_level
            stop_possible = high >= stop_loss
        
        # Handle T1 trigger logic: don't check stop if T1 just triggered in this bar
        if stop_possible and t1_triggered and not t1_triggered_previous_bar:
            # T1 just triggered in this bar - don't check stop in same bar
            if target_possible:
                # Only target is checked
                return {
                    "exit_price": target_level,
                    "exit_time": entry_time,
                    "exit_reason": "Win",
                    "target_hit": True,
                    "stop_hit": False,
                    "time_expired": False
                }
            else:
                # Neither target nor stop checked (T1 protection)
                return None
        
        # Case 1: Only target is possible
        if target_possible and not stop_possible:
            return {
                "exit_price": target_level,
                "exit_time": entry_time,
                "exit_reason": "Win",
                "target_hit": True,
                "stop_hit": False,
                "time_expired": False
            }
        
        # Case 2: Only stop is possible
        if stop_possible and not target_possible:
            return {
                "exit_price": stop_loss,
                "exit_time": entry_time,
                "exit_reason": "Loss",
                "target_hit": False,
                "stop_hit": True,
                "time_expired": False
            }
        
        # Case 3: Both target and stop are possible - use close-distance rule
        if target_possible and stop_possible:
            # Compute absolute distance from close to target and stop
            target_distance = abs(close_price - target_level)
            stop_distance = abs(close_price - stop_loss)
            
            # Whichever distance is smaller wins
            # If distances are exactly equal, break tie in favor of STOP (conservative bias)
            if target_distance < stop_distance:
                # Target is closer - target hits first
                return {
                    "exit_price": target_level,
                    "exit_time": entry_time,
                    "exit_reason": "Win",
                    "target_hit": True,
                    "stop_hit": False,
                    "time_expired": False
                }
            else:
                # Stop is closer OR equal (tie-break favors STOP) - stop hits first
                return {
                    "exit_price": stop_loss,
                    "exit_time": entry_time,
                    "exit_reason": "Loss",
                    "target_hit": False,
                    "stop_hit": True,
                    "time_expired": False
                }
        
        # Case 4: Neither target nor stop hit in this bar
        return None
    
    # REMOVED: Old execution methods replaced by deterministic close-distance rule
    # The following methods are no longer used and have been replaced by _simulate_intra_bar_execution:
    # - _calculate_simple_execution_order
    # - _analyze_actual_price_movement
    # - _target_hits_first_long
    # - _target_hits_first_short
    
    def _simulate_realistic_entry(self, bar: pd.Series, breakout_level: float, 
                                direction: str, tick_size: float = 0.25) -> dict:
        """
        Determine entry execution at breakout level using deterministic rule
        
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
        
        # Determine entry price at breakout level (no slippage)
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
    
    
    # REMOVED: Probabilistic and heuristic execution methods
    # The following methods have been removed as they violate the deterministic requirement:
    # - _simulate_brownian_bridge_execution (probabilistic/stochastic)
    # - _generate_brownian_bridge_path (probabilistic/stochastic)
    # - _estimate_stochastic_volatility (probabilistic/stochastic)
    # - _simulate_ml_based_execution (ML/heuristic)
    # - _extract_ml_features (ML/heuristic)
    # - _predict_execution_probability (ML/heuristic)
    # - _calculate_simple_execution_order (fallback method, no longer needed)
    #
    # All intra-bar execution now uses the single deterministic close-distance rule
    # implemented in _simulate_intra_bar_execution.
    
    def _adjust_stop_loss_t1(self, entry_price: float, direction: str, 
                            target_pts: float, instrument: str) -> float:
        """Adjust stop loss for T1 trigger"""
        # All instruments now use normal break-even behavior
        # Move stop loss to 1 tick below break-even
        tick_size = self.instrument_manager.get_tick_size(instrument.upper())
        
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
        
        # Explicit BE check: if exit_reason is "BE", return immediately
        # Do not rely solely on t1_triggered to infer BE
        if exit_reason == "BE":
            if self.debug_manager.is_debug_enabled():
                try:
                    print(f"   -> BE (explicit exit reason)")
                except Exception:
                    pass
            return "BE"
        
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
        if data is None or exit_time is None or pd.isna(exit_time):
            return "Win"  # Default to Win if no data available or trade still open
        
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
                    # Both hit in same bar - use deterministic close-distance rule
                    # Tie-break favors BE (conservative bias, same as STOP in main execution)
                    close = bar['close']
                    target_distance = abs(close - target_price)
                    be_distance = abs(close - be_price)
                    
                    if target_distance < be_distance:
                        # Target is closer - target hits first
                        if self.debug_manager.is_debug_enabled():
                            try:
                                print(f"  WIN: Both hit, close closer to target at {close}")
                            except Exception:
                                pass
                        return "Win"
                    else:
                        # BE is closer OR equal (tie-break favors BE) - BE hits first
                        if self.debug_manager.is_debug_enabled():
                            try:
                                print(f"  BE: Both hit, close closer to BE (or tie) at {close}")
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
                    # Both hit in same bar - use deterministic close-distance rule
                    # Tie-break favors BE (conservative bias, same as STOP in main execution)
                    close = bar['close']
                    target_distance = abs(close - target_price)
                    be_distance = abs(close - be_price)
                    
                    if target_distance < be_distance:
                        # Target is closer - target hits first
                        if self.debug_manager.is_debug_enabled():
                            try:
                                print(f"  WIN: Both hit, close closer to target at {close}")
                            except Exception:
                                pass
                        return "Win"
                    else:
                        # BE is closer OR equal (tie-break favors BE) - BE hits first
                        if self.debug_manager.is_debug_enabled():
                            try:
                                print(f"  BE: Both hit, close closer to BE (or tie) at {close}")
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
                        target_hit: bool = False, use_display_profit: bool = False) -> float:
        """
        Calculate profit based on result and trigger
        
        This method now delegates to InstrumentManager.calculate_profit() for unified logic.
        
        Args:
            entry_price: Trade entry price
            exit_price: Trade exit price
            direction: Trade direction ("Long" or "Short")
            result: Trade result ("Win", "BE", "Loss", "TIME")
            t1_triggered: Whether T1 trigger is activated
            target_pts: Target points (unscaled)
            instrument: Trading instrument
            target_hit: Whether target was hit (unused, kept for compatibility)
            use_display_profit: If True, returns display profit (ES equivalent for micro-futures)
            
        Returns:
            Calculated profit
        """
        # Use unified profit calculation from InstrumentManager
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
        tick_size = self.instrument_manager.get_tick_size(instrument.upper())
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