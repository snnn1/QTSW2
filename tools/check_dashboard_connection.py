"""
Check Dashboard Backend Connection - Diagnose connection issues
"""

import sys
import requests
import time
from pathlib import Path

# Add project root to path
qtsw2_root = Path(__file__).parent.parent
if str(qtsw2_root) not in sys.path:
    sys.path.insert(0, str(qtsw2_root))

def main():
    print("="*80)
    print("DASHBOARD BACKEND CONNECTION DIAGNOSTIC")
    print("="*80)
    
    backend_url = "http://localhost:8001"
    
    # Test 1: Health endpoint
    print(f"\n[TEST 1] Health Check")
    print(f"  URL: {backend_url}/health")
    try:
        start_time = time.time()
        response = requests.get(f"{backend_url}/health", timeout=5)
        elapsed = (time.time() - start_time) * 1000
        print(f"  Status: {response.status_code}")
        print(f"  Response time: {elapsed:.0f}ms")
        if response.status_code == 200:
            data = response.json()
            print(f"  Response: {data}")
            print(f"  [PASS] Backend is responding")
        else:
            print(f"  [FAIL] Backend returned status {response.status_code}")
    except requests.exceptions.ConnectionError:
        print(f"  [FAIL] Cannot connect to backend")
        print(f"  Make sure the backend is running:")
        print(f"    cd {qtsw2_root}")
        print(f"    batch\\START_DASHBOARD.bat")
        return 1
    except requests.exceptions.Timeout:
        print(f"  [FAIL] Health check timed out (>5 seconds)")
        print(f"  Backend may be overloaded or stuck")
        return 1
    except Exception as e:
        print(f"  [ERROR] {e}")
        return 1
    
    # Test 2: Pipeline status endpoint
    print(f"\n[TEST 2] Pipeline Status")
    print(f"  URL: {backend_url}/api/pipeline/status")
    try:
        start_time = time.time()
        response = requests.get(f"{backend_url}/api/pipeline/status", timeout=10)
        elapsed = (time.time() - start_time) * 1000
        print(f"  Status: {response.status_code}")
        print(f"  Response time: {elapsed:.0f}ms")
        if response.status_code == 200:
            data = response.json()
            print(f"  Pipeline state: {data.get('state', 'unknown')}")
            print(f"  [PASS] Pipeline status endpoint working")
        else:
            print(f"  [FAIL] Pipeline status returned {response.status_code}")
    except requests.exceptions.Timeout:
        print(f"  [FAIL] Pipeline status timed out (>10 seconds)")
        print(f"  Backend may be slow or stuck")
        return 1
    except Exception as e:
        print(f"  [ERROR] {e}")
    
    # Test 3: WebSocket endpoint (check if it exists)
    print(f"\n[TEST 3] WebSocket Endpoint")
    print(f"  URL: ws://localhost:8001/ws/events")
    print(f"  Note: WebSocket connections require a browser/client")
    print(f"  Check browser console for WebSocket connection status")
    
    # Test 4: Check if backend process is running
    print(f"\n[TEST 4] Backend Process Check")
    try:
        import psutil
        backend_found = False
        for proc in psutil.process_iter(['pid', 'name', 'cmdline']):
            try:
                cmdline = proc.info.get('cmdline', [])
                if cmdline:
                    cmdline_str = ' '.join(cmdline)
                    if 'uvicorn' in cmdline_str.lower() or 'main.py' in cmdline_str.lower():
                        if '8001' in cmdline_str or 'dashboard' in cmdline_str.lower():
                            print(f"  [FOUND] Backend process: PID {proc.info['pid']}")
                            print(f"    Command: {cmdline_str[:100]}...")
                            backend_found = True
            except (psutil.NoSuchProcess, psutil.AccessDenied):
                pass
        if not backend_found:
            print(f"  [WARNING] No backend process found")
            print(f"  Backend may not be running")
    except ImportError:
        print(f"  [SKIP] psutil not available (install with: pip install psutil)")
    except Exception as e:
        print(f"  [ERROR] {e}")
    
    # Test 5: Check port availability
    print(f"\n[TEST 5] Port 8001 Availability")
    try:
        import socket
        sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        sock.settimeout(1)
        result = sock.connect_ex(('localhost', 8001))
        sock.close()
        if result == 0:
            print(f"  [OK] Port 8001 is open (backend is listening)")
        else:
            print(f"  [FAIL] Port 8001 is not accessible")
            print(f"  Backend may not be running")
    except Exception as e:
        print(f"  [ERROR] {e}")
    
    print(f"\n[SUMMARY]")
    print(f"  If health check passed but dashboard is slow:")
    print(f"    1. Check browser console for errors (F12)")
    print(f"    2. Check WebSocket connection status in browser console")
    print(f"    3. Try refreshing the page")
    print(f"    4. Check backend logs for errors")
    print(f"    5. Restart backend: batch\\START_DASHBOARD.bat")
    
    return 0

if __name__ == "__main__":
    sys.exit(main())
