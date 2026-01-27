#!/usr/bin/env python3
"""Analyze why there are too many bars in the window"""
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

# Find BAR_ADMISSION_PROOF events for NQ2 in the window
bar_proof = []
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
                    # Check if in [08:00, 11:00)
                    if bar_time.hour >= 8 and (bar_time.hour < 11 or (bar_time.hour == 11 and bar_time.minute == 0)):
                        bar_proof.append({
                            'bar_time': bar_time_str,
                            'bar_time_dt': bar_time,
                            'result': data.get('comparison_result', False),
                            'source': data.get('bar_source', 'N/A')
                        })
                except:
                    pass

if bar_proof:
    print("="*80)
    print(f"BAR ANALYSIS FOR [08:00, 11:00) WINDOW:")
    print("="*80)
    print(f"  Total bars checked: {len(bar_proof)}")
    
    # Group by minute
    bars_by_minute = defaultdict(list)
    for bar in bar_proof:
        bar_dt = bar['bar_time_dt']
        minute_key = f"{bar_dt.hour:02d}:{bar_dt.minute:02d}"
        bars_by_minute[minute_key].append(bar)
    
    print(f"  Unique minutes: {len(bars_by_minute)}")
    print(f"  Expected minutes: 180 (3 hours * 60 minutes)")
    
    # Find minutes with multiple bars
    duplicate_minutes = {k: v for k, v in bars_by_minute.items() if len(v) > 1}
    if duplicate_minutes:
        print(f"\n  Minutes with MULTIPLE bars: {len(duplicate_minutes)}")
        print(f"  Sample duplicate minutes:")
        for minute, bars in list(duplicate_minutes.items())[:10]:
            sources = [b['source'] for b in bars]
            print(f"    {minute}: {len(bars)} bars | Sources: {set(sources)}")
    
    # Count by source
    by_source = defaultdict(int)
    for bar in bar_proof:
        by_source[bar['source']] += 1
    
    print(f"\n  Bars by source:")
    for source, count in by_source.items():
        print(f"    {source}: {count}")
    
    # Check if bars are from different instruments
    print(f"\n  Checking for instrument mixing...")
    
    # Find bars at the same timestamp but different sources
    timestamp_groups = defaultdict(list)
    for bar in bar_proof:
        timestamp_groups[bar['bar_time']].append(bar)
    
    same_timestamp_different_source = {k: v for k, v in timestamp_groups.items() if len(v) > 1 and len(set(b['source'] for b in v)) > 1}
    if same_timestamp_different_source:
        print(f"  Found {len(same_timestamp_different_source)} timestamps with bars from multiple sources")
        print(f"  Sample:")
        for ts, bars in list(same_timestamp_different_source.items())[:5]:
            sources = [b['source'] for b in bars]
            print(f"    {ts}: Sources: {set(sources)}")

print(f"\n{'='*80}")
