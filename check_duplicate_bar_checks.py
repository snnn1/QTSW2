#!/usr/bin/env python3
"""Check if the same bar is being checked multiple times"""
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

# Find BAR_ADMISSION_PROOF events for NQ2
bar_proof_nq2 = []
for e in events:
    if (e.get('ts_utc', '').startswith('2026-01-26') and 
        e.get('stream') == 'NQ2' and
        e.get('event') == 'BAR_ADMISSION_PROOF'):
        data = e.get('data', {})
        if isinstance(data, dict):
            bar_time_str = data.get('bar_time_chicago', '')
            bar_utc_str = data.get('bar_time_raw_utc', '')
            source = data.get('bar_source', 'N/A')
            result = data.get('comparison_result', False)
            event_ts = e.get('ts_utc', 'N/A')
            
            bar_proof_nq2.append({
                'bar_time_chicago': bar_time_str,
                'bar_time_utc': bar_utc_str,
                'source': source,
                'result': result,
                'event_timestamp': event_ts
            })

if bar_proof_nq2:
    print("="*80)
    print(f"BAR_ADMISSION_PROOF FOR NQ2 (Found {len(bar_proof_nq2)}):")
    print("="*80)
    
    # Group by bar timestamp (UTC)
    by_bar_utc = defaultdict(list)
    for bar in bar_proof_nq2:
        if bar['bar_time_utc']:
            by_bar_utc[bar['bar_time_utc']].append(bar)
    
    # Find bars checked multiple times
    duplicate_checks = {k: v for k, v in by_bar_utc.items() if len(v) > 1}
    
    print(f"\n  Total bar checks: {len(bar_proof_nq2)}")
    print(f"  Unique bar timestamps: {len(by_bar_utc)}")
    print(f"  Bars checked multiple times: {len(duplicate_checks)}")
    
    if duplicate_checks:
        print(f"\n  Sample duplicate checks:")
        for bar_utc, checks in list(duplicate_checks.items())[:10]:
            print(f"    Bar UTC: {bar_utc}")
            for check in checks:
                print(f"      Event: {check['event_timestamp'][:19]} | Source: {check['source']} | Result: {check['result']}")
    
    # Check bars in [08:00, 11:00) window
    bars_in_window = []
    for bar in bar_proof_nq2:
        bar_time_str = bar['bar_time_chicago']
        if bar_time_str:
            try:
                bar_time = datetime.fromisoformat(bar_time_str.replace('Z', '+00:00'))
                if bar_time.hour >= 8 and (bar_time.hour < 11 or (bar_time.hour == 11 and bar_time.minute == 0)):
                    bars_in_window.append(bar)
            except:
                pass
    
    print(f"\n  Bars in [08:00, 11:00) window: {len(bars_in_window)}")
    
    # Group window bars by UTC timestamp
    window_by_utc = defaultdict(list)
    for bar in bars_in_window:
        if bar['bar_time_utc']:
            window_by_utc[bar['bar_time_utc']].append(bar)
    
    duplicate_in_window = {k: v for k, v in window_by_utc.items() if len(v) > 1}
    print(f"  Unique bar timestamps in window: {len(window_by_utc)}")
    print(f"  Expected: 180 (3 hours * 60 minutes)")
    print(f"  Duplicate checks in window: {len(duplicate_in_window)}")
    
    if duplicate_in_window:
        print(f"\n  Sample duplicates in window:")
        for bar_utc, checks in list(duplicate_in_window.items())[:5]:
            print(f"    {bar_utc}: {len(checks)} checks")
            sources = [c['source'] for c in checks]
            print(f"      Sources: {set(sources)} | Event times: {[c['event_timestamp'][:19] for c in checks]}")

print(f"\n{'='*80}")
