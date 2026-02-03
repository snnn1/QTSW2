#!/usr/bin/env python3
"""Check which streams are actually working"""
import json
import re
import os
import glob
from collections import defaultdict

print("=" * 80)
print("WORKING STREAMS ANALYSIS")
print("=" * 80)

# Check ENGINE log
engine_log = "logs/robot/robot_ENGINE.jsonl"
with open(engine_log, 'r', encoding='utf-8-sig') as f:
    events = [json.loads(l) for l in f if l.strip()]

# Find recent TIMETABLE_PARSING_COMPLETE events (today)
today_parsing = [e for e in events if ('TIMETABLE_PARSING_COMPLETE' in str(e.get('event', '')) or 'TIMETABLE_PARSING_COMPLETE' in str(e.get('event_type', ''))) and '2026-02-02' in str(e.get('ts', e.get('ts_utc', '')))]

print(f"\nFound {len(today_parsing)} TIMETABLE_PARSING_COMPLETE events today")
print("\nRecent events showing accepted streams:")
for e in today_parsing[-10:]:
    payload_str = str(e.get('payload') or e.get('data', {}).get('payload', ''))
    ts = e.get('ts') or e.get('ts_utc', 'N/A')
    accepted_match = re.search(r'accepted\s*=\s*(\d+)', payload_str)
    skipped_match = re.search(r'skipped\s*=\s*(\d+)', payload_str)
    
    if accepted_match and int(accepted_match.group(1)) > 0:
        print(f"\n  [{ts}]")
        print(f"    Accepted: {accepted_match.group(1)}")
        if skipped_match:
            print(f"    Skipped: {skipped_match.group(1)}")

# Check which instruments have successful streams by looking at instrument logs
print("\n" + "=" * 80)
print("Checking instrument-specific logs for successful streams...")

instrument_logs = glob.glob("logs/robot/robot_*.jsonl")
instrument_status = defaultdict(lambda: {'created': 0, 'skipped': 0, 'working': False})

for log_file in instrument_logs:
    if 'ENGINE' in log_file:
        continue
    
    instrument = log_file.split('_')[-1].replace('.jsonl', '')
    with open(log_file, 'r', encoding='utf-8-sig') as f:
        for line in f:
            if '2026-02-02' not in line:
                continue
            
            if 'STREAM_CREATED' in line or 'STREAMS_CREATED' in line:
                instrument_status[instrument]['created'] += 1
                instrument_status[instrument]['working'] = True
            
            if 'CANONICAL_MISMATCH' in line:
                instrument_status[instrument]['skipped'] += 1

print("\nInstrument Status Summary:")
print("-" * 80)
for instrument in sorted(instrument_status.keys()):
    status = instrument_status[instrument]
    working = "[OK] WORKING" if status['working'] else "[SKIP] NOT WORKING"
    print(f"\n{instrument}: {working}")
    print(f"  Streams Created: {status['created']}")
    print(f"  Streams Skipped (CANONICAL_MISMATCH): {status['skipped']}")

# Check for specific GC success
print("\n" + "=" * 80)
print("Checking GC specifically...")

gc_log = "logs/robot/robot_GC.jsonl"
if os.path.exists(gc_log):
    with open(gc_log, 'r', encoding='utf-8-sig') as f:
        gc_events = [json.loads(l) for l in f if l.strip() and '2026-02-02' in l]
    
    gc_streams = [e for e in gc_events if 'GC1' in str(e) or 'GC2' in str(e)]
    gc_created = [e for e in gc_events if 'STREAM_CREATED' in str(e.get('event', '')) or 'STREAMS_CREATED' in str(e.get('event', ''))]
    gc_skipped = [e for e in gc_events if 'CANONICAL_MISMATCH' in str(e)]
    
    print(f"GC Events Today: {len(gc_events)}")
    print(f"GC Streams Created: {len(gc_created)}")
    print(f"GC Streams Skipped: {len(gc_skipped)}")
    
    if gc_created:
        print("\nGC Stream Creation Events:")
        for e in gc_created[-5:]:
            ts = e.get('ts') or e.get('ts_utc', 'N/A')
            print(f"  [{ts}] {e.get('event', e.get('event_type', 'N/A'))}")

print("\n" + "=" * 80)
