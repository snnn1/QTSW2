#!/usr/bin/env python3
"""Check bar timestamps vs time window"""
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

# Find BAR_ADMISSION_PROOF events (not retrospective)
bar_proof = []
for e in events:
    if (e.get('ts_utc', '').startswith('2026-01-26') and 
        e.get('stream') == 'NQ2' and
        e.get('event') == 'BAR_ADMISSION_PROOF'):
        bar_proof.append(e)

if bar_proof:
    print("="*80)
    print(f"BAR_ADMISSION_PROOF EVENTS (Found {len(bar_proof)}):")
    print("="*80)
    
    # Get latest hydration summary for comparison
    hydration = None
    for e in events:
        if (e.get('ts_utc', '').startswith('2026-01-26') and 
            e.get('event') == 'HYDRATION_SUMMARY' and
            e.get('stream') == 'NQ2'):
            hydration = e
            break
    
    if hydration:
        data = hydration.get('data', {})
        range_start = data.get('range_start_chicago', '')
        slot_time = data.get('slot_time_chicago', '')
        print(f"\n  Range window: [{range_start}, {slot_time})")
    
    # Show first 10 and last 10 bars
    print("\n  FIRST 10 BARS:")
    for e in bar_proof[:10]:
        data = e.get('data', {})
        if isinstance(data, dict):
            bar_time = data.get('bar_time_chicago', 'N/A')
            range_start = data.get('range_start_chicago', 'N/A')
            slot_time = data.get('slot_time_chicago', 'N/A')
            result = data.get('comparison_result', 'N/A')
            source = data.get('bar_source', 'N/A')
            print(f"    {bar_time} | Result: {result} | Source: {source}")
    
    print("\n  LAST 10 BARS:")
    for e in bar_proof[-10:]:
        data = e.get('data', {})
        if isinstance(data, dict):
            bar_time = data.get('bar_time_chicago', 'N/A')
            range_start = data.get('range_start_chicago', 'N/A')
            slot_time = data.get('slot_time_chicago', 'N/A')
            result = data.get('comparison_result', 'N/A')
            source = data.get('bar_source', 'N/A')
            print(f"    {bar_time} | Result: {result} | Source: {source}")
    
    # Count accepted vs rejected
    accepted = sum(1 for e in bar_proof if e.get('data', {}).get('comparison_result', False) == True)
    rejected = len(bar_proof) - accepted
    print(f"\n  SUMMARY:")
    print(f"    Total bars checked: {len(bar_proof)}")
    print(f"    Accepted: {accepted}")
    print(f"    Rejected: {rejected}")
    
    # Check if bars are being rejected because they're outside the window
    if rejected > 0:
        print(f"\n  REJECTED BAR SAMPLE:")
        rejected_bars = [e for e in bar_proof if e.get('data', {}).get('comparison_result', False) != True]
        for e in rejected_bars[:5]:
            data = e.get('data', {})
            if isinstance(data, dict):
                bar_time = data.get('bar_time_chicago', 'N/A')
                detail = data.get('comparison_detail', 'N/A')
                print(f"    {bar_time} | {detail[:80]}")

# Check BAR_RECEIVED_DIAGNOSTIC events
bar_received = []
for e in events:
    if (e.get('ts_utc', '').startswith('2026-01-26') and 
        e.get('stream') == 'NQ2' and
        e.get('event') == 'BAR_RECEIVED_DIAGNOSTIC'):
        bar_received.append(e)

if bar_received:
    print(f"\n{'='*80}")
    print(f"BAR_RECEIVED_DIAGNOSTIC EVENTS (Found {len(bar_received)}):")
    print(f"{'='*80}")
    latest = bar_received[-1]
    data = latest.get('data', {})
    if isinstance(data, dict):
        print(f"  Latest bar Chicago: {data.get('bar_chicago', 'N/A')}")
        print(f"  In range window: {data.get('in_range_window', 'N/A')}")
        print(f"  Bar buffer count: {data.get('bar_buffer_count', 'N/A')}")
        print(f"  Range start: {data.get('range_start_chicago', 'N/A')}")
        print(f"  Range end: {data.get('range_end_chicago', 'N/A')}")

print(f"\n{'='*80}")
