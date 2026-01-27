"""
Quick diagnostic script to test Watchdog WebSocket connection.

This helps verify:
1. Backend is running on port 8002
2. WebSocket routes are registered
3. Connection attempt reaches the handler (check backend logs for WS_CONNECT_ATTEMPT)
"""
import asyncio
import websockets
import json
import sys

async def test_websocket():
    """Test WebSocket connection to watchdog backend."""
    uri = "ws://localhost:8002/ws/events"
    
    print(f"Testing WebSocket connection to: {uri}")
    print("=" * 60)
    
    try:
        print("Attempting connection...")
        async with websockets.connect(uri) as websocket:
            print("✅ Connection established!")
            print("Waiting for messages (5 seconds)...")
            
            try:
                # Wait for messages with timeout
                message = await asyncio.wait_for(websocket.recv(), timeout=5.0)
                data = json.loads(message)
                print(f"✅ Received message: {data.get('type', 'unknown')}")
                if data.get('type') == 'heartbeat':
                    print(f"   Server time: {data.get('server_time_utc')}")
                elif data.get('type') == 'snapshot_chunk':
                    print(f"   Snapshot chunk: {data.get('chunk_index')}/{data.get('total_chunks')}")
                    print(f"   Events in chunk: {len(data.get('events', []))}")
            except asyncio.TimeoutError:
                print("⚠️  No message received within 5 seconds (connection is alive but no data)")
            
            print("\n✅ WebSocket connection test PASSED")
            return True
            
    except websockets.exceptions.InvalidStatusCode as e:
        print(f"❌ Connection failed with status code: {e.status_code}")
        print(f"   Response headers: {e.headers}")
        return False
    except websockets.exceptions.ConnectionClosed as e:
        print(f"❌ Connection closed immediately")
        print(f"   Close code: {e.code}")
        print(f"   Close reason: {e.reason}")
        return False
    except ConnectionRefusedError:
        print(f"❌ Connection refused - backend not running on port 8002")
        print("   Make sure watchdog backend is started")
        return False
    except Exception as e:
        print(f"❌ Connection failed: {type(e).__name__}: {e}")
        return False

if __name__ == "__main__":
    print("\nWatchdog WebSocket Diagnostic Test")
    print("=" * 60)
    print("\nIMPORTANT: Check backend logs for:")
    print("  - 'WS_CONNECT_ATTEMPT' (proves route handler invoked)")
    print("  - 'WS_ACCEPTED' (proves accept succeeded)")
    print("  - 'WS_ERROR phase=accept' (if accept fails)")
    print("=" * 60 + "\n")
    
    success = asyncio.run(test_websocket())
    
    print("\n" + "=" * 60)
    if success:
        print("✅ Test completed - check backend logs for WS_CONNECT_ATTEMPT")
    else:
        print("❌ Test failed - check backend logs and verify backend is running")
    print("=" * 60)
