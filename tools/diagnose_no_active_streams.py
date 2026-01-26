"""Diagnose why streams are not becoming active"""
import json
from pathlib import Path
from datetime import datetime
from collections import Counter

def parse_timestamp(ts_str):
    try:
        return datetime.fromisoformat(ts_str.replace('Z', '+00:00'))
    except:
        return None

def main():
    log_dir = Path("logs/robot")
    
    # Read all log files
    all_logs = sorted([f for f in log_dir.glob('robot_*.jsonl')], 
                      key=lambda p: p.stat().st_mtime, reverse=True)
    
    print("="*80)
    print("DIAGNOSIS: WHY STREAMS ARE NOT ACTIVE")
    print("="*80)
    
    # Read events from recent logs
    events = []
    for log_file in all_logs[:5]:
        try:
            with open(log_file, 'r', encoding='utf-8-sig') as f:
                for line in f:
                    line = line.strip()
                    if line:
                        try:
                            events.append(json.loads(line))
                        except:
                            pass
        except:
            pass
    
    # Sort by timestamp
    events.sort(key=lambda e: parse_timestamp(e.get('ts_utc', '')) or datetime.min)
    
    # Get recent events (last 500)
    recent = events[-500:] if len(events) > 500 else events
    
    # Find latest ENGINE_START
    starts = [e for e in recent if e.get('event') == 'ENGINE_START']
    if not starts:
        print("[ERROR] No ENGINE_START found")
        return
    
    latest_start = starts[-1]
    start_time = parse_timestamp(latest_start.get('ts_utc', ''))
    
    print(f"\nLatest ENGINE_START: {start_time.strftime('%Y-%m-%d %H:%M:%S UTC') if start_time else 'N/A'}")
    
    # Filter events since latest start
    events_since_start = [e for e in recent 
                          if parse_timestamp(e.get('ts_utc', '')) and 
                          parse_timestamp(e.get('ts_utc', '')) >= start_time]
    
    print(f"Events since start: {len(events_since_start)}\n")
    
    # Check for BarsRequest events
    barsrequest_events = [e for e in events_since_start 
                         if 'BARSREQUEST' in e.get('event', '').upper()]
    
    print(f"[BARSREQUEST EVENTS]")
    print(f"  Total: {len(barsrequest_events)}")
    
    if barsrequest_events:
        barsrequest_types = Counter([e.get('event') for e in barsrequest_events])
        print(f"  Breakdown:")
        for btype, count in sorted(barsrequest_types.items()):
            print(f"    {btype}: {count}")
        
        # Show recent BarsRequest events
        print(f"\n  Recent BarsRequest events:")
        for e in barsrequest_events[-10:]:
            ts = parse_timestamp(e.get('ts_utc', ''))
            inst = e.get('instrument', 'N/A')
            event_type = e.get('event', 'N/A')
            level = e.get('level', '')
            data = e.get('data', {})
            
            print(f"    {ts.strftime('%H:%M:%S UTC') if ts else 'N/A':19} | {level:5} | {inst:4} | {event_type}")
            
            # Show error details if present
            if level == 'ERROR':
                payload = str(data.get('payload', ''))
                if 'error' in payload.lower() or 'failed' in payload.lower():
                    print(f"      Error: {payload[:100]}")
    else:
        print(f"  [CRITICAL] No BarsRequest events found!")
        print(f"    This means BarsRequest is not being called or is failing silently")
    
    # Check for PRE_HYDRATION_BARS_LOADED
    bars_loaded = [e for e in events_since_start 
                  if e.get('event') == 'PRE_HYDRATION_BARS_LOADED']
    
    print(f"\n[PRE_HYDRATION_BARS_LOADED EVENTS]")
    print(f"  Total: {len(bars_loaded)}")
    
    if bars_loaded:
        print(f"  [OK] Bars are being loaded!")
        for e in bars_loaded[-5:]:
            ts = parse_timestamp(e.get('ts_utc', ''))
            inst = e.get('instrument', 'N/A')
            data = e.get('data', {})
            payload = str(data.get('payload', ''))
            
            # Extract bar_count from payload
            bar_count = 'N/A'
            if 'bar_count =' in payload:
                try:
                    bar_count = payload.split('bar_count =')[1].split(',')[0].strip()
                except:
                    pass
            
            print(f"    {ts.strftime('%H:%M:%S UTC') if ts else 'N/A'}: {inst:4} | {bar_count} bars")
    else:
        print(f"  [CRITICAL] No bars have been loaded!")
        print(f"    Streams cannot progress without bars")
    
    # Check for ON_BAR events
    on_bar = [e for e in events_since_start if e.get('event') == 'ON_BAR']
    
    print(f"\n[ON_BAR EVENTS]")
    print(f"  Total: {len(on_bar)}")
    
    if on_bar:
        print(f"  [OK] Live bars are being received!")
        for e in on_bar[-5:]:
            ts = parse_timestamp(e.get('ts_utc', ''))
            inst = e.get('instrument', 'N/A')
            print(f"    {ts.strftime('%H:%M:%S UTC') if ts else 'N/A'}: {inst}")
    else:
        print(f"  [WARN] No live bars being received")
        print(f"    This is OK if market is closed, but problematic if market is open")
    
    # Check for connection status
    connection_events = [e for e in events_since_start 
                        if 'CONNECTION' in e.get('event', '').upper() or 
                           'DISCONNECT' in e.get('event', '').upper()]
    
    print(f"\n[CONNECTION STATUS]")
    print(f"  Connection-related events: {len(connection_events)}")
    
    if connection_events:
        for e in connection_events[-5:]:
            ts = parse_timestamp(e.get('ts_utc', ''))
            event_type = e.get('event', 'N/A')
            level = e.get('level', '')
            print(f"    {ts.strftime('%H:%M:%S UTC') if ts else 'N/A'}: {level:5} | {event_type}")
    
    # Check for STREAMS_CREATED
    streams_created = [e for e in events_since_start 
                      if e.get('event') == 'STREAMS_CREATED']
    
    print(f"\n[STREAMS_CREATED EVENTS]")
    print(f"  Total: {len(streams_created)}")
    
    if streams_created:
        latest = streams_created[-1]
        data = latest.get('data', {})
        payload = str(data.get('payload', ''))
        
        # Extract stream_count
        stream_count = 'N/A'
        if 'stream_count =' in payload:
            try:
                stream_count = payload.split('stream_count =')[1].split(',')[0].strip()
            except:
                pass
        
        print(f"  Latest: {stream_count} streams created")
    
    # Summary diagnosis
    print("\n" + "="*80)
    print("DIAGNOSIS SUMMARY")
    print("="*80)
    
    issues = []
    
    if len(barsrequest_events) == 0:
        issues.append("BarsRequest is not being called - check if RequestHistoricalBarsForPreHydration() is invoked")
    
    barsrequest_failed = [e for e in barsrequest_events if e.get('level') == 'ERROR' or 'FAILED' in e.get('event', '').upper()]
    if barsrequest_failed:
        issues.append(f"BarsRequest is failing ({len(barsrequest_failed)} failures) - check errors above")
    
    if len(bars_loaded) == 0:
        issues.append("No bars are being loaded - streams cannot progress from PRE_HYDRATION")
    
    if len(on_bar) == 0:
        issues.append("No live bars being received - check NinjaTrader data connection")
    
    if issues:
        print("\n[ISSUES FOUND]")
        for i, issue in enumerate(issues, 1):
            print(f"  {i}. {issue}")
        
        print("\n[RECOMMENDED ACTIONS]")
        print("  1. Check NinjaTrader connection status")
        print("  2. Verify data feed is connected and active")
        print("  3. Check if RequestHistoricalBarsForPreHydration() is being called")
        print("  4. Look for BarsRequest errors in logs")
        print("  5. Verify market is actually open (if expecting live bars)")
    else:
        print("\n[OK] All checks passed - bars should be flowing")
        print("  If streams still not active, check stream state transitions")

if __name__ == '__main__':
    main()
