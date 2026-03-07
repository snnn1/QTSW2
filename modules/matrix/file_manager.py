"""
File management for Master Matrix.

This module handles saving and loading master matrix files
in both Parquet and JSON formats.
"""

import re
import logging
import subprocess
import sys
import threading
from concurrent.futures import ThreadPoolExecutor, as_completed
from pathlib import Path
from typing import Optional, Tuple, Dict
from datetime import datetime, timezone

import pandas as pd

from .utils import _enforce_trade_date_invariants
from .config import SAVE_JSON_ON_BUILD

logger = logging.getLogger(__name__)

# Regex to extract row count from filename: master_matrix_YYYYMMDD_HHMMSS_Nn.parquet
_ROW_COUNT_RE = re.compile(r"_(\d+)n\.parquet$", re.IGNORECASE)


def _parse_row_count_from_path(path: Path) -> Optional[int]:
    """Parse row count from filename. Returns None for legacy files without _Nn suffix."""
    m = _ROW_COUNT_RE.search(path.name)
    return int(m.group(1)) if m else None


def _get_best_matrix_path_by_row_count(parquet_files: list) -> Optional[Path]:
    """
    Select best parquet file by row count. Uses filename suffix when available (no disk read).
    Fallback: read up to 10 files when no files have row count in filename (legacy).
    """
    files_with_count = [(pf, _parse_row_count_from_path(pf)) for pf in parquet_files]
    has_any_count = any(c is not None for _, c in files_with_count)
    if has_any_count:
        # Sort by row count descending; files without count go last (treat as 0)
        best = max(files_with_count, key=lambda x: (x[1] or 0, x[0].name))
        return best[0]
    return None  # Fallback: caller will read files


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
    
    row_count = len(df)
    if specific_date:
        # Save as "today" file (no row count in filename - small, one per date)
        date_str = specific_date.replace('-', '')
        parquet_file = output_path / f"master_matrix_today_{date_str}.parquet"
        json_file = output_path / f"master_matrix_today_{date_str}.json"
    else:
        # Save as full backtest file with row count for fast best-file selection
        parquet_file = output_path / f"master_matrix_{timestamp}_{row_count}n.parquet"
        json_file = output_path / f"master_matrix_{timestamp}_{row_count}n.json"
    
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
    
    # Save as Parquet and JSON in parallel when both are needed (df is already a copy)
    import time
    from .instrumentation import log_timing_event
    t_save_start = time.perf_counter()
    save_json = specific_date is not None or SAVE_JSON_ON_BUILD
    if save_json:
        df_for_json = df.copy()
        with ThreadPoolExecutor(max_workers=2) as ex:
            fut_parquet = ex.submit(
                lambda: df.to_parquet(parquet_file, index=False, compression='snappy')
            )
            fut_json = ex.submit(
                lambda: df_for_json.to_json(json_file, orient='records', date_format='iso', indent=2)
            )
            for fut in as_completed([fut_parquet, fut_json]):
                fut.result()
        logger.info(f"Saved: {parquet_file} (columns: {list(df.columns)})")
        logger.info(f"Saved: {json_file}")
    else:
        df.to_parquet(parquet_file, index=False, compression='snappy')
        logger.info(f"Saved: {parquet_file} (columns: {list(df.columns)})")
    
    duration_ms = int((time.perf_counter() - t_save_start) * 1000)
    log_timing_event(
        phase="matrix_save",
        duration_ms=duration_ms,
        row_count=len(df),
        stream_count=len(df["Stream"].unique()) if "Stream" in df.columns else 0,
        file_path=str(parquet_file),
        mode="today" if specific_date else "full",
    )
    from .build_journal import journal_event
    journal_event(
        event_type="matrix_saved",
        mode="today" if specific_date else "full",
        rows_written=len(df),
        matrix_file_path=str(parquet_file),
        duration_ms=duration_ms,
    )
    
    # Persist execution timetable and trigger eligibility builder in background (non-blocking)
    def _run_timetable_and_eligibility():
        try:
            import time
            from .build_journal import journal_event
            journal_event(event_type="timetable_start", mode="from_matrix")
            t_timetable = time.perf_counter()
            time.sleep(0.1)  # Brief delay to let filesystem settle
            sys.path.insert(0, str(Path(__file__).parent.parent.parent))
            from modules.timetable.timetable_engine import TimetableEngine
            df_copy = df.copy()
            if 'trade_date' in df_copy.columns and not pd.api.types.is_datetime64_any_dtype(df_copy['trade_date']):
                df_copy['trade_date'] = pd.to_datetime(df_copy['trade_date'], errors='raise')
            engine = TimetableEngine(timetable_output_dir=timetable_output_dir) if timetable_output_dir else TimetableEngine()
            engine.write_execution_timetable_from_master_matrix(
                df_copy,
                trade_date=specific_date,
                stream_filters=stream_filters,
                execution_mode=True,
            )
            logger.info("Execution timetable persisted from master matrix")
            duration_ms = int((time.perf_counter() - t_timetable) * 1000)
            from .instrumentation import log_timing_event
            log_timing_event(phase="timetable_generation", duration_ms=duration_ms, mode="from_matrix")
            journal_event(event_type="timetable_complete", mode="from_matrix", duration_ms=duration_ms)
            _trigger_eligibility_builder_after_save(output_dir=output_dir)
        except Exception as e:
            from .build_journal import journal_event
            journal_event(event_type="failure", mode="timetable", error=str(e))
            logger.warning(f"Failed to persist execution timetable: {e}")
            import traceback
            logger.debug(f"Timetable persistence traceback: {traceback.format_exc()}")
    
    t = threading.Thread(target=_run_timetable_and_eligibility, daemon=True)
    t.start()
    
    return parquet_file, json_file


def load_existing_matrix(output_dir: str) -> pd.DataFrame:
    """
    Load the master matrix file with the most rows (fullest history).
    
    Uses row count in filename when available (no disk read). Falls back to
    reading up to 10 files for legacy filenames without _Nn suffix.
    
    Args:
        output_dir: Directory containing master matrix files
        
    Returns:
        DataFrame with existing matrix data, or empty DataFrame if not found
    """
    import time
    from .instrumentation import log_timing_event
    t0 = time.perf_counter()
    output_path = Path(output_dir)
    existing_df = pd.DataFrame()
    
    if output_path.exists():
        parquet_files = sorted(output_path.glob("master_matrix_*.parquet"), reverse=True)
        if parquet_files:
            best_path = _get_best_matrix_path_by_row_count(parquet_files[:20])
            if best_path is not None:
                # Fast path: row count in filename, load single file
                try:
                    existing_df = pd.read_parquet(best_path)
                    logger.info(f"Loaded existing master matrix: {len(existing_df)} trades (from {best_path.name})")
                except Exception as e:
                    logger.debug(f"Could not load {best_path.name}: {e}")
            else:
                # Fallback: legacy files, read up to 10 to find fullest
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
            
            if not existing_df.empty:
                _enforce_trade_date_invariants(existing_df, "parquet_reload")
                try:
                    from .integrity import validate_matrix_integrity
                    ok, issues = validate_matrix_integrity(existing_df)
                    if issues:
                        for msg in issues:
                            logger.warning(f"Matrix integrity: {msg}")
                except Exception as e:
                    logger.debug(f"Integrity validation skipped: {e}")
    
    duration_ms = int((time.perf_counter() - t0) * 1000)
    dmin, dmax = None, None
    if not existing_df.empty and "trade_date" in existing_df.columns:
        mn, mx = existing_df["trade_date"].min(), existing_df["trade_date"].max()
        dmin = mn.strftime("%Y-%m-%d") if pd.notna(mn) and hasattr(mn, "strftime") else None
        dmax = mx.strftime("%Y-%m-%d") if pd.notna(mx) and hasattr(mx, "strftime") else None
    log_timing_event(
        phase="matrix_load",
        duration_ms=duration_ms,
        row_count=len(existing_df) if not existing_df.empty else 0,
        stream_count=len(existing_df["Stream"].unique()) if not existing_df.empty and "Stream" in existing_df.columns else 0,
        date_min=dmin,
        date_max=dmax,
    )
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
    Uses row count in filename when available (no disk read). Falls back to
    reading up to 10 files for legacy filenames.
    """
    output_path = Path(output_dir)
    if not output_path.exists():
        return None
    parquet_files = sorted(output_path.glob("master_matrix_*.parquet"), reverse=True)
    if not parquet_files:
        return None
    best_path = _get_best_matrix_path_by_row_count(parquet_files[:20])
    if best_path is not None:
        return best_path
    # Fallback: legacy files
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



