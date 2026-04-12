#!/usr/bin/env python3
"""Stream timeline analysis and problem assessment"""
import json
import glob
import re
from datetime import datetime, timezone
from collections import defaultdict, OrderedDict

print("=" * 100)
print("STREAM TIMELINE ANALYSIS")
print("=" * 100)
print(f"Analysis Time: {datetime.now(timezone.utc).isoformat()}")
print()

# Load all events
print("Loading events...")
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

# Find enabled streams from timetable
print("\n" + "=" * 100)
print("IDENTIFYING ENABLED STREAMS")
print("=" * 100)

# Get latest TIMETABLE_PARSING_COMPLETE to see what streams are enabled
timetable_events = [e for e in all_events if 'TIMETABLE_PARSING_COMPLETE' in str(e.get('event', ''))]
if timetable_events:
    latest_timetable = timetable_events[-1]
    payload_str = str(latest_timetable.get('payload') or latest_timetable.get('data', {}).get('payload', ''))
    ts = latest_timetable.get('ts') or latest_timetable.get('ts_utc', 'N/A')
    print(f"Latest TIMETABLE_PARSING_COMPLETE: {ts}")
    
    # Extract accepted streams count
    accepted_match = re.search(r'accepted\s*=\s*(\d+)', payload_str)
    if accepted_match:
        print(f"Accepted Streams: {accepted_match.group(1)}")

# Find all STREAM_CREATED events to identify which streams exist
print("\nFinding created streams...")
stream_created_events = []
for inst, events in instrument_events.items():
    for e in events:
        if 'STREAM_CREATED' in str(e.get('event', '')):
            stream_id = e.get('stream') or e.get('data', {}).get('stream_id') or 'N/A'
            instrument = e.get('instrument', inst)
            ts = e.get('ts') or e.get('ts_utc', 'N/A')
            if stream_id != 'N/A' and stream_id:
                stream_created_events.append({
                    'stream': stream_id,
                    'instrument': instrument,
                    'timestamp': ts,
                    'event': e
                })

# Filter to today's streams
today = datetime.now(timezone.utc).date()
today_streams = []
for s in stream_created_events:
    try:
        ts_str = s['timestamp']
        ts = datetime.fromisoformat(ts_str.replace('Z', '+00:00'))
        if ts.date() == today:
            today_streams.append(s)
    except:
        pass

print(f"\nStreams created today: {len(today_streams)}")
for s in today_streams:
    print(f"  {s['stream']} ({s['instrument']})")

# Build timeline for each stream
print("\n" + "=" * 100)
print("STREAM TIMELINES")
print("=" * 100)

def parse_timestamp(ts_str):
    """Parse timestamp string to datetime."""
    try:
        ts_str = ts_str.replace('Z', '+00:00')
        return datetime.fromisoformat(ts_str)
    except:
        return None

# Key events to track for each stream
key_event_types = [
    'STREAM_CREATED',
    'STREAM_ARMED',
    'BAR_RECEIVED',
    'BAR_ACCEPTED',
    'BAR_REJECTED',
    'BARSREQUEST_EXECUTED',
    'RANGE_LOCKED',
    'RANGE_BUILDING',
    'HYDRATION_COMPLETE',
    'STREAM_DONE',
    'STREAM_SKIPPED',
    'CANONICAL_MISMATCH'
]

for stream_info in sorted(today_streams, key=lambda x: x['stream'] or ''):
    stream_id = stream_info['stream']
    instrument = stream_info['instrument']
    
    print(f"\n{'='*100}")
    print(f"STREAM: {stream_id} ({instrument})")
    print(f"{'='*100}")
    
    # Collect all events for this stream
    stream_timeline = []
    
    # Check ENGINE log
    for e in all_events:
        stream_field = e.get('stream', '')
        if stream_id in str(stream_field):
            ts = e.get('ts') or e.get('ts_utc', '')
            event_type = e.get('event') or e.get('event_type', '')
            if ts and any(key in str(event_type) for key in key_event_types):
                stream_timeline.append({
                    'timestamp': ts,
                    'event_type': event_type,
                    'source': 'ENGINE',
                    'data': e
                })
    
    # Check instrument-specific log
    if instrument in instrument_events:
        for e in instrument_events[instrument]:
            stream_field = e.get('stream', '')
            if stream_id in str(stream_field):
                ts = e.get('ts') or e.get('ts_utc', '')
                event_type = e.get('event') or e.get('event_type', '')
                if ts and any(key in str(event_type) for key in key_event_types):
                    stream_timeline.append({
                        'timestamp': ts,
                        'event_type': event_type,
                        'source': instrument,
                        'data': e
                    })
    
    # Sort by timestamp
    stream_timeline.sort(key=lambda x: parse_timestamp(x['timestamp']) or datetime.min.replace(tzinfo=timezone.utc))
    
    # Display timeline
    if stream_timeline:
        print(f"\nTimeline ({len(stream_timeline)} events):")
        print("-" * 100)
        
        for event in stream_timeline[:50]:  # Limit to first 50 events
            ts = event['timestamp']
            event_type = event['event_type']
            source = event['source']
            
            # Format timestamp
            try:
                dt = parse_timestamp(ts)
                if dt:
                    ts_display = dt.strftime("%H:%M:%S")
                else:
                    ts_display = ts[:19] if len(ts) > 19 else ts
            except:
                ts_display = ts[:19] if len(ts) > 19 else ts
            
            print(f"  [{ts_display}] [{source:6}] {event_type}")
            
            # Show key details for important events
            if 'BARSREQUEST_EXECUTED' in event_type:
                payload_str = str(event['data'].get('payload') or event['data'].get('data', {}).get('payload', ''))
                bars_match = re.search(r'bars_returned\s*=\s*(\d+)', payload_str)
                if bars_match:
                    print(f"      Bars Returned: {bars_match.group(1)}")
            
            if 'RANGE_LOCKED' in event_type:
                print(f"      *** RANGE LOCKED ***")
            
            if 'STREAM_ARMED' in event_type:
                print(f"      *** STREAM ARMED ***")
    else:
        print("\n[WARN] No timeline events found for this stream")
    
    # Assess problems
    print(f"\nProblem Assessment:")
    print("-" * 100)
    
    problems = []
    
    # Check if stream was created
    created = any('STREAM_CREATED' in str(e['event_type']) for e in stream_timeline)
    if not created:
        problems.append("Stream creation not found in timeline")
    else:
        print("[OK] Stream created")
    
    # Check if stream was armed
    armed = any('STREAM_ARMED' in str(e['event_type']) for e in stream_timeline)
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
        # Check if bars were returned
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
    bars_received = [e for e in stream_timeline if 'BAR_RECEIVED' in str(e['event_type']) or 'BAR_ACCEPTED' in str(e['event_type'])]
    if not bars_received:
        problems.append("No bars received/accepted")
    else:
        print(f"[OK] Bars received/accepted: {len(bars_received)} event(s)")
    
    # Check range lock
    range_locked = any('RANGE_LOCKED' in str(e['event_type']) for e in stream_timeline)
    if not range_locked:
        problems.append("Range not locked")
    else:
        print("[OK] Range locked")
    
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
        print("\n[WARN] Problems detected:")
        for problem in problems:
            print(f"  - {problem}")
    else:
        print("\n[OK] No problems detected")

# Summary
print("\n" + "=" * 100)
print("SUMMARY")
print("=" * 100)
print(f"Total streams analyzed: {len(today_streams)}")
print(f"Streams with timelines: {sum(1 for s in today_streams if True)}")  # All have timelines

print("\n" + "=" * 100)
