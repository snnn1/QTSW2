#!/usr/bin/env python3
"""Check instrument mapping for CL2 and YM2"""
import json
from pathlib import Path
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

print("="*80)
print("INSTRUMENT MAPPING ANALYSIS:")
print("="*80)

# Check all streams for instrument mapping
stream_instruments = defaultdict(set)

for e in events:
    if e.get('ts_utc', '').startswith('2026-01-26'):
        stream = e.get('stream', '')
        if stream:
            # Get instrument from various events
            data = e.get('data', {})
            if isinstance(data, dict):
                exec_instrument = data.get('instrument', '')
                canonical = data.get('canonical_instrument', '')
                if exec_instrument:
                    stream_instruments[stream].add(exec_instrument)
                if canonical:
                    stream_instruments[stream].add(f"canonical:{canonical}")

# Check BarsRequest events to see which instruments are being requested
barsrequest_instruments = set()
for e in events:
    if e.get('ts_utc', '').startswith('2026-01-26'):
        if e.get('event') == 'BARSREQUEST_REQUESTED':
            data = e.get('data', {})
            if isinstance(data, dict):
                instrument = data.get('instrument', '')
                if instrument:
                    barsrequest_instruments.add(instrument)

print(f"\n  Instruments requested via BarsRequest:")
for inst in sorted(barsrequest_instruments):
    print(f"    - {inst}")

print(f"\n  Stream instrument mapping:")
for stream in sorted(stream_instruments.keys()):
    instruments = sorted(stream_instruments[stream])
    print(f"    {stream}: {', '.join(instruments)}")

# Check CL2 and YM2 specifically
print(f"\n  CL2 and YM2 analysis:")
for stream in ['CL2', 'YM2']:
    print(f"\n    {stream}:")
    if stream in stream_instruments:
        print(f"      Instruments: {', '.join(sorted(stream_instruments[stream]))}")
    else:
        print(f"      No instrument data found")
    
    # Check if BarsRequest was called for their instruments
    stream_events = [e for e in events 
                    if e.get('ts_utc', '').startswith('2026-01-26') and 
                    e.get('stream') == stream]
    
    hydration = [e for e in stream_events if e.get('event') == 'HYDRATION_SUMMARY']
    if hydration:
        h_data = hydration[-1].get('data', {})
        if isinstance(h_data, dict):
            exec_inst = h_data.get('instrument', 'N/A')
            canonical_inst = h_data.get('canonical_instrument', 'N/A')
            print(f"      Execution instrument: {exec_inst}")
            print(f"      Canonical instrument: {canonical_inst}")
            
            # Check if BarsRequest was called for this instrument
            if exec_inst in barsrequest_instruments:
                print(f"      BarsRequest WAS called for {exec_inst}")
            else:
                print(f"      BarsRequest was NOT called for {exec_inst}")

print(f"\n{'='*80}")
