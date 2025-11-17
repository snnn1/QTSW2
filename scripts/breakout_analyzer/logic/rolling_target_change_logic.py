"""
Rolling Target Change Logic Module
Handles dynamic target progression based on rolling window of actual trades
when slot switching is enabled. Looks at last 3 rows to calculate target changes.
"""

from typing import List, Dict, Optional, Tuple
from dataclasses import dataclass
from logic.config_logic import Instrument
from collections import deque

# Base targets for each instrument
BASE_TARGETS = {
    "ES": 10,
    "NQ": 50,
    "GC": 5,
    "NG": 0.05,
    "YM": 100,
    "CL": 0.5,
    # Micro futures use same base targets
    "MES": 10,
    "MNQ": 50,
    "MGC": 5,
    "MNG": 0.05,
    "MYM": 100,
    "MCL": 0.5,
}

# Tick sizes imported from breakout_core.config


@dataclass
class TradeRecord:
    """Record of a completed trade for rolling analysis"""
    date: str
    session: str
    time_slot: str
    peak: float
    result: str
    target_used: float


@dataclass
class RollingTargetChangeResult:
    """Result of rolling target change calculation"""
    new_target: float
    streak_count: int
    change_reason: str
    ignored: bool = False
    trades_analyzed: int = 0


class RollingTargetChangeManager:
    """
    Manages dynamic target progression based on rolling window of actual trades.
    When slot switching is enabled, this looks at the last 3 actual trades to make decisions.
    """
    
    def __init__(self, window_size: int = 3):
        """
        Initialize the rolling target change manager.
        
        Args:
            window_size: Number of recent trades to analyze (default 3)
        """
        self.window_size = window_size
        self._trade_history: Dict[Instrument, deque] = {}
        self._current_targets: Dict[Instrument, float] = {}
        self._streak_counts: Dict[Instrument, int] = {}
        
    def round_down_to_tick(self, value: float, tick: float) -> float:
        """Round a value down to nearest tradable tick size."""
        return (value // tick) * tick
    
    def get_ladder(self, base: float) -> List[float]:
        """Return ladder levels in 50% increments of base, capped at 7 levels (4× base)."""
        return [base * (1 + 0.5 * i) for i in range(7)]
    
    def initialize_instrument(self, instrument: Instrument) -> None:
        """Initialize tracking for a new instrument"""
        if instrument not in self._trade_history:
            base = BASE_TARGETS[instrument]
            self._current_targets[instrument] = base
            self._streak_counts[instrument] = 0
            self._trade_history[instrument] = deque(maxlen=50)  # Keep last 50 trades
    
    def get_base_target(self, instrument: Instrument) -> float:
        """Get base target for an instrument"""
        return BASE_TARGETS[instrument]
    
    def get_current_target(self, instrument: Instrument) -> float:
        """Get current target for an instrument"""
        self.initialize_instrument(instrument)
        return self._current_targets[instrument]
    
    # get_tick_size moved to ConfigManager in logic/config_logic.py
    
    def add_trade_record(self, instrument: Instrument, trade_record: TradeRecord):
        """
        Add a completed trade record to the history.
        
        Args:
            instrument: Trading instrument
            trade_record: Completed trade record
        """
        self.initialize_instrument(instrument)
        self._trade_history[instrument].append(trade_record)
    
    def update_target_rolling(self, instrument: Instrument, debug: bool = False) -> RollingTargetChangeResult:
        """
        Update target based on rolling window of recent trades.
        This is the key method that looks at the last N trades to make decisions.
        
        Args:
            instrument: Trading instrument
            debug: Whether to print debug information
            
        Returns:
            RollingTargetChangeResult with new target and change details
        """
        self.initialize_instrument(instrument)
        
        base = BASE_TARGETS[instrument]
        tick = TICK_SIZE[instrument]
        ladder = self.get_ladder(base)
        
        # 95% threshold floored to tick
        ignore_threshold = self.round_down_to_tick(base * 0.95, tick)
        
        current_target = self._current_targets[instrument]
        current_streak = self._streak_counts[instrument]
        
        # Get recent trades (last window_size trades)
        recent_trades = list(self._trade_history[instrument])[-self.window_size:]
        
        if debug:
            print(f"\n{instrument} rolling target update (last {len(recent_trades)} trades):")
            print(f"  Current Target: {current_target}")
            print(f"  Current Streak: {current_streak}")
            for i, trade in enumerate(recent_trades):
                print(f"  Trade {i+1}: {trade.date} {trade.time_slot} peak={trade.peak} target_used={trade.target_used}")
        
        if len(recent_trades) == 0:
            return RollingTargetChangeResult(
                new_target=current_target,
                streak_count=current_streak,
                change_reason="No trades in history",
                ignored=True,
                trades_analyzed=0
            )
        
        # Find current target position in ladder
        try:
            current_idx = ladder.index(current_target)
        except ValueError:
            # If current target not in ladder, use base
            current_idx = 0
            current_target = base
            self._current_targets[instrument] = base
        
        # --- Ignore Rule ---
        # Check if any recent trade had a peak below ignore threshold
        recent_peaks = [self.round_down_to_tick(trade.peak, tick) for trade in recent_trades]
        min_peak = min(recent_peaks) if recent_peaks else 0
        
        if min_peak < ignore_threshold:
            result = RollingTargetChangeResult(
                new_target=current_target,
                streak_count=current_streak,
                change_reason=f"Ignored (min peak {min_peak} < 95% base={ignore_threshold})",
                ignored=True,
                trades_analyzed=len(recent_trades)
            )
            if debug:
                print(f"  Result: {result.change_reason}")
            return result
        
        # --- Startup Rule (when at base) ---
        if current_target == base:
            # Check if we have enough trades and all hit 2× base
            if len(recent_trades) >= 3:
                qualifying_trades = [t for t in recent_trades if self.round_down_to_tick(t.peak, tick) >= 2 * base]
                if len(qualifying_trades) >= 3:
                    # All 3 recent trades qualified for startup promotion
                    if current_idx + 1 < len(ladder):
                        new_target = ladder[current_idx + 1]
                        self._current_targets[instrument] = new_target
                        self._streak_counts[instrument] = 0
                        result = RollingTargetChangeResult(
                            new_target=new_target,
                            streak_count=0,
                            change_reason=f"Startup promotion → {new_target} (3/3 trades ≥ 2× base)",
                            trades_analyzed=len(recent_trades)
                        )
                    else:
                        result = RollingTargetChangeResult(
                            new_target=current_target,
                            streak_count=0,
                            change_reason="At cap (no promotion possible)",
                            trades_analyzed=len(recent_trades)
                        )
                else:
                    result = RollingTargetChangeResult(
                        new_target=current_target,
                        streak_count=0,
                        change_reason=f"Startup reset ({len(qualifying_trades)}/3 trades ≥ 2× base)",
                        trades_analyzed=len(recent_trades)
                    )
            else:
                result = RollingTargetChangeResult(
                    new_target=current_target,
                    streak_count=0,
                    change_reason=f"Startup waiting ({len(recent_trades)}/3 trades)",
                    trades_analyzed=len(recent_trades)
                )
            
            if debug:
                print(f"  Result: {result.change_reason}")
            return result
        
        # --- Demotion Rule (normal phase) ---
        # Check if any recent trade had peak <= current target
        demotion_trades = [t for t in recent_trades if self.round_down_to_tick(t.peak, tick) <= current_target]
        if demotion_trades:
            if current_idx > 0:
                new_target = ladder[current_idx - 1]
            else:
                new_target = base
            self._current_targets[instrument] = new_target
            self._streak_counts[instrument] = 0
            result = RollingTargetChangeResult(
                new_target=new_target,
                streak_count=0,
                change_reason=f"Demotion → {new_target} (peak ≤ current target in recent trades)",
                trades_analyzed=len(recent_trades)
            )
            if debug:
                print(f"  Result: {result.change_reason}")
            return result
        
        # --- Promotion Rule (normal phase) ---
        if current_idx + 1 < len(ladder):
            next_rung = ladder[current_idx + 1]
            # Check if we have enough trades and all hit next rung
            if len(recent_trades) >= 3:
                qualifying_trades = [t for t in recent_trades if self.round_down_to_tick(t.peak, tick) >= next_rung]
                if len(qualifying_trades) >= 3:
                    # All 3 recent trades qualified for promotion
                    new_target = next_rung
                    self._current_targets[instrument] = new_target
                    self._streak_counts[instrument] = 0
                    result = RollingTargetChangeResult(
                        new_target=new_target,
                        streak_count=0,
                        change_reason=f"Promotion → {new_target} (3/3 trades ≥ {next_rung})",
                        trades_analyzed=len(recent_trades)
                    )
                else:
                    result = RollingTargetChangeResult(
                        new_target=current_target,
                        streak_count=0,
                        change_reason=f"Promotion waiting ({len(qualifying_trades)}/3 trades ≥ {next_rung})",
                        trades_analyzed=len(recent_trades)
                    )
            else:
                result = RollingTargetChangeResult(
                    new_target=current_target,
                    streak_count=0,
                    change_reason=f"Promotion waiting ({len(recent_trades)}/3 trades)",
                    trades_analyzed=len(recent_trades)
                )
            
            if debug:
                print(f"  Result: {result.change_reason}")
            return result
        else:
            result = RollingTargetChangeResult(
                new_target=current_target,
                streak_count=0,
                change_reason="At cap (no promotion possible)",
                trades_analyzed=len(recent_trades)
            )
            if debug:
                print(f"  Result: {result.change_reason}")
            return result
    
    def reset_instrument(self, instrument: Instrument) -> None:
        """Reset target progression for an instrument"""
        if instrument in self._current_targets:
            base = BASE_TARGETS[instrument]
            self._current_targets[instrument] = base
            self._streak_counts[instrument] = 0
            self._trade_history[instrument] = deque(maxlen=50)
    
    def get_trade_history(self, instrument: Instrument) -> List[TradeRecord]:
        """Get trade history for an instrument"""
        return list(self._trade_history.get(instrument, []))
    
    def get_status_summary(self, instrument: Instrument) -> Dict:
        """Get status summary for an instrument"""
        self.initialize_instrument(instrument)
        
        recent_trades = list(self._trade_history[instrument])[-self.window_size:]
        
        return {
            "instrument": instrument,
            "current_target": self._current_targets[instrument],
            "streak_count": self._streak_counts[instrument],
            "base_target": BASE_TARGETS[instrument],
            "total_trades": len(self._trade_history[instrument]),
            "recent_trades": recent_trades,
            "window_size": self.window_size
        }


# Example usage and testing
if __name__ == "__main__":
    # Test the rolling target change logic
    manager = RollingTargetChangeManager(window_size=3)
    
    # Test ES progression with rolling window
    print("=== ES Rolling Target Progression Test ===")
    
    # Simulate trades
    test_trades = [
        TradeRecord("2025-01-02", "S1", "08:00", 20.0, "Win", 10.0),
        TradeRecord("2025-01-03", "S1", "08:00", 21.0, "Win", 10.0),
        TradeRecord("2025-01-04", "S1", "09:00", 19.0, "Win", 10.0),  # Slot switched
    ]
    
    for trade in test_trades:
        manager.add_trade_record("ES", trade)
        result = manager.update_target_rolling("ES", debug=True)
        print(f"After trade {trade.date} {trade.time_slot}: Target = {result.new_target}")
    
    # Test status summary
    print("\n=== Status Summary ===")
    status = manager.get_status_summary("ES")
    print(f"ES Status: {status}")
