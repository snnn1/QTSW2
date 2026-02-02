#!/usr/bin/env python3
"""
Check watchdog status and see if it's tracking robot events correctly.
"""
import json
import requests
from datetime import datetime, timezone
import pytz

def main():
    chicago_tz = pytz.timezone('America/Chicago')
    
    print("="*80)
    print("WATCHDOG STATUS ASSESSMENT")
    print("="*80)
    
    try:
        # Try to get watchdog status from API (watchdog runs on port 8002)
        base_url = "http://localhost:8002"
        
        print("\n1. Checking watchdog API endpoints...")
        
        # Check debug endpoint
        try:
            debug_resp = requests.get(f"{base_url}/api/watchdog/debug", timeout=5)
            if debug_resp.status_code == 200:
                debug_data = debug_resp.json()
                print(f"   [OK] Debug endpoint accessible")
                print(f"   Aggregator initialized: {debug_data.get('aggregator_instance', False)}")
                print(f"   Has state manager: {debug_data.get('has_state_manager', False)}")
                print(f"   Has event feed: {debug_data.get('has_event_feed', False)}")
            else:
                print(f"   [WARN] Debug endpoint returned {debug_resp.status_code}")
        except Exception as e:
            print(f"   [ERROR] Cannot reach watchdog API: {e}")
            print(f"   Is the watchdog backend running? Try:")
            print(f"     python -m uvicorn modules.watchdog.backend.main:app --host 0.0.0.0 --port 8002")
            print(f"     Or use: batch\\START_WATCHDOG_BACKEND.bat")
            return
        
        # Check status endpoint
        print("\n2. Checking watchdog status...")
        try:
            status_resp = requests.get(f"{base_url}/api/watchdog/status", timeout=5)
            if status_resp.status_code == 200:
                status = status_resp.json()
                
                engine_status = status.get('engine', {})
                print(f"   Engine Status:")
                print(f"     Alive: {engine_status.get('alive', False)}")
                print(f"     Tick Age: {engine_status.get('tick_age_seconds', 0):.1f}s")
                print(f"     Threshold: {engine_status.get('tick_stall_threshold_seconds', 15)}s")
                
                connection_status = status.get('connection', {})
                print(f"\n   Connection Status:")
                print(f"     Status: {connection_status.get('status', 'Unknown')}")
                last_update = connection_status.get('last_update_utc')
                if last_update:
                    try:
                        dt = datetime.fromisoformat(last_update.replace('Z', '+00:00'))
                        dt_chicago = dt.astimezone(chicago_tz)
                        age = (datetime.now(timezone.utc) - dt).total_seconds()
                        print(f"     Last Update: {dt_chicago.strftime('%Y-%m-%d %H:%M:%S')} CT ({age:.1f}s ago)")
                    except:
                        print(f"     Last Update: {last_update}")
                
                recovery_status = status.get('recovery', {})
                print(f"\n   Recovery Status:")
                print(f"     State: {recovery_status.get('state', 'Unknown')}")
                print(f"     Active: {recovery_status.get('active', False)}")
                
            else:
                print(f"   [WARN] Status endpoint returned {status_resp.status_code}")
        except Exception as e:
            print(f"   [ERROR] Error getting status: {e}")
        
        # Check events endpoint
        print("\n3. Checking recent events...")
        try:
            events_resp = requests.get(f"{base_url}/api/watchdog/events", params={"since_seq": 0}, timeout=5)
            if events_resp.status_code == 200:
                events_data = events_resp.json()
                events = events_data.get('events', [])
                print(f"   [OK] Retrieved {len(events)} events")
                
                # Filter connection events
                conn_events = [e for e in events if 'CONNECTION' in e.get('type', '')]
                print(f"   Connection events in recent feed: {len(conn_events)}")
                for e in conn_events[-5:]:
                    print(f"     {e.get('type')}: {e.get('ts_utc', '')[:19]}")
            else:
                print(f"   [WARN] Events endpoint returned {events_resp.status_code}")
        except Exception as e:
            print(f"   [ERROR] Error getting events: {e}")
        
        print("\n" + "="*80)
        print("ASSESSMENT COMPLETE")
        print("="*80)
        
    except Exception as e:
        print(f"\n[ERROR] Assessment failed: {e}")
        import traceback
        traceback.print_exc()

if __name__ == "__main__":
    main()
