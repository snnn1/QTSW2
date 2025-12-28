"""
Pipeline management endpoints – thin API layer
"""

import logging
import asyncio
from datetime import datetime, timezone
from fastapi import APIRouter, HTTPException, Body
from pydantic import BaseModel
from typing import Optional, Dict

try:
    from ..models import PipelineStartRequest
except ImportError:
    from models import PipelineStartRequest, Dict

try:
    from modules.orchestrator.state import PipelineStage
except ImportError:
    from orchestrator.state import PipelineStage

router = APIRouter(prefix="/api/pipeline", tags=["pipeline"])
logger = logging.getLogger(__name__)


def get_orchestrator():
    try:
        from ..main import orchestrator_instance
    except ImportError:
        from main import orchestrator_instance
    return orchestrator_instance


# ---------------------------------------------------------------------
# Pipeline lifecycle
# ---------------------------------------------------------------------

@router.post("/start")
async def start_pipeline(request: PipelineStartRequest = PipelineStartRequest()):
    """
    Start a pipeline run.
    
    Request body (JSON, optional):
    {
        "manual": true/false  # true for manual runs, false for scheduled runs. Defaults to true.
    }
    
    Guarantees pipeline/start event is emitted BEFORE any work begins,
    allowing frontend to connect WebSocket first.
    """
    orchestrator = get_orchestrator()
    if not orchestrator:
        raise HTTPException(503, "Pipeline orchestrator not available")
    
    # Phase-1 always-on: Orchestrator is ready once instance exists (no subsystem blocking)
    # If orchestrator doesn't exist, that's a real error
    if not orchestrator:
        raise HTTPException(503, "Pipeline orchestrator not available")

    # Read manual flag from request (defaults to True for backward compatibility)
    manual = request.manual if request else True
    # Read manual_override flag (for policy gate - manual runs only)
    manual_override = getattr(request, 'manual_override', False) if request else False
    
    logger.info(f"[START] Starting pipeline: manual={manual}, manual_override={manual_override}")

    try:
        # Start the pipeline - orchestrator handles run_id generation and event emission
        # The API is a thin command layer - event emission belongs only in the orchestrator
        # Policy gate is enforced inside start_pipeline()
        run_ctx = await asyncio.wait_for(
            orchestrator.start_pipeline(manual=manual, manual_override=manual_override),
            timeout=5.0,  # should be instant
        )

        return {
            "run_id": run_ctx.run_id,
            "state": run_ctx.state.value,
            "message": "Pipeline started",
        }

    except asyncio.TimeoutError:
        raise HTTPException(
            504,
            "Pipeline start timeout (started in background)",
        )
    except ValueError as e:
        raise HTTPException(400, str(e))
    except Exception as e:
        logger.exception("Failed to start pipeline")
        raise HTTPException(500, str(e))


@router.post("/stop")
async def stop_pipeline():
    orchestrator = get_orchestrator()
    if not orchestrator:
        raise HTTPException(503, "Pipeline orchestrator not available")

    try:
        run_ctx = await orchestrator.stop_pipeline()
        return {
            "run_id": run_ctx.run_id,
            "state": run_ctx.state.value,
            "message": "Pipeline stopped",
        }
    except ValueError as e:
        raise HTTPException(400, str(e))
    except Exception as e:
        logger.exception("Failed to stop pipeline")
        raise HTTPException(500, str(e))


# ---------------------------------------------------------------------
# Status & snapshot
# ---------------------------------------------------------------------

@router.get("/runs")
async def list_runs(
    limit: int = 100,
    result: Optional[str] = None
):
    """
    List run summaries (first-class persisted artifacts).
    
    Query params:
    - limit: Maximum number of runs to return (default: 100)
    - result: Filter by result (success, failed, stopped)
    
    Returns:
        List of run summaries, most recent first
    """
    orchestrator = get_orchestrator()
    if not orchestrator:
        return {"runs": []}
    
    runs = orchestrator.run_history.list_runs(limit=limit, result_filter=result)
    
    return {
        "runs": [run.to_dict() for run in runs],
        "total": len(runs)
    }


@router.get("/runs/{run_id}")
async def get_run(run_id: str):
    """
    Get a specific run summary by run_id.
    
    Returns:
        Run summary or 404 if not found
    """
    orchestrator = get_orchestrator()
    if not orchestrator:
        raise HTTPException(status_code=404, detail="Orchestrator not available")
    
    run = orchestrator.run_history.get_run(run_id)
    if not run:
        raise HTTPException(status_code=404, detail=f"Run {run_id} not found")
    
    return run.to_dict()


@router.get("/status")
async def get_pipeline_status():
    orchestrator = get_orchestrator()
    if not orchestrator:
        return {"state": "unavailable"}

    # ADDITION 1: Canonical pipeline state - only 4 valid values: idle, running, stopped, error
    # No inferred states, no starting_up, ready, waiting, initializing
    from modules.orchestrator.state import canonical_state, PipelineRunState
    
    # CRITICAL: Read from single canonical state source (state_manager.get_state())
    # This ensures polling and WebSocket always agree on state
    status = await orchestrator.get_status()
    if status:
        # Map internal FSM state to canonical state
        canonical = canonical_state(status.state)
        # Return canonical state in same format as state_change events
        # This ensures consistency between polling and WebSocket
        return {
            "state": canonical,
            "run_id": status.run_id,
            "current_stage": status.current_stage.value if status.current_stage else None,
            "started_at": status.started_at.isoformat() if status.started_at else None,
            "updated_at": status.updated_at.isoformat() if status.updated_at else None,
            "error": status.error,
            "retry_count": status.retry_count,
            "metadata": status.metadata,
            # Include internal state for debugging (not used by frontend)
            "_internal_state": status.state.value,
        }
    else:
        return {"state": "idle"}


@router.get("/snapshot")
async def get_pipeline_snapshot():
    orchestrator = get_orchestrator()
    if not orchestrator:
        raise HTTPException(503, "Pipeline orchestrator not available")

    return await orchestrator.get_snapshot()


# ---------------------------------------------------------------------
# Stage execution
# ---------------------------------------------------------------------

@router.post("/stage/{stage_name}")
async def run_stage(stage_name: str):
    orchestrator = get_orchestrator()
    if not orchestrator:
        raise HTTPException(503, "Pipeline orchestrator not available")

    stage_map = {
        "translator": PipelineStage.TRANSLATOR,
        "analyzer": PipelineStage.ANALYZER,
        "merger": PipelineStage.MERGER,
    }

    if stage_name not in stage_map:
        raise HTTPException(
            400,
            f"Invalid stage: {stage_name}. "
            f"Expected one of {list(stage_map)}",
        )

    try:
        run_ctx = await orchestrator.run_single_stage(stage_map[stage_name])
        return {
            "run_id": run_ctx.run_id,
            "state": run_ctx.state.value,
            "stage": stage_name,
            "message": f"{stage_name} started",
        }
    except ValueError as e:
        raise HTTPException(400, str(e))
    except Exception as e:
        logger.exception("Stage run failed")
        raise HTTPException(500, str(e))


# ---------------------------------------------------------------------
# Maintenance
# ---------------------------------------------------------------------

@router.post("/reset")
async def reset_pipeline():
    orchestrator = get_orchestrator()
    if not orchestrator:
        raise HTTPException(503, "Pipeline orchestrator not available")

    try:
        # Get current status
        status = await orchestrator.get_status()
        
        # Try to release lock if there's a status with run_id
        if status and status.run_id:
            try:
                await orchestrator.lock_manager.release(status.run_id)
            except Exception as e:
                logger.warning(f"Failed to release lock for run {status.run_id}: {e}")
                # Continue with force clear
        
        # Force clear all locks (handles stale locks)
        await orchestrator.lock_manager.force_clear_all()
        
        # Clear pipeline state
        await orchestrator.state_manager.clear_run()

        return {"message": "Pipeline state reset"}
    except Exception as e:
        logger.exception("Pipeline reset failed")
        raise HTTPException(500, str(e))


@router.post("/clear-lock")
async def clear_pipeline_lock():
    """
    Force clear the pipeline lock file.
    Use with caution when pipeline is stuck with a lock error.
    """
    orchestrator = get_orchestrator()
    if not orchestrator:
        raise HTTPException(503, "Pipeline orchestrator not available")

    try:
        lock_info = await orchestrator.lock_manager.get_lock_info()
        success = await orchestrator.lock_manager.force_clear_all()
        if success:
            return {"message": "Pipeline lock cleared successfully", "previous_lock_info": lock_info}
        else:
            raise HTTPException(500, "Failed to clear pipeline lock")
    except Exception as e:
        logger.exception("Failed to clear pipeline lock")
        raise HTTPException(500, str(e))


# ---------------------------------------------------------------------
# Scheduler notifications
# ---------------------------------------------------------------------

class ScheduledRunNotification(BaseModel):
    run_id: str
    source: str = "windows_scheduler"


@router.post("/notify-scheduled-run")
async def notify_scheduled_run(notification: ScheduledRunNotification):
    """
    Notification endpoint for scheduled runs triggered by Windows Task Scheduler.
    Publishes a scheduler event to the EventBus so it appears in the live events log.
    
    This endpoint is informational, not authoritative. Duplicate notifications are ignored.
    """
    orchestrator = get_orchestrator()
    if not orchestrator:
        # Don't fail if orchestrator is not available - just log
        logger.warning("Received scheduled run notification but orchestrator not available")
        return {"message": "Notification received (orchestrator not available)"}

    try:
        run_id = notification.run_id
        source = notification.source
        
        # Protect against duplicates - if run_id already exists in state manager, NO-OP
        status = await orchestrator.get_status()
        if status and status.run_id == run_id:
            logger.info(f"📅 Scheduled run notification ignored (duplicate): {run_id} (source: {source})")
            return {
                "message": "Scheduled run notification received (duplicate, ignored)",
                "run_id": run_id,
                "event_published": False
            }
        
        # Publish scheduler event to EventBus
        await orchestrator.event_bus.publish({
            "run_id": run_id,
            "stage": "scheduler",
            "event": "scheduled_run_started",
            "timestamp": datetime.now(timezone.utc).isoformat(),
            "msg": f"Scheduled pipeline run started by {source}",
            "data": {
                "source": source,
                "run_id": run_id
            }
        })
        
        logger.info(f"📅 Scheduled run notification received: {run_id} (source: {source})")
        
        return {
            "message": "Scheduled run notification received",
            "run_id": run_id,
            "event_published": True
        }
    except Exception as e:
        logger.exception("Failed to process scheduled run notification")
        # Don't raise HTTPException - we don't want to fail the scheduled run
        return {
            "message": "Notification received but failed to publish event",
            "error": str(e)
        }


# ---------------------------------------------------------------------
# Direct event publishing (for run_pipeline_standalone.py)
# ---------------------------------------------------------------------

class PublishEventRequest(BaseModel):
    """Request model for publishing any event to EventBus"""
    run_id: str
    stage: str
    event: str
    timestamp: Optional[str] = None
    msg: Optional[str] = None
    data: Optional[Dict] = None


@router.post("/publish-event")
async def publish_event(request: PublishEventRequest = Body(...)):
    """
    Publish any pipeline event directly to the EventBus.
    
    This endpoint is used by run_pipeline_standalone.py to publish events
    (translator, analyzer, merger) directly to the backend's EventBus
    for real-time visibility in the dashboard.
    
    Returns:
        {"published": bool, "message": str}
    """
    orchestrator = get_orchestrator()
    if not orchestrator:
        return {
            "published": False,
            "message": "Orchestrator not available"
        }
    
    try:
        # Build event dict
        event = {
            "run_id": request.run_id,
            "stage": request.stage,
            "event": request.event,
            "timestamp": request.timestamp or datetime.now(timezone.utc).isoformat(),
        }
        if request.msg is not None:
            event["msg"] = request.msg
        if request.data is not None:
            event["data"] = request.data
        
        # Publish to EventBus
        await orchestrator.event_bus.publish(event)
        
        logger.info(f"[SUCCESS] Published {request.stage}/{request.event} event (run: {request.run_id[:8]})")
        
        return {
            "published": True,
            "message": f"Event {request.stage}/{request.event} published successfully"
        }
    except Exception as e:
        logger.error(f"Failed to publish event {request.stage}/{request.event}: {e}", exc_info=True)
        return {
            "published": False,
            "message": f"Failed to publish event: {str(e)}"
        }


@router.post("/publish-scheduler-event")
async def publish_scheduler_event(request: PublishEventRequest = Body(...)):
    """
    Publish a scheduler event directly to the EventBus.
    
    This endpoint is used by run_pipeline_standalone.py to publish scheduler events
    (scheduler/start, scheduler/success, scheduler/failed) directly to the backend's
    EventBus for real-time visibility in the dashboard.
    
    Returns:
        {"published": bool, "message": str}
    """
    orchestrator = get_orchestrator()
    if not orchestrator:
        return {
            "published": False,
            "message": "Orchestrator not available"
        }
    
    try:
        # Ensure stage is "scheduler"
        if request.stage != "scheduler":
            return {
                "published": False,
                "message": f"Invalid stage for scheduler event: {request.stage} (must be 'scheduler')"
            }
        
        # Build event dict
        event = {
            "run_id": request.run_id,
            "stage": "scheduler",
            "event": request.event,
            "timestamp": request.timestamp or datetime.now(timezone.utc).isoformat(),
        }
        if request.msg is not None:
            event["msg"] = request.msg
        if request.data is not None:
            event["data"] = request.data
        
        # Publish to EventBus
        await orchestrator.event_bus.publish(event)
        
        logger.info(f"[SUCCESS] Published scheduler/{request.event} event (run: {request.run_id[:8]})")
        
        return {
            "published": True,
            "message": f"Scheduler event {request.event} published successfully"
        }
    except Exception as e:
        logger.error(f"Failed to publish scheduler event {request.event}: {e}", exc_info=True)
        return {
            "published": False,
            "message": f"Failed to publish scheduler event: {str(e)}"
        }
