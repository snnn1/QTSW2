#!/usr/bin/env python3
"""Restart backend by finding and killing the process, then starting it again"""

import subprocess
import time
import sys
from pathlib import Path

def find_backend_process():
    """Find the backend process"""
    try:
        result = subprocess.run(
            ["tasklist", "/FI", "IMAGENAME eq python.exe", "/FO", "CSV"],
            capture_output=True,
            text=True,
            timeout=5
        )
        lines = result.stdout.splitlines()
        for line in lines[1:]:  # Skip header
            if "python" in line.lower() and ("uvicorn" in line.lower() or "main:app" in line.lower()):
                parts = line.split(',')
                if len(parts) > 1:
                    pid = parts[1].strip('"')
                    return pid
    except:
        pass
    return None

def kill_process(pid):
    """Kill a process by PID"""
    try:
        subprocess.run(["taskkill", "/F", "/PID", str(pid)], check=True, timeout=5)
        return True
    except:
        return False

def start_backend():
    """Start the backend"""
    qtsw2_root = Path(__file__).parent
    backend_cmd = [
        "python", "-m", "uvicorn",
        "dashboard.backend.main:app",
        "--host", "0.0.0.0",
        "--port", "8001"
    ]
    
    try:
        # Start in new window
        subprocess.Popen(
            backend_cmd,
            cwd=str(qtsw2_root),
            creationflags=subprocess.CREATE_NEW_CONSOLE
        )
        return True
    except Exception as e:
        print(f"Error starting backend: {e}")
        return False

def main():
    print("="*60)
    print("Backend Restart")
    print("="*60)
    
    # Find and kill existing backend
    print("\n1. Finding backend process...")
    pid = find_backend_process()
    if pid:
        print(f"   Found backend process: PID {pid}")
        print("   Killing process...")
        if kill_process(pid):
            print("   ✅ Process killed")
            time.sleep(2)  # Wait for process to fully stop
        else:
            print("   ⚠️  Failed to kill process")
    else:
        print("   No backend process found (may already be stopped)")
    
    # Start backend
    print("\n2. Starting backend...")
    if start_backend():
        print("   ✅ Backend started")
        print("   Waiting 5 seconds for backend to initialize...")
        time.sleep(5)
        
        # Check if it's running
        print("\n3. Checking backend health...")
        try:
            import requests
            response = requests.get("http://localhost:8001/health", timeout=5)
            if response.status_code == 200:
                print("   ✅ Backend is healthy!")
            else:
                print(f"   ⚠️  Backend returned status {response.status_code}")
        except Exception as e:
            print(f"   ⚠️  Backend not responding yet: {e}")
            print("   (May need a few more seconds)")
    else:
        print("   ❌ Failed to start backend")
        sys.exit(1)
    
    print("\n" + "="*60)
    print("Backend restart complete!")
    print("="*60)

if __name__ == "__main__":
    main()




