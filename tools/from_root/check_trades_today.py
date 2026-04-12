#!/usr/bin/env python3
"""Check actual trades executed today"""
import json
import glob
from pathlib import Path

print("=" * 100)
print("TRADE EXECUTION ANALYSIS - February 2, 2026")
print("=" * 100)
print()

# Check journal files for today
journal_dir = Path("logs/robot/journal")
today_journals = list(journal_dir.glob("2026-02-02_*.json"))

print(f"Found {len(today_journals)} journal files for today:")
print()

streams_with_activity = {}
for journal_file in sorted(today_journals):
    stream_id = journal_file.stem.split('_', 1)[1]
    with open(journal_file, 'r', encoding='utf-8') as f:
        data = json.load(f)
    
    streams_with_activity[stream_id] = {
        'state': data.get('LastState', 'N/A'),
        'entry_detected': data.get('EntryDetected', False),
        'stop_brackets_submitted': data.get('StopBracketsSubmittedAtLock', False),
        'last_update': data.get('LastUpdateUtc', 'N/A')
    }
    
    status = "[ACTIVE]" if data.get('LastState') == 'RANGE_LOCKED' else "[OTHER]"
    entry = "ENTRY" if data.get('EntryDetected') else "NO ENTRY"
    print(f"  {status} {stream_id}: {data.get('LastState', 'N/A')} - {entry} - Updated: {data.get('LastUpdateUtc', 'N/A')[:19]}")

print()
print("=" * 100)
print("CHECKING INSTRUMENT LOGS FOR ORDER SUBMISSIONS")
print("=" * 100)
print()

# Check instrument logs for order submissions
instruments = ['CL', 'ES', 'GC', 'NQ', 'NG', 'RTY', 'YM']
for instrument in instruments:
    log_file = Path(f"logs/robot/robot_{instrument}.jsonl")
    if not log_file.exists():
        continue
    
    events = []
    with open(log_file, 'r', encoding='utf-8-sig') as f:
        for line in f:
            try:
                e = json.loads(line.strip())
                if '2026-02-02' in str(e.get('ts_utc', '')):
                    events.append(e)
            except:
                pass
    
    if not events:
        continue
    
    # Check for key events
    range_locked = [e for e in events if 'RANGE_LOCKED' in str(e.get('event', ''))]
    order_submit = [e for e in events if 'ORDER_SUBMIT' in str(e.get('event', ''))]
    intent_registered = [e for e in events if 'INTENT_REGISTERED' in str(e.get('event', ''))]
    execution_filled = [e for e in events if 'EXECUTION_FILLED' in str(e.get('event', ''))]
    
    if range_locked or order_submit or intent_registered:
        print(f"\n{instrument}:")
        print(f"  RANGE_LOCKED: {len(range_locked)}")
        if range_locked:
            last = range_locked[-1]
            print(f"    Last: {last.get('ts_utc', 'N/A')[:19]} - Stream: {last.get('stream', 'N/A')}")
        print(f"  ORDER_SUBMIT: {len(order_submit)}")
        if order_submit:
            last = order_submit[-1]
            print(f"    Last: {last.get('ts_utc', 'N/A')[:19]} - Event: {last.get('event', 'N/A')}")
        print(f"  INTENT_REGISTERED: {len(intent_registered)}")
        print(f"  EXECUTION_FILLED: {len(execution_filled)}")

print()
print("=" * 100)
print("SUMMARY")
print("=" * 100)
print()

active_streams = [s for s, d in streams_with_activity.items() if d['state'] == 'RANGE_LOCKED']
streams_with_entries = [s for s, d in streams_with_activity.items() if d['entry_detected']]

print(f"Streams with RANGE_LOCKED: {len(active_streams)}")
for stream in sorted(active_streams):
    print(f"  - {stream}")

print(f"\nStreams with EntryDetected: {len(streams_with_entries)}")
for stream in sorted(streams_with_entries):
    print(f"  - {stream}")

print()
print("=" * 100)
