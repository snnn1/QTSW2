#!/usr/bin/env python3
"""Check for exact event names including any variations"""
import json
import re
from pathlib import Path

log_dir = Path("logs/robot")
all_event_names = set()

# Collect all event names
for log_file in log_dir.glob("robot_*.jsonl"):
    try:
        with open(log_file, 'r', encoding='utf-8') as f:
            for line in f:
                line = line.strip()
                if line:
                    try:
                        event = json.loads(line)
                        event_name = event.get('event', '')
                        if 'PRE_HYDRATION' in event_name or 'RANGE' in event_name:
                            all_event_names.add(event_name)
                    except:
                        pass
    except:
        pass

print("="*80)
print("ALL PRE_HYDRATION AND RANGE EVENT NAMES")
print("="*80)
for name in sorted(all_event_names):
    print(f"  {name}")

# Check for any event containing "BEFORE" or "RANGE_START"
print("\n=== Events containing BEFORE or RANGE_START ===")
for name in sorted(all_event_names):
    if 'BEFORE' in name.upper() or 'RANGE_START' in name.upper():
        print(f"  {name}")

print("\n" + "="*80)
