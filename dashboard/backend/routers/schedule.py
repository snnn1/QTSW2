"""
Schedule management endpoints
"""
import json
import os
import logging
from datetime import datetime, timedelta
from fastapi import APIRouter, HTTPException
from typing import Optional
import pytz
import subprocess

from ..config import SCHEDULE_CONFIG_FILE, SCHEDULER_SCRIPT, QTSW2_ROOT, load_schedule_config, save_schedule_config
from ..models import ScheduleConfig

router = APIRouter(prefix="/api", tags=["schedule"])
logger = logging.getLogger(__name__)

# Global scheduler process (will be set from main.py)
scheduler_process: Optional[subprocess.Popen] = None

def set_scheduler_process(process: Optional[subprocess.Popen]):
    """Set the scheduler process reference."""
    global scheduler_process
    scheduler_process = process


@router.get("/schedule", response_model=ScheduleConfig)
async def get_schedule():
    """Get current scheduled daily run time."""
    return load_schedule_config()


@router.post("/schedule", response_model=ScheduleConfig)
async def update_schedule(config: ScheduleConfig):
    """Update scheduled daily run time."""
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
        
        # Reload orchestrator scheduler if available
        try:
            from ..main import orchestrator_instance
        except ImportError:
            import sys
            from pathlib import Path
            backend_path = Path(__file__).parent.parent
            if str(backend_path) not in sys.path:
                sys.path.insert(0, str(backend_path))
            from main import orchestrator_instance
        if orchestrator_instance and orchestrator_instance.scheduler:
            orchestrator_instance.scheduler.reload()
            logger.info("Orchestrator scheduler reloaded with new schedule")
        
        return config
    except Exception as e:
        logger.error(f"Failed to save schedule config: {e}", exc_info=True)
        raise HTTPException(
            status_code=500,
            detail="Failed to save schedule. Please check logs for details."
        )


@router.post("/scheduler/start")
async def start_scheduler():
    """Start the scheduler process."""
    global scheduler_process
    logger = logging.getLogger(__name__)
    
    # Check if already running
    if scheduler_process is not None and scheduler_process.poll() is None:
        return {
            "running": True,
            "status": "already_running",
            "pid": scheduler_process.pid,
            "message": "Scheduler is already running"
        }
    
    # Load schedule config to get the scheduled time
    try:
        schedule_config = load_schedule_config()
        schedule_time = schedule_config.schedule_time
    except Exception as e:
        logger.warning(f"Could not load schedule config, using default 07:30: {e}")
        schedule_time = "07:30"
    
    # Start scheduler process
    try:
        env = os.environ.copy()
        cmd = ["python", str(SCHEDULER_SCRIPT), "--schedule", schedule_time, "--no-debug-window"]
        
        scheduler_process = subprocess.Popen(
            cmd,
            cwd=str(QTSW2_ROOT),
            env=env,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE
        )
        
        logger.info(f"Scheduler started with PID {scheduler_process.pid}, schedule time: {schedule_time}")
        return {
            "running": True,
            "status": "started",
            "pid": scheduler_process.pid,
            "schedule_time": schedule_time,
            "message": f"Scheduler started (runs at {schedule_time} CT)"
        }
    except Exception as e:
        logger.error(f"Failed to start scheduler: {e}", exc_info=True)
        raise HTTPException(
            status_code=500,
            detail=f"Failed to start scheduler: {str(e)}"
        )


@router.get("/scheduler/status")
async def get_scheduler_status():
    """Get scheduler process status."""
    if scheduler_process is None:
        return {
            "running": False,
            "status": "not_started",
            "message": "Scheduler process not started"
        }
    
    # Check if process is still running
    if scheduler_process.poll() is None:
        return {
            "running": True,
            "status": "running",
            "pid": scheduler_process.pid,
            "message": "Scheduler is running (runs every 15 minutes)"
        }
    else:
        return {
            "running": False,
            "status": "stopped",
            "returncode": scheduler_process.returncode,
            "message": f"Scheduler process stopped (exit code: {scheduler_process.returncode})"
        }


@router.get("/schedule/next")
async def get_next_scheduled_run():
    """Get next scheduled run time (runs every 15 minutes)."""
    try:
        chicago_tz = pytz.timezone("America/Chicago")
        now_chicago = datetime.now(chicago_tz)
        
        # Calculate next 15-minute interval (:00, :15, :30, :45)
        current_minute = now_chicago.minute
        current_second = now_chicago.second
        current_microsecond = now_chicago.microsecond
        
        # Check if we're exactly at a 15-minute mark
        is_at_15min_mark = (current_minute % 15 == 0) and current_second == 0 and current_microsecond == 0
        
        if is_at_15min_mark:
            # We're exactly at a 15-minute mark, next run is in 15 minutes
            next_run = now_chicago + timedelta(minutes=15)
        else:
            # Calculate minutes until next 15-minute mark
            minutes_until_next = 15 - (current_minute % 15)
            next_run = now_chicago + timedelta(minutes=minutes_until_next)
            # Set seconds and microseconds to 0
            next_run = next_run.replace(second=0, microsecond=0)
        
        # Calculate wait time
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
        logger.error(f"Error calculating next run time: {e}")
        return {
            "error": str(e),
            "interval": "15 minutes",
            "runs_all_day": True
        }

