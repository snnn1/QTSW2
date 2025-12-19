"""
Schedule metadata and Windows Task Scheduler integration endpoints.

This module does NOT:
- Execute or schedule pipeline runs (Windows Task Scheduler does this)
- Own timing logic or run loops
- Control when runs occur

This module ONLY:
- Reads/writes schedule.json (metadata storage)
- Enables/disables Windows Task Scheduler task (advisory control)
- Shows observed scheduler health and expected next run time
- Verifies Windows Task Scheduler task exists

Execution authority: Windows Task Scheduler (external system)
This module is advisory/observational only.
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
    """
    Get schedule metadata from schedule.json.
    
    Note: This is metadata only. Windows Task Scheduler controls actual execution timing.
    Windows Task Scheduler runs every 15 minutes regardless of this config.
    """
    return load_schedule_config()


@router.post("/schedule", response_model=ScheduleConfig)
async def update_schedule(config: ScheduleConfig):
    """
    Update schedule metadata in schedule.json.
    
    Note: This only updates metadata. Windows Task Scheduler controls actual execution timing.
    Windows Task Scheduler runs every 15 minutes regardless of this config.
    This endpoint is kept for compatibility with UI that may display schedule preferences.
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
    """
    Enable Windows Task Scheduler task (advisory control).
    
    This does NOT schedule or execute runs - it only enables the external Windows Task Scheduler task.
    Execution authority remains with Windows Task Scheduler.
    """
    logger.info("=" * 60)
    logger.info("SCHEDULER ENABLE ENDPOINT CALLED")
    logger.info("=" * 60)
    try:
        from ..main import orchestrator_instance
    except ImportError:
        import sys
        from pathlib import Path
        backend_path = Path(__file__).parent.parent
        if str(backend_path) not in sys.path:
            sys.path.insert(0, str(backend_path))
        from main import orchestrator_instance
    
    logger.info(f"Scheduler enable requested - orchestrator_instance={orchestrator_instance is not None}")
    
    if orchestrator_instance is None:
        logger.error("Orchestrator instance is None - orchestrator may have failed to start")
        raise HTTPException(
            status_code=503,
            detail="Orchestrator not available - check backend logs for startup errors"
        )
    
    logger.info(f"Scheduler object check - scheduler={orchestrator_instance.scheduler is not None}")
    if orchestrator_instance.scheduler is None:
        logger.error("Scheduler is None in orchestrator instance - scheduler initialization failed")
        raise HTTPException(
            status_code=503,
            detail="Scheduler not available - check backend logs for initialization errors"
        )
    
    try:
        logger.info("Calling scheduler.enable()...")
        success, error_msg = orchestrator_instance.scheduler.enable(changed_by="dashboard")
        logger.info(f"Scheduler enable result: success={success}, error_msg={error_msg if error_msg else 'None'}")
        if success:
            # Publish scheduler event to EventBus
            try:
                await orchestrator_instance.event_bus.publish({
                    "run_id": "__system__",  # System-level event (no specific run_id)
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
            
            # If it's a permission error, provide clear guidance
            if "Permission denied" in detail or "Access is denied" in detail:
                detail = (
                    "Permission denied: Windows Task Scheduler requires administrator privileges to enable/disable tasks. "
                    "SOLUTION: Run the backend as administrator - Right-click 'batch\\START_DASHBOARD.bat' and select 'Run as administrator'. "
                    "Alternatively, manually enable/disable the task in Task Scheduler (taskschd.msc)."
                )
            
            logger.error(f"Failed to enable scheduler: {detail}")
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
    """
    Disable Windows Task Scheduler task (advisory control).
    
    This does NOT stop scheduling - it only disables the external Windows Task Scheduler task.
    Execution authority remains with Windows Task Scheduler.
    """
    logger.info("=" * 60)
    logger.info("SCHEDULER DISABLE ENDPOINT CALLED")
    logger.info("=" * 60)
    try:
        from ..main import orchestrator_instance
    except ImportError:
        import sys
        from pathlib import Path
        backend_path = Path(__file__).parent.parent
        if str(backend_path) not in sys.path:
            sys.path.insert(0, str(backend_path))
        from main import orchestrator_instance
    
    if orchestrator_instance is None:
        logger.error("Orchestrator instance is None - orchestrator may have failed to start")
        raise HTTPException(
            status_code=503,
            detail="Orchestrator not available - check backend logs for startup errors"
        )
    
    if orchestrator_instance.scheduler is None:
        logger.error("Scheduler is None in orchestrator instance - scheduler initialization failed")
        raise HTTPException(
            status_code=503,
            detail="Scheduler not available - check backend logs for initialization errors"
        )
    
    try:
        logger.info(f"Orchestrator instance available: {orchestrator_instance is not None}")
        logger.info(f"Scheduler object available: {orchestrator_instance.scheduler is not None}")
        logger.info("Calling scheduler.disable()...")
        success, error_msg = orchestrator_instance.scheduler.disable(changed_by="dashboard")
        logger.info(f"Scheduler disable result: success={success}, error_msg={error_msg if error_msg else 'None'}")
        if success:
            # Publish scheduler event to EventBus
            try:
                await orchestrator_instance.event_bus.publish({
                    "run_id": "__system__",  # System-level event (no specific run_id)
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
    """
    Get scheduler health status inferred from observed events.
    
    This endpoint does NOT check backend state or "connection" status.
    It infers scheduler health by looking for scheduler/start events in the event history.
    
    Status meanings:
    - 🟢 Active: Scheduled runs observed recently (within expected cadence window)
    - 🟡 Stale: No scheduled run observed within expected window (15-20 minutes)
    - 🔴 Unknown: No historical scheduled runs ever observed
    """
    logger.debug("[SCHEDULER API] Status endpoint called (event-based inference)")
    try:
        from ..main import orchestrator_instance
        from datetime import datetime, timedelta
    except ImportError:
        import sys
        from pathlib import Path
        backend_path = Path(__file__).parent.parent
        if str(backend_path) not in sys.path:
            sys.path.insert(0, str(backend_path))
        from main import orchestrator_instance
        from datetime import datetime, timedelta
    
    if orchestrator_instance is None:
        logger.error("Orchestrator instance is None - orchestrator may have failed to start")
        return {
            "status": "unknown",
            "health": "unknown",
            "message": "Orchestrator not available - check backend logs for startup errors",
            "windows_task_exists": None,
            "windows_task_enabled": None
        }
    
    if orchestrator_instance.scheduler is None:
        logger.error("Scheduler is None in orchestrator instance - scheduler initialization failed")
        return {
            "status": "unknown",
            "health": "unknown",
            "message": "Scheduler not available - check backend logs for initialization errors",
            "windows_task_exists": None,
            "windows_task_enabled": None
        }
    
    # Get Windows Task Scheduler state (for reference, but not the primary health indicator)
    logger.debug("[SCHEDULER API] Getting scheduler state from orchestrator...")
    state = orchestrator_instance.scheduler.get_state()
    windows_status = state.get("windows_task_status", {})
    windows_task_enabled = windows_status.get("enabled", False)
    windows_task_exists = windows_status.get("exists", None)
    logger.debug(f"[SCHEDULER API] Windows task status: exists={windows_task_exists}, enabled={windows_task_enabled}, state={windows_status.get('state')}")
    
    # INFER HEALTH FROM OBSERVED EVENTS (not backend state)
    # Look for scheduler/start events in recent event history
    recent_events = orchestrator_instance.event_bus.get_recent_events(limit=500)
    
    # Filter for scheduler/start events (these indicate scheduled runs)
    scheduler_start_events = [
        e for e in recent_events 
        if e.get("stage") == "scheduler" and e.get("event") == "start"
    ]
    
    # Determine health based on most recent scheduler/start event
    now = datetime.now()
    
    health = "unknown"
    last_scheduled_run_time = None
    expected_next_run_time = None
    message = ""
    
    # Expected interval between scheduled runs (15 minutes in seconds)
    EXPECTED_INTERVAL_SECONDS = 15 * 60  # 900 seconds
    
    if scheduler_start_events:
        # Find the most recent scheduler/start event
        most_recent = max(scheduler_start_events, key=lambda e: e.get("timestamp", ""))
        last_scheduled_run_time = most_recent.get("timestamp")
        
        try:
            # Parse timestamp (handle both with and without timezone)
            timestamp_str = last_scheduled_run_time
            if timestamp_str.endswith("Z"):
                timestamp_str = timestamp_str.replace("Z", "+00:00")
            
            last_run_time = datetime.fromisoformat(timestamp_str)
            
            # Remove timezone for comparison if present
            if last_run_time.tzinfo:
                last_run_time_tz = last_run_time
                last_run_time = last_run_time.replace(tzinfo=None)
            else:
                last_run_time_tz = None
            
            time_since_last = (now - last_run_time).total_seconds()
            
            # Calculate expected next run time (last run + interval, rounded to next 15-minute mark)
            if last_run_time:
                # Calculate next 15-minute interval after last run
                # Runs occur at :00, :15, :30, :45 of each hour
                minutes = last_run_time.minute
                interval_slot = (minutes // 15) * 15
                next_slot_minutes = interval_slot + 15
                if next_slot_minutes >= 60:
                    # Next hour
                    expected_next_run_time_dt = last_run_time.replace(minute=0, second=0, microsecond=0) + timedelta(hours=1)
                else:
                    expected_next_run_time_dt = last_run_time.replace(minute=next_slot_minutes, second=0, microsecond=0)
                expected_next_run_time = expected_next_run_time_dt.isoformat()
            
            # Scheduler health: active if last_run < 2 × interval
            # This gives slack for runs that are slightly late
            health_threshold = 2 * EXPECTED_INTERVAL_SECONDS  # 30 minutes
            
            if time_since_last <= health_threshold:
                health = "active"
                minutes_ago = int(time_since_last / 60)
                message = f"Scheduled runs observed recently (last run {minutes_ago} minute{'s' if minutes_ago != 1 else ''} ago)"
            elif time_since_last <= 3600:  # 30-60 minutes ago
                health = "stale"
                minutes_ago = int(time_since_last / 60)
                message = f"No scheduled run observed recently (last run {minutes_ago} minutes ago, expected every 15 minutes)"
            else:  # > 60 minutes
                health = "stale"
                hours_ago = time_since_last / 3600
                message = f"Scheduled runs appear stopped (last run {hours_ago:.1f} hour{'s' if hours_ago > 1 else ''} ago)"
        except (ValueError, AttributeError, TypeError) as e:
            logger.warning(f"Failed to parse timestamp {last_scheduled_run_time}: {e}")
            health = "unknown"
            message = "Could not determine scheduler health from events"
            expected_next_run_time = None
    else:
        # No scheduler/start events ever observed
        health = "unknown"
        message = "No scheduled runs observed yet (may be disabled or not yet run)"
        expected_next_run_time = None
        last_scheduled_run_time = None
    
    # Map health to UI-friendly status (legacy compatibility)
    status = "enabled" if health == "active" else ("disabled" if health == "stale" else "unknown")
    
    response = {
        "status": status,  # Legacy: enabled/disabled/unknown (deprecated - use health)
        "health": health,  # active/stale/unknown (primary field for UI)
        "message": message,
        "enabled": windows_task_enabled,  # Direct Windows Task Scheduler enabled state (for UI toggle)
        "last_scheduled_run_time": last_scheduled_run_time,  # Explicit field name
        "expected_next_run_time": expected_next_run_time,  # Calculated expected next run
        "windows_task_exists": windows_task_exists,
        "windows_task_enabled": windows_task_enabled,  # Explicit Windows task enabled state
        "windows_task_state": windows_status.get("state", "Unknown"),
        "scheduler_enabled_setting": state.get("scheduler_enabled", False),  # What the setting is, not health
        "last_changed": state.get("last_changed_timestamp"),
        "last_changed_by": state.get("last_changed_by")
    }
    
    logger.debug(f"[SCHEDULER API] Status response: health={health}, enabled={windows_task_enabled}, last_run={last_scheduled_run_time}, scheduler_events={len(scheduler_start_events)}")
    
    return response


@router.get("/schedule/next")
async def get_next_scheduled_run():
    """
    Get expected next scheduled run time (calendar/expectation only).
    
    This calculates the expected next run based on the 15-minute cadence pattern.
    It does NOT schedule or control execution - Windows Task Scheduler does that.
    This is advisory information for display purposes only.
    """
    # Use DEBUG level to reduce log noise (polling endpoint)
    logger.debug("Next scheduled run requested")
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

