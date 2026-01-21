#!/usr/bin/env python3
"""Quick check of strategy status from logs"""
import json
from pathlib import Path
from datetime import datetime

log_file = Path("logs/robot/robot_ENGINE.jsonl")
if not log_file.exists():
    print(f"Log file not found: {log_file}")
    exit(1)

events = []
with open(log_file, 'r', encoding='utf-8') as f:
    for line in f:
        line = line.strip()
        if line:
            try:
                events.append(json.loads(line))
            except:
                pass

if not events:
    print("No events found in log file")
    exit(1)

print("="*80)
print("STRATEGY STATUS CHECK")
print("="*80)
print(f"\nTotal events in log: {len(events)}")

# Get recent events
recent = events[-50:] if len(events) > 50 else events
print(f"\n=== RECENT EVENTS (last {len(recent)}) ===")
for e in recent[-20:]:
    ts = e.get('ts_utc', 'N/A')[:19] if len(e.get('ts_utc', '')) > 19 else e.get('ts_utc', 'N/A')
    event = e.get('event', 'N/A')
    trading_date = e.get('trading_date', 'N/A')
    print(f"{ts} | {event:35} | {trading_date}")

# Check for key events
recent_all = events[-200:] if len(events) > 200 else events

bar_partial = [e for e in recent_all if 'BAR_PARTIAL' in e.get('event', '')]
bar_accepted = [e for e in recent_all if 'BAR_ACCEPTED' in e.get('event', '') or 'BAR_DELIVERY' in e.get('event', '')]
bar_mismatch = [e for e in recent_all if 'BAR_DATE_MISMATCH' in e.get('event', '')]
pre_hydration = [e for e in recent_all if 'PRE_HYDRATION' in e.get('event', '')]
forced_transition = [e for e in recent_all if 'PRE_HYDRATION_FORCED_TRANSITION' in e.get('event', '')]
state_transitions = [e for e in recent_all if 'TRANSITION' in e.get('event', '')]
errors = [e for e in recent_all if 'ERROR' in e.get('event', '') or 'FAILED' in e.get('event', '')]

print(f"\n=== BAR PROCESSING (last {len(recent_all)} events) ===")
print(f"BAR_PARTIAL events: {len(bar_partial)}")
print(f"BAR_ACCEPTED/DELIVERY events: {len(bar_accepted)}")
print(f"BAR_DATE_MISMATCH events: {len(bar_mismatch)}")
print(f"PRE_HYDRATION events: {len(pre_hydration)}")
print(f"FORCED_TRANSITION events: {len(forced_transition)}")
print(f"State TRANSITION events: {len(state_transitions)}")
print(f"ERROR/FAILED events: {len(errors)}")

if bar_partial:
    print(f"\n=== RECENT BAR_PARTIAL EVENTS ===")
    for e in bar_partial[-5:]:
        payload = e.get('data', {}).get('payload', {})
        bar_source = payload.get('bar_source', 'N/A')
        event_type = e.get('event', 'N/A')
        ts = e.get('ts_utc', 'N/A')[:19] if len(e.get('ts_utc', '')) > 19 else e.get('ts_utc', 'N/A')
        print(f"{ts} | {event_type:35} | Source: {bar_source}")

if forced_transition:
    print(f"\n=== PRE_HYDRATION FORCED TRANSITIONS ===")
    for e in forced_transition[-3:]:
        payload = e.get('data', {}).get('payload', {})
        ts = e.get('ts_utc', 'N/A')[:19] if len(e.get('ts_utc', '')) > 19 else e.get('ts_utc', 'N/A')
        reason = payload.get('reason', 'N/A')
        bar_count = payload.get('bar_count', 'N/A')
        print(f"{ts} | Reason: {reason} | Bar count: {bar_count}")

if errors:
    print(f"\n=== ERRORS/WARNINGS ===")
    for e in errors[-10:]:
        ts = e.get('ts_utc', 'N/A')[:19] if len(e.get('ts_utc', '')) > 19 else e.get('ts_utc', 'N/A')
        event_type = e.get('event', 'N/A')
        print(f"{ts} | {event_type}")

if state_transitions:
    print(f"\n=== STATE TRANSITIONS ===")
    for e in state_transitions[-10:]:
        ts = e.get('ts_utc', 'N/A')[:19] if len(e.get('ts_utc', '')) > 19 else e.get('ts_utc', 'N/A')
        event_type = e.get('event', 'N/A')
        print(f"{ts} | {event_type}")

# Check latest trading date
if events:
    latest = events[-1]
    print(f"\n=== CURRENT STATE ===")
    print(f"Latest trading date: {latest.get('trading_date', 'N/A')}")
    print(f"Latest event: {latest.get('event', 'N/A')}")
    print(f"Latest timestamp: {latest.get('ts_utc', 'N/A')}")

print("\n" + "="*80)
