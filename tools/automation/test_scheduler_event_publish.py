"""
Test script to verify scheduler event publishing to backend EventBus.

This script tests:
1. httpx is available
2. Backend is reachable
3. Event publishing endpoint works
4. Events are received and published by EventBus
"""

import sys
import asyncio
from pathlib import Path
from datetime import datetime, timezone

# Add project root to path
qtsw2_root = Path(__file__).parent.parent
if str(qtsw2_root) not in sys.path:
    sys.path.insert(0, str(qtsw2_root))

async def test_scheduler_event_publish():
    """Test publishing a scheduler event to the backend"""
    
    # Test 1: Check httpx availability
    print("Test 1: Checking httpx availability...")
    try:
        import httpx
        print(f"  [OK] httpx is installed (version: {httpx.__version__})")
    except ImportError:
        print("  [FAIL] httpx is NOT installed")
        print("  Install with: pip install httpx>=0.24.0")
        return False
    
    # Test 2: Check backend connectivity
    print("\nTest 2: Checking backend connectivity...")
    try:
        async with httpx.AsyncClient(timeout=3.0) as client:
            # Try the API status endpoint instead of /health
            response = await client.get("http://localhost:8001/api/pipeline/status", timeout=3.0)
            if response.status_code in [200, 503]:  # 503 is OK if orchestrator not ready
                print("  [OK] Backend is reachable at http://localhost:8001")
            else:
                print(f"  [WARN] Backend returned status {response.status_code}")
                return False
    except httpx.ConnectError:
        print("  [FAIL] Cannot connect to backend at http://localhost:8001")
        print("  Make sure the dashboard backend is running")
        return False
    except Exception as e:
        error_msg = str(e).encode('ascii', 'replace').decode('ascii')
        print(f"  [FAIL] Error checking backend: {error_msg}")
        return False
    
    # Test 3: Publish a test scheduler event
    print("\nTest 3: Publishing test scheduler event...")
    test_run_id = "test-run-12345"
    event_data = {
        "run_id": test_run_id,
        "stage": "scheduler",
        "event": "start",
        "timestamp": datetime.now(timezone.utc).isoformat(),
        "msg": "Test scheduler event from test script",
        "data": {"manual": False, "test": True}
    }
    
    try:
        async with httpx.AsyncClient(timeout=5.0) as client:
            response = await client.post(
                "http://localhost:8001/api/pipeline/publish-scheduler-event",
                json=event_data,
                timeout=5.0
            )
            
            if response.status_code == 200:
                result = response.json()
                if result.get("published"):
                    print(f"  [OK] Test event published successfully!")
                    print(f"     Event: scheduler/start (run: {test_run_id[:8]})")
                    print(f"     Check the Live Events panel in the dashboard to see if it appears")
                    return True
                else:
                    print(f"  [WARNING] Backend received event but did not publish: {result.get('message', 'unknown error')}")
                    return False
            else:
                print(f"  [FAIL] Backend returned status {response.status_code}")
                print(f"     Response: {response.text[:200]}")
                return False
    except Exception as e:
        print(f"  [FAIL] Error publishing event: {e}")
        return False

def main():
    """Main entry point"""
    print("=" * 60)
    print("Scheduler Event Publishing Test")
    print("=" * 60)
    
    try:
        success = asyncio.run(test_scheduler_event_publish())
        print("\n" + "=" * 60)
        if success:
            print("[SUCCESS] All tests passed!")
            print("\nNext steps:")
            print("  1. Check the dashboard Live Events panel")
            print("  2. Run the task scheduler manually")
            print("  3. Verify scheduler events appear immediately")
        else:
            print("[FAIL] Some tests failed")
            print("\nTroubleshooting:")
            print("  1. Install httpx: pip install httpx>=0.24.0")
            print("  2. Start the dashboard backend")
            print("  3. Verify backend is running on http://localhost:8001")
        print("=" * 60)
        return 0 if success else 1
    except KeyboardInterrupt:
        print("\n\nTest interrupted by user")
        return 1
    except Exception as e:
        print(f"\n\n[ERROR] Unexpected error: {e}")
        import traceback
        traceback.print_exc()
        return 1

if __name__ == "__main__":
    sys.exit(main())

