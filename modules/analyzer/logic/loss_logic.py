"""
Loss Logic Module
Handles stop loss management, loss calculations, risk management, and loss-related validations
"""

import pandas as pd
from typing import Optional, Tuple, Dict, List
from dataclasses import dataclass
from enum import Enum

class LossType(Enum):
    """Types of losses in the trading system"""
    STOP_LOSS = "stop_loss"
    TIME_EXPIRY = "time_expiry"
    BREAK_EVEN = "break_even"
    PARTIAL_LOSS = "partial_loss"
    MAX_LOSS = "max_loss"

class StopLossType(Enum):
    """Types of stop loss adjustments"""
    INITIAL = "initial"
    T1_ADJUSTED = "t1_adjusted"
    TRAILING = "trailing"
    BREAK_EVEN = "break_even"

@dataclass
class LossResult:
    """Result of loss calculation and analysis"""
    loss_type: LossType
    loss_amount: float
    loss_percentage: float
    stop_loss_type: StopLossType
    stop_loss_price: float
    exit_price: float
    exit_time: pd.Timestamp
    risk_reward_ratio: float
    max_drawdown: float
    is_acceptable_loss: bool
    loss_reason: str

@dataclass
class StopLossConfig:
    """Configuration for stop loss management"""
    initial_multiplier: float = 3.0  # 3x target as initial stop
    t1_adjustment: str = "break_even"  # "break_even" or "partial"
    max_loss_per_trade: float = 100.0  # Maximum loss per trade
    max_daily_loss: float = 500.0  # Maximum daily loss
    risk_per_trade: float = 0.02  # 2% risk per trade

class LossManager:
    """Handles all loss-related calculations and management"""
    
    def __init__(self, config: StopLossConfig = None, instrument_manager=None):
        """
        Initialize loss manager
        
        Args:
            config: Stop loss configuration (uses defaults if None)
            instrument_manager: InstrumentManager instance for instrument-specific calculations
        """
        self.instrument_manager = instrument_manager
        self.config = config or StopLossConfig()
        self.daily_losses = {}  # Track daily losses by date
        self.trade_losses = []  # Track individual trade losses
        
    def calculate_initial_stop_loss(self, entry_price: float, direction: str, 
                                  target_pts: float, instrument: str = "ES", 
                                  range_size: float = None, range_high: float = None, 
                                  range_low: float = None) -> float:
        """
        Calculate initial stop loss price
        
        Stop loss = min(range_size, 3 * target_pts) in points
        Then converted to price based on direction.
        
        Args:
            entry_price: Trade entry price
            direction: Trade direction ("Long" or "Short")
            target_pts: Target points for the trade
            instrument: Trading instrument (not used, kept for compatibility)
            range_size: Range size in points (required)
            range_high: Range high price (not used, kept for compatibility)
            range_low: Range low price (not used, kept for compatibility)
            
        Returns:
            Initial stop loss price
        """
        # Calculate stop loss in points: min(range_size, 3 * target_pts)
        max_sl_points = 3 * target_pts
        
        if range_size is None:
            # Fallback to 3x target if no range size provided
            sl_points = max_sl_points
        else:
            # Stop loss = min(range_size, 3 * target_pts)
            sl_points = min(range_size, max_sl_points)
        
        # Convert points to price based on direction
        if direction == "Long":
            return entry_price - sl_points
        else:
            return entry_price + sl_points
    
    def adjust_stop_loss_t1(self, entry_price: float, direction: str, 
                           target_pts: float, instrument: str = "ES") -> Tuple[float, str]:
        """
        Adjust stop loss for T1 trigger (65% of target reached)
        
        Args:
            entry_price: Trade entry price
            direction: Trade direction
            target_pts: Target points
            instrument: Trading instrument
            
        Returns:
            Tuple of (new_stop_loss, adjustment_reason)
        """
        if not self.instrument_manager:
            raise ValueError("InstrumentManager required for stop loss adjustment")
        
        if self.config.t1_adjustment == "break_even":
            # Move stop loss to 1 tick below break-even
            tick_size = self.instrument_manager.get_tick_size(instrument.upper())
            
            if direction == "Long":
                new_sl = entry_price - tick_size  # 1 tick below entry for long trades
            else:
                new_sl = entry_price + tick_size  # 1 tick above entry for short trades
            
            reason = "T1: Moved to 1 tick below break-even"
            
        elif self.config.t1_adjustment == "partial":
            # Move stop loss to partial profit (25% of target)
            partial_profit = target_pts * 0.25
            if direction == "Long":
                new_sl = entry_price + partial_profit
            else:
                new_sl = entry_price - partial_profit
            reason = "T1: Moved to 25% profit"
            
        else:
            # All instruments: 1 tick below break-even
            tick_size = self.instrument_manager.get_tick_size(instrument.upper())
            
            if direction == "Long":
                new_sl = entry_price - tick_size  # 1 tick below entry for long trades
            else:
                new_sl = entry_price + tick_size  # 1 tick above entry for short trades
            
            reason = "T1: Moved to 1 tick below break-even"
            
        return new_sl, reason
    
    def check_stop_loss_hit(self, high: float, low: float, stop_loss: float, 
                           direction: str) -> bool:
        """
        Check if stop loss was hit in a bar
        
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
    
    def calculate_loss_amount(self, entry_price: float, exit_price: float, 
                            direction: str, instrument: str = "ES") -> float:
        """
        Calculate loss amount in points
        
        Args:
            entry_price: Trade entry price
            exit_price: Trade exit price
            direction: Trade direction
            instrument: Trading instrument
            
        Returns:
            Loss amount in points
        """
        if direction == "Long":
            loss_pts = entry_price - exit_price
        else:
            loss_pts = exit_price - entry_price
            
        # Scale for micro-futures using InstrumentManager
        if self.instrument_manager:
            loss_pts = self.instrument_manager.scale_profit(instrument.upper(), loss_pts)
        else:
            # Fallback if InstrumentManager not available (shouldn't happen)
            if instrument.startswith("M"):
                loss_pts = loss_pts / 10.0
            
        return loss_pts
    
    def calculate_loss_percentage(self, loss_amount: float, account_size: float) -> float:
        """
        Calculate loss as percentage of account
        
        Args:
            loss_amount: Loss amount in points
            account_size: Account size in points
            
        Returns:
            Loss percentage
        """
        if account_size <= 0:
            return 0.0
        return (loss_amount / account_size) * 100
    
    def calculate_risk_reward_ratio(self, entry_price: float, target_price: float,
                                  stop_loss_price: float, direction: str) -> float:
        """
        Calculate risk-reward ratio for the trade
        
        Args:
            entry_price: Trade entry price
            target_price: Target price
            stop_loss_price: Stop loss price
            direction: Trade direction
            
        Returns:
            Risk-reward ratio
        """
        if direction == "Long":
            reward = target_price - entry_price
            risk = entry_price - stop_loss_price
        else:
            reward = entry_price - target_price
            risk = stop_loss_price - entry_price
            
        if risk <= 0:
            return 0.0
            
        return reward / risk
    
    def calculate_max_drawdown(self, entry_price: float, peak_price: float,
                             direction: str) -> float:
        """
        Calculate maximum drawdown from peak
        
        Args:
            entry_price: Trade entry price
            peak_price: Peak price reached
            direction: Trade direction
            
        Returns:
            Maximum drawdown in points
        """
        if direction == "Long":
            return peak_price - entry_price
        else:
            return entry_price - peak_price
    
    def validate_loss_limits(self, loss_amount: float, trade_date: pd.Timestamp) -> bool:
        """
        Validate if loss is within acceptable limits
        
        Args:
            loss_amount: Loss amount in points
            trade_date: Date of the trade
            
        Returns:
            True if loss is within limits
        """
        # Check per-trade limit
        if loss_amount > self.config.max_loss_per_trade:
            return False
            
        # Check daily limit
        date_key = trade_date.date()
        daily_loss = self.daily_losses.get(date_key, 0.0)
        if (daily_loss + loss_amount) > self.config.max_daily_loss:
            return False
            
        return True
    
    def record_loss(self, loss_amount: float, trade_date: pd.Timestamp, 
                   loss_type: LossType, trade_id: str = None):
        """
        Record a loss for tracking and analysis
        
        Args:
            loss_amount: Loss amount in points
            trade_date: Date of the trade
            loss_type: Type of loss
            trade_id: Optional trade identifier
        """
        # Update daily loss tracking
        date_key = trade_date.date()
        self.daily_losses[date_key] = self.daily_losses.get(date_key, 0.0) + loss_amount
        
        # Record individual trade loss
        loss_record = {
            "trade_id": trade_id,
            "date": trade_date,
            "loss_amount": loss_amount,
            "loss_type": loss_type.value,
            "daily_total": self.daily_losses[date_key]
        }
        self.trade_losses.append(loss_record)
    
    def get_daily_loss(self, trade_date: pd.Timestamp) -> float:
        """
        Get total loss for a specific date
        
        Args:
            trade_date: Date to check
            
        Returns:
            Total loss for the date
        """
        date_key = trade_date.date()
        return self.daily_losses.get(date_key, 0.0)
    
    def get_loss_statistics(self, days: int = 30) -> Dict:
        """
        Get loss statistics for the specified number of days
        
        Args:
            days: Number of days to analyze
            
        Returns:
            Dictionary with loss statistics
        """
        if not self.trade_losses:
            return {
                "total_trades": 0,
                "total_loss": 0.0,
                "average_loss": 0.0,
                "max_loss": 0.0,
                "loss_rate": 0.0,
                "worst_day": 0.0
            }
        
        # Filter by date range
        cutoff_date = pd.Timestamp.now() - pd.Timedelta(days=days)
        recent_losses = [l for l in self.trade_losses if l["date"] >= cutoff_date]
        
        if not recent_losses:
            return {
                "total_trades": 0,
                "total_loss": 0.0,
                "average_loss": 0.0,
                "max_loss": 0.0,
                "loss_rate": 0.0,
                "worst_day": 0.0
            }
        
        total_loss = sum(l["loss_amount"] for l in recent_losses)
        max_loss = max(l["loss_amount"] for l in recent_losses)
        average_loss = total_loss / len(recent_losses)
        
        # Calculate worst day
        daily_totals = {}
        for loss in recent_losses:
            date_key = loss["date"].date()
            daily_totals[date_key] = daily_totals.get(date_key, 0.0) + loss["loss_amount"]
        worst_day = max(daily_totals.values()) if daily_totals else 0.0
        
        return {
            "total_trades": len(recent_losses),
            "total_loss": total_loss,
            "average_loss": average_loss,
            "max_loss": max_loss,
            "loss_rate": len(recent_losses) / days,
            "worst_day": worst_day
        }
    
    def analyze_loss_patterns(self) -> Dict:
        """
        Analyze loss patterns and provide insights
        
        Returns:
            Dictionary with loss pattern analysis
        """
        if not self.trade_losses:
            return {"message": "No loss data available"}
        
        # Group by loss type
        loss_types = {}
        for loss in self.trade_losses:
            loss_type = loss["loss_type"]
            if loss_type not in loss_types:
                loss_types[loss_type] = []
            loss_types[loss_type].append(loss["loss_amount"])
        
        # Calculate statistics by type
        type_stats = {}
        for loss_type, amounts in loss_types.items():
            type_stats[loss_type] = {
                "count": len(amounts),
                "total": sum(amounts),
                "average": sum(amounts) / len(amounts),
                "max": max(amounts)
            }
        
        # Find most common loss type
        most_common = max(type_stats.keys(), key=lambda k: type_stats[k]["count"])
        
        return {
            "loss_types": type_stats,
            "most_common_loss_type": most_common,
            "total_losses": len(self.trade_losses),
            "total_loss_amount": sum(l["loss_amount"] for l in self.trade_losses)
        }
    
    def reset_daily_losses(self):
        """Reset daily loss tracking (call at start of new day)"""
        self.daily_losses.clear()
    
    def create_loss_result(self, entry_price: float, exit_price: float, 
                          direction: str, stop_loss_price: float,
                          exit_time: pd.Timestamp, loss_type: LossType,
                          stop_loss_type: StopLossType, target_price: float = None,
                          peak_price: float = None, instrument: str = "ES",
                          account_size: float = 10000.0) -> LossResult:
        """
        Create a comprehensive loss result object
        
        Args:
            entry_price: Trade entry price
            exit_price: Trade exit price
            direction: Trade direction
            stop_loss_price: Stop loss price that was hit
            exit_time: Exit timestamp
            loss_type: Type of loss
            stop_loss_type: Type of stop loss
            target_price: Target price (for risk-reward calculation)
            peak_price: Peak price reached (for drawdown calculation)
            instrument: Trading instrument
            account_size: Account size for percentage calculation
            
        Returns:
            LossResult object with all loss details
        """
        # Calculate loss amount
        loss_amount = self.calculate_loss_amount(entry_price, exit_price, direction, instrument)
        
        # Calculate loss percentage
        loss_percentage = self.calculate_loss_percentage(loss_amount, account_size)
        
        # Calculate risk-reward ratio
        risk_reward_ratio = 0.0
        if target_price:
            risk_reward_ratio = self.calculate_risk_reward_ratio(
                entry_price, target_price, stop_loss_price, direction
            )
        
        # Calculate max drawdown
        max_drawdown = 0.0
        if peak_price:
            max_drawdown = self.calculate_max_drawdown(entry_price, peak_price, direction)
        
        # Determine if loss is acceptable
        is_acceptable = self.validate_loss_limits(loss_amount, exit_time)
        
        # Generate loss reason
        loss_reason = self._generate_loss_reason(loss_type, stop_loss_type, loss_amount)
        
        return LossResult(
            loss_type=loss_type,
            loss_amount=loss_amount,
            loss_percentage=loss_percentage,
            stop_loss_type=stop_loss_type,
            stop_loss_price=stop_loss_price,
            exit_price=exit_price,
            exit_time=exit_time,
            risk_reward_ratio=risk_reward_ratio,
            max_drawdown=max_drawdown,
            is_acceptable_loss=is_acceptable,
            loss_reason=loss_reason
        )
    
    def _generate_loss_reason(self, loss_type: LossType, stop_loss_type: StopLossType, 
                            loss_amount: float) -> str:
        """Generate human-readable loss reason"""
        reasons = {
            LossType.STOP_LOSS: f"Stop loss hit ({stop_loss_type.value})",
            LossType.TIME_EXPIRY: "Trade expired due to time",
            LossType.BREAK_EVEN: "Trade closed at break-even",
            LossType.PARTIAL_LOSS: f"Partial loss: {loss_amount:.2f} points",
            LossType.MAX_LOSS: f"Maximum loss limit reached: {loss_amount:.2f} points"
        }
        
        return reasons.get(loss_type, "Unknown loss type")
    
    def get_stop_loss_recommendations(self, instrument: str, volatility: float = None) -> Dict:
        """
        Get stop loss recommendations based on instrument and market conditions
        
        Args:
            instrument: Trading instrument
            volatility: Current market volatility (optional)
            
        Returns:
            Dictionary with stop loss recommendations
        """
        # Base recommendations by instrument
        base_recommendations = {
            "ES": {"multiplier": 3.0, "min_distance": 2.0, "max_distance": 8.0},
            "NQ": {"multiplier": 3.0, "min_distance": 5.0, "max_distance": 20.0},
            "YM": {"multiplier": 3.0, "min_distance": 10.0, "max_distance": 40.0},
            "CL": {"multiplier": 2.5, "min_distance": 0.5, "max_distance": 2.0},
            "NG": {"multiplier": 2.5, "min_distance": 0.05, "max_distance": 0.20},
            "GC": {"multiplier": 2.0, "min_distance": 2.0, "max_distance": 10.0}
        }
        
        # Get base recommendation
        rec = base_recommendations.get(instrument.upper(), base_recommendations["ES"])
        
        # Adjust for volatility if provided
        if volatility:
            if volatility > 1.5:  # High volatility
                rec["multiplier"] *= 1.2
                rec["max_distance"] *= 1.3
            elif volatility < 0.7:  # Low volatility
                rec["multiplier"] *= 0.8
                rec["max_distance"] *= 0.7
        
        return rec
