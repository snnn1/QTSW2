"""
WebSocket utility functions for error handling and connection management.

Centralizes disconnect / cancel / timeout semantics to prevent repeated fragile try/except blocks.
Ensures logging never crashes ASGI.
"""
import logging
from typing import Optional

try:
    from uvicorn.protocols.utils import ClientDisconnected
except ImportError:
    # Fallback if import fails
    ClientDisconnected = Exception

from fastapi import WebSocketDisconnect

logger = logging.getLogger(__name__)


def is_connection_closed_error(error: Exception) -> bool:
    """
    Check if error indicates WebSocket connection is closed.
    
    Returns True for:
    - WebSocketDisconnect
    - ClientDisconnected
    - ConnectionClosedError
    - RuntimeError with "close message" or "handshake" in message
    - Any error message containing "not connected", "closed", "disconnect"
    """
    error_type = type(error).__name__
    error_msg = str(error).lower()
    
    # Check error type
    if error_type in ("WebSocketDisconnect", "ClientDisconnected", "ConnectionClosedError"):
        return True
    
    # Check for RuntimeError with connection-related messages
    if error_type == "RuntimeError":
        if ("close message" in error_msg or "handshake" in error_msg or 
            "cannot call" in error_msg):
            return True
    
    # Check error message content
    if ("not connected" in error_msg or "closed" in error_msg or 
        "disconnect" in error_msg or "close message" in error_msg):
        return True
    
    return False


def should_break_on_error(error: Exception, phase: str) -> bool:
    """
    Determine if error handling should break out of loop.
    
    Returns True for:
    - Connection closed errors (always break)
    - CancelledError (clean disconnect, break)
    
    Returns False for:
    - TimeoutError (continue, just log)
    - Other errors (log and continue, unless connection closed)
    """
    import asyncio
    
    # Connection closed errors always break
    if is_connection_closed_error(error):
        return True
    
    # CancelledError means clean disconnect
    if isinstance(error, asyncio.CancelledError):
        return True
    
    # TimeoutError - continue, just log
    if isinstance(error, asyncio.TimeoutError):
        return False
    
    # Other errors - continue unless connection closed
    return False


def log_websocket_error(
    error: Exception, 
    phase: str, 
    client_info: str, 
    connection_id: Optional[str] = None
) -> None:
    """
    Centralized WebSocket error logging (never crashes).
    
    Handles:
    - CancelledError -> INFO level (clean disconnect)
    - Connection closed errors -> INFO level (normal)
    - TimeoutError -> WARNING level
    - Other errors -> ERROR level with exception traceback
    
    All string formatting is precomputed to prevent crashes.
    """
    import asyncio
    
    # Precompute all string values safely
    try:
        error_type_str = type(error).__name__
    except:
        error_type_str = "UnknownError"
    
    try:
        error_msg_str = str(error)
    except:
        error_msg_str = "Error string conversion failed"
    
    try:
        connection_id_str = str(connection_id) if connection_id else "unknown"
    except:
        connection_id_str = "unknown"
    
    try:
        client_info_str = str(client_info)
    except:
        client_info_str = "unknown"
    
    try:
        phase_str = str(phase)
    except:
        phase_str = "unknown"
    
    # Determine log level and message based on error type
    if isinstance(error, asyncio.CancelledError):
        # CancelledError = clean disconnect, log as INFO
        try:
            log_msg = (
                f"WS_ERROR phase={phase_str} connection_id={connection_id_str} "
                f"client={client_info_str} reason=cancelled"
            )
            logger.info(log_msg)
        except:
            # Fallback if even basic logging fails
            logger.info(f"WS_ERROR phase={phase_str} reason=cancelled")
    
    elif is_connection_closed_error(error):
        # Connection closed errors = normal disconnect, log as INFO
        try:
            log_msg = (
                f"WS_ERROR phase={phase_str} connection_id={connection_id_str} "
                f"client={client_info_str} reason=connection_closed error_type={error_type_str}"
            )
            logger.info(log_msg)
        except:
            logger.info(f"WS_ERROR phase={phase_str} reason=connection_closed")
    
    elif isinstance(error, asyncio.TimeoutError):
        # TimeoutError = warning, not fatal
        try:
            log_msg = (
                f"WS_ERROR phase={phase_str} connection_id={connection_id_str} "
                f"client={client_info_str} reason=timeout"
            )
            logger.warning(log_msg)
        except:
            logger.warning(f"WS_ERROR phase={phase_str} reason=timeout")
    
    else:
        # Other errors = error level with traceback
        try:
            log_msg = (
                f"WS_ERROR phase={phase_str} connection_id={connection_id_str} "
                f"client={client_info_str} error_type={error_type_str}"
            )
            logger.exception(log_msg)
        except:
            # Fallback if logging fails
            try:
                logger.error(f"WS_ERROR phase={phase_str} error_type={error_type_str}")
            except:
                # Last resort - just print
                print(f"WS_ERROR phase={phase_str} error_type={error_type_str}")
