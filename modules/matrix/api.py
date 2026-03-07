"""
Master Matrix API Router
FastAPI endpoints for master matrix operations
"""

import asyncio
import importlib
import inspect
import logging
import math
import sys
from pathlib import Path
from typing import Optional, List, Dict, Any, Tuple
from datetime import datetime

from fastapi import APIRouter, HTTPException
from fastapi.responses import JSONResponse, Response
from pydantic import BaseModel

# Calculate project root
QTSW2_ROOT = Path(__file__).parent.parent.parent

router = APIRouter(prefix="/api/matrix", tags=["matrix"])
logger = logging.getLogger(__name__)

# Module-level cache for matrix data: key -> (df_full, stats_full, years)
# Key: (file_path, mtime, stream_include_tuple, contract_multiplier, include_filtered_executed)
_matrix_data_cache: Dict[Tuple, Tuple[Any, Any, List]] = {}
_breakdown_cache: Dict[Tuple, Dict] = {}
_stream_stats_cache: Dict[Tuple, Dict] = {}

# Performance counters for /api/matrix/performance dashboard
_api_cache_hits: int = 0
_api_cache_misses: int = 0
_last_matrix_row_count: Optional[int] = None


def _invalidate_matrix_cache():
    """Clear matrix data cache and MatrixState (call after build/resequence)."""
    global _matrix_data_cache, _breakdown_cache, _stream_stats_cache, _api_cache_hits, _api_cache_misses
    _matrix_data_cache.clear()
    _breakdown_cache.clear()
    _stream_stats_cache.clear()
    _api_cache_hits = 0
    _api_cache_misses = 0
    try:
        from .matrix_state import invalidate_matrix_state
        invalidate_matrix_state()
    except Exception:
        pass
    logger.debug("Matrix data cache invalidated")


# ============================================================
# Models
# ============================================================

class StreamFilterConfig(BaseModel):
    exclude_days_of_week: List[str] = []  # e.g., ["Wednesday", "Friday"]
    exclude_days_of_month: List[int] = []  # e.g., [4, 16, 30]
    exclude_times: List[str] = []  # e.g., ["07:30", "08:00"]


class MatrixBuildRequest(BaseModel):
    start_date: Optional[str] = None
    end_date: Optional[str] = None
    specific_date: Optional[str] = None
    analyzer_runs_dir: str = "data/analyzed"
    output_dir: str = "data/master_matrix"
    stream_filters: Optional[Dict[str, StreamFilterConfig]] = None
    streams: Optional[List[str]] = None  # If provided, only rebuild these streams
    warmup_months: Optional[int] = None  # Reserved for future use (e.g. warmup period)
    visible_years: Optional[List[int]] = None  # Reserved for future use (e.g. year filtering)


class BreakdownRequest(BaseModel):
    breakdown_type: str  # "day", "dom", "doy", "doy_by_year", "time", "date", "month", "year"
    stream_filters: Optional[Dict[str, StreamFilterConfig]] = None
    use_filtered: bool = False  # If True, apply filters; if False, use all data
    contract_multiplier: float = 1.0
    stream_include: Optional[List[str]] = None  # List of streams to include (None = all streams)


class StreamStatsRequest(BaseModel):
    stream_id: str  # e.g., "ES1", "ES2", "GC1", etc.
    include_filtered_executed: bool = False  # If True, include filtered executed trades
    contract_multiplier: float = 1.0


class MatrixDiffRequest(BaseModel):
    file_a: str  # Path or filename of first matrix (e.g. master_matrix_20250307_120000_45231n.parquet)
    file_b: str  # Path or filename of second matrix


# ============================================================
# Endpoints
# ============================================================

@router.post("/build")
async def build_master_matrix(request: MatrixBuildRequest):
    """Build or rebuild the master matrix from analyzer runs."""
    try:
        from modules.matrix.master_matrix import MasterMatrix
        
        matrix = MasterMatrix(analyzer_runs_dir=request.analyzer_runs_dir)
        
        # Convert stream filters format if provided
        stream_filters_dict = None
        if request.stream_filters:
            stream_filters_dict = {}
            for stream_id, filter_config in request.stream_filters.items():
                stream_filters_dict[stream_id] = {
                    "exclude_days_of_week": filter_config.exclude_days_of_week,
                    "exclude_days_of_month": filter_config.exclude_days_of_month,
                    "exclude_times": filter_config.exclude_times
                }
        
        matrix.build_master_matrix(
            start_date=request.start_date,
            end_date=request.end_date,
            specific_date=request.specific_date,
            output_dir=request.output_dir,
            stream_filters=stream_filters_dict,
            analyzer_runs_dir=request.analyzer_runs_dir,
            streams=request.streams,
            warmup_months=request.warmup_months,
            visible_years=request.visible_years
        )
        _invalidate_matrix_cache()
        return {"status": "success", "message": "Master matrix built successfully"}
    except Exception as e:
        logger.error(f"Error building master matrix: {e}", exc_info=True)
        raise HTTPException(status_code=500, detail=str(e))




class ResequenceRequest(BaseModel):
    resequence_days: int = 40
    analyzer_runs_dir: str = "data/analyzed"
    output_dir: str = "data/master_matrix"
    stream_filters: Optional[Dict[str, StreamFilterConfig]] = None


@router.post("/resequence")
async def resequence_master_matrix(request: ResequenceRequest):
    """Perform rolling resequence: remove window rows and resequence from checkpoint state.
    
    Behavior:
    1. Discover all analyzer output from data/analyzed/ (full disk scan)
    2. Compute resequence_start_date = today - N trading days
    3. Load existing master matrix
    4. Remove all rows where trade_date >= resequence_start_date
    5. Restore sequencer state from checkpoint immediately before resequence_start_date
    6. Run sequencer forward using analyzer data for dates >= resequence_start_date
    7. Append newly sequenced rows to preserved historical matrix rows
    8. Save single new master matrix file
    """
    try:
        from modules.matrix.master_matrix import MasterMatrix
        
        matrix = MasterMatrix(analyzer_runs_dir=request.analyzer_runs_dir)
        
        # Convert stream filters format if provided
        stream_filters_dict = None
        if request.stream_filters:
            stream_filters_dict = {}
            for stream_id, filter_config in request.stream_filters.items():
                stream_filters_dict[stream_id] = {
                    "exclude_days_of_week": filter_config.exclude_days_of_week,
                    "exclude_days_of_month": filter_config.exclude_days_of_month,
                    "exclude_times": filter_config.exclude_times
                }
        
        logger.info(f"ROLLING RESEQUENCE: Resequencing last {request.resequence_days} trading days")
        
        df, summary = matrix.build_master_matrix_rolling_resequence(
            resequence_days=request.resequence_days,
            output_dir=request.output_dir,
            stream_filters=stream_filters_dict,
            analyzer_runs_dir=request.analyzer_runs_dir
        )
        
        if "error" in summary:
            raise HTTPException(status_code=500, detail=summary["error"])
        
        _invalidate_matrix_cache()
        return {
            "status": "success",
            "message": f"Rolling resequence complete: {summary.get('rows_preserved', 0)} preserved + {summary.get('rows_resequenced', 0)} resequenced",
            "summary": summary
        }
    except Exception as e:
        logger.error(f"Error resequencing master matrix: {e}", exc_info=True)
        raise HTTPException(status_code=500, detail=str(e))


def _get_latest_analyzer_write_time(analyzer_runs_dir: Path) -> Optional[float]:
    """Get the latest modification time of any file in the analyzer output directory.
    
    Returns:
        Unix timestamp (float) of the most recently modified file, or None if directory doesn't exist or is empty.
    """
    if not analyzer_runs_dir.exists():
        return None
    
    latest_mtime = None
    
    # Check all parquet files in all stream subdirectories
    for stream_dir in analyzer_runs_dir.iterdir():
        if stream_dir.is_dir():
            for parquet_file in stream_dir.glob("*.parquet"):
                mtime = parquet_file.stat().st_mtime
                if latest_mtime is None or mtime > latest_mtime:
                    latest_mtime = mtime
    
    return latest_mtime


@router.post("/reload_latest")
async def reload_latest_matrix():
    """Reload the most recent matrix file from disk without rebuilding.
    
    This endpoint simply finds and returns the latest matrix file metadata,
    allowing the frontend to immediately see the current disk state.
    """
    try:
        root_matrix_dir = QTSW2_ROOT / "data" / "master_matrix"
        
        if not root_matrix_dir.exists():
            logger.warning(f"Matrix directory does not exist: {root_matrix_dir}")
            raise HTTPException(
                status_code=404, 
                detail=f"Master matrix directory not found: {root_matrix_dir.resolve()}. Please build the matrix first."
            )
        
        from modules.matrix.file_manager import get_best_matrix_file
        file_to_load = get_best_matrix_file(str(root_matrix_dir))
        
        if file_to_load is None:
            logger.warning(f"No matrix files found in: {root_matrix_dir}")
            raise HTTPException(
                status_code=404, 
                detail=f"No master matrix files found in {root_matrix_dir.resolve()}. Please build the matrix first using 'Rebuild Matrix' button."
            )
        
        file_mtime = file_to_load.stat().st_mtime
        matrix_file_id = file_to_load.name
        
        return {
            "status": "success",
            "matrix_file_id": matrix_file_id,
            "file": file_to_load.name,
            "file_mtime": file_mtime,
            "message": "Latest matrix file identified. Use GET /api/matrix/data to load it."
        }
    except HTTPException:
        raise
    except Exception as e:
        logger.error(f"Error reloading latest matrix: {e}", exc_info=True)
        raise HTTPException(status_code=500, detail=str(e))


@router.get("/freshness")
async def get_matrix_freshness(analyzer_runs_dir: str = "data/analyzed"):
    """Get freshness metadata comparing analyzer output vs matrix build time.
    
    Returns:
        Dictionary with latest_analyzer_write_time, latest_matrix_build_time, and staleness indicators.
    """
    try:
        analyzer_dir = QTSW2_ROOT / analyzer_runs_dir
        latest_analyzer_write_time = _get_latest_analyzer_write_time(analyzer_dir)
        
        # Get latest matrix file modification time
        root_matrix_dir = QTSW2_ROOT / "data" / "master_matrix"
        latest_matrix_build_time = None
        matrix_file_id = None
        
        if root_matrix_dir.exists():
            parquet_files = list(root_matrix_dir.glob("master_matrix_*.parquet"))
            if parquet_files:
                # Find most recent file
                from datetime import datetime, timedelta
                now = datetime.now()
                max_future_tolerance = timedelta(days=1)
                
                def parse_timestamp_from_filename(file_path: Path) -> Optional[datetime]:
                    import re
                    pattern = re.compile(r'master_matrix_(\d{8})_(\d{6})\.parquet$')
                    match = pattern.search(file_path.name)
                    if match:
                        try:
                            date_str = match.group(1) + match.group(2)
                            return datetime.strptime(date_str, '%Y%m%d%H%M%S')
                        except ValueError:
                            return None
                    return None
                
                files_with_timestamp = []
                files_without_timestamp = []
                
                for f in parquet_files:
                    ts = parse_timestamp_from_filename(f)
                    if ts and ts <= now + max_future_tolerance:
                        files_with_timestamp.append((ts, f))
                    else:
                        files_without_timestamp.append(f)
                
                if files_with_timestamp:
                    files_with_timestamp.sort(key=lambda x: x[0], reverse=True)
                    latest_file = files_with_timestamp[0][1]
                elif files_without_timestamp:
                    files_without_timestamp.sort(key=lambda p: p.stat().st_mtime, reverse=True)
                    latest_file = files_without_timestamp[0]
                else:
                    latest_file = None
                
                if latest_file:
                    latest_matrix_build_time = latest_file.stat().st_mtime
                    matrix_file_id = latest_file.name
        
        # Determine staleness
        is_stale = False
        staleness_message = None
        if latest_analyzer_write_time is not None and latest_matrix_build_time is not None:
            if latest_analyzer_write_time > latest_matrix_build_time:
                is_stale = True
                staleness_seconds = latest_analyzer_write_time - latest_matrix_build_time
                staleness_minutes = staleness_seconds / 60
                staleness_message = f"Analyzer has newer data than Matrix (analyzer is {staleness_minutes:.1f} minutes newer)"
        
        return {
            "latest_analyzer_write_time": latest_analyzer_write_time,
            "latest_matrix_build_time": latest_matrix_build_time,
            "matrix_file_id": matrix_file_id,
            "is_stale": is_stale,
            "staleness_message": staleness_message
        }
    except Exception as e:
        logger.error(f"Error getting freshness metadata: {e}", exc_info=True)
        raise HTTPException(status_code=500, detail=str(e))


@router.get("/data")
async def get_matrix_data(file_path: Optional[str] = None, limit: int = 10000, order: str = "newest", essential_columns_only: bool = True, skip_cleaning: bool = False, contract_multiplier: float = 1.0, include_filtered_executed: bool = False, stream_include: Optional[str] = None, nocache: bool = False, start_date: Optional[str] = None, end_date: Optional[str] = None, include_stats: bool = True):
    """Get master matrix data from the most recent file or specified file.
    
    Args:
        file_path: Optional path to specific file
        limit: Maximum number of rows to return (default 10000 for fast initial load, 0 = no limit)
        order: Sort order - "newest" (newest first, default) or "oldest" (canonical ascending order)
        essential_columns_only: If True, return only essential columns for faster initial load (default True)
        skip_cleaning: If True, skip expensive per-value sanitization for faster initial load (default False)
        contract_multiplier: Contract size multiplier (e.g., 2.0 for trading 2 contracts, default 1.0)
        include_filtered_executed: If True, include filtered executed trades in stats_full calculation (default False)
        stream_include: Comma-separated list of streams to include (e.g., "ES1,ES2"). If None or empty, all streams included.
        nocache: If True, bypass cache and force fresh load (default False).
        start_date: Filter to trades on or after this date (YYYY-MM-DD). Reduces payload when set.
        end_date: Filter to trades on or before this date (YYYY-MM-DD). Reduces payload when set.
        include_stats: If False, skip stats calculation for faster load when stats panel not needed (default True).
    """
    # Parse stream_include into list
    stream_include_list = None
    if stream_include:
        stream_include_list = [s.strip() for s in stream_include.split(',') if s.strip()]
        if len(stream_include_list) == 0:
            stream_include_list = None
    
    # Explicitly parse include_filtered_executed to ensure boolean (FastAPI query params can be strings)
    if isinstance(include_filtered_executed, str):
        include_filtered_executed = include_filtered_executed.lower() in ('true', '1', 'yes', 'on')
    include_filtered_executed = bool(include_filtered_executed)
    
    logger.info(f"GET /api/matrix/data called with contract_multiplier={contract_multiplier}, include_filtered_executed={include_filtered_executed} (parsed as bool), stream_include={stream_include_list}")
    
    global _api_cache_hits, _api_cache_misses, _last_matrix_row_count
    try:
        import pandas as pd
        
        # Determine file to load
        root_matrix_dir = QTSW2_ROOT / "data" / "master_matrix"
        
        if not root_matrix_dir.exists():
            raise HTTPException(status_code=404, detail=f"Master matrix directory not found: {root_matrix_dir}")
        
        if file_path:
            # Use specified file
            file_to_load = Path(file_path)
            if not file_to_load.exists():
                raise HTTPException(status_code=404, detail=f"File not found: {file_path}")
        else:
            # Prefer fullest file (most rows) so truncated resequences don't replace full rebuilds
            from modules.matrix.file_manager import get_best_matrix_file
            file_to_load = get_best_matrix_file(str(root_matrix_dir))
            if file_to_load is None:
                raise HTTPException(status_code=404, detail=f"No master matrix files found. Checked: {root_matrix_dir}. Build the matrix first.")
            logger.info(f"Selected fullest file: {file_to_load.name}")
        
        file_mtime = file_to_load.stat().st_mtime
        cache_key = (str(file_to_load), file_mtime, tuple(sorted(stream_include_list or [])), contract_multiplier, include_filtered_executed, include_stats)
        
        # Check cache (skip if nocache=True)
        df_full = None
        stats_full = None
        years = []
        if not nocache and cache_key in _matrix_data_cache:
            _api_cache_hits += 1
            df_full, stats_full, years = _matrix_data_cache[cache_key]
            if not include_stats:
                stats_full = None
            logger.info(f"Matrix data cache hit for {file_to_load.name}")
            try:
                from modules.matrix.instrumentation import log_timing_event
                log_timing_event(phase="api_matrix_load", duration_ms=0, cache_hit=True, row_count=len(df_full) if df_full is not None else 0)
            except Exception:
                pass
        
        if df_full is None:
            import time
            from modules.matrix.matrix_state import get_matrix_state
            t_load = time.perf_counter()
            # MatrixState: use in-process df when file unchanged; else load in thread (populates state)
            df_from_state, path_used, from_cache = await asyncio.to_thread(
                get_matrix_state, str(root_matrix_dir), file_to_load
            )
            if df_from_state is not None:
                df = df_from_state.copy()
            else:
                def _load_parquet_sync():
                    import pandas as pd
                    return pd.read_parquet(file_to_load)
                try:
                    df = await asyncio.to_thread(_load_parquet_sync)
                except Exception as e:
                    raise HTTPException(status_code=500, detail=f"Failed to read file {file_to_load.name}: {str(e)}")
            
            if df.empty:
                raise HTTPException(status_code=404, detail=f"File {file_to_load.name} is empty")
            
            df_full = df.copy()

            # Integrity validation (non-blocking, logs issues)
            try:
                from modules.matrix.integrity import validate_matrix_integrity
                ok, issues = validate_matrix_integrity(df_full)
                if issues:
                    for msg in issues:
                        logger.warning(f"Matrix integrity: {msg}")
                    if not ok:
                        logger.error("Matrix integrity check failed - missing required columns")
            except Exception as e:
                logger.debug(f"Integrity validation skipped: {e}")
            
            # Apply stream inclusion filter if specified (before stats calculation)
            if stream_include_list and 'Stream' in df_full.columns:
                original_len = len(df_full)
                df_full = df_full[df_full['Stream'].isin(stream_include_list)].copy()
                logger.info(f"Applied stream_include filter: {original_len} -> {len(df_full)} rows (streams: {stream_include_list})")
            
            # Calculate stats from FULL dataset BEFORE limiting (skip when include_stats=False)
            if include_stats:
                try:
                    from modules.matrix import statistics
                    df_for_stats = df_full.copy()
                    if "ProfitDollars" in df_for_stats.columns:
                        df_for_stats = df_for_stats.drop(columns=["ProfitDollars"])
                    logger.info(f"[API] Calculating stats with include_filtered_executed={include_filtered_executed} (type: {type(include_filtered_executed).__name__})")
                    stats_full = statistics.calculate_summary_stats(df_for_stats, include_filtered_executed=include_filtered_executed, contract_multiplier=contract_multiplier)
                    total_profit = stats_full.get('performance_trade_metrics', {}).get('total_profit', 'N/A') if stats_full else 'N/A'
                    total_rows = len(df_full)
                    executed_total = stats_full.get('sample_counts', {}).get('executed_trades_total', 0) if stats_full else 0
                    executed_allowed = stats_full.get('sample_counts', {}).get('executed_trades_allowed', 0) if stats_full else 0
                    executed_filtered = stats_full.get('sample_counts', {}).get('executed_trades_filtered', 0) if stats_full else 0
                    logger.info(f"Calculated full-dataset stats from {total_rows} total rows: {len(stats_full) if stats_full else 0} metrics (contract_multiplier={contract_multiplier}, include_filtered_executed={include_filtered_executed}, total_profit={total_profit})")
                    logger.info(f"[API] Stats sample counts: total={executed_total}, allowed={executed_allowed}, filtered={executed_filtered}")
                except Exception as e:
                    logger.error(f"CRITICAL: Could not calculate full-dataset stats - stats will be incomplete: {e}")
                    import traceback
                    logger.error(f"Stats calculation traceback: {traceback.format_exc()}")
                    stats_full = None
            else:
                stats_full = None
            
            # Extract years from FULL dataset BEFORE limiting (so we see all available years)
            if 'trade_date' in df_full.columns:
                df_years = df_full.copy()
                if pd.api.types.is_datetime64_any_dtype(df_years['trade_date']):
                    year_values = df_years['trade_date'].dt.year.dropna().unique().tolist()
                    year_values = [int(y) for y in year_values if not pd.isna(y)]
                    if year_values:
                        years = sorted(year_values, reverse=True)
            
            # Store in cache for subsequent requests
            _matrix_data_cache[cache_key] = (df_full, stats_full, years)
            _api_cache_misses += 1
            _last_matrix_row_count = len(df_full)
            try:
                from modules.matrix.instrumentation import log_timing_event
                duration_ms = int((time.perf_counter() - t_load) * 1000)
                log_timing_event(phase="api_matrix_load", duration_ms=duration_ms, cache_hit=False, row_count=len(df_full), file_path=str(file_to_load))
            except Exception:
                pass
        
        # df starts as df_full; we apply date filter, then limit/order to produce returned rows
        df = df_full.copy()
        
        # Apply date range filter if specified (reduces data transferred)
        if (start_date or end_date) and 'trade_date' in df.columns and pd.api.types.is_datetime64_any_dtype(df['trade_date']):
            if start_date:
                start_dt = pd.to_datetime(start_date)
                df = df[df['trade_date'] >= start_dt]
            if end_date:
                end_dt = pd.to_datetime(end_date)
                df = df[df['trade_date'] <= end_dt]
        
        total_after_date_filter = len(df)
        
        # Apply limit and ordering
        # limit=0 means no limit - return all trades
        if limit > 0:
            if order == "newest":
                # Sort by trade_date descending to get newest rows first
                # Ensure trade_date is datetime for proper sorting
                # DATE OWNERSHIP: trade_date should already be normalized in saved parquet files
                # Validate dtype but don't parse
                if 'trade_date' in df.columns:
                    from modules.matrix.data_loader import _validate_trade_date_dtype
                    try:
                        _validate_trade_date_dtype(df, "api_load")
                    except ValueError:
                        # If validation fails, log warning but continue (file may be from old version)
                        logger.warning("API: trade_date validation failed - file may be from old version")
                    df = df.sort_values('trade_date', ascending=False, na_position='last').head(limit)
                elif 'Date' in df.columns:
                    # Fallback for old files without trade_date
                    logger.warning("API: Using Date column as fallback - file may be from old version")
                    df = df.copy()
                    df['trade_date'] = pd.to_datetime(df['Date'], errors='coerce')
                    df = df.sort_values('trade_date', ascending=False, na_position='last').head(limit)
                else:
                    logger.warning("No date column found for newest-first sorting, using tail() fallback")
                    df = df.tail(limit)
            else:
                df = df.head(limit)
        
        # Reduce columns if requested (for faster initial load)
        if essential_columns_only:
            # Include all columns that the frontend expects in DEFAULT_COLUMNS
            essential_cols = [
                'Date', 'trade_date', 'Stream', 'Instrument', 'Profit', 'Result', 'Time',
                'EntryTime', 'ExitTime', 'Session', 'Direction', 'Target', 'Range', 
                'StopLoss', 'Peak', 'Time Change',
                'final_allowed', 'ProfitDollars', 'day_of_month', 'dow', 'dow_full', 'month', 'year',
            ]
            # Only include columns that exist in the dataframe
            available_cols = [col for col in essential_cols if col in df.columns]
            # Always include all columns that exist (don't drop any)
            df = df[available_cols]
        
        # Generate stable file ID (filename) for change detection (file_mtime already set above)
        matrix_file_id = file_to_load.name
        
        # Extract date ranges for debugging/metadata
        full_min_trade_date = None
        full_max_trade_date = None
        returned_min_trade_date = None
        returned_max_trade_date = None
        
        # DATE OWNERSHIP: trade_date should already be normalized in saved parquet files
        # Validate dtype but don't parse
        if 'trade_date' in df_full.columns:
            from modules.matrix.data_loader import _validate_trade_date_dtype
            try:
                _validate_trade_date_dtype(df_full, "api_full")
            except ValueError:
                logger.warning("API: trade_date validation failed for full dataset - file may be from old version")
            full_min_trade_date = df_full['trade_date'].min()
            full_max_trade_date = df_full['trade_date'].max()
        
        if 'trade_date' in df.columns:
            try:
                _validate_trade_date_dtype(df, "api_returned")
            except ValueError:
                logger.warning("API: trade_date validation failed for returned dataset - file may be from old version")
            returned_min_trade_date = df['trade_date'].min()
            returned_max_trade_date = df['trade_date'].max()
        
        # Ensure ProfitDollars column exists in the returned dataframe (for frontend use)
        # CRITICAL: Drop existing ProfitDollars first to force recalculation with correct contract values
        if "ProfitDollars" in df.columns:
            df = df.drop(columns=["ProfitDollars"])
        from modules.matrix import statistics
        df = statistics._ensure_profit_dollars_column(df, contract_multiplier=contract_multiplier)
        
        # Clean data if requested (expensive operation, can be skipped for faster initial load)
        if not skip_cleaning:
            # Convert to records and clean values
            records = df.to_dict('records')
            # Clean each record (handle NaN, None, etc.)
            cleaned_records = []
            for record in records:
                cleaned = {}
                for key, value in record.items():
                    if pd.isna(value):
                        cleaned[key] = None
                    elif isinstance(value, (pd.Timestamp, pd.DatetimeIndex)):
                        cleaned[key] = value.isoformat() if hasattr(value, 'isoformat') else str(value)
                    elif isinstance(value, (int, float)) and (pd.isna(value) or math.isinf(value)):
                        cleaned[key] = None
                    else:
                        cleaned[key] = value
                cleaned_records.append(cleaned)
            records = cleaned_records
        else:
            # Skip cleaning - just convert to records (faster)
            records = df.to_dict('records')
            # Still need to handle datetime serialization
            for record in records:
                for key, value in record.items():
                    if isinstance(value, (pd.Timestamp, pd.DatetimeIndex)):
                        record[key] = value.isoformat() if hasattr(value, 'isoformat') else str(value)
                    elif pd.isna(value):
                        record[key] = None
        
        # Extract unique streams and instruments from FULL dataset (before limiting)
        # Filter out None values before sorting to avoid comparison errors
        if 'Stream' in df_full.columns:
            stream_values = [s for s in df_full['Stream'].unique().tolist() if s is not None and pd.notna(s)]
            streams = sorted(stream_values)
        else:
            streams = []
        
        if 'Instrument' in df_full.columns:
            instrument_values = [i for i in df_full['Instrument'].unique().tolist() if i is not None and pd.notna(i)]
            instruments = sorted(instrument_values)
        else:
            instruments = []
        
        response_data = {
            "data": records,
            "total": total_after_date_filter if (start_date or end_date) else len(df_full),
            "loaded": len(records),
            "file": file_to_load.name,
            "matrix_file_id": matrix_file_id,  # Stable file identifier for change detection
            "file_mtime": file_mtime,
            "streams": streams,
            "instruments": instruments,
            "years": years,
            "stats_full": stats_full,
            "full_min_trade_date": str(full_min_trade_date) if full_min_trade_date is not None else None,
            "full_max_trade_date": str(full_max_trade_date) if full_max_trade_date is not None else None,
            "returned_min_trade_date": str(returned_min_trade_date) if returned_min_trade_date is not None else None,
            "returned_max_trade_date": str(returned_max_trade_date) if returned_max_trade_date is not None else None
        }
        
        # Use orjson for faster JSON serialization if available
        try:
            import orjson
            return JSONResponse(content=response_data, media_type="application/json")
        except ImportError:
            import json
            return JSONResponse(content=response_data, media_type="application/json")
    except Exception as e:
        import traceback
        error_detail = str(e)
        error_traceback = traceback.format_exc()
        logger.error(f"Failed to load matrix data: {error_detail}")
        logger.error(f"Traceback: {error_traceback}")
        raise HTTPException(status_code=500, detail=f"Failed to load matrix data: {error_detail}")


@router.post("/breakdown")
async def calculate_profit_breakdown(request: BreakdownRequest):
    """Calculate profit breakdowns (DOW, DOM, time, etc.) from the full dataset.
    
    This endpoint calculates breakdowns from ALL data in the parquet file, not just the loaded subset.
    """
    try:
        import pandas as pd
        
        # DOY breakdown is analysis-only. Day-based filters are not supported and will be ignored.
        if request.breakdown_type == 'doy':
            # DOY is analysis-only - no day-based filters allowed
            # This is defensive - StreamFilterConfig doesn't have exclude_days_of_year
            # But log if someone tries to add it in the future
            for stream_id, filter_config in (request.stream_filters or {}).items():
                if hasattr(filter_config, 'exclude_days_of_year'):
                    logger.warning(
                        f"DOY breakdown requested but day-based filters detected for {stream_id}. "
                        f"DOY is analysis-only - day filters will be ignored."
                    )
        
        # Find fullest matrix file (same logic as /data endpoint)
        root_matrix_dir = QTSW2_ROOT / "data" / "master_matrix"
        
        if not root_matrix_dir.exists():
            raise HTTPException(status_code=404, detail=f"Master matrix directory not found: {root_matrix_dir}")
        
        from modules.matrix.file_manager import get_best_matrix_file
        file_to_load = get_best_matrix_file(str(root_matrix_dir))
        
        if file_to_load is None:
            raise HTTPException(status_code=404, detail=f"No master matrix files found. Build the matrix first.")
        
        file_mtime = file_to_load.stat().st_mtime
        stream_include_tuple = tuple(sorted(request.stream_include or []))
        sf_hash = 0
        if request.stream_filters:
            import json
            sf_serialized = {k: (v.model_dump() if hasattr(v, 'model_dump') else v) for k, v in request.stream_filters.items()}
            sf_hash = hash(json.dumps(sf_serialized, sort_keys=True))
        cache_key = (str(file_to_load), file_mtime, request.breakdown_type, stream_include_tuple, request.contract_multiplier, request.use_filtered, sf_hash)
        if cache_key in _breakdown_cache:
            cached = _breakdown_cache[cache_key]
            logger.info(f"Breakdown cache hit for {request.breakdown_type}")
            return cached
        
        # Load full dataset
        def _load_parquet_sync():
            return pd.read_parquet(file_to_load)
        
        df = await asyncio.to_thread(_load_parquet_sync)
        
        if df.empty:
            raise HTTPException(status_code=404, detail=f"File {file_to_load.name} is empty")
        
        # Apply stream inclusion filter if specified (before other processing)
        if request.stream_include and len(request.stream_include) > 0 and 'Stream' in df.columns:
            original_len = len(df)
            df = df[df['Stream'].isin(request.stream_include)].copy()
            logger.info(f"Applied stream_include filter to breakdown: {original_len} -> {len(df)} rows (streams: {request.stream_include})")
        
        # Prepare dataframe
        from modules.matrix import statistics
        df = statistics._ensure_final_allowed(df)
        df = statistics._normalize_results(df)
        df = statistics._ensure_profit_column(df)
        df = statistics._ensure_profit_dollars_column(df, contract_multiplier=request.contract_multiplier)
        df = statistics._ensure_date_column(df)
        
        # Apply filters and calculate breakdown via breakdown_service
        if request.use_filtered and request.stream_filters:
            logger.info(f"Applying filters: use_filtered={request.use_filtered}, stream_filters keys={list(request.stream_filters.keys())}")
        from modules.matrix.breakdown_service import calculate_breakdown
        stream_filters_dict = dict(request.stream_filters) if request.stream_filters else {}
        breakdown, total_rows = calculate_breakdown(
            df,
            request.breakdown_type,
            stream_filters=stream_filters_dict,
            use_filtered=request.use_filtered
        )
        
        result = {
            "breakdown": breakdown,
            "breakdown_type": request.breakdown_type,
            "total_rows": total_rows,
            "contract_multiplier": request.contract_multiplier
        }
        _breakdown_cache[cache_key] = result
        return result
        
    except Exception as e:
        logger.error(f"Error calculating profit breakdown: {e}", exc_info=True)
        raise HTTPException(status_code=500, detail=str(e))


@router.post("/stream-stats")
async def get_stream_stats(request: StreamStatsRequest):
    """Get statistics for a specific stream using the full dataset."""
    try:
        import pandas as pd
        from modules.matrix import statistics
        
        # Find fullest matrix file (same logic as /data endpoint)
        root_matrix_dir = QTSW2_ROOT / "data" / "master_matrix"
        
        if not root_matrix_dir.exists():
            raise HTTPException(status_code=404, detail=f"Master matrix directory not found: {root_matrix_dir}")
        
        from modules.matrix.file_manager import get_best_matrix_file
        file_to_load = get_best_matrix_file(str(root_matrix_dir))
        
        if file_to_load is None:
            raise HTTPException(status_code=404, detail=f"No master matrix files found. Build the matrix first.")
        
        file_mtime = file_to_load.stat().st_mtime
        stats_cache_key = (str(file_to_load), file_mtime, request.stream_id, request.include_filtered_executed, request.contract_multiplier)
        if stats_cache_key in _stream_stats_cache:
            cached = _stream_stats_cache[stats_cache_key]
            logger.info(f"Stream stats cache hit for {request.stream_id}")
            return JSONResponse(content=cached, media_type="application/json")
        
        # Load full dataset (no limit, no column filtering - get everything)
        def _load_parquet_sync():
            return pd.read_parquet(file_to_load)
        
        df = await asyncio.to_thread(_load_parquet_sync)
        
        logger.info(f"Loaded full parquet file: {len(df)} total rows")
        
        if df.empty:
            raise HTTPException(status_code=404, detail=f"File {file_to_load.name} is empty")
        
        # Filter to specific stream
        if 'Stream' not in df.columns:
            raise HTTPException(status_code=400, detail="Stream column not found in data")
        
        stream_df = df[df['Stream'] == request.stream_id].copy()
        
        logger.info(f"Filtered to stream {request.stream_id}: {len(stream_df)} rows (should be full dataset for this stream)")
        
        if stream_df.empty:
            return JSONResponse(content={
                "stream_id": request.stream_id,
                "stats": None,
                "message": f"No data found for stream {request.stream_id}"
            }, media_type="application/json")
        
        # Prepare dataframe
        df_for_stats = statistics._ensure_final_allowed(stream_df)
        df_for_stats = statistics._normalize_results(df_for_stats)
        df_for_stats = statistics._ensure_profit_column(df_for_stats)
        
        # CRITICAL: Force recomputation of ProfitDollars with the correct contract values and multiplier
        # We MUST drop and recalculate to ensure correct values
        if "ProfitDollars" in df_for_stats.columns:
            df_for_stats = df_for_stats.drop(columns=["ProfitDollars"])
        
        # Calculate stats for this stream (this will recalculate ProfitDollars with correct contract values)
        logger.info(f"Calculating stats for stream {request.stream_id}: {len(stream_df)} rows (full dataset), contract_multiplier={request.contract_multiplier}")
        
        stats = statistics.calculate_summary_stats(
            df_for_stats,
            include_filtered_executed=request.include_filtered_executed,
            contract_multiplier=request.contract_multiplier
        )
        
        total_profit = stats.get('performance_trade_metrics', {}).get('total_profit', 'N/A') if stats else 'N/A'
        executed_trades = stats.get('sample_counts', {}).get('executed_trades_total', 'N/A') if stats else 'N/A'
        logger.info(f"Calculated stats for stream {request.stream_id}: total_profit={total_profit}, executed_trades={executed_trades}")
        
        result = {
            "stream_id": request.stream_id,
            "stats": stats,
            "contract_multiplier": request.contract_multiplier
        }
        _stream_stats_cache[stats_cache_key] = result
        return JSONResponse(content=result, media_type="application/json")
        
    except HTTPException as e:
        raise e
    except Exception as e:
        import traceback
        logger.error(f"Error calculating stream stats: {e}", exc_info=True)
        raise HTTPException(status_code=500, detail=f"Failed to calculate stream stats: {str(e)}")


@router.get("/performance")
async def get_matrix_performance():
    """
    Return matrix performance metrics for the dashboard.
    Aggregates from in-memory counters and timing logs.
    """
    try:
        # In-memory cache stats
        total_requests = _api_cache_hits + _api_cache_misses
        cache_hit_rate = (_api_cache_hits / total_requests * 100) if total_requests > 0 else None

        # Read timing log for recent phases (last 500 lines)
        timing_log = QTSW2_ROOT / "logs" / "matrix_timing.jsonl"
        rebuild_time_ms = None
        resequence_time_ms = None
        matrix_save_time_ms = None
        matrix_load_time_ms = None
        timetable_time_ms = None

        if timing_log.exists():
            lines = []
            try:
                with open(timing_log, "r", encoding="utf-8") as f:
                    lines = f.readlines()
            except Exception:
                pass

            # Take last 500 lines to limit memory
            recent = lines[-500:] if len(lines) > 500 else lines

            for line in recent:
                line = line.strip()
                if not line:
                    continue
                try:
                    import json
                    ev = json.loads(line)
                    phase = ev.get("phase")
                    dur = ev.get("duration_ms")
                    if phase == "full_rebuild" and dur is not None:
                        rebuild_time_ms = dur
                    elif phase == "rolling_resequence" and dur is not None and ev.get("error") is None:
                        resequence_time_ms = dur
                    elif phase == "matrix_save" and dur is not None:
                        matrix_save_time_ms = dur
                    elif phase == "api_matrix_load" and dur is not None and not ev.get("cache_hit"):
                        matrix_load_time_ms = dur
                    elif phase == "timetable_generation" and dur is not None:
                        timetable_time_ms = dur
                except (json.JSONDecodeError, KeyError):
                    continue

        # Matrix size: from last load, or parse from best file filename (no disk read)
        matrix_size = _last_matrix_row_count
        if matrix_size is None:
            try:
                from modules.matrix.file_manager import get_best_matrix_file
                from modules.matrix.file_manager import _parse_row_count_from_path
                root_matrix_dir = QTSW2_ROOT / "data" / "master_matrix"
                if root_matrix_dir.exists():
                    best_file = get_best_matrix_file(str(root_matrix_dir))
                    if best_file:
                        matrix_size = _parse_row_count_from_path(best_file)
            except Exception:
                pass

        return {
            "matrix_size": matrix_size,
            "rebuild_time_ms": rebuild_time_ms,
            "resequence_time_ms": resequence_time_ms,
            "matrix_save_time_ms": matrix_save_time_ms,
            "matrix_load_time_ms": matrix_load_time_ms,
            "timetable_time_ms": timetable_time_ms,
            "api_cache_hit_rate": round(cache_hit_rate, 1) if cache_hit_rate is not None else None,
            "api_cache_hits": _api_cache_hits,
            "api_cache_misses": _api_cache_misses,
        }
    except Exception as e:
        logger.error(f"Error getting performance metrics: {e}", exc_info=True)
        raise HTTPException(status_code=500, detail=str(e))


@router.post("/diff")
async def diff_matrices(request: MatrixDiffRequest):
    """
    Compare two matrix files. Returns differences in RS, Time, final_allowed, Profit
    for final rows (one per stream per date).
    """
    try:
        import pandas as pd
        root_matrix_dir = QTSW2_ROOT / "data" / "master_matrix"
        if not root_matrix_dir.exists():
            raise HTTPException(status_code=404, detail="Master matrix directory not found")

        def resolve_path(name: str) -> Path:
            p = Path(name)
            if not p.is_absolute():
                p = root_matrix_dir / name
            if not p.exists():
                raise HTTPException(status_code=404, detail=f"File not found: {name}")
            return p

        path_a = resolve_path(request.file_a)
        path_b = resolve_path(request.file_b)

        df_a = pd.read_parquet(path_a)
        df_b = pd.read_parquet(path_b)

        date_col = "trade_date" if "trade_date" in df_a.columns else "Date"
        for df in (df_a, df_b):
            if date_col not in df.columns:
                raise HTTPException(status_code=400, detail=f"Missing {date_col} column")

        # Final rows only; take one per (stream, date) - use last if duplicates
        final_a = df_a[df_a["final_allowed"] == True] if "final_allowed" in df_a.columns else df_a
        final_b = df_b[df_b["final_allowed"] == True] if "final_allowed" in df_b.columns else df_b
        final_a = final_a.drop_duplicates(subset=["Stream", date_col], keep="last")
        final_b = final_b.drop_duplicates(subset=["Stream", date_col], keep="last")

        # Merge on (Stream, trade_date)
        key_cols = ["Stream", date_col]
        cols_a = key_cols + ["Time", "Profit"]
        if "rs_value" in final_a.columns:
            cols_a.append("rs_value")
        merge_a = final_a[cols_a].copy()
        merge_a.columns = key_cols + ["Time_A", "Profit_A"] + (["rs_value_A"] if "rs_value" in final_a.columns else [])

        cols_b = key_cols + ["Time", "Profit"]
        if "rs_value" in final_b.columns:
            cols_b.append("rs_value")
        merge_b = final_b[cols_b].copy()
        merge_b.columns = key_cols + ["Time_B", "Profit_B"] + (["rs_value_B"] if "rs_value" in final_b.columns else [])

        merged = pd.merge(merge_a, merge_b, on=key_cols, how="outer")

        differences = []
        for _, row in merged.iterrows():
            diff = {}
            stream = row.get("Stream")
            td = row.get(date_col)
            if pd.isna(td) and hasattr(td, "strftime"):
                td_str = str(td)
            else:
                td_str = str(td)[:10] if pd.notna(td) else ""

            if "Time_A" in merged.columns and "Time_B" in merged.columns:
                ta, tb = row.get("Time_A"), row.get("Time_B")
                if str(ta) != str(tb):
                    diff["time"] = {"a": str(ta) if pd.notna(ta) else None, "b": str(tb) if pd.notna(tb) else None}
            if "rs_value_A" in merged.columns and "rs_value_B" in merged.columns:
                ra, rb = row.get("rs_value_A"), row.get("rs_value_B")
                if pd.notna(ra) and pd.notna(rb) and float(ra) != float(rb):
                    diff["rs_value"] = {"a": float(ra), "b": float(rb)}
                elif pd.notna(ra) != pd.notna(rb):
                    diff["rs_value"] = {"a": float(ra) if pd.notna(ra) else None, "b": float(rb) if pd.notna(rb) else None}
            if "Profit_A" in merged.columns and "Profit_B" in merged.columns:
                pa, pb = row.get("Profit_A"), row.get("Profit_B")
                if pd.notna(pa) and pd.notna(pb) and abs(float(pa) - float(pb)) > 1e-6:
                    diff["profit"] = {"a": float(pa), "b": float(pb)}

            if diff:
                differences.append({"stream": stream, "trade_date": td_str, "differences": diff})

        return {
            "file_a": request.file_a,
            "file_b": request.file_b,
            "rows_a": len(final_a),
            "rows_b": len(final_b),
            "differences": differences[:500],
            "total_differences": len(differences),
        }
    except HTTPException:
        raise
    except Exception as e:
        logger.error(f"Error diffing matrices: {e}", exc_info=True)
        raise HTTPException(status_code=500, detail=str(e))


@router.get("/stream-health")
async def get_stream_health():
    """
    Return per-stream health metrics: rolling RS, drawdown, hit rate.
    """
    try:
        import pandas as pd
        from modules.matrix.file_manager import get_best_matrix_file
        from modules.matrix.statistics import calculate_summary_stats

        root_matrix_dir = QTSW2_ROOT / "data" / "master_matrix"
        if not root_matrix_dir.exists():
            return {"streams": []}

        best_file = get_best_matrix_file(str(root_matrix_dir))
        if best_file is None or not best_file.exists():
            return {"streams": []}

        df = pd.read_parquet(best_file)
        if df.empty or "Stream" not in df.columns:
            return {"streams": []}

        streams = df["Stream"].dropna().unique().tolist()
        results = []
        for stream_id in sorted(streams):
            stream_df = df[df["Stream"] == stream_id].copy()
            if stream_df.empty:
                continue
            stream_df = stream_df.sort_values("trade_date" if "trade_date" in stream_df.columns else "Date")
            stats = calculate_summary_stats(stream_df, include_filtered_executed=True, contract_multiplier=1.0)
            perf = stats.get("performance_trade_metrics", {}) or {}
            risk = stats.get("risk_metrics", {}) or {}
            sample = stats.get("sample_counts", {}) or {}
            rs_recent = None
            if "rs_value" in stream_df.columns and stream_df["rs_value"].notna().any():
                try:
                    rs_recent = float(stream_df["rs_value"].dropna().iloc[-1])
                except (IndexError, ValueError):
                    pass
            results.append({
                "stream_id": stream_id,
                "win_rate": perf.get("win_rate"),
                "total_profit": perf.get("total_profit"),
                "max_drawdown": risk.get("max_drawdown"),
                "executed_trades": sample.get("executed_trades_total"),
                "rs_value_recent": rs_recent,
            })
        return {"streams": results}
    except Exception as e:
        logger.error(f"Error getting stream health: {e}", exc_info=True)
        raise HTTPException(status_code=500, detail=str(e))


@router.get("/files")
async def list_matrix_files():
    """List available master matrix files."""
    try:
        root_matrix_dir = QTSW2_ROOT / "data" / "master_matrix"
        
        if not root_matrix_dir.exists():
            return {"files": []}
        
        parquet_files = list(root_matrix_dir.glob("master_matrix_*.parquet"))
        
        files_info = []
        for f in parquet_files:
            stat = f.stat()
            files_info.append({
                "name": f.name,
                "size": stat.st_size,
                "mtime": stat.st_mtime
            })
        
        # Sort by mtime descending (newest first)
        files_info.sort(key=lambda x: x["mtime"], reverse=True)
        
        return {"files": files_info}
    except Exception as e:
        logger.error(f"Error listing matrix files: {e}", exc_info=True)
        raise HTTPException(status_code=500, detail=str(e))
