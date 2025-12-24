"""
History manager for Master Matrix sequencer logic.

This module handles updating rolling histories for time slots.
The rolling history maintains the last N scores for each time slot,
used for time change decisions in the sequencer.
"""

from typing import Dict, List

# Rolling window size: maintain last 13 days of scores
ROLLING_WINDOW_SIZE = 13


def update_time_slot_history(
    time_slot_histories: Dict[str, List[int]],
    time: str,
    score: int
) -> None:
    """
    Update rolling history for a time slot by adding a new score.
    
    Maintains a rolling window of the last N scores (default: 13).
    This ensures all time slots have consistent history lengths
    for proper comparison during time change decisions.
    
    Args:
        time_slot_histories: Dictionary mapping time strings to lists of scores
        time: Time string (normalized to HH:MM format)
        score: Score to add to history (typically from calculate_time_score)
        
    Returns:
        None (modifies time_slot_histories in place)
    """
    # Initialize list if time slot doesn't exist
    if time not in time_slot_histories:
        time_slot_histories[time] = []
    
    # Add new score
    time_slot_histories[time].append(score)
    
    # Maintain rolling window: keep only last N scores
    if len(time_slot_histories[time]) > ROLLING_WINDOW_SIZE:
        time_slot_histories[time] = time_slot_histories[time][-ROLLING_WINDOW_SIZE:]


__all__ = ['update_time_slot_history', 'ROLLING_WINDOW_SIZE']

