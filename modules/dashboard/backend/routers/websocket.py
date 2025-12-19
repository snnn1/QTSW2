"""
WebSocket endpoints for real-time event streaming - Using EventBus
"""
import asyncio
import logging
from typing import Optional
from fastapi import APIRouter, WebSocket, WebSocketDisconnect

# Lazy import to avoid circular dependency - only import when needed
def get_orchestrator():
    """Get orchestrator instance with lazy import to avoid circular dependencies."""
    try:
        # Try relative import first (when running as module)
        from ..main import orchestrator_instance
        return orchestrator_instance
    except (ImportError, ValueError):
        # Fallback to absolute import (when running as script)
        import sys
        from pathlib import Path
        # Add parent to path
        backend_path = Path(__file__).parent.parent
        if str(backend_path) not in sys.path:
            sys.path.insert(0, str(backend_path))
        from main import orchestrator_instance
        return orchestrator_instance

router = APIRouter(tags=["websocket"])
logger = logging.getLogger(__name__)


@router.websocket("/ws/events")
async def websocket_events(websocket: WebSocket, run_id: Optional[str] = None):
    """WebSocket endpoint for real-time event streaming.
    
    If run_id is provided, only events for that run_id are sent.
    If run_id is None, all events are sent (for admin/debugging).
    """
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
    logger.info(f"WebSocket connected (run_id: {run_id[:8] if run_id else 'all'})")
    
    try:
        # Send event snapshot (from JSONL files)
        # Load last 1 hour of events for dashboard on open
        # This is a UI convenience, not a data dump - JSONL files are the source of truth
        try:
            # Load events: last 1 hour, no max limit (show all events from last hour)
            # exclude_verbose=True filters out metric/progress events for cleaner UI snapshots
            historical_events = orchestrator.event_bus.load_jsonl_events_since(hours=1, max_events=10000, exclude_verbose=True)
            
            # Filter by run_id if provided
            if run_id:
                historical_events = [
                    e for e in historical_events 
                    if e.get("run_id") == run_id
                ]
            
            # Send snapshot with 1-hour window
            await websocket.send_json({
                "type": "snapshot",
                "window_hours": 1,
                "max_events": 10000,
                "events": historical_events
            })
            logger.debug(f"Sent {len(historical_events)} events from last 1 hour to WebSocket client")
        except Exception as e:
            logger.error(f"Error loading/sending 1-hour event snapshot: {e}", exc_info=True)
            # Send empty snapshot on error (don't block connection)
            await websocket.send_json({
                "type": "snapshot",
                "window_hours": 1,
                "events": []
            })
        
        # Subscribe to EventBus and filter by run_id if provided
        async for event in orchestrator.event_bus.subscribe():
            try:
                # Filter events by run_id if specified
                if run_id is None or event.get("run_id") == run_id:
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

