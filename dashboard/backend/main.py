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
from datetime import datetime
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

app = FastAPI(title="Pipeline Dashboard API")

# Configure logging to reduce verbosity for routine polling
logging.getLogger("uvicorn.access").setLevel(logging.WARNING)

# Configure logging to also write to log file
LOG_FILE_PATH = QTSW2_ROOT / "logs" / "backend_debug.log"
LOG_FILE_PATH.parent.mkdir(parents=True, exist_ok=True)

# Create a file handler for the log file
file_handler = logging.FileHandler(LOG_FILE_PATH, mode='a', encoding='utf-8')
file_handler.setLevel(logging.INFO)
file_formatter = logging.Formatter('%(asctime)s - %(levelname)s - %(message)s')
file_handler.setFormatter(file_formatter)

# Add file handler to root logger
root_logger = logging.getLogger()
root_logger.addHandler(file_handler)
root_logger.setLevel(logging.INFO)

# Log startup message
import sys
startup_msg = "=" * 80 + "\nMaster Matrix Backend Starting...\n" + "=" * 80 + "\n"
print(startup_msg, file=sys.stderr)
sys.stderr.flush()
# Also write directly to log file
with open(LOG_FILE_PATH, 'a', encoding='utf-8') as f:
    f.write(startup_msg)
    f.flush()

# Startup event handler - this runs when FastAPI starts
@app.on_event("startup")
async def startup_event():
    import sys
    logger = logging.getLogger(__name__)
    startup_msg2 = "=" * 80 + "\nFastAPI application started - ready to receive requests\n" + "=" * 80 + "\n"
    print(startup_msg2, file=sys.stderr)
    sys.stderr.flush()
    # Also write directly to log file
    with open(LOG_FILE_PATH, 'a', encoding='utf-8') as f:
        f.write(startup_msg2)
        f.flush()
    logger.info("FastAPI application started - ready to receive requests")
    
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

# CORS middleware for React frontend
app.add_middleware(
    CORSMiddleware,
    allow_origins=["http://localhost:5173", "http://localhost:5174", "http://localhost:3000", "http://192.168.1.171:5174"],  # Vite default ports + network
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
)


# ============================================================
# Data Models
# ============================================================

class ScheduleConfig(BaseModel):
    schedule_time: str  # HH:MM format


class PipelineStartResponse(BaseModel):
    run_id: str
    event_log_path: str
    status: str


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
    # Validate time format
    try:
        datetime.strptime(config.schedule_time, "%H:%M")
    except ValueError:
        raise HTTPException(status_code=400, detail="Invalid time format. Use HH:MM (24-hour)")
    
    save_schedule_config(config)
    return config


@app.post("/api/pipeline/start", response_model=PipelineStartResponse)
async def start_pipeline(wait_for_export: bool = False, launch_ninjatrader: bool = False):
    """
    Start a pipeline run immediately.
    
    Args:
        wait_for_export: If True, wait for new exports before processing (default: False)
        launch_ninjatrader: If True, launch NinjaTrader before waiting for exports (default: False)
    """
    run_id = str(uuid.uuid4())
    event_log_path = EVENT_LOGS_DIR / f"pipeline_{run_id}.jsonl"
    
    # Create empty event log file
    event_log_path.touch()
    
    # Launch pipeline as separate process
    env = os.environ.copy()
    env["PIPELINE_EVENT_LOG"] = str(event_log_path)
    
    # Build command arguments
    cmd = ["python", str(SCHEDULER_SCRIPT), "--now", "--no-debug-window"]
    if wait_for_export:
        cmd.append("--wait-for-export")
    if launch_ninjatrader:
        cmd.append("--launch-ninjatrader")
    
    try:
        process = subprocess.Popen(
            cmd,
            cwd=str(QTSW2_ROOT),
            env=env,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE
        )
        
        return PipelineStartResponse(
            run_id=run_id,
            event_log_path=str(event_log_path),
            status="started"
        )
    except Exception as e:
        raise HTTPException(status_code=500, detail=f"Failed to start pipeline: {str(e)}")


@app.post("/api/pipeline/stage/{stage_name}", response_model=PipelineStartResponse)
async def run_stage(stage_name: str):
    """
    Run a specific pipeline stage (translator, analyzer, or sequential).
    
    Args:
        stage_name: Name of the stage to run (translator, analyzer, or sequential)
    """
    if stage_name not in ["translator", "analyzer", "sequential"]:
        raise HTTPException(status_code=400, detail=f"Invalid stage name: {stage_name}. Must be one of: translator, analyzer, sequential")
    
    run_id = str(uuid.uuid4())
    event_log_path = EVENT_LOGS_DIR / f"pipeline_{run_id}.jsonl"
    
    # Create empty event log file
    event_log_path.touch()
    
    # Launch stage as separate process
    env = os.environ.copy()
    env["PIPELINE_EVENT_LOG"] = str(event_log_path)
    
    # Build command arguments
    cmd = ["python", str(SCHEDULER_SCRIPT), "--stage", stage_name, "--no-debug-window"]
    
    try:
        process = subprocess.Popen(
            cmd,
            cwd=str(QTSW2_ROOT),
            env=env,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE
        )
        
        return PipelineStartResponse(
            run_id=run_id,
            event_log_path=str(event_log_path),
            status="started"
        )
    except Exception as e:
        raise HTTPException(status_code=500, detail=f"Failed to start {stage_name} stage: {str(e)}")


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


@app.get("/api/pipeline/status")
async def get_pipeline_status():
    """Get current pipeline run status (if any active run)."""
    # Find most recent event log
    event_logs = sorted(EVENT_LOGS_DIR.glob("pipeline_*.jsonl"), key=lambda p: p.stat().st_mtime, reverse=True)
    
    if not event_logs:
        return {"active": False}
    
    latest_log = event_logs[0]
    
    # Check if log file is very old (more than 1 hour) - consider it inactive
    import time
    file_age_seconds = time.time() - latest_log.stat().st_mtime
    if file_age_seconds > 3600:  # 1 hour
        return {"active": False}
    
    # Read last event to determine status
    try:
        with open(latest_log, "r") as f:
            lines = f.readlines()
            if lines:
                last_event = json.loads(lines[-1])
                event_type = last_event.get("event", "")
                stage = last_event.get("stage", "")
                
                # Check if pipeline is completed
                # Pipeline is complete if:
                # 1. Last event is "success" or "failure" from "audit" stage (pipeline complete)
                # 2. Last event is "success" or "failure" from "pipeline" stage with "complete" message
                # 3. Last event is "success" or "failure" from individual stage (translator, analyzer, sequential) - single stage run complete
                # 4. Log file hasn't been modified in last 2 minutes (likely completed)
                
                is_complete = False
                if stage == "audit" and event_type in ["success", "failure"]:
                    is_complete = True
                elif stage == "pipeline" and event_type in ["success", "failure"]:
                    msg = last_event.get("msg", "").lower()
                    if "complete" in msg or "finished" in msg:
                        is_complete = True
                elif stage in ["translator", "analyzer", "sequential"] and event_type in ["success", "failure"]:
                    # Single stage run is complete if last event is success/failure from that stage
                    # Check if there are any newer events (wait a bit for potential follow-up events)
                    # If file hasn't been modified in 30 seconds after stage completion, consider it done
                    if file_age_seconds > 30:  # 30 seconds after last event
                        is_complete = True
                
                # If file hasn't been modified in 2 minutes, consider it inactive (reduced from 5 minutes)
                if file_age_seconds > 120:  # 2 minutes
                    is_complete = True
                
                if is_complete:
                    return {"active": False}
                
                # Verify the run_id exists and has a valid event log
                run_id = last_event.get("run_id")
                if run_id:
                    expected_log = EVENT_LOGS_DIR / f"pipeline_{run_id}.jsonl"
                    if not expected_log.exists():
                        # Run ID doesn't match any existing log file - consider inactive
                        return {"active": False}
                
                return {
                    "active": True,
                    "run_id": run_id,
                    "stage": last_event.get("stage"),
                    "event": last_event.get("event"),
                    "timestamp": last_event.get("timestamp")
                }
    except Exception as e:
        print(f"Error reading pipeline status: {e}")
        pass
    
    return {"active": False}


# ============================================================
# WebSocket Event Streaming
# ============================================================

class ConnectionManager:
    """Manages WebSocket connections."""
    
    def __init__(self):
        self.active_connections: list[WebSocket] = []
        self.tail_tasks: Dict[str, asyncio.Task] = {}
    
    async def connect(self, websocket: WebSocket):
        await websocket.accept()
        self.active_connections.append(websocket)
    
    def disconnect(self, websocket: WebSocket):
        if websocket in self.active_connections:
            self.active_connections.remove(websocket)
    
    async def send_personal_message(self, message: dict, websocket: WebSocket):
        try:
            await websocket.send_json(message)
        except Exception:
            self.disconnect(websocket)
    
    async def broadcast(self, message: dict):
        disconnected = []
        for connection in list(self.active_connections):  # Create copy to avoid modification during iteration
            try:
                # Check if connection is still in active_connections before sending
                if connection in self.active_connections:
                    await connection.send_json(message)
            except Exception as e:
                error_msg = str(e)
                # Don't log expected errors (connection closed, etc.)
                if "websocket.close" not in error_msg.lower() and "response already completed" not in error_msg.lower():
                    print(f"Error broadcasting to WebSocket: {e}")
                disconnected.append(connection)
        
        for conn in disconnected:
            self.disconnect(conn)
    
    def start_tailing(self, event_log_path: str):
        """Start tailing an event log file."""
        if event_log_path in self.tail_tasks:
            return  # Already tailing
        
        task = asyncio.create_task(self._tail_file(event_log_path))
        self.tail_tasks[event_log_path] = task
    
    async def _tail_file(self, event_log_path: str):
        """Tail a file and broadcast events."""
        path = Path(event_log_path)
        print(f"Tailing file: {path}, exists: {path.exists()}")
        
        # Wait for file to be created if it doesn't exist
        max_wait = 30  # Wait up to 30 seconds for file to be created
        waited = 0
        while not path.exists() and waited < max_wait:
            await asyncio.sleep(0.5)
            waited += 0.5
            # Check if we still have active connections
            if not self.active_connections:
                print(f"No active connections, stopping tail wait for {path}")
                return
        
        if not path.exists():
            print(f"Event log file does not exist after waiting: {path}")
            # Send a message to clients that the file doesn't exist
            try:
                await self.broadcast({
                    "stage": "system",
                    "event": "error",
                    "msg": f"Event log file not found: {path.name}",
                    "timestamp": datetime.now().isoformat()
                })
            except Exception:
                pass  # Ignore errors if no connections
            return
        
        # Read existing content first and broadcast it
        last_position = 0
        try:
            with open(path, "r", encoding="utf-8") as f:
                existing_lines = f.readlines()
                print(f"Found {len(existing_lines)} existing lines in event log")
                if existing_lines:
                    for line in existing_lines:
                        if line.strip():
                            try:
                                event = json.loads(line)
                                print(f"Broadcasting existing event: {event.get('stage', 'unknown')}/{event.get('event', 'unknown')}")
                                await self.broadcast(event)
                            except json.JSONDecodeError as e:
                                print(f"Failed to parse line: {line[:100]}, error: {e}")
                                pass
                            except Exception as e:
                                print(f"Error broadcasting existing event: {e}")
                                # Don't break - continue with other events
                    last_position = f.tell()
                else:
                    # Empty file - wait for events to be written
                    print(f"Event log file is empty, waiting for events...")
                    last_position = 0
                    # Send a status message to client that we're waiting
                    try:
                        await self.broadcast({
                            "stage": "system",
                            "event": "log",
                            "msg": "Waiting for pipeline events...",
                            "timestamp": datetime.now().isoformat()
                        })
                    except Exception:
                        pass  # Ignore if no connections
        except Exception as e:
            print(f"Error reading existing content: {e}")
            import traceback
            traceback.print_exc()
            last_position = 0
        
        # Poll for new lines - keep tailing even if there are temporary errors
        consecutive_errors = 0
        max_consecutive_errors = 20  # Allow more errors before giving up
        last_successful_read = time.time()
        
        while True:
            try:
                # Check if we still have active connections
                if not self.active_connections:
                    print(f"No active connections, stopping tail for {path}")
                    # Clean up task reference
                    if event_log_path in self.tail_tasks:
                        self.tail_tasks.pop(event_log_path)
                    break
                
                # Check if file exists
                if not path.exists():
                    consecutive_errors += 1
                    if consecutive_errors >= max_consecutive_errors:
                        print(f"File still doesn't exist after {max_consecutive_errors} checks, stopping tail")
                        break
                    await asyncio.sleep(1)  # Wait longer if file doesn't exist
                    continue
                
                # Try to read new lines
                try:
                    with open(path, "r", encoding="utf-8") as f:
                        f.seek(last_position)
                        new_lines = f.readlines()
                        last_position = f.tell()
                        last_successful_read = time.time()
                        
                        # Process new lines
                        for line in new_lines:
                            if line.strip():
                                try:
                                    event = json.loads(line)
                                    # Only broadcast if we still have connections
                                    if self.active_connections:
                                        await self.broadcast(event)
                                    consecutive_errors = 0  # Reset error counter on success
                                except json.JSONDecodeError as e:
                                    consecutive_errors += 1
                                    print(f"Failed to parse JSON line (error {consecutive_errors}/{max_consecutive_errors}): {e}")
                                    if consecutive_errors >= max_consecutive_errors:
                                        print(f"Too many JSON parse errors, but continuing to tail...")
                                        consecutive_errors = max_consecutive_errors - 5  # Reset to allow recovery
                                except Exception as e:
                                    error_msg = str(e)
                                    consecutive_errors += 1
                                    # Don't log expected errors (connection closed, etc.)
                                    if "websocket.close" not in error_msg.lower() and "response already completed" not in error_msg.lower():
                                        print(f"Error broadcasting new event (error {consecutive_errors}/{max_consecutive_errors}): {e}")
                                    # Don't break - keep trying
                                    if consecutive_errors >= max_consecutive_errors:
                                        print(f"Too many broadcast errors, but continuing to tail...")
                                        consecutive_errors = max_consecutive_errors - 5  # Reset to allow recovery
                except (IOError, OSError, PermissionError) as e:
                    # File might be locked or temporarily unavailable
                    consecutive_errors += 1
                    print(f"File read error (attempt {consecutive_errors}/{max_consecutive_errors}): {e}")
                    if consecutive_errors < max_consecutive_errors:
                        await asyncio.sleep(1)  # Wait longer on file errors
                        continue
                    else:
                        print(f"Too many file read errors, but continuing to tail...")
                        consecutive_errors = max_consecutive_errors - 5  # Reset to allow recovery
                        await asyncio.sleep(2)
                        continue
                
                await asyncio.sleep(0.5)  # Poll every 500ms
                
            except FileNotFoundError:
                # File deleted or moved - wait a bit and check again
                consecutive_errors += 1
                print(f"File not found (attempt {consecutive_errors}/{max_consecutive_errors})")
                if consecutive_errors < max_consecutive_errors:
                    await asyncio.sleep(1)
                    continue
                else:
                    print(f"File still not found after {max_consecutive_errors} attempts, but continuing to tail...")
                    consecutive_errors = max_consecutive_errors - 5  # Reset to allow recovery
                    await asyncio.sleep(2)
                    continue
            except asyncio.CancelledError:
                print(f"Tailing task cancelled for {path}")
                break
            except Exception as e:
                consecutive_errors += 1
                print(f"Unexpected error in tail loop (attempt {consecutive_errors}/{max_consecutive_errors}): {e}")
                import traceback
                traceback.print_exc()
                if consecutive_errors < max_consecutive_errors:
                    await asyncio.sleep(1)  # Wait before retrying
                    continue
                else:
                    print(f"Too many errors, but continuing to tail...")
                    consecutive_errors = max_consecutive_errors - 5  # Reset to allow recovery
                    await asyncio.sleep(2)
                    continue
        
        # Clean up
        if event_log_path in self.tail_tasks:
            del self.tail_tasks[event_log_path]


manager = ConnectionManager()


@app.websocket("/ws/events")
async def websocket_events(websocket: WebSocket, run_id: Optional[str] = None):
    """WebSocket endpoint for real-time event streaming."""
    try:
        await manager.connect(websocket)
        print(f"WebSocket connected. run_id: {run_id}")
        
        # Send initial connection confirmation
        try:
            await websocket.send_json({
                "type": "connected",
                "run_id": run_id,
                "timestamp": datetime.now().isoformat()
            })
        except Exception as e:
            print(f"Error sending initial message: {e}")
            manager.disconnect(websocket)
            return
        
        # If run_id provided, start tailing that log
        if run_id:
            event_log_path = EVENT_LOGS_DIR / f"pipeline_{run_id}.jsonl"
            print(f"Starting to tail event log: {event_log_path}")
            print(f"Event log exists: {event_log_path.exists()}")
            try:
                manager.start_tailing(str(event_log_path))
            except Exception as e:
                print(f"Error starting tailing: {e}")
                import traceback
                traceback.print_exc()
                # Don't disconnect - continue with connection
    except Exception as e:
        print(f"Error in WebSocket connection setup: {e}")
        import traceback
        traceback.print_exc()
        try:
            manager.disconnect(websocket)
        except:
            pass
        return
    
    # Keep connection alive
    try:
        while True:
            try:
                # Check if websocket is still in active connections
                if websocket not in manager.active_connections:
                    print("WebSocket no longer in active connections, breaking")
                    break
                    
                # Wait for client messages (ping/pong or requests)
                data = await asyncio.wait_for(websocket.receive_text(), timeout=60.0)  # Increased timeout
                # Echo back or handle commands
                if data == "ping":
                    try:
                        await websocket.send_json({"type": "pong"})
                    except Exception as e:
                        print(f"Error sending pong: {e}")
                        break
            except asyncio.TimeoutError:
                # Send keepalive to prevent connection timeout
                try:
                    if websocket in manager.active_connections:
                        await websocket.send_json({"type": "keepalive", "timestamp": datetime.now().isoformat()})
                except Exception as e:
                    error_msg = str(e)
                    # If connection is closed, break the loop
                    if "websocket.close" in error_msg.lower() or "response already completed" in error_msg.lower():
                        print("WebSocket closed during keepalive")
                        break
                    print(f"Error sending keepalive: {e}")
                    # Don't break on keepalive errors - connection might still be valid
            except WebSocketDisconnect:
                print("WebSocket disconnect detected in receive loop")
                break
            except Exception as e:
                print(f"Unexpected error in WebSocket loop: {e}")
                import traceback
                traceback.print_exc()
                break
                
    except WebSocketDisconnect:
        print("WebSocket disconnected normally")
        manager.disconnect(websocket)
        # Stop tailing when connection closes
        if run_id:
            event_log_path = EVENT_LOGS_DIR / f"pipeline_{run_id}.jsonl"
            if str(event_log_path) in manager.tail_tasks:
                task = manager.tail_tasks.pop(str(event_log_path))
                task.cancel()
    except Exception as e:
        print(f"WebSocket error: {e}")
        import traceback
        traceback.print_exc()
        try:
            manager.disconnect(websocket)
            # Stop tailing on error
            if run_id:
                event_log_path = EVENT_LOGS_DIR / f"pipeline_{run_id}.jsonl"
                if str(event_log_path) in manager.tail_tasks:
                    task = manager.tail_tasks.pop(str(event_log_path))
                    task.cancel()
        except:
            pass


@app.websocket("/ws/events/{run_id}")
async def websocket_events_by_run(websocket: WebSocket, run_id: str):
    """WebSocket endpoint for specific run ID."""
    print(f"WebSocket connection request for run_id: {run_id}")
    await websocket_events(websocket, run_id=run_id)


# ============================================================
# File Count Endpoints
# ============================================================

@app.get("/api/metrics/files")
async def get_file_counts():
    """Get file counts from data directories."""
    # These paths should match your actual data directories
    # Using the same paths as in the scheduler
    data_raw = QTSW2_ROOT / "data" / "raw"  # Fixed: QTSW2/data/raw (where DataExporter writes)
    data_processed = QTSW2_ROOT / "data" / "processed"  # QTSW2/data/processed (where files should go)
    analyzer_runs = QTSW2_ROOT / "data" / "analyzer_runs"  # Analyzer output folder
    sequencer_runs = QTSW2_ROOT / "data" / "sequencer_runs"  # Sequencer output folder
    
    # Count raw CSV files (exclude subdirectories like logs folder)
    raw_count = 0
    if data_raw.exists():
        raw_files = list(data_raw.glob("*.csv"))
        raw_count = len([f for f in raw_files if f.parent == data_raw])
    
    # Count processed files
    processed_count = 0
    if data_processed.exists():
        processed_files = list(data_processed.glob("*.parquet"))
        processed_files.extend(list(data_processed.glob("*.csv")))
        processed_count = len([f for f in processed_files if f.parent == data_processed])
    
    # Count analyzed files (monthly consolidated files in instrument/year folders)
    analyzed_count = 0
    if analyzer_runs.exists():
        # Count monthly parquet files in instrument/year subfolders
        analyzed_files = list(analyzer_runs.rglob("*.parquet"))
        analyzed_count = len(analyzed_files)
    
    return {
        "raw_files": raw_count,
        "processed_files": processed_count,
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
        
        master_df = matrix.build_master_matrix(
            start_date=request.start_date,
            end_date=request.end_date,
            specific_date=request.specific_date,
            output_dir=request.output_dir,
            stream_filters=stream_filters_dict,
            analyzer_runs_dir=str(analyzer_runs_path),
            streams=request.streams
        )
        
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
        
        timetable_df = engine.generate_timetable(trade_date=request.date)
        
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
        
        # Load parquet file
        try:
            df = pd.read_parquet(file_to_load)
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

