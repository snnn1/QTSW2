"""
WebSocket endpoints for real-time event streaming - Using EventBus
"""
import asyncio
import logging
from typing import Optional
from fastapi import APIRouter, WebSocket, WebSocketDisconnect

# Import orchestrator_instance dynamically - handle both relative and absolute imports
try:
    from ..main import orchestrator_instance
    def get_orchestrator():
        from ..main import orchestrator_instance
        return orchestrator_instance
except ImportError:
    import sys
    from pathlib import Path
    # Add parent to path
    backend_path = Path(__file__).parent.parent
    if str(backend_path) not in sys.path:
        sys.path.insert(0, str(backend_path))
    from main import orchestrator_instance
    def get_orchestrator():
        from main import orchestrator_instance
        return orchestrator_instance

router = APIRouter(tags=["websocket"])
logger = logging.getLogger(__name__)


@router.websocket("/ws/events")
async def websocket_events(websocket: WebSocket, run_id: Optional[str] = None):
    """WebSocket endpoint for real-time event streaming."""
    # Accept WebSocket connection (FastAPI handles origin checking)
    try:
        await websocket.accept()
    except Exception as e:
        logger.error(f"Failed to accept WebSocket: {e}")
        return
    
    # Get orchestrator instance dynamically
    orchestrator = get_orchestrator()
    if orchestrator is None:
        # Use valid WebSocket close code (1011 = internal error)
        await websocket.close(code=1011, reason="Orchestrator not available")
        return
    logger.info(f"WebSocket connected (run_id: {run_id[:8] if run_id else None})")
    
    try:
        # Send initial snapshot
        try:
            snapshot = await orchestrator.get_snapshot()
            await websocket.send_json({
                "type": "snapshot",
                "data": snapshot
            })
        except Exception as e:
            logger.error(f"Error sending snapshot: {e}")
        
        # Subscribe to EventBus
        async for event in orchestrator.event_bus.subscribe():
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

