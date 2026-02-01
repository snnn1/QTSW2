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
from typing import Optional, List, Dict
from datetime import datetime

from fastapi import APIRouter, HTTPException
from fastapi.responses import JSONResponse, Response
from pydantic import BaseModel

# Calculate project root
QTSW2_ROOT = Path(__file__).parent.parent.parent

router = APIRouter(prefix="/api/matrix", tags=["matrix"])
logger = logging.getLogger(__name__)


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
            streams=request.streams
        )
        
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
        
        parquet_files = list(root_matrix_dir.glob("master_matrix_*.parquet"))
        
        if not parquet_files:
            logger.warning(f"No matrix files found in: {root_matrix_dir}")
            raise HTTPException(
                status_code=404, 
                detail=f"No master matrix files found in {root_matrix_dir.resolve()}. Please build the matrix first using 'Rebuild Matrix' button."
            )
        
        # Find most recent file by parsing timestamp from filename
        from datetime import datetime, timedelta
        now = datetime.now()
        max_future_tolerance = timedelta(days=1)
        
        def parse_timestamp_from_filename(file_path: Path) -> Optional[datetime]:
            """Parse timestamp from filename like master_matrix_YYYYMMDD_HHMMSS.parquet"""
            import re
            pattern = re.compile(r'master_matrix_(\d{8})_(\d{6})\.parquet$')
            match = pattern.search(file_path.name)
            if match:
                try:
                    date_str = match.group(1) + match.group(2)  # YYYYMMDDHHMMSS
                    return datetime.strptime(date_str, '%Y%m%d%H%M%S')
                except ValueError:
                    return None
            return None
        
        # Separate files with valid timestamps from those without
        files_with_timestamp = []
        files_without_timestamp = []
        
        for f in parquet_files:
            ts = parse_timestamp_from_filename(f)
            if ts:
                # Reject files dated more than 1 day in the future
                if ts <= now + max_future_tolerance:
                    files_with_timestamp.append((ts, f))
                else:
                    logger.warning(f"Rejecting future-dated file: {f.name} (timestamp: {ts})")
            else:
                files_without_timestamp.append(f)
        
        if files_with_timestamp:
            # Sort by timestamp descending (newest first)
            files_with_timestamp.sort(key=lambda x: x[0], reverse=True)
            file_to_load = files_with_timestamp[0][1]
            logger.info(f"Selected latest file by timestamp: {file_to_load.name}")
        elif files_without_timestamp:
            # Fallback to mtime if no valid timestamps found
            files_without_timestamp.sort(key=lambda p: p.stat().st_mtime, reverse=True)
            file_to_load = files_without_timestamp[0]
            logger.warning(f"No valid timestamped files found, using mtime fallback: {file_to_load.name}")
        else:
            raise HTTPException(status_code=404, detail=f"No valid master matrix files found. Checked: {root_matrix_dir}. Build the matrix first.")
        
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
async def get_matrix_data(file_path: Optional[str] = None, limit: int = 10000, order: str = "newest", essential_columns_only: bool = True, skip_cleaning: bool = False, contract_multiplier: float = 1.0, include_filtered_executed: bool = False, stream_include: Optional[str] = None):
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
    """
    # Parse stream_include into list
    stream_include_list = None
    if stream_include:
        stream_include_list = [s.strip() for s in stream_include.split(',') if s.strip()]
        if len(stream_include_list) == 0:
            stream_include_list = None
    
    logger.info(f"GET /api/matrix/data called with contract_multiplier={contract_multiplier}, include_filtered_executed={include_filtered_executed}, stream_include={stream_include_list}")
    
    try:
        import pandas as pd
        
        # Determine file to load
        root_matrix_dir = QTSW2_ROOT / "data" / "master_matrix"
        
        if not root_matrix_dir.exists():
            raise HTTPException(status_code=404, detail=f"Master matrix directory not found: {root_matrix_dir}")
        
        parquet_files = []
        if root_matrix_dir.exists():
            parquet_files.extend(root_matrix_dir.glob("master_matrix_*.parquet"))
        
        if file_path:
            # Use specified file
            file_to_load = Path(file_path)
            if not file_to_load.exists():
                raise HTTPException(status_code=404, detail=f"File not found: {file_path}")
        else:
            # Find most recent file by parsing timestamp from filename
            # Reject future-dated files (beyond 1 day tolerance) to avoid selecting invalid files
            if parquet_files:
                from datetime import datetime, timedelta
                now = datetime.now()
                max_future_tolerance = timedelta(days=1)
                
                def parse_timestamp_from_filename(file_path: Path) -> Optional[datetime]:
                    """Parse timestamp from filename like master_matrix_YYYYMMDD_HHMMSS.parquet"""
                    import re
                    pattern = re.compile(r'master_matrix_(\d{8})_(\d{6})\.parquet$')
                    match = pattern.search(file_path.name)
                    if match:
                        try:
                            date_str = match.group(1) + match.group(2)  # YYYYMMDDHHMMSS
                            return datetime.strptime(date_str, '%Y%m%d%H%M%S')
                        except ValueError:
                            return None
                    return None
                
                # Separate files with valid timestamps from those without
                files_with_timestamp = []
                files_without_timestamp = []
                
                for f in parquet_files:
                    ts = parse_timestamp_from_filename(f)
                    if ts:
                        # Reject files dated more than 1 day in the future
                        if ts <= now + max_future_tolerance:
                            files_with_timestamp.append((ts, f))
                        else:
                            logger.warning(f"Rejecting future-dated file: {f.name} (timestamp: {ts})")
                    else:
                        files_without_timestamp.append(f)
                
                if files_with_timestamp:
                    # Sort by timestamp descending (newest first)
                    files_with_timestamp.sort(key=lambda x: x[0], reverse=True)
                    file_to_load = files_with_timestamp[0][1]
                    logger.info(f"Selected latest file by timestamp: {file_to_load.name}")
                elif files_without_timestamp:
                    # Fallback to mtime if no valid timestamps found
                    files_without_timestamp.sort(key=lambda p: p.stat().st_mtime, reverse=True)
                    file_to_load = files_without_timestamp[0]
                    logger.warning(f"No valid timestamped files found, using mtime fallback: {file_to_load.name}")
                else:
                    raise HTTPException(status_code=404, detail=f"No valid master matrix files found. Checked: {root_matrix_dir}. Build the matrix first.")
            else:
                raise HTTPException(status_code=404, detail=f"No master matrix files found. Checked: {root_matrix_dir}. Build the matrix first.")
        
        # Load parquet file in thread pool to avoid blocking (large files)
        def _load_parquet_sync():
            """Synchronous parquet load to run in thread"""
            import pandas as pd
            return pd.read_parquet(file_to_load)
        
        try:
            df = await asyncio.to_thread(_load_parquet_sync)
        except Exception as e:
            raise HTTPException(status_code=500, detail=f"Failed to read file {file_to_load.name}: {str(e)}")
        
        if df.empty:
            raise HTTPException(status_code=404, detail=f"File {file_to_load.name} is empty")
        
        # Store full dataframe before any limiting/processing
        df_full = df.copy()
        
        # Apply stream inclusion filter if specified (before stats calculation)
        if stream_include_list and 'Stream' in df_full.columns:
            original_len = len(df_full)
            df_full = df_full[df_full['Stream'].isin(stream_include_list)].copy()
            logger.info(f"Applied stream_include filter: {original_len} -> {len(df_full)} rows (streams: {stream_include_list})")
        
        # Calculate stats from FULL dataset BEFORE limiting (so stats reflect all history)
        # CRITICAL: Always calculate stats_full from df_full to ensure stats cover ALL available data
        # Even if limit=0, we still calculate stats_full separately to maintain consistency
        stats_full = None
        try:
            from modules.matrix.master_matrix import MasterMatrix
            from modules.matrix import statistics
            # Calculate stats directly using statistics module to ensure proper data preparation
            # Use include_filtered_executed parameter from request to match UI toggle state
            # Apply contract_multiplier to match user's contract size setting
            # IMPORTANT: Use df_full (before limiting) for stats calculation, and ensure ProfitDollars is recomputed with multiplier
            # The statistics module will properly prepare the dataframe (normalize results, ensure columns, etc.)
            # Make a copy to avoid modifying the original df
            df_for_stats = df_full.copy()
            # Force recomputation of ProfitDollars with the correct multiplier by dropping it if it exists
            if "ProfitDollars" in df_for_stats.columns:
                df_for_stats = df_for_stats.drop(columns=["ProfitDollars"])
            stats_full = statistics.calculate_summary_stats(df_for_stats, include_filtered_executed=include_filtered_executed, contract_multiplier=contract_multiplier)
            total_profit = stats_full.get('performance_trade_metrics', {}).get('total_profit', 'N/A') if stats_full else 'N/A'
            total_rows = len(df_full)
            logger.info(f"Calculated full-dataset stats from {total_rows} total rows: {len(stats_full) if stats_full else 0} metrics (contract_multiplier={contract_multiplier}, include_filtered_executed={include_filtered_executed}, total_profit={total_profit})")
        except Exception as e:
            logger.error(f"CRITICAL: Could not calculate full-dataset stats - stats will be incomplete: {e}")
            import traceback
            logger.error(f"Stats calculation traceback: {traceback.format_exc()}")
            stats_full = None
        
        # Extract years from FULL dataset BEFORE limiting (so we see all available years)
        years = []
        if 'trade_date' in df.columns:
            df_years = df.copy()
            # Dtype guard: assert trade_date is datetime-like before .dt accessor
            if not pd.api.types.is_datetime64_any_dtype(df_years['trade_date']):
                raise ValueError(
                    f"api.get_matrix_data: trade_date is {df_years['trade_date'].dtype}, "
                    f"cannot use .dt accessor. No fallback to Date column."
                )
            year_values = df_years['trade_date'].dt.year.dropna().unique().tolist()
            year_values = [int(y) for y in year_values if not pd.isna(y)]
            if year_values:
                try:
                    years = sorted(year_values, reverse=True)  # Newest first
                except Exception as e:
                    logger.error(f"Error sorting years: {e}, values: {year_values[:10]}")
                    years = sorted(year_values, reverse=True) if year_values else []
        
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
        
        # Get file modification time for cache/version checking
        file_mtime = file_to_load.stat().st_mtime
        
        # Generate stable file ID (filename) for change detection
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
            "total": len(df_full), # Total rows in the full parquet file
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
        
        # Find latest matrix file (same logic as /data endpoint)
        root_matrix_dir = QTSW2_ROOT / "data" / "master_matrix"
        
        if not root_matrix_dir.exists():
            raise HTTPException(status_code=404, detail=f"Master matrix directory not found: {root_matrix_dir}")
        
        parquet_files = list(root_matrix_dir.glob("master_matrix_*.parquet"))
        
        if not parquet_files:
            raise HTTPException(status_code=404, detail=f"No master matrix files found. Build the matrix first.")
        
        # Find latest file by timestamp or mtime
        from datetime import datetime, timedelta
        import re
        now = datetime.now()
        max_future_tolerance = timedelta(days=1)
        
        def parse_timestamp_from_filename(file_path: Path) -> Optional[datetime]:
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
            file_to_load = files_with_timestamp[0][1]
        elif files_without_timestamp:
            files_without_timestamp.sort(key=lambda p: p.stat().st_mtime, reverse=True)
            file_to_load = files_without_timestamp[0]
        else:
            raise HTTPException(status_code=404, detail="No valid master matrix files found")
        
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
        
        # Apply filters if requested
        if request.use_filtered and request.stream_filters:
            logger.info(f"Applying filters: use_filtered={request.use_filtered}, stream_filters keys={list(request.stream_filters.keys())}")
            # Apply stream filters - when 'master' is used, apply filters to all streams
            # Otherwise, apply filters per stream
            mask = pd.Series([True] * len(df))
            
            # Check if we have 'master' filters (apply to all streams)
            master_filters = request.stream_filters.get('master')
            if master_filters:
                # Apply master filters to all rows
                if master_filters.exclude_days_of_week:
                    if 'dow_full' in df.columns:
                        mask = mask & ~df['dow_full'].isin(master_filters.exclude_days_of_week)
                    elif 'dow' in df.columns:
                        dow_map = {'Monday': 0, 'Tuesday': 1, 'Wednesday': 2, 'Thursday': 3, 'Friday': 4, 'Saturday': 5, 'Sunday': 6}
                        exclude_dow_nums = [dow_map.get(d, -1) for d in master_filters.exclude_days_of_week]
                        mask = mask & ~df['dow'].isin(exclude_dow_nums)
                
                if master_filters.exclude_days_of_month:
                    if 'day_of_month' in df.columns:
                        mask = mask & ~df['day_of_month'].isin(master_filters.exclude_days_of_month)
                
                if master_filters.exclude_times:
                    if 'Time' in df.columns:
                        mask = mask & ~df['Time'].isin(master_filters.exclude_times)
            
            # Apply per-stream filters (for specific streams)
            for stream_id, filter_config in request.stream_filters.items():
                if stream_id == 'master':
                    continue  # Already handled above
                
                # Create stream-specific mask
                stream_rows = df['Stream'] == stream_id
                stream_mask = pd.Series([True] * len(df))
                
                if filter_config.exclude_days_of_week:
                    if 'dow_full' in df.columns:
                        stream_mask = stream_mask & ~df['dow_full'].isin(filter_config.exclude_days_of_week)
                    elif 'dow' in df.columns:
                        dow_map = {'Monday': 0, 'Tuesday': 1, 'Wednesday': 2, 'Thursday': 3, 'Friday': 4, 'Saturday': 5, 'Sunday': 6}
                        exclude_dow_nums = [dow_map.get(d, -1) for d in filter_config.exclude_days_of_week]
                        stream_mask = stream_mask & ~df['dow'].isin(exclude_dow_nums)
                
                if filter_config.exclude_days_of_month:
                    if 'day_of_month' in df.columns:
                        stream_mask = stream_mask & ~df['day_of_month'].isin(filter_config.exclude_days_of_month)
                
                if filter_config.exclude_times:
                    if 'Time' in df.columns:
                        stream_mask = stream_mask & ~df['Time'].isin(filter_config.exclude_times)
                
                # Apply stream-specific filters only to that stream's rows
                mask = mask & (~stream_rows | stream_mask)
            
            # Also apply final_allowed filter
            if 'final_allowed' in df.columns:
                mask = mask & (df['final_allowed'] != False)
            
            original_len = len(df)
            df = df[mask].copy()
            logger.info(f"After filtering: {len(df)} rows remaining (from {original_len} total)")
        
        # Calculate breakdown based on type
        breakdown = {}
        
        if request.breakdown_type in ['day', 'dow']:
            # Day of week breakdown - format matches frontend worker output
            # Group by both DOW and Stream to show each stream's data separately
            if 'dow_full' in df.columns and 'Stream' in df.columns:
                grouped = df.groupby(['dow_full', 'Stream'])['ProfitDollars'].sum().reset_index()
                for _, row in grouped.iterrows():
                    dow = str(row['dow_full'])
                    stream = str(row['Stream'])
                    profit = float(row['ProfitDollars'])
                    if dow not in breakdown:
                        breakdown[dow] = {}
                    breakdown[dow][stream] = profit
            elif 'dow' in df.columns and 'Stream' in df.columns:
                dow_names = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday']
                grouped = df.groupby(['dow', 'Stream'])['ProfitDollars'].sum().reset_index()
                for _, row in grouped.iterrows():
                    dow_num = int(row['dow'])
                    dow_name = dow_names[dow_num] if 0 <= dow_num < len(dow_names) else f'Day{dow_num}'
                    stream = str(row['Stream'])
                    profit = float(row['ProfitDollars'])
                    if dow_name not in breakdown:
                        breakdown[dow_name] = {}
                    breakdown[dow_name][stream] = profit
        
        elif request.breakdown_type == 'dom':
            # Day of month breakdown - format matches frontend worker output
            # Group by both DOM and Stream to show each stream's data separately
            if 'day_of_month' in df.columns and 'Stream' in df.columns:
                grouped = df.groupby(['day_of_month', 'Stream'])['ProfitDollars'].sum().reset_index()
                for _, row in grouped.iterrows():
                    dom = int(row['day_of_month'])
                    stream = str(row['Stream'])
                    profit = float(row['ProfitDollars'])
                    if dom not in breakdown:
                        breakdown[dom] = {}
                    breakdown[dom][stream] = profit
        
        elif request.breakdown_type == 'time':
            # Time breakdown - format matches frontend worker output (grouped by Time and Stream)
            # Returns: {"08:00": {"ES1": 1000, "ES2": 2000}, "09:00": {...}}
            if 'Time' in df.columns and 'Stream' in df.columns:
                # Filter out invalid times
                valid_df = df[df['Time'].notna() & (df['Time'] != 'NA') & (df['Time'] != '00:00')].copy()
                
                # Group by both Time and Stream to show each stream's data separately
                grouped = valid_df.groupby(['Time', 'Stream'])['ProfitDollars'].sum().reset_index()
                
                for _, row in grouped.iterrows():
                    time = str(row['Time']).strip()
                    stream = str(row['Stream'])
                    profit = float(row['ProfitDollars'])
                    
                    if time not in breakdown:
                        breakdown[time] = {}
                    breakdown[time][stream] = profit
                
                logger.info(f"Time breakdown: {len(breakdown)} time slots, streams: {set([s for slots in breakdown.values() for s in slots.keys()])}")
        
        elif request.breakdown_type == 'month':
            # Month breakdown - format matches frontend worker output (grouped by Month and Stream)
            # Returns: {"2024-01": {"ES1": 1000, "ES2": 2000}, "2024-02": {...}}
            # Frontend expects format "YYYY-MM" (e.g., "2024-01")
            if 'trade_date' in df.columns and 'Stream' in df.columns:
                # Dtype guard: assert trade_date is datetime-like before .dt accessor
                if not pd.api.types.is_datetime64_any_dtype(df['trade_date']):
                    raise ValueError(
                        f"api.calculate_profit_breakdown: trade_date is {df['trade_date'].dtype}, "
                        f"cannot use .dt accessor. No fallback to Date column."
                    )
                # Create a copy to avoid modifying the original dataframe
                df_month = df.copy()
                # Create year-month string in format "YYYY-MM"
                # Use strftime to ensure consistent "YYYY-MM" format
                # CRITICAL: Use unique column name 'year_month_str' to avoid conflicts with existing 'month' column
                df_month['year_month_str'] = df_month['trade_date'].dt.strftime('%Y-%m')
                
                # Verify the format is correct and log for debugging
                if len(df_month) > 0:
                    sample_year_month = df_month['year_month_str'].iloc[0]
                    logger.info(f"Month breakdown: Created year_month_str column, sample value: '{sample_year_month}' (type: {type(sample_year_month).__name__})")
                    # Check if 'month' column exists (should NOT be used)
                    if 'month' in df_month.columns:
                        sample_month = df_month['month'].iloc[0]
                        logger.warning(f"Month breakdown: Existing 'month' column found (value: {sample_month}), but using year_month_str instead")
                
                # Group by both Year-Month and Stream to show each stream's data separately
                # CRITICAL: Must use 'year_month_str', NOT 'month'
                grouped = df_month.groupby(['year_month_str', 'Stream'])['ProfitDollars'].sum().reset_index()
                logger.info(f"Month breakdown: Grouped into {len(grouped)} rows using year_month_str column")
                
                # Debug: Verify grouped result has correct column
                if len(grouped) > 0:
                    first_year_month = grouped['year_month_str'].iloc[0]
                    logger.info(f"Month breakdown: First grouped year_month_str value: '{first_year_month}' (type: {type(first_year_month).__name__})")
                
                for _, row in grouped.iterrows():
                    # year_month_str is already a string in "YYYY-MM" format from strftime
                    year_month = str(row['year_month_str']).strip()
                    stream = str(row['Stream'])
                    profit = float(row['ProfitDollars'])
                    
                    # Validate format (should be "YYYY-MM")
                    if len(year_month) != 7 or year_month[4] != '-':
                        logger.error(f"Month breakdown: Invalid year_month format: '{year_month}' (expected 'YYYY-MM'), row: {dict(row)}")
                        continue
                    
                    if year_month not in breakdown:
                        breakdown[year_month] = {}
                    breakdown[year_month][stream] = profit
                
                sample_keys = list(breakdown.keys())[:3] if len(breakdown) > 0 else []
                logger.info(f"Month breakdown: {len(breakdown)} months, sample keys: {sample_keys}, streams: {set([s for months in breakdown.values() for s in months.keys()])}")
        
        elif request.breakdown_type == 'year':
            # Year breakdown - format matches frontend worker output (grouped by Year and Stream)
            # Returns: {2024: {"ES1": 1000, "ES2": 2000}, 2025: {...}}
            # trade_date should already be datetime from DataLoader normalization
            if 'trade_date' in df.columns and 'Stream' in df.columns:
                # Dtype guard: assert trade_date is datetime-like before .dt accessor
                if not pd.api.types.is_datetime64_any_dtype(df['trade_date']):
                    raise ValueError(
                        f"api.calculate_profit_breakdown: trade_date is {df['trade_date'].dtype}, "
                        f"cannot use .dt accessor. No fallback to Date column."
                    )
                df['year'] = df['trade_date'].dt.year
                # Group by both Year and Stream to show each stream's data separately
                grouped = df.groupby(['year', 'Stream'])['ProfitDollars'].sum().reset_index()
                for _, row in grouped.iterrows():
                    year = int(row['year'])
                    stream = str(row['Stream'])
                    profit = float(row['ProfitDollars'])
                    if year not in breakdown:
                        breakdown[year] = {}
                    breakdown[year][stream] = profit
                
                logger.info(f"Year breakdown: {len(breakdown)} years, streams: {set([s for years in breakdown.values() for s in years.keys()])}")
        
        return {
            "breakdown": breakdown,
            "breakdown_type": request.breakdown_type,
            "total_rows": len(df),
            "contract_multiplier": request.contract_multiplier
        }
        
    except Exception as e:
        logger.error(f"Error calculating profit breakdown: {e}", exc_info=True)
        raise HTTPException(status_code=500, detail=str(e))


@router.post("/stream-stats")
async def get_stream_stats(request: StreamStatsRequest):
    """Get statistics for a specific stream using the full dataset."""
    try:
        import pandas as pd
        from modules.matrix import statistics
        
        # Find latest matrix file (same logic as /data endpoint)
        root_matrix_dir = QTSW2_ROOT / "data" / "master_matrix"
        
        if not root_matrix_dir.exists():
            raise HTTPException(status_code=404, detail=f"Master matrix directory not found: {root_matrix_dir}")
        
        parquet_files = list(root_matrix_dir.glob("master_matrix_*.parquet"))
        
        if not parquet_files:
            raise HTTPException(status_code=404, detail=f"No master matrix files found. Build the matrix first.")
        
        # Use same file selection logic as /data endpoint
        from datetime import datetime, timedelta
        now = datetime.now()
        max_future_tolerance = timedelta(days=1)
        
        def parse_timestamp_from_filename(file_path: Path) -> Optional[datetime]:
            try:
                # Extract timestamp from filename: master_matrix_YYYYMMDD_HHMMSS.parquet
                name = file_path.stem
                if '_' in name:
                    parts = name.split('_')
                    if len(parts) >= 3:
                        date_str = parts[2]  # YYYYMMDD
                        time_str = parts[3] if len(parts) > 3 else "000000"  # HHMMSS
                        timestamp_str = f"{date_str}_{time_str}"
                        return datetime.strptime(timestamp_str, "%Y%m%d_%H%M%S")
            except Exception:
                pass
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
            file_to_load = files_with_timestamp[0][1]
        elif files_without_timestamp:
            files_without_timestamp.sort(key=lambda p: p.stat().st_mtime, reverse=True)
            file_to_load = files_without_timestamp[0]
        else:
            raise HTTPException(status_code=404, detail="No valid master matrix files found")
        
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
        
        return JSONResponse(content={
            "stream_id": request.stream_id,
            "stats": stats,
            "contract_multiplier": request.contract_multiplier
        }, media_type="application/json")
        
    except HTTPException as e:
        raise e
    except Exception as e:
        import traceback
        logger.error(f"Error calculating stream stats: {e}", exc_info=True)
        raise HTTPException(status_code=500, detail=f"Failed to calculate stream stats: {str(e)}")


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
