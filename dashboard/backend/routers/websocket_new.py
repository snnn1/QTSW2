"""
WebSocket endpoints for real-time event streaming - Using EventBus
"""
import asyncio
import logging
from typing import Optional
from fastapi import APIRouter, WebSocket, WebSocketDisconnect

from ..main import orchestrator_instance

router = APIRouter(tags=["websocket"])
logger = logging.getLogger(__name__)


@router.websocket("/ws/events")
async def websocket_events(websocket: WebSocket, run_id: Optional[str] = None):
    """WebSocket endpoint for real-time event streaming."""
    if orchestrator_instance is None:
        await websocket.close(code=503, reason="Orchestrator not available")
        return
    
    await websocket.accept()
    logger.info(f"WebSocket connected (run_id: {run_id[:8] if run_id else None})")
    
    try:
        # Send initial snapshot
        try:
            snapshot = await orchestrator_instance.get_snapshot()
            await websocket.send_json({
                "type": "snapshot",
                "data": snapshot
            })
        except Exception as e:
            logger.error(f"Error sending snapshot: {e}")
        
        # Subscribe to EventBus
        async for event in orchestrator_instance.event_bus.subscribe():
            try:
                await websocket.send_json(event)
            except Exception as e:
                logger.debug(f"Error sending event: {e}")
                break
    
    except WebSocketDisconnect:
        logger.debug("WebSocket disconnected")
    except Exception as e:
        logger.error(f"WebSocket error: {e}")
    finally:
        logger.debug("WebSocket connection closed")


@router.websocket("/ws/events/{run_id}")
async def websocket_events_by_run(websocket: WebSocket, run_id: str):
    """WebSocket endpoint for specific run ID."""
    await websocket_events(websocket, run_id=run_id)

