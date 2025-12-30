"""
Master Matrix API Router
FastAPI endpoints for master matrix operations
"""

import asyncio
import importlib
import inspect
import logging
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


# ============================================================
# Endpoints
# ============================================================

@router.post("/build")
async def build_master_matrix(request: MatrixBuildRequest):
    """Build master matrix from all streams."""
    # Get logger first
    logger.info("=" * 80)
    logger.info("BUILD ENDPOINT HIT!")
    logger.info("=" * 80)
    
    # Write directly to master_matrix.log FIRST (before module reload) - FORCE IT
    master_matrix_log = QTSW2_ROOT / "logs" / "master_matrix.log"
    timestamp = datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    try:
        master_matrix_log.parent.mkdir(parents=True, exist_ok=True)
        # Open in append mode, write immediately, force flush
        f = open(master_matrix_log, 'a', encoding='utf-8')
        f.write(f"{timestamp} - INFO - {'=' * 80}\n")
        f.write(f"{timestamp} - INFO - BUILD ENDPOINT HIT!\n")
        f.write(f"{timestamp} - INFO - {'=' * 80}\n")
        f.flush()
        if hasattr(f, 'fileno'):
            try:
                import os
                os.fsync(f.fileno())
            except:
                pass
        f.close()
        # Also print to stderr to confirm
        print(f"[DEBUG] Wrote BUILD ENDPOINT HIT to {master_matrix_log}", file=sys.stderr, flush=True)
    except Exception as e:
        error_msg = f"ERROR writing to master_matrix.log: {e}"
        logger.error(error_msg)
        print(error_msg, file=sys.stderr, flush=True)
    
    # RELOAD MODULE FIRST - before anything else
    try:
        sys.path.insert(0, str(QTSW2_ROOT))
        
        # Use centralized module reloader to ensure latest code is loaded
        from modules.matrix.module_reloader import ensure_matrix_modules_reloaded
        ensure_matrix_modules_reloaded()
        
        # Import from modules.matrix.master_matrix
        from modules.matrix.master_matrix import MasterMatrix
        
        # Verify the signature
        sig = inspect.signature(MasterMatrix.__init__)
        params = list(sig.parameters.keys())
        logger.info(f"MasterMatrix signature: {params}")
        
    except Exception as e:
        logger.error(f"ERROR loading MasterMatrix module: {e}")
        raise HTTPException(status_code=500, detail=f"Failed to load MasterMatrix module: {e}")
    
    # Write to master_matrix.log using the master_matrix logger (after successful reload)
    master_matrix_logger = logging.getLogger('modules.matrix.master_matrix')
    master_matrix_logger.info("BUILD ENDPOINT CALLED - Master Matrix import successful")
    master_matrix_logger.info("About to process stream_filters")
    
    # Convert stream_filters from Pydantic models to dicts
    stream_filters_dict = None
    if request.stream_filters:
        stream_filters_dict = {
            stream_id: {
                "exclude_days_of_week": filter_config.exclude_days_of_week,
                "exclude_days_of_month": filter_config.exclude_days_of_month,
                "exclude_times": filter_config.exclude_times
            }
            for stream_id, filter_config in request.stream_filters.items()
        }
    
    # Write debug info to master matrix log using logger
    master_matrix_logger.info("=" * 80)
    master_matrix_logger.info("BUILDING MASTER MATRIX - FILTER DEBUG")
    master_matrix_logger.info(f"Streams to rebuild: {request.streams}")
    master_matrix_logger.info(f"Stream filters received: {stream_filters_dict}")
    if stream_filters_dict:
        for stream_id, filters in stream_filters_dict.items():
            exclude_times = filters.get('exclude_times', [])
            if exclude_times:
                master_matrix_logger.info(f"  {stream_id}: exclude_times = {exclude_times}")
    master_matrix_logger.info("=" * 80)
    
    logger.info("=" * 80)
    logger.info("[DEBUG] BUILDING MASTER MATRIX")
    logger.info(f"[DEBUG] Streams: {request.streams}")
    logger.info(f"[DEBUG] Filters: {stream_filters_dict}")
    if stream_filters_dict:
        for stream_id, filters in stream_filters_dict.items():
            exclude_times = filters.get('exclude_times', [])
            if exclude_times:
                logger.info(f"[DEBUG]   {stream_id}: exclude_times = {exclude_times}")
    logger.info("=" * 80)
    
    try:
        # Initialize MasterMatrix
        logger.info("About to create MasterMatrix instance")
        
        # Only pass parameters that exist in the signature
        sig = inspect.signature(MasterMatrix.__init__)
        valid_params = set(sig.parameters.keys()) - {'self'}
        logger.info(f"MasterMatrix valid parameters: {valid_params}")
        
        # Build kwargs only with valid parameters
        init_kwargs = {}
        if "analyzer_runs_dir" in valid_params:
            # Convert relative path to absolute path
            analyzer_runs_path = Path(request.analyzer_runs_dir)
            if not analyzer_runs_path.is_absolute():
                analyzer_runs_path = QTSW2_ROOT / analyzer_runs_path
            init_kwargs["analyzer_runs_dir"] = str(analyzer_runs_path)
        if "stream_filters" in valid_params and stream_filters_dict is not None:
            init_kwargs["stream_filters"] = stream_filters_dict
        
        logger.info(f"Creating MasterMatrix with kwargs: {init_kwargs}")
        
        try:
            matrix = MasterMatrix(**init_kwargs)
            logger.info("MasterMatrix created successfully")
        except TypeError as e:
            error_str = str(e)
            logger.error(f"TypeError creating MasterMatrix: {error_str}")
            logger.error(f"init_kwargs passed: {init_kwargs}")
            logger.error(f"Valid params: {valid_params}")
            raise HTTPException(status_code=500, detail=error_str)
        
        print("Calling build_master_matrix...", file=sys.stderr)
        sys.stderr.flush()
        logger.info("Calling build_master_matrix...")
        
        # Convert analyzer_runs_dir to absolute path for build_master_matrix call
        analyzer_runs_path = Path(request.analyzer_runs_dir)
        if not analyzer_runs_path.is_absolute():
            analyzer_runs_path = QTSW2_ROOT / analyzer_runs_path
        
        # Run heavy matrix build in thread pool to avoid blocking FastAPI event loop
        def _build_matrix_sync():
            """Synchronous matrix build function to run in thread"""
            return matrix.build_master_matrix(
                start_date=request.start_date,
                end_date=request.end_date,
                specific_date=request.specific_date,
                output_dir=request.output_dir,
                stream_filters=stream_filters_dict,
                analyzer_runs_dir=str(analyzer_runs_path),
                streams=request.streams
            )
        
        # Execute in thread pool (non-blocking for event loop)
        master_df = await asyncio.to_thread(_build_matrix_sync)
        
        print(f"Build complete. Trades: {len(master_df)}", file=sys.stderr)
        sys.stderr.flush()
        logger.info(f"Build complete. Trades: {len(master_df)}")
        
        if master_df.empty:
            return {
                "status": "success",
                "message": "Master matrix built but is empty",
                "trades": 0,
                "streams": [],
                "instruments": []
            }
        
        # Calculate summary statistics
        stats = matrix._log_summary_stats(master_df)
        
        return {
            "status": "success",
            "message": "Master matrix built successfully",
            "trades": len(master_df),
            "date_range": {
                "start": str(master_df['trade_date'].min()),
                "end": str(master_df['trade_date'].max())
            },
            "streams": sorted(master_df['Stream'].unique().tolist()),
            "instruments": sorted(master_df['Instrument'].unique().tolist()),
            "allowed_trades": int(master_df['final_allowed'].sum()),
            "statistics": stats
        }
    except HTTPException:
        raise
    except Exception as e:
        raise HTTPException(status_code=500, detail=f"Failed to build master matrix: {str(e)}")


class MatrixUpdateRequest(BaseModel):
    mode: str = "window"  # "window" for rolling window update
    reprocess_days: Optional[int] = None  # Optional override (default from config)
    analyzer_runs_dir: str = "data/analyzed"
    output_dir: str = "data/master_matrix"
    stream_filters: Optional[Dict[str, StreamFilterConfig]] = None


@router.post("/update")
async def update_master_matrix(request: MatrixUpdateRequest):
    """Update master matrix using rolling window update."""
    logger.info("=" * 80)
    logger.info("UPDATE ENDPOINT HIT!")
    logger.info("=" * 80)
    
    if request.mode != "window":
        raise HTTPException(status_code=400, detail=f"Unsupported update mode: {request.mode}")
    
    try:
        # Reload module to ensure latest code
        sys.path.insert(0, str(QTSW2_ROOT))
        from modules.matrix.module_reloader import ensure_matrix_modules_reloaded
        ensure_matrix_modules_reloaded()
        
        from modules.matrix.master_matrix import MasterMatrix
        
        # Convert stream_filters from Pydantic models to dicts
        stream_filters_dict = None
        if request.stream_filters:
            stream_filters_dict = {
                stream_id: {
                    "exclude_days_of_week": filter_config.exclude_days_of_week,
                    "exclude_days_of_month": filter_config.exclude_days_of_month,
                    "exclude_times": filter_config.exclude_times
                }
                for stream_id, filter_config in request.stream_filters.items()
            }
        
        # Initialize MasterMatrix
        analyzer_runs_path = Path(request.analyzer_runs_dir)
        if not analyzer_runs_path.is_absolute():
            analyzer_runs_path = QTSW2_ROOT / analyzer_runs_path
        
        matrix = MasterMatrix(
            analyzer_runs_dir=str(analyzer_runs_path),
            stream_filters=stream_filters_dict
        )
        
        # Convert output_dir to absolute path
        output_path = Path(request.output_dir)
        if not output_path.is_absolute():
            output_path = QTSW2_ROOT / output_path
        
        # Run window update in thread pool
        def _update_matrix_sync():
            """Synchronous matrix update function to run in thread"""
            return matrix.build_master_matrix_window_update(
                reprocess_days=request.reprocess_days,
                output_dir=str(output_path),
                stream_filters=stream_filters_dict,
                analyzer_runs_dir=str(analyzer_runs_path)
            )
        
        updated_df, run_summary = await asyncio.to_thread(_update_matrix_sync)
        
        if 'error' in run_summary:
            raise HTTPException(status_code=500, detail=run_summary['error'])
        
        if updated_df.empty:
            return {
                "status": "success",
                "message": "Window update completed but matrix is empty",
                **run_summary
            }
        
        # Calculate summary statistics
        stats = matrix._log_summary_stats(updated_df)
        
        return {
            "status": "success",
            "message": "Master matrix updated successfully",
            "trades": len(updated_df),
            "date_range": {
                "start": str(updated_df['trade_date'].min()),
                "end": str(updated_df['trade_date'].max())
            },
            "streams": sorted(updated_df['Stream'].unique().tolist()),
            "instruments": sorted(updated_df['Instrument'].unique().tolist()),
            "allowed_trades": int(updated_df['final_allowed'].sum()),
            "statistics": stats,
            **run_summary
        }
    except HTTPException:
        raise
    except Exception as e:
        logger.error(f"Failed to update master matrix: {e}")
        import traceback
        logger.debug(f"Traceback: {traceback.format_exc()}")
        raise HTTPException(status_code=500, detail=f"Failed to update master matrix: {str(e)}")


@router.get("/files")
async def list_matrix_files():
    """List available master matrix files."""
    try:
        # Check both possible locations
        root_matrix_dir = QTSW2_ROOT / "data" / "master_matrix"
        
        # Collect files
        parquet_files = []
        if root_matrix_dir.exists():
            parquet_files.extend(root_matrix_dir.glob("master_matrix_*.parquet"))
        
        # Remove duplicates and sort by modification time
        parquet_files = sorted(set(parquet_files), key=lambda p: p.stat().st_mtime, reverse=True)
        
        files = []
        for file_path in parquet_files:
            files.append({
                "name": file_path.name,
                "path": str(file_path),
                "size": file_path.stat().st_size,
                "modified": datetime.fromtimestamp(file_path.stat().st_mtime).isoformat()
            })
        
        return {"files": files[:20]}  # Return last 20 files
    except Exception as e:
        raise HTTPException(status_code=500, detail=f"Failed to list matrix files: {str(e)}")


@router.get("/data")
async def get_matrix_data(file_path: Optional[str] = None, limit: int = 0, essential_columns_only: bool = True, skip_cleaning: bool = False):
    """Get master matrix data from the most recent file or specified file.
    
    Args:
        file_path: Optional path to specific file
        limit: Maximum number of rows to return (0 = no limit, default 0 to return all trades)
        essential_columns_only: If True, return only essential columns for faster initial load (default True)
        skip_cleaning: If True, skip expensive per-value sanitization for faster initial load (default False)
    """
    try:
        import pandas as pd
        
        # Check both possible locations
        root_matrix_dir = QTSW2_ROOT / "data" / "master_matrix"
        
        # Collect files from both locations
        parquet_files = []
        if root_matrix_dir.exists():
            parquet_files.extend(root_matrix_dir.glob("master_matrix_*.parquet"))
        
        if file_path:
            # Use specified file
            file_to_load = Path(file_path)
            if not file_to_load.exists():
                raise HTTPException(status_code=404, detail=f"File not found: {file_path}")
        else:
            # Find most recent file
            if parquet_files:
                parquet_files = sorted(parquet_files, key=lambda p: p.stat().st_mtime, reverse=True)
                file_to_load = parquet_files[0]
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
        
        # Extract years from FULL dataset BEFORE limiting (so we see all available years)
        years = []
        if 'trade_date' in df.columns:
            df_years = df.copy()
            df_years['trade_date'] = pd.to_datetime(df_years['trade_date'], errors='coerce')
            year_values = df_years['trade_date'].dt.year.dropna().unique().tolist()
            year_values = [int(y) for y in year_values if not pd.isna(y)]
            if year_values:
                try:
                    years = sorted(year_values, reverse=True)  # Newest first
                except Exception as e:
                    logger.error(f"Error sorting years: {e}, values: {year_values[:10]}")
                    years = sorted(year_values, reverse=True) if year_values else []
        
        # Apply limit (assume MasterMatrix output is already correctly sorted)
        # limit=0 means no limit - return all trades
        if limit > 0:
            df = df.head(limit)
        
        # Strict essential columns list for initial load (no dynamic additions)
        # Identity: Date, trade_date, Stream, Instrument
        # Ordering: Time, EntryTime, ExitTime
        # Outcome: Result, Profit, final_allowed
        # Trading details: Target, Range, StopLoss, Peak
        # Minimal context: Session, Direction, DOW
        ESSENTIAL_COLUMNS = [
            'Date', 'trade_date', 'Stream', 'Instrument',  # Identity
            'Time', 'EntryTime', 'ExitTime',  # Ordering
            'Result', 'Profit', 'final_allowed',  # Outcome
            'Target', 'Range', 'StopLoss', 'Peak',  # Trading details
            'Session', 'Direction', 'DOW',  # Minimal context
            'Time Change'  # Essential for sequencer visualization
        ]
        
        # Reduce columns for faster initial load (strict list only, no dynamic additions)
        if essential_columns_only:
            # Only keep columns that exist in the dataframe
            available_essential = [col for col in ESSENTIAL_COLUMNS if col in df.columns]
            df = df[available_essential]
        
        # Get file modification time (when matrix was actually built/updated)
        file_mtime = None
        try:
            if file_to_load.exists():
                import os
                from datetime import datetime
                mtime_seconds = os.path.getmtime(file_to_load)
                file_mtime = datetime.fromtimestamp(mtime_seconds).isoformat()
        except Exception as e:
            logger.debug(f"Could not get file modification time: {e}")
        
        # Convert to records - use fast path for initial load if skip_cleaning is True
        if skip_cleaning:
            # Fast path: Use pandas native conversion with single pandas-level replace
            # Replace all NaN/NA values at DataFrame level (O(n) not O(n×m))
            import numpy as np
            # Replace pd.NA and NaN with None in a single operation
            df_cleaned = df.replace({pd.NA: None, np.nan: None})
            # Convert datetime columns to strings for JSON serialization
            # This ensures pandas Timestamp objects are converted before to_dict('records')
            for col in df_cleaned.columns:
                if pd.api.types.is_datetime64_any_dtype(df_cleaned[col]):
                    df_cleaned[col] = df_cleaned[col].astype(str).replace('NaT', None)
            records = df_cleaned.to_dict('records')
            # Also handle any Timestamp objects that might still be in the records (defensive)
            for record in records:
                for key, value in record.items():
                    if isinstance(value, pd.Timestamp):
                        record[key] = str(value)
                    elif value is pd.NaT:
                        record[key] = None
        else:
            # Full sanitization path (for exports, full dataset requests)
            import numpy as np
            
            # Convert to records first
            records = df.to_dict('records')
            
            # Clean all values to ensure JSON compliance
            def clean_value(v):
                """Clean a value to be JSON-compliant"""
                # Handle None
                if v is None:
                    return None
                
                # Handle pandas Timestamp objects - convert to string
                if pd.api.types.is_datetime64_any_dtype(type(v)) or isinstance(v, pd.Timestamp):
                    return str(v)
                
                # Handle float values (NaN, Infinity, out-of-range)
                if isinstance(v, float):
                    # Check for NaN
                    if pd.isna(v) or np.isnan(v):
                        return None
                    # Check for Infinity
                    if np.isinf(v):
                        return None
                    # Check if float is too large for JSON (JSON max is ~1.797e308)
                    if abs(v) > 1e308:
                        return None
                
                # Handle numpy types that might cause issues
                if isinstance(v, (np.integer, np.floating)):
                    # Convert numpy types to Python native types
                    if np.isnan(v) or np.isinf(v):
                        return None
                    return v.item() if hasattr(v, 'item') else float(v)
                
                return v
            
            # Clean all values in records
            records = [{k: clean_value(v) for k, v in record.items()} for record in records]
        
        # Get metadata - handle None values to avoid comparison errors
        streams = []
        if 'Stream' in df.columns:
            stream_values = df['Stream'].unique().tolist()
            # Filter out None values before sorting
            stream_values = [s for s in stream_values if s is not None and pd.notna(s)]
            if stream_values:
                try:
                    streams = sorted(stream_values)
                except Exception as e:
                    logger.error(f"Error sorting streams: {e}, values: {stream_values[:10]}")
                    streams = stream_values  # Return unsorted if sorting fails
        
        instruments = []
        if 'Instrument' in df.columns:
            instrument_values = df['Instrument'].unique().tolist()
            # Filter out None values before sorting
            instrument_values = [i for i in instrument_values if i is not None and pd.notna(i)]
            if instrument_values:
                try:
                    instruments = sorted(instrument_values)
                except Exception as e:
                    logger.error(f"Error sorting instruments: {e}, values: {instrument_values[:10]}")
                    instruments = instrument_values  # Return unsorted if sorting fails
        
        # Years already extracted above from full dataset before limiting
        
        # Update execution timetable when loading matrix data
        # This ensures timetable_current.json reflects the latest matrix state
        try:
            # Load full dataframe (not limited) for timetable generation
            df_full = await asyncio.to_thread(_load_parquet_sync)
            
            # Update execution timetable from loaded matrix
            sys.path.insert(0, str(QTSW2_ROOT))
            from modules.timetable.timetable_engine import TimetableEngine
            
            engine = TimetableEngine()
            # Use None for trade_date to auto-detect latest date, None for stream_filters (will use defaults)
            engine.write_execution_timetable_from_master_matrix(
                df_full,
                trade_date=None,  # Auto-detect latest date
                stream_filters=None  # No stream filters available in GET request
            )
            logger.info("Execution timetable updated from loaded matrix data")
        except Exception as e:
            # Log but don't fail the data load if timetable update fails
            logger.warning(f"Failed to update execution timetable when loading matrix data: {e}")
            import traceback
            logger.debug(f"Timetable update traceback: {traceback.format_exc()}")
        
        response_data = {
            "data": records,
            "total": len(df),
            "loaded": len(records),
            "file": file_to_load.name,
            "file_mtime": file_mtime,  # When matrix was actually built/updated
            "streams": streams,
            "instruments": instruments,
            "years": years
        }
        
        # Use orjson for faster JSON serialization if available
        try:
            import orjson
            # orjson handles NaN/None automatically, but Timestamp objects need to be strings
            # We convert datetime columns to strings above, but handle any remaining Timestamp objects
            json_bytes = orjson.dumps(
                response_data,
                option=orjson.OPT_SERIALIZE_NUMPY | orjson.OPT_SERIALIZE_DATACLASS | orjson.OPT_PASSTHROUGH_DATETIME
            )
            return Response(content=json_bytes, media_type="application/json")
        except (ImportError, TypeError) as e:
            # Fallback to standard JSON serialization if orjson fails (e.g., Timestamp not handled)
            # FastAPI's JSONResponse will handle serialization via Pydantic
            logger.debug(f"orjson serialization failed, using JSONResponse fallback: {e}")
            return JSONResponse(content=response_data, media_type="application/json")
    except Exception as e:
        import traceback
        error_detail = str(e)
        error_traceback = traceback.format_exc()
        logger.error(f"Failed to load matrix data: {error_detail}")
        logger.error(f"Traceback: {error_traceback}")
        raise HTTPException(status_code=500, detail=f"Failed to load matrix data: {error_detail}")


@router.get("/test")
async def test_matrix_endpoint():
    """Test endpoint for matrix API."""
    return {
        "status": "success",
        "message": "Matrix API is working",
        "module": "modules.matrix.api"
    }

