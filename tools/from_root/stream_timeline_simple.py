#!/usr/bin/env python3
"""Simplified stream timeline analysis"""
import json
import glob
import re
from datetime import datetime, timezone
from collections import defaultdict

print("=" * 100)
print("STREAM TIMELINE ANALYSIS")
print("=" * 100)

# Get streams from STREAMS_CREATED events
engine_log = "logs/robot/robot_ENGINE.jsonl"
with open(engine_log, 'r', encoding='utf-8-sig') as f:
    events = [json.loads(l) for l in f if l.strip()]

# Find STREAMS_CREATED events from today
today_streams_created = [e for e in events if 'STREAMS_CREATED' in str(e.get('event', '')) and '2026-02-02' in str(e.get('ts', e.get('ts_utc', '')))]

print(f"Found {len(today_streams_created)} STREAMS_CREATED events today")

# Extract stream IDs from payload
enabled_streams = set()
for e in today_streams_created:
    payload_str = str(e.get('payload') or e.get('data', {}).get('payload', ''))
    # Look for stream patterns like GC1, GC2, CL1, etc.
    stream_matches = re.findall(r'([A-Z]{1,3}\d+)', payload_str)
    enabled_streams.update(stream_matches)

# Also check instrument logs for stream IDs
instrument_logs = glob.glob("logs/robot/robot_*.jsonl")
for log_file in instrument_logs:
    if 'ENGINE' in log_file:
        continue
    instrument = log_file.split('_')[-1].replace('.jsonl', '')
    with open(log_file, 'r', encoding='utf-8-sig') as f:
        for line in f:
            if '2026-02-02' in line and ('STREAM_CREATED' in line or 'stream_id' in line):
                try:
                    e = json.loads(line.strip())
                    stream_id = e.get('stream') or e.get('data', {}).get('stream_id', '')
                    if stream_id:
                        enabled_streams.add(stream_id)
                    # Also check payload string
                    payload_str = str(e.get('payload') or e.get('data', {}).get('payload', ''))
                    stream_matches = re.findall(r'([A-Z]{1,3}\d+)', payload_str)
                    enabled_streams.update(stream_matches)
                except:
                    pass

print(f"\nEnabled streams found: {sorted(enabled_streams)}")

# If no streams found, use common patterns
if not enabled_streams:
    print("\n[WARN] No streams found in STREAMS_CREATED, checking instrument logs...")
    # Check which instruments have activity
    active_instruments = set()
    for log_file in instrument_logs:
        if 'ENGINE' in log_file:
            continue
        instrument = log_file.split('_')[-1].replace('.jsonl', '')
        with open(log_file, 'r', encoding='utf-8-sig') as f:
            lines = f.readlines()
            recent_lines = [l for l in lines if '2026-02-02T1' in l]  # Today afternoon
            if len(recent_lines) > 100:  # Has significant activity
                active_instruments.add(instrument)
                # Assume streams exist: INSTRUMENT1 and INSTRUMENT2
                enabled_streams.add(f"{instrument}1")
                enabled_streams.add(f"{instrument}2")

print(f"Streams to analyze: {sorted(enabled_streams)}")

# Build timeline for each stream
for stream_id in sorted(enabled_streams):
    print(f"\n{'='*100}")
    print(f"STREAM: {stream_id}")
    print(f"{'='*100}")
    
    timeline = []
    
    # Check all logs
    all_logs = [engine_log] + instrument_logs
    for log_file in all_logs:
        with open(log_file, 'r', encoding='utf-8-sig') as f:
            for line in f:
                if stream_id in line and '2026-02-02' in line:
                    try:
                        e = json.loads(line.strip())
                        ts = e.get('ts') or e.get('ts_utc', '')
                        event_type = e.get('event') or e.get('event_type', '')
                        if ts and event_type:
                            timeline.append({
                                'timestamp': ts,
                                'event_type': event_type,
                                'source': log_file.split('_')[-1].replace('.jsonl', '') if '_' in log_file else 'ENGINE',
                                'data': e
                            })
                    except:
                        pass
    
    # Sort by timestamp
    def parse_ts(ts_str):
        try:
            return datetime.fromisoformat(ts_str.replace('Z', '+00:00'))
        except:
            return datetime.min.replace(tzinfo=timezone.utc)
    
    timeline.sort(key=lambda x: parse_ts(x['timestamp']))
    
    # Display key events
    key_events = ['CREATED', 'ARMED', 'BARSREQUEST', 'RANGE_LOCKED', 'HYDRATION', 'DONE', 'ERROR', 'REJECTED']
    
    print(f"\nTimeline ({len(timeline)} total events, showing key events):")
    print("-" * 100)
    
    shown_events = []
    for event in timeline:
        event_type = str(event['event_type'])
        if any(key in event_type for key in key_events):
            ts = event['timestamp']
            try:
                dt = parse_ts(ts)
                ts_display = dt.strftime("%H:%M:%S")
            except:
                ts_display = ts[:19] if len(ts) > 19 else ts
            
            source = event['source'][:6]
            print(f"  [{ts_display}] [{source}] {event_type}")
            shown_events.append(event_type)
            
            # Show details for important events
            if 'BARSREQUEST_EXECUTED' in event_type:
                payload_str = str(event['data'].get('payload') or event['data'].get('data', {}).get('payload', ''))
                bars_match = re.search(r'bars_returned\s*=\s*(\d+)', payload_str)
                if bars_match:
                    print(f"      -> Bars Returned: {bars_match.group(1)}")
    
    # Problem assessment
    print(f"\nProblem Assessment:")
    print("-" * 100)
    
    has_created = any('CREATED' in str(e['event_type']) for e in timeline)
    has_armed = any('ARMED' in str(e['event_type']) for e in timeline)
    has_barsrequest = any('BARSREQUEST' in str(e['event_type']) for e in timeline)
    has_range_locked = any('RANGE_LOCKED' in str(e['event_type']) for e in timeline)
    has_errors = any('ERROR' in str(e['event_type']) or e['data'].get('level') == 'ERROR' for e in timeline)
    
    problems = []
    
    if has_created:
        print("[OK] Stream created")
    else:
        problems.append("Stream creation not found")
    
    if has_armed:
        print("[OK] Stream armed")
    else:
        problems.append("Stream not armed")
    
    if has_barsrequest:
        print("[OK] BarsRequest executed")
    else:
        problems.append("No BarsRequest executed")
    
    if has_range_locked:
        print("[OK] Range locked")
    else:
        problems.append("Range not locked")
    
    if has_errors:
        error_count = sum(1 for e in timeline if 'ERROR' in str(e['event_type']) or e['data'].get('level') == 'ERROR')
        problems.append(f"{error_count} error event(s)")
        print(f"[WARN] {error_count} error event(s) found")
    
    if problems:
        print("\n[WARN] Problems:")
        for p in problems:
            print(f"  - {p}")
    else:
        print("\n[OK] No problems detected")

print("\n" + "=" * 100)
