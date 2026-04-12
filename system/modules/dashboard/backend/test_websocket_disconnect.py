"""
Test script to diagnose WebSocket disconnection issues.

Run this while the backend is running to monitor WebSocket connections.
"""
import asyncio
import logging
from datetime import datetime
from fastapi import WebSocket
from starlette.websockets import WebSocketState

logging.basicConfig(level=logging.DEBUG)
logger = logging.getLogger(__name__)

async def test_websocket_connection():
    """Test WebSocket connection stability"""
    import websockets
    
    uri = "ws://localhost:8001/ws/events"
    logger.info(f"Connecting to {uri}...")
    
    try:
        async with websockets.connect(uri) as websocket:
            logger.info("[OK] WebSocket connected")
            
            # Monitor connection for 5 minutes
            start_time = datetime.now()
            events_received = 0
            errors = []
            
            while True:
                try:
                    # Wait for message with timeout
                    message = await asyncio.wait_for(websocket.recv(), timeout=60.0)
                    events_received += 1
                    
                    if events_received % 10 == 0:
                        elapsed = (datetime.now() - start_time).total_seconds()
                        logger.info(f"Received {events_received} events in {elapsed:.1f}s")
                    
                except asyncio.TimeoutError:
                    elapsed = (datetime.now() - start_time).total_seconds()
                    logger.warning(f"[WARNING] No message received in 60s (total: {events_received} events, elapsed: {elapsed:.1f}s)")
                    
                except websockets.exceptions.ConnectionClosed as e:
                    logger.error(f"[ERROR] Connection closed unexpectedly: code={e.code}, reason={e.reason}")
                    errors.append(f"ConnectionClosed: {e.code} - {e.reason}")
                    break
                    
                except Exception as e:
                    logger.error(f"[ERROR] Unexpected error: {type(e).__name__}: {e}")
                    errors.append(f"{type(e).__name__}: {e}")
                    
                # Stop after 5 minutes
                if (datetime.now() - start_time).total_seconds() > 300:
                    logger.info("[OK] Test completed - 5 minutes elapsed")
                    break
                    
    except Exception as e:
        logger.error(f"[ERROR] Failed to connect: {type(e).__name__}: {e}")
        return
    
    # Summary
    elapsed = (datetime.now() - start_time).total_seconds()
    logger.info(f"\n{'='*60}")
    logger.info(f"Test Summary:")
    logger.info(f"  Duration: {elapsed:.1f} seconds")
    logger.info(f"  Events received: {events_received}")
    logger.info(f"  Errors: {len(errors)}")
    if errors:
        logger.info(f"  Error details:")
        for error in errors:
            logger.info(f"    - {error}")
    logger.info(f"{'='*60}")

if __name__ == "__main__":
    asyncio.run(test_websocket_connection())

