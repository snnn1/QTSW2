"""
Schedule management endpoints - Windows Task Scheduler control
"""
import json
import logging
import subprocess
import os
from pathlib import Path
from datetime import datetime, timedelta
from fastapi import APIRouter, HTTPException
from typing import Optional
import pytz

try:
    from ..config import SCHEDULE_CONFIG_FILE, QTSW2_ROOT, load_schedule_config, save_schedule_config
    from ..models import ScheduleConfig
except ImportError:
    # Fallback for direct execution
    import sys
    from pathlib import Path
    backend_path = Path(__file__).parent.parent
    if str(backend_path) not in sys.path:
        sys.path.insert(0, str(backend_path))
    from config import SCHEDULE_CONFIG_FILE, QTSW2_ROOT, load_schedule_config, save_schedule_config
    from models import ScheduleConfig

router = APIRouter(prefix="/api", tags=["schedule"])
logger = logging.getLogger(__name__)


@router.get("/schedule", response_model=ScheduleConfig)
async def get_schedule():
    """Get current scheduled daily run time."""
    return load_schedule_config()


@router.post("/schedule", response_model=ScheduleConfig)
async def update_schedule(config: ScheduleConfig):
    """
    Update scheduled daily run time.
    Note: This is kept for compatibility but Windows Task Scheduler runs every 15 minutes.
    """
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
        logger.info(f"Schedule config updated to: {config.schedule_time} (Windows Task Scheduler runs every 15 minutes)")
        return config
    except Exception as e:
        logger.error(f"Failed to save schedule config: {e}", exc_info=True)
        raise HTTPException(
            status_code=500,
            detail="Failed to save schedule. Please check logs for details."
        )


@router.post("/scheduler/enable")
async def enable_scheduler():
    """Enable Windows Task Scheduler automation."""
    try:
        from ..main import orchestrator_instance
    except ImportError:
        import sys
        from pathlib import Path
        backend_path = Path(__file__).parent.parent
        if str(backend_path) not in sys.path:
            sys.path.insert(0, str(backend_path))
        from main import orchestrator_instance
    
    if orchestrator_instance is None or orchestrator_instance.scheduler is None:
        raise HTTPException(
            status_code=503,
            detail="Orchestrator not available"
        )
    
    try:
        success, error_msg = orchestrator_instance.scheduler.enable(changed_by="dashboard")
        if success:
            # Publish scheduler event to EventBus
            try:
                await orchestrator_instance.event_bus.publish({
                    "run_id": None,  # No specific run_id for scheduler lifecycle events
                    "stage": "scheduler",
                    "event": "enabled",
                    "timestamp": datetime.now().isoformat(),
                    "msg": "Windows Task Scheduler automation enabled",
                    "data": {
                        "changed_by": "dashboard"
                    }
                })
            except Exception as e:
                logger.warning(f"Failed to publish scheduler enabled event: {e}")
            # Optional: Open Windows Task Scheduler GUI when automation is enabled
            # Commented out - uncomment if you want it to open automatically
            # try:
            #     if os.name == 'nt':  # Windows only
            #         subprocess.Popen(['taskschd.msc'], shell=True)
            #         logger.info("Opened Windows Task Scheduler GUI")
            # except Exception as e:
            #     logger.warning(f"Failed to open Task Scheduler GUI: {e}")
            
            # Optional: Open scheduler activity terminal window
            # Commented out - uncomment if you want it to open automatically
            # try:
            #     if os.name == 'nt':  # Windows only
            #         script_path = QTSW2_ROOT / "batch" / "VIEW_SCHEDULER_TERMINAL_LIVE.bat"
            #         if script_path.exists():
            #             subprocess.Popen(
            #                 ['cmd', '/c', 'start', 'cmd', '/k', str(script_path)],
            #                 shell=True,
            #                 cwd=str(QTSW2_ROOT)
            #             )
            #             logger.info(f"Opened scheduler activity terminal: {script_path}")
            # except Exception as e:
            #     logger.warning(f"Failed to open scheduler terminal: {e}", exc_info=True)
            
            return {
                "enabled": True,
                "status": "enabled",
                "message": "Windows Task Scheduler automation enabled"
            }
        else:
            # Use the detailed error message from scheduler
            detail = error_msg or "Failed to enable Windows Task Scheduler. Check backend logs for details."
            raise HTTPException(
                status_code=500,
                detail=detail
            )
    except HTTPException:
        raise
    except Exception as e:
        logger.error(f"Error in enable_scheduler endpoint: {e}", exc_info=True)
        raise HTTPException(
            status_code=500,
            detail=f"Failed to enable scheduler: {str(e)}"
        )


@router.post("/scheduler/disable")
async def disable_scheduler():
    """Disable Windows Task Scheduler automation."""
    try:
        from ..main import orchestrator_instance
    except ImportError:
        import sys
        from pathlib import Path
        backend_path = Path(__file__).parent.parent
        if str(backend_path) not in sys.path:
            sys.path.insert(0, str(backend_path))
        from main import orchestrator_instance
    
    if orchestrator_instance is None or orchestrator_instance.scheduler is None:
        raise HTTPException(
            status_code=503,
            detail="Orchestrator not available"
        )
    
    try:
        success, error_msg = orchestrator_instance.scheduler.disable(changed_by="dashboard")
        if success:
            # Publish scheduler event to EventBus
            try:
                await orchestrator_instance.event_bus.publish({
                    "run_id": None,  # No specific run_id for scheduler lifecycle events
                    "stage": "scheduler",
                    "event": "disabled",
                    "timestamp": datetime.now().isoformat(),
                    "msg": "Windows Task Scheduler automation disabled",
                    "data": {
                        "changed_by": "dashboard"
                    }
                })
            except Exception as e:
                logger.warning(f"Failed to publish scheduler disabled event: {e}")
            return {
                "enabled": False,
                "status": "disabled",
                "message": "Windows Task Scheduler automation disabled"
            }
        else:
            # Use the detailed error message from scheduler
            detail = error_msg or "Failed to disable Windows Task Scheduler. Check backend logs for details."
            raise HTTPException(
                status_code=500,
                detail=detail
            )
    except HTTPException:
        raise
    except Exception as e:
        logger.error(f"Error in disable_scheduler endpoint: {e}", exc_info=True)
        raise HTTPException(
            status_code=500,
            detail=f"Failed to disable scheduler: {str(e)}"
        )


@router.get("/scheduler/status")
async def get_scheduler_status():
    """Get Windows Task Scheduler status."""
    try:
        from ..main import orchestrator_instance
    except ImportError:
        import sys
        from pathlib import Path
        backend_path = Path(__file__).parent.parent
        if str(backend_path) not in sys.path:
            sys.path.insert(0, str(backend_path))
        from main import orchestrator_instance
    
    if orchestrator_instance is None or orchestrator_instance.scheduler is None:
        return {
            "enabled": False,
            "status": "unknown",
            "message": "Orchestrator not available"
        }
    
    state = orchestrator_instance.scheduler.get_state()
    windows_status = state.get("windows_task_status", {})
    
    # Use authoritative enabled status from Windows Task Scheduler
    # Call is_enabled() which directly queries Windows Task Scheduler
    enabled = orchestrator_instance.scheduler.is_enabled()
    exists = windows_status.get("exists", None)
    
    if exists is False:
        message = "Windows Task Scheduler task not found. Run setup script to create it."
    elif enabled:
        message = "Windows Task Scheduler automation is enabled (runs every 15 minutes)"
    else:
        message = "Windows Task Scheduler automation is disabled"
    
    return {
        "enabled": enabled,
        "status": "enabled" if enabled else "disabled",
        "windows_task_exists": exists,
        "windows_task_enabled": windows_status.get("enabled", False),
        "windows_task_state": windows_status.get("state", "Unknown"),
        "last_changed": state.get("last_changed_timestamp"),
        "last_changed_by": state.get("last_changed_by"),
        "message": message
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

