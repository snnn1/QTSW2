#!/usr/bin/env python3
"""Detailed stream timeline analysis"""
import json
import glob
import re
from datetime import datetime, timezone
from collections import defaultdict

print("=" * 100)
print("DETAILED STREAM TIMELINE ANALYSIS")
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

# Find today's streams from STREAM_CREATED events
print("\n" + "=" * 100)
print("IDENTIFYING TODAY'S STREAMS")
print("=" * 100)

today = datetime.now(timezone.utc).date()
today_streams = []

# Check ENGINE log for STREAMS_CREATED
for e in all_events:
    if 'STREAMS_CREATED' in str(e.get('event', '')):
        ts_str = e.get('ts') or e.get('ts_utc', '')
        if ts_str and today.isoformat() in ts_str:
            payload_str = str(e.get('payload') or e.get('data', {}).get('payload', ''))
            # Try to extract stream info from payload
            if 'streams' in payload_str.lower():
                today_streams.append({
                    'source': 'STREAMS_CREATED',
                    'timestamp': ts_str,
                    'event': e
                })

# Check instrument logs for STREAM_CREATED
for inst, events in instrument_events.items():
    for e in events:
        if 'STREAM_CREATED' in str(e.get('event', '')):
            ts_str = e.get('ts') or e.get('ts_utc', '')
            if ts_str:
                try:
                    ts = datetime.fromisoformat(ts_str.replace('Z', '+00:00'))
                    if ts.date() == today:
                        stream_id = e.get('stream') or e.get('data', {}).get('stream_id') or 'N/A'
                        if stream_id and stream_id != 'N/A':
                            today_streams.append({
                                'stream': stream_id,
                                'instrument': inst,
                                'timestamp': ts_str,
                                'event': e
                            })
                except:
                    pass

# Get unique streams
unique_streams = {}
for s in today_streams:
    if 'stream' in s:
        stream_id = s['stream']
        if stream_id not in unique_streams:
            unique_streams[stream_id] = s

print(f"\nFound {len(unique_streams)} unique streams created today:")
for stream_id in sorted(unique_streams.keys()):
    s = unique_streams[stream_id]
    print(f"  {stream_id} ({s.get('instrument', 'N/A')})")

# If no streams found, try to infer from recent activity
if not unique_streams:
    print("\nNo STREAM_CREATED events found. Checking recent activity...")
    # Look for recent BAR events to infer active streams
    recent_bar_streams = defaultdict(list)
    cutoff = datetime.now(timezone.utc).timestamp() - 3600  # Last hour
    
    for inst, events in instrument_events.items():
        for e in events:
            ts_str = e.get('ts') or e.get('ts_utc', '')
            if ts_str:
                try:
                    ts = datetime.fromisoformat(ts_str.replace('Z', '+00:00')).timestamp()
                    if ts > cutoff:
                        stream_field = e.get('stream') or e.get('data', {}).get('stream_id') or ''
                        if stream_field and ('BAR' in str(e.get('event', '')) or 'HYDRATION' in str(e.get('event', ''))):
                            recent_bar_streams[stream_field].append({
                                'timestamp': ts_str,
                                'event_type': e.get('event') or e.get('event_type', 'N/A'),
                                'instrument': inst
                            })
                except:
                    pass
    
    if recent_bar_streams:
        print(f"\nFound {len(recent_bar_streams)} streams with recent activity:")
        for stream_id in sorted(recent_bar_streams.keys()):
            events = recent_bar_streams[stream_id]
            inst = events[0]['instrument'] if events else 'N/A'
            print(f"  {stream_id} ({inst}) - {len(events)} recent events")
            unique_streams[stream_id] = {
                'stream': stream_id,
                'instrument': inst,
                'timestamp': events[0]['timestamp'] if events else 'N/A'
            }

# Build detailed timeline for each stream
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

# Key events to track
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
    'CANONICAL_MISMATCH',
    'BAR_BUFFER_ADD',
    'BAR_BUFFER_REJECTED'
]

for stream_id, stream_info in sorted(unique_streams.items()):
    instrument = stream_info.get('instrument', 'N/A')
    
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
            stream_field = e.get('stream') or e.get('data', {}).get('stream_id', '')
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
        
        # Group by event type for summary
        event_counts = defaultdict(int)
        for event in stream_timeline:
            event_counts[event['event_type']] += 1
        
        print("\nEvent Summary:")
        for event_type, count in sorted(event_counts.items()):
            print(f"  {event_type}: {count}")
        
        print("\nDetailed Timeline (last 30 events):")
        for event in stream_timeline[-30:]:
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
            
            if 'BAR_ACCEPTED' in event_type:
                payload_str = str(event['data'].get('payload') or event['data'].get('data', {}).get('payload', ''))
                print(f"      Bar accepted")
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
        # Check recent bar activity
        recent_bars = [e for e in bars_received if parse_timestamp(e['timestamp']) and 
                      (datetime.now(timezone.utc) - parse_timestamp(e['timestamp'])).total_seconds() < 3600]
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
        rejection_reasons = defaultdict(int)
        for r in rejections:
            payload_str = str(r['data'].get('payload') or r['data'].get('data', {}).get('payload', ''))
            reason_match = re.search(r'reason\s*=\s*([^,}]+)', payload_str)
            if reason_match:
                rejection_reasons[reason_match.group(1).strip()] += 1
        if rejection_reasons:
            print("  Rejection reasons:")
            for reason, count in sorted(rejection_reasons.items()):
                print(f"    {reason}: {count}")
    
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

# Summary
print("\n" + "=" * 100)
print("SUMMARY")
print("=" * 100)
print(f"Total streams analyzed: {len(unique_streams)}")

print("\n" + "=" * 100)
