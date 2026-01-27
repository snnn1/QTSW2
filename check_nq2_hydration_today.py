#!/usr/bin/env python3
"""Check today's NQ2 hydration status"""
import json
from pathlib import Path

log_dir = Path("logs/robot")
events = []

# Read all log files
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
    except Exception as e:
        print(f"Error reading {log_file.name}: {e}")

# Filter for today's events
today_events = [e for e in events if e.get('ts_utc', '').startswith('2026-01-26')]

# Find NQ2/MNQ related events
nq2_today = []
for e in today_events:
    event_name = e.get('event', '')
    data = e.get('data', {})
    payload = data.get('payload', {}) if isinstance(data, dict) and isinstance(data.get('payload'), dict) else {}
    stream_id = payload.get('stream_id', '') if isinstance(payload, dict) else ''
    stream = e.get('stream', '')
    instrument = e.get('instrument', '')
    
    is_nq2 = ('NQ2' in str(stream_id) or 'NQ2' in str(stream))
    is_mnq = ('MNQ' in str(instrument) or 'MNQ' in str(payload.get('instrument', '')) if isinstance(payload, dict) else False)
    
    if is_nq2 or is_mnq:
        nq2_today.append(e)

print("="*80)
print("TODAY'S NQ2/MNQ EVENTS")
print("="*80)
print(f"\nTotal NQ2/MNQ events today: {len(nq2_today)}")

# Group by event type
event_types = {}
for e in nq2_today:
    event_name = e.get('event', 'UNKNOWN')
    if event_name not in event_types:
        event_types[event_name] = []
    event_types[event_name].append(e)

print(f"\nEvent types found: {len(event_types)}")
for event_type, event_list in sorted(event_types.items()):
    print(f"  {event_type}: {len(event_list)} events")

# Check for HYDRATION_SUMMARY
if 'HYDRATION_SUMMARY' in event_types:
    latest = event_types['HYDRATION_SUMMARY'][-1]
    data = latest.get('data', {})
    payload = data.get('payload', {}) if isinstance(data, dict) and isinstance(data.get('payload'), dict) else {}
    
    print(f"\n{'='*80}")
    print("LATEST HYDRATION_SUMMARY (TODAY):")
    print(f"{'='*80}")
    print(f"  Timestamp: {latest.get('ts_utc', 'N/A')[:19]}")
    
    if isinstance(payload, dict):
        print(f"  Expected bars: {payload.get('expected_bars', 'N/A')}")
        print(f"  Expected full range bars: {payload.get('expected_full_range_bars', 'N/A')}")
        print(f"  Loaded bars: {payload.get('loaded_bars', payload.get('total_bars_in_buffer', 'N/A'))}")
        print(f"  Completeness: {payload.get('completeness_pct', 'N/A')}%")
        print(f"  Late start: {payload.get('late_start', 'N/A')}")
        print(f"  Missed breakout: {payload.get('missed_breakout', 'N/A')}")
        print(f"  Range high: {payload.get('reconstructed_range_high', 'N/A')}")
        print(f"  Range low: {payload.get('reconstructed_range_low', 'N/A')}")
        print(f"  Historical bars: {payload.get('historical_bar_count', 'N/A')}")
        print(f"  Live bars: {payload.get('live_bar_count', 'N/A')}")
        print(f"  Deduped bars: {payload.get('deduped_bar_count', 'N/A')}")
        print(f"\n  All payload keys: {list(payload.keys())}")
    else:
        print(f"  Payload type: {type(payload)}")
        print(f"  Payload: {str(payload)[:200]}")

# Check for BARSREQUEST events
if 'BARSREQUEST_FILTER_SUMMARY' in event_types or 'PRE_HYDRATION_BARS_LOADED' in event_types:
    print(f"\n{'='*80}")
    print("BARSREQUEST EVENTS:")
    print(f"{'='*80}")
    barsrequest_events = event_types.get('BARSREQUEST_FILTER_SUMMARY', []) + event_types.get('PRE_HYDRATION_BARS_LOADED', [])
    for e in barsrequest_events[-5:]:
        ts = e.get('ts_utc', 'N/A')[:19]
        event_name = e.get('event', 'N/A')
        data = e.get('data', {})
        payload = data.get('payload', {}) if isinstance(data, dict) and isinstance(data.get('payload'), dict) else {}
        print(f"  {ts} | {event_name}")
        if isinstance(payload, dict):
            print(f"    Instrument: {payload.get('instrument', 'N/A')}")
            print(f"    Bar count: {payload.get('bar_count', payload.get('accepted_bar_count', 'N/A'))}")

# Check current state
print(f"\n{'='*80}")
print("CURRENT STATE (from journal):")
print(f"{'='*80}")
journal_file = Path("logs/robot/journal/2026-01-26_NQ2.json")
if journal_file.exists():
    journal = json.loads(journal_file.read_text())
    print(f"  State: {journal.get('LastState', 'N/A')}")
    print(f"  Last Update: {journal.get('LastUpdateUtc', 'N/A')[:19]}")
    print(f"  Committed: {journal.get('Committed', 'N/A')}")

print(f"\n{'='*80}")
