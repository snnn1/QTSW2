#!/usr/bin/env python3
"""Check if range is being calculated with UTC vs Chicago timezone issue"""
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

# Find RANGE_START_INITIALIZED events
range_start_events = []
for e in events:
    if (e.get('ts_utc', '').startswith('2026-01-26') and 
        e.get('event') == 'RANGE_START_INITIALIZED' and
        e.get('stream') == 'NQ2'):
        range_start_events.append(e)

if range_start_events:
    latest = range_start_events[-1]
    print("="*80)
    print("RANGE_START_INITIALIZED EVENT:")
    print("="*80)
    data = latest.get('data', {})
    if isinstance(data, dict):
        range_start_chicago_str = data.get('range_start_chicago', '')
        range_start_time_str = data.get('range_start_time_string', '')
        print(f"  Range start time string: {range_start_time_str}")
        print(f"  Range start Chicago (from event): {range_start_chicago_str}")
        
        # Parse and show what it should be
        if range_start_chicago_str:
            try:
                # Parse ISO format
                chicago_time = datetime.fromisoformat(range_start_chicago_str.replace('Z', '+00:00'))
                print(f"  Parsed Chicago time: {chicago_time}")
                print(f"  Offset: {chicago_time.tzinfo}")
                
                # Convert to UTC
                if chicago_time.tzinfo:
                    utc_time = chicago_time.astimezone(timezone.utc)
                    print(f"  Equivalent UTC time: {utc_time}")
                    print(f"  UTC offset: {utc_time - chicago_time}")
            except Exception as ex:
                print(f"  Error parsing: {ex}")

# Find BAR_ADMISSION_PROOF_RETROSPECTIVE events to see actual comparisons
bar_proof_events = []
for e in events:
    if (e.get('ts_utc', '').startswith('2026-01-26') and 
        e.get('event') == 'BAR_ADMISSION_PROOF_RETROSPECTIVE' and
        e.get('stream') == 'NQ2'):
        bar_proof_events.append(e)

if bar_proof_events:
    print(f"\n{'='*80}")
    print("BAR ADMISSION PROOF (first 5 bars):")
    print(f"{'='*80}")
    for e in bar_proof_events[:5]:
        data = e.get('data', {})
        if isinstance(data, dict):
            payload = data.get('payload', {})
            if isinstance(payload, dict):
                bar_time_utc = payload.get('bar_time_raw_utc', '')
                bar_time_chicago = payload.get('bar_time_chicago', '')
                range_start_chicago = payload.get('range_start_chicago', '')
                range_end_chicago = payload.get('range_end_chicago', '')
                comparison_result = payload.get('comparison_result', False)
                
                print(f"\n  Bar UTC: {bar_time_utc[:19] if bar_time_utc else 'N/A'}")
                print(f"  Bar Chicago: {bar_time_chicago[:19] if bar_time_chicago else 'N/A'}")
                print(f"  Range start Chicago: {range_start_chicago[:19] if range_start_chicago else 'N/A'}")
                print(f"  Range end Chicago: {range_end_chicago[:19] if range_end_chicago else 'N/A'}")
                print(f"  Included: {comparison_result}")
                
                # Check if times look correct
                if bar_time_chicago and range_start_chicago:
                    try:
                        bar_ct = datetime.fromisoformat(bar_time_chicago.replace('Z', '+00:00'))
                        range_start_ct = datetime.fromisoformat(range_start_chicago.replace('Z', '+00:00'))
                        
                        # Check if bar time is 6 hours ahead (suggesting UTC vs Chicago issue)
                        time_diff_hours = (bar_ct - range_start_ct).total_seconds() / 3600
                        if abs(time_diff_hours - 6) < 1 or abs(time_diff_hours - 5) < 1:
                            print(f"  ⚠️  WARNING: Time difference suggests UTC/Chicago mismatch!")
                            print(f"      Expected ~0 hours difference, got {time_diff_hours:.1f} hours")
                    except:
                        pass

print(f"\n{'='*80}")
print("DIAGNOSIS:")
print(f"{'='*80}")
print("If bar times are 5-6 hours ahead of range start, this suggests:")
print("  - Range start is being interpreted as UTC instead of Chicago")
print("  - Or bars are being interpreted as Chicago instead of UTC")
print(f"{'='*80}")
