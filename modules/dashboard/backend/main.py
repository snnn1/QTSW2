"""
Pipeline Dashboard Backend - FastAPI Integration Layer

[ARCHITECTURE] NOTE: This is a backend integration layer / control plane, not a domain module.

This file serves as the FastAPI application entry point and integration hub, combining:
- Backend bootstrap and orchestrator wiring
- Debug endpoints for troubleshooting
- Matrix execution endpoints
- Timetable execution endpoints
- Streamlit application launchers
- Legacy schedule metadata endpoints (schedule.json)

This is intentionally NOT "clean architecture" - it's a control plane that wires together
various subsystems. This is acceptable and appropriate for an integration layer.

Do NOT refactor this into multiple domain modules unless there's a clear need.
The current structure is fine for a control plane / integration layer.

Domain logic belongs in:
- orchestrator/ (pipeline coordination)
- routers/ (API endpoint handlers)
- modules/ (core business logic)
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

from fastapi import FastAPI, HTTPException
# WebSocket imports removed - WebSocket endpoints are in routers/websocket.py
from fastapi.middleware.cors import CORSMiddleware
from fastapi.middleware.gzip import GZipMiddleware
from pydantic import BaseModel
import uvicorn
import logging
import pandas as pd
import numpy as np

# Configuration
# Calculate project root: modules/dashboard/backend/main.py -> QTSW2 root (go up 3 levels)
QTSW2_ROOT = Path(__file__).parent.parent.parent.parent
# Legacy scheduler removed - use automation/run_pipeline_standalone.py instead
DATA_MERGER_SCRIPT = QTSW2_ROOT / "modules" / "merger" / "merger.py"
PARALLEL_ANALYZER_SCRIPT = QTSW2_ROOT / "tools" / "run_analyzer_parallel.py"
EVENT_LOGS_DIR = QTSW2_ROOT / "automation" / "logs" / "events"
EVENT_LOGS_DIR.mkdir(parents=True, exist_ok=True)


# Streamlit app scripts
TRANSLATOR_APP = QTSW2_ROOT / "scripts" / "translate_raw_app.py"
ANALYZER_APP = QTSW2_ROOT / "scripts" / "breakout_analyzer" / "analyzer_app" / "app.py"
SEQUENTIAL_APP = QTSW2_ROOT / "sequential_processor" / "sequential_processor_app.py"

# Master Matrix and Timetable Engine scripts
MASTER_MATRIX_SCRIPT = QTSW2_ROOT / "scripts" / "maintenance" / "run_matrix_and_timetable.py"
MASTER_MATRIX_MODULE = QTSW2_ROOT / "modules" / "matrix" / "master_matrix.py"
TIMETABLE_MODULE = QTSW2_ROOT / "modules" / "timetable" / "timetable_engine.py"

# Schedule config file
SCHEDULE_CONFIG_FILE = QTSW2_ROOT / "configs" / "schedule.json"

# Legacy scheduler process removed - Windows Task Scheduler runs pipeline directly

# Global orchestrator instance
orchestrator_instance = None

# File counts cache (for fast response times)
_file_counts_cache = {
    "raw_files": 0,
    "translated_files": 0,
    "analyzed_files": 0,
    "computed_at": None,
    "last_duration_ms": 0,
    "refresh_task": None,
}
_file_counts_cache_ttl_seconds = 30  # Refresh cache if older than 30 seconds


@asynccontextmanager
async def lifespan(app: FastAPI):
    """Lifespan event handler for startup and shutdown."""
    global orchestrator_instance
    logger = logging.getLogger(__name__)
    logger.info("Pipeline Dashboard API started")
    
    # Initialize orchestrator
    print("\n" + "=" * 60)
    print("INITIALIZING ORCHESTRATOR")
    print("=" * 60)
    try:
        print("Step 1: Importing orchestrator...")
        logger.info("Initializing Pipeline Orchestrator...")
        try:
            from modules.orchestrator import PipelineOrchestrator, OrchestratorConfig
        except ImportError:
            from orchestrator import PipelineOrchestrator, OrchestratorConfig
        print("   [OK] Imported")
        
        print("Step 2: Creating config...")
        logger.info("Creating orchestrator config...")
        config = OrchestratorConfig.from_environment(qtsw2_root=QTSW2_ROOT)
        logger.info(f"Config created: event_logs_dir={config.event_logs_dir}")
        print(f"   [OK] Config created: {config.event_logs_dir}")
        
        print("Step 3: Creating orchestrator instance...")
        logger.info("Creating orchestrator instance...")
        orchestrator_instance = PipelineOrchestrator(
            config=config,
            schedule_config_path=SCHEDULE_CONFIG_FILE,
            logger=logger
        )
        logger.info("Orchestrator instance created")
        print("   [OK] Instance created")
        
        print("Step 4: Starting orchestrator...")
        logger.info("Starting orchestrator (scheduler and watchdog)...")
        await orchestrator_instance.start()
        logger.info("Pipeline Orchestrator started successfully")
        print("   [OK] Orchestrator started successfully!")
        print("=" * 60 + "\n")

        # Warm snapshot cache in background so WebSocket snapshots are instant
        try:
            orchestrator_instance.event_bus.start_snapshot_warmer(
                hours=4.0,
                max_events=100,
                exclude_verbose=True,
                ttl_seconds=15,
                interval_seconds=15,
            )
            logger.info("Snapshot cache warmer started (4h window, 100 events, 15s interval)")
        except Exception as warm_err:
            logger.warning(f"Failed to start snapshot cache warmer: {warm_err}")
    except Exception as e:
        import traceback
        error_msg = f"Failed to start orchestrator: {e}\nFull traceback:\n{traceback.format_exc()}"
        logger.error(error_msg, exc_info=True)
        # Print to console for immediate visibility
        print("\n" + "=" * 60)
        print("[ERROR] Orchestrator failed to start!")
        print("=" * 60)
        print(f"Error type: {type(e).__name__}")
        print(f"Error message: {str(e)}")
        print("\nFull traceback:")
        traceback.print_exc()
        print("=" * 60 + "\n")
        orchestrator_instance = None
    
    # Legacy scheduler removed - Windows Task Scheduler runs pipeline directly via automation/run_pipeline_standalone.py
    
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
    
    # Initialize file counts cache on startup (non-blocking background task)
    asyncio.create_task(_refresh_file_counts_cache())
    
    yield  # Application runs here
    
    # Shutdown
    logger = logging.getLogger(__name__)
    
    # Stop orchestrator
    if orchestrator_instance:
        try:
            logger.info("Stopping Pipeline Orchestrator...")
            await orchestrator_instance.stop()
            logger.info("Pipeline Orchestrator stopped")
        except Exception as e:
            logger.error(f"Error stopping orchestrator: {e}")
    
    # Legacy scheduler removed - Windows Task Scheduler runs pipeline directly


app = FastAPI(title="Pipeline Dashboard API", lifespan=lifespan)

# Configure logging - DEBUG mode for troubleshooting
logging.getLogger("uvicorn.access").setLevel(logging.INFO)
logging.getLogger("uvicorn.error").setLevel(logging.DEBUG)
logging.getLogger("dashboard").setLevel(logging.DEBUG)
logging.getLogger("orchestrator").setLevel(logging.DEBUG)

# Configure logging to write to both console and log file
LOG_FILE_PATH = QTSW2_ROOT / "logs" / "backend_debug.log"
LOG_FILE_PATH.parent.mkdir(parents=True, exist_ok=True)

# Create a file handler for the log file
file_handler = logging.FileHandler(LOG_FILE_PATH, mode='a', encoding='utf-8')
file_handler.setLevel(logging.DEBUG)
file_formatter = logging.Formatter('%(asctime)s - %(name)s - %(levelname)s - %(message)s')
file_handler.setFormatter(file_formatter)

# Create a console handler for immediate visibility
console_handler = logging.StreamHandler(sys.stderr)
console_handler.setLevel(logging.DEBUG)
console_formatter = logging.Formatter('%(asctime)s - %(name)s - %(levelname)s - %(message)s')
console_handler.setFormatter(console_formatter)

# Add handlers to root logger
root_logger = logging.getLogger()
# Don't add if already added (reload issue)
if not any(isinstance(h, logging.FileHandler) and h.baseFilename == str(LOG_FILE_PATH) for h in root_logger.handlers):
    root_logger.addHandler(file_handler)
# Add console handler if not already present
if not any(isinstance(h, logging.StreamHandler) for h in root_logger.handlers):
    root_logger.addHandler(console_handler)
root_logger.setLevel(logging.DEBUG)

# Startup message written to log file only


# CORS middleware for React frontend
app.add_middleware(
    CORSMiddleware,
    allow_origins=["http://localhost:5173", "http://localhost:5174", "http://localhost:3000", "http://192.168.1.171:5174"],  # Vite default ports + network
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
)

# Gzip compression middleware for faster data transfer
app.add_middleware(GZipMiddleware, minimum_size=1000)  # Compress responses > 1KB

# Import and include routers
# Handle both module import (dashboard.backend.main) and direct script execution
try:
    from .routers import pipeline, schedule, websocket, apps, metrics
except (ImportError, ValueError):
    # Fallback: Add parent directory to path and import
    import sys
    from pathlib import Path
    # Add dashboard/backend to path so routers can import
    backend_path = Path(__file__).parent
    if str(backend_path) not in sys.path:
        sys.path.insert(0, str(backend_path))
    # Also add modules to path for orchestrator imports
    dashboard_path = backend_path.parent
    if str(dashboard_path) not in sys.path:
        sys.path.insert(0, str(dashboard_path))
    try:
        from routers import pipeline, schedule, websocket, apps, metrics
    except ImportError as e:
        # Last resort: try absolute import
        from dashboard.backend.routers import pipeline, schedule, websocket, apps, metrics

# Import matrix API router from matrix module
try:
    from modules.matrix.api import router as matrix_router
except ImportError:
    # Fallback if import fails
    import sys
    sys.path.insert(0, str(QTSW2_ROOT))
    from modules.matrix.api import router as matrix_router

app.include_router(pipeline.router)
app.include_router(schedule.router)
app.include_router(websocket.router)
app.include_router(apps.router)
app.include_router(metrics.router)
app.include_router(matrix_router)  # Matrix API from modules.matrix.api

# Legacy scheduler removed - no process to set


# ============================================================
# Data Models
# ============================================================

class ScheduleConfig(BaseModel):
    schedule_time: str  # HH:MM format


class PipelineStartResponse(BaseModel):
    run_id: str
    event_log_path: str
    status: str


class PipelineStartRequest(BaseModel):
    wait_for_export: bool = False
    launch_ninjatrader: bool = False


# ============================================================
# Schedule Management
# ============================================================

def load_schedule_config() -> ScheduleConfig:
    """Load schedule configuration from file."""
    if SCHEDULE_CONFIG_FILE.exists():
        with open(SCHEDULE_CONFIG_FILE, "r") as f:
            data = json.load(f)
            return ScheduleConfig(**data)
    return ScheduleConfig(schedule_time="07:30")


def save_schedule_config(config: ScheduleConfig):
    """Save schedule configuration to file."""
    with open(SCHEDULE_CONFIG_FILE, "w") as f:
        json.dump(config.dict(), f, indent=2)


# ============================================================
# API Endpoints
# ============================================================

@app.get("/")
async def root():
    return {"message": "Pipeline Dashboard API", "status": "running", "test_endpoint": "/api/test-debug-log"}

@app.get("/health")
async def health_check():
    """Health check endpoint for monitoring"""
    # Use DEBUG level to reduce log noise (health checks are frequent)
    logger = logging.getLogger(__name__)
    logger.debug("Health check requested")
    return {
        "status": "healthy",
        "timestamp": datetime.now().isoformat(),
        "service": "Pipeline Dashboard API"
    }

@app.get("/api/debug/connection")
async def debug_connection():
    """Debug endpoint to test connection and CORS"""
    return {
        "status": "connected",
        "timestamp": datetime.now().isoformat(),
        "backend": "running",
        "cors": "configured",
        "endpoints": {
            "health": "/health",
            "pipeline_status": "/api/pipeline/status",
            "scheduler_status": "/api/scheduler/status"
        }
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


@app.get("/api/schedule", response_model=ScheduleConfig)
async def get_schedule():
    """Get current scheduled daily run time."""
    return load_schedule_config()


@app.post("/api/schedule", response_model=ScheduleConfig)
async def update_schedule(config: ScheduleConfig):
    """Update scheduled daily run time."""
    logger = logging.getLogger(__name__)
    # Validate time format
    try:
        datetime.strptime(config.schedule_time, "%H:%M")
    except ValueError:
        logger.warning(f"Invalid schedule time format received: {config.schedule_time}")
        raise HTTPException(
            status_code=400,
            detail="Invalid time format. Please use HH:MM format (24-hour, e.g., '07:30')"
        )
    
    # Validate time range (00:00 to 23:59)
    try:
        hour, minute = map(int, config.schedule_time.split(':'))
        if hour < 0 or hour > 23 or minute < 0 or minute > 59:
            raise ValueError("Time out of range")
    except (ValueError, AttributeError):
        logger.warning(f"Invalid schedule time values: {config.schedule_time}")
        raise HTTPException(
            status_code=400,
            detail="Invalid time. Hours must be 0-23, minutes must be 0-59."
        )
    
    try:
        save_schedule_config(config)
        logger.info(f"Schedule updated to: {config.schedule_time}")
        return config
    except Exception as e:
        logger.error(f"Failed to save schedule config: {e}", exc_info=True)
        raise HTTPException(
            status_code=500,
            detail="Failed to save schedule. Please check logs for details."
        )


# Legacy scheduler endpoints removed - use routers/schedule.py for Windows Task Scheduler control


@app.get("/api/schedule/next")
async def get_next_scheduled_run():
    """Get next scheduled run time (runs every 15 minutes)."""
    try:
        import pytz
        chicago_tz = pytz.timezone("America/Chicago")
        now_chicago = datetime.now(chicago_tz)
        
        # Calculate next 15-minute interval (:00, :15, :30, :45)
        current_minute = now_chicago.minute
        current_second = now_chicago.second
        current_microsecond = now_chicago.microsecond
        
        # Check if we're exactly at a 15-minute mark
        is_at_15min_mark = (current_minute % 15 == 0) and current_second == 0 and current_microsecond == 0
        
        if is_at_15min_mark:
            # Already at :00, :15, :30, or :45, wait 15 minutes for next interval
            minutes_to_add = 15
        else:
            # Calculate minutes until next 15-minute mark
            minutes_to_add = 15 - (current_minute % 15)
        
        next_run = now_chicago + timedelta(minutes=minutes_to_add)
        # Round down seconds and microseconds to get exact :00, :15, :30, or :45
        next_run = next_run.replace(second=0, microsecond=0)
        
        wait_seconds = (next_run - now_chicago).total_seconds()
        wait_minutes = wait_seconds / 60
        wait_minutes_int = int(wait_seconds // 60)
        wait_seconds_remaining = int(wait_seconds % 60)
        
        return {
            "next_run_time": next_run.strftime("%Y-%m-%d %H:%M:%S %Z"),
            "next_run_time_short": next_run.strftime("%H:%M"),
            "wait_minutes": round(wait_minutes, 1),
            "wait_seconds": int(wait_seconds),
            "wait_minutes_int": wait_minutes_int,
            "wait_seconds_remaining": wait_seconds_remaining,
            "wait_display": f"{wait_minutes_int} min {wait_seconds_remaining} sec",
            "interval": "15 minutes",
            "runs_all_day": True
        }
    except Exception as e:
        logger = logging.getLogger(__name__)
        logger.error(f"Error calculating next run time: {e}")
        return {
            "error": str(e),
            "interval": "15 minutes",
            "runs_all_day": True
        }


# NOTE: Pipeline endpoints are now in routers/pipeline.py and use the new orchestrator
# The old legacy endpoints have been removed - they used the legacy scheduler
# All pipeline operations now go through the orchestrator_instance


@app.post("/api/merger/run")
async def run_data_merger():
    """
    Run the data merger/consolidator to merge daily files into monthly files.
    """
    try:
        # Launch data merger as separate process
        cmd = ["python", str(DATA_MERGER_SCRIPT)]
        
        process = subprocess.Popen(
            cmd,
            cwd=str(QTSW2_ROOT),
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True
        )
        
        # Wait for process to complete (with timeout)
        try:
            stdout, stderr = process.communicate(timeout=300)  # 5 minute timeout
            return_code = process.returncode
            
            if return_code == 0:
                return {
                    "status": "success",
                    "message": "Data merger completed successfully",
                    "output": stdout[-1000:] if stdout else ""  # Last 1000 chars
                }
            else:
                return {
                    "status": "error",
                    "message": f"Data merger failed with return code {return_code}",
                    "output": stderr[-1000:] if stderr else stdout[-1000:] if stdout else ""
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


# NOTE: /api/pipeline/status is now in routers/pipeline.py and uses the orchestrator
# The old legacy endpoint that read from JSONL files directly has been removed


# ============================================================
# WebSocket Event Streaming
# ============================================================
# NOTE: WebSocket endpoints are handled by routers/websocket.py
# The old JSONL file-tailing ConnectionManager has been removed.
# Event architecture: EventBus + WebSocket (routers/websocket.py subscribes to EventBus)
# JSONL files are for persistence and replay only (via orchestrator JSONL monitor)


# ============================================================
# File Count Endpoints
# ============================================================

async def _refresh_file_counts_cache():
    """Background task to refresh file counts cache."""
    global _file_counts_cache
    
    t0 = time.perf_counter()
    
    # These paths should match your actual data directories
    data_raw = QTSW2_ROOT / "data" / "raw"  # Raw CSV files from DataExporter
    data_translated = QTSW2_ROOT / "data" / "translated"  # Translated Parquet files (translator output)
    analyzer_runs = QTSW2_ROOT / "data" / "analyzer_runs"  # Analyzer output folder
    
    # OPTIMIZED: Count files using streaming counter (no list materialization)
    # This prevents blocking the event loop and uses less memory
    async def count_files_streaming(pattern: str, directory: Path, exclude_logs: bool = True) -> int:
        """Count files matching pattern using streaming (no list materialization)."""
        if not directory.exists():
            return 0
        
        loop = asyncio.get_event_loop()
        def scan_files():
            count = 0
            for file_path in directory.rglob(pattern):
                if exclude_logs and "logs" in str(file_path):
                    continue
                count += 1
            return count
        return await loop.run_in_executor(None, scan_files)
    
    # Count all file types in parallel
    raw_count, translated_count, analyzed_count = await asyncio.gather(
        count_files_streaming("*.csv", data_raw, exclude_logs=True),
        count_files_streaming("*.parquet", data_translated, exclude_logs=False),
        count_files_streaming("*.parquet", QTSW2_ROOT / "data" / "analyzed", exclude_logs=True)
    )
    
    duration_ms = int((time.perf_counter() - t0) * 1000)
    
    # Update cache
    _file_counts_cache.update({
        "raw_files": raw_count,
        "translated_files": translated_count,
        "analyzed_files": analyzed_count,
        "computed_at": datetime.now(),
        "last_duration_ms": duration_ms,
    })
    


@app.get("/api/metrics/files")
async def get_file_counts():
    """Get file counts from data directories (cached for fast response)."""
    global _file_counts_cache
    
    # ALWAYS return immediately - even if cache is empty (return zeros, refresh in background)
    # This ensures instant response on first request
    
    # Trigger background refresh if cache is stale or doesn't exist
    cache_age = None
    if _file_counts_cache["computed_at"]:
        cache_age = (datetime.now() - _file_counts_cache["computed_at"]).total_seconds()
    
    should_refresh = (
        _file_counts_cache["computed_at"] is None or
        cache_age is None or
        cache_age > _file_counts_cache_ttl_seconds
    )
    
    if should_refresh and _file_counts_cache["refresh_task"] is None:
        # Start background refresh task (don't await - return cached immediately)
        _file_counts_cache["refresh_task"] = asyncio.create_task(_refresh_file_counts_cache())
        
        # Clear refresh task reference when done
        def clear_task(task):
            if _file_counts_cache["refresh_task"] == task:
                _file_counts_cache["refresh_task"] = None
        
        _file_counts_cache["refresh_task"].add_done_callback(clear_task)
    
    # Return cached values immediately (even if zeros - cache will update in background)
    return {
        "raw_files": _file_counts_cache["raw_files"],
        "translated_files": _file_counts_cache["translated_files"],
        "analyzed_files": _file_counts_cache["analyzed_files"]
    }


# ============================================================
# Streamlit App Launchers
# ============================================================

@app.post("/api/apps/translator/start")
async def start_translator_app():
    """Start the Translator Streamlit app."""
    try:
        # Check if already running by checking if port 8501 is in use
        import socket
        sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        result = sock.connect_ex(('localhost', 8501))
        sock.close()
        
        if result == 0:
            return {"status": "already_running", "url": "http://localhost:8501"}
        
        # Start Streamlit app in new console window (Windows)
        if os.name == 'nt':
            # Use start command to open in new window
            # Verify we're using the correct translator app
            translator_path = QTSW2_ROOT / "scripts" / "translate_raw_app.py"
            app_path = str(translator_path).replace('/', '\\')
            subprocess.Popen(
                f'start "Translator App" cmd /k "streamlit run \"{app_path}\" --server.port 8501"',
                shell=True,
                cwd=str(QTSW2_ROOT)
            )
        else:
            # Linux/Mac
            subprocess.Popen(
                ["streamlit", "run", str(TRANSLATOR_APP), "--server.port", "8501"],
                cwd=str(QTSW2_ROOT)
            )
        
        # Wait a moment for the app to start
        await asyncio.sleep(3)
        
        return {"status": "started", "url": "http://localhost:8501"}
    except Exception as e:
        raise HTTPException(status_code=500, detail=f"Failed to start translator app: {str(e)}")


@app.post("/api/apps/analyzer/start")
async def start_analyzer_app():
    """Start the Analyzer Streamlit app."""
    try:
        # Check if already running
        import socket
        sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        result = sock.connect_ex(('localhost', 8502))
        sock.close()
        
        if result == 0:
            return {"status": "already_running", "url": "http://localhost:8502"}
        
        # Start Streamlit app in new console window (Windows)
        if os.name == 'nt':
            # Use start command to open in new window
            # Verify we're using the correct analyzer app
            analyzer_path = QTSW2_ROOT / "scripts" / "breakout_analyzer" / "analyzer_app" / "app.py"
            app_path = str(analyzer_path).replace('/', '\\')
            subprocess.Popen(
                f'start "Analyzer App" cmd /k "streamlit run \"{app_path}\" --server.port 8502"',
                shell=True,
                cwd=str(QTSW2_ROOT)
            )
        else:
            # Linux/Mac
            subprocess.Popen(
                ["streamlit", "run", str(ANALYZER_APP), "--server.port", "8502"],
                cwd=str(QTSW2_ROOT)
            )
        
        # Wait a moment for the app to start
        await asyncio.sleep(3)
        
        return {"status": "started", "url": "http://localhost:8502"}
    except Exception as e:
        raise HTTPException(status_code=500, detail=f"Failed to start analyzer app: {str(e)}")


@app.post("/api/apps/sequential/start")
async def start_sequential_app():
    """Start the Sequential Processor Streamlit app."""
    try:
        # Check if already running
        import socket
        sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        result = sock.connect_ex(('localhost', 8503))
        sock.close()
        
        if result == 0:
            return {"status": "already_running", "url": "http://localhost:8503"}
        
        # Start Streamlit app in new console window (Windows)
        if os.name == 'nt':
            # Use start command to open in new window
            # Verify we're using the correct sequential processor app
            sequential_path = QTSW2_ROOT / "sequential_processor" / "sequential_processor_app.py"
            app_path = str(sequential_path).replace('/', '\\')
            subprocess.Popen(
                f'start "Sequential Processor App" cmd /k "streamlit run \"{app_path}\" --server.port 8503"',
                shell=True,
                cwd=str(QTSW2_ROOT)
            )
        else:
            # Linux/Mac
            subprocess.Popen(
                ["streamlit", "run", str(SEQUENTIAL_APP), "--server.port", "8503"],
                cwd=str(QTSW2_ROOT)
            )
        
        # Wait a moment for the app to start
        await asyncio.sleep(3)
        
        return {"status": "started", "url": "http://localhost:8503"}
    except Exception as e:
        raise HTTPException(status_code=500, detail=f"Failed to start sequential app: {str(e)}")


# ============================================================
# Timetable Engine API (Master Matrix API moved to modules.matrix.api)
# ============================================================

class TimetableRequest(BaseModel):
    date: Optional[str] = None
    scf_threshold: float = 0.5
    analyzer_runs_dir: str = "data/analyzed"
    output_dir: str = "data/timetable"


class ExecutionTimetableStream(BaseModel):
    stream: str
    instrument: str
    session: str
    slot_time: str
    enabled: bool


class ExecutionTimetableRequest(BaseModel):
    trading_date: str
    streams: List[ExecutionTimetableStream]


# Master Matrix endpoints moved to modules.matrix.api router
# All matrix endpoints are now in: modules/matrix/api.py
# - POST /api/matrix/build
# - GET /api/matrix/files  
# - GET /api/matrix/data
# - GET /api/matrix/test


@app.post("/api/timetable/generate")
async def generate_timetable(request: TimetableRequest):
    """Generate timetable for a trading day."""
    try:
        import sys
        sys.path.insert(0, str(QTSW2_ROOT))
        from modules.timetable.timetable_engine import TimetableEngine
        
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
        
        # Execution timetable (timetable_current.json) is automatically written by generate_timetable()
        # No need to call save_timetable() - canonical file is the single source of truth
        
        # Convert DataFrame to list of dicts for JSON response
        entries = timetable_df.to_dict('records')
        
        return {
            "status": "success",
            "message": "Timetable generated successfully",
            "date": timetable_df['trade_date'].iloc[0] if not timetable_df.empty else None,
            "entries": entries,
            "total_entries": len(timetable_df),
            "allowed_trades": int(timetable_df['allowed'].sum()),
            "execution_file": "data/timetable/timetable_current.json"
        }
    except Exception as e:
        raise HTTPException(status_code=500, detail=f"Failed to generate timetable: {str(e)}")


# Matrix endpoints moved to modules.matrix.api router
# - GET /api/matrix/files -> modules/matrix/api.py
# - GET /api/matrix/data -> modules/matrix/api.py


@app.post("/api/timetable/execution")
async def save_execution_timetable(request: ExecutionTimetableRequest):
    """Save execution timetable file from UI-calculated timetable."""
    try:
        import json
        import pytz
        from pathlib import Path
        
        output_dir = Path("data/timetable")
        output_dir.mkdir(parents=True, exist_ok=True)
        
        # Clean up old files
        for file_path in output_dir.iterdir():
            if file_path.name != "timetable_current.json" and file_path.name != "timetable_current.tmp":
                try:
                    file_path.unlink()
                except:
                    pass
        
        # Get current timestamp in America/Chicago timezone
        chicago_tz = pytz.timezone("America/Chicago")
        chicago_now = datetime.now(chicago_tz)
        as_of = chicago_now.isoformat()
        
        # Compute trading_date using CME rollover rule (17:00 Chicago)
        # If Chicago time < 17:00: trading_date = Chicago calendar date
        # If Chicago time >= 17:00: trading_date = Chicago calendar date + 1 day
        chicago_date = chicago_now.date()
        chicago_hour = chicago_now.hour
        
        # Rollover at 17:00 Chicago time
        if chicago_hour >= 17:
            from datetime import timedelta
            trading_date = (chicago_date + timedelta(days=1)).isoformat()
            rollover_applied = True
        else:
            trading_date = chicago_date.isoformat()
            rollover_applied = False
        
        # Log CME trading date computation for verification
        logging.info(
            f"CME_TRADING_DATE_ROLLOVER: "
            f"Chicago time={chicago_now.strftime('%Y-%m-%d %H:%M:%S %Z')}, "
            f"hour={chicago_hour}, "
            f"calendar_date={chicago_date.isoformat()}, "
            f"computed_trading_date={trading_date}, "
            f"rollover_applied={rollover_applied}"
        )
        
        # Validation: Flag violations
        if chicago_hour >= 17 and trading_date == chicago_date.isoformat():
            logging.warning(
                f"CME_TRADING_DATE_VALIDATION_FAILED: "
                f"as_of={as_of} (>= 17:00) but trading_date={trading_date} "
                f"equals calendar date {chicago_date.isoformat()}"
            )
        
        # Optional: Log if request.trading_date differs
        if request.trading_date != trading_date:
            logging.info(
                f"CME_TRADING_DATE_COMPUTED: "
                f"request.trading_date={request.trading_date}, computed trading_date={trading_date}"
            )
        
        # Build execution timetable document
        execution_timetable = {
            'as_of': as_of,
            'trading_date': trading_date,  # Use computed value
            'timezone': 'America/Chicago',
            'source': 'master_matrix',
            'streams': [s.dict() for s in request.streams]
        }
        
        # Atomic write: write to temp file, then rename
        temp_file = output_dir / "timetable_current.tmp"
        final_file = output_dir / "timetable_current.json"
        
        try:
            # Write to temporary file
            with open(temp_file, 'w', encoding='utf-8') as f:
                json.dump(execution_timetable, f, indent=2, ensure_ascii=False)
            
            # Atomic rename
            temp_file.replace(final_file)
            
            return {
                "status": "success",
                "message": "Execution timetable saved",
                "file": str(final_file),
                "streams": len(request.streams)
            }
        except Exception as e:
            # Clean up temp file on error
            if temp_file.exists():
                try:
                    temp_file.unlink()
                except:
                    pass
            raise
        
    except Exception as e:
        raise HTTPException(status_code=500, detail=f"Failed to save execution timetable: {str(e)}")


@app.get("/api/timetable/current")
async def get_current_timetable():
    """Get current execution timetable file (timetable_current.json)."""
    try:
        timetable_file = QTSW2_ROOT / "data" / "timetable" / "timetable_current.json"
        if not timetable_file.exists():
            raise HTTPException(status_code=404, detail="timetable_current.json not found")
        
        with open(timetable_file, 'r', encoding='utf-8') as f:
            timetable = json.load(f)
        
        return timetable
    except HTTPException:
        raise
    except Exception as e:
        raise HTTPException(status_code=500, detail=f"Failed to read timetable: {str(e)}")


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

