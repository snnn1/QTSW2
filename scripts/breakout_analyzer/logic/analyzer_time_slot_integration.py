#!/usr/bin/env python3
"""
Integration of Time Slot Switching System with Breakout Analyzer
Shows how to implement dynamic time slot selection based on performance
"""

import pandas as pd
from datetime import date, datetime
from typing import Dict, List, Optional, Tuple
import sys
import os

# Add the logic directory to path
sys.path.append(os.path.dirname(__file__))

from time_slot_logic import TimeSlotManager, TradeResult
from result_logic import ResultProcessor


class AnalyzerTimeSlotIntegration:
    """
    Integrates time slot switching with the breakout analyzer.
    This class manages dynamic time slot selection based on historical performance.
    """
    
    def __init__(self, base_window_size: int = 13):
        """
        Initialize the integration.
        
        Args:
            base_window_size: Rolling window size for performance calculations
        """
        self.slot_manager = TimeSlotManager(base_window_size)
        self.result_processor = ResultProcessor()
        self.current_active_slots: Dict[Tuple[str, date], str] = {}  # (session, date) -> current_slot
        self.session_defaults: Dict[str, str] = {}  # session -> default_slot
        
    def set_session_default_slot(self, session: str, default_slot: str):
        """
        Set the default time slot for a session.
        
        Args:
            session: Session name (e.g., "S1", "S2", "S3")
            default_slot: Default time slot (e.g., "08:00", "09:00")
        """
        self.session_defaults[session] = default_slot
        if session not in self.current_active_slots:
            self.current_active_slots[session] = default_slot
        
        # Set the default in the slot manager
        slot_key = f"{session}_{default_slot}"
        self.slot_manager.set_default_slot(slot_key)
    
    def load_historical_results(self, results_df: pd.DataFrame):
        """
        Load historical results into the time slot manager.
        
        Args:
            results_df: DataFrame with historical trade results
        """
        for _, row in results_df.iterrows():
            trade_date = pd.to_datetime(row['Date']).date()
            time_slot = row['Time']
            session = row['Session']
            result = row['Result']
            
            # Create session-specific slot key
            slot_key = f"{session}_{time_slot}"
            
            # Add to slot manager
            self.slot_manager.add_trade_result(trade_date, slot_key, result)
    
    def get_active_slot_for_session(self, session: str, trade_date: date) -> str:
        """
        Get the active time slot for a session on a given date.
        
        Args:
            session: Session name
            trade_date: Trading date
            
        Returns:
            Active time slot string
        """
        session_date_key = (session, trade_date)
        
        if session_date_key not in self.current_active_slots:
            # Use default if not set for this date
            default_slot = self.session_defaults.get(session, "08:00")
            self.current_active_slots[session_date_key] = default_slot
            return default_slot
        
        return self.current_active_slots[session_date_key]
    
    def update_slot_after_trade(self, session: str, trade_date: date, 
                               time_slot: str, result: str) -> Tuple[bool, Optional[str], str]:
        """
        Update slot selection after a trade result.
        
        Args:
            session: Session name
            trade_date: Trading date
            time_slot: Time slot that was traded
            result: Trade result
            
        Returns:
            Tuple of (should_switch, target_slot, reason)
        """
        # Create session-specific slot key
        slot_key = f"{session}_{time_slot}"
        
        # Only update current slot if this is the active slot for this session/date
        session_date_key = (session, trade_date)
        current_active_slot = self.current_active_slots.get(session_date_key)
        if time_slot == current_active_slot:
            # This is the active slot - update current slot and check for switching
            self.slot_manager.current_slot = slot_key
            
            # Check if we should switch
            should_switch, target_slot, reason = self.slot_manager.should_switch_slot(trade_date, result)
            
            if should_switch and target_slot:
                # Extract the time slot from the target (remove session prefix)
                new_time_slot = target_slot.split('_', 1)[1] if '_' in target_slot else target_slot
                self.current_active_slots[session_date_key] = new_time_slot
                return True, new_time_slot, reason
            
            return False, None, reason
        else:
            # This is not the active slot - just track performance, no switching
            return False, None, f"Not active slot (active: {current_active_slot})"
    
    def filter_ranges_by_active_slots(self, ranges: List, session: str, trade_date: date) -> List:
        """
        Filter ranges to only include the active time slot for a session.
        
        Args:
            ranges: List of range objects
            session: Session name
            trade_date: Trading date
            
        Returns:
            Filtered list of ranges for the active slot only
        """
        active_slot = self.get_active_slot_for_session(session, trade_date)
        
        # Filter ranges to only include the active time slot
        filtered_ranges = []
        for range_obj in ranges:
            if (hasattr(range_obj, 'session') and range_obj.session == session and
                hasattr(range_obj, 'end_label') and range_obj.end_label == active_slot):
                filtered_ranges.append(range_obj)
        
        return filtered_ranges
    
    def get_slot_performance_summary(self) -> Dict[str, Dict]:
        """
        Get a summary of all slot performances.
        
        Returns:
            Dictionary with performance summaries
        """
        return self.slot_manager.get_slot_summary()
    
    def print_slot_status(self, session: str):
        """
        Print the current status of time slots for a session.
        
        Args:
            session: Session name
        """
        print(f"\n=== {session} TIME SLOT STATUS (INDEPENDENT FROM OTHER SESSIONS) ===")
        current_slot = self.current_active_slots.get(session, "Not set")
        print(f"Current active slot: {current_slot}")
        
        # Get performance summary
        summary = self.get_slot_performance_summary()
        session_slots = {k: v for k, v in summary.items() if k.startswith(f"{session}_")}
        
        if session_slots:
            print(f"{session} slot performances:")
            for slot_key, perf in sorted(session_slots.items()):
                slot_name = slot_key.split('_', 1)[1] if '_' in slot_key else slot_key
                current_indicator = " (ACTIVE)" if slot_name == current_slot else ""
                print(f"  {slot_name}{current_indicator}: score={perf['rolling_sum']}, trades={perf['trade_count']}")
        else:
            print(f"No historical data available for {session}")


# Example integration with the analyzer
def integrate_with_analyzer_example():
    """
    Example showing how to integrate time slot switching with the analyzer.
    """
    print("=== ANALYZER TIME SLOT INTEGRATION EXAMPLE ===")
    
    # Initialize the integration
    integration = AnalyzerTimeSlotIntegration(base_window_size=13)
    
    # Set default slots for each session
    integration.set_session_default_slot("S1", "08:00")
    integration.set_session_default_slot("S2", "09:00")
    integration.set_session_default_slot("S3", "10:00")
    
    # Simulate loading historical results
    print("\n1. Loading historical results...")
    historical_results = pd.DataFrame([
        {"Date": "2025-01-02", "Time": "08:00", "Session": "S1", "Result": "Win"},
        {"Date": "2025-01-02", "Time": "09:00", "Session": "S1", "Result": "Loss"},
        {"Date": "2025-01-03", "Time": "08:00", "Session": "S1", "Result": "Loss"},
        {"Date": "2025-01-03", "Time": "09:00", "Session": "S1", "Result": "Win"},
        {"Date": "2025-01-04", "Time": "08:00", "Session": "S1", "Result": "BE"},
        {"Date": "2025-01-04", "Time": "09:00", "Session": "S1", "Result": "Win"},
    ])
    
    integration.load_historical_results(historical_results)
    
    # Show current status
    integration.print_slot_status("S1")
    
    # Simulate a new trade
    print("\n2. Simulating new trade...")
    trade_date = date(2025, 1, 5)
    session = "S1"
    current_slot = integration.get_active_slot_for_session(session, trade_date)
    print(f"Trading {session} at {current_slot} on {trade_date}")
    
    # Simulate a loss
    should_switch, new_slot, reason = integration.update_slot_after_trade(
        session, trade_date, current_slot, "Loss"
    )
    
    print(f"Trade result: Loss")
    print(f"Should switch: {should_switch}")
    if should_switch:
        print(f"Switch to: {new_slot}")
        print(f"Reason: {reason}")
    else:
        print(f"Reason: {reason}")
    
    # Show updated status
    integration.print_slot_status("S1")


def modify_analyzer_for_time_slot_switching():
    """
    Shows how to modify the main analyzer to support time slot switching.
    This would be integrated into the main run_strategy function.
    """
    print("\n=== MODIFYING ANALYZER FOR TIME SLOT SWITCHING ===")
    
    print("""
    To integrate time slot switching into the main analyzer:
    
    1. Add TimeSlotManager to the main analyzer:
       ```python
       from logic.analyzer_time_slot_integration import AnalyzerTimeSlotIntegration
       
       # In run_strategy function, add:
       slot_integration = AnalyzerTimeSlotIntegration(base_window_size=13)
       slot_integration.set_session_default_slot("S1", "08:00")
       slot_integration.set_session_default_slot("S2", "09:00") 
       slot_integration.set_session_default_slot("S3", "10:00")
       ```
    
    2. Filter ranges by active slots:
       ```python
       # Before processing ranges, filter by active slots:
       for session in enabled_sessions:
           active_slot = slot_integration.get_active_slot_for_session(session, trade_date)
           session_ranges = [r for r in ranges if r.session == session and r.end_label == active_slot]
           # Process only these ranges
       ```
    
    3. Update slot selection after each trade:
       ```python
       # After each trade execution:
       should_switch, new_slot, reason = slot_integration.update_slot_after_trade(
           session, trade_date, time_label, trade_execution.result_classification
       )
       if debug:
           print(f"SLOT SWITCH: {reason}")
       ```
    
    4. Add slot status to debug output:
       ```python
       if debug:
           slot_integration.print_slot_status(session)
       ```
    """)


if __name__ == "__main__":
    integrate_with_analyzer_example()
    modify_analyzer_for_time_slot_switching()
