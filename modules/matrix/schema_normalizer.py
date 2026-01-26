"""
Schema normalization for Master Matrix.

This module ensures consistent schema across all streams by adding
missing columns with default values and normalizing data types.

IMPORTANT: This module READS the Time column but NEVER mutates it.
The Time column is OWNED by sequencer_logic.py. This module may COPY Time
to other columns (entry_time, exit_time, etc.) but never modifies Time itself.

DATE OWNERSHIP: DataLoader owns date normalization. This module MUST NOT
re-parse dates. It MAY only validate dtype/presence of trade_date column.
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
    
    # Ensure we have a copy before modifying (avoid SettingWithCopyWarning and dtype issues)
    df = df.copy()
    
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
    
    # DATE OWNERSHIP: DataLoader owns date normalization
    # This module MUST NOT re-parse dates, MAY only validate dtype/presence
    # Ensure we have a copy before modifying (avoid SettingWithCopyWarning)
    df = df.copy()
    
    # Validate that trade_date exists and has correct dtype (if present)
    if 'trade_date' in df.columns:
        from .data_loader import _validate_trade_date_dtype
        try:
            _validate_trade_date_dtype(df, "schema_normalizer")
        except ValueError as e:
            logger.error(f"Schema normalization: trade_date validation failed: {e}")
            raise
        
        # CRITICAL: Ensure trade_date is datetime dtype before any operations
        # Operations like df['Date'] = df['trade_date'] can sometimes change dtype
        if not pd.api.types.is_datetime64_any_dtype(df['trade_date']):
            logger.warning(
                f"trade_date column is {df['trade_date'].dtype} in schema_normalizer, "
                f"converting to datetime64. This should not happen - check data processing pipeline."
            )
            df['trade_date'] = pd.to_datetime(df['trade_date'], errors='raise')
    
    # Keep Date column for backward compatibility (may be used by downstream code)
    # But trade_date is the canonical column
    if 'Date' not in df.columns and 'trade_date' in df.columns:
        # Create Date column from trade_date for backward compatibility
        # Ensure trade_date is datetime before copying
        if pd.api.types.is_datetime64_any_dtype(df['trade_date']):
            df['Date'] = df['trade_date']
        else:
            # trade_date is not datetime - convert first
            df['trade_date'] = pd.to_datetime(df['trade_date'], errors='raise')
            df['Date'] = df['trade_date']
    
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
    # entry_time, exit_time - map from analyzer EntryTime/ExitTime if available
    if 'entry_time' not in df.columns:
        # Prefer EntryTime from analyzer, fallback to Time
        if 'EntryTime' in df.columns:
            df['entry_time'] = df['EntryTime']
        else:
            df['entry_time'] = df['Time'] if 'Time' in df.columns else ''
        # Fill None values to avoid comparison errors during sorting
        if df['entry_time'].dtype == 'object':
            df['entry_time'] = df['entry_time'].fillna('')
    
    if 'exit_time' not in df.columns:
        # Use ExitTime from analyzer if available, otherwise fallback to entry_time
        if 'ExitTime' in df.columns:
            df['exit_time'] = df['ExitTime']
        else:
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
    
    # ProfitDollars - computed from Profit * contract_value * contract_multiplier
    # This is a derived column required by filter_engine for stream health gate calculations
    # Created here alongside other derived columns (R, pnl, etc.)
    if 'ProfitDollars' not in df.columns:
        from .statistics import _ensure_profit_dollars_column_inplace
        _ensure_profit_dollars_column_inplace(df, contract_multiplier=1.0)
        logger.debug("ProfitDollars column created by schema_normalizer (derived column)")
    
    # rs_value (Rolling Sum value - would need to calculate from sequential processor)
    if 'rs_value' not in df.columns:
        df['rs_value'] = np.nan
    
    # selected_time (same as Time for now)
    if 'selected_time' not in df.columns:
        df['selected_time'] = df['Time']
    
    # time_bucket (same as Time for now)
    if 'time_bucket' not in df.columns:
        df['time_bucket'] = df['Time']
    
    # trade_date: DataLoader owns normalization - should already exist
    # If trade_date doesn't exist, this is an error (should have been normalized by DataLoader)
    if 'trade_date' not in df.columns:
        logger.error(
            "trade_date column missing - DataLoader should have normalized Date to trade_date. "
            "This indicates a contract violation in the data loading pipeline."
        )
        # For backward compatibility, try to create from Date if available
        # But log this as an error since it violates single ownership
        if 'Date' in df.columns:
            logger.error(
                "Creating trade_date from Date as fallback - this violates single ownership. "
                "DataLoader should have normalized dates before schema normalization."
            )
            # Convert Date to datetime and create trade_date
            try:
                df['trade_date'] = pd.to_datetime(df['Date'], errors='raise')
            except (ValueError, TypeError) as e:
                logger.error(f"Cannot create trade_date from Date - Date conversion failed: {e}")
                df['trade_date'] = pd.NaT
        else:
            df['trade_date'] = pd.NaT
            logger.error("No 'Date' or 'trade_date' column found - trade_date set to NaT")
    
    # CRITICAL: Ensure trade_date is datetime dtype before returning
    # This is a final safeguard - trade_date must be datetime for downstream .dt accessor
    if 'trade_date' in df.columns:
        if not pd.api.types.is_datetime64_any_dtype(df['trade_date']):
            logger.warning(
                f"trade_date column is {df['trade_date'].dtype} at end of normalize_schema, "
                f"converting to datetime64. This should not happen - check data processing pipeline."
            )
            # Only convert if not all NaT (which would fail with errors='raise')
            if not df['trade_date'].isna().all():
                df['trade_date'] = pd.to_datetime(df['trade_date'], errors='raise')
            else:
                logger.warning("trade_date is all NaT - cannot convert to datetime")
    
    return df



