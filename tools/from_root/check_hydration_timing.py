#!/usr/bin/env python3
"""Check when hydration runs - timing analysis"""
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

# Find HYDRATION_SUMMARY events for NQ2
nq2_hydration = []
for e in events:
    if (e.get('ts_utc', '').startswith('2026-01-26') and 
        e.get('stream') == 'NQ2' and
        e.get('event') == 'HYDRATION_SUMMARY'):
        nq2_hydration.append(e)

# Find PRE_HYDRATION_HANDLER_TRACE events
pre_hydration_traces = []
for e in events:
    if (e.get('ts_utc', '').startswith('2026-01-26') and 
        e.get('stream') == 'NQ2' and
        e.get('event') == 'PRE_HYDRATION_HANDLER_TRACE'):
        pre_hydration_traces.append(e)

# Find TICK_CALLED events
tick_called = []
for e in events:
    if (e.get('ts_utc', '').startswith('2026-01-26') and 
        e.get('stream') == 'NQ2' and
        e.get('event') == 'TICK_CALLED'):
        tick_called.append(e)

print("="*80)
print("HYDRATION TIMING ANALYSIS:")
print("="*80)

print(f"\n  HYDRATION_SUMMARY events: {len(nq2_hydration)}")
if nq2_hydration:
    for i, h in enumerate(nq2_hydration):
        ts = h.get('ts_utc', 'N/A')
        data = h.get('data', {})
        if isinstance(data, dict):
            loaded = data.get('loaded_bars', 'N/A')
            expected = data.get('expected_bars', 'N/A')
            print(f"    {i+1}. {ts[:19]}: {loaded}/{expected} bars")

print(f"\n  PRE_HYDRATION_HANDLER_TRACE events: {len(pre_hydration_traces)}")
if pre_hydration_traces:
    print(f"    First: {pre_hydration_traces[0].get('ts_utc', 'N/A')[:19]}")
    print(f"    Last: {pre_hydration_traces[-1].get('ts_utc', 'N/A')[:19]}")
    if len(pre_hydration_traces) > 1:
        # Calculate interval
        first_ts = datetime.fromisoformat(pre_hydration_traces[0].get('ts_utc', '').replace('Z', '+00:00'))
        last_ts = datetime.fromisoformat(pre_hydration_traces[-1].get('ts_utc', '').replace('Z', '+00:00'))
        interval = (last_ts - first_ts).total_seconds() / (len(pre_hydration_traces) - 1) if len(pre_hydration_traces) > 1 else 0
        print(f"    Average interval: {interval:.1f} seconds")

print(f"\n  TICK_CALLED events: {len(tick_called)}")
if tick_called:
    print(f"    First: {tick_called[0].get('ts_utc', 'N/A')[:19]}")
    print(f"    Last: {tick_called[-1].get('ts_utc', 'N/A')[:19]}")
    if len(tick_called) > 1:
        # Calculate interval
        first_ts = datetime.fromisoformat(tick_called[0].get('ts_utc', '').replace('Z', '+00:00'))
        last_ts = datetime.fromisoformat(tick_called[-1].get('ts_utc', '').replace('Z', '+00:00'))
        interval = (last_ts - first_ts).total_seconds() / (len(tick_called) - 1) if len(tick_called) > 1 else 0
        print(f"    Average interval: {interval:.1f} seconds")

print(f"\n{'='*80}")
print("SUMMARY:")
print(f"{'='*80}")
print("  Hydration runs:")
print("    1. When stream is initialized (starts in PRE_HYDRATION state)")
print("    2. On every Tick() call while in PRE_HYDRATION state")
print("    3. Tick() is called:")
print("       - From OnBarUpdate() when bars arrive (bar-driven)")
print("       - Periodically (every 2 seconds based on RobotEngine constructor)")
print("    4. HandlePreHydrationState() executes on each Tick()")
print("    5. Hydration completes when:")
print("       - Bars are loaded and range is computed")
print("       - Stream transitions to ARMED state")
print(f"\n{'='*80}")
