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



