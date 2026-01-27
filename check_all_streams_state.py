#!/usr/bin/env python3
"""Check state of all enabled streams"""
import json
from pathlib import Path
from datetime import datetime
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

# Group events by stream
streams = defaultdict(lambda: {
    'latest_state': None,
    'latest_hydration': None,
    'latest_range': None,
    'latest_range_locked': None,
    'bars_committed': 0,
    'last_event_time': None
})

for e in events:
    if e.get('ts_utc', '').startswith('2026-01-26'):
        stream = e.get('stream', '')
        if stream:
            event_type = e.get('event', '')
            ts = e.get('ts_utc', '')
            
            # Track latest state from any event
            state = e.get('state', '')
            if state:
                streams[stream]['latest_state'] = state
            
            # Track latest hydration summary
            if event_type == 'HYDRATION_SUMMARY':
                streams[stream]['latest_hydration'] = e
                streams[stream]['last_event_time'] = ts
            
            # Track latest range initialization
            if event_type == 'RANGE_INITIALIZED_FROM_HISTORY':
                streams[stream]['latest_range'] = e
                streams[stream]['last_event_time'] = ts
            
            # Track latest range locked
            if event_type in ['RANGE_LOCKED_INCREMENTAL', 'RANGE_LOCK_SNAPSHOT']:
                streams[stream]['latest_range_locked'] = e
                streams[stream]['last_event_time'] = ts
            
            # Count committed bars
            if event_type == 'BAR_BUFFER_ADD_COMMITTED':
                streams[stream]['bars_committed'] += 1

print("="*80)
print("ALL ENABLED STREAMS STATE:")
print("="*80)

# Sort streams alphabetically
for stream in sorted(streams.keys()):
    data = streams[stream]
    print(f"\n  Stream: {stream}")
    print(f"  {'-'*76}")
    
    # State
    state = data['latest_state'] or 'UNKNOWN'
    print(f"    State: {state}")
    
    # Last event time
    if data['last_event_time']:
        print(f"    Last event: {data['last_event_time'][:19]}")
    
    # Hydration status
    hydration = data['latest_hydration']
    if hydration:
        h_data = hydration.get('data', {})
        if isinstance(h_data, dict):
            expected = h_data.get('expected_bars', 'N/A')
            loaded = h_data.get('loaded_bars', 'N/A')
            completeness = h_data.get('completeness_pct', 'N/A')
            print(f"    Hydration: {loaded}/{expected} bars ({completeness}%)")
            
            range_start = h_data.get('range_start_chicago', '')
            slot_time = h_data.get('slot_time_chicago', '')
            if range_start and slot_time:
                try:
                    rs = datetime.fromisoformat(range_start.replace('Z', '+00:00'))
                    st = datetime.fromisoformat(slot_time.replace('Z', '+00:00'))
                    print(f"    Window: [{rs.strftime('%H:%M')}, {st.strftime('%H:%M')})")
                except:
                    pass
    
    # Range values
    range_locked = data['latest_range_locked']
    if range_locked:
        r_data = range_locked.get('data', {})
        if isinstance(r_data, dict):
            range_high = r_data.get('range_high')
            range_low = r_data.get('range_low')
            if range_high is not None and range_low is not None:
                spread = float(range_high) - float(range_low)
                print(f"    Range: High={range_high}, Low={range_low}, Spread={spread:.2f}")
    else:
        # Fallback to RANGE_INITIALIZED_FROM_HISTORY
        range_init = data['latest_range']
        if range_init:
            r_data = range_init.get('data', {})
            if isinstance(r_data, dict):
                range_high = r_data.get('range_high')
                range_low = r_data.get('range_low')
                if range_high is not None and range_low is not None:
                    spread = float(range_high) - float(range_low)
                    print(f"    Range (from history): High={range_high}, Low={range_low}, Spread={spread:.2f}")
    
    # Bars committed
    if data['bars_committed'] > 0:
        print(f"    Bars committed: {data['bars_committed']}")

print(f"\n{'='*80}")
print("SUMMARY:")
print(f"{'='*80}")

# Count streams by state
state_counts = defaultdict(int)
for stream, data in streams.items():
    state = data['latest_state'] or 'UNKNOWN'
    state_counts[state] += 1

print(f"\n  Streams by state:")
for state, count in sorted(state_counts.items()):
    print(f"    {state}: {count}")

# Count streams with ranges
streams_with_ranges = sum(1 for s in streams.values() 
                          if s['latest_range_locked'] or s['latest_range'])
print(f"\n  Streams with computed ranges: {streams_with_ranges}/{len(streams)}")

# Count streams with hydration
streams_hydrated = sum(1 for s in streams.values() if s['latest_hydration'])
print(f"  Streams with hydration data: {streams_hydrated}/{len(streams)}")

print(f"\n{'='*80}")
