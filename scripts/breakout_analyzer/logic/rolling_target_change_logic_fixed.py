"""
Fixed Rolling Target Change Logic Module
Handles dynamic target progression based on rolling window of actual trades
when slot switching is enabled. Implements proper ladder system with +50% increments.
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
    instrument: str
    peak: float
    profit: float
    is_win: bool
    is_loss: bool
    is_breakeven: bool
    is_notrade: bool
    slot: str

@dataclass
class RollingTargetChangeResult:
    """Result of rolling target change analysis"""
    new_target: float
    streak_count: int
    change_reason: str
    ignored: bool = False
    trades_analyzed: int = 0

class RollingTargetChangeManager:
    """
    Manages target changes based on rolling window of trades.
    Implements proper ladder system with +50% increments.
    """
    
    def __init__(self, window_size: int = 3):
        self.window_size = window_size
        self._trade_records: Dict[str, deque] = {}  # instrument -> deque of TradeRecord
        self._current_targets: Dict[str, float] = {}  # instrument -> current target
        self._streak_counts: Dict[str, int] = {}  # instrument -> current streak count
        self._ladders: Dict[str, List[float]] = {}  # instrument -> ladder of targets
        
        # Build ladders for each instrument
        for instrument in BASE_TARGETS:
            self._ladders[instrument] = self._build_ladder(instrument)
    
    def _build_ladder(self, instrument: str) -> List[float]:
        """Build ladder with proper progression: base, 2×, 3×, 4×, capped at max target"""
        base = BASE_TARGETS[instrument]
        ladder = [base]  # Start with base target
        
        # Define max targets for each instrument
        max_targets = {
            "ES": 40,
            "NQ": 200,
            "GC": 20,
            "NG": 0.2,
            "YM": 400,
            "CL": 2.0,
            # Micro futures use same max targets
            "MES": 40,
            "MNQ": 200,
            "MGC": 20,
            "MNG": 0.2,
            "MYM": 400,
            "MCL": 2.0,
        }
        
        max_target = max_targets.get(instrument, base * 4)  # Default to 4× base if not specified
        
        # Build ladder: base, 2×, 3×, 4× (capped at max_target)
        multipliers = [2, 3, 4]
        for mult in multipliers:
            target = base * mult
            rounded_target = self.round_down_to_tick(target, TICK_SIZE[instrument])
            if rounded_target <= max_target:
                ladder.append(rounded_target)
            else:
                break
        
        return ladder
    
    def round_down_to_tick(self, value: float, tick_size: float) -> float:
        """Round down to nearest tick"""
        return (value // tick_size) * tick_size
    
    def initialize_instrument(self, instrument: str):
        """Initialize instrument with base target"""
        if instrument not in self._current_targets:
            self._current_targets[instrument] = BASE_TARGETS[instrument]
            self._trade_records[instrument] = deque(maxlen=self.window_size)
            self._streak_counts[instrument] = 0
    
    def get_current_target(self, instrument: str) -> float:
        """Get current target for instrument"""
        if instrument not in self._current_targets:
            self._current_targets[instrument] = BASE_TARGETS[instrument]
        return self._current_targets[instrument]
    
    def update_target_rolling(self, instrument: Instrument, peak: float, debug: bool = False) -> RollingTargetChangeResult:
        """
        Update target using rolling window logic (last 3 trades) when slot switching is enabled.
        Uses the same analyzer logic but considers the last 3 trades instead of individual trades.
        """
        self.initialize_instrument(instrument)

        base = BASE_TARGETS[instrument]
        tick = TICK_SIZE[instrument]
        ladder = self._ladders[instrument]

        # 95% threshold floored to tick
        ignore_threshold = self.round_down_to_tick(base * 0.95, tick)

        current_target = self._current_targets[instrument]
        current_streak = self._streak_counts[instrument]

        peak = self.round_down_to_tick(peak, tick)

        # Create a trade record for this peak
        trade = TradeRecord(
            instrument=instrument,
            peak=peak,
            profit=0.0,  # We don't have profit info here
            is_win=False,  # We don't have result info here
            is_loss=False,
            is_breakeven=False,
            is_notrade=False,
            slot="Unknown"  # We don't have slot info here
        )

        # Add to rolling window
        self._trade_records[instrument].append(trade)

        # Get last 3 peaks for rolling window analysis
        recent_peaks = [t.peak for t in self._trade_records[instrument]]

        if debug:
            print(f"\n{instrument} rolling target update:")
            print(f"  Current Peak: {peak}, Current Target: {current_target}")
            print(f"  Recent 3 peaks: {recent_peaks}")
            print(f"  Current Streak: {current_streak}")

        # --- Ignore Rule ---
        if peak < ignore_threshold:
            result = RollingTargetChangeResult(
                new_target=current_target,
                streak_count=current_streak,
                change_reason=f"Ignored (<95% base={ignore_threshold})",
                ignored=True
            )
            if debug:
                print(f"  Result: {result.change_reason}")
            return result

        # Find current target position in ladder
        try:
            current_idx = ladder.index(current_target)
        except ValueError:
            current_idx = 0
            current_target = base
            self._current_targets[instrument] = base

        # --- Rolling Window Logic ---
        # Need at least 3 peaks to make decisions
        if len(recent_peaks) < 3:
            result = RollingTargetChangeResult(
                new_target=current_target,
                streak_count=current_streak,
                change_reason=f"Not enough data ({len(recent_peaks)}/3 peaks)"
            )
            if debug:
                print(f"  Result: {result.change_reason}")
            return result

        # --- Startup Rule (when at base) ---
        if current_target == base:
            # Check if all 3 recent peaks are >= 2× base
            if all(p >= 2 * base for p in recent_peaks):
                if current_idx + 1 < len(ladder):
                    new_target = ladder[current_idx + 1]
                    self._current_targets[instrument] = new_target
                    self._streak_counts[instrument] = 0
                    result = RollingTargetChangeResult(
                        new_target=new_target,
                        streak_count=0,
                        change_reason=f"Rolling startup promotion → {new_target}"
                    )
                else:
                    result = RollingTargetChangeResult(
                        new_target=current_target,
                        streak_count=0,
                        change_reason="Rolling startup (at cap)"
                    )
            else:
                result = RollingTargetChangeResult(
                    new_target=current_target,
                    streak_count=0,
                    change_reason="Rolling startup blocked (not all peaks ≥ 2× base)"
                )
            if debug:
                print(f"  Result: {result.change_reason}")
            return result

        # --- Demotion Rule (normal phase) ---
        # Check if any recent peak is <= current target
        if any(p <= current_target for p in recent_peaks):
            if current_idx > 0:
                new_target = ladder[current_idx - 1]
            else:
                new_target = base
            self._current_targets[instrument] = new_target
            self._streak_counts[instrument] = 0
            result = RollingTargetChangeResult(
                new_target=new_target,
                streak_count=0,
                change_reason=f"Rolling demotion → {new_target}"
            )
            if debug:
                print(f"  Result: {result.change_reason}")
            return result

        # --- Promotion Rule (normal phase) ---
        if current_idx + 1 < len(ladder):
            next_rung = ladder[current_idx + 1]
            # Check if all 3 recent peaks are >= next rung
            if all(p >= next_rung for p in recent_peaks):
                new_target = next_rung
                self._current_targets[instrument] = new_target
                self._streak_counts[instrument] = 0
                result = RollingTargetChangeResult(
                    new_target=new_target,
                    streak_count=0,
                    change_reason=f"Rolling promotion → {new_target}"
                )
            else:
                result = RollingTargetChangeResult(
                    new_target=current_target,
                    streak_count=0,
                    change_reason=f"Rolling promotion blocked (not all peaks ≥ {next_rung})"
                )
        else:
            result = RollingTargetChangeResult(
                new_target=current_target,
                streak_count=0,
                change_reason="Rolling (at cap)"
            )

        if debug:
            print(f"  Result: {result.change_reason}")
        return result
    
    def process_trade(self, trade: TradeRecord, debug: bool = False) -> RollingTargetChangeResult:
        """
        Process a trade and determine if target should change.
        Implements proper ladder system rules.
        """
        instrument = trade.instrument
        
        # Initialize if needed
        if instrument not in self._trade_records:
            self._trade_records[instrument] = deque(maxlen=self.window_size)
            self._current_targets[instrument] = BASE_TARGETS[instrument]
            self._streak_counts[instrument] = 0
        
        # Add trade to rolling window
        self._trade_records[instrument].append(trade)
        
        current_target = self.get_current_target(instrument)
        ladder = self._ladders[instrument]
        base = BASE_TARGETS[instrument]
        tick = TICK_SIZE[instrument]
        
        # Find current rung index
        current_idx = ladder.index(current_target)
        
        # Ignore rule: peaks below 95% of base don't affect the ladder
        ignore_threshold = base * 0.95
        if trade.peak < ignore_threshold:
            result = RollingTargetChangeResult(
                new_target=current_target,
                streak_count=self._streak_counts[instrument],
                change_reason=f"Ignored (peak {trade.peak} < 95% base={ignore_threshold})",
                ignored=True,
                trades_analyzed=len(self._trade_records[instrument])
            )
            if debug:
                print(f"  Result: {result.change_reason}")
            return result
        
        # Demotion rule: any peak ≤ current target drops to rung below
        if trade.peak <= current_target:
            if current_idx > 0:  # Can demote
                new_target = ladder[current_idx - 1]
                self._current_targets[instrument] = new_target
                self._streak_counts[instrument] = 0
                result = RollingTargetChangeResult(
                    new_target=new_target,
                    streak_count=0,
                    change_reason=f"Demotion → {new_target} (peak {trade.peak} ≤ {current_target})",
                    trades_analyzed=len(self._trade_records[instrument])
                )
                if debug:
                    print(f"  Result: {result.change_reason}")
                return result
            else:  # Already at base, no demotion possible
                result = RollingTargetChangeResult(
                    new_target=current_target,
                    streak_count=0,
                    change_reason=f"Demotion blocked (already at base {current_target})",
                    trades_analyzed=len(self._trade_records[instrument])
                )
                if debug:
                    print(f"  Result: {result.change_reason}")
                return result
        
        # Check if we have enough trades for promotion
        recent_trades = list(self._trade_records[instrument])
        if len(recent_trades) < 3:
            result = RollingTargetChangeResult(
                new_target=current_target,
                streak_count=self._streak_counts[instrument],
                change_reason=f"Not enough data ({len(recent_trades)}/3 trades)",
                trades_analyzed=len(recent_trades)
            )
            if debug:
                print(f"  Result: {result.change_reason}")
            return result
        
        # Special case: Base to First Rung
        if current_idx == 0:  # At base
            # Need 3 consecutive peaks ≥ 2× base
            required_threshold = base * 2
            recent_peaks = [t.peak for t in recent_trades[-3:]]
            
            if all(peak >= required_threshold for peak in recent_peaks):
                new_target = ladder[1]  # Move to first rung
                self._current_targets[instrument] = new_target
                self._streak_counts[instrument] = 0
                result = RollingTargetChangeResult(
                    new_target=new_target,
                    streak_count=0,
                    change_reason=f"Base→First Rung → {new_target} (3 peaks ≥ {required_threshold})",
                    trades_analyzed=len(recent_trades)
                )
                if debug:
                    print(f"  Result: {result.change_reason}")
                return result
            else:
                result = RollingTargetChangeResult(
                    new_target=current_target,
                    streak_count=0,
                    change_reason=f"Base→First Rung blocked (peaks: {recent_peaks}, need ≥ {required_threshold})",
                    trades_analyzed=len(recent_trades)
                )
                if debug:
                    print(f"  Result: {result.change_reason}")
                return result
        
        # Normal case: Rung to Next Rung
        if current_idx < len(ladder) - 1:  # Not at cap
            next_rung = ladder[current_idx + 1]
            recent_peaks = [t.peak for t in recent_trades[-3:]]
            
            if all(peak >= next_rung for peak in recent_peaks):
                new_target = next_rung
                self._current_targets[instrument] = new_target
                self._streak_counts[instrument] = 0
                result = RollingTargetChangeResult(
                    new_target=new_target,
                    streak_count=0,
                    change_reason=f"Rung→Next → {new_target} (3 peaks ≥ {next_rung})",
                    trades_analyzed=len(recent_trades)
                )
                if debug:
                    print(f"  Result: {result.change_reason}")
                return result
            else:
                result = RollingTargetChangeResult(
                    new_target=current_target,
                    streak_count=0,
                    change_reason=f"Rung→Next blocked (peaks: {recent_peaks}, need ≥ {next_rung})",
                    trades_analyzed=len(recent_trades)
                )
                if debug:
                    print(f"  Result: {result.change_reason}")
                return result
        
        # At cap: no more promotions
        result = RollingTargetChangeResult(
            new_target=current_target,
            streak_count=0,
            change_reason=f"At cap {current_target} (no more promotions)",
            trades_analyzed=len(recent_trades)
        )
        if debug:
            print(f"  Result: {result.change_reason}")
        return result