"""
WebSocket endpoints for real-time event streaming - Using EventBus
"""
import asyncio
import logging
from typing import Optional
from fastapi import APIRouter, WebSocket, WebSocketDisconnect
from starlette.websockets import WebSocketState
import time
import json
from pathlib import Path

# Import configuration constants
try:
    from ..config import (
        SNAPSHOT_MAX_EVENTS,
        SNAPSHOT_WINDOW_HOURS,
        SNAPSHOT_CHUNK_SIZE,
        SNAPSHOT_LOAD_TIMEOUT,
    )
except ImportError:
    # Fallback if import fails
    from config import (
        SNAPSHOT_MAX_EVENTS,
        SNAPSHOT_WINDOW_HOURS,
        SNAPSHOT_CHUNK_SIZE,
        SNAPSHOT_LOAD_TIMEOUT,
    )

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
    
    # Start live event stream immediately (don't wait for snapshot)
    events_sent = 0
    logger.info(f"[WebSocket] Starting event stream immediately ({client_info})")
    
    # Load and send snapshot in background (non-blocking)
    async def send_snapshot_background():
        """Load and send snapshot in background without blocking live stream."""
        try:
            logger.info(f"[WebSocket] Snapshot task started, waiting 500ms ({client_info})")
            # Wait a moment to let other endpoints respond first (file counts, status, etc.)
            await asyncio.sleep(0.5)  # 500ms delay - allows other data to load first
            logger.info(f"[WebSocket] Snapshot task waking up, checking connection state: {websocket.client_state} ({client_info})")
            
            # Verify orchestrator is available
            if orchestrator is None or orchestrator.event_bus is None:
                logger.error(f"[WebSocket] Orchestrator or event_bus not available for snapshot ({client_info})")
                return
            
            # Snapshot: configurable window and max events
            # Pull from cached snapshot (threaded) so it returns instantly if fresh
            # Add timeout to prevent snapshot loading from hanging
            try:
                historical_events = await asyncio.wait_for(
                    asyncio.to_thread(
                        orchestrator.event_bus.get_snapshot_cached,
                        SNAPSHOT_WINDOW_HOURS,
                        SNAPSHOT_MAX_EVENTS,
                        True,
                        orchestrator.event_bus._snapshot_cache_ttl_seconds,
                    ),
                    timeout=SNAPSHOT_LOAD_TIMEOUT
                )
            except asyncio.TimeoutError:
                logger.warning(f"[WebSocket] Snapshot loading timed out after {SNAPSHOT_LOAD_TIMEOUT}s ({client_info})")
                return
            # Filter by run_id if provided
            if run_id:
                historical_events = [
                    e for e in historical_events 
                    if e.get("run_id") == run_id
                ]
            
            # Only send snapshot if we have events; trim to cap if oversized
            if historical_events and len(historical_events) > 0:
                logger.info(f"[WebSocket] Snapshot loaded: {len(historical_events)} events found ({client_info})")
                if len(historical_events) > SNAPSHOT_MAX_EVENTS:
                    # Keep most recent events up to max
                    historical_events = historical_events[-SNAPSHOT_MAX_EVENTS:]
                    logger.info(f"[WebSocket] Snapshot oversized -> trimmed to {SNAPSHOT_MAX_EVENTS} events ({client_info})")
                
                logger.info(f"[WebSocket] Starting to send snapshot: {len(historical_events)} events in chunks ({client_info})")

                # Send newest -> oldest chunks (UI sorts, so this surfaces recent first)
                historical_events = list(reversed(historical_events))

                # Stream snapshot in chunks to keep UI responsive
                # Chunk size configured for optimal performance
                chunk_size = SNAPSHOT_CHUNK_SIZE
                total_events = len(historical_events)
                total_chunks = (total_events + chunk_size - 1) // chunk_size

                if websocket.client_state == WebSocketState.CONNECTED:
                    logger.info(f"[WebSocket] WebSocket connected, sending {total_chunks} chunks ({client_info})")
                    for idx in range(total_chunks):
                        # Check connection state before each chunk - cancel if disconnected
                        if websocket.client_state != WebSocketState.CONNECTED:
                            logger.info(f"[WebSocket] Connection closed during snapshot send, cancelling remaining chunks ({client_info})")
                            break
                        
                        chunk = historical_events[idx * chunk_size:(idx + 1) * chunk_size]
                        if not chunk:
                            continue
                        try:
                            await websocket.send_json({
                                "type": "snapshot_chunk",
                                "window_hours": SNAPSHOT_WINDOW_HOURS,
                                "max_events": SNAPSHOT_MAX_EVENTS,
                                "chunk_index": idx,
                                "total_chunks": total_chunks,
                                "events": chunk,
                            })
                            # Small delay between chunks to keep UI responsive
                            await asyncio.sleep(0.05)  # Reduced from 0.1s since chunks are larger
                        except Exception as chunk_err:
                            # Check if error is due to disconnection
                            error_msg = str(chunk_err).lower()
                            if "not connected" in error_msg or "closed" in error_msg or "disconnect" in error_msg:
                                logger.info(f"[WebSocket] Connection closed while sending chunk {idx}/{total_chunks}, cancelling ({client_info})")
                                break
                            logger.error(f"[WebSocket] Error sending chunk {idx}/{total_chunks}: {chunk_err} ({client_info})")
                            break
                    
                    # Final marker (only if still connected)
                    if websocket.client_state == WebSocketState.CONNECTED:
                        try:
                            await websocket.send_json({
                                "type": "snapshot_done",
                                "window_hours": SNAPSHOT_WINDOW_HOURS,
                                "max_events": SNAPSHOT_MAX_EVENTS,
                                "total_events": total_events,
                                "total_chunks": total_chunks,
                            })
                            logger.info(f"[WebSocket] Sent snapshot in {total_chunks} chunks ({total_events} events) ({client_info})")
                        except Exception as done_err:
                            logger.error(f"[WebSocket] Error sending snapshot_done: {done_err} ({client_info})")
                else:
                    logger.warning(f"[WebSocket] WebSocket not connected (state: {websocket.client_state}), skipping snapshot ({client_info})")
            else:
                logger.warning(f"[WebSocket] No events in snapshot - skipping ({client_info})")

        except Exception as e:
            # Snapshot failed - log but continue (live stream already running)
            logger.error(f"[WebSocket] Snapshot send failed ({client_info}): {e}", exc_info=True)
    
    # Load snapshot in background (non-blocking) - small and fast
    # Live events stream immediately while snapshot loads
    snapshot_task = asyncio.create_task(send_snapshot_background())
    logger.info(f"[WebSocket] Snapshot task created and started - live events streaming immediately ({client_info})")
    
    # Add done callback to log if task completes or fails
    def snapshot_task_done(task):
        try:
            if task.exception():
                logger.error(f"[WebSocket] Snapshot task failed with exception ({client_info}): {task.exception()}", exc_info=task.exception())
            else:
                logger.info(f"[WebSocket] Snapshot task completed successfully ({client_info})")
        except Exception as e:
            logger.error(f"[WebSocket] Error checking snapshot task status ({client_info}): {e}")
    
    snapshot_task.add_done_callback(snapshot_task_done)
    
    try:
        # Stream events forever - never disconnect
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
        # Cancel snapshot task if still running
        if 'snapshot_task' in locals() and not snapshot_task.done():
            snapshot_task.cancel()
            try:
                await snapshot_task
            except asyncio.CancelledError:
                pass
        
        # Log connection close with all available context
        events_count = events_sent if 'events_sent' in locals() else 0
        logger.info(
            f"[WebSocket] Connection closed ({client_info}, events_sent: {events_count})"
        )


@router.websocket("/ws/events/{run_id}")
async def websocket_events_by_run(websocket: WebSocket, run_id: str):
    """WebSocket endpoint for specific run ID."""
    await websocket_events(websocket, run_id=run_id)

