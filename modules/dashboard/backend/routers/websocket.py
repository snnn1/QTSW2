"""
WebSocket endpoints for real-time event streaming - Using EventBus
"""
import asyncio
import logging
from typing import Optional
from fastapi import APIRouter, WebSocket, WebSocketDisconnect
from starlette.websockets import WebSocketState

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
    client_info = f"run_id: {run_id[:8] if run_id else 'all'}"
    try:
        await websocket.accept()
        logger.info(f"[WebSocket] Connection accepted ({client_info})")
    except Exception as e:
        logger.error(f"[WebSocket] Failed to accept connection ({client_info}): {e}", exc_info=True)
        return
    
    # Get orchestrator instance dynamically
    orchestrator = get_orchestrator()
    if orchestrator is None:
        # ADDITION 2: Backend never closes WebSocket proactively EXCEPT:
        # - Client disconnects (handled by WebSocketDisconnect exception)
        # - Server shutting down (handled by FastAPI lifespan)
        # - Fatal internal exception (this case - orchestrator not available is fatal)
        logger.error(f"[WebSocket] Fatal error - orchestrator not available ({client_info})")
        await websocket.close(code=1011, reason="Orchestrator not available")
        return
    logger.info(f"[WebSocket] Connection established ({client_info})")
    
    # Phase-1 always-on: WebSocket stays connected forever, streams events whenever they occur
    # Optionally send snapshot, but don't block events waiting for it
    
    try:
        # Optionally send snapshot (non-blocking, best-effort, reduced size for performance)
        # Phase-1: Snapshot is optional - if it's slow, skip it and just stream live events
        try:
            # Reduced snapshot: last 15 minutes, max 500 events (much faster than 1 hour, 10k events)
            historical_events = orchestrator.event_bus.load_jsonl_events_since(hours=0.25, max_events=500, exclude_verbose=True)
            
            # Filter by run_id if provided
            if run_id:
                historical_events = [
                    e for e in historical_events 
                    if e.get("run_id") == run_id
                ]
            
            # Only send snapshot if we have events and it's not too large
            if historical_events and len(historical_events) <= 500:
                await websocket.send_json({
                    "type": "snapshot",
                    "window_hours": 0.25,
                    "max_events": 500,
                    "events": historical_events
                })
                logger.info(f"[WebSocket] Sent {len(historical_events)} events from snapshot ({client_info})")
            else:
                logger.debug(f"[WebSocket] Skipping snapshot - too many events ({len(historical_events) if historical_events else 0}) ({client_info})")
        except Exception as e:
            # Snapshot failed - log but continue to live events (this is expected if files are large)
            logger.debug(f"[WebSocket] Snapshot send failed ({client_info}): {e} - continuing to live events")
        
        # Stream events forever - never disconnect
        events_sent = 0
        logger.info(f"[WebSocket] Starting event stream ({client_info})")
        async for event in orchestrator.event_bus.subscribe():
            # Check connection state before sending
            if websocket.client_state != WebSocketState.CONNECTED:
                logger.info(f"[WebSocket] Connection not in CONNECTED state ({client_info}): {websocket.client_state}")
                break
            
            try:
                # Filter events by run_id if specified
                if run_id is None or event.get("run_id") == run_id:
                    await websocket.send_json(event)
                    events_sent += 1
                    # Log first few events and then every 100
                    if events_sent <= 5 or events_sent % 100 == 0:
                        logger.info(f"[WebSocket] Sent event #{events_sent} ({client_info}): {event.get('stage', 'unknown')}/{event.get('event', 'unknown')}")
            except WebSocketDisconnect:
                # Client disconnected - this is normal
                logger.info(f"[WebSocket] Client disconnected ({client_info}, events_sent: {events_sent})")
                break
            except Exception as e:
                # Any other error - check if connection is closed, otherwise continue
                error_msg = str(e).lower()
                if "not connected" in error_msg or "closed" in error_msg or "disconnect" in error_msg:
                    logger.info(f"[WebSocket] Connection closed ({client_info}, events_sent: {events_sent}): {e}")
                    break
                else:
                    # Temporary error - log and continue
                    logger.warning(f"[WebSocket] Error sending event ({client_info}): {e} - continuing")
                    # Continue to next event - don't break connection
    
    except WebSocketDisconnect:
        # Client disconnected - log with details
        # WebSocketDisconnect is raised when client closes connection
        logger.info(
            f"[WebSocket] Client disconnected ({client_info}, "
            f"events_sent: {events_sent if 'events_sent' in locals() else 0})"
        )
    except Exception as e:
        # Unexpected error - log with details but don't close socket proactively
        error_type = type(e).__name__
        error_msg = str(e)
        events_count = events_sent if 'events_sent' in locals() else 0
        logger.error(
            f"[WebSocket] Unexpected error ({client_info}): "
            f"type={error_type}, message={error_msg}, events_sent={events_count}",
            exc_info=True
        )
        # Don't close socket - let client disconnect naturally
    finally:
        # Log connection close with all available context
        events_count = events_sent if 'events_sent' in locals() else 0
        logger.info(
            f"[WebSocket] Connection closed ({client_info}, events_sent: {events_count})"
        )


@router.websocket("/ws/events/{run_id}")
async def websocket_events_by_run(websocket: WebSocket, run_id: str):
    """WebSocket endpoint for specific run ID."""
    await websocket_events(websocket, run_id=run_id)

