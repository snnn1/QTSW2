"""
Trading day calculation utilities for Master Matrix.

This module provides data-driven trading day counting from merged data,
not calendar-based, to accurately calculate rolling window start dates.
"""

import logging
import pandas as pd
from typing import Optional, Tuple
from pathlib import Path

from .logging_config import setup_matrix_logger

logger = setup_matrix_logger(__name__, console=True, level=logging.INFO)


def count_trading_days(
    df: pd.DataFrame,
    start_date: str,
    end_date: str
) -> int:
    """
    Count unique trading days between two dates in merged data.
    
    Uses data-driven approach: counts unique dates present in the DataFrame,
    not calendar days. This ensures accuracy for actual trading days.
    
    Args:
        df: DataFrame with 'trade_date' or 'Date' column
        start_date: Start date (YYYY-MM-DD) - inclusive
        end_date: End date (YYYY-MM-DD) - inclusive
        
    Returns:
        Number of unique trading days in the range
    """
    if df.empty:
        return 0
    
    # Use trade_date if available, otherwise Date
    date_col = 'trade_date' if 'trade_date' in df.columns else 'Date'
    if date_col not in df.columns:
        logger.warning("No date column found in DataFrame")
        return 0
    
    # Ensure datetime type
    df = df.copy()
    df[date_col] = pd.to_datetime(df[date_col], errors='coerce')
    
    # Filter to date range
    start_dt = pd.to_datetime(start_date)
    end_dt = pd.to_datetime(end_date)
    
    mask = (df[date_col] >= start_dt) & (df[date_col] <= end_dt)
    filtered_df = df[mask]
    
    if filtered_df.empty:
        return 0
    
    # Count unique dates (normalize to date only, ignore time)
    unique_dates = filtered_df[date_col].dt.normalize().unique()
    return len(unique_dates)


def find_trading_days_back(
    df: pd.DataFrame,
    from_date: str,
    days_back: int
) -> Optional[str]:
    """
    Find the date that is exactly N trading days before from_date.
    
    Uses data-driven approach: counts unique dates in merged data,
    not calendar days.
    
    Args:
        df: DataFrame with 'trade_date' or 'Date' column
        from_date: Reference date (YYYY-MM-DD)
        days_back: Number of trading days to go back
        
    Returns:
        Date string (YYYY-MM-DD) that is N trading days before from_date,
        or None if insufficient history
    """
    if df.empty:
        logger.warning("Empty DataFrame provided")
        return None
    
    # Use trade_date if available, otherwise Date
    date_col = 'trade_date' if 'trade_date' in df.columns else 'Date'
    if date_col not in df.columns:
        logger.warning("No date column found in DataFrame")
        return None
    
    # Ensure datetime type
    df = df.copy()
    df[date_col] = pd.to_datetime(df[date_col], errors='coerce')
    
    # Remove invalid dates
    df = df[df[date_col].notna()].copy()
    
    if df.empty:
        logger.warning("No valid dates in DataFrame")
        return None
    
    # Get unique dates, sorted
    unique_dates = sorted(df[date_col].dt.normalize().unique())
    
    if not unique_dates:
        return None
    
    from_dt = pd.to_datetime(from_date).normalize()
    
    # Find index of from_date (or closest date before it)
    from_idx = None
    for i, date in enumerate(unique_dates):
        if date > from_dt:
            # Use previous date if from_date not exactly in data
            from_idx = i - 1 if i > 0 else None
            break
        elif date == from_dt:
            from_idx = i
            break
    
    if from_idx is None:
        # from_date is before all dates in data
        logger.warning(f"from_date {from_date} is before all dates in data")
        return None
    
    # Go back N trading days
    target_idx = from_idx - days_back
    
    if target_idx < 0:
        logger.warning(
            f"Insufficient history: need {days_back} trading days back from {from_date}, "
            f"but only {from_idx + 1} trading days available"
        )
        return None
    
    target_date = unique_dates[target_idx]
    return target_date.strftime('%Y-%m-%d')


def get_merged_data_date_range(merged_data_dir: str) -> Tuple[Optional[str], Optional[str]]:
    """
    Get the date range of merged data files.
    
    Args:
        merged_data_dir: Directory containing merged data files
        
    Returns:
        Tuple of (min_date, max_date) as YYYY-MM-DD strings, or (None, None) if no data
    """
    merged_path = Path(merged_data_dir)
    
    if not merged_path.exists():
        logger.warning(f"Merged data directory does not exist: {merged_data_dir}")
        return None, None
    
    # Look for parquet files in the merged directory
    parquet_files = list(merged_path.glob("**/*.parquet"))
    
    if not parquet_files:
        logger.warning(f"No parquet files found in {merged_data_dir}")
        return None, None
    
    min_date = None
    max_date = None
    
    for parquet_file in parquet_files:
        try:
            df = pd.read_parquet(parquet_file)
            if df.empty:
                continue
            
            # Use trade_date if available, otherwise Date
            date_col = 'trade_date' if 'trade_date' in df.columns else 'Date'
            if date_col not in df.columns:
                continue
            
            df[date_col] = pd.to_datetime(df[date_col], errors='coerce')
            df = df[df[date_col].notna()]
            
            if df.empty:
                continue
            
            file_min = df[date_col].min()
            file_max = df[date_col].max()
            
            if min_date is None or file_min < min_date:
                min_date = file_min
            if max_date is None or file_max > max_date:
                max_date = file_max
        except Exception as e:
            logger.debug(f"Error reading {parquet_file}: {e}")
            continue
    
    if min_date is None or max_date is None:
        return None, None
    
    return min_date.strftime('%Y-%m-%d'), max_date.strftime('%Y-%m-%d')


__all__ = ['count_trading_days', 'find_trading_days_back', 'get_merged_data_date_range']

