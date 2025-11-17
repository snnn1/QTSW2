#!/usr/bin/env python3
"""
Time Slot Switching Logic for Trading Strategy
Implements scoring system and switching rules based on rolling performance
"""

import pandas as pd
from datetime import datetime, date, time
from typing import Dict, List, Optional, Tuple
from dataclasses import dataclass
from collections import defaultdict, deque
import numpy as np


@dataclass
class TradeResult:
    """Represents a single trade result."""
    date: date
    time_slot: str  # e.g., "08:00", "09:00"
    result: str  # "Win", "Loss", "BE", "NoTrade"
    score: int  # +1 for Win, -2 for Loss, 0 for BE/NoTrade


@dataclass
class SlotPerformance:
    """Performance metrics for a time slot."""
    time_slot: str
    rolling_sum: int
    trade_count: int
    window_size: int
    recent_trades: List[TradeResult]


class TimeSlotManager:
    """Manages time slot switching based on rolling performance scores."""
    
    def __init__(self, base_window_size: int = 13):
        """
        Initialize the time slot manager.
        
        Args:
            base_window_size: Base rolling window size for calculations
        """
        self.base_window_size = base_window_size
        self.slot_histories: Dict[str, deque] = defaultdict(lambda: deque(maxlen=50))  # Keep last 50 trades per slot
        self.current_slot: Optional[str] = None
        self.default_slot: Optional[str] = None
        
    def set_default_slot(self, slot: str):
        """Set the default time slot to start with."""
        self.default_slot = slot
        if self.current_slot is None:
            self.current_slot = slot
    
    def add_trade_result(self, trade_date: date, time_slot: str, result: str):
        """
        Add a trade result to the appropriate slot history.
        
        Args:
            trade_date: Date of the trade
            time_slot: Time slot (e.g., "08:00", "09:00")
            result: Trade result ("Win", "Loss", "BE", "NoTrade")
        """
        score = self._calculate_score(result)
        trade_result = TradeResult(
            date=trade_date,
            time_slot=time_slot,
            result=result,
            score=score
        )
        
        self.slot_histories[time_slot].append(trade_result)
    
    def _calculate_score(self, result: str) -> int:
        """
        Calculate score based on trade result.
        
        Args:
            result: Trade result string
            
        Returns:
            Score: +1 for Win, -2 for Loss, 0 for BE/NoTrade/Time
        """
        result_upper = result.upper()
        if result_upper == "WIN":
            return 1
        elif result_upper == "LOSS":
            return -2
        elif result_upper in ["BE", "BREAK_EVEN", "BREAKEVEN"]:
            return 0
        elif result_upper in ["NOTRADE", "NO_TRADE"]:
            return 0
        elif result_upper == "TIME":
            return 0
        else:
            # Default to 0 for unknown results
            return 0
    
    def get_slot_performance(self, time_slot: str, window_size: Optional[int] = None) -> SlotPerformance:
        """
        Get performance metrics for a specific time slot.
        
        Args:
            time_slot: Time slot to analyze
            window_size: Rolling window size (uses base_window_size if None)
            
        Returns:
            SlotPerformance object with metrics
        """
        if window_size is None:
            window_size = self.base_window_size
        
        history = self.slot_histories[time_slot]
        
        if len(history) == 0:
            return SlotPerformance(
                time_slot=time_slot,
                rolling_sum=0,
                trade_count=0,
                window_size=window_size,
                recent_trades=[]
            )
        
        # Get the last N trades (or all if fewer than N)
        recent_trades = list(history)[-window_size:] if len(history) >= window_size else list(history)
        
        # Calculate rolling sum
        rolling_sum = sum(trade.score for trade in recent_trades)
        
        return SlotPerformance(
            time_slot=time_slot,
            rolling_sum=rolling_sum,
            trade_count=len(recent_trades),
            window_size=len(recent_trades),
            recent_trades=recent_trades
        )
    
    def get_all_slot_performances(self, window_size: Optional[int] = None) -> Dict[str, SlotPerformance]:
        """
        Get performance metrics for all time slots.
        
        Args:
            window_size: Rolling window size
            
        Returns:
            Dictionary mapping time slots to their performance metrics
        """
        performances = {}
        for slot in self.slot_histories.keys():
            performances[slot] = self.get_slot_performance(slot, window_size)
        
        return performances
    
    def find_best_alternative_slot(self, exclude_slot: str, window_size: Optional[int] = None) -> Tuple[Optional[str], int]:
        """
        Find the best alternative time slot (excluding the specified slot).
        
        Args:
            exclude_slot: Time slot to exclude from consideration
            window_size: Rolling window size
            
        Returns:
            Tuple of (best_slot, best_score) or (None, 0) if no alternatives
        """
        performances = self.get_all_slot_performances(window_size)
        
        # Remove the excluded slot
        if exclude_slot in performances:
            del performances[exclude_slot]
        
        if not performances:
            return None, 0
        
        # Find the slot with the highest rolling sum
        best_slot = max(performances.keys(), key=lambda slot: performances[slot].rolling_sum)
        best_score = performances[best_slot].rolling_sum
        
        return best_slot, best_score
    
    def should_switch_slot(self, trade_date: date, current_result: str) -> Tuple[bool, Optional[str], str]:
        """
        Determine if we should switch time slots based on the rules.
        
        Args:
            trade_date: Date of the current trade
            current_result: Result of the current trade
            
        Returns:
            Tuple of (should_switch, target_slot, reason)
        """
        if self.current_slot is None:
            return False, None, "No current slot set"
        
        # Add the current trade result to history
        self.add_trade_result(trade_date, self.current_slot, current_result)
        
        # Get current slot performance
        current_performance = self.get_slot_performance(self.current_slot)
        current_score = current_performance.rolling_sum
        
        # Find best alternative
        best_alternative, best_score = self.find_best_alternative_slot(self.current_slot)
        
        if best_alternative is None:
            return False, None, "No alternative slots available"
        
        # Rule 1: Switch if current slot loses today AND another slot has higher rolling sum
        if current_result.upper() == "LOSS" and best_score > current_score:
            return True, best_alternative, f"Current slot lost and {best_alternative} has higher score ({best_score} vs {current_score})"
        
        # Rule 2: Switch if current slot loses today AND another slot is ≥ 5 points higher
        if current_result.upper() == "LOSS" and best_score >= current_score + 5:
            return True, best_alternative, f"Current slot lost and {best_alternative} is ≥5 points higher ({best_score} vs {current_score})"
        
        # Check for ties and extend window if needed (only if current slot lost)
        if current_result.upper() == "LOSS" and best_score == current_score:
            # Extend window size until we have a clear winner
            extended_window = self._resolve_ties()
            if extended_window is not None:
                extended_performance = self.get_all_slot_performances(extended_window)
                if self.current_slot in extended_performance and best_alternative in extended_performance:
                    current_extended = extended_performance[self.current_slot].rolling_sum
                    best_extended = extended_performance[best_alternative].rolling_sum
                    
                    if best_extended > current_extended:
                        return True, best_alternative, f"Current slot lost, extended window ({extended_window}) shows {best_alternative} ahead ({best_extended} vs {current_extended})"
        
        return False, None, f"Stay on {self.current_slot} (score: {current_score})"
    
    def _resolve_ties(self, max_window: int = 20) -> Optional[int]:
        """
        Resolve ties by extending the rolling window.
        
        Args:
            max_window: Maximum window size to try
            
        Returns:
            Window size that resolves ties, or None if no resolution found
        """
        for window_size in range(self.base_window_size + 1, max_window + 1):
            performances = self.get_all_slot_performances(window_size)
            
            if len(performances) < 2:
                continue
            
            # Get all scores
            scores = [perf.rolling_sum for perf in performances.values()]
            
            # Check if there's a clear winner
            max_score = max(scores)
            max_count = scores.count(max_score)
            
            if max_count == 1:
                return window_size
        
        return None
    
    def switch_to_slot(self, new_slot: str):
        """
        Switch to a new time slot.
        
        Args:
            new_slot: New time slot to switch to
        """
        self.current_slot = new_slot
    
    def get_current_slot(self) -> Optional[str]:
        """Get the current active time slot."""
        return self.current_slot
    
    def get_slot_summary(self) -> Dict[str, Dict]:
        """
        Get a summary of all time slots.
        
        Returns:
            Dictionary with slot summaries
        """
        summary = {}
        performances = self.get_all_slot_performances()
        
        for slot, perf in performances.items():
            summary[slot] = {
                'rolling_sum': perf.rolling_sum,
                'trade_count': perf.trade_count,
                'window_size': perf.window_size,
                'is_current': slot == self.current_slot,
                'recent_results': [trade.result for trade in perf.recent_trades[-5:]]  # Last 5 results
            }
        
        return summary
    
    def reset_slot_histories(self):
        """Reset all slot histories (useful for testing)."""
        self.slot_histories.clear()
        self.current_slot = self.default_slot


# Example usage and testing
def test_time_slot_manager():
    """Test the TimeSlotManager functionality."""
    print("=== Testing TimeSlotManager ===")
    
    manager = TimeSlotManager(base_window_size=13)
    manager.set_default_slot("08:00")
    
    # Simulate some trade results
    print("\n1. Adding trade results...")
    test_results = [
        (date(2025, 1, 2), "08:00", "Win"),
        (date(2025, 1, 2), "09:00", "Loss"),
        (date(2025, 1, 3), "08:00", "Loss"),
        (date(2025, 1, 3), "09:00", "Win"),
        (date(2025, 1, 4), "08:00", "BE"),
        (date(2025, 1, 4), "09:00", "Win"),
        (date(2025, 1, 5), "08:00", "Loss"),
        (date(2025, 1, 5), "09:00", "Win"),
    ]
    
    for trade_date, slot, result in test_results:
        manager.add_trade_result(trade_date, slot, result)
        print(f"  {trade_date} {slot}: {result}")
    
    # Test slot performances
    print("\n2. Slot Performances:")
    performances = manager.get_all_slot_performances()
    for slot, perf in performances.items():
        print(f"  {slot}: score={perf.rolling_sum}, trades={perf.trade_count}")
    
    # Test switching logic
    print("\n3. Testing Switching Logic:")
    should_switch, target_slot, reason = manager.should_switch_slot(date(2025, 1, 6), "Loss")
    print(f"  Should switch: {should_switch}")
    print(f"  Target slot: {target_slot}")
    print(f"  Reason: {reason}")
    
    # Test slot summary
    print("\n4. Slot Summary:")
    summary = manager.get_slot_summary()
    for slot, info in summary.items():
        current_indicator = " (CURRENT)" if info['is_current'] else ""
        print(f"  {slot}{current_indicator}: score={info['rolling_sum']}, recent={info['recent_results']}")


if __name__ == "__main__":
    test_time_slot_manager()

