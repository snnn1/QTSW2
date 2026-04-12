#!/usr/bin/env python3
"""Check what execution instruments streams are using"""
import json
from pathlib import Path

log_dir = Path("logs/robot")
events = []

for log_file in log_dir.glob("robot_*.jsonl"):
    try:
        with open(log_file, 'r', encoding='utf-8') as f:
            for line in f:
                if line.strip():
                    try:
                        events.append(json.loads(line))
                    except:
                        pass
    except:
        pass

print("="*80)
print("STREAM EXECUTION INSTRUMENTS:")
print("="*80)

# Get STREAMS_CREATED events
streams_created = [e for e in events 
                  if e.get('ts_utc', '').startswith('2026-01-26') and
                  e.get('event') == 'STREAMS_CREATED']

if streams_created:
    latest = streams_created[-1]
    data = latest.get('data', {})
    streams = data.get('streams', [])
    
    print(f"\n  Streams created: {len(streams)}")
    print(f"\n  Execution instruments per stream:")
    
    execution_instruments = {}
    for s in streams:
        stream_id = s.get('stream_id', 'N/A')
        exec_inst = s.get('execution_instrument', 'N/A')
        canonical = s.get('canonical_instrument', 'N/A')
        committed = s.get('committed', False)
        
        if not committed:  # Only show enabled streams
            print(f"    {stream_id}: Execution={exec_inst}, Canonical={canonical}")
            if exec_inst not in execution_instruments:
                execution_instruments[exec_inst] = []
            execution_instruments[exec_inst].append(stream_id)
    
    print(f"\n  Unique execution instruments from enabled streams:")
    for inst in sorted(execution_instruments.keys()):
        print(f"    {inst}: {len(execution_instruments[inst])} stream(s) - {', '.join(execution_instruments[inst])}")

# Check HYDRATION_SUMMARY for execution instruments
print(f"\n{'='*80}")
print("EXECUTION INSTRUMENTS FROM HYDRATION_SUMMARY:")
print(f"{'='*80}")

hydration_events = [e for e in events 
                   if e.get('ts_utc', '').startswith('2026-01-26') and
                   e.get('event') == 'HYDRATION_SUMMARY']

exec_inst_from_hydration = {}
for e in hydration_events:
    stream = e.get('stream', '')
    data = e.get('data', {})
    exec_inst = data.get('instrument', '')
    canonical = data.get('canonical_instrument', '')
    
    if exec_inst:
        if exec_inst not in exec_inst_from_hydration:
            exec_inst_from_hydration[exec_inst] = []
        exec_inst_from_hydration[exec_inst].append(stream)

for inst in sorted(exec_inst_from_hydration.keys()):
    print(f"    {inst}: {', '.join(sorted(set(exec_inst_from_hydration[inst])))}")

print(f"\n{'='*80}")
