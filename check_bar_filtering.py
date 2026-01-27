#!/usr/bin/env python3
"""Check why bars aren't being loaded"""
import json
from pathlib import Path

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

# Find BAR filtering events
print("="*80)
print("BAR FILTERING EVENTS (Latest):")
print("="*80)

# Check BAR_REJECTED events
bar_rejected = []
for e in events:
    if (e.get('ts_utc', '').startswith('2026-01-26') and 
        e.get('stream') == 'NQ2' and
        'REJECT' in e.get('event', '').upper()):
        bar_rejected.append(e)

if bar_rejected:
    print(f"\n  BAR_REJECTED events: {len(bar_rejected)}")
    for e in bar_rejected[-5:]:
        print(f"    {e.get('ts_utc', 'N/A')[:19]} | {e.get('event', 'N/A')}")

# Check BAR_ADMISSION_PROOF events
bar_proof = []
for e in events:
    if (e.get('ts_utc', '').startswith('2026-01-26') and 
        e.get('stream') == 'NQ2' and
        'BAR_ADMISSION_PROOF' in e.get('event', '')):
        bar_proof.append(e)

if bar_proof:
    print(f"\n  BAR_ADMISSION_PROOF events: {len(bar_proof)}")
    latest = bar_proof[-1]
    data = latest.get('data', {})
    if isinstance(data, dict):
        payload = data.get('payload', {})
        if isinstance(payload, dict):
            print(f"    Latest proof - Bar time: {payload.get('bar_time_chicago', 'N/A')}")
            print(f"    Range start: {payload.get('range_start_chicago', 'N/A')}")
            print(f"    Slot time: {payload.get('slot_time_chicago', 'N/A')}")
            print(f"    Result: {payload.get('comparison_result', 'N/A')}")

# Check RANGE_COMPUTE_BAR_FILTERING
range_filtering = []
for e in events:
    if (e.get('ts_utc', '').startswith('2026-01-26') and 
        e.get('event') == 'RANGE_COMPUTE_BAR_FILTERING' and
        e.get('stream') == 'NQ2'):
        range_filtering.append(e)

if range_filtering:
    latest = range_filtering[-1]
    print(f"\n  RANGE_COMPUTE_BAR_FILTERING:")
    data = latest.get('data', {})
    if isinstance(data, dict):
        payload = data.get('payload', {})
        if isinstance(payload, dict):
            print(f"    Bars in buffer: {payload.get('bars_in_buffer', 'N/A')}")
            print(f"    Bars accepted: {payload.get('bars_accepted', 'N/A')}")
            print(f"    Bars filtered by date: {payload.get('bars_filtered_by_date', 'N/A')}")
            print(f"    Bars filtered by time window: {payload.get('bars_filtered_by_time_window', 'N/A')}")

# Check latest HYDRATION_SUMMARY for filtering counts
hydration = []
for e in events:
    if (e.get('ts_utc', '').startswith('2026-01-26') and 
        e.get('event') == 'HYDRATION_SUMMARY' and
        e.get('stream') == 'NQ2'):
        hydration.append(e)

if hydration:
    latest = hydration[-1]
    data = latest.get('data', {})
    if isinstance(data, dict):
        print(f"\n  HYDRATION_SUMMARY filtering:")
        print(f"    Total bars in buffer: {data.get('total_bars_in_buffer', 'N/A')}")
        print(f"    Historical bars: {data.get('historical_bar_count', 'N/A')}")
        print(f"    Live bars: {data.get('live_bar_count', 'N/A')}")
        print(f"    Deduped bars: {data.get('deduped_bar_count', 'N/A')}")
        print(f"    Filtered future bars: {data.get('filtered_future_bar_count', 'N/A')}")
        print(f"    Filtered partial bars: {data.get('filtered_partial_bar_count', 'N/A')}")

print(f"\n{'='*80}")
