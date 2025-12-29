"""
Quick script to check if the /api/matrix/update endpoint is registered.
Run this while the backend is running.
"""
import requests
import json

def check_endpoint():
    """Check if the update endpoint exists."""
    base_url = "http://localhost:8000"
    
    # Check if backend is running
    try:
        response = requests.get(f"{base_url}/api/matrix/test", timeout=2)
        if response.status_code == 200:
            print("OK Backend is running")
        else:
            print(f"X Backend returned status {response.status_code}")
            return
    except requests.exceptions.ConnectionError:
        print("X Backend is not running. Start it with:")
        print("  cd modules/dashboard/backend")
        print("  python main.py")
        return
    except Exception as e:
        print(f"✗ Error checking backend: {e}")
        return
    
    # Try to get OpenAPI schema to see registered endpoints
    try:
        response = requests.get(f"{base_url}/openapi.json", timeout=2)
        if response.status_code == 200:
            schema = response.json()
            paths = schema.get('paths', {})
            
            # Check for update endpoint
            update_path = "/api/matrix/update"
            if update_path in paths:
                print(f"OK Endpoint {update_path} is registered!")
                print(f"  Methods: {list(paths[update_path].keys())}")
            else:
                print(f"X Endpoint {update_path} is NOT registered")
                print("\nAvailable /api/matrix endpoints:")
                for path in sorted(paths.keys()):
                    if path.startswith("/api/matrix"):
                        print(f"  {path}: {list(paths[path].keys())}")
                print("\n⚠ The backend may need to be restarted to pick up the new endpoint.")
        else:
            print(f"X Could not fetch OpenAPI schema: {response.status_code}")
    except Exception as e:
        print(f"X Error checking endpoints: {e}")

if __name__ == "__main__":
    print("=" * 60)
    print("Checking /api/matrix/update endpoint")
    print("=" * 60)
    check_endpoint()

