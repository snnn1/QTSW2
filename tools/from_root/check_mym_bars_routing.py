#!/usr/bin/env python3
"""Check if MYM bars are being routed to YM2"""
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
print("MYM BARS ROUTING ANALYSIS:")
print("="*80)

# Find all bars received for MYM
mym_bars = []
for e in events:
    if e.get('ts_utc', '').startswith('2026-01-26'):
        # Check BAR_RECEIVED_DIAGNOSTIC or ENGINE_BAR_HEARTBEAT
        event_type = e.get('event', '')
        if event_type in ['BAR_RECEIVED_DIAGNOSTIC', 'ENGINE_BAR_HEARTBEAT']:
            data = e.get('data', {})
            if isinstance(data, dict):
                instrument = data.get('instrument', '')
                if instrument == 'MYM':
                    mym_bars.append(e)

print(f"\n  MYM bars received: {len(mym_bars)}")
if mym_bars:
    print(f"    First: {mym_bars[0].get('ts_utc', 'N/A')[:19]}")
    print(f"    Last: {mym_bars[-1].get('ts_utc', 'N/A')[:19]}")

# Check if MYM bars were routed to YM streams
ym_streams = ['YM1', 'YM2']
for stream in ym_streams:
    print(f"\n  {stream}:")
    
    # Check BAR_ADMISSION_PROOF for this stream
    admission = [e for e in events 
                if e.get('ts_utc', '').startswith('2026-01-26') and 
                e.get('stream') == stream and
                e.get('event') == 'BAR_ADMISSION_PROOF']
    
    print(f"    BAR_ADMISSION_PROOF: {len(admission)}")
    
    # Check what instrument these bars are from
    if admission:
        instruments = set()
        for e in admission:
            data = e.get('data', {})
            if isinstance(data, dict):
                inst = data.get('instrument', '')
                if inst:
                    instruments.add(inst)
        print(f"    Instruments in admission: {', '.join(sorted(instruments))}")
    
    # Check BAR_BUFFER_ADD_COMMITTED
    committed = [e for e in events 
                if e.get('ts_utc', '').startswith('2026-01-26') and 
                e.get('stream') == stream and
                e.get('event') == 'BAR_BUFFER_ADD_COMMITTED']
    
    print(f"    BAR_BUFFER_ADD_COMMITTED: {len(committed)}")

# Check BarsRequest for MYM
mym_barsrequest = [e for e in events 
                  if e.get('ts_utc', '').startswith('2026-01-26') and 
                  'BARSREQUEST' in e.get('event', '')]
mym_related = []
for e in mym_barsrequest:
    data = e.get('data', {})
    if isinstance(data, dict):
        instrument = data.get('instrument', '')
        if instrument == 'MYM':
            mym_related.append(e)

print(f"\n  BarsRequest events for MYM: {len(mym_related)}")
for br in mym_related:
    print(f"    - {br.get('event')} at {br.get('ts_utc', '')[:19]}")

# Check if MYM is in the parity spec as a micro
print(f"\n  Checking if MYM -> YM mapping exists...")
# We can't check the spec directly, but we can infer from behavior

print(f"\n{'='*80}")
print("COMPARISON WITH MNQ -> NQ:")
print(f"{'='*80}")

# Check MNQ bars routing to NQ2
nq2_admission = [e for e in events 
                if e.get('ts_utc', '').startswith('2026-01-26') and 
                e.get('stream') == 'NQ2' and
                e.get('event') == 'BAR_ADMISSION_PROOF']

print(f"\n  NQ2 BAR_ADMISSION_PROOF: {len(nq2_admission)}")
if nq2_admission:
    instruments = set()
    for e in nq2_admission:
        data = e.get('data', {})
        if isinstance(data, dict):
            inst = data.get('instrument', '')
            if inst:
                instruments.add(inst)
    print(f"    Instruments: {', '.join(sorted(instruments))}")

print(f"\n{'='*80}")
