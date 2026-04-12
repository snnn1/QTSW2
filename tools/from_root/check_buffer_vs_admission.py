#!/usr/bin/env python3
"""Compare BAR_ADMISSION_PROOF vs actual buffer adds"""
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

# Find BAR_ADMISSION_PROOF events for NQ2 in window
admission_proof = []
for e in events:
    if (e.get('ts_utc', '').startswith('2026-01-26') and 
        e.get('stream') == 'NQ2' and
        e.get('event') == 'BAR_ADMISSION_PROOF'):
        data = e.get('data', {})
        if isinstance(data, dict):
            bar_time_str = data.get('bar_time_chicago', '')
            if bar_time_str:
                try:
                    bar_time = datetime.fromisoformat(bar_time_str.replace('Z', '+00:00'))
                    if bar_time.hour >= 8 and (bar_time.hour < 11 or (bar_time.hour == 11 and bar_time.minute == 0)):
                        admission_proof.append({
                            'bar_utc': data.get('bar_time_raw_utc', ''),
                            'bar_chicago': bar_time_str,
                            'result': data.get('comparison_result', False),
                            'source': data.get('bar_source', 'N/A'),
                            'event_ts': e.get('ts_utc', '')
                        })
                except:
                    pass

# Find BAR_BUFFER_ADD_ATTEMPT events (new logging)
buffer_attempts = []
for e in events:
    if (e.get('ts_utc', '').startswith('2026-01-26') and 
        e.get('stream') == 'NQ2' and
        e.get('event') == 'BAR_BUFFER_ADD_ATTEMPT'):
        data = e.get('data', {})
        if isinstance(data, dict):
            buffer_attempts.append({
                'bar_utc': data.get('bar_timestamp_utc', ''),
                'bar_chicago': data.get('bar_timestamp_chicago', ''),
                'source': data.get('bar_source', 'N/A'),
                'event_ts': e.get('ts_utc', '')
            })

# Find BAR_BUFFER_ADD_COMMITTED events
buffer_committed = []
for e in events:
    if (e.get('ts_utc', '').startswith('2026-01-26') and 
        e.get('stream') == 'NQ2' and
        e.get('event') == 'BAR_BUFFER_ADD_COMMITTED'):
        data = e.get('data', {})
        if isinstance(data, dict):
            buffer_committed.append({
                'bar_utc': data.get('bar_timestamp_utc', ''),
                'bar_chicago': data.get('bar_timestamp_chicago', ''),
                'source': data.get('bar_source', 'N/A'),
                'buffer_count': data.get('new_buffer_count', 'N/A'),
                'event_ts': e.get('ts_utc', '')
            })

# Find BAR_BUFFER_REJECTED events
buffer_rejected = []
for e in events:
    if (e.get('ts_utc', '').startswith('2026-01-26') and 
        e.get('stream') == 'NQ2' and
        e.get('event') == 'BAR_BUFFER_REJECTED'):
        data = e.get('data', {})
        if isinstance(data, dict):
            buffer_rejected.append({
                'bar_utc': data.get('bar_timestamp_utc', ''),
                'bar_chicago': data.get('bar_timestamp_chicago', ''),
                'source': data.get('bar_source', 'N/A'),
                'reason': data.get('rejection_reason', 'N/A'),
                'event_ts': e.get('ts_utc', '')
            })

print("="*80)
print("BAR BUFFER FLOW ANALYSIS FOR NQ2 [08:00, 11:00):")
print("="*80)

print(f"\n  BAR_ADMISSION_PROOF (passed check): {len(admission_proof)}")
print(f"  BAR_BUFFER_ADD_ATTEMPT: {len(buffer_attempts)}")
print(f"  BAR_BUFFER_ADD_COMMITTED: {len(buffer_committed)}")
print(f"  BAR_BUFFER_REJECTED: {len(buffer_rejected)}")

if buffer_rejected:
    # Group rejections by reason
    by_reason = defaultdict(int)
    for r in buffer_rejected:
        by_reason[r['reason']] += 1
    
    print(f"\n  Rejections by reason:")
    for reason, count in by_reason.items():
        print(f"    {reason}: {count}")

# Check if bars that passed admission are reaching buffer
if admission_proof and buffer_attempts:
    # Get unique bars that passed admission
    passed_admission = {a['bar_utc'] for a in admission_proof if a['result'] == True or a['result'] == 'True'}
    attempted_buffer = {b['bar_utc'] for b in buffer_attempts}
    
    print(f"\n  Bars that passed admission: {len(passed_admission)}")
    print(f"  Bars that attempted buffer: {len(attempted_buffer)}")
    print(f"  Bars that passed but never attempted: {len(passed_admission - attempted_buffer)}")
    print(f"  Bars that attempted but didn't pass: {len(attempted_buffer - passed_admission)}")

if buffer_committed:
    # Get unique committed bars
    committed_bars = {b['bar_utc'] for b in buffer_committed}
    print(f"\n  Unique bars committed to buffer: {len(committed_bars)}")
    print(f"  Expected: 180 (3 hours * 60 minutes)")
    
    if len(committed_bars) > 180:
        print(f"  ⚠️  WARNING: More bars than expected in buffer!")

print(f"\n{'='*80}")
