"""
WebSocket endpoints for watchdog real-time event streaming

Live-signal only: WebSocket provides optional live updates.
REST provides authoritative state.

No snapshots, no file IO, no history replay.
"""
import asyncio
import logging
from typing import Optional
from datetime import datetime, timezone
from fastapi import APIRouter, WebSocket, WebSocketDisconnect
from starlette.websockets import WebSocketState
try:
    from uvicorn.protocols.utils import ClientDisconnected
except ImportError:
    # Fallback if import fails
    ClientDisconnected = Exception

from modules.watchdog.config import (
    WS_SEND_TIMEOUT_SECONDS,
    WS_HEARTBEAT_INTERVAL_SECONDS,
    WS_MAX_SEND_FAILURES,
)
from modules.watchdog.websocket_tracker import get_tracker
from modules.watchdog.backend.websocket_utils import (
    is_connection_closed_error,
    should_break_on_error,
    log_websocket_error,
)

# Import aggregator instance lazily to avoid circular import
def get_aggregator_instance():
    """Get aggregator instance from watchdog router (lazy import to avoid circular dependency)."""
    try:
        from modules.watchdog.backend.routers import watchdog as watchdog_router
        aggregator = watchdog_router.aggregator_instance
        if aggregator is None:
            logger.debug("Aggregator instance is None in watchdog_router")
        return aggregator
    except ImportError as import_err:
        logger.error(f"Failed to import watchdog router: {import_err}")
        return None
    except Exception as e:
        logger.error(f"Unexpected error getting aggregator instance: {e}")
        return None

router = APIRouter(tags=["websocket"])
logger = logging.getLogger(__name__)


@router.websocket("/ws/events")
async def websocket_events(websocket: WebSocket, run_id: Optional[str] = None):
    """
    WebSocket endpoint for real-time watchdog event streaming.
    
    Live-signal only: Sends heartbeat and important events from in-memory ring buffer.
    No snapshots, no file IO, no history replay.
    
    Lifecycle:
    - WS_CONNECT_ATTEMPT: Route handler invoked (proves route match)
    - WS_ACCEPTED: Connection accepted
    - WS_ERROR: Any exception (with phase tag)
    - WS_CLOSED: Connection closed (in finally)
    """
    logger.info("WS_HANDLER_CALLED")
    tracker = get_tracker()
    connection_id: Optional[str] = None
    connect_start_time: Optional[datetime] = None
    events_sent = 0
    send_fail_count = 0
    last_sent_seq = 0  # Track last sent sequence ID per connection
    
    # Extract client info for logging
    client_host = websocket.client.host if websocket.client else "unknown"
    client_port = websocket.client.port if websocket.client else 0
    path = str(websocket.url.path)
    run_id_display = run_id[:8] if run_id else None
    
    # Generate connection_id early for correlation
    import uuid
    connection_id_short = str(uuid.uuid4())[:8]
    
    # WS_CONNECT_ATTEMPT - First line, proves route match
    logger.info("WS_CONNECT_ATTEMPT client=%s", websocket.client)
    
    # Ensure accept() completes before any send operations
    try:
        # Attempt to accept connection
        connect_start_time = datetime.now(timezone.utc)
        await websocket.accept()
        
        # Yield control back to Uvicorn to complete handshake
        await asyncio.sleep(0)
        
        # WS_ACCEPTED - only log after accept() succeeds
        logger.info("WS_ACCEPTED client=%s", websocket.client)
        
        # Register connection with tracker (after accept)
        connection_id = await tracker.register_connection(path, run_id)
    except Exception as e:
        # WS_ERROR phase=accept
        logger.exception("WS_ERROR phase=accept")
        await tracker.record_accept_failure(str(e))
        # Try to send close frame if possible
        try:
            if websocket.client_state != WebSocketState.CLOSED:
                await websocket.close(code=1011, reason=f"Accept failed: {str(e)}")
        except:
            pass
        return
    
    # Verify connection is fully established before proceeding
    if websocket.client_state != WebSocketState.CONNECTED:
        logger.warning(f"WS_CONNECTION_NOT_READY client={client_host}:{client_port} state={websocket.client_state}")
        return
    
    # Get aggregator instance (lazy import to avoid circular dependency)
    # Note: We allow connections even if aggregator isn't ready yet - we just won't send events
    aggregator = get_aggregator_instance()
    if not aggregator:
        logger.warning(
            f"WS_WARNING: Aggregator not available yet connection_id={connection_id_short} "
            f"client={client_host}:{client_port} - connection will only send heartbeats"
        )
        # Don't close connection - allow it to stay open and just send heartbeats
        # The aggregator might become available later
    
    # Track last message sent time for heartbeat
    last_message_sent_utc = datetime.now(timezone.utc)
    
    try:
        logger.info("WS_LOOP_ENTER")
        # Main loop: Send heartbeat and important events
        while True:
            # Check connection state
            if websocket.client_state != WebSocketState.CONNECTED:
                break
            
            # Check if we've exceeded max send failures
            if send_fail_count >= WS_MAX_SEND_FAILURES:
                logger.warning(
                    f"WS_CLOSED connection_id={connection_id_short} client={client_host}:{client_port} "
                    f"reason=max_send_failures_exceeded fail_count={send_fail_count} events_sent={events_sent}"
                )
                await websocket.close(code=1008, reason="Max send failures exceeded")
                break
            
            # Get new important events since last_sent_seq from ring buffer (if aggregator available)
            new_events = []
            if aggregator:
                try:
                    new_events = aggregator.get_important_events_since(last_sent_seq)
                    # Filter by run_id if specified
                    if run_id:
                        new_events = [e for e in new_events if e.get("run_id") == run_id]
                except Exception as get_events_err:
                    logger.warning(f"Error getting events from aggregator: {get_events_err}")
                    # Try to get aggregator again in case it became available
                    aggregator = get_aggregator_instance()
            
            # Send new events if any
            if new_events:
                for event in new_events:
                    # Check connection state before sending
                    if websocket.client_state != WebSocketState.CONNECTED:
                        break
                    
                    if not websocket.client:
                        break
                    
                    try:
                        await asyncio.wait_for(
                            websocket.send_json(event),
                            timeout=WS_SEND_TIMEOUT_SECONDS
                        )
                        await tracker.record_message_sent()
                        events_sent += 1
                        send_fail_count = 0  # Reset on successful send
                        last_message_sent_utc = datetime.now(timezone.utc)
                        
                        # Update last_sent_seq
                        event_seq = event.get("seq", 0)
                        if event_seq > last_sent_seq:
                            last_sent_seq = event_seq
                        
                        if events_sent <= 5 or events_sent % 100 == 0:
                            logger.debug(f"Sent event #{events_sent} seq={event_seq} to {client_host}:{client_port}")
                    except Exception as send_err:
                        # Use utility to determine if we should break
                        if should_break_on_error(send_err, "send"):
                            break
                        
                        # Log error using utility (never crashes)
                        log_websocket_error(send_err, "send", f"{client_host}:{client_port}", connection_id_short)
                        
                        # Increment failure count and continue to next event
                        send_fail_count += 1
                        await tracker.record_dropped_events(1)
                        await tracker.record_error(f"Error sending event: {send_err}")
                        # Continue to next event
            
            # Check for heartbeat (if no events sent for interval)
            now_utc = datetime.now(timezone.utc)
            time_since_last_message = (now_utc - last_message_sent_utc).total_seconds()
            
            if time_since_last_message >= WS_HEARTBEAT_INTERVAL_SECONDS:
                # Send heartbeat
                try:
                    # Double-check connection state before sending
                    if websocket.client_state != WebSocketState.CONNECTED:
                        break
                    
                    if not websocket.client:
                        break
                    
                    await asyncio.wait_for(
                        websocket.send_json({
                            "type": "heartbeat",
                            "server_time_utc": now_utc.isoformat(),
                        }),
                        timeout=WS_SEND_TIMEOUT_SECONDS
                    )
                    await tracker.record_message_sent()
                    last_message_sent_utc = now_utc
                    logger.debug(f"Heartbeat sent to {client_host}:{client_port}")
                except Exception as heartbeat_err:
                    # Use utility to determine if we should break
                    if should_break_on_error(heartbeat_err, "heartbeat"):
                        break
                    
                    # Log error using utility (never crashes)
                    log_websocket_error(heartbeat_err, "heartbeat", f"{client_host}:{client_port}", connection_id_short)
                    
                    # Increment failure count
                    send_fail_count += 1
                    await tracker.record_dropped_events(1)
                    await tracker.record_error(f"Heartbeat send failed: {heartbeat_err}")
            
            # Sleep before next poll (poll every second)
            await asyncio.sleep(1)
    
    except Exception as stream_err:
        logger.exception("WS_FATAL")
        # Use utility to log error (handles CancelledError, WebSocketDisconnect, etc.)
        if should_break_on_error(stream_err, "stream"):
            # Clean disconnect - log as INFO
            log_websocket_error(stream_err, "stream", f"{client_host}:{client_port}", connection_id_short)
        else:
            # Unexpected error - log with traceback
            log_websocket_error(stream_err, "stream", f"{client_host}:{client_port}", connection_id_short)
            await tracker.record_error(f"Unexpected error in streaming loop: {stream_err}")
    finally:
        logger.info("WS_CLEANUP")
        # Unregister connection
        if connection_id:
            await tracker.unregister_connection(connection_id)
        
        # WS_CLOSED - Log connection closure (exception-safe, precompute all values)
        try:
            # Precompute all values as strings to avoid formatting exceptions
            connection_id_str = str(connection_id_short) if connection_id_short else "unknown"
            client_info_str = f"{client_host}:{client_port}"
            path_str = str(path)
            events_sent_str = str(events_sent)
            
            # Compute duration safely
            duration_seconds_value = None
            if connect_start_time:
                try:
                    duration_seconds_value = (datetime.now(timezone.utc) - connect_start_time).total_seconds()
                except:
                    duration_seconds_value = None
            
            # Format duration string safely
            if duration_seconds_value is None:
                duration_seconds_str = "None"
            else:
                try:
                    duration_seconds_str = f"{duration_seconds_value:.1f}"
                except:
                    duration_seconds_str = str(duration_seconds_value)
            
            # Get close code/reason safely
            try:
                close_code = getattr(websocket, 'close_code', None)
                close_code_str = str(close_code) if close_code is not None else "None"
            except:
                close_code_str = "unknown"
            
            try:
                close_reason = getattr(websocket, 'close_reason', None)
                close_reason_str = str(close_reason) if close_reason else "none"
            except:
                close_reason_str = "unknown"
            
            # Build log message from precomputed strings
            close_log_msg = (
                f"WS_CLOSED connection_id={connection_id_str} client={client_info_str} path={path_str} "
                f"events_sent={events_sent_str} duration_seconds={duration_seconds_str} "
                f"close_code={close_code_str} close_reason={close_reason_str}"
            )
            logger.info(close_log_msg)
        except Exception as log_err:
            # Exception-safe logging - never crash the handler
            try:
                # Fallback: use raw values only
                logger.warning(f"WS_CLOSED_LOG_FAILED connection_id={connection_id_short} client={client_host}:{client_port} error={type(log_err).__name__}")
            except:
                # Even fallback logging failed - just print
                print(f"WS_CLOSED_LOG_FAILED connection_id={connection_id_short} client={client_host}:{client_port}")


@router.websocket("/ws/events/{run_id}")
async def websocket_events_by_run(websocket: WebSocket, run_id: str):
    """WebSocket endpoint for specific run ID."""
    await websocket_events(websocket, run_id=run_id)
