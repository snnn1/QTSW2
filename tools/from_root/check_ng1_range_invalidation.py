#!/usr/bin/env python3
"""Check why NG1 range was invalidated"""
import json
from pathlib import Path
from datetime import datetime

log_dir = Path("logs/robot")
events = []
for log_file in log_dir.glob("robot_*.jsonl"):
    try:
        with open(log_file, 'r', encoding='utf-8') as f:
            for line in f:
                line = line.strip()
                if line:
                    try:
                        events.append(json.loads(line))
                    except:
                        pass
    except:
        pass

today_events = [e for e in events if e.get('ts_utc', '').startswith('2026-01-26')]
ng1_events = [e for e in today_events if e.get('stream') == 'NG1']
ng1_events.sort(key=lambda x: x.get('ts_utc', ''))

print("="*80)
print("NG1 RANGE INVALIDATION INVESTIGATION")
print("="*80)

# Find RANGE_INVALIDATED events
invalidated = [e for e in ng1_events if 'RANGE_INVALIDATED' in e.get('event', '')]
if invalidated:
    print(f"\nRANGE_INVALIDATED events found: {len(invalidated)}")
    for inv in invalidated:
        print(f"\n  {inv.get('ts_utc', '')[:19]} - {inv.get('event', 'N/A')}")
        data = inv.get('data', {})
        if isinstance(data, dict):
            print(f"    Reason: {data.get('reason', 'N/A')}")
            print(f"    Message: {data.get('message', 'N/A')}")
            print(f"    All data keys: {list(data.keys())}")

# Find gap-related events
gap_events = [e for e in ng1_events if 'GAP' in e.get('event', '')]
if gap_events:
    print(f"\nGap-related events: {len(gap_events)}")
    for g in gap_events[-10:]:
        print(f"  {g.get('ts_utc', '')[:19]} - {g.get('event', 'N/A')}")
        data = g.get('data', {})
        if isinstance(data, dict):
            print(f"    Gap minutes: {data.get('gap_minutes', 'N/A')}")
            print(f"    Largest gap: {data.get('largest_single_gap_minutes', 'N/A')}")
            print(f"    Total gap: {data.get('total_gap_minutes', 'N/A')}")

# Find RANGE_COMPUTE_FAILED events
compute_failed = [e for e in ng1_events if 'RANGE_COMPUTE_FAILED' in e.get('event', '')]
if compute_failed:
    print(f"\nRANGE_COMPUTE_FAILED events: {len(compute_failed)}")
    for cf in compute_failed[-5:]:
        print(f"  {cf.get('ts_utc', '')[:19]} - {cf.get('event', 'N/A')}")
        data = cf.get('data', {})
        if isinstance(data, dict):
            print(f"    Reason: {data.get('reason', 'N/A')}")

# Find events around the commit time (16:42:30)
commit_time = "2026-01-26T16:42:30"
around_commit = [e for e in ng1_events 
                if e.get('ts_utc', '') >= '2026-01-26T16:42:20' and 
                e.get('ts_utc', '') <= '2026-01-26T16:42:40']

print(f"\nEvents around commit time (16:42:30):")
if around_commit:
    for e in around_commit[:20]:
        event_type = e.get('event', 'N/A')
        state = e.get('state', 'N/A')
        print(f"  {e.get('ts_utc', '')[:19]} | {event_type} | state={state}")
        if 'RANGE' in event_type or 'GAP' in event_type or 'INVALID' in event_type:
            data = e.get('data', {})
            if isinstance(data, dict):
                print(f"    Data: {json.dumps(data, indent=2, default=str)[:200]}")

# Check for slot end summary that might mention invalidation
slot_summaries = [e for e in ng1_events if 'SLOT_END_SUMMARY' in e.get('event', '')]
if slot_summaries:
    print(f"\nSLOT_END_SUMMARY events: {len(slot_summaries)}")
    for ss in slot_summaries[-3:]:
        print(f"  {ss.get('ts_utc', '')[:19]} - {ss.get('event', 'N/A')}")
        data = ss.get('data', {})
        if isinstance(data, dict):
            print(f"    Outcome: {data.get('outcome', 'N/A')}")
            print(f"    Reason: {data.get('reason', 'N/A')}")

print(f"\n{'='*80}")
