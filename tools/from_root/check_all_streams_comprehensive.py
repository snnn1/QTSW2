#!/usr/bin/env python3
"""Comprehensive check of all streams: ranges, bars, hydration status"""
import json
from pathlib import Path
from collections import defaultdict

log_dir = Path("logs/robot")
events = []

# Read all robot log files
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
        pass

print("="*80)
print("COMPREHENSIVE STREAM STATUS:")
print("="*80)

# Get all streams
all_streams = set()
for e in events:
    if e.get('ts_utc', '').startswith('2026-01-26'):
        stream = e.get('stream', '')
        if stream:
            all_streams.add(stream)

# Process each stream
stream_data = {}
for stream in sorted(all_streams):
    stream_events = [e for e in events 
                    if e.get('ts_utc', '').startswith('2026-01-26') and 
                    e.get('stream') == stream]
    
    data = {
        'stream': stream,
        'bars_committed': 0,
        'bars_attempt': 0,
        'bars_rejected': 0,
        'hydration_summary': None,
        'range_initialized': None,
        'range_locked': None,
        'barsrequest_requested': 0,
        'barsrequest_executed': 0,
        'barsrequest_failed': 0,
        'barsrequest_skipped': 0,
        'bars_loaded': 0,
        'execution_instrument': None,
        'canonical_instrument': None,
        'state': None
    }
    
    # Count bar events
    for e in stream_events:
        event_type = e.get('event', '')
        if event_type == 'BAR_BUFFER_ADD_COMMITTED':
            data['bars_committed'] += 1
        elif event_type == 'BAR_BUFFER_ADD_ATTEMPT':
            data['bars_attempt'] += 1
        elif event_type == 'BAR_BUFFER_REJECTED':
            data['bars_rejected'] += 1
        elif event_type == 'HYDRATION_SUMMARY':
            data['hydration_summary'] = e.get('data', {})
        elif event_type == 'RANGE_INITIALIZED_FROM_HISTORY':
            data['range_initialized'] = e.get('data', {})
        elif event_type == 'RANGE_LOCKED_INCREMENTAL':
            data['range_locked'] = e.get('data', {})
        elif event_type == 'BARSREQUEST_REQUESTED':
            data['barsrequest_requested'] += 1
        elif event_type == 'BARSREQUEST_EXECUTED':
            data['barsrequest_executed'] += 1
        elif event_type == 'BARSREQUEST_FAILED':
            data['barsrequest_failed'] += 1
        elif event_type == 'BARSREQUEST_SKIPPED':
            data['barsrequest_skipped'] += 1
        elif event_type == 'PRE_HYDRATION_BARS_LOADED':
            e_data = e.get('data', {})
            if isinstance(e_data, dict):
                instrument = e_data.get('instrument', '')
                if instrument:
                    data['bars_loaded'] += e_data.get('bar_count', 0)
    
    # Get latest hydration summary for instrument info
    if data['hydration_summary']:
        data['execution_instrument'] = data['hydration_summary'].get('instrument', 'N/A')
        data['canonical_instrument'] = data['hydration_summary'].get('canonical_instrument', 'N/A')
    
    # Get latest state from any event
    latest_event = max(stream_events, key=lambda x: x.get('ts_utc', ''), default=None)
    if latest_event:
        data['state'] = latest_event.get('state', 'N/A')
    
    stream_data[stream] = data

# Print summary
print(f"\n  Total streams: {len(stream_data)}")
print(f"\n{'='*80}")
print("STREAM DETAILS:")
print(f"{'='*80}")

for stream in sorted(stream_data.keys()):
    data = stream_data[stream]
    print(f"\n  Stream: {stream}")
    print(f"  {'-'*76}")
    
    # Instrument info
    if data['execution_instrument']:
        print(f"    Execution Instrument: {data['execution_instrument']}")
        print(f"    Canonical Instrument: {data['canonical_instrument']}")
    
    # State
    if data['state']:
        print(f"    State: {data['state']}")
    
    # BarsRequest status
    print(f"    BarsRequest:")
    print(f"      Requested: {data['barsrequest_requested']}")
    print(f"      Executed: {data['barsrequest_executed']}")
    print(f"      Failed: {data['barsrequest_failed']}")
    print(f"      Skipped: {data['barsrequest_skipped']}")
    
    # Bars loaded
    if data['bars_loaded'] > 0:
        print(f"    Bars loaded (PRE_HYDRATION_BARS_LOADED): {data['bars_loaded']}")
    
    # Bar buffer status
    print(f"    Bar Buffer:")
    print(f"      Attempts: {data['bars_attempt']}")
    print(f"      Rejected: {data['bars_rejected']}")
    print(f"      Committed: {data['bars_committed']}")
    
    # Hydration summary
    if data['hydration_summary']:
        h = data['hydration_summary']
        print(f"    Hydration:")
        print(f"      Loaded bars: {h.get('loaded_bars', 'N/A')}")
        print(f"      Expected bars: {h.get('expected_bars', 'N/A')}")
        completeness = h.get('completeness_pct', 'N/A')
        if completeness != 'N/A' and isinstance(completeness, (int, float)):
            print(f"      Completeness: {completeness:.1f}%")
        else:
            print(f"      Completeness: {completeness}")
    
    # Range initialized
    if data['range_initialized']:
        r = data['range_initialized']
        range_high = r.get('range_high')
        range_low = r.get('range_low')
        if range_high is not None and range_low is not None:
            spread = float(range_high) - float(range_low)
            print(f"    Range (RANGE_INITIALIZED_FROM_HISTORY):")
            print(f"      High: {range_high}")
            print(f"      Low: {range_low}")
            print(f"      Spread: {spread}")
            print(f"      Bars used: {r.get('bars_used', 'N/A')}")
            print(f"      Range start: {r.get('range_start_chicago', 'N/A')}")
            print(f"      Slot time: {r.get('slot_time_chicago', 'N/A')}")
    
    # Range locked (latest)
    if data['range_locked']:
        r = data['range_locked']
        range_high = r.get('range_high')
        range_low = r.get('range_low')
        if range_high is not None and range_low is not None:
            spread = float(range_high) - float(range_low)
            print(f"    Range (RANGE_LOCKED_INCREMENTAL - latest):")
            print(f"      High: {range_high}")
            print(f"      Low: {range_low}")
            print(f"      Spread: {spread}")

# Summary table
print(f"\n{'='*80}")
print("SUMMARY TABLE:")
print(f"{'='*80}")
print(f"{'Stream':<8} {'Exec Inst':<10} {'Bars Req':<9} {'Bars Exec':<10} {'Bars Comm':<10} {'Range High':<12} {'Range Low':<12} {'Spread':<10}")
print(f"{'-'*8} {'-'*10} {'-'*9} {'-'*10} {'-'*10} {'-'*12} {'-'*12} {'-'*10}")

for stream in sorted(stream_data.keys()):
    data = stream_data[stream]
    exec_inst = data['execution_instrument'] or 'N/A'
    bars_req = data['barsrequest_requested']
    bars_exec = data['barsrequest_executed']
    bars_comm = data['bars_committed']
    
    # Get range from range_locked (latest) or range_initialized
    range_high = 'N/A'
    range_low = 'N/A'
    spread = 'N/A'
    
    if data['range_locked']:
        r = data['range_locked']
        range_high_val = r.get('range_high')
        range_low_val = r.get('range_low')
        if range_high_val is not None and range_low_val is not None:
            try:
                range_high = f"{float(range_high_val):.2f}"
                range_low = f"{float(range_low_val):.2f}"
                spread = f"{float(range_high_val) - float(range_low_val):.2f}"
            except (ValueError, TypeError):
                range_high = str(range_high_val)
                range_low = str(range_low_val)
                spread = 'N/A'
    elif data['range_initialized']:
        r = data['range_initialized']
        range_high_val = r.get('range_high')
        range_low_val = r.get('range_low')
        if range_high_val is not None and range_low_val is not None:
            try:
                range_high = f"{float(range_high_val):.2f}"
                range_low = f"{float(range_low_val):.2f}"
                spread = f"{float(range_high_val) - float(range_low_val):.2f}"
            except (ValueError, TypeError):
                range_high = str(range_high_val)
                range_low = str(range_low_val)
                spread = 'N/A'
    
    print(f"{stream:<8} {exec_inst:<10} {bars_req:<9} {bars_exec:<10} {bars_comm:<10} {range_high:<12} {range_low:<12} {spread:<10}")

print(f"\n{'='*80}")
