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

# Date normalization configuration
# Strict mode (default): Invalid dates cause contract violations (fail-closed)
# Salvage mode (opt-in): Invalid dates are dropped or stream fails with salvage report
# Salvage mode never propagates NaT downstream
ALLOW_INVALID_DATES_SALVAGE = False  # Set to True only for debugging

# Critical streams configuration
# Streams marked as critical will cause ERROR if empty (fail-closed)
# Non-critical streams will cause WARN if empty (continue processing)
CRITICAL_STREAMS = {'ES1', 'ES2', 'GC1', 'GC2'}  # Core streams that must have data

# Stream health gate defaults
STREAM_HEALTH_ROLLING_WINDOW = 25  # Number of trades in rolling window
STREAM_HEALTH_SUSPEND_THRESHOLD = -750.0  # Suspend if rolling sum <= this (dollars)
STREAM_HEALTH_RESUME_THRESHOLD = 0.0  # Resume if rolling sum >= this (dollars)

__all__ = ['SLOT_ENDS', 'ROLLING_WINDOW_SIZE', 'DOM_BLOCKED_DAYS', 'SCF_THRESHOLD', 
           'MATRIX_REPROCESS_TRADING_DAYS', 'MATRIX_CHECKPOINT_FREQUENCY', 'ALLOW_INVALID_DATES_SALVAGE',
           'CRITICAL_STREAMS', 'STREAM_HEALTH_ROLLING_WINDOW', 'STREAM_HEALTH_SUSPEND_THRESHOLD',
           'STREAM_HEALTH_RESUME_THRESHOLD']

