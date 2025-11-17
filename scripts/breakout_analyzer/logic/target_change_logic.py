"""
Target Change Logic Module
Handles dynamic target progression based on trade performance peaks
"""

from typing import List, Dict, Optional, Tuple
from dataclasses import dataclass
from logic.config_logic import Instrument

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
class TargetChangeResult:
    """Result of target change calculation"""
    new_target: float
    streak_count: int
    change_reason: str
    ignored: bool = False


class TargetChangeManager:
    """Manages dynamic target progression based on trade performance"""
    
    def __init__(self):
        """Initialize target change manager"""
        self._target_histories: Dict[Instrument, List[float]] = {}
        self._streak_counts: Dict[Instrument, int] = {}
        self._current_targets: Dict[Instrument, float] = {}
        
    def round_down_to_tick(self, value: float, tick: float) -> float:
        """Round a value down to nearest tradable tick size."""
        return (value // tick) * tick
    
    def get_ladder(self, base: float) -> List[float]:
        """Return ladder levels in 50% increments of base, capped at 7 levels (4× base)."""
        return [base * (1 + 0.5 * i) for i in range(7)]
    
    def initialize_instrument(self, instrument: Instrument) -> None:
        """Initialize tracking for a new instrument"""
        if instrument not in self._target_histories:
            base = BASE_TARGETS[instrument]
            self._current_targets[instrument] = base
            self._streak_counts[instrument] = 0
            self._target_histories[instrument] = []
    
    def get_base_target(self, instrument: Instrument) -> float:
        """Get base target for an instrument"""
        return BASE_TARGETS[instrument]
    
    def get_current_target(self, instrument: Instrument) -> float:
        """Get current target for an instrument"""
        self.initialize_instrument(instrument)
        return self._current_targets[instrument]
    
    # get_tick_size moved to ConfigManager in logic/config_logic.py
    
    def update_target(self, instrument: Instrument, peak: float, debug: bool = False) -> TargetChangeResult:
        """
        Update target based on trade peak performance.
        
        Args:
            instrument: Trading instrument
            peak: Peak profit/loss reached in the trade
            debug: Whether to print debug information
            
        Returns:
            TargetChangeResult with new target and change details
        """
        self.initialize_instrument(instrument)
        
        base = BASE_TARGETS[instrument]
        tick = TICK_SIZE[instrument]
        ladder = self.get_ladder(base)
        
        # 95% threshold floored to tick
        ignore_threshold = self.round_down_to_tick(base * 0.95, tick)
        
        current_target = self._current_targets[instrument]
        current_streak = self._streak_counts[instrument]
        
        peak = self.round_down_to_tick(peak, tick)
        
        # Store the peak in history
        self._target_histories[instrument].append(peak)
        
        if debug:
            print(f"\n{instrument} target update:")
            print(f"  Peak: {peak}, Current Target: {current_target}")
            print(f"  Current Streak: {current_streak}")
        
        # --- Ignore Rule ---
        if peak < ignore_threshold:
            result = TargetChangeResult(
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
            # If current target not in ladder, use base
            current_idx = 0
            current_target = base
            self._current_targets[instrument] = base
        
        # --- Startup Rule (when at base) ---
        if current_target == base:
            if peak >= 2 * base:
                current_streak += 1
                note = f"Startup streak {current_streak}/3"
                if current_streak >= 3 and current_idx + 1 < len(ladder):
                    new_target = ladder[current_idx + 1]
                    self._current_targets[instrument] = new_target
                    self._streak_counts[instrument] = 0
                    note = f"Startup promotion → {new_target}"
                else:
                    self._streak_counts[instrument] = current_streak
                
                result = TargetChangeResult(
                    new_target=self._current_targets[instrument],
                    streak_count=self._streak_counts[instrument],
                    change_reason=note
                )
                if debug:
                    print(f"  Result: {result.change_reason}")
                return result
            else:
                self._streak_counts[instrument] = 0
                result = TargetChangeResult(
                    new_target=current_target,
                    streak_count=0,
                    change_reason=f"Reset streak (<2× base)"
                )
                if debug:
                    print(f"  Result: {result.change_reason}")
                return result
        
        # --- Demotion Rule (normal phase) ---
        if peak <= current_target:
            if current_idx > 0:
                new_target = ladder[current_idx - 1]
            else:
                new_target = base
            self._current_targets[instrument] = new_target
            self._streak_counts[instrument] = 0
            result = TargetChangeResult(
                new_target=new_target,
                streak_count=0,
                change_reason=f"Demotion → {new_target}"
            )
            if debug:
                print(f"  Result: {result.change_reason}")
            return result
        
        # --- Promotion Rule (normal phase) ---
        if current_idx + 1 < len(ladder):
            next_rung = ladder[current_idx + 1]
            if peak >= next_rung:
                current_streak += 1
                note = f"Streak {current_streak}/3 (need ≥ {next_rung})"
                if current_streak >= 3:
                    new_target = next_rung
                    self._current_targets[instrument] = new_target
                    self._streak_counts[instrument] = 0
                    note = f"Promotion → {new_target}"
                else:
                    self._streak_counts[instrument] = current_streak
                
                result = TargetChangeResult(
                    new_target=self._current_targets[instrument],
                    streak_count=self._streak_counts[instrument],
                    change_reason=note
                )
                if debug:
                    print(f"  Result: {result.change_reason}")
                return result
            else:
                self._streak_counts[instrument] = 0
                result = TargetChangeResult(
                    new_target=current_target,
                    streak_count=0,
                    change_reason=f"Reset streak (< next rung)"
                )
                if debug:
                    print(f"  Result: {result.change_reason}")
                return result
        else:
            self._streak_counts[instrument] = 0
            result = TargetChangeResult(
                new_target=current_target,
                streak_count=0,
                change_reason=f"At cap (no promotion)"
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
            self._target_histories[instrument] = []
    
    def get_target_history(self, instrument: Instrument) -> List[float]:
        """Get target history for an instrument"""
        return self._target_histories.get(instrument, [])
    
    def update_target_rolling(self, instrument: Instrument, peak: float, debug: bool = False) -> TargetChangeResult:
        """
        Update target using rolling window logic (last 3 trades) when slot switching is enabled.
        Uses the same analyzer logic but considers the last 3 trades instead of individual trades.
        """
        self.initialize_instrument(instrument)
        
        base = BASE_TARGETS[instrument]
        tick = TICK_SIZE[instrument]
        ladder = self.get_ladder(base)
        
        # 95% threshold floored to tick
        ignore_threshold = self.round_down_to_tick(base * 0.95, tick)
        
        current_target = self._current_targets[instrument]
        current_streak = self._streak_counts[instrument]
        
        peak = self.round_down_to_tick(peak, tick)
        
        # Store the peak in history
        self._target_histories[instrument].append(peak)
        
        # Get last 3 peaks for rolling window analysis
        recent_peaks = self._target_histories[instrument][-3:] if len(self._target_histories[instrument]) >= 3 else self._target_histories[instrument]
        
        if debug:
            print(f"\n{instrument} rolling target update:")
            print(f"  Current Peak: {peak}, Current Target: {current_target}")
            print(f"  Recent 3 peaks: {recent_peaks}")
            print(f"  Current Streak: {current_streak}")
        
        # --- Ignore Rule ---
        if peak < ignore_threshold:
            result = TargetChangeResult(
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
            # Don't apply any changes until we have 3 peaks
            result = TargetChangeResult(
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
                    result = TargetChangeResult(
                        new_target=new_target,
                        streak_count=0,
                        change_reason=f"Rolling startup promotion → {new_target}"
                    )
                else:
                    result = TargetChangeResult(
                        new_target=current_target,
                        streak_count=0,
                        change_reason="Rolling startup (at cap)"
                    )
            else:
                result = TargetChangeResult(
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
            result = TargetChangeResult(
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
                result = TargetChangeResult(
                    new_target=new_target,
                    streak_count=0,
                    change_reason=f"Rolling promotion → {new_target}"
                )
            else:
                result = TargetChangeResult(
                    new_target=current_target,
                    streak_count=0,
                    change_reason=f"Rolling promotion blocked (not all peaks ≥ {next_rung})"
                )
        else:
            result = TargetChangeResult(
                new_target=current_target,
                streak_count=0,
                change_reason="Rolling (at cap)"
            )
        
        if debug:
            print(f"  Result: {result.change_reason}")
        return result
    
    def get_streak_count(self, instrument: Instrument) -> int:
        """Get current streak count for an instrument"""
        return self._streak_counts.get(instrument, 0)
    
    def simulate_target_progression(self, instrument: Instrument, peaks: List[float], debug: bool = False) -> List[float]:
        """
        Simulate target progression for a sequence of peaks (for testing/analysis).
        This doesn't modify the current state.
        
        Args:
            instrument: Trading instrument
            peaks: List of peak values
            debug: Whether to print debug information
            
        Returns:
            List of targets after each trade
        """
        # Save current state
        original_target = self._current_targets.get(instrument, BASE_TARGETS[instrument])
        original_streak = self._streak_counts.get(instrument, 0)
        original_history = self._target_histories.get(instrument, [])
        
        # Reset for simulation
        self.reset_instrument(instrument)
        
        results = []
        for i, peak in enumerate(peaks):
            result = self.update_target(instrument, peak, debug)
            results.append(result.new_target)
            
            if debug:
                print(f"Trade {i+1}: Peak={peak}, Target={result.new_target}, Reason={result.change_reason}")
        
        # Restore original state
        self._current_targets[instrument] = original_target
        self._streak_counts[instrument] = original_streak
        self._target_histories[instrument] = original_history
        
        return results
    
    def get_status_summary(self, instrument: Instrument) -> Dict:
        """Get status summary for an instrument"""
        self.initialize_instrument(instrument)
        
        return {
            "instrument": instrument,
            "current_target": self._current_targets[instrument],
            "streak_count": self._streak_counts[instrument],
            "base_target": BASE_TARGETS[instrument],
            "total_trades": len(self._target_histories[instrument]),
            "recent_peaks": self._target_histories[instrument][-5:] if self._target_histories[instrument] else []
        }


# Example usage and testing
if __name__ == "__main__":
    # Test the target change logic
    manager = TargetChangeManager()
    
    # Test ES progression
    peaks_es = [20, 21, 19, 22, 23, 24, 14, 17, 22, 26, 24, 25, 20]
    print("=== ES Target Progression Test ===")
    results = manager.simulate_target_progression("ES", peaks_es, debug=True)
    print(f"Final results: {results}")
    
    # Test GC progression
    peaks_gc = [9.8, 11, 12, 13, 7.2, 12, 11, 10, 12.6, 13, 15, 12.4, 9.8]
    print("\n=== GC Target Progression Test ===")
    results = manager.simulate_target_progression("GC", peaks_gc, debug=True)
    print(f"Final results: {results}")
    
    # Test status summary
    print("\n=== Status Summary ===")
    status = manager.get_status_summary("ES")
    print(f"ES Status: {status}")
