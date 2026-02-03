#!/usr/bin/env python3
"""Check which streams are working vs being skipped"""
import json
import glob
import re
from collections import defaultdict

print("=" * 80)
print("STREAM STATUS ANALYSIS")
print("=" * 80)

# Check ENGINE log for TIMETABLE_PARSING_COMPLETE
engine_log = "logs/robot/robot_ENGINE.jsonl"
with open(engine_log, 'r', encoding='utf-8-sig') as f:
    events = [json.loads(l) for l in f if l.strip()]

# Find recent TIMETABLE_PARSING_COMPLETE events
parsing_complete = [e for e in events if e.get('event_type') == 'TIMETABLE_PARSING_COMPLETE' or e.get('event') == 'TIMETABLE_PARSING_COMPLETE']

print(f"\nFound {len(parsing_complete)} TIMETABLE_PARSING_COMPLETE events")
print("\nLast 5 TIMETABLE_PARSING_COMPLETE events:")
for e in parsing_complete[-5:]:
    payload_str = str(e.get('payload') or e.get('data', {}).get('payload', ''))
    ts = e.get('ts') or e.get('ts_utc', 'N/A')
    accepted_match = re.search(r'accepted\s*=\s*(\d+)', payload_str)
    skipped_match = re.search(r'skipped\s*=\s*(\d+)', payload_str)
    total_match = re.search(r'total_enabled\s*=\s*(\d+)', payload_str)
    
    print(f"\n  [{ts}]")
    if total_match:
        print(f"    Total Enabled: {total_match.group(1)}")
    if accepted_match:
        print(f"    Accepted: {accepted_match.group(1)}")
    if skipped_match:
        print(f"    Skipped: {skipped_match.group(1)}")

# Check STREAM_CREATED events
stream_created = [e for e in events if e.get('event_type') == 'STREAM_CREATED' or e.get('event') == 'STREAM_CREATED']
print(f"\n\nFound {len(stream_created)} STREAM_CREATED events")
if stream_created:
    print("\nLast 10 STREAM_CREATED events:")
    for e in stream_created[-10:]:
        ts = e.get('ts') or e.get('ts_utc', 'N/A')
        stream = e.get('stream', 'N/A')
        instrument = e.get('instrument', 'N/A')
        print(f"  [{ts}] Stream: {stream}, Instrument: {instrument}")

# Check STREAMS_CREATED events
streams_created = [e for e in events if e.get('event_type') == 'STREAMS_CREATED' or e.get('event') == 'STREAMS_CREATED']
print(f"\n\nFound {len(streams_created)} STREAMS_CREATED events")
if streams_created:
    print("\nLast STREAMS_CREATED event:")
    e = streams_created[-1]
    payload_str = str(e.get('payload') or e.get('data', {}).get('payload', ''))
    ts = e.get('ts') or e.get('ts_utc', 'N/A')
    print(f"  [{ts}]")
    print(f"    {payload_str[:600]}")

# Check instrument-specific logs for STREAM_SKIPPED events
print("\n" + "=" * 80)
print("Checking instrument-specific logs for STREAM_SKIPPED events...")

instrument_logs = glob.glob("logs/robot/robot_*.jsonl")
instrument_skipped = defaultdict(list)

for log_file in instrument_logs:
    if 'ENGINE' in log_file:
        continue
    
    instrument = log_file.split('_')[-1].replace('.jsonl', '')
    with open(log_file, 'r', encoding='utf-8-sig') as f:
        for line in f:
            if 'STREAM_SKIPPED' in line or 'CANONICAL_MISMATCH' in line:
                try:
                    e = json.loads(line.strip())
                    payload_str = str(e.get('payload') or e.get('data', {}).get('payload', ''))
                    ts = e.get('ts') or e.get('ts_utc', 'N/A')
                    
                    # Only show recent events (today)
                    if '2026-02-02' in ts:
                        instrument_skipped[instrument].append((ts, payload_str))
                except:
                    pass

print(f"\nFound STREAM_SKIPPED events for {len(instrument_skipped)} instruments:")
for instrument, events in sorted(instrument_skipped.items()):
    print(f"\n  {instrument}: {len(events)} skipped events")
    # Show most recent
    for ts, payload in events[-3:]:
        # Extract key info
        reason_match = re.search(r'reason\s*=\s*([^,}]+)', payload)
        master_match = re.search(r'ninjatrader_master_instrument\s*=\s*([^,}]+)', payload)
        canonical_match = re.search(r'timetable_canonical\s*=\s*([^,}]+)', payload)
        stream_match = re.search(r'stream_id\s*=\s*([^,}]+)', payload)
        
        print(f"    [{ts}]")
        if stream_match:
            print(f"      Stream: {stream_match.group(1).strip()}")
        if reason_match:
            print(f"      Reason: {reason_match.group(1).strip()}")
        if master_match:
            print(f"      NT Master: {master_match.group(1).strip()}")
        if canonical_match:
            print(f"      Timetable Canonical: {canonical_match.group(1).strip()}")

# Check which instruments have successful stream creation
print("\n" + "=" * 80)
print("Checking which instruments have successful streams...")

instrument_streams = defaultdict(list)
for e in stream_created:
    instrument = e.get('instrument', 'N/A')
    stream = e.get('stream', 'N/A')
    ts = e.get('ts') or e.get('ts_utc', 'N/A')
    if '2026-02-02' in ts:
        instrument_streams[instrument].append(stream)

print(f"\nInstruments with successful stream creation (today):")
for instrument, streams in sorted(instrument_streams.items()):
    print(f"  {instrument}: {len(streams)} streams - {', '.join(streams)}")

if not instrument_streams:
    print("  No streams created today")

print("\n" + "=" * 80)
