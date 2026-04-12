#!/usr/bin/env python3
"""Check if SlotTimeUtc conversion is correct"""
import json
from pathlib import Path
from datetime import datetime, timezone, timedelta

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

# Find RANGE_START_INITIALIZED events to see both Chicago and UTC times
range_start_events = []
for e in events:
    if (e.get('ts_utc', '').startswith('2026-01-26') and 
        e.get('event') == 'RANGE_START_INITIALIZED' and
        e.get('stream') == 'NQ2'):
        range_start_events.append(e)

if range_start_events:
    latest = range_start_events[-1]
    print("="*80)
    print("TIME BOUNDARIES CHECK:")
    print("="*80)
    data = latest.get('data', {})
    if isinstance(data, dict):
        payload = data.get('payload', {})
        if isinstance(payload, dict):
            range_start_chicago = payload.get('range_start_chicago', '')
            print(f"  Range start Chicago: {range_start_chicago}")

# Find HYDRATION_SUMMARY to see all times
hydration_summary = []
for e in events:
    if (e.get('ts_utc', '').startswith('2026-01-26') and 
        e.get('event') == 'HYDRATION_SUMMARY' and
        e.get('stream') == 'NQ2'):
        hydration_summary.append(e)

if hydration_summary:
    latest = hydration_summary[-1]
    print(f"\n{'='*80}")
    print("HYDRATION_SUMMARY TIME BOUNDARIES:")
    print(f"{'='*80}")
    data = latest.get('data', {})
    if isinstance(data, dict):
        range_start_chicago = data.get('range_start_chicago', '')
        slot_time_chicago = data.get('slot_time_chicago', '')
        now_chicago = data.get('now_chicago', '')
        
        print(f"  Range start Chicago: {range_start_chicago}")
        print(f"  Slot time Chicago: {slot_time_chicago}")
        print(f"  Now Chicago: {now_chicago}")
        
        # Parse and check if times make sense
        try:
            if range_start_chicago and slot_time_chicago:
                range_start = datetime.fromisoformat(range_start_chicago.replace('Z', '+00:00'))
                slot_time = datetime.fromisoformat(slot_time_chicago.replace('Z', '+00:00'))
                
                # Check if slot_time is 3 hours after range_start (11:00 - 08:00 = 3 hours)
                time_diff = (slot_time - range_start).total_seconds() / 3600
                print(f"\n  Time difference: {time_diff:.1f} hours (expected 3.0)")
                
                if abs(time_diff - 3.0) > 0.1:
                    print(f"  ⚠️  WARNING: Time difference is not 3 hours!")
                    print(f"      This suggests timezone conversion issue")
                
                # Check if times are in Chicago timezone (should be UTC-06:00 or UTC-05:00)
                range_offset = range_start.utcoffset().total_seconds() / 3600
                slot_offset = slot_time.utcoffset().total_seconds() / 3600
                print(f"  Range start offset: UTC{range_offset:+.0f}:00")
                print(f"  Slot time offset: UTC{slot_offset:+.0f}:00")
                
                if range_offset not in [-6, -5] or slot_offset not in [-6, -5]:
                    print(f"  ⚠️  WARNING: Offsets are not Chicago timezone!")
                    print(f"      Expected UTC-06:00 (CST) or UTC-05:00 (CDT)")
                    print(f"      This suggests times are in wrong timezone")
        except Exception as ex:
            print(f"  Error parsing times: {ex}")

# Check top-level event fields for slot_time_chicago and slot_time_utc
if hydration_summary:
    latest = hydration_summary[-1]
    print(f"\n{'='*80}")
    print("TOP-LEVEL EVENT FIELDS:")
    print(f"{'='*80}")
    slot_time_chicago_top = latest.get('slot_time_chicago', '')
    print(f"  slot_time_chicago (top-level): {slot_time_chicago_top}")
    
    # Check if there's a slot_time_utc field
    if 'slot_time_utc' in latest:
        slot_time_utc = latest.get('slot_time_utc', '')
        print(f"  slot_time_utc (top-level): {slot_time_utc}")
        
        # Verify conversion
        if slot_time_chicago_top and slot_time_utc:
            try:
                chicago_time = datetime.fromisoformat(slot_time_chicago_top.replace('Z', '+00:00'))
                utc_time = datetime.fromisoformat(slot_time_utc.replace('Z', '+00:00'))
                
                # Calculate expected UTC time (Chicago + offset)
                chicago_offset_hours = chicago_time.utcoffset().total_seconds() / 3600
                expected_utc = chicago_time - timedelta(hours=chicago_offset_hours)
                
                print(f"\n  Chicago time: {chicago_time}")
                print(f"  UTC time (from event): {utc_time}")
                print(f"  Expected UTC (Chicago - {chicago_offset_hours:.0f}h): {expected_utc.replace(tzinfo=timezone.utc)}")
                
                time_diff_minutes = abs((utc_time - expected_utc.replace(tzinfo=timezone.utc)).total_seconds() / 60)
                if time_diff_minutes > 1:
                    print(f"  ⚠️  WARNING: UTC conversion mismatch! Difference: {time_diff_minutes:.1f} minutes")
                else:
                    print(f"  ✓  UTC conversion looks correct (difference: {time_diff_minutes:.1f} minutes)")
            except Exception as ex:
                print(f"  Error verifying conversion: {ex}")

print(f"\n{'='*80}")
print("DIAGNOSIS:")
print(f"{'='*80}")
print("If times are correct but range is still wrong:")
print("  1. Check if bars with prices > 25742.25 are in the time window")
print("  2. Verify bar timestamps are being converted correctly")
print("  3. Check if range calculation is using the correct end time")
print(f"{'='*80}")
