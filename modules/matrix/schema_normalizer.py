"""
Schema normalization for Master Matrix.

This module ensures consistent schema across all streams by adding
missing columns with default values and normalizing data types.

IMPORTANT: This module READS the Time column but NEVER mutates it.
The Time column is OWNED by sequencer_logic.py. This module may COPY Time
to other columns (entry_time, exit_time, etc.) but never modifies Time itself.
"""

import logging
import pandas as pd
import numpy as np

logger = logging.getLogger(__name__)


def normalize_schema(df: pd.DataFrame) -> pd.DataFrame:
    """
    Normalize fields so every stream has the same schema.
    Adds missing columns with default values.
    
    Args:
        df: Input DataFrame
        
    Returns:
        Normalized DataFrame with consistent schema
    """
    logger.info("Normalizing schema...")
    
    # Required columns from analyzer output
    required_columns = {
        'Date': 'object',
        'Time': 'object',
        'Target': 'float64',
        'Range': 'float64',
        'StopLoss': 'float64',  # Stop Loss - from analyzer output
        'Peak': 'float64',
        'Direction': 'object',
        'Result': 'object',
        'Stream': 'object',
        'Instrument': 'object',
        'Session': 'object',
        'Profit': 'float64',
    }
    
    # Optional columns (may not exist in all files)
    optional_columns = {
        'scf_s1': 'float64',
        'scf_s2': 'float64',
        'onr': 'float64',
        'onr_high': 'float64',
        'onr_low': 'float64',
        'actual_trade_time': 'object',  # Original analyzer time (preserved by sequencer)
        'date_repaired': 'bool',  # Flag indicating if date was repaired
        'original_date': 'object',  # Original Date value before repair
    }
    
    # Ensure Date is datetime
    if 'Date' in df.columns:
        if df['Date'].dtype == 'object':
            df['Date'] = pd.to_datetime(df['Date'], errors='coerce')
        elif not pd.api.types.is_datetime64_any_dtype(df['Date']):
            df['Date'] = pd.to_datetime(df['Date'], errors='coerce')
    
    # Add missing required columns
    for col, dtype in required_columns.items():
        if col not in df.columns:
            if col == 'Date':
                df[col] = pd.NaT
            elif dtype == 'float64':
                df[col] = np.nan
            elif dtype == 'object':
                df[col] = ''
            else:
                df[col] = None
    
    # Add missing optional columns with NaN
    for col, dtype in optional_columns.items():
        if col not in df.columns:
            df[col] = np.nan
    
    # Create derived columns that may not exist
    df = create_derived_columns(df)
    
    logger.info(f"Schema normalized. Columns: {list(df.columns)}")
    
    return df


def create_derived_columns(df: pd.DataFrame) -> pd.DataFrame:
    """
    Create derived columns that may not exist in the input data.
    
    Args:
        df: Input DataFrame
        
    Returns:
        DataFrame with derived columns added
    """
    # entry_time, exit_time (using Time as entry_time, exit_time would be calculated)
    if 'entry_time' not in df.columns:
        df['entry_time'] = df['Time'] if 'Time' in df.columns else ''
        # Fill None values to avoid comparison errors during sorting
        if df['entry_time'].dtype == 'object':
            df['entry_time'] = df['entry_time'].fillna('')
    
    if 'exit_time' not in df.columns:
        # For now, set exit_time same as entry_time (would need actual exit logic)
        df['exit_time'] = df['entry_time'] if 'entry_time' in df.columns else ''
        # Fill None values to avoid comparison errors during sorting
        if df['exit_time'].dtype == 'object':
            df['exit_time'] = df['exit_time'].fillna('')
    
    # entry_price, exit_price (not in analyzer output, create placeholder)
    if 'entry_price' not in df.columns:
        df['entry_price'] = np.nan
    
    if 'exit_price' not in df.columns:
        df['exit_price'] = np.nan
    
    # R (Risk-Reward ratio) - calculate from Profit/Target
    if 'R' not in df.columns:
        df['R'] = df.apply(
            lambda row: row['Profit'] / row['Target'] if pd.notna(row['Target']) and row['Target'] != 0 else np.nan,
            axis=1
        )
    
    # pnl (same as Profit for now)
    if 'pnl' not in df.columns:
        df['pnl'] = df['Profit']
    
    # rs_value (Rolling Sum value - would need to calculate from sequential processor)
    if 'rs_value' not in df.columns:
        df['rs_value'] = np.nan
    
    # selected_time (same as Time for now)
    if 'selected_time' not in df.columns:
        df['selected_time'] = df['Time']
    
    # time_bucket (same as Time for now)
    if 'time_bucket' not in df.columns:
        df['time_bucket'] = df['Time']
    
    # trade_date (same as Date) - keep as datetime for consistent sorting
    if 'trade_date' not in df.columns:
        if 'Date' in df.columns:
            # Keep as datetime (not date objects) to avoid type mixing issues
            # Use errors='coerce' to convert invalid dates to NaT, but log warnings for debugging
            df['trade_date'] = pd.to_datetime(df['Date'], errors='coerce')
            
            # Log if any dates failed to parse
            invalid_dates = df['trade_date'].isna() & df['Date'].notna()
            if invalid_dates.any():
                invalid_count = invalid_dates.sum()
                invalid_samples = df[invalid_dates]['Date'].head(10).tolist()
                logger.warning(
                    f"Failed to parse {invalid_count} date(s) to trade_date. "
                    f"Sample invalid values: {invalid_samples}"
                )
        else:
            df['trade_date'] = pd.NaT
            logger.warning("No 'Date' column found - trade_date set to NaT for all rows")
    
    return df



