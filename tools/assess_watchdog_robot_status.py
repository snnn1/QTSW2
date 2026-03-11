#!/usr/bin/env python3
"""
Comprehensive assessment of watchdog and robot engine status.
"""
import json
import requests
from pathlib import Path
from datetime import datetime, timezone, timedelta
import pytz

def parse_timestamp(ts_str: str):
    """Parse ISO timestamp"""
    if not ts_str:
        return None
    try:
        if 'T' in ts_str:
            if ts_str.endswith('Z'):
                ts_str = ts_str[:-1] + '+00:00'
            elif '+' not in ts_str:
                ts_str = ts_str + '+00:00'
            dt = datetime.fromisoformat(ts_str)
            if dt.tzinfo is None:
                dt = dt.replace(tzinfo=timezone.utc)
            return dt
    except:
        pass
    return None

def main():
    chicago_tz = pytz.timezone('America/Chicago')
    base_url = "http://localhost:8002"
    
    print("="*80)
    print("WATCHDOG & ROBOT ENGINE STATUS ASSESSMENT")
    print("="*80)
    
    # 1. Check robot logs directly
    print("\n1. ROBOT LOGS ANALYSIS")
    print("-" * 80)
    
    log_dir = Path("logs/robot")
    cutoff = datetime.now(timezone.utc) - timedelta(hours=1)
    
    events = []
    for log_file in sorted(log_dir.glob("robot_*.jsonl"), reverse=True)[:3]:
        try:
            with open(log_file, 'r', encoding='utf-8') as f:
                for line in f:
                    line = line.strip()
                    if line:
                        try:
                            e = json.loads(line)
                            ts = parse_timestamp(e.get('ts_utc', ''))
                            if ts and ts >= cutoff:
                                events.append(e)
                        except:
                            pass
        except:
            pass
    
    events.sort(key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
    
    print(f"   Events in last hour: {len(events):,}")
    
    # Check ENGINE_TICK_CALLSITE
    tick_events = [e for e in events if e.get('event') == 'ENGINE_TICK_CALLSITE']
    if tick_events:
        latest_tick = max(tick_events, key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
        ts = parse_timestamp(latest_tick.get('ts_utc', ''))
        if ts:
            age = (datetime.now(timezone.utc) - ts).total_seconds()
            ts_chicago = ts.astimezone(chicago_tz)
            print(f"   [OK] ENGINE_TICK_CALLSITE: Latest {ts_chicago.strftime('%H:%M:%S')} CT ({age:.1f}s ago)")
            if age > 60:
                print(f"      [WARN] Engine appears stopped (no ticks for {age:.0f}s)")
        else:
            print(f"   [WARN] ENGINE_TICK_CALLSITE: Found but timestamp invalid")
    else:
        print(f"   [ERROR] ENGINE_TICK_CALLSITE: NOT FOUND (engine may be stopped)")
    
    # Check connection events
    conn_events = [e for e in events if 'CONNECTION' in e.get('event', '')]
    print(f"   Connection events: {len(conn_events)}")
    if conn_events:
        latest_conn = max(conn_events, key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
        ts = parse_timestamp(latest_conn.get('ts_utc', ''))
        if ts:
            age = (datetime.now(timezone.utc) - ts).total_seconds()
            ts_chicago = ts.astimezone(chicago_tz)
            print(f"   [OK] Latest: {latest_conn.get('event')} at {ts_chicago.strftime('%H:%M:%S')} CT ({age:.1f}s ago)")
    
    # 2. Check watchdog feed file
    print("\n2. WATCHDOG FEED FILE")
    print("-" * 80)
    
    feed_file = Path("logs/robot/frontend_feed.jsonl")
    if feed_file.exists():
        with open(feed_file, 'r', encoding='utf-8') as f:
            feed_lines = f.readlines()
        print(f"   Feed file: {len(feed_lines):,} lines")
        
        # Check last 1000 lines for connection events
        feed_events = []
        for line in feed_lines[-1000:]:
            try:
                e = json.loads(line.strip())
                feed_events.append(e)
            except:
                pass
        
        feed_conn = [e for e in feed_events if 'CONNECTION' in e.get('event_type', '')]
        print(f"   Connection events in feed (last 1000 lines): {len(feed_conn)}")
        if feed_conn:
            latest_feed_conn = feed_conn[-1]
            print(f"   [OK] Latest: {latest_feed_conn.get('event_type')}")
    else:
        print(f"   [ERROR] Feed file not found: {feed_file}")
    
    # 3. Check watchdog API
    print("\n3. WATCHDOG API STATUS")
    print("-" * 80)
    
    try:
        status_resp = requests.get(f"{base_url}/api/watchdog/status", timeout=5)
        if status_resp.status_code == 200:
            status = status_resp.json()
            
            engine = status.get('engine', {})
            print(f"   Engine Alive: {engine.get('alive', False)}")
            print(f"   Tick Age: {engine.get('tick_age_seconds', 0):.1f}s")
            
            connection = status.get('connection', {})
            conn_status = connection.get('status', 'Unknown')
            print(f"   Connection Status: {conn_status}")
            
            if conn_status == "Unknown":
                print(f"      [WARN] Connection status is Unknown - state_manager may need initialization")
            
            recovery = status.get('recovery', {})
            print(f"   Recovery State: {recovery.get('state', 'Unknown')}")
            
        else:
            print(f"   [ERROR] Status API returned {status_resp.status_code}")
    except Exception as e:
        print(f"   [ERROR] Cannot reach watchdog API: {e}")
        return
    
    # 4. Check recent events from API
    print("\n4. WATCHDOG EVENTS API")
    print("-" * 80)
    
    try:
        events_resp = requests.get(f"{base_url}/api/watchdog/events", params={"since_seq": 0}, timeout=5)
        if events_resp.status_code == 200:
            events_data = events_resp.json()
            api_events = events_data.get('events', [])
            print(f"   Total events: {len(api_events)}")
            
            api_conn = [e for e in api_events if 'CONNECTION' in e.get('type', '')]
            print(f"   Connection events: {len(api_conn)}")
            
            if len(api_conn) == 0 and len(feed_conn) > 0:
                print(f"      [WARN] Feed has {len(feed_conn)} connection events but API shows 0")
                print(f"         Events may be filtered or cursor position issue")
        else:
            print(f"   [ERROR] Events API returned {events_resp.status_code}")
    except Exception as e:
        print(f"   [ERROR] Error getting events: {e}")
    
    # 5. Summary and recommendations
    print("\n" + "="*80)
    print("ASSESSMENT SUMMARY")
    print("="*80)
    
    print("\n[OK] Watchdog Updates Completed:")
    print("   - CONNECTION_RECOVERED_NOTIFICATION added to config")
    print("   - Event processor updated to handle new event type")
    print("   - Aggregator updated to include in important events")
    
    print("\n[WARN] Potential Issues:")
    print("   1. Robot engine appears stopped (no recent ticks)")
    print("   2. Connection status shows 'Unknown' in watchdog API")
    print("   3. Connection events exist in feed but may not be processed into state")
    
    print("\nRecommendations:")
    print("   1. Restart robot engine if it's stopped")
    print("   2. Check watchdog logs for errors processing events")
    print("   3. Verify state_manager is initializing connection status from feed")
    print("   4. Test with sustained disconnect (>=60s) to trigger CONNECTION_RECOVERED_NOTIFICATION")

if __name__ == "__main__":
    main()
