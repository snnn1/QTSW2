"""
File management for Master Matrix.

This module handles saving and loading master matrix files
in both Parquet and JSON formats.
"""

import logging
import sys
from pathlib import Path
from typing import Optional, Tuple, Dict
from datetime import datetime
import pandas as pd

from .utils import _enforce_trade_date_invariants

logger = logging.getLogger(__name__)


def save_master_matrix(
    df: pd.DataFrame,
    output_dir: str,
    specific_date: Optional[str] = None,
    stream_filters: Optional[Dict] = None
) -> Tuple[Path, Path]:
    """
    Save master matrix to both Parquet and JSON formats.
    
    Args:
        df: Master matrix DataFrame to save
        output_dir: Directory to save files
        specific_date: If provided, saves as "today" file with date in filename
        
    Returns:
        Tuple of (parquet_file_path, json_file_path)
    """
    output_path = Path(output_dir)
    output_path.mkdir(parents=True, exist_ok=True)
    
    timestamp = datetime.now().strftime('%Y%m%d_%H%M%S')
    
    if specific_date:
        # Save as "today" file
        date_str = specific_date.replace('-', '')
        parquet_file = output_path / f"master_matrix_today_{date_str}.parquet"
        json_file = output_path / f"master_matrix_today_{date_str}.json"
    else:
        # Save as full backtest file
        parquet_file = output_path / f"master_matrix_{timestamp}.parquet"
        json_file = output_path / f"master_matrix_{timestamp}.json"
    
    # SL column should already exist from schema_normalizer (NaN if missing from analyzer)
    
    # Normalize Date column dtype before saving to parquet
    # Date column must be consistent for pyarrow to handle it
    # Since Date is backward compatibility and trade_date is canonical, ensure Date matches trade_date
    # CRITICAL: Ensure we have a copy before modifying (avoid SettingWithCopyWarning)
    df = df.copy()
    
    if 'Date' in df.columns and 'trade_date' in df.columns:
        # If trade_date is datetime, Date should also be datetime
        if pd.api.types.is_datetime64_any_dtype(df['trade_date']):
            # Use trade_date as source of truth (Date is just backward compatibility)
            # Always set Date from trade_date to ensure consistency
            df['Date'] = df['trade_date'].copy()
            logger.debug(
                f"Set Date column from trade_date (datetime64) before saving to parquet"
            )
        else:
            # trade_date is not datetime (shouldn't happen, but handle it)
            logger.warning(
                f"trade_date is not datetime dtype ({df['trade_date'].dtype}), "
                f"cannot normalize Date column"
            )
            # If Date has mixed types, convert all to strings
            if df['Date'].dtype == 'object':
                df['Date'] = df['Date'].astype(str)
    
    # Save as Parquet
    df.to_parquet(parquet_file, index=False, compression='snappy')
    logger.info(f"Saved: {parquet_file} (columns: {list(df.columns)})")
    
    # Save as JSON (for easy inspection)
    df.to_json(json_file, orient='records', date_format='iso', indent=2)
    logger.info(f"Saved: {json_file}")
    
    # Persist execution timetable from master matrix (authoritative persistence point)
    try:
        sys.path.insert(0, str(Path(__file__).parent.parent.parent))
        from modules.timetable.timetable_engine import TimetableEngine
        
        # Ensure trade_date is datetime dtype before passing to timetable engine
        # Parquet save/load or DataFrame operations can sometimes change dtype
        if 'trade_date' in df.columns:
            if not pd.api.types.is_datetime64_any_dtype(df['trade_date']):
                logger.warning(
                    f"trade_date column is {df['trade_date'].dtype} before timetable generation, "
                    f"converting to datetime64. This should not happen - check data processing pipeline."
                )
                df['trade_date'] = pd.to_datetime(df['trade_date'], errors='raise')
        
        engine = TimetableEngine()
        engine.write_execution_timetable_from_master_matrix(
            df, 
            trade_date=specific_date,
            stream_filters=stream_filters
        )
        logger.info("Execution timetable persisted from master matrix")
    except Exception as e:
        # Log but don't fail matrix save if timetable persistence fails
        logger.warning(f"Failed to persist execution timetable: {e}")
        import traceback
        logger.debug(f"Timetable persistence traceback: {traceback.format_exc()}")
    
    return parquet_file, json_file


def load_existing_matrix(output_dir: str) -> pd.DataFrame:
    """
    Load the most recent existing master matrix file.
    
    Args:
        output_dir: Directory containing master matrix files
        
    Returns:
        DataFrame with existing matrix data, or empty DataFrame if not found
    """
    output_path = Path(output_dir)
    existing_df = pd.DataFrame()
    
    if output_path.exists():
        parquet_files = sorted(output_path.glob("master_matrix_*.parquet"), reverse=True)
        if parquet_files:
            try:
                existing_df = pd.read_parquet(parquet_files[0])
                logger.info(f"Loaded existing master matrix: {len(existing_df)} trades")
                
                # After parquet load: enforce invariants (fail-closed)
                # trade_date canonical, Date derived for legacy compatibility only
                # Must use pd.to_datetime(errors='raise') for any repair attempt; no errors='coerce'
                # Always sync Date from trade_date if Date exists
                if not existing_df.empty:
                    _enforce_trade_date_invariants(existing_df, "parquet_reload")
            except Exception as e:
                logger.warning(f"Could not load existing master matrix: {e}")
    
    return existing_df


def get_latest_matrix_file(output_dir: str) -> Optional[Path]:
    """
    Get the path to the most recent master matrix file.
    
    Args:
        output_dir: Directory containing master matrix files
        
    Returns:
        Path to latest file, or None if not found
    """
    output_path = Path(output_dir)
    if not output_path.exists():
        return None
    
    parquet_files = sorted(output_path.glob("master_matrix_*.parquet"), reverse=True)
    return parquet_files[0] if parquet_files else None



