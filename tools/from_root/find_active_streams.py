#!/usr/bin/env python3
"""Find active streams from recent bar activity"""
import json
import glob
import re
from datetime import datetime, timezone
from collections import defaultdict

print("=" * 100)
print("FINDING ACTIVE STREAMS FROM RECENT ACTIVITY")
print("=" * 100)

# Look for streams with recent bar activity (last 2 hours)
cutoff = datetime.now(timezone.utc).timestamp() - 7200
active_streams = defaultdict(lambda: {
    'instrument': None,
    'events': [],
    'last_event_time': None,
    'bar_count': 0,
    'barsrequest_count': 0,
    'range_locked': False,
    'armed': False
})

instrument_logs = glob.glob("logs/robot/robot_*.jsonl")

for log_file in instrument_logs:
    if 'ENGINE' in log_file:
        continue
    
    instrument = log_file.split('_')[-1].replace('.jsonl', '')
    
    with open(log_file, 'r', encoding='utf-8-sig') as f:
        for line in f:
            try:
                e = json.loads(line.strip())
                ts_str = e.get('ts_utc') or e.get('ts', '')
                if not ts_str:
                    continue
                
                ts = datetime.fromisoformat(ts_str.replace('Z', '+00:00')).timestamp()
                if ts < cutoff:
                    continue
                
                stream_id = e.get('stream') or e.get('data', {}).get('stream_id', '')
                if not stream_id or stream_id == 'N/A':
                    continue
                
                event_type = str(e.get('event') or e.get('event_type', ''))
                
                if stream_id not in active_streams:
                    active_streams[stream_id]['instrument'] = instrument
                
                active_streams[stream_id]['events'].append({
                    'timestamp': ts_str,
                    'event_type': event_type,
                    'data': e
                })
                
                if ts > (active_streams[stream_id]['last_event_time'] or 0):
                    active_streams[stream_id]['last_event_time'] = ts
                
                if 'BAR_ACCEPTED' in event_type or 'BAR_RECEIVED' in event_type:
                    active_streams[stream_id]['bar_count'] += 1
                
                if 'BARSREQUEST_EXECUTED' in event_type:
                    active_streams[stream_id]['barsrequest_count'] += 1
                
                if 'RANGE_LOCKED' in event_type:
                    active_streams[stream_id]['range_locked'] = True
                
                if 'STREAM_ARMED' in event_type or 'ARMED' in event_type:
                    active_streams[stream_id]['armed'] = True
                    
            except Exception as ex:
                pass

print(f"\nFound {len(active_streams)} streams with recent activity (last 2 hours):")
print()

for stream_id in sorted(active_streams.keys()):
    info = active_streams[stream_id]
    last_dt = datetime.fromtimestamp(info['last_event_time'], tz=timezone.utc) if info['last_event_time'] else None
    last_str = last_dt.strftime("%H:%M:%S") if last_dt else "N/A"
    
    print(f"{stream_id} ({info['instrument']})")
    print(f"  Total Events: {len(info['events'])}")
    print(f"  Last Event: {last_str}")
    print(f"  Bars Accepted: {info['bar_count']}")
    print(f"  BarsRequest Executed: {info['barsrequest_count']}")
    print(f"  Armed: {info['armed']}")
    print(f"  Range Locked: {info['range_locked']}")
    print()

# Now build timelines for these active streams
print("=" * 100)
print("BUILDING TIMELINES FOR ACTIVE STREAMS")
print("=" * 100)

def parse_timestamp(ts_str):
    try:
        ts_str = ts_str.replace('Z', '+00:00')
        return datetime.fromisoformat(ts_str)
    except:
        return None

key_events = [
    'STREAM_CREATED', 'STREAM_ARMED', 'BAR_RECEIVED', 'BAR_ACCEPTED', 
    'BAR_REJECTED', 'BARSREQUEST_EXECUTED', 'RANGE_LOCKED', 
    'RANGE_BUILDING', 'HYDRATION_COMPLETE', 'STREAM_DONE'
]

for stream_id in sorted(active_streams.keys()):
    info = active_streams[stream_id]
    instrument = info['instrument']
    
    print(f"\n{'='*100}")
    print(f"STREAM: {stream_id} ({instrument})")
    print(f"{'='*100}")
    
    # Get timeline events
    timeline = []
    for event in info['events']:
        event_type = event['event_type']
        if any(key in event_type for key in key_events):
            timeline.append(event)
    
    timeline.sort(key=lambda x: parse_timestamp(x['timestamp']) or datetime.min.replace(tzinfo=timezone.utc))
    
    if timeline:
        print(f"\nTimeline ({len(timeline)} key events):")
        print("-" * 100)
        
        for event in timeline[-30:]:  # Last 30 events
            ts = event['timestamp']
            event_type = event['event_type']
            
            try:
                dt = parse_timestamp(ts)
                ts_display = dt.strftime("%H:%M:%S") if dt else ts[:19]
            except:
                ts_display = ts[:19] if len(ts) > 19 else ts
            
            print(f"  [{ts_display}] {event_type}")
            
            # Show details for important events
            if 'BARSREQUEST_EXECUTED' in event_type:
                payload_str = str(event['data'].get('payload') or event['data'].get('data', {}).get('payload', ''))
                bars_match = re.search(r'bars_returned\s*=\s*(\d+)', payload_str)
                if bars_match:
                    print(f"      Bars: {bars_match.group(1)}")
            
            if 'RANGE_LOCKED' in event_type:
                print(f"      *** RANGE LOCKED ***")
            
            if 'STREAM_ARMED' in event_type:
                print(f"      *** ARMED ***")
    else:
        print("\n[WARN] No key timeline events found")
    
    # Problem assessment
    print(f"\nAssessment:")
    print("-" * 100)
    
    issues = []
    
    if not info['armed']:
        issues.append("Stream not armed")
    else:
        print("[OK] Stream armed")
    
    if info['barsrequest_count'] == 0:
        issues.append("No BarsRequest executed")
    else:
        print(f"[OK] BarsRequest executed: {info['barsrequest_count']} time(s)")
    
    if info['bar_count'] == 0:
        issues.append("No bars accepted")
    else:
        print(f"[OK] Bars accepted: {info['bar_count']}")
        if info['bar_count'] < 10:
            issues.append(f"Very few bars ({info['bar_count']})")
    
    if not info['range_locked']:
        issues.append("Range not locked (may be too early)")
    else:
        print("[OK] Range locked")
    
    if issues:
        print("\n[WARN] Issues:")
        for issue in issues:
            print(f"  - {issue}")
    else:
        print("\n[OK] Stream appears healthy")

print("\n" + "=" * 100)
