#!/usr/bin/env python3
"""Build stream timelines from timetable and recent logs"""
import json
import glob
import re
from datetime import datetime, timezone
from collections import defaultdict

print("=" * 100)
print("STREAM TIMELINE ANALYSIS FROM TIMETABLE")
print("=" * 100)

# Load timetable to see enabled streams
timetable_path = "data/timetable/timetable_current.json"
with open(timetable_path, 'r', encoding='utf-8') as f:
    timetable = json.load(f)

print(f"\nTimetable Trading Date: {timetable.get('trading_date', 'N/A')}")
print(f"Timetable Timezone: {timetable.get('timezone', 'N/A')}")

# Extract enabled streams
enabled_streams = []
if 'streams' in timetable:
    for stream in timetable['streams']:
        if stream.get('enabled', False):
            stream_id = stream.get('stream', 'N/A')
            instrument = stream.get('instrument', 'N/A')
            session = stream.get('session', 'N/A')
            slot_time = stream.get('slot_time', 'N/A')
            enabled_streams.append({
                'stream_id': stream_id,
                'instrument': instrument,
                'session': session,
                'slot_time': slot_time
            })

print(f"\nEnabled Streams: {len(enabled_streams)}")
for s in enabled_streams:
    print(f"  {s['stream_id']} ({s['instrument']}) - {s['session']} @ {s['slot_time']}")

# Load all events
print("\nLoading events...")
engine_log = "logs/robot/robot_ENGINE.jsonl"
all_events = []

with open(engine_log, 'r', encoding='utf-8-sig') as f:
    for line in f:
        try:
            e = json.loads(line.strip())
            all_events.append(e)
        except:
            pass

# Load instrument-specific logs
instrument_logs = glob.glob("logs/robot/robot_*.jsonl")
instrument_events = defaultdict(list)

for log_file in instrument_logs:
    if 'ENGINE' in log_file:
        continue
    
    instrument = log_file.split('_')[-1].replace('.jsonl', '')
    with open(log_file, 'r', encoding='utf-8-sig') as f:
        for line in f:
            try:
                e = json.loads(line.strip())
                instrument_events[instrument].append(e)
            except:
                pass

print(f"Loaded {len(all_events)} ENGINE events")
for inst, events in instrument_events.items():
    print(f"  {inst}: {len(events)} events")

# Build timeline for each enabled stream
print("\n" + "=" * 100)
print("STREAM TIMELINES")
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
    'RANGE_BUILDING', 'HYDRATION_COMPLETE', 'STREAM_DONE',
    'BAR_BUFFER_ADD', 'BAR_BUFFER_REJECTED', 'STREAM_SKIPPED'
]

for stream_info in enabled_streams:
    stream_id = stream_info['stream_id']
    instrument = stream_info['instrument']
    
    print(f"\n{'='*100}")
    print(f"STREAM: {stream_id} ({instrument})")
    print(f"  Session: {stream_info['session']}, Slot Time: {stream_info['slot_time']}")
    print(f"{'='*100}")
    
    # Collect all events for this stream
    stream_timeline = []
    
    # Check ENGINE log
    for e in all_events:
        stream_field = e.get('stream', '')
        if stream_id in str(stream_field):
            ts = e.get('ts') or e.get('ts_utc', '')
            event_type = e.get('event') or e.get('event_type', '')
            if ts and any(key in str(event_type) for key in key_events):
                stream_timeline.append({
                    'timestamp': ts,
                    'event_type': event_type,
                    'source': 'ENGINE',
                    'data': e
                })
    
    # Check instrument-specific log
    if instrument in instrument_events:
        for e in instrument_events[instrument]:
            stream_field = e.get('stream') or e.get('data', {}).get('stream_id', '')
            if stream_id in str(stream_field):
                ts = e.get('ts') or e.get('ts_utc', '')
                event_type = e.get('event') or e.get('event_type', '')
                if ts and any(key in str(event_type) for key in key_events):
                    stream_timeline.append({
                        'timestamp': ts,
                        'event_type': event_type,
                        'source': instrument,
                        'data': e
                    })
    
    # Also check for events that mention this stream in payload
    for inst, events in instrument_events.items():
        for e in events:
            data = e.get('data') or {}
            payload_str = str(e.get('payload') or data.get('payload', ''))
            if stream_id in payload_str:
                ts = e.get('ts') or e.get('ts_utc', '')
                event_type = e.get('event') or e.get('event_type', '')
                if ts and any(key in str(event_type) for key in key_events):
                    # Avoid duplicates
                    if not any(t['timestamp'] == ts and t['event_type'] == event_type for t in stream_timeline):
                        stream_timeline.append({
                            'timestamp': ts,
                            'event_type': event_type,
                            'source': inst,
                            'data': e
                        })
    
    # Sort by timestamp
    stream_timeline.sort(key=lambda x: parse_timestamp(x['timestamp']) or datetime.min.replace(tzinfo=timezone.utc))
    
    # Display timeline
    if stream_timeline:
        print(f"\nTimeline ({len(stream_timeline)} events):")
        print("-" * 100)
        
        # Group by event type for summary
        event_counts = defaultdict(int)
        for event in stream_timeline:
            event_counts[event['event_type']] += 1
        
        print("\nEvent Summary:")
        for event_type, count in sorted(event_counts.items()):
            print(f"  {event_type}: {count}")
        
        print("\nDetailed Timeline (last 40 events):")
        for event in stream_timeline[-40:]:
            ts = event['timestamp']
            event_type = event['event_type']
            source = event['source']
            
            try:
                dt = parse_timestamp(ts)
                ts_display = dt.strftime("%H:%M:%S") if dt else ts[:19]
            except:
                ts_display = ts[:19] if len(ts) > 19 else ts
            
            print(f"  [{ts_display}] [{source:6}] {event_type}")
            
            # Show key details
            if 'BARSREQUEST_EXECUTED' in event_type:
                payload_str = str(event['data'].get('payload') or event['data'].get('data', {}).get('payload', ''))
                bars_match = re.search(r'bars_returned\s*=\s*(\d+)', payload_str)
                if bars_match:
                    print(f"      Bars Returned: {bars_match.group(1)}")
            
            if 'RANGE_LOCKED' in event_type:
                print(f"      *** RANGE LOCKED ***")
            
            if 'STREAM_ARMED' in event_type:
                print(f"      *** STREAM ARMED ***")
            
            if 'STREAM_SKIPPED' in event_type:
                payload_str = str(event['data'].get('payload') or event['data'].get('data', {}).get('payload', ''))
                reason_match = re.search(r'reason\s*=\s*([^,}]+)', payload_str)
                master_match = re.search(r'ninjatrader_master_instrument\s*=\s*([^,}]+)', payload_str)
                if reason_match:
                    print(f"      Reason: {reason_match.group(1).strip()}")
                if master_match:
                    print(f"      NT Master: {master_match.group(1).strip()}")
    else:
        print("\n[WARN] No timeline events found for this stream")
    
    # Assess problems
    print(f"\nProblem Assessment:")
    print("-" * 100)
    
    problems = []
    warnings = []
    
    # Check if stream was created
    created = any('STREAM_CREATED' in str(e['event_type']) for e in stream_timeline)
    if not created:
        # Check if it was skipped
        skipped = any('STREAM_SKIPPED' in str(e['event_type']) for e in stream_timeline)
        if skipped:
            problems.append("Stream was skipped (not created)")
        else:
            problems.append("No stream creation events found")
    else:
        print("[OK] Stream created")
    
    # Check if stream was armed
    armed = any('STREAM_ARMED' in str(e['event_type']) or 'ARMED' in str(e['event_type']) for e in stream_timeline)
    if not armed:
        problems.append("Stream not armed")
    else:
        print("[OK] Stream armed")
    
    # Check BarsRequest
    barsrequest = [e for e in stream_timeline if 'BARSREQUEST' in str(e['event_type'])]
    if not barsrequest:
        problems.append("No BarsRequest executed")
    else:
        print(f"[OK] BarsRequest executed: {len(barsrequest)} event(s)")
        for br in barsrequest:
            payload_str = str(br['data'].get('payload') or br['data'].get('data', {}).get('payload', ''))
            bars_match = re.search(r'bars_returned\s*=\s*(\d+)', payload_str)
            if bars_match:
                bars_count = int(bars_match.group(1))
                if bars_count == 0:
                    problems.append(f"BarsRequest returned 0 bars")
                else:
                    print(f"  Bars returned: {bars_count}")
    
    # Check bar reception
    bars_received = [e for e in stream_timeline if 'BAR_ACCEPTED' in str(e['event_type']) or 'BAR_RECEIVED' in str(e['event_type'])]
    if not bars_received:
        problems.append("No bars received/accepted")
    else:
        print(f"[OK] Bars received/accepted: {len(bars_received)} event(s)")
        # Check recent activity
        recent_cutoff = datetime.now(timezone.utc).timestamp() - 3600
        recent_bars = [e for e in bars_received if parse_timestamp(e['timestamp']) and 
                      parse_timestamp(e['timestamp']).timestamp() > recent_cutoff]
        if not recent_bars:
            warnings.append("No bars in last hour")
        else:
            print(f"  Recent bars (last hour): {len(recent_bars)}")
    
    # Check range lock
    range_locked = any('RANGE_LOCKED' in str(e['event_type']) for e in stream_timeline)
    if not range_locked:
        warnings.append("Range not locked (may be too early)")
    else:
        print("[OK] Range locked")
    
    # Check hydration
    hydration = [e for e in stream_timeline if 'HYDRATION' in str(e['event_type'])]
    if hydration:
        print(f"[OK] Hydration events: {len(hydration)}")
    
    # Check for errors
    errors = [e for e in stream_timeline if 'ERROR' in str(e['event_type']) or e['data'].get('level') == 'ERROR']
    if errors:
        problems.append(f"{len(errors)} error event(s) found")
        print(f"[WARN] Errors found: {len(errors)}")
    
    # Check for rejections
    rejections = [e for e in stream_timeline if 'REJECTED' in str(e['event_type']) or 'REJECTION' in str(e['event_type'])]
    if rejections:
        print(f"[INFO] Rejections: {len(rejections)} event(s)")
    
    if problems:
        print("\n[ERROR] Problems detected:")
        for problem in problems:
            print(f"  - {problem}")
    
    if warnings:
        print("\n[WARN] Warnings:")
        for warning in warnings:
            print(f"  - {warning}")
    
    if not problems and not warnings:
        print("\n[OK] No problems detected - stream appears healthy")

print("\n" + "=" * 100)
print("SUMMARY")
print("=" * 100)
print(f"Total enabled streams: {len(enabled_streams)}")
print(f"Streams with events: {sum(1 for s in enabled_streams if True)}")

print("\n" + "=" * 100)
