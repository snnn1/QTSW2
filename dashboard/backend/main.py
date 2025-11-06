"""
Pipeline Dashboard Backend
FastAPI server for pipeline monitoring and control
"""

import os
import json
import subprocess
import asyncio
from pathlib import Path
from typing import Optional, Dict
from datetime import datetime
import uuid

from fastapi import FastAPI, WebSocket, WebSocketDisconnect, HTTPException
from fastapi.middleware.cors import CORSMiddleware
from pydantic import BaseModel
import uvicorn

# Configuration
QTSW2_ROOT = Path(__file__).parent.parent.parent
SCHEDULER_SCRIPT = QTSW2_ROOT / "automation" / "daily_data_pipeline_scheduler.py"
EVENT_LOGS_DIR = QTSW2_ROOT / "automation" / "logs" / "events"
EVENT_LOGS_DIR.mkdir(parents=True, exist_ok=True)

# Schedule config file
SCHEDULE_CONFIG_FILE = QTSW2_ROOT / "automation" / "schedule_config.json"

app = FastAPI(title="Pipeline Dashboard API")

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
async def start_pipeline():
    """Start a pipeline run immediately."""
    run_id = str(uuid.uuid4())
    event_log_path = EVENT_LOGS_DIR / f"pipeline_{run_id}.jsonl"
    
    # Create empty event log file
    event_log_path.touch()
    
    # Launch pipeline as separate process
    env = os.environ.copy()
    env["PIPELINE_EVENT_LOG"] = str(event_log_path)
    
    try:
        process = subprocess.Popen(
            ["python", str(SCHEDULER_SCRIPT), "--now"],
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


@app.get("/api/pipeline/status")
async def get_pipeline_status():
    """Get current pipeline run status (if any active run)."""
    # Find most recent event log
    event_logs = sorted(EVENT_LOGS_DIR.glob("pipeline_*.jsonl"), key=lambda p: p.stat().st_mtime, reverse=True)
    
    if not event_logs:
        return {"active": False}
    
    latest_log = event_logs[0]
    
    # Read last event to determine status
    try:
        with open(latest_log, "r") as f:
            lines = f.readlines()
            if lines:
                last_event = json.loads(lines[-1])
                return {
                    "active": True,
                    "run_id": last_event.get("run_id"),
                    "stage": last_event.get("stage"),
                    "event": last_event.get("event"),
                    "timestamp": last_event.get("timestamp")
                }
    except Exception:
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
        for connection in self.active_connections:
            try:
                await connection.send_json(message)
            except Exception:
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
        
        # Wait for file to be created if it doesn't exist
        max_wait = 10  # Wait up to 10 seconds
        waited = 0
        while not path.exists() and waited < max_wait:
            await asyncio.sleep(0.5)
            waited += 0.5
        
        if not path.exists():
            return
        
        # Read existing content first and broadcast it
        try:
            with open(path, "r", encoding="utf-8") as f:
                existing_lines = f.readlines()
                for line in existing_lines:
                    if line.strip():
                        try:
                            event = json.loads(line)
                            await self.broadcast(event)
                        except json.JSONDecodeError:
                            pass
                last_position = f.tell()
        except Exception:
            last_position = 0
        
        # Poll for new lines
        while True:
            try:
                if not path.exists():
                    await asyncio.sleep(0.5)
                    continue
                
                with open(path, "r", encoding="utf-8") as f:
                    f.seek(last_position)
                    new_lines = f.readlines()
                    last_position = f.tell()
                    
                    for line in new_lines:
                        if line.strip():
                            try:
                                event = json.loads(line)
                                await self.broadcast(event)
                            except json.JSONDecodeError:
                                pass
                
                await asyncio.sleep(0.5)  # Poll every 500ms
                
            except FileNotFoundError:
                # File deleted or moved - wait a bit and check again
                await asyncio.sleep(1)
                if not path.exists():
                    # File is gone, stop tailing
                    break
            except Exception as e:
                print(f"Error tailing file {event_log_path}: {e}")
                await asyncio.sleep(1)
        
        # Clean up
        if event_log_path in self.tail_tasks:
            del self.tail_tasks[event_log_path]


manager = ConnectionManager()


@app.websocket("/ws/events")
async def websocket_events(websocket: WebSocket, run_id: Optional[str] = None):
    """WebSocket endpoint for real-time event streaming."""
    await manager.connect(websocket)
    
    try:
        # If run_id provided, start tailing that log
        if run_id:
            event_log_path = EVENT_LOGS_DIR / f"pipeline_{run_id}.jsonl"
            manager.start_tailing(str(event_log_path))
        
        # Keep connection alive
        while True:
            try:
                # Wait for client messages (ping/pong or requests)
                data = await asyncio.wait_for(websocket.receive_text(), timeout=30.0)
                # Echo back or handle commands
                if data == "ping":
                    await websocket.send_json({"type": "pong"})
            except asyncio.TimeoutError:
                # Send keepalive
                await websocket.send_json({"type": "keepalive"})
            except WebSocketDisconnect:
                break
                
    except WebSocketDisconnect:
        manager.disconnect(websocket)
    except Exception as e:
        print(f"WebSocket error: {e}")
        manager.disconnect(websocket)


@app.websocket("/ws/events/{run_id}")
async def websocket_events_by_run(websocket: WebSocket, run_id: str):
    """WebSocket endpoint for specific run ID."""
    await websocket_events(websocket, run_id=run_id)


# ============================================================
# File Count Endpoints
# ============================================================

@app.get("/api/metrics/files")
async def get_file_counts():
    """Get file counts from data directories."""
    # These paths should match your actual data directories
    # Using the same paths as in the scheduler
    data_raw = QTSW2_ROOT.parent / "QTSW" / "data_raw"
    data_processed = QTSW2_ROOT.parent / "QTSW" / "data_processed"
    
    raw_count = len(list(data_raw.glob("*.csv"))) if data_raw.exists() else 0
    processed_count = (
        len(list(data_processed.glob("*.parquet"))) + 
        len(list(data_processed.glob("*.csv")))
    ) if data_processed.exists() else 0
    
    return {
        "raw_files": raw_count,
        "processed_files": processed_count
    }


if __name__ == "__main__":
    uvicorn.run(app, host="0.0.0.0", port=8000)

