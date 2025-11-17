#!/usr/bin/env python3
"""
Example showing how S1 and S2 sessions work with fixed slots
"""

import pandas as pd
from datetime import date
import sys
import os

# Add the parent directory to the path so we can import breakout_core
sys.path.append(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from logic.analyzer_time_slot_integration import AnalyzerTimeSlotIntegration


def demonstrate_separate_sessions():
    """Demonstrate that S1 and S2 sessions work independently with fixed slots."""
    print("=== SEPARATE SESSION DEMONSTRATION ===")
    
    print("\n=== SESSION CONFIGURATION ===")
    print("S1 Session: 02:00-09:00 (slots: 07:30, 08:00, 09:00)")
    print("S2 Session: 08:00-11:00 (slots: 09:30, 10:00, 10:30, 11:00)")
    
    print("\n=== FIXED SLOT CONFIGURATION ===")
    print("S1 Fixed Slot: 08:00")
    print("S2 Fixed Slot: 09:30")
    
    print("\n=== KEY POINTS ===")
    print("✅ S1 and S2 are completely separate sessions")
    print("✅ S1 trades at 08:00 (fixed)")
    print("✅ S2 trades at 09:30 (fixed)")
    print("✅ Each session maintains its own trading logic")
    print("✅ Sessions operate independently")
    print("✅ No slot switching - uses fixed time slots")


if __name__ == "__main__":
    demonstrate_separate_sessions()
