#!/usr/bin/env python3
"""
Audit Watchdog State Manager

Checks:
- State manager internal state
- Field population
- Rebuild functions work
- State computation inputs
"""
import json
import sys
import requests
from pathlib import Path
from datetime import datetime, timezone

# Add project root to path
project_root = Path(__file__).parent
sys.path.insert(0, str(project_root))

print("="*80)
print("WATCHDOG STATE MANAGER AUDIT")
print("="*80)

# 1. Check API status
print("\n1. API STATUS CHECK")
print("-" * 80)

try:
    response = requests.get("http://localhost:8002/api/watchdog/status", timeout=5)
    if response.status_code == 200:
        status = response.json()
        print("[OK] API responding")
        
        print(f"\nCurrent Status:")
        print(f"  engine_alive: {status.get('engine_alive')}")
        print(f"  engine_activity_state: {status.get('engine_activity_state')}")
        print(f"  last_engine_tick_chicago: {status.get('last_engine_tick_chicago')}")
        print(f"  connection_status: {status.get('connection_status')}")
        print(f"  last_connection_event_utc: {status.get('last_connection_event_utc')}")
        print(f"  data_stall_detected: {len(status.get('data_stall_detected', {}))} instrument(s)")
        print(f"  worst_last_bar_age_seconds: {status.get('worst_last_bar_age_seconds')}")
        print(f"  last_identity_invariants_pass: {status.get('last_identity_invariants_pass')}")
        print(f"  recovery_state: {status.get('recovery_state')}")
        
        # Check for issues
        issues = []
        if not status.get('engine_alive'):
            issues.append("Engine not alive")
        if status.get('connection_status') == 'Unknown':
            issues.append("Connection status unknown")
        if status.get('worst_last_bar_age_seconds') is None:
            issues.append("No bar tracking data")
        if status.get('last_identity_invariants_pass') is None:
            issues.append("Identity status unknown")
        
        if issues:
            print(f"\n⚠️  ISSUES DETECTED:")
            for issue in issues:
                print(f"  - {issue}")
        else:
            print(f"\n[OK] No obvious issues in API status")
            
    else:
        print(f"❌ API returned status {response.status_code}")
        status = None
except requests.exceptions.ConnectionError:
    print("[ERROR] API not responding (connection refused)")
    print("   Is the watchdog backend running?")
    status = None
except Exception as e:
    print(f"[ERROR] Error checking API: {e}")
    status = None

# 2. Check feed file for recent events
print("\n2. RECENT EVENTS IN FEED")
print("-" * 80)

from modules.watchdog.config import FRONTEND_FEED_FILE

if FRONTEND_FEED_FILE.exists():
    with open(FRONTEND_FEED_FILE, 'r', encoding='utf-8') as f:
        lines = f.readlines()
    
    if lines:
        # Get last 50 events
        recent = []
        for line in lines[-50:]:
            if line.strip():
                try:
                    event = json.loads(line.strip())
                    recent.append(event)
                except:
                    continue
        
        ticks = [e for e in recent if e.get('event_type') == 'ENGINE_TICK_CALLSITE']
        bars = [e for e in recent if 'BAR' in e.get('event_type', '')]
        connections = [e for e in recent if 'CONNECTION' in e.get('event_type', '')]
        identity = [e for e in recent if 'IDENTITY' in e.get('event_type', '')]
        
        print(f"Last 50 events:")
        print(f"  ENGINE_TICK_CALLSITE: {len(ticks)}")
        print(f"  Bar events: {len(bars)}")
        print(f"  Connection events: {len(connections)}")
        print(f"  Identity events: {len(identity)}")
        
        if ticks:
            latest_tick = ticks[-1]
            tick_ts = datetime.fromisoformat(latest_tick.get('timestamp_utc', '').replace('Z', '+00:00'))
            age_seconds = (datetime.now(timezone.utc) - tick_ts).total_seconds()
            print(f"\n  Latest tick: {tick_ts.isoformat()[:19]} ({age_seconds:.1f}s ago)")
            
            if status and not status.get('engine_alive'):
                print(f"  ⚠️  WARNING: Tick exists but engine_alive=False")
        
        if bars:
            latest_bar = bars[-1]
            bar_ts = datetime.fromisoformat(latest_bar.get('timestamp_utc', '').replace('Z', '+00:00'))
            age_seconds = (datetime.now(timezone.utc) - bar_ts).total_seconds()
            instrument = latest_bar.get('execution_instrument_full_name') or latest_bar.get('instrument') or 'UNKNOWN'
            print(f"\n  Latest bar: {bar_ts.isoformat()[:19]} ({age_seconds:.1f}s ago), instrument={instrument}")
            
            if status and status.get('worst_last_bar_age_seconds') is None:
                print(f"  ⚠️  WARNING: Bar exists but worst_last_bar_age_seconds=None")
        
        if connections:
            latest_conn = connections[-1]
            print(f"\n  Latest connection: {latest_conn.get('event_type')} at {latest_conn.get('timestamp_utc', '')[:19]}")
            
            if status and status.get('connection_status') == 'Unknown':
                print(f"  ⚠️  WARNING: Connection event exists but connection_status=Unknown")
        
        if identity:
            latest_id = identity[-1]
            print(f"\n  Latest identity: {latest_id.get('event_type')} at {latest_id.get('timestamp_utc', '')[:19]}")
            
            if status and status.get('last_identity_invariants_pass') is None:
                print(f"  ⚠️  WARNING: Identity event exists but last_identity_invariants_pass=None")
    else:
        print("Feed file is empty")
else:
    print("Feed file not found")

# 3. Test rebuild functions
print("\n3. REBUILD FUNCTION TEST")
print("-" * 80)

# Simulate _rebuild_connection_status_from_recent_events
def test_rebuild_connection():
    if not FRONTEND_FEED_FILE.exists():
        return None
    
    try:
        with open(FRONTEND_FEED_FILE, 'r', encoding='utf-8-sig') as f:
            all_lines = f.readlines()
            recent_lines = all_lines[-5000:] if len(all_lines) > 5000 else all_lines
        
        latest_connection_event = None
        latest_timestamp = None
        
        for line in recent_lines:
            line = line.strip()
            if not line:
                continue
            
            try:
                event = json.loads(line)
                event_type = event.get("event_type")
                
                if event_type not in ("CONNECTION_LOST", "CONNECTION_LOST_SUSTAINED", 
                                     "CONNECTION_RECOVERED", "CONNECTION_RECOVERED_NOTIFICATION"):
                    continue
                
                timestamp_str = event.get("timestamp_utc")
                if not timestamp_str:
                    continue
                
                try:
                    timestamp = datetime.fromisoformat(timestamp_str.replace('Z', '+00:00'))
                    if timestamp.tzinfo is None:
                        timestamp = timestamp.replace(tzinfo=timezone.utc)
                except Exception:
                    continue
                
                if latest_timestamp is None or timestamp > latest_timestamp:
                    latest_connection_event = event
                    latest_timestamp = timestamp
            except json.JSONDecodeError:
                continue
        
        return latest_connection_event, latest_timestamp
    except Exception as e:
        print(f"  ERROR: {e}")
        return None

conn_result = test_rebuild_connection()
if conn_result:
    event, ts = conn_result
    print(f"✅ Rebuild connection test SUCCESS")
    print(f"   Found: {event.get('event_type')} at {ts.isoformat()[:19] if ts else 'unknown'}")
else:
    print(f"[ERROR] Rebuild connection test FAILED - no connection events found")

# 4. Check state computation inputs
print("\n4. STATE COMPUTATION INPUTS")
print("-" * 80)

if status:
    print("State computation would use:")
    print(f"  last_engine_tick_utc: {status.get('last_engine_tick_chicago')}")
    print(f"  last_bar_utc_by_execution_instrument: {len(status.get('data_stall_detected', {}))} instruments tracked")
    print(f"  connection_status: {status.get('connection_status')}")
    print(f"  last_identity_invariants_pass: {status.get('last_identity_invariants_pass')}")
    
    # Check thresholds
    from modules.watchdog.config import (
        ENGINE_TICK_STALL_THRESHOLD_SECONDS,
        DATA_STALL_THRESHOLD_SECONDS
    )
    
    print(f"\nThresholds:")
    print(f"  ENGINE_TICK_STALL_THRESHOLD_SECONDS: {ENGINE_TICK_STALL_THRESHOLD_SECONDS}")
    print(f"  DATA_STALL_THRESHOLD_SECONDS: {DATA_STALL_THRESHOLD_SECONDS}")
    
    # Check if state would be correct given recent events
    if ticks and status.get('last_engine_tick_chicago') is None:
        print(f"\n⚠️  ISSUE: Recent ticks exist but last_engine_tick_chicago is None")
        print(f"   State manager not updating from events")
    
    if bars and status.get('worst_last_bar_age_seconds') is None:
        print(f"\n⚠️  ISSUE: Recent bars exist but worst_last_bar_age_seconds is None")
        print(f"   Bar tracking not updating from events")

print("\n" + "="*80)
print("AUDIT COMPLETE")
print("="*80)
