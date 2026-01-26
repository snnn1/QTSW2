"""Check current stream states and why they might not be active"""
import json
from pathlib import Path
from datetime import datetime
from collections import Counter, defaultdict

def parse_timestamp(ts_str):
    try:
        return datetime.fromisoformat(ts_str.replace('Z', '+00:00'))
    except:
        return None

def main():
    log_dir = Path("logs/robot")
    
    # Read all log files (not just ENGINE)
    all_logs = sorted([f for f in log_dir.glob('robot_*.jsonl')], 
                      key=lambda p: p.stat().st_mtime, reverse=True)
    
    print("="*80)
    print("STREAM STATE ANALYSIS")
    print("="*80)
    
    # Read events from all instrument logs
    events = []
    for log_file in all_logs[:10]:  # Check 10 most recent log files
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
    
    # Get recent events (last 1000)
    recent = events[-1000:] if len(events) > 1000 else events
    
    # Find latest ENGINE_START
    starts = [e for e in recent if e.get('event') == 'ENGINE_START']
    if not starts:
        print("[ERROR] No ENGINE_START found")
        return
    
    latest_start = starts[-1]
    start_time = parse_timestamp(latest_start.get('ts_utc', ''))
    
    print(f"\nLatest ENGINE_START: {start_time.strftime('%Y-%m-%d %H:%M:%S UTC') if start_time else 'N/A'}")
    print(f"Analyzing events since then...\n")
    
    # Filter events since latest start
    events_since_start = [e for e in recent 
                          if parse_timestamp(e.get('ts_utc', '')) and 
                          parse_timestamp(e.get('ts_utc', '')) >= start_time]
    
    # Stream state transitions
    state_transitions = [e for e in events_since_start if e.get('event') == 'STREAM_STATE_TRANSITION']
    
    print(f"[STREAM STATE TRANSITIONS]")
    print(f"  Total transitions: {len(state_transitions)}")
    
    if state_transitions:
        # Group by stream
        streams = defaultdict(list)
        for e in state_transitions:
            stream = e.get('stream', 'UNKNOWN')
            inst = e.get('instrument', 'UNKNOWN')
            data = e.get('data', {})
            payload = str(data.get('payload', ''))
            
            # Extract state from payload
            state = 'UNKNOWN'
            if 'new_state =' in payload:
                try:
                    state = payload.split('new_state =')[1].split(',')[0].strip()
                except:
                    pass
            
            ts = parse_timestamp(e.get('ts_utc', ''))
            streams[stream].append({
                'time': ts,
                'state': state,
                'instrument': inst
            })
        
        print(f"\n  Unique streams: {len(streams)}")
        
        # Show current state of each stream
        print(f"\n  Current stream states:")
        for stream, transitions in sorted(streams.items()):
            if transitions:
                latest = sorted(transitions, key=lambda x: x['time'] or datetime.min)[-1]
                inst = latest['instrument']
                state = latest['state']
                time_str = latest['time'].strftime('%H:%M:%S UTC') if latest['time'] else 'N/A'
                print(f"    {inst:4} | {stream[:50]:50} | {state:20} | {time_str}")
    
    # STREAM_ARMED events
    armed = [e for e in events_since_start if e.get('event') == 'STREAM_ARMED']
    print(f"\n[STREAM_ARMED EVENTS]")
    print(f"  Total: {len(armed)}")
    
    # RANGE_LOCKED events (active streams)
    range_locked = [e for e in events_since_start if e.get('event') == 'RANGE_LOCKED']
    print(f"\n[RANGE_LOCKED EVENTS]")
    print(f"  Total: {len(range_locked)}")
    if range_locked:
        print(f"  [OK] Found {len(range_locked)} active streams!")
        for e in range_locked[-10:]:
            ts = parse_timestamp(e.get('ts_utc', ''))
            inst = e.get('instrument', 'N/A')
            stream = e.get('stream', 'N/A')[:50]
            print(f"    {ts.strftime('%H:%M:%S UTC') if ts else 'N/A'}: {inst:4} | {stream}")
    else:
        print(f"  [WARN] No RANGE_LOCKED events - streams not becoming active!")
    
    # PRE_HYDRATION events
    pre_hydration = [e for e in events_since_start if 'PRE_HYDRATION' in e.get('event', '').upper()]
    print(f"\n[PRE_HYDRATION EVENTS]")
    print(f"  Total: {len(pre_hydration)}")
    pre_hydration_types = Counter([e.get('event') for e in pre_hydration])
    for ptype, count in sorted(pre_hydration_types.items()):
        print(f"    {ptype}: {count}")
    
    # RANGE_COMPUTE_FAILED
    range_failed = [e for e in events_since_start if e.get('event') == 'RANGE_COMPUTE_FAILED']
    print(f"\n[RANGE_COMPUTE_FAILED EVENTS]")
    print(f"  Total: {len(range_failed)}")
    if range_failed:
        reasons = Counter([e.get('data', {}).get('reason', 'N/A') for e in range_failed])
        print(f"  Reasons:")
        for reason, count in reasons.most_common(5):
            print(f"    {reason}: {count}")
    
    # Errors related to streams
    stream_errors = [e for e in events_since_start 
                    if e.get('level') == 'ERROR' and 
                    ('STREAM' in e.get('event', '').upper() or 
                     'RANGE' in e.get('event', '').upper() or
                     'PRE_HYDRATION' in e.get('event', '').upper())]
    
    print(f"\n[STREAM-RELATED ERRORS]")
    print(f"  Total: {len(stream_errors)}")
    if stream_errors:
        for e in stream_errors[-10:]:
            ts = parse_timestamp(e.get('ts_utc', ''))
            inst = e.get('instrument', 'N/A')
            event_type = e.get('event', 'N/A')
            print(f"    {ts.strftime('%H:%M:%S UTC') if ts else 'N/A'}: {inst:4} | {event_type}")
    
    # Check for bars being received
    bars_received = [e for e in events_since_start if 'BAR' in e.get('event', '').upper() and 'RECEIVED' in e.get('event', '').upper()]
    print(f"\n[BAR RECEIVED EVENTS]")
    print(f"  Total: {len(bars_received)}")
    
    # Check for ON_BAR events
    on_bar = [e for e in events_since_start if e.get('event') == 'ON_BAR']
    print(f"\n[ON_BAR EVENTS]")
    print(f"  Total: {len(on_bar)}")
    
    # Summary
    print("\n" + "="*80)
    print("DIAGNOSIS")
    print("="*80)
    
    if len(range_locked) == 0:
        print("\n[ISSUE] No streams have reached RANGE_LOCKED state")
        print("  Possible causes:")
        
        if len(armed) > 0 and len(range_locked) == 0:
            print("    1. Streams are armed but not progressing to RANGE_LOCKED")
            print("       - Check if bars are being received (ON_BAR events)")
            print("       - Check for RANGE_COMPUTE_FAILED events")
            print("       - Check stream state transitions")
        
        if len(pre_hydration) > 0:
            print(f"    2. Streams are in PRE_HYDRATION ({len(pre_hydration)} events)")
            print("       - May be waiting for historical bars")
        
        if len(range_failed) > 0:
            print(f"    3. Range computation is failing ({len(range_failed)} failures)")
            print("       - Check RANGE_COMPUTE_FAILED reasons above")
        
        if len(on_bar) == 0:
            print("    4. No bars being received (ON_BAR events)")
            print("       - Market data may not be flowing")
            print("       - Check NinjaTrader connection")
    else:
        print(f"\n[OK] Found {len(range_locked)} active streams (RANGE_LOCKED)")
    
    # Latest stream-related events
    print(f"\n[LATEST 15 STREAM-RELATED EVENTS]")
    stream_related = [e for e in events_since_start 
                     if any(x in e.get('event', '').upper() 
                           for x in ['STREAM', 'RANGE', 'PRE_HYDRATION', 'ARMED', 'ON_BAR'])]
    
    for e in stream_related[-15:]:
        ts = parse_timestamp(e.get('ts_utc', ''))
        level = e.get('level', '')
        event_type = e.get('event', '')
        inst = e.get('instrument', '')
        print(f"  {ts.strftime('%H:%M:%S UTC') if ts else 'N/A':19} | {level:5} | {inst:4} | {event_type}")

if __name__ == '__main__':
    main()
