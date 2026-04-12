#!/usr/bin/env python3
"""Check NQ2 hydration status and completeness metrics"""
import json
from pathlib import Path
from datetime import datetime

log_dir = Path("logs/robot")
events = []

# Find all log files and read them
log_files = list(log_dir.glob("robot_*.jsonl"))
if not log_files:
    print("No log files found in logs/robot")
    exit(1)

print(f"Found {len(log_files)} log files")
print("Reading all log files...")

for log_file in log_files:
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

print(f"Total events loaded: {len(events)}")

# Sort events by timestamp (most recent first for display)
events.sort(key=lambda e: e.get('ts_utc', ''), reverse=True)

# Filter for NQ2 hydration events
nq2_events = []
for e in events:
    data = e.get('data', {})
    if isinstance(data, dict):
        payload = data.get('payload', {})
        if isinstance(payload, dict):
            stream_id = payload.get('stream_id', '')
        else:
            stream_id = ''
    else:
        payload = {}
        stream_id = ''
    
    stream = e.get('stream', '')
    event_name = e.get('event', '')
    
    # Check for NQ2 or MNQ (MNQ routes to NQ2 via canonical mapping)
    is_nq2_stream = ('NQ2' in str(stream_id) or 'NQ2' in str(stream))
    instrument = e.get('instrument', '')
    payload_instrument = payload.get('instrument', '') if isinstance(payload, dict) else ''
    is_mnq_instrument = ('MNQ' in str(instrument) or 'MNQ' in str(payload_instrument))
    
    if (is_nq2_stream or is_mnq_instrument) and ('HYDRATION' in event_name or 'PRE_HYDRATION' in event_name or 'BARSREQUEST' in event_name):
        nq2_events.append(e)

print(f"\n{'='*80}")
print(f"NQ2 HYDRATION STATUS")
print(f"{'='*80}")
print(f"\nTotal NQ2 hydration events: {len(nq2_events)}")

if nq2_events:
    print("\nRecent NQ2 hydration events:")
    for e in nq2_events[-10:]:
        ts = e.get('ts_utc', 'N/A')[:19]
        event_name = e.get('event', 'N/A')
        payload = e.get('data', {}).get('payload', {})
        print(f"  {ts} | {event_name}")
        
        # Show HYDRATION_SUMMARY details
        if event_name == 'HYDRATION_SUMMARY':
            data = e.get('data', {})
            payload = data.get('payload', {}) if isinstance(data, dict) else {}
            if isinstance(payload, dict):
                print(f"    Expected bars: {payload.get('expected_bars', 'N/A')}")
                print(f"    Loaded bars: {payload.get('loaded_bars', 'N/A')}")
                print(f"    Completeness: {payload.get('completeness_pct', 'N/A')}%")
                print(f"    Late start: {payload.get('late_start', 'N/A')}")
                print(f"    Missed breakout: {payload.get('missed_breakout', 'N/A')}")
                print(f"    Range high: {payload.get('reconstructed_range_high', 'N/A')}")
                print(f"    Range low: {payload.get('reconstructed_range_low', 'N/A')}")
        
        # Show LATE_START_MISSED_BREAKOUT details
        if event_name == 'LATE_START_MISSED_BREAKOUT':
            data = e.get('data', {})
            payload = data.get('payload', {}) if isinstance(data, dict) else {}
            if isinstance(payload, dict):
                print(f"    Breakout time: {payload.get('breakout_time_chicago', 'N/A')}")
                print(f"    Breakout direction: {payload.get('breakout_direction', 'N/A')}")
                print(f"    Breakout price: {payload.get('breakout_price', 'N/A')}")
    
    # Check for latest HYDRATION_SUMMARY
    latest_summary = [e for e in nq2_events if e.get('event') == 'HYDRATION_SUMMARY']
    if latest_summary:
        latest = latest_summary[-1]
        data = latest.get('data', {})
        payload = data.get('payload', {}) if isinstance(data, dict) and isinstance(data.get('payload'), dict) else {}
        print(f"\n{'='*80}")
        print("LATEST HYDRATION_SUMMARY FOR NQ2:")
        print(f"{'='*80}")
        print(f"  Timestamp: {latest.get('ts_utc', 'N/A')[:19]}")
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
        
        # Show full payload if new fields are missing (for debugging)
        if payload.get('expected_bars') is None and payload.get('late_start') is None:
            print(f"\n  [NOTE] Old format HYDRATION_SUMMARY detected - new fields not present")
            print(f"  Available fields: {list(payload.keys())[:10]}...")
    
    # Check for today's events specifically
    today_events = [e for e in nq2_events if e.get('ts_utc', '').startswith('2026-01-26')]
    if today_events:
        print(f"\n{'='*80}")
        print(f"TODAY'S (2026-01-26) NQ2 HYDRATION EVENTS: {len(today_events)}")
        print(f"{'='*80}")
        for e in today_events[-10:]:
            ts = e.get('ts_utc', 'N/A')[:19]
            event_name = e.get('event', 'N/A')
            print(f"  {ts} | {event_name}")
    else:
        print(f"\n{'='*80}")
        print("TODAY'S (2026-01-26) NQ2 HYDRATION EVENTS: 0")
        print(f"{'='*80}")
        print("  No hydration events found for today.")
        print("  NQ2 journal shows RANGE_BUILDING state - hydration already completed.")
else:
    print("\n[WARN] No NQ2 hydration events found!")
    print("  This may mean:")
    print("  - Stream hasn't reached PRE_HYDRATION state yet")
    print("  - Stream is using a different instrument name")
    print("  - Logs are from a different run")

# Check for boundary contract
boundary_events = [e for e in events if e.get('event') == 'HYDRATION_BOUNDARY_CONTRACT']
nq2_boundary = []
for e in boundary_events:
    data = e.get('data', {})
    if isinstance(data, dict):
        payload = data.get('payload', {})
        if isinstance(payload, dict):
            stream_id = payload.get('stream_id', '')
            if 'NQ2' in str(stream_id):
                nq2_boundary.append(e)

if nq2_boundary:
    print(f"\n{'='*80}")
    print("HYDRATION BOUNDARY CONTRACT FOR NQ2:")
    print(f"{'='*80}")
    latest_boundary = nq2_boundary[-1]
    data = latest_boundary.get('data', {})
    payload = data.get('payload', {}) if isinstance(data, dict) and isinstance(data.get('payload'), dict) else {}
    print(f"  Range build window: {payload.get('range_build_window', 'N/A')}")
    print(f"  Missed-breakout scan window: {payload.get('missed_breakout_scan_window', 'N/A')}")

print(f"\n{'='*80}")
