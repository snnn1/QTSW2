#!/usr/bin/env python3
"""Check which bars are being included/excluded in range calculation"""
import json
from pathlib import Path
from datetime import datetime

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
        pass

# Find BAR_ADMISSION_PROOF_RETROSPECTIVE events for NQ2
bar_proof_events = []
for e in events:
    if (e.get('ts_utc', '').startswith('2026-01-26') and 
        e.get('event') == 'BAR_ADMISSION_PROOF_RETROSPECTIVE' and
        e.get('stream') == 'NQ2'):
        bar_proof_events.append(e)

if bar_proof_events:
    print("="*80)
    print("BAR FILTERING IN RANGE CALCULATION:")
    print("="*80)
    
    # Sort by timestamp
    bar_proof_events.sort(key=lambda x: x.get('ts_utc', ''))
    
    included_bars = []
    excluded_bars = []
    max_high_included = None
    max_high_excluded = None
    
    for e in bar_proof_events:
        data = e.get('data', {})
        if isinstance(data, dict):
            payload = data.get('payload', {})
            if isinstance(payload, dict):
                bar_time_chicago = payload.get('bar_time_chicago', '')
                range_start_chicago = payload.get('range_start_chicago', '')
                range_end_chicago = payload.get('range_end_chicago', '')
                comparison_result = payload.get('comparison_result', False)
                comparison_detail = payload.get('comparison_detail', '')
                
                # Try to extract bar high if available
                bar_high = None
                # Check if there's a corresponding bar admission event with prices
                
                if comparison_result:
                    included_bars.append({
                        'time': bar_time_chicago[:19] if bar_time_chicago else 'N/A',
                        'detail': comparison_detail
                    })
                else:
                    excluded_bars.append({
                        'time': bar_time_chicago[:19] if bar_time_chicago else 'N/A',
                        'detail': comparison_detail
                    })
    
    print(f"\n  Included bars: {len(included_bars)}")
    print(f"  Excluded bars: {len(excluded_bars)}")
    
    if included_bars:
        print(f"\n  First included bar: {included_bars[0]['time']}")
        print(f"  Last included bar: {included_bars[-1]['time']}")
        print(f"  Sample detail: {included_bars[0]['detail']}")
    
    if excluded_bars:
        print(f"\n  First excluded bar: {excluded_bars[0]['time']}")
        print(f"  Last excluded bar: {excluded_bars[-1]['time']}")
        print(f"  Sample detail: {excluded_bars[0]['detail']}")
        
        # Check if excluded bars are close to range boundaries
        print(f"\n  Checking excluded bars near boundaries...")
        for bar in excluded_bars[:10]:
            print(f"    {bar['time']}: {bar['detail'][:100]}")

# Find RANGE_COMPUTE_BAR_FILTERING events
filtering_events = []
for e in events:
    if (e.get('ts_utc', '').startswith('2026-01-26') and 
        e.get('event') == 'RANGE_COMPUTE_BAR_FILTERING' and
        e.get('stream') == 'NQ2'):
        filtering_events.append(e)

if filtering_events:
    print(f"\n{'='*80}")
    print("RANGE_COMPUTE_BAR_FILTERING EVENTS:")
    print(f"{'='*80}")
    latest = filtering_events[-1]
    data = latest.get('data', {})
    if isinstance(data, dict):
        payload = data.get('payload', {})
        if isinstance(payload, dict):
            print(f"  Bars in buffer: {payload.get('bars_in_buffer', 'N/A')}")
            print(f"  Bars accepted: {payload.get('bars_accepted', 'N/A')}")
            print(f"  Bars filtered by date: {payload.get('bars_filtered_by_date', 'N/A')}")
            print(f"  Bars filtered by time window: {payload.get('bars_filtered_by_time_window', 'N/A')}")
            print(f"  Range start Chicago: {payload.get('range_start_chicago', 'N/A')}")
            print(f"  Range end Chicago: {payload.get('range_end_chicago', 'N/A')}")
            print(f"  First filtered bar UTC: {payload.get('first_filtered_bar_utc', 'N/A')}")
            print(f"  First filtered bar reason: {payload.get('first_filtered_bar_reason', 'N/A')}")

# Check latest RANGE_INITIALIZED_FROM_HISTORY for actual bar count used
range_init_events = []
for e in events:
    if (e.get('ts_utc', '').startswith('2026-01-26') and 
        e.get('event') == 'RANGE_INITIALIZED_FROM_HISTORY' and
        e.get('stream') == 'NQ2'):
        range_init_events.append(e)

if range_init_events:
    latest = range_init_events[-1]
    print(f"\n{'='*80}")
    print("RANGE_INITIALIZED_FROM_HISTORY:")
    print(f"{'='*80}")
    data = latest.get('data', {})
    if isinstance(data, dict):
        payload = data.get('payload', {})
        if isinstance(payload, dict):
            print(f"  Range high: {payload.get('range_high', 'N/A')}")
            print(f"  Range low: {payload.get('range_low', 'N/A')}")
            print(f"  Bar count: {payload.get('bar_count', 'N/A')}")
            print(f"  Range start Chicago: {payload.get('range_start_chicago', 'N/A')}")
            print(f"  Computed up to Chicago: {payload.get('computed_up_to_chicago', 'N/A')}")
            print(f"  Slot time Chicago: {payload.get('slot_time_chicago', 'N/A')}")

print(f"\n{'='*80}")
print("ANALYSIS:")
print(f"{'='*80}")
print(f"  Your MNQ range high: 25903")
print(f"  System range high: 25742.25")
print(f"  Difference: 160.75 points")
print(f"\n  If bars with prices > 25742.25 are being excluded, check:")
print(f"  1. Are they outside the time window [08:00, 11:00) Chicago?")
print(f"  2. Are bar timestamps being converted correctly?")
print(f"  3. Is the range end time (slot_time) being interpreted correctly?")
print(f"{'='*80}")
