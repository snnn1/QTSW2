"""
Pipeline Dashboard Backend
FastAPI server for pipeline monitoring and control
"""

import os
import sys
import json
import subprocess
import asyncio
import time
from pathlib import Path
from typing import Optional, Dict, List
from datetime import datetime, timedelta
from contextlib import asynccontextmanager
import uuid

from fastapi import FastAPI, WebSocket, WebSocketDisconnect, HTTPException
from fastapi.middleware.cors import CORSMiddleware
from pydantic import BaseModel
import uvicorn
import logging
import pandas as pd
import numpy as np

# Configuration
QTSW2_ROOT = Path(__file__).parent.parent.parent
SCHEDULER_SCRIPT = QTSW2_ROOT / "automation" / "daily_data_pipeline_scheduler.py"
DATA_MERGER_SCRIPT = QTSW2_ROOT / "tools" / "data_merger.py"
PARALLEL_ANALYZER_SCRIPT = QTSW2_ROOT / "tools" / "run_analyzer_parallel.py"
EVENT_LOGS_DIR = QTSW2_ROOT / "automation" / "logs" / "events"
EVENT_LOGS_DIR.mkdir(parents=True, exist_ok=True)

# Streamlit app scripts
TRANSLATOR_APP = QTSW2_ROOT / "scripts" / "translate_raw_app.py"
ANALYZER_APP = QTSW2_ROOT / "scripts" / "breakout_analyzer" / "analyzer_app" / "app.py"
SEQUENTIAL_APP = QTSW2_ROOT / "sequential_processor" / "sequential_processor_app.py"

# Master Matrix and Timetable Engine scripts
MASTER_MATRIX_SCRIPT = QTSW2_ROOT / "run_matrix_and_timetable.py"
MASTER_MATRIX_MODULE = QTSW2_ROOT / "master_matrix" / "master_matrix.py"
TIMETABLE_MODULE = QTSW2_ROOT / "timetable_engine" / "timetable_engine.py"

# Schedule config file
SCHEDULE_CONFIG_FILE = QTSW2_ROOT / "automation" / "schedule_config.json"

# Legacy scheduler removed - orchestrator handles scheduling

# Global orchestrator instance
orchestrator_instance = None

def get_orchestrator():
    """Get the global orchestrator instance"""
    return orchestrator_instance


# ============================================================
# Import Helpers (simplify repeated try/except chains)
# ============================================================

def _import_orchestrator():
    """Import orchestrator classes with fallback handling"""
    try:
        from .orchestrator import PipelineOrchestrator, OrchestratorConfig
        return PipelineOrchestrator, OrchestratorConfig
    except ImportError:
        from orchestrator import PipelineOrchestrator, OrchestratorConfig
        return PipelineOrchestrator, OrchestratorConfig


@asynccontextmanager
async def lifespan(app: FastAPI):
    """Lifespan event handler for startup and shutdown.
    
    CRITICAL: Startup must return control immediately (yield) to allow FastAPI
    to accept HTTP requests. No blocking operations, loops, or background tasks here.
    """
    global orchestrator_instance
    logger = logging.getLogger(__name__)
    logger.info("Pipeline Dashboard API starting...")
    
    # ONLY construct objects and validate config - NO starting, NO blocking
    try:
        logger.info("Creating orchestrator instance...")
        PipelineOrchestrator, OrchestratorConfig = _import_orchestrator()
        
        config = OrchestratorConfig.from_environment(qtsw2_root=QTSW2_ROOT)
        orchestrator_instance = PipelineOrchestrator(
            config=config,
            schedule_config_path=SCHEDULE_CONFIG_FILE,
            logger=logger
        )
        logger.info("Orchestrator instance created (not started)")
    except Exception as e:
        logger.error(f"Failed to create orchestrator instance: {e}", exc_info=True)
        orchestrator_instance = None
    
    # Create master matrix debug log file on startup
    master_matrix_log = QTSW2_ROOT / "logs" / "master_matrix.log"
    master_matrix_log.parent.mkdir(parents=True, exist_ok=True)
    try:
        with open(master_matrix_log, 'a', encoding='utf-8') as f:
            from datetime import datetime
            f.write(f"[{datetime.now().strftime('%Y-%m-%d %H:%M:%S')}] Backend started - master matrix debug logging ready\n")
            f.flush()
    except:
        pass
    
    # Define orchestrator startup function (runs after yield, non-blocking)
    async def start_orchestrator_background():
        """Start orchestrator after FastAPI is ready to accept requests"""
        await asyncio.sleep(0.1)  # Brief delay to ensure FastAPI is fully ready
        if orchestrator_instance:
            try:
                logger.info("Starting orchestrator (background task)...")
                await orchestrator_instance.start()
                logger.info("Orchestrator started successfully")
            except Exception as e:
                logger.error(f"Failed to start orchestrator in background: {e}", exc_info=True)
    
    # Create background task to start orchestrator (NON-BLOCKING - task is not awaited)
    # The task will run after yield when FastAPI is accepting requests
    # Store in app.state for shutdown tracking
    background_task = None
    if orchestrator_instance:
        background_task = asyncio.create_task(start_orchestrator_background())
        app.state.orchestrator_task = background_task
    
    # CRITICAL: Yield immediately - no blocking operations, no awaited tasks
    # FastAPI will not accept HTTP requests until this yield is reached
    # The background task created above will run after this yield (non-blocking)
    yield  # Application runs here - FastAPI can now accept requests
    
    # Shutdown phase: Clean up background tasks and orchestrator
    logger.info("Shutting down...")
    
    # Cancel orchestrator background task if it exists
    background_task = getattr(app.state, 'orchestrator_task', None)
    if background_task and not background_task.done():
        background_task.cancel()
        try:
            await background_task
        except asyncio.CancelledError:
            pass
    
    # Stop orchestrator (this will also stop any internal background tasks)
    if orchestrator_instance:
        try:
            logger.info("Stopping Pipeline Orchestrator...")
            await orchestrator_instance.stop()
            logger.info("Pipeline Orchestrator stopped")
        except Exception as e:
            logger.error(f"Error stopping orchestrator: {e}")
    
    # Legacy scheduler cleanup removed - no longer used
    
    logger.info("Shutdown complete")
    
    # Stop orchestrator
    if orchestrator_instance:
        try:
            logger.info("Stopping Pipeline Orchestrator...")
            await orchestrator_instance.stop()
            logger.info("Pipeline Orchestrator stopped")
        except Exception as e:
            logger.error(f"Error stopping orchestrator: {e}")
    
    # Legacy scheduler cleanup removed - no longer used
    
    logger.info("Shutdown complete")


app = FastAPI(title="Pipeline Dashboard API", lifespan=lifespan)

# Configure logging - DEBUG mode for troubleshooting
logging.getLogger("uvicorn.access").setLevel(logging.INFO)
logging.getLogger("uvicorn.error").setLevel(logging.DEBUG)
logging.getLogger("dashboard").setLevel(logging.DEBUG)
logging.getLogger("orchestrator").setLevel(logging.DEBUG)

# Configure logging to also write to log file
LOG_FILE_PATH = QTSW2_ROOT / "logs" / "backend_debug.log"
LOG_FILE_PATH.parent.mkdir(parents=True, exist_ok=True)

# Create a file handler for the log file
file_handler = logging.FileHandler(LOG_FILE_PATH, mode='a', encoding='utf-8')
file_handler.setLevel(logging.DEBUG)  # Changed to DEBUG to catch more
file_formatter = logging.Formatter('%(asctime)s - %(name)s - %(levelname)s - %(message)s')
file_handler.setFormatter(file_formatter)

# Add file handler to root logger
root_logger = logging.getLogger()
# Don't add if already added (reload issue)
if not any(isinstance(h, logging.FileHandler) and h.baseFilename == str(LOG_FILE_PATH) for h in root_logger.handlers):
    root_logger.addHandler(file_handler)
root_logger.setLevel(logging.DEBUG)  # Changed to DEBUG

# Startup message written to log file only


# CORS middleware for React frontend
app.add_middleware(
    CORSMiddleware,
    allow_origins=["http://localhost:5173", "http://localhost:5174", "http://localhost:3000", "http://192.168.1.171:5174"],  # Vite default ports + network
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
)

# Import and include routers
def _import_routers():
    """Import routers with fallback handling"""
    try:
        from .routers import pipeline, schedule, websocket, apps, metrics, matrix
        return pipeline, schedule, websocket, apps, metrics, matrix
    except (ImportError, ValueError):
        # Fallback: Add parent directory to path and import
        import sys
        from pathlib import Path
        backend_path = Path(__file__).parent
        if str(backend_path) not in sys.path:
            sys.path.insert(0, str(backend_path))
        dashboard_path = backend_path.parent
        if str(dashboard_path) not in sys.path:
            sys.path.insert(0, str(dashboard_path))
        try:
            from routers import pipeline, schedule, websocket, apps, metrics, matrix
            return pipeline, schedule, websocket, apps, metrics, matrix
        except ImportError:
            from dashboard.backend.routers import pipeline, schedule, websocket, apps, metrics, matrix
            return pipeline, schedule, websocket, apps, metrics, matrix

pipeline, schedule, websocket, apps, metrics, matrix = _import_routers()

app.include_router(pipeline.router)
app.include_router(schedule.router)
app.include_router(websocket.router)
app.include_router(apps.router)
app.include_router(metrics.router)
app.include_router(matrix.router)

# Legacy scheduler process tracking removed


# ============================================================
# Data Models
# ============================================================
# Note: Most models are in models.py - only Matrix-specific models here


# ============================================================
# API Endpoints
# ============================================================
# Note: Schedule management endpoints are in routers/schedule.py
# Note: Pipeline endpoints are in routers/pipeline.py

@app.get("/")
async def root():
    return {"message": "Pipeline Dashboard API", "status": "running", "test_endpoint": "/api/test-debug-log"}

@app.get("/health")
async def health_check():
    """Health check endpoint for monitoring"""
    return {
        "status": "healthy",
        "timestamp": datetime.now().isoformat(),
        "service": "Pipeline Dashboard API"
    }

@app.get("/api/matrix/test")
async def test_matrix_endpoint():
    """Simple test endpoint to verify frontend can reach backend"""
    logger = logging.getLogger(__name__)
    logger.info("TEST ENDPOINT HIT - Frontend can reach backend!")
    return {"status": "success", "message": "Backend is reachable from frontend"}


@app.get("/api/test-debug-log")
async def test_debug_log():
    """Test endpoint to verify debug logging works"""
    import os
    from datetime import datetime
    master_matrix_log = QTSW2_ROOT / "logs" / "master_matrix.log"
    master_matrix_log.parent.mkdir(parents=True, exist_ok=True)
    
    timestamp = datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    test_msg = f"[{timestamp}] TEST: Debug log endpoint works!\n"
    
    try:
        with open(master_matrix_log, 'a', encoding='utf-8') as f:
            f.write(test_msg)
            f.flush()
            os.fsync(f.fileno())
        return {"status": "success", "message": "Debug log written successfully", "file": str(master_matrix_log)}
    except Exception as e:
        return {"status": "error", "message": str(e), "file": str(master_matrix_log)}


# Schedule and pipeline endpoints moved to routers - see routers/schedule.py and routers/pipeline.py


@app.post("/api/merger/run")
async def run_data_merger():
    """
    Run the data merger/consolidator to merge daily files into monthly files.
    """
    try:
        # Launch data merger as separate process
        # CRITICAL: Do not use PIPE - redirect to files to avoid deadlocks
        cmd = ["python", str(DATA_MERGER_SCRIPT)]
        
        # Create log files for output
        log_dir = QTSW2_ROOT / "logs"
        log_dir.mkdir(parents=True, exist_ok=True)
        stdout_log = log_dir / "merger_stdout.log"
        stderr_log = log_dir / "merger_stderr.log"
        
        with open(stdout_log, 'w', encoding='utf-8') as stdout_file, \
             open(stderr_log, 'w', encoding='utf-8') as stderr_file:
            process = subprocess.Popen(
                cmd,
                cwd=str(QTSW2_ROOT),
                stdout=stdout_file,
                stderr=stderr_file,
                text=True
            )
            
            # Wait for process to complete (with timeout)
            try:
                return_code = process.wait(timeout=300)  # 5 minute timeout
                
                # Read output from log files
                stdout_file.seek(0)
                stderr_file.seek(0)
                stdout_content = stdout_file.read()
                stderr_content = stderr_file.read()
                
                if return_code == 0:
                    return {
                        "status": "success",
                        "message": "Data merger completed successfully",
                        "output": stdout_content[-1000:] if stdout_content else ""  # Last 1000 chars
                    }
                else:
                    return {
                        "status": "error",
                        "message": f"Data merger failed with return code {return_code}",
                        "output": stderr_content[-1000:] if stderr_content else stdout_content[-1000:] if stdout_content else ""
                    }
            except subprocess.TimeoutExpired:
                process.kill()
                return {
                    "status": "timeout",
                    "message": "Data merger timed out after 5 minutes",
                    "output": ""
                }
            
    except Exception as e:
        raise HTTPException(status_code=500, detail=f"Failed to run data merger: {str(e)}")


# Pipeline status endpoint moved to routers/pipeline.py


# ============================================================
# WebSocket Event Streaming
# ============================================================
# NOTE: WebSocket endpoints are handled by routers/websocket.py using EventBus
# All file-tailing code has been removed to prevent connection leaks


# File count and app launcher endpoints moved to routers - see routers/metrics.py and routers/apps.py


# ============================================================
# Master Matrix & Timetable Engine API
# ============================================================

class StreamFilterConfig(BaseModel):
    exclude_days_of_week: List[str] = []  # e.g., ["Wednesday", "Friday"]
    exclude_days_of_month: List[int] = []  # e.g., [4, 16, 30]
    exclude_times: List[str] = []  # e.g., ["07:30", "08:00"]


class MatrixBuildRequest(BaseModel):
    start_date: Optional[str] = None
    end_date: Optional[str] = None
    specific_date: Optional[str] = None
    analyzer_runs_dir: str = "data/analyzer_runs"
    output_dir: str = "data/master_matrix"
    stream_filters: Optional[Dict[str, StreamFilterConfig]] = None
    streams: Optional[List[str]] = None  # If provided, only rebuild these streams


class TimetableRequest(BaseModel):
    date: Optional[str] = None
    scf_threshold: float = 0.5
    analyzer_runs_dir: str = "data/analyzer_runs"
    output_dir: str = "data/timetable"


@app.post("/api/matrix/build")
async def build_master_matrix(request: MatrixBuildRequest):
    """Build master matrix from all streams."""
    import os
    import sys
    from datetime import datetime
    
    # Get logger first
    logger = logging.getLogger(__name__)
    logger.info("=" * 80)
    logger.info("BUILD ENDPOINT HIT!")
    logger.info("=" * 80)
    
    # Write directly to master_matrix.log FIRST (before module reload) - FORCE IT
    master_matrix_log = QTSW2_ROOT / "logs" / "master_matrix.log"
    from datetime import datetime
    timestamp = datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    try:
        master_matrix_log.parent.mkdir(parents=True, exist_ok=True)
        # Open in append mode, write immediately, force flush
        f = open(master_matrix_log, 'a', encoding='utf-8')
        f.write(f"{timestamp} - INFO - {'=' * 80}\n")
        f.write(f"{timestamp} - INFO - BUILD ENDPOINT HIT!\n")
        f.write(f"{timestamp} - INFO - {'=' * 80}\n")
        f.flush()
        import os
        if hasattr(f, 'fileno'):
            try:
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
    
    # Import MasterMatrix (simplified - standard import, no complex reloading)
    try:
        import inspect
        sys.path.insert(0, str(QTSW2_ROOT))
        from master_matrix.master_matrix import MasterMatrix
        
        # Verify the signature
        sig = inspect.signature(MasterMatrix.__init__)
        params = list(sig.parameters.keys())
        logger.info(f"MasterMatrix signature: {params}")
    except Exception as e:
        logger.error(f"ERROR importing MasterMatrix module: {e}")
        raise HTTPException(status_code=500, detail=f"Failed to load MasterMatrix module: {e}")
    
    # Write to master_matrix.log using the master_matrix logger (after successful reload)
    master_matrix_logger = logging.getLogger('master_matrix.master_matrix')
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
        # Initialize MasterMatrix - verify we have the latest version
        logger.info("About to create MasterMatrix instance")
        
        # Only pass parameters that exist in the signature - explicitly check signature first
        import inspect
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
        
        # Explicitly exclude sequencer_runs_dir even if it exists in signature
        if "sequencer_runs_dir" in init_kwargs:
            del init_kwargs["sequencer_runs_dir"]
        
        logger.info(f"Creating MasterMatrix with kwargs: {init_kwargs}")
        logger.info(f"Valid parameters from signature: {valid_params}")
        
        # Double-check we're not passing sequencer_runs_dir
        if "sequencer_runs_dir" in init_kwargs:
            logger.error(f"ERROR: sequencer_runs_dir is in init_kwargs! Removing it...")
            del init_kwargs["sequencer_runs_dir"]
        
        try:
            matrix = MasterMatrix(**init_kwargs)
            logger.info("MasterMatrix created successfully")
        except TypeError as e:
            error_str = str(e)
            logger.error(f"TypeError creating MasterMatrix: {error_str}")
            logger.error(f"init_kwargs passed: {init_kwargs}")
            logger.error(f"Valid params: {valid_params}")
            
            # If the error mentions sequencer_runs_dir but we didn't pass it, the module is cached
            if "sequencer_runs_dir" in error_str and "sequencer_runs_dir" not in init_kwargs:
                raise HTTPException(
                    status_code=500, 
                    detail=f"{error_str} - The backend is using a cached version of MasterMatrix. Please restart the backend completely."
                )
            else:
                raise HTTPException(status_code=500, detail=error_str)
        
        print("Calling build_master_matrix...", file=sys.stderr)
        sys.stderr.flush()
        logger.info("Calling build_master_matrix...")
        
        # Force immediate output
        import sys
        print(f"[DEBUG] About to call build_master_matrix with filters: {stream_filters_dict}", file=sys.stderr)
        sys.stderr.flush()
        with open(LOG_FILE_PATH, 'a', encoding='utf-8') as f:
            f.write(f"[DEBUG] About to call build_master_matrix with filters: {stream_filters_dict}\n")
            f.flush()
        
        # Convert analyzer_runs_dir to absolute path for build_master_matrix call
        analyzer_runs_path = Path(request.analyzer_runs_dir)
        if not analyzer_runs_path.is_absolute():
            analyzer_runs_path = QTSW2_ROOT / analyzer_runs_path
        
        # Run heavy matrix build in thread pool to avoid blocking FastAPI event loop
        # This keeps matrix operations separate from pipeline but non-blocking
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


@app.post("/api/timetable/generate")
async def generate_timetable(request: TimetableRequest):
    """Generate timetable for a trading day."""
    try:
        import sys
        sys.path.insert(0, str(QTSW2_ROOT))
        from timetable_engine.timetable_engine import TimetableEngine
        
        engine = TimetableEngine(
            master_matrix_dir="data/master_matrix",
            analyzer_runs_dir=request.analyzer_runs_dir
        )
        engine.scf_threshold = request.scf_threshold
        
        # Run heavy timetable generation in thread pool to avoid blocking FastAPI
        def _generate_timetable_sync():
            """Synchronous timetable generation to run in thread"""
            return engine.generate_timetable(trade_date=request.date)
        
        timetable_df = await asyncio.to_thread(_generate_timetable_sync)
        
        if timetable_df.empty:
            return {
                "status": "success",
                "message": "Timetable generated but is empty",
                "entries": [],
                "allowed": 0
            }
        
        # Save timetable
        parquet_file, json_file = engine.save_timetable(timetable_df, request.output_dir)
        
        # Convert DataFrame to list of dicts for JSON response
        entries = timetable_df.to_dict('records')
        
        return {
            "status": "success",
            "message": "Timetable generated successfully",
            "date": timetable_df['trade_date'].iloc[0] if not timetable_df.empty else None,
            "entries": entries,
            "total_entries": len(timetable_df),
            "allowed_trades": int(timetable_df['allowed'].sum()),
            "files": {
                "parquet": str(parquet_file),
                "json": str(json_file)
            }
        }
    except Exception as e:
        raise HTTPException(status_code=500, detail=f"Failed to generate timetable: {str(e)}")


@app.get("/api/matrix/files")
async def list_matrix_files():
    """List available master matrix files."""
    try:
        # Check both possible locations (where files are actually saved vs. where they might be)
        # Backend script is in dashboard/backend/, so resolve relative to that
        backend_dir = Path(__file__).parent  # dashboard/backend/
        backend_matrix_dir = backend_dir / "data" / "master_matrix"  # dashboard/backend/data/master_matrix/
        root_matrix_dir = QTSW2_ROOT / "data" / "master_matrix"  # QTSW2_ROOT/data/master_matrix/
        
        # Collect files from both locations
        parquet_files = []
        if backend_matrix_dir.exists():
            parquet_files.extend(backend_matrix_dir.glob("master_matrix_*.parquet"))
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


@app.get("/api/matrix/data")
async def get_matrix_data(file_path: Optional[str] = None, limit: int = 50000):
    """Get master matrix data from the most recent file or specified file."""
    logger = logging.getLogger(__name__)
    try:
        import pandas as pd
        
        # Check both possible locations (where files are actually saved vs. where they might be)
        # Files are saved to relative path "data/master_matrix" from backend's working directory
        # Backend script is in dashboard/backend/, so resolve relative to that
        backend_dir = Path(__file__).parent  # dashboard/backend/
        backend_matrix_dir = backend_dir / "data" / "master_matrix"  # dashboard/backend/data/master_matrix/
        root_matrix_dir = QTSW2_ROOT / "data" / "master_matrix"  # QTSW2_ROOT/data/master_matrix/
        
        # Collect files from both locations
        parquet_files = []
        if backend_matrix_dir.exists():
            parquet_files.extend(backend_matrix_dir.glob("master_matrix_*.parquet"))
        if root_matrix_dir.exists():
            parquet_files.extend(root_matrix_dir.glob("master_matrix_*.parquet"))
        
        if file_path:
            # Use specified file
            file_to_load = Path(file_path)
            if not file_to_load.exists():
                return {"data": [], "total": 0, "file": None, "error": f"File not found: {file_path}"}
        else:
            # Find most recent file from all collected files
            if parquet_files:
                # Sort all files by modification time and get most recent
                parquet_files = sorted(parquet_files, key=lambda p: p.stat().st_mtime, reverse=True)
                file_to_load = parquet_files[0]
            else:
                return {"data": [], "total": 0, "file": None, "error": f"No master matrix files found. Checked: {backend_matrix_dir} and {root_matrix_dir}. Build the matrix first."}
        
        # Load parquet file in thread pool to avoid blocking (large files)
        def _load_parquet_sync():
            """Synchronous parquet load to run in thread"""
            return pd.read_parquet(file_to_load)
        
        try:
            df = await asyncio.to_thread(_load_parquet_sync)
        except Exception as e:
            return {"data": [], "total": 0, "file": file_to_load.name, "error": f"Failed to read file: {str(e)}"}
        
        if df.empty:
            return {"data": [], "total": 0, "file": file_to_load.name, "error": "File is empty"}
        
        # Extract available years from source analyzer_runs data
        # Files are named like: ES1_an_2017_01.parquet, so extract year from filename
        available_years = []
        try:
            logger.info("=" * 80)
            logger.info("YEAR EXTRACTION DEBUG START")
            logger.info("=" * 80)
            
            analyzer_runs_dir = QTSW2_ROOT / "data" / "analyzer_runs"
            logger.info(f"Checking analyzer_runs_dir: {analyzer_runs_dir}")
            logger.info(f"Directory exists: {analyzer_runs_dir.exists()}")
            
            if analyzer_runs_dir.exists():
                import re
                parquet_files = list(analyzer_runs_dir.rglob("*.parquet"))
                logger.info(f"Found {len(parquet_files)} parquet files")
                
                all_years = set()
                sample_files = []
                
                # Extract years from filenames (format: STREAM_an_YYYY_MM.parquet)
                for parquet_file in parquet_files:
                    # Look for 4-digit year in filename
                    match = re.search(r'_(\d{4})_', parquet_file.name)
                    if match:
                        year = int(match.group(1))
                        if 2000 <= year <= 2100:  # Sanity check
                            all_years.add(year)
                            if len(sample_files) < 5:
                                sample_files.append((parquet_file.name, year))
                
                logger.info(f"Sample files checked: {sample_files}")
                logger.info(f"Unique years found: {sorted(all_years)}")
                
                if all_years:
                    available_years = sorted(list(all_years))
                    logger.info(f"✓ Found {len(available_years)} years from filenames: {available_years}")
                else:
                    logger.warning("✗ No years found in filenames!")
            
            # If no years found from source, fallback to matrix data
            if not available_years:
                logger.info("Falling back to matrix data for years...")
                if 'trade_date' in df.columns:
                    logger.info("Using trade_date column")
                    dates = pd.to_datetime(df['trade_date'], errors='coerce')
                    years = dates.dt.year.dropna().unique()
                    available_years = sorted([int(y) for y in years if pd.notna(y)])
                    logger.info(f"Years from trade_date: {available_years}")
                elif 'Date' in df.columns:
                    logger.info("Using Date column")
                    dates = pd.to_datetime(df['Date'], errors='coerce')
                    years = dates.dt.year.dropna().unique()
                    available_years = sorted([int(y) for y in years if pd.notna(y)])
                    logger.info(f"Years from Date: {available_years}")
                else:
                    logger.warning("No Date or trade_date column in dataframe!")
            
            logger.info(f"FINAL available_years (from source files): {available_years}")
            logger.info("=" * 80)
            logger.info("YEAR EXTRACTION DEBUG END")
            logger.info("=" * 80)
            
            # Also add years from the actual data to ensure completeness
            if 'trade_date' in df.columns:
                dates = pd.to_datetime(df['trade_date'], errors='coerce')
                years_from_data = sorted([int(y) for y in dates.dt.year.dropna().unique() if pd.notna(y)])
                if years_from_data:
                    # Merge with available_years to ensure all years are included
                    all_years_set = set(available_years) | set(years_from_data)
                    available_years = sorted(list(all_years_set))
                    logger.info(f"Combined years (source files + data): {available_years}")
            elif 'Date' in df.columns:
                dates = pd.to_datetime(df['Date'], errors='coerce')
                years_from_data = sorted([int(y) for y in dates.dt.year.dropna().unique() if pd.notna(y)])
                if years_from_data:
                    # Merge with available_years to ensure all years are included
                    all_years_set = set(available_years) | set(years_from_data)
                    available_years = sorted(list(all_years_set))
                    logger.info(f"Combined years (source files + data): {available_years}")
        except Exception as e:
            logger.error(f"Error extracting years: {e}")
            import traceback
            logger.error(traceback.format_exc())
            available_years = []
        
        # Extract years from the actual data being returned (to verify all years are included)
        years_in_data = []
        if 'trade_date' in df.columns:
            dates = pd.to_datetime(df['trade_date'], errors='coerce')
            years_in_data = sorted([int(y) for y in dates.dt.year.dropna().unique() if pd.notna(y)])
        elif 'Date' in df.columns:
            dates = pd.to_datetime(df['Date'], errors='coerce')
            years_in_data = sorted([int(y) for y in dates.dt.year.dropna().unique() if pd.notna(y)])
        
        logger.info(f"Years in loaded data: {years_in_data}")
        logger.info(f"Total records in file: {len(df)}")
        logger.info(f"Returning all data (up to limit {limit}) - includes all years: {years_in_data}")
        
        # Convert to records (limit rows AFTER extracting years)
        # NOTE: This returns ALL data from the file - no year filtering applied
        records = df.head(limit).to_dict('records')
        
        # Convert dates and other types to strings for JSON
        for record in records:
            for key, value in record.items():
                # Check for NaN/NaT first (before type checks)
                if pd.isna(value) or str(type(value)) == "<class 'pandas._libs.tslibs.nattype.NaTType'>":
                    record[key] = None
                elif isinstance(value, (pd.Timestamp, datetime)):
                    if hasattr(value, 'isoformat'):
                        record[key] = value.isoformat()
                    else:
                        record[key] = str(value)
                elif isinstance(value, (np.integer, np.floating)):
                    # Convert numpy types to Python types (preserve 0 values)
                    converted = float(value) if isinstance(value, np.floating) else int(value)
                    record[key] = converted
                elif isinstance(value, (int, float)):
                    # Python int/float - keep as is (including 0)
                    record[key] = value
        
        response = {
            "data": records,
            "total": len(df),
            "file": file_to_load.name,
            "loaded": len(records),
            "date_range": {
                "start": str(df['trade_date'].min()) if 'trade_date' in df.columns and not df['trade_date'].empty else None,
                "end": str(df['trade_date'].max()) if 'trade_date' in df.columns and not df['trade_date'].empty else None
            },
            "streams": sorted(df['Stream'].unique().tolist()) if 'Stream' in df.columns and not df['Stream'].empty else [],
            "instruments": sorted(df['Instrument'].unique().tolist()) if 'Instrument' in df.columns and not df['Instrument'].empty else [],
            "years": available_years,
            "allowed_trades": int(df['final_allowed'].sum()) if 'final_allowed' in df.columns and not df['final_allowed'].empty else 0
        }
        
        logger.info("=" * 80)
        logger.info("API RESPONSE DEBUG")
        logger.info(f"Years in response: {response['years']}")
        logger.info(f"Number of years: {len(response['years'])}")
        logger.info(f"Years type: {type(response['years'])}")
        logger.info("=" * 80)
        
        return response
    except Exception as e:
        import traceback
        error_detail = f"Failed to load matrix data: {str(e)}\n{traceback.format_exc()}"
        raise HTTPException(status_code=500, detail=error_detail)


@app.get("/api/timetable/files")
async def list_timetable_files():
    """List available timetable files."""
    try:
        timetable_dir = QTSW2_ROOT / "data" / "timetable"
        if not timetable_dir.exists():
            return {"files": []}
        
        files = []
        for file_path in sorted(timetable_dir.glob("*.parquet"), key=lambda p: p.stat().st_mtime, reverse=True):
            files.append({
                "name": file_path.name,
                "path": str(file_path),
                "size": file_path.stat().st_size,
                "modified": datetime.fromtimestamp(file_path.stat().st_mtime).isoformat()
            })
        
        return {"files": files[:20]}  # Return last 20 files
    except Exception as e:
        raise HTTPException(status_code=500, detail=f"Failed to list timetable files: {str(e)}")


if __name__ == "__main__":
    # Force immediate output to stderr and log file
    import sys
    uvicorn_msg = "=" * 80 + "\nStarting uvicorn server...\n" + "=" * 80 + "\n"
    print(uvicorn_msg, file=sys.stderr)
    sys.stderr.flush()
    sys.stdout.flush()
    # Also write directly to log file
    with open(LOG_FILE_PATH, 'a', encoding='utf-8') as f:
        f.write(uvicorn_msg)
        f.flush()
    
    # Set log level to info so we can see SL calculation logs
    uvicorn.run(app, host="0.0.0.0", port=8000, log_level="info")

