"""
Quick test script to verify the window update endpoint works.
Run this after starting the dashboard backend.
"""
import requests
import json
import sys

API_BASE = "http://localhost:8000/api"

def test_window_update_endpoint():
    """Test the window update endpoint."""
    print("Testing window update endpoint...")
    print(f"API Base: {API_BASE}")
    
    # Test 1: Check if endpoint exists
    print("\n1. Testing endpoint availability...")
    try:
        url = f"{API_BASE}/matrix/update"
        payload = {
            "mode": "window",
            "reprocess_days": None  # Use default from config
        }
        
        print(f"POST {url}")
        print(f"Payload: {json.dumps(payload, indent=2)}")
        
        response = requests.post(url, json=payload, timeout=30)
        
        print(f"Status Code: {response.status_code}")
        
        if response.status_code == 200:
            result = response.json()
            print("✓ Endpoint works!")
            print(f"Response: {json.dumps(result, indent=2)}")
            return True
        else:
            print(f"✗ Endpoint returned error: {response.status_code}")
            try:
                error_data = response.json()
                print(f"Error: {json.dumps(error_data, indent=2)}")
            except:
                print(f"Error text: {response.text}")
            return False
            
    except requests.exceptions.ConnectionError:
        print("✗ Connection error: Is the backend server running?")
        print("  Start it with: cd modules/dashboard/backend && python main.py")
        return False
    except Exception as e:
        print(f"✗ Error: {e}")
        import traceback
        traceback.print_exc()
        return False

def test_checkpoint_system():
    """Test if checkpoint system is accessible."""
    print("\n2. Testing checkpoint system...")
    try:
        from modules.matrix.checkpoint_manager import CheckpointManager
        
        checkpoint_mgr = CheckpointManager()
        latest = checkpoint_mgr.load_latest_checkpoint()
        
        if latest:
            print(f"✓ Found checkpoint: {latest.get('checkpoint_id')}")
            print(f"  Date: {latest.get('checkpoint_date')}")
            print(f"  Streams: {list(latest.get('streams', {}).keys())}")
            return True
        else:
            print("⚠ No checkpoint found. You need to run a full rebuild first.")
            print("  The window update requires a checkpoint to restore state from.")
            return False
            
    except Exception as e:
        print(f"✗ Error checking checkpoint system: {e}")
        import traceback
        traceback.print_exc()
        return False

if __name__ == "__main__":
    print("=" * 60)
    print("Window Update Endpoint Test")
    print("=" * 60)
    
    # Test checkpoint system first
    checkpoint_ok = test_checkpoint_system()
    
    if not checkpoint_ok:
        print("\n⚠ WARNING: No checkpoint found.")
        print("  You must run a full rebuild first to create a checkpoint.")
        print("  The window update button will fail without a checkpoint.")
        sys.exit(1)
    
    # Test endpoint
    endpoint_ok = test_window_update_endpoint()
    
    if endpoint_ok:
        print("\n" + "=" * 60)
        print("✓ All tests passed! The button should work.")
        print("=" * 60)
    else:
        print("\n" + "=" * 60)
        print("✗ Tests failed. Check the errors above.")
        print("=" * 60)
        sys.exit(1)

