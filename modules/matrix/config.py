"""
Centralized configuration for Master Matrix.

This module contains all configuration constants used across the master matrix
and timetable engine modules. This ensures consistency and makes it easy to
update values in one place.
"""

# Session time slots - SINGLE SOURCE OF TRUTH
# These define the canonical trading time slots for each session
SLOT_ENDS = {
    "S1": ["07:30", "08:00", "09:00"],
    "S2": ["09:30", "10:00", "10:30", "11:00"],
}

# Rolling window size for time slot history
ROLLING_WINDOW_SIZE = 13

# Day-of-month blocked days for "2" streams (e.g., ES2, GC2)
DOM_BLOCKED_DAYS = {4, 16, 30}

# SCF threshold for timetable engine
SCF_THRESHOLD = 0.5

# Rolling window update configuration
MATRIX_REPROCESS_TRADING_DAYS = 35
MATRIX_CHECKPOINT_FREQUENCY = "weekly"  # "weekly" or "monthly"

__all__ = ['SLOT_ENDS', 'ROLLING_WINDOW_SIZE', 'DOM_BLOCKED_DAYS', 'SCF_THRESHOLD', 
           'MATRIX_REPROCESS_TRADING_DAYS', 'MATRIX_CHECKPOINT_FREQUENCY']

