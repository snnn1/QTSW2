#!/usr/bin/env python3
"""Check execution gate states from violations"""
import json
from pathlib import Path

# Read latest robot events
log_dir = Path("logs/robot")
events = []
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
    except:
        pass

today_events = [e for e in events if e.get('ts_utc', '').startswith('2026-01-26')]
today_events.sort(key=lambda x: x.get('ts_utc', ''))

print("="*80)
print("EXECUTION GATE STATES (from latest violations)")
print("="*80)

# Get latest execution gate violation for each stream
violations = [e for e in today_events 
             if 'EXECUTION_GATE' in e.get('event', '') and 
             'VIOLATION' in e.get('event', '')]

by_stream = {}
for v in violations:
    stream = v.get('stream', 'N/A')
    if stream not in by_stream:
        by_stream[stream] = []
    by_stream[stream].append(v)

for stream_id in sorted(by_stream.keys()):
    stream_violations = by_stream[stream_id]
    latest = max(stream_violations, key=lambda x: x.get('ts_utc', ''))
    
    print(f"\n{stream_id}:")
    print(f"  Latest violation: {latest.get('ts_utc', '')[:19]}")
    
    data = latest.get('data', {})
    if isinstance(data, dict):
        print(f"  Gate states:")
        print(f"    realtime_ok: {data.get('realtime_ok', 'N/A')}")
        print(f"    trading_day: {data.get('trading_day', 'N/A')}")
        print(f"    session_active: {data.get('session_active', 'N/A')}")
        print(f"    slot_reached: {data.get('slot_reached', 'N/A')}")
        print(f"    timetable_enabled: {data.get('timetable_enabled', 'N/A')}")
        print(f"    stream_armed: {data.get('stream_armed', 'N/A')}")
        print(f"    can_detect_entries: {data.get('can_detect_entries', 'N/A')}")
        print(f"    entry_detected: {data.get('entry_detected', 'N/A')}")
        print(f"    breakout_levels_computed: {data.get('breakout_levels_computed', 'N/A')}")
        print(f"    execution_mode: {data.get('execution_mode', 'N/A')}")
        
        # Identify which gates are failing
        failing_gates = []
        if not data.get('realtime_ok', False):
            failing_gates.append("REALTIME_OK")
        if not data.get('session_active', False):
            failing_gates.append("SESSION_ACTIVE")
        if not data.get('slot_reached', False):
            failing_gates.append("SLOT_REACHED")
        if not data.get('timetable_enabled', False):
            failing_gates.append("TIMETABLE_ENABLED")
        if not data.get('stream_armed', False):
            failing_gates.append("STREAM_ARMED")
        if not data.get('can_detect_entries', False):
            failing_gates.append("CAN_DETECT_ENTRIES")
        
        if failing_gates:
            print(f"  FAILING GATES: {', '.join(failing_gates)}")
        else:
            print(f"  All gates appear OK - check RiskGate for other failures")

print(f"\n{'='*80}")
