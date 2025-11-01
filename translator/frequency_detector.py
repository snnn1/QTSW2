"""
Data Frequency Detection
Detects whether data is tick-level or minute-level
"""

import pandas as pd
from typing import Optional


def detect_data_frequency(df: pd.DataFrame) -> str:
    """
    Detect the frequency/resolution of the data
    
    Args:
        df: DataFrame with timestamp column
        
    Returns:
        Frequency string: 'tick', '1min', '5min', '15min', '1hour', '1day', or 'unknown'
    """
    if df.empty or 'timestamp' not in df.columns:
        return 'unknown'
    
    if len(df) < 2:
        return 'unknown'
    
    # Get time differences between consecutive rows
    timestamps = pd.to_datetime(df['timestamp']).sort_values()
    time_diffs = timestamps.diff().dropna()
    
    if len(time_diffs) == 0:
        return 'unknown'
    
    # Get median time difference (most common interval)
    median_diff = time_diffs.median()
    diff_seconds = median_diff.total_seconds()
    
    # Round to nearest common interval
    if diff_seconds < 1:
        return 'tick'  # Less than 1 second = tick data
    elif diff_seconds < 10:
        return 'tick'  # Less than 10 seconds = likely tick data
    elif 50 <= diff_seconds <= 70:
        return '1min'  # ~60 seconds = 1 minute bars
    elif 290 <= diff_seconds <= 310:
        return '5min'  # ~300 seconds = 5 minute bars
    elif 890 <= diff_seconds <= 910:
        return '15min'  # ~900 seconds = 15 minute bars
    elif 3590 <= diff_seconds <= 3610:
        return '1hour'  # ~3600 seconds = 1 hour bars
    elif 86300 <= diff_seconds <= 86500:
        return '1day'  # ~86400 seconds = daily bars
    elif diff_seconds < 60:
        return 'tick'  # Less than 60 seconds but > 10 seconds = high frequency tick
    else:
        return 'unknown'


def is_tick_data(df: pd.DataFrame) -> bool:
    """
    Determine if data is tick-level
    
    Args:
        df: DataFrame with timestamp column
        
    Returns:
        True if tick data, False if bar data
    """
    frequency = detect_data_frequency(df)
    return frequency == 'tick'


def is_minute_data(df: pd.DataFrame) -> bool:
    """
    Determine if data is minute-level or higher
    
    Args:
        df: DataFrame with timestamp column
        
    Returns:
        True if minute/hour/day data, False if tick
    """
    frequency = detect_data_frequency(df)
    return frequency in ['1min', '5min', '15min', '1hour', '1day']


def get_data_type_summary(df: pd.DataFrame) -> dict:
    """
    Get detailed summary about data type and frequency
    
    Args:
        df: DataFrame with timestamp column
        
    Returns:
        Dictionary with:
        - frequency: detected frequency string
        - is_tick: bool
        - is_minute: bool
        - median_interval_seconds: float
        - total_rows: int
        - time_range_start: timestamp
        - time_range_end: timestamp
    """
    if df.empty or 'timestamp' not in df.columns:
        return {
            'frequency': 'unknown',
            'is_tick': False,
            'is_minute': False,
            'median_interval_seconds': 0,
            'total_rows': 0,
            'time_range_start': None,
            'time_range_end': None
        }
    
    timestamps = pd.to_datetime(df['timestamp']).sort_values()
    time_diffs = timestamps.diff().dropna()
    median_diff_seconds = time_diffs.median().total_seconds() if len(time_diffs) > 0 else 0
    
    frequency = detect_data_frequency(df)
    
    return {
        'frequency': frequency,
        'is_tick': is_tick_data(df),
        'is_minute': is_minute_data(df),
        'median_interval_seconds': median_diff_seconds,
        'total_rows': len(df),
        'time_range_start': timestamps.min() if len(timestamps) > 0 else None,
        'time_range_end': timestamps.max() if len(timestamps) > 0 else None
    }

