#!/usr/bin/env python3
"""Comprehensive timeline analysis for enabled streams from timetable"""
import json
import glob
import re
from datetime import datetime, timezone
from collections import defaultdict
from pathlib import Path

print("=" * 100)
print("ENABLED STREAM TIMELINE ANALYSIS")
print("=" * 100)
print(f"Analysis Time: {datetime.now(timezone.utc).isoformat()}")
print()

# Load timetable to get enabled streams
timetable_path = "data/timetable/timetable_current.json"
with open(timetable_path, 'r', encoding='utf-8') as f:
    timetable = json.load(f)

enabled_streams = {}
for stream_def in timetable.get('streams', []):
    if stream_def.get('enabled', False):
        stream_id = stream_def['stream']
        enabled_streams[stream_id] = {
            'instrument': stream_def['instrument'],
            'session': stream_def['session'],
            'slot_time': stream_def['slot_time']
        }

print(f"Found {len(enabled_streams)} enabled streams:")
for stream_id, info in sorted(enabled_streams.items()):
    print(f"  {stream_id}: {info['instrument']} ({info['session']}, {info['slot_time']})")
print()

# Load ENGINE log
print("Loading ENGINE log...")
engine_log = Path("logs/robot/robot_ENGINE.jsonl")
all_events = []
if engine_log.exists():
    with open(engine_log, 'r', encoding='utf-8-sig') as f:
        for line in f:
            try:
                e = json.loads(line.strip())
                all_events.append(e)
            except:
                pass
print(f"Loaded {len(all_events)} ENGINE events")

# Load instrument-specific logs
print("Loading instrument logs...")
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

for inst, events in instrument_events.items():
    print(f"  {inst}: {len(events)} events")
print()

def parse_timestamp(ts_str):
    """Parse timestamp string to datetime."""
    if not ts_str:
        return None
    try:
        ts_str = str(ts_str).replace('Z', '+00:00')
        return datetime.fromisoformat(ts_str)
    except:
        return None

def parse_payload(payload):
    """Parse payload (can be dict or string)."""
    if isinstance(payload, dict):
        return payload
    if isinstance(payload, str):
        # Try to extract key-value pairs from string
        result = {}
        # Look for common patterns
        for pattern in [
            r'(\w+)\s*=\s*([^,\s}]+)',
            r'"(\w+)"\s*:\s*"([^"]+)"',
            r'"(\w+)"\s*:\s*(\d+)',
        ]:
            matches = re.findall(pattern, payload)
            for key, value in matches:
                result[key] = value
        return result
    return {}

def get_event_field(event, *fields):
    """Get field from event, checking multiple possible locations."""
    for field in fields:
        if field in event:
            return event[field]
        if 'data' in event and isinstance(event['data'], dict):
            if field in event['data']:
                return event['data'][field]
    return None

# Key events to track
key_event_types = [
    'ENGINE_START',
    'SPEC_LOADED',
    'TRADING_DATE_LOCKED',
    'TIMETABLE_LOADED',
    'TIMETABLE_VALIDATED',
    'TIMETABLE_UPDATED',
    'TIMETABLE_PARSING_COMPLETE',
    'STREAMS_CREATION_ATTEMPT',
    'STREAMS_CREATION_NOT_ATTEMPTED',
    'STREAMS_CREATION_SKIPPED',
    'STREAMS_CREATION_FAILED',
    'STREAM_CREATED',
    'STREAMS_CREATED',
    'STREAM_SKIPPED',
    'STREAM_ARMED',
    'BAR_RECEIVED',
    'BAR_ACCEPTED',
    'BAR_REJECTED',
    'BARSREQUEST_REQUESTED',
    'BARSREQUEST_EXECUTED',
    'BARSREQUEST_SKIPPED',
    'BARSREQUEST_FAILED',
    'RANGE_LOCKED',
    'RANGE_BUILDING',
    'HYDRATION_COMPLETE',
    'STREAM_DONE',
    'CANONICAL_MISMATCH',
]

# Build timeline for each enabled stream
print("=" * 100)
print("STREAM TIMELINES")
print("=" * 100)

for stream_id in sorted(enabled_streams.keys()):
    info = enabled_streams[stream_id]
    instrument = info['instrument']
    
    print(f"\n{'='*100}")
    print(f"STREAM: {stream_id} ({instrument}, {info['session']}, {info['slot_time']})")
    print(f"{'='*100}")
    
    # Collect all events for this stream
    stream_timeline = []
    
    # Check ENGINE log for stream-specific events
    for e in all_events:
        event_type = get_event_field(e, 'event', 'event_type', 'type') or ''
        stream_field = get_event_field(e, 'stream', 'stream_id') or ''
        payload = get_event_field(e, 'payload', 'data')
        
        # Check if this event relates to our stream
        matches = False
        if stream_id in str(stream_field):
            matches = True
        elif stream_id in str(payload):
            matches = True
        elif any(key in event_type for key in ['TIMETABLE', 'STREAMS_CREATION', 'ENGINE_START', 'SPEC_LOADED', 'TRADING_DATE']):
            matches = True  # Global events
        
        if matches:
            ts = get_event_field(e, 'ts', 'ts_utc', 'timestamp')
            if ts:
                stream_timeline.append({
                    'timestamp': ts,
                    'event_type': event_type,
                    'source': 'ENGINE',
                    'data': e,
                    'payload': payload
                })
    
    # Check instrument-specific log
    if instrument in instrument_events:
        for e in instrument_events[instrument]:
            event_type = get_event_field(e, 'event', 'event_type', 'type') or ''
            stream_field = get_event_field(e, 'stream', 'stream_id') or ''
            payload = get_event_field(e, 'payload', 'data')
            
            if stream_id in str(stream_field) or stream_id in str(payload):
                ts = get_event_field(e, 'ts', 'ts_utc', 'timestamp')
                if ts:
                    stream_timeline.append({
                        'timestamp': ts,
                        'event_type': event_type,
                        'source': instrument,
                        'data': e,
                        'payload': payload
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
        
        print("\nDetailed Timeline (last 50 events):")
        for event in stream_timeline[-50:]:
            ts = event['timestamp']
            event_type = event['event_type']
            source = event['source']
            
            # Format timestamp
            try:
                dt = parse_timestamp(ts)
                if dt:
                    ts_display = dt.strftime("%Y-%m-%d %H:%M:%S")
                else:
                    ts_display = str(ts)[:19] if len(str(ts)) > 19 else str(ts)
            except:
                ts_display = str(ts)[:19] if len(str(ts)) > 19 else str(ts)
            
            print(f"  [{ts_display}] [{source:6}] {event_type}")
            
            # Show key details for important events
            payload = parse_payload(event.get('payload', {}))
            
            if 'BARSREQUEST_EXECUTED' in event_type:
                bars_returned = payload.get('bars_returned') or payload.get('bars_count')
                if bars_returned:
                    print(f"      Bars Returned: {bars_returned}")
            
            if 'TIMETABLE_PARSING_COMPLETE' in event_type:
                accepted = payload.get('accepted', 'N/A')
                skipped = payload.get('skipped', 'N/A')
                print(f"      Accepted: {accepted}, Skipped: {skipped}")
            
            if 'STREAM_SKIPPED' in event_type:
                reason = payload.get('reason', 'N/A')
                print(f"      Reason: {reason}")
            
            if 'STREAM_CREATED' in event_type or 'STREAMS_CREATED' in event_type:
                print(f"      *** STREAM CREATED ***")
            
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
    warnings = []
    
    # Check if stream was created
    created = any('STREAM_CREATED' in str(e['event_type']) for e in stream_timeline)
    streams_created = any('STREAMS_CREATED' in str(e['event_type']) for e in stream_timeline)
    
    if not created and not streams_created:
        problems.append("Stream creation not found in timeline")
    else:
        print("[OK] Stream creation event found")
    
    # Check timetable parsing
    timetable_parsing = [e for e in stream_timeline if 'TIMETABLE_PARSING_COMPLETE' in str(e['event_type'])]
    if timetable_parsing:
        last_parse = timetable_parsing[-1]
        payload = parse_payload(last_parse.get('payload', {}))
        accepted = payload.get('accepted', 0)
        skipped = payload.get('skipped', 0)
        if accepted == 0 and skipped > 0:
            problems.append(f"Timetable parsing: accepted=0, skipped={skipped}")
        else:
            print(f"[OK] Timetable parsing: accepted={accepted}, skipped={skipped}")
    
    # Check for canonical mismatch
    canonical_mismatches = [e for e in stream_timeline if 'CANONICAL_MISMATCH' in str(e['event_type']) or 'CANONICAL_MISMATCH' in str(e.get('payload', ''))]
    if canonical_mismatches:
        problems.append(f"Canonical mismatch detected: {len(canonical_mismatches)} event(s)")
    else:
        print("[OK] No canonical mismatch")
    
    # Check if stream was armed
    armed = any('STREAM_ARMED' in str(e['event_type']) for e in stream_timeline)
    if not armed:
        warnings.append("Stream not armed (may be too early)")
    else:
        print("[OK] Stream armed")
    
    # Check BarsRequest
    barsrequest_executed = [e for e in stream_timeline if 'BARSREQUEST_EXECUTED' in str(e['event_type'])]
    barsrequest_skipped = [e for e in stream_timeline if 'BARSREQUEST_SKIPPED' in str(e['event_type'])]
    barsrequest_failed = [e for e in stream_timeline if 'BARSREQUEST_FAILED' in str(e['event_type'])]
    
    if barsrequest_executed:
        print(f"[OK] BarsRequest executed: {len(barsrequest_executed)} event(s)")
        # Check if bars were returned
        for br in barsrequest_executed:
            payload = parse_payload(br.get('payload', {}))
            bars_count = payload.get('bars_returned') or payload.get('bars_count') or 0
            if bars_count == 0:
                problems.append("BarsRequest returned 0 bars")
            else:
                print(f"  Bars returned: {bars_count}")
    elif barsrequest_skipped:
        print(f"[WARN] BarsRequest skipped: {len(barsrequest_skipped)} event(s)")
        for br in barsrequest_skipped[-1:]:
            payload = parse_payload(br.get('payload', {}))
            reason = payload.get('reason', 'N/A')
            print(f"  Reason: {reason}")
    elif barsrequest_failed:
        problems.append(f"BarsRequest failed: {len(barsrequest_failed)} event(s)")
    else:
        warnings.append("No BarsRequest executed/skipped")
    
    # Check bar reception
    bars_received = [e for e in stream_timeline if 'BAR_RECEIVED' in str(e['event_type']) or 'BAR_ACCEPTED' in str(e['event_type'])]
    if not bars_received:
        warnings.append("No bars received/accepted")
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
print(f"Total enabled streams analyzed: {len(enabled_streams)}")

print("\n" + "=" * 100)
