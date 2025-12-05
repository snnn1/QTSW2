"""
Pipeline management endpoints - Using Orchestrator
"""
import logging
from fastapi import APIRouter, HTTPException
from typing import Optional

from ..main import orchestrator_instance
from ..orchestrator import PipelineStage

router = APIRouter(prefix="/api/pipeline", tags=["pipeline"])
logger = logging.getLogger(__name__)


@router.post("/start")
async def start_pipeline(manual: bool = True):
    """
    Start a pipeline run immediately.
    """
    if orchestrator_instance is None:
        raise HTTPException(
            status_code=503,
            detail="Pipeline orchestrator not available"
        )
    
    try:
        run_ctx = await orchestrator_instance.start_pipeline(manual=manual)
        return {
            "run_id": run_ctx.run_id,
            "status": run_ctx.state.value,
            "message": "Pipeline started"
        }
    except ValueError as e:
        raise HTTPException(status_code=400, detail=str(e))
    except Exception as e:
        logger.error(f"Failed to start pipeline: {e}", exc_info=True)
        raise HTTPException(
            status_code=500,
            detail=f"Failed to start pipeline: {str(e)}"
        )


@router.get("/status")
async def get_pipeline_status():
    """
    Get current pipeline status.
    """
    if orchestrator_instance is None:
        return {"status": None, "message": "Orchestrator not available"}
    
    try:
        status = await orchestrator_instance.get_status()
        if status:
            return status.to_dict()
        return {"status": None, "message": "No active pipeline run"}
    except Exception as e:
        logger.error(f"Failed to get status: {e}", exc_info=True)
        raise HTTPException(
            status_code=500,
            detail=f"Failed to get status: {str(e)}"
        )


@router.get("/snapshot")
async def get_pipeline_snapshot():
    """
    Get pipeline snapshot (status + recent events + metrics).
    """
    if orchestrator_instance is None:
        return {"status": None, "events": [], "message": "Orchestrator not available"}
    
    try:
        snapshot = await orchestrator_instance.get_snapshot()
        return snapshot
    except Exception as e:
        logger.error(f"Failed to get snapshot: {e}", exc_info=True)
        raise HTTPException(
            status_code=500,
            detail=f"Failed to get snapshot: {str(e)}"
        )


@router.post("/stop")
async def stop_pipeline():
    """
    Stop current pipeline run.
    """
    if orchestrator_instance is None:
        raise HTTPException(
            status_code=503,
            detail="Pipeline orchestrator not available"
        )
    
    try:
        run_ctx = await orchestrator_instance.stop_pipeline()
        return {
            "run_id": run_ctx.run_id,
            "status": run_ctx.state.value,
            "message": "Pipeline stopped"
        }
    except ValueError as e:
        raise HTTPException(status_code=400, detail=str(e))
    except Exception as e:
        logger.error(f"Failed to stop pipeline: {e}", exc_info=True)
        raise HTTPException(
            status_code=500,
            detail=f"Failed to stop pipeline: {str(e)}"
        )


@router.post("/stage/{stage_name}")
async def run_stage(stage_name: str):
    """
    Run a specific pipeline stage.
    
    Args:
        stage_name: Name of the stage (translator, analyzer, merger)
    """
    if orchestrator_instance is None:
        raise HTTPException(
            status_code=503,
            detail="Pipeline orchestrator not available"
        )
    
    # Map stage name to enum
    stage_map = {
        "translator": PipelineStage.TRANSLATOR,
        "analyzer": PipelineStage.ANALYZER,
        "merger": PipelineStage.MERGER,
    }
    
    if stage_name not in stage_map:
        raise HTTPException(
            status_code=400,
            detail=f"Invalid stage name: {stage_name}. Must be one of: {list(stage_map.keys())}"
        )
    
    try:
        run_ctx = await orchestrator_instance.run_single_stage(stage_map[stage_name])
        return {
            "run_id": run_ctx.run_id,
            "status": run_ctx.state.value,
            "stage": stage_name,
            "message": f"Stage {stage_name} started"
        }
    except ValueError as e:
        raise HTTPException(status_code=400, detail=str(e))
    except Exception as e:
        logger.error(f"Failed to run stage: {e}", exc_info=True)
        raise HTTPException(
            status_code=500,
            detail=f"Failed to run stage: {str(e)}"
        )


@router.post("/reset")
async def reset_pipeline():
    """
    Reset pipeline state (clear current run).
    """
    if orchestrator_instance is None:
        raise HTTPException(
            status_code=503,
            detail="Pipeline orchestrator not available"
        )
    
    try:
        await orchestrator_instance.state_manager.clear_run()
        return {"message": "Pipeline state reset"}
    except Exception as e:
        logger.error(f"Failed to reset pipeline: {e}", exc_info=True)
        raise HTTPException(
            status_code=500,
            detail=f"Failed to reset pipeline: {str(e)}"
        )

