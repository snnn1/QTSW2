#!/usr/bin/env python3
"""Check for PRE_HYDRATION_COMPLETE_SET events"""
import json
from pathlib import Path

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

events.sort(key=lambda e: e.get('ts_utc', ''))
recent = events[-1000:]

pre_hyd_complete = [e for e in recent if 'PRE_HYDRATION_COMPLETE_SET' in e.get('event', '')]

print("="*80)
print("PRE_HYDRATION_COMPLETE_SET CHECK")
print("="*80)
print(f"\nTotal events found: {len(pre_hyd_complete)}")

if pre_hyd_complete:
    print("\n=== Recent PRE_HYDRATION_COMPLETE_SET events (last 10) ===")
    for e in pre_hyd_complete[-10:]:
        ts = e.get('ts_utc', 'N/A')[:19] if isinstance(e.get('ts_utc'), str) else 'N/A'
        payload = e.get('data', {}).get('payload', {})
        stream = payload.get('stream_id', 'N/A')
        exec_mode = payload.get('execution_mode', 'N/A')
        print(f"  {ts} | Stream: {stream} | Mode: {exec_mode}")
else:
    print("\n[WARN] No PRE_HYDRATION_COMPLETE_SET events found!")
    print("  -> Code may not be compiled yet")
    print("  -> OR _preHydrationComplete is already true (set in previous tick)")

print("\n" + "="*80)
