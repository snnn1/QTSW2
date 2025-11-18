"""
Pipeline Dashboard Backend
FastAPI server for pipeline monitoring and control
"""

import os
import json
import subprocess
import asyncio
import time
from pathlib import Path
from typing import Optional, Dict
from datetime import datetime
import uuid

from fastapi import FastAPI, WebSocket, WebSocketDisconnect, HTTPException
from fastapi.middleware.cors import CORSMiddleware
from pydantic import BaseModel
import uvicorn
import logging

# Configuration
QTSW2_ROOT = Path(__file__).parent.parent.parent
SCHEDULER_SCRIPT = QTSW2_ROOT / "automation" / "daily_data_pipeline_scheduler.py"
DATA_MERGER_SCRIPT = QTSW2_ROOT / "tools" / "data_merger.py"
EVENT_LOGS_DIR = QTSW2_ROOT / "automation" / "logs" / "events"
EVENT_LOGS_DIR.mkdir(parents=True, exist_ok=True)

# Schedule config file
SCHEDULE_CONFIG_FILE = QTSW2_ROOT / "automation" / "schedule_config.json"

app = FastAPI(title="Pipeline Dashboard API")

# Configure logging to reduce verbosity for routine polling
logging.getLogger("uvicorn.access").setLevel(logging.WARNING)

# CORS middleware for React frontend
app.add_middleware(
    CORSMiddleware,
    allow_origins=["http://localhost:5173", "http://localhost:3000"],  # Vite default port
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
    return {"message": "Pipeline Dashboard API"}


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
    data_raw_archived = data_raw / "archived"  # Archive folder for raw files
    data_processed = QTSW2_ROOT / "data" / "processed"  # QTSW2/data/processed (where files should go)
    data_processed_archived = data_processed / "archived"  # Archive folder for processed files
    analyzer_runs = QTSW2_ROOT / "data" / "analyzer_runs"  # Analyzer output folder
    sequencer_runs = QTSW2_ROOT / "data" / "sequencer_runs"  # Sequencer output folder
    
    # Count raw CSV files (exclude subdirectories like logs and archived folders)
    raw_count = 0
    if data_raw.exists():
        raw_files = list(data_raw.glob("*.csv"))
        raw_count = len([f for f in raw_files if f.parent == data_raw])
    
    # Count processed files (exclude archived folder)
    processed_count = 0
    if data_processed.exists():
        processed_files = list(data_processed.glob("*.parquet"))
        processed_files.extend(list(data_processed.glob("*.csv")))
        # Exclude files in archived subfolder
        processed_count = len([f for f in processed_files if f.parent == data_processed])
    
    # Count archived files from raw and processed archive locations only
    # (analyzer/sequencer archiving removed - handled by data merger)
    archived_count = 0
    if data_raw_archived.exists():
        archived_count += len(list(data_raw_archived.glob("*.csv")))
    if data_processed_archived.exists():
        archived_count += len(list(data_processed_archived.glob("*.parquet")))
        archived_count += len(list(data_processed_archived.glob("*.csv")))
    
    # Count analyzed files (monthly consolidated files in instrument/year folders)
    analyzed_count = 0
    if analyzer_runs.exists():
        # Count monthly parquet files in instrument/year subfolders
        analyzed_files = list(analyzer_runs.rglob("*.parquet"))
        # Exclude files in summaries folder
        analyzed_count = len([f for f in analyzed_files if "summaries" not in str(f)])
    
    return {
        "raw_files": raw_count,
        "processed_files": processed_count,
        "archived_files": archived_count,
        "analyzed_files": analyzed_count
    }


if __name__ == "__main__":
    uvicorn.run(app, host="0.0.0.0", port=8000, log_level="warning")

