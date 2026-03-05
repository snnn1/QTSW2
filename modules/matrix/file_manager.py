"""
File management for Master Matrix.

This module handles saving and loading master matrix files
in both Parquet and JSON formats.
"""

import logging
import subprocess
import sys
import threading
from pathlib import Path
from typing import Optional, Tuple, Dict
from datetime import datetime, timezone

import pandas as pd

from .utils import _enforce_trade_date_invariants

logger = logging.getLogger(__name__)


def _trigger_eligibility_builder_async(trading_date: str, output_dir: str) -> None:
    """
    Fire-and-forget: spawn background thread to run eligibility_builder.py.
    Never blocks or raises. Logs success/failure.
    Uses absolute paths so cwd does not affect file resolution.
    """
    def _run():
        try:
            project_root = Path(__file__).resolve().parent.parent.parent
            script_path = project_root / "scripts" / "eligibility_builder.py"
            if not script_path.exists():
                logger.warning("ELIGIBILITY_BUILDER_SKIP: script not found at %s", script_path)
                return
            timetable_dir = (project_root / "data" / "timetable").resolve()
            matrix_dir = (project_root / output_dir).resolve()
            python_exe = sys.executable or "python"
            cmd = [
                python_exe, str(script_path),
                "--date", trading_date,
                "--output-dir", str(timetable_dir),
                "--master-matrix-dir", str(matrix_dir),
            ]
            logger.info("ELIGIBILITY_BUILDER_TRIGGERED: trading_date=%s output=%s", trading_date, timetable_dir)
            result = subprocess.run(
                cmd,
                cwd=str(project_root),
                capture_output=True,
                text=True,
                timeout=120,
            )
            if result.returncode == 0:
                logger.info("ELIGIBILITY_BUILDER_COMPLETED: exit_code=0")
            else:
                stderr_trunc = (result.stderr or "")[:500]
                logger.warning(
                    "ELIGIBILITY_BUILDER_FAILED: exit_code=%s stderr=%s",
                    result.returncode,
                    stderr_trunc,
                )
        except subprocess.TimeoutExpired:
            logger.warning("ELIGIBILITY_BUILDER_FAILED: timeout")
        except Exception as e:
            logger.warning("ELIGIBILITY_BUILDER_FAILED: %s", e)


def _trigger_eligibility_builder_after_save(output_dir: str = "data/master_matrix") -> None:
    """Trigger eligibility builder in background using CME trading date."""
    try:
        from modules.timetable.cme_session import get_trading_date_cme
        utc_now = datetime.now(timezone.utc)
        trading_date = get_trading_date_cme(utc_now)
        t = threading.Thread(target=_trigger_eligibility_builder_async, args=(trading_date, output_dir), daemon=True)
        t.start()
    except Exception as e:
        logger.warning("ELIGIBILITY_BUILDER_TRIGGER_SKIP: %s", e)


def save_master_matrix(
    df: pd.DataFrame,
    output_dir: str,
    specific_date: Optional[str] = None,
    stream_filters: Optional[Dict] = None,
    timetable_output_dir: Optional[str] = None
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
        
        engine = TimetableEngine(timetable_output_dir=timetable_output_dir) if timetable_output_dir else TimetableEngine()
        engine.write_execution_timetable_from_master_matrix(
            df,
            trade_date=specific_date,
            stream_filters=stream_filters,
            execution_mode=True,  # Robot path: eligibility artifact only; never manual filters
        )
        logger.info("Execution timetable persisted from master matrix")
        _trigger_eligibility_builder_after_save(output_dir=output_dir)
    except Exception as e:
        # Log but don't fail matrix save if timetable persistence fails
        logger.warning(f"Failed to persist execution timetable: {e}")
        import traceback
        logger.debug(f"Timetable persistence traceback: {traceback.format_exc()}")
    
    return parquet_file, json_file


def load_existing_matrix(output_dir: str) -> pd.DataFrame:
    """
    Load the master matrix file with the most rows (fullest history).
    
    Prefers files with more rows over the most recent file, so a truncated
    resequence does not replace a full rebuild. Checks the 10 most recent
    files to avoid loading too many.
    
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
            best_df = None
            best_rows = 0
            for pf in parquet_files[:10]:
                try:
                    df = pd.read_parquet(pf)
                    if len(df) > best_rows:
                        best_rows = len(df)
                        best_df = df
                except Exception as e:
                    logger.debug(f"Could not load {pf.name}: {e}")
            if best_df is not None:
                existing_df = best_df
                logger.info(f"Loaded existing master matrix: {len(existing_df)} trades (fullest of {min(10, len(parquet_files))} recent files)")
                
                # After parquet load: enforce invariants (fail-closed)
                # trade_date canonical, Date derived for legacy compatibility only
                if not existing_df.empty:
                    _enforce_trade_date_invariants(existing_df, "parquet_reload")
    
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


def get_best_matrix_file(output_dir: str) -> Optional[Path]:
    """
    Get the path to the master matrix file with the most rows (fullest history).
    Used by resequence and API so truncated resequences do not replace full rebuilds.
    """
    output_path = Path(output_dir)
    if not output_path.exists():
        return None
    parquet_files = sorted(output_path.glob("master_matrix_*.parquet"), reverse=True)
    if not parquet_files:
        return None
    best_path = None
    best_rows = 0
    for pf in parquet_files[:10]:
        try:
            df = pd.read_parquet(pf)
            if len(df) > best_rows:
                best_rows = len(df)
                best_path = pf
        except Exception:
            pass
    return best_path



