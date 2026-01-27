#!/usr/bin/env python3
"""Debug bar admission issues"""
import json
from pathlib import Path
from datetime import datetime

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

# Sort events by timestamp
events.sort(key=lambda x: x.get('ts_utc', ''))

# Find BAR_ADMISSION_PROOF_RETROSPECTIVE events (these show filtering decisions)
bar_proof = []
for e in events:
    if (e.get('ts_utc', '').startswith('2026-01-26') and 
        e.get('stream') == 'NQ2' and
        e.get('event') == 'BAR_ADMISSION_PROOF_RETROSPECTIVE'):
        bar_proof.append(e)

if bar_proof:
    print("="*80)
    print(f"BAR_ADMISSION_PROOF_RETROSPECTIVE EVENTS (Found {len(bar_proof)}):")
    print("="*80)
    
    # Show first 5 and last 5
    print("\n  FIRST 5:")
    for e in bar_proof[:5]:
        data = e.get('data', {})
        if isinstance(data, dict):
            payload = data.get('payload', {})
            if isinstance(payload, dict):
                bar_time = payload.get('bar_time_chicago', 'N/A')
                range_start = payload.get('range_start_chicago', 'N/A')
                range_end = payload.get('range_end_chicago', 'N/A')
                result = payload.get('comparison_result', 'N/A')
                detail = payload.get('comparison_detail', 'N/A')
                print(f"    {e.get('ts_utc', 'N/A')[:19]}")
                print(f"      Bar time: {bar_time}")
                print(f"      Range: [{range_start}, {range_end})")
                print(f"      Result: {result}")
                print(f"      Detail: {detail[:100] if detail else 'N/A'}")
                print()
    
    print("\n  LAST 5:")
    for e in bar_proof[-5:]:
        data = e.get('data', {})
        if isinstance(data, dict):
            payload = data.get('payload', {})
            if isinstance(payload, dict):
                bar_time = payload.get('bar_time_chicago', 'N/A')
                range_start = payload.get('range_start_chicago', 'N/A')
                range_end = payload.get('range_end_chicago', 'N/A')
                result = payload.get('comparison_result', 'N/A')
                detail = payload.get('comparison_detail', 'N/A')
                print(f"    {e.get('ts_utc', 'N/A')[:19]}")
                print(f"      Bar time: {bar_time}")
                print(f"      Range: [{range_start}, {range_end})")
                print(f"      Result: {result}")
                print(f"      Detail: {detail[:100] if detail else 'N/A'}")
                print()
    
    # Count accepted vs rejected
    accepted = sum(1 for e in bar_proof if e.get('data', {}).get('payload', {}).get('comparison_result', False))
    rejected = len(bar_proof) - accepted
    print(f"\n  SUMMARY:")
    print(f"    Total bars checked: {len(bar_proof)}")
    print(f"    Accepted: {accepted}")
    print(f"    Rejected: {rejected}")

# Check for BAR_ADMITTED events
bar_admitted = []
for e in events:
    if (e.get('ts_utc', '').startswith('2026-01-26') and 
        e.get('stream') == 'NQ2' and
        e.get('event') == 'BAR_ADMITTED'):
        bar_admitted.append(e)

if bar_admitted:
    print(f"\n{'='*80}")
    print(f"BAR_ADMITTED EVENTS (Found {len(bar_admitted)}):")
    print(f"{'='*80}")
    for e in bar_admitted[-5:]:
        print(f"    {e.get('ts_utc', 'N/A')[:19]}")

# Check for BAR_REJECTED events
bar_rejected = []
for e in events:
    if (e.get('ts_utc', '').startswith('2026-01-26') and 
        e.get('stream') == 'NQ2' and
        e.get('event') == 'BAR_REJECTED'):
        bar_rejected.append(e)

if bar_rejected:
    print(f"\n{'='*80}")
    print(f"BAR_REJECTED EVENTS (Found {len(bar_rejected)}):")
    print(f"{'='*80}")
    for e in bar_rejected[-5:]:
        data = e.get('data', {})
        if isinstance(data, dict):
            reason = data.get('reason', 'N/A')
            print(f"    {e.get('ts_utc', 'N/A')[:19]} | Reason: {reason}")

# Check latest HYDRATION_SUMMARY for state
hydration = []
for e in events:
    if (e.get('ts_utc', '').startswith('2026-01-26') and 
        e.get('event') == 'HYDRATION_SUMMARY' and
        e.get('stream') == 'NQ2'):
        hydration.append(e)

if hydration:
    latest = hydration[-1]
    print(f"\n{'='*80}")
    print("LATEST HYDRATION_SUMMARY STATE:")
    print(f"{'='*80}")
    data = latest.get('data', {})
    if isinstance(data, dict):
        print(f"  State: {latest.get('source', 'N/A')}")
        print(f"  Total bars in buffer: {data.get('total_bars_in_buffer', 'N/A')}")
        print(f"  Loaded bars: {data.get('loaded_bars', 'N/A')}")
        print(f"  Range start Chicago: {data.get('range_start_chicago', 'N/A')}")
        print(f"  Slot time Chicago: {data.get('slot_time_chicago', 'N/A')}")
        print(f"  Now Chicago: {data.get('now_chicago', 'N/A')}")

print(f"\n{'='*80}")
