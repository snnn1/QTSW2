#!/usr/bin/env python3
"""Check why streams are being skipped"""
import json
import re

log_file = "logs/robot/robot_ENGINE.jsonl"

with open(log_file, 'r', encoding='utf-8-sig') as f:
    events = [json.loads(l) for l in f if l.strip()]

print("=" * 80)
print("STREAM SKIPPING ANALYSIS")
print("=" * 80)

# Find TIMETABLE_PARSING_COMPLETE events
parsing_complete = [e for e in events if e.get('event_type') == 'TIMETABLE_PARSING_COMPLETE' or e.get('event') == 'TIMETABLE_PARSING_COMPLETE']

print(f"\nFound {len(parsing_complete)} TIMETABLE_PARSING_COMPLETE events")
print("\nLast 5 TIMETABLE_PARSING_COMPLETE events:")
for e in parsing_complete[-5:]:
    payload_str = str(e.get('payload') or e.get('data', {}).get('payload', ''))
    ts = e.get('ts') or e.get('ts_utc', 'N/A')
    print(f"\n  [{ts}]")
    print(f"    {payload_str[:500]}")
    
    # Try to extract accepted/skipped from string
    accepted_match = re.search(r'accepted\s*=\s*(\d+)', payload_str)
    skipped_match = re.search(r'skipped\s*=\s*(\d+)', payload_str)
    if accepted_match:
        print(f"    Accepted: {accepted_match.group(1)}")
    if skipped_match:
        print(f"    Skipped: {skipped_match.group(1)}")

# Check for any STREAM_SKIPPED events in instrument logs
print("\n" + "=" * 80)
print("Checking instrument-specific logs for STREAM_SKIPPED events...")

import glob
instrument_logs = glob.glob("logs/robot/robot_*.jsonl")
for log_file in instrument_logs:
    if 'ENGINE' in log_file:
        continue
    with open(log_file, 'r', encoding='utf-8-sig') as f:
        for line in f:
            if 'STREAM_SKIPPED' in line or 'CANONICAL_MISMATCH' in line:
                try:
                    e = json.loads(line.strip())
                    payload_str = str(e.get('payload') or e.get('data', {}).get('payload', ''))
                    ts = e.get('ts') or e.get('ts_utc', 'N/A')
                    print(f"\n[{log_file}] [{ts}]")
                    print(f"  {payload_str[:400]}")
                except:
                    pass

print("\n" + "=" * 80)
print("Checking for master instrument name in logs...")

# Look for any events that mention master instrument
for e in events[-1000:]:
    payload_str = str(e.get('payload') or e.get('data', {}).get('payload', ''))
    if 'master_instrument' in payload_str.lower() or 'ninjatrader_master' in payload_str.lower():
        ts = e.get('ts') or e.get('ts_utc', 'N/A')
        event_type = e.get('event_type') or e.get('event', 'N/A')
        print(f"\n[{ts}] {event_type}")
        print(f"  {payload_str[:400]}")
