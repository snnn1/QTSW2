#!/usr/bin/env python3
"""Analyze robot log volume"""
import json
import os
from pathlib import Path
from datetime import datetime, timedelta, timezone
from collections import Counter

qtsw2_root = Path(__file__).parent.parent
log_file = qtsw2_root / "logs" / "robot" / "robot_ENGINE.jsonl"

if not log_file.exists():
    print(f"Log file not found: {log_file}")
    exit(1)

# File size
size_mb = log_file.stat().st_size / (1024 * 1024)

# Count lines
with open(log_file, 'r', encoding='utf-8') as f:
    lines = sum(1 for _ in f)

print("=" * 70)
print("ROBOT LOG VOLUME ANALYSIS")
print("=" * 70)
print(f"\nLog File: {log_file.name}")
print(f"File Size: {size_mb:.2f} MB")
print(f"Total Lines: {lines:,}")

# Parse events
events = []
with open(log_file, 'r', encoding='utf-8') as f:
    for line in f:
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

# Time range
first_ts = events[0].get('ts_utc', '')
last_ts = events[-1].get('ts_utc', '')
print(f"First Event: {first_ts[:19] if first_ts else 'N/A'}")
print(f"Last Event:  {last_ts[:19] if last_ts else 'N/A'}")

# Last 24 hours
cutoff = datetime.now(timezone.utc) - timedelta(hours=24)
recent_events = []
for event in events:
    ts_str = event.get('ts_utc', '')
    if ts_str:
        try:
            ts = datetime.fromisoformat(ts_str.replace('Z', '+00:00'))
            if ts > cutoff:
                recent_events.append(event)
        except:
            pass

print(f"\nLast 24 Hours: {len(recent_events):,} events")
print(f"Events per hour: {len(recent_events) / 24:.1f}")

# Event type breakdown
event_counts = Counter(e.get('event', 'UNKNOWN') for e in recent_events)
print(f"\n{'=' * 70}")
print("TOP 20 EVENT TYPES (Last 24h)")
print("=" * 70)
for event, count in event_counts.most_common(20):
    pct = (count / len(recent_events)) * 100 if recent_events else 0
    print(f"  {event:45} {count:6,} ({pct:5.1f}%)")

# Level breakdown
level_counts = Counter(e.get('level', 'UNKNOWN') for e in recent_events)
print(f"\n{'=' * 70}")
print("LOG LEVELS (Last 24h)")
print("=" * 70)
for level, count in sorted(level_counts.items()):
    pct = (count / len(recent_events)) * 100 if recent_events else 0
    print(f"  {level:10} {count:6,} ({pct:5.1f}%)")

# Check for high-frequency events
print(f"\n{'=' * 70}")
print("HIGH-FREQUENCY EVENTS (>100/hour)")
print("=" * 70)
high_freq = [(e, c) for e, c in event_counts.items() if c / 24 > 100]
if high_freq:
    for event, count in sorted(high_freq, key=lambda x: x[1], reverse=True):
        per_hour = count / 24
        print(f"  {event:45} {per_hour:6.1f} events/hour")
else:
    print("  None")

# Check logging config
config_path = qtsw2_root / "configs" / "robot" / "logging.json"
if config_path.exists():
    with open(config_path) as f:
        config = json.load(f)
    print(f"\n{'=' * 70}")
    print("LOGGING CONFIGURATION")
    print("=" * 70)
    print(f"  Diagnostic Logs: {config.get('enable_diagnostic_logs', False)}")
    print(f"  Min Log Level: {config.get('min_log_level', 'INFO')}")
    print(f"  Max File Size: {config.get('max_file_size_mb', 50)} MB")
    print(f"  Max Rotated Files: {config.get('max_rotated_files', 5)}")
else:
    print(f"\n{'=' * 70}")
    print("LOGGING CONFIGURATION")
    print("=" * 70)
    print("  Using defaults (config file not found)")
    print("  Diagnostic Logs: False (default)")
    print("  Min Log Level: INFO (default)")

print("\n" + "=" * 70)
