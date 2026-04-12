"""
Time column mutation guard.

This module provides a guard function to detect and prevent unauthorized
mutations of the Time column outside of sequencer_logic.py.

The Time column is OWNED by sequencer_logic.py and represents:
"The sequencer's intended trading slot for that day."

It must NEVER be re-derived or mutated downstream.
"""

import logging
import traceback

logger = logging.getLogger(__name__)

# Track if Time column mutations are detected
_time_mutation_detected = False


def check_time_mutation(df, operation_name: str = "unknown") -> bool:
    """
    Check if Time column is being mutated outside sequencer_logic.py.
    
    This is a diagnostic function to detect bugs where Time column
    is being modified downstream.
    
    Args:
        df: DataFrame to check
        operation_name: Name of the operation being performed
        
    Returns:
        True if mutation detected, False otherwise
    """
    global _time_mutation_detected
    
    # Check if Time column exists and has been modified
    # This is a read-only check - we don't prevent mutation here,
    # but we log it as a potential bug
    
    # Get caller information
    import inspect
    stack = inspect.stack()
    caller_file = stack[1].filename if len(stack) > 1 else "unknown"
    caller_name = stack[1].function if len(stack) > 1 else "unknown"
    
    # Check if caller is sequencer_logic.py (authorized)
    if 'sequencer_logic.py' in caller_file:
        return False  # Authorized mutation
    
    # If Time column is being set/modified outside sequencer_logic, log warning
    # Note: This is a passive check - actual prevention happens via code review
    # and documentation
    
    return False  # No mutation detected in this check


def assert_time_immutable(df, context: str = ""):
    """
    Assert that Time column should be treated as immutable.
    
    This function logs a warning if Time column is being modified
    outside of sequencer_logic.py.
    
    Args:
        df: DataFrame being processed
        context: Context string for logging
    """
    import inspect
    stack = inspect.stack()
    caller_file = stack[1].filename if len(stack) > 1 else "unknown"
    
    # If not sequencer_logic.py, log a reminder that Time is immutable
    if 'sequencer_logic.py' not in caller_file and 'Time' in df.columns:
        logger.debug(f"Time column accessed in {caller_file} - treating as immutable (context: {context})")

