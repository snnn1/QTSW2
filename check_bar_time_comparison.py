#!/usr/bin/env python3
"""Check bar time comparison details"""
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

# Find BAR_ADMISSION_PROOF events for bars at 08:00
bar_proof_0800 = []
for e in events:
    if (e.get('ts_utc', '').startswith('2026-01-26') and 
        e.get('stream') == 'NQ2' and
        e.get('event') == 'BAR_ADMISSION_PROOF'):
        data = e.get('data', {})
        if isinstance(data, dict):
            bar_time = data.get('bar_time_chicago', '')
            if '08:00' in bar_time:
                bar_proof_0800.append(e)

if bar_proof_0800:
    print("="*80)
    print(f"BAR_ADMISSION_PROOF AT 08:00 (Found {len(bar_proof_0800)}):")
    print("="*80)
    
    # Show first few
    for e in bar_proof_0800[:3]:
        data = e.get('data', {})
        if isinstance(data, dict):
            bar_time = data.get('bar_time_chicago', '')
            range_start = data.get('range_start_chicago', '')
            slot_time = data.get('slot_time_chicago', '')
            result = data.get('comparison_result', 'N/A')
            detail = data.get('comparison_detail', 'N/A')
            
            print(f"\n  Bar time: {bar_time}")
            print(f"  Range start: {range_start}")
            print(f"  Slot time: {slot_time}")
            print(f"  Comparison result: {result} (type: {type(result).__name__})")
            print(f"  Detail: {detail}")
            
            # Try to parse and compare
            try:
                bar_dt = datetime.fromisoformat(bar_time.replace('Z', '+00:00'))
                range_dt = datetime.fromisoformat(range_start.replace('Z', '+00:00'))
                slot_dt = datetime.fromisoformat(slot_time.replace('Z', '+00:00'))
                
                print(f"  Parsed bar: {bar_dt}")
                print(f"  Parsed range_start: {range_dt}")
                print(f"  Parsed slot_time: {slot_dt}")
                print(f"  bar >= range_start: {bar_dt >= range_dt}")
                print(f"  bar < slot_time: {bar_dt < slot_dt}")
                print(f"  Expected result: {bar_dt >= range_dt and bar_dt < slot_dt}")
            except Exception as ex:
                print(f"  Parse error: {ex}")

print(f"\n{'='*80}")
