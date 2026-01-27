"""
WebSocket endpoints for watchdog real-time event streaming

Streams events from frontend_feed.jsonl to connected clients.
Watchdog backend is standalone - uses its own event feed, not dashboard's EventBus.
"""
import asyncio
import logging
import json
from typing import Optional, Dict, Tuple, List
from pathlib import Path
from fastapi import APIRouter, WebSocket, WebSocketDisconnect
from starlette.websockets import WebSocketState

from modules.watchdog.config import FRONTEND_FEED_FILE

router = APIRouter(tags=["websocket"])
logger = logging.getLogger(__name__)


def _read_feed_events_since(cursor_position: int = 0, max_events: int = 100) -> Tuple[List[Dict], int]:
    """
    Read events from frontend_feed.jsonl since cursor position.
    
    Returns:
        (events, new_cursor_position)
    """
    if not FRONTEND_FEED_FILE.exists():
        return [], 0
    
    events = []
    current_position = 0
    
    try:
        # Use utf-8-sig to handle UTF-8 BOM markers
        with open(FRONTEND_FEED_FILE, 'r', encoding='utf-8-sig') as f:
            # Seek to cursor position
            f.seek(cursor_position)
            
            # Read new lines
            parse_errors = 0
            for line in f:
                line = line.strip()
                if not line:
                    continue
                
                try:
                    event = json.loads(line)
                    events.append(event)
                    if len(events) >= max_events:
                        break
                except json.JSONDecodeError:
                    # Silently skip malformed JSON lines
                    parse_errors += 1
                    continue
            
            # Log parse errors only once per read, not per line
            if parse_errors > 0:
                logger.debug(f"Skipped {parse_errors} malformed JSON lines in feed file")
            
            # Get new cursor position
            current_position = f.tell()
    
    except Exception as e:
        logger.error(f"Error reading feed file {FRONTEND_FEED_FILE}: {e}")
    
    return events, current_position


async def _send_snapshot(websocket: WebSocket, client_info: str) -> int:
    """
    Send snapshot of recent events (last 100 events).
    Returns number of events sent.
    """
    try:
        # Read last 100 events from feed file
        if not FRONTEND_FEED_FILE.exists():
            return 0
        
        # Read entire file and get last 100 events
        all_events = []
        try:
            # Use utf-8-sig to handle UTF-8 BOM markers
            with open(FRONTEND_FEED_FILE, 'r', encoding='utf-8-sig') as f:
                for line in f:
                    line = line.strip()
                    if not line:
                        continue
                    try:
                        event = json.loads(line)
                        all_events.append(event)
                    except json.JSONDecodeError:
                        # Silently skip malformed JSON lines
                        continue
        except Exception as e:
            logger.error(f"Error reading feed file for snapshot: {e}")
            return 0
        
        # Get last 100 events
        snapshot_events = all_events[-100:] if len(all_events) > 100 else all_events
        
        if not snapshot_events:
            return 0
        
        # Send snapshot in chunks
        chunk_size = 25
        total_chunks = (len(snapshot_events) + chunk_size - 1) // chunk_size
        
        for idx in range(total_chunks):
            if websocket.client_state != WebSocketState.CONNECTED:
                break
            
            chunk = snapshot_events[idx * chunk_size:(idx + 1) * chunk_size]
            if not chunk:
                continue
            
            try:
                await websocket.send_json({
                    "type": "snapshot_chunk",
                    "window_hours": 4.0,
                    "max_events": 100,
                    "chunk_index": idx,
                    "total_chunks": total_chunks,
                    "events": chunk,
                })
                await asyncio.sleep(0.05)  # Small delay between chunks
            except Exception as chunk_err:
                logger.error(f"[WebSocket] Error sending chunk {idx}/{total_chunks}: {chunk_err} ({client_info})")
                break
        
        # Send snapshot done marker
        if websocket.client_state == WebSocketState.CONNECTED:
            try:
                await websocket.send_json({
                    "type": "snapshot_done",
                    "window_hours": 4.0,
                    "max_events": 100,
                    "total_events": len(snapshot_events),
                    "total_chunks": total_chunks,
                })
                logger.info(f"[WebSocket] Sent snapshot: {len(snapshot_events)} events ({client_info})")
            except Exception as done_err:
                logger.error(f"[WebSocket] Error sending snapshot_done: {done_err} ({client_info})")
        
        return len(snapshot_events)
    
    except Exception as e:
        logger.error(f"[WebSocket] Snapshot send failed ({client_info}): {e}", exc_info=True)
        return 0


@router.websocket("/ws/events")
async def websocket_events(websocket: WebSocket, run_id: Optional[str] = None):
    """
    WebSocket endpoint for real-time watchdog event streaming.
    
    Streams events from frontend_feed.jsonl to connected clients.
    If run_id is provided, only events for that run_id are sent.
    """
    client_info = f"run_id: {run_id[:8] if run_id else 'all'}"
    
    try:
        await websocket.accept()
        logger.info(f"[WebSocket] Connection accepted ({client_info})")
    except Exception as e:
        logger.error(f"[WebSocket] Failed to accept connection ({client_info}): {e}", exc_info=True)
        return
    
    # Send snapshot in background (non-blocking)
    snapshot_task = asyncio.create_task(_send_snapshot(websocket, client_info))
    
    # Track cursor position for this client
    cursor_position = 0
    events_sent = 0
    
    try:
        # Stream events forever - poll frontend_feed.jsonl for new events
        while True:
            # Check connection state
            if websocket.client_state != WebSocketState.CONNECTED:
                logger.info(f"[WebSocket] Connection not connected ({client_info}): {websocket.client_state}")
                break
            
            # Read new events since cursor position
            new_events, new_cursor = _read_feed_events_since(cursor_position, max_events=50)
            
            # Send new events
            for event in new_events:
                # Filter by run_id if specified
                if run_id is None or event.get("run_id") == run_id:
                    try:
                        await websocket.send_json(event)
                        events_sent += 1
                        if events_sent <= 5 or events_sent % 100 == 0:
                            logger.info(f"[WebSocket] Sent event #{events_sent} ({client_info}): {event.get('event', 'unknown')}")
                    except WebSocketDisconnect:
                        logger.info(f"[WebSocket] Client disconnected ({client_info}, events_sent: {events_sent})")
                        break
                    except Exception as e:
                        error_msg = str(e).lower()
                        if "not connected" in error_msg or "closed" in error_msg or "disconnect" in error_msg:
                            logger.info(f"[WebSocket] Connection closed ({client_info}, events_sent: {events_sent}): {e}")
                            break
                        else:
                            logger.warning(f"[WebSocket] Error sending event ({client_info}): {e} - continuing")
            
            # Update cursor position
            if new_cursor > cursor_position:
                cursor_position = new_cursor
            
            # Sleep before next poll (poll every second)
            await asyncio.sleep(1)
    
    except WebSocketDisconnect:
        logger.info(f"[WebSocket] Client disconnected ({client_info}, events_sent: {events_sent})")
    except Exception as e:
        error_type = type(e).__name__
        error_msg = str(e)
        logger.error(
            f"[WebSocket] Unexpected error ({client_info}): "
            f"type={error_type}, message={error_msg}, events_sent={events_sent}",
            exc_info=True
        )
    finally:
        # Cancel snapshot task if still running
        if not snapshot_task.done():
            snapshot_task.cancel()
            try:
                await snapshot_task
            except asyncio.CancelledError:
                pass
        
        logger.info(f"[WebSocket] Connection closed ({client_info}, events_sent: {events_sent})")


@router.websocket("/ws/events/{run_id}")
async def websocket_events_by_run(websocket: WebSocket, run_id: str):
    """WebSocket endpoint for specific run ID."""
    await websocket_events(websocket, run_id=run_id)
