"""
Pipeline Dashboard Backend - Simplified Main File
FastAPI server for pipeline monitoring and control
"""
import os
import sys
import subprocess
import logging
from pathlib import Path
from typing import Optional
from datetime import datetime
from contextlib import asynccontextmanager

from fastapi import FastAPI, HTTPException
from fastapi.middleware.cors import CORSMiddleware
import uvicorn

# Import routers
from .routers import pipeline, schedule, websocket, apps, metrics, matrix

# Import config
from .config import (
    QTSW2_ROOT, SCHEDULER_SCRIPT, LOG_FILE_PATH, CORS_ORIGINS,
    load_schedule_config  # This function should be in config or schedule router
)

# Global variable to track scheduler process
scheduler_process: Optional[subprocess.Popen] = None


@asynccontextmanager
async def lifespan(app: FastAPI):
    """Lifespan event handler for startup and shutdown."""
    global scheduler_process
    logger = logging.getLogger(__name__)
    
    # Startup
    startup_msg = "=" * 80 + "\nFastAPI application started - ready to receive requests\n" + "=" * 80 + "\n"
    print(startup_msg, file=sys.stderr)
    sys.stderr.flush()
    
    # Write to log file
    with open(LOG_FILE_PATH, 'a', encoding='utf-8') as f:
        f.write(startup_msg)
        f.flush()
    logger.info("FastAPI application started - ready to receive requests")
    
    # Auto-start scheduler on backend startup
    try:
        schedule_config = load_schedule_config()
        schedule_time = schedule_config.schedule_time
        logger.info(f"Auto-starting scheduler with schedule time: {schedule_time}")
        
        env = os.environ.copy()
        cmd = ["python", str(SCHEDULER_SCRIPT), "--schedule", schedule_time, "--no-debug-window"]
        
        scheduler_process = subprocess.Popen(
            cmd,
            cwd=str(QTSW2_ROOT),
            env=env,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE
        )
        logger.info(f"Scheduler auto-started with PID {scheduler_process.pid}")
        
        # Set scheduler process in schedule router
        schedule.set_scheduler_process(scheduler_process)
    except Exception as e:
        logger.warning(f"Could not auto-start scheduler: {e}. You can start it manually via /api/scheduler/start")
    
    # Create master matrix debug log file on startup
    master_matrix_log = QTSW2_ROOT / "logs" / "master_matrix.log"
    master_matrix_log.parent.mkdir(parents=True, exist_ok=True)
    try:
        with open(master_matrix_log, 'a', encoding='utf-8') as f:
            f.write(f"[{datetime.now().strftime('%Y-%m-%d %H:%M:%S')}] Backend started - master matrix debug logging ready\n")
            f.flush()
    except:
        pass
    
    yield  # Application runs here
    
    # Shutdown
    if scheduler_process:
        try:
            logger.info("Stopping scheduler process...")
            scheduler_process.terminate()
            scheduler_process.wait(timeout=5)
            logger.info("Scheduler stopped")
        except Exception as e:
            logger.error(f"Error stopping scheduler: {e}")
            if scheduler_process:
                scheduler_process.kill()


# Create FastAPI app
app = FastAPI(title="Pipeline Dashboard API", lifespan=lifespan)

# Configure logging
logging.getLogger("uvicorn.access").setLevel(logging.WARNING)

# Create file handler for logging
file_handler = logging.FileHandler(LOG_FILE_PATH, mode='a', encoding='utf-8')
file_handler.setLevel(logging.INFO)
file_formatter = logging.Formatter('%(asctime)s - %(levelname)s - %(message)s')
file_handler.setFormatter(file_formatter)

# Add file handler to root logger
root_logger = logging.getLogger()
root_logger.addHandler(file_handler)
root_logger.setLevel(logging.INFO)

# CORS middleware
app.add_middleware(
    CORSMiddleware,
    allow_origins=CORS_ORIGINS,
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
)

# Include routers
app.include_router(pipeline.router)
app.include_router(schedule.router)
app.include_router(websocket.router)
app.include_router(apps.router)
app.include_router(metrics.router)
app.include_router(matrix.router)

# Basic endpoints
@app.get("/")
async def root():
    """Root endpoint."""
    return {"message": "Pipeline Dashboard API", "status": "running"}

@app.get("/health")
async def health_check():
    """Health check endpoint for monitoring"""
    return {
        "status": "healthy",
        "timestamp": datetime.now().isoformat(),
        "service": "Pipeline Dashboard API"
    }

# Note: Complex endpoints like /api/matrix/build and /api/timetable/generate
# are kept in the original main.py due to complex module reloading requirements.
# These can be moved later if needed.

if __name__ == "__main__":
    uvicorn.run("dashboard.backend.main:app", host="0.0.0.0", port=8000, reload=True)



