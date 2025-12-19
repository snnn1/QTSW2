"""
Pipeline Dashboard Backend - FastAPI Integration Layer

⚠️ ARCHITECTURE NOTE: This is a backend integration layer / control plane, not a domain module.

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
            from .orchestrator import PipelineOrchestrator, OrchestratorConfig
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
    # Also add dashboard to path for orchestrator imports
    dashboard_path = backend_path.parent
    if str(dashboard_path) not in sys.path:
        sys.path.insert(0, str(dashboard_path))
    try:
        from routers import pipeline, schedule, websocket, apps, metrics
    except ImportError as e:
        # Last resort: try absolute import
        from dashboard.backend.routers import pipeline, schedule, websocket, apps, metrics

app.include_router(pipeline.router)
app.include_router(schedule.router)
app.include_router(websocket.router)
app.include_router(apps.router)
app.include_router(metrics.router)
# Matrix router removed - endpoints are implemented directly in main.py to avoid conflicts

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

@app.get("/api/metrics/files")
async def get_file_counts():
    """Get file counts from data directories."""
    # These paths should match your actual data directories
    data_raw = QTSW2_ROOT / "data" / "raw"  # Raw CSV files from DataExporter
    data_translated = QTSW2_ROOT / "data" / "translated"  # Translated Parquet files (translator output)
    analyzer_runs = QTSW2_ROOT / "data" / "analyzer_runs"  # Analyzer output folder
    sequencer_runs = QTSW2_ROOT / "data" / "sequencer_runs"  # Sequencer output folder
    
    # Count raw CSV files (exclude subdirectories like logs folder)
    raw_count = 0
    if data_raw.exists():
        raw_files = list(data_raw.glob("*.csv"))
        raw_count = len([f for f in raw_files if f.parent == data_raw])
    
    # Count translated files (translator output - recursive search for subdirectories)
    translated_count = 0
    if data_translated.exists():
        translated_files = list(data_translated.rglob("*.parquet"))
        translated_count = len(translated_files)
    
    # Count analyzed files (monthly consolidated files in instrument/year folders)
    analyzed_count = 0
    if analyzer_runs.exists():
        # Count monthly parquet files in instrument/year subfolders
        analyzed_files = list(analyzer_runs.rglob("*.parquet"))
        analyzed_count = len(analyzed_files)
    
    return {
        "raw_files": raw_count,
        "translated_files": translated_count,
        "analyzed_files": analyzed_count
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
    
    # RELOAD MODULE FIRST - before anything else
    try:
        import importlib
        import inspect
        sys.path.insert(0, str(QTSW2_ROOT))
        
        # ALWAYS reload the module to ensure we have the latest version
        # Remove all master_matrix related modules from cache
        modules_to_remove = [key for key in list(sys.modules.keys()) if 'master_matrix' in key]
        for module_name in modules_to_remove:
            del sys.modules[module_name]
        
        # Clear import cache
        importlib.invalidate_caches()
        
        # Force reload from file - this ensures we get the latest code
        import importlib.util
        master_matrix_file = QTSW2_ROOT / "master_matrix" / "master_matrix.py"
        spec = importlib.util.spec_from_file_location(
            "master_matrix_master_matrix",
            master_matrix_file
        )
        if spec is None or spec.loader is None:
            raise ImportError(f"Could not load spec from {master_matrix_file}")
        
        master_matrix_module = importlib.util.module_from_spec(spec)
        # Execute the module to load it
        spec.loader.exec_module(master_matrix_module)
        MasterMatrix = master_matrix_module.MasterMatrix
        
        # Verify the signature
        sig = inspect.signature(MasterMatrix.__init__)
        params = list(sig.parameters.keys())
        logger.info(f"MasterMatrix signature: {params}")
        
        if 'sequencer_runs_dir' not in params:
            logger.warning("WARNING: MasterMatrix does NOT have sequencer_runs_dir parameter!")
        else:
            logger.info("MasterMatrix has sequencer_runs_dir parameter - good!")
    except Exception as e:
        logger.error(f"ERROR reloading MasterMatrix module: {e}")
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
        
        # Extract available years from the matrix data itself
        available_years = []
        try:
            if 'trade_date' in df.columns:
                dates = pd.to_datetime(df['trade_date'], errors='coerce')
                available_years = sorted([int(y) for y in dates.dt.year.dropna().unique() if pd.notna(y)])
            elif 'Date' in df.columns:
                dates = pd.to_datetime(df['Date'], errors='coerce')
                available_years = sorted([int(y) for y in dates.dt.year.dropna().unique() if pd.notna(y)])
            
            logger.info(f"Extracted {len(available_years)} years from matrix data: {available_years}")
        except Exception as e:
            logger.error(f"Error extracting years: {e}")
            available_years = []
        
        logger.info(f"Total records in file: {len(df)}, returning up to {limit} records")
        
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

