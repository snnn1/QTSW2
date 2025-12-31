#!/usr/bin/env python3
"""
Start pipeline with manual override (bypasses health check).

Use this when you get "Manual run requires override: health is unstable" error.

Usage:
    python tools/start_pipeline_override.py
"""

import requests
import json
import sys
from pathlib import Path

# Default backend URL
API_BASE = "http://localhost:8001/api"

def start_pipeline_with_override():
    """Start pipeline with manual override enabled."""
    url = f"{API_BASE}/pipeline/start"
    
    payload = {
        "manual": True,
        "manual_override": True
    }
    
    print(f"Starting pipeline with override...")
    print(f"POST {url}")
    print(f"Payload: {json.dumps(payload, indent=2)}")
    print()
    
    try:
        response = requests.post(url, json=payload, timeout=10)
        
        if response.status_code == 200:
            result = response.json()
            print("✅ Pipeline started successfully!")
            print(f"Run ID: {result.get('run_id', 'unknown')}")
            print(f"State: {result.get('state', 'unknown')}")
            return 0
        else:
            error_text = response.text
            print(f"❌ Pipeline start failed (HTTP {response.status_code})")
            print(f"Error: {error_text}")
            
            # Check if it's still blocked (shouldn't happen with override, but check anyway)
            if "requires override" in error_text or "health is unstable" in error_text:
                print("\n⚠️  WARNING: Override didn't work. This might be a BLOCKED health state (cannot override).")
                print("   Check the run history to see if there's a critical infrastructure error.")
            
            return 1
            
    except requests.exceptions.ConnectionError:
        print(f"❌ Connection error: Could not reach backend at {API_BASE}")
        print("   Make sure the dashboard backend is running on port 8001")
        return 1
    except requests.exceptions.Timeout:
        print(f"❌ Timeout: Backend didn't respond within 10 seconds")
        return 1
    except Exception as e:
        print(f"❌ Unexpected error: {e}")
        return 1


if __name__ == "__main__":
    # Allow custom API base URL via environment variable
    import os
    if "API_BASE" in os.environ:
        API_BASE = os.environ["API_BASE"]
    
    exit_code = start_pipeline_with_override()
    sys.exit(exit_code)
