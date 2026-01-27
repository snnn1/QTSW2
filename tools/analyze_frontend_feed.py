#!/usr/bin/env python3
"""Analyze frontend_feed.jsonl file"""
import json
from pathlib import Path
from collections import Counter
from datetime import datetime, timedelta, timezone

log_file = Path("logs/robot/frontend_feed.jsonl")

if not log_file.exists():
    print(f"File not found: {log_file}")
    exit(1)

file_size_mb = log_file.stat().st_size / (1024 * 1024)
print(f"File: {log_file.name}")
print(f"Size: {file_size_mb:.2f} MB")

# Count lines
with open(log_file, 'r', encoding='utf-8') as f:
    lines = sum(1 for _ in f)

print(f"Total Lines: {lines:,}")

# Sample events
events = []
sample_size = 100000  # Sample first 100k events for speed
with open(log_file, 'r', encoding='utf-8') as f:
    for i, line in enumerate(f):
        if i >= sample_size:
            break
        line = line.strip()
        if line:
            try:
                event = json.loads(line)
                events.append(event)
            except:
                pass

if not events:
    print("\nNo events found")
    exit(0)

print(f"\nSampled Events: {len(events):,}")

# Event type breakdown
event_types = Counter(e.get('event', 'UNKNOWN') for e in events)
print(f"\n{'=' * 70}")
print("TOP 20 EVENT TYPES")
print("=" * 70)
for event, count in event_types.most_common(20):
    pct = (count / len(events)) * 100 if events else 0
    print(f"  {event:45} {count:6,} ({pct:5.1f}%)")

# Check average event size
avg_size = sum(len(json.dumps(e)) for e in events[:1000]) / min(1000, len(events))
print(f"\nAverage event size: {avg_size:.0f} bytes")
print(f"Estimated total events: {int(file_size_mb * 1024 * 1024 / avg_size):,}")
