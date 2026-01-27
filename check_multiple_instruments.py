#!/usr/bin/env python3
"""Check if bars from multiple instruments are being mixed"""
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

# Find BAR_ADMISSION_PROOF events at 08:00
bar_proof_0800 = []
for e in events:
    if (e.get('ts_utc', '').startswith('2026-01-26') and 
        e.get('event') == 'BAR_ADMISSION_PROOF' and
        '08:00' in str(e.get('data', {}).get('bar_time_chicago', ''))):
        bar_proof_0800.append(e)

if bar_proof_0800:
    print("="*80)
    print(f"BAR_ADMISSION_PROOF AT 08:00 (Found {len(bar_proof_0800)}):")
    print("="*80)
    
    # Group by stream
    by_stream = defaultdict(list)
    for e in bar_proof_0800:
        stream = e.get('stream', 'N/A')
        by_stream[stream].append(e)
    
    print(f"\n  Bars by stream:")
    for stream, bars in by_stream.items():
        print(f"    {stream}: {len(bars)} bars")
        
        # Check instruments
        instruments = set()
        sources = set()
        for bar in bars:
            instruments.add(bar.get('instrument', 'N/A'))
            data = bar.get('data', {})
            if isinstance(data, dict):
                sources.add(data.get('bar_source', 'N/A'))
        
        print(f"      Instruments: {instruments}")
        print(f"      Sources: {sources}")

# Check if there are multiple streams receiving bars for the same instrument
print(f"\n{'='*80}")
print("CHECKING STREAM/INSTRUMENT MAPPING:")
print(f"{'='*80}")

# Find all BAR_ADMISSION_PROOF events
all_bar_proof = []
for e in events:
    if (e.get('ts_utc', '').startswith('2026-01-26') and 
        e.get('event') == 'BAR_ADMISSION_PROOF'):
        all_bar_proof.append(e)

if all_bar_proof:
    # Group by (stream, instrument)
    by_stream_instrument = defaultdict(int)
    for e in all_bar_proof:
        stream = e.get('stream', 'N/A')
        instrument = e.get('instrument', 'N/A')
        by_stream_instrument[(stream, instrument)] += 1
    
    print(f"\n  Total bars checked: {len(all_bar_proof)}")
    print(f"  Unique (stream, instrument) combinations: {len(by_stream_instrument)}")
    print(f"\n  Bars by (stream, instrument):")
    for (stream, instrument), count in sorted(by_stream_instrument.items()):
        print(f"    {stream} / {instrument}: {count} bars")

print(f"\n{'='*80}")
