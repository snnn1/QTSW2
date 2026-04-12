#!/usr/bin/env python3
"""Check for loud error events - summarizes ERROR/VIOLATION/backpressure events over a time window"""
import argparse
import json
import os
from pathlib import Path
from datetime import datetime, timedelta
from collections import defaultdict

ap = argparse.ArgumentParser()
ap.add_argument("--log-dir", default=None, help="Path to logs/robot directory (overrides env/config)")
ap.add_argument("--hours-back", type=int, default=24, help="Lookback window in hours")
args = ap.parse_args()

log_dir = None
if args.log_dir:
    log_dir = Path(args.log_dir)
else:
    env_log_dir = os.environ.get("QTSW2_LOG_DIR")
    env_root = os.environ.get("QTSW2_PROJECT_ROOT")
    if env_log_dir:
        log_dir = Path(env_log_dir)
    elif env_root:
        log_dir = Path(env_root) / "logs" / "robot"
    else:
        log_dir = Path("logs/robot")

if not log_dir.exists():
    print(f"Log directory not found: {log_dir}")
    raise SystemExit(1)

# Default: last 24 hours
hours_back = args.hours_back
cutoff_time = (datetime.utcnow() - timedelta(hours=hours_back)).isoformat() + "Z"

print("="*80)
print("LOUD ERROR EVENTS SUMMARY")
print("="*80)
print(f"\nScanning events since: {cutoff_time[:19]} UTC")
print(f"Time window: {hours_back} hours")

all_events = []
for log_file in log_dir.glob("robot_*.jsonl"):
    try:
        with open(log_file, 'r', encoding='utf-8') as f:
            for line in f:
                line = line.strip()
                if line:
                    try:
                        event = json.loads(line)
                        ts = event.get('ts_utc', '')
                        if ts >= cutoff_time:
                            event['_source_file'] = log_file.name
                            all_events.append(event)
                    except:
                        pass
    except:
        pass

all_events.sort(key=lambda e: e.get('ts_utc', ''))

# Categorize events
error_events = []
violation_events = []
backpressure_events = []
logging_errors = []

for event in all_events:
    event_name = event.get('event', '')
    level = event.get('level', '')
    
    # Check event name patterns
    if 'ERROR' in event_name or level == 'ERROR':
        error_events.append(event)
    if 'VIOLATION' in event_name:
        violation_events.append(event)
    if 'BACKPRESSURE' in event_name or 'DROP' in event_name:
        backpressure_events.append(event)
    if 'LOG_' in event_name and ('ERROR' in event_name or 'FAILURE' in event_name):
        logging_errors.append(event)

print(f"\nTotal events scanned: {len(all_events)}")
print(f"\n=== ERROR Events: {len(error_events)} ===")
if error_events:
    # Group by event type
    by_type = defaultdict(list)
    for e in error_events:
        by_type[e.get('event', 'UNKNOWN')].append(e)
    
    for event_type, events in sorted(by_type.items(), key=lambda x: -len(x[1])):
        print(f"\n  {event_type}: {len(events)} occurrences")
        if events:
            latest = events[-1]
            ts = latest.get('ts_utc', 'N/A')[:19]
            payload = latest.get('data', {}).get('payload', {})
            exception_type = payload.get('exception_type', 'N/A')
            error_msg = payload.get('error', 'N/A')
            print(f"    Latest: {ts}")
            if exception_type != 'N/A':
                print(f"    Exception: {exception_type}")
            if error_msg != 'N/A' and len(error_msg) < 100:
                print(f"    Error: {error_msg}")
            if len(events) <= 5:
                print(f"    All occurrences:")
                for e in events:
                    ts_e = e.get('ts_utc', 'N/A')[:19]
                    print(f"      {ts_e}")
else:
    print("  [OK] No ERROR events found")

print(f"\n=== VIOLATION Events: {len(violation_events)} ===")
if violation_events:
    by_type = defaultdict(list)
    for e in violation_events:
        by_type[e.get('event', 'UNKNOWN')].append(e)
    
    for event_type, events in sorted(by_type.items(), key=lambda x: -len(x[1])):
        print(f"\n  {event_type}: {len(events)} occurrences")
        if events:
            latest = events[-1]
            ts = latest.get('ts_utc', 'N/A')[:19]
            payload = latest.get('data', {}).get('payload', {})
            print(f"    Latest: {ts}")
            if len(events) <= 5:
                for e in events:
                    ts_e = e.get('ts_utc', 'N/A')[:19]
                    print(f"      {ts_e}")
else:
    print("  [OK] No VIOLATION events found")

print(f"\n=== Backpressure/Drop Events: {len(backpressure_events)} ===")
if backpressure_events:
    by_type = defaultdict(list)
    for e in backpressure_events:
        by_type[e.get('event', 'UNKNOWN')].append(e)
    
    for event_type, events in sorted(by_type.items(), key=lambda x: -len(x[1])):
        print(f"\n  {event_type}: {len(events)} occurrences")
        if events:
            latest = events[-1]
            ts = latest.get('ts_utc', 'N/A')[:19]
            payload = latest.get('data', {}).get('payload', {})
            dropped_debug = payload.get('dropped_debug_count', 0)
            dropped_info = payload.get('dropped_info_count', 0)
            queue_size = payload.get('queue_size', 'N/A')
            print(f"    Latest: {ts}")
            print(f"    Dropped DEBUG: {dropped_debug}, Dropped INFO: {dropped_info}")
            print(f"    Queue size: {queue_size}")
else:
    print("  [OK] No backpressure/drop events found")

print(f"\n=== Logging Pipeline Errors: {len(logging_errors)} ===")
if logging_errors:
    by_type = defaultdict(list)
    for e in logging_errors:
        by_type[e.get('event', 'UNKNOWN')].append(e)
    
    for event_type, events in sorted(by_type.items(), key=lambda x: -len(x[1])):
        print(f"\n  {event_type}: {len(events)} occurrences")
        if events:
            latest = events[-1]
            ts = latest.get('ts_utc', 'N/A')[:19]
            payload = latest.get('data', {}).get('payload', {})
            exception_type = payload.get('exception_type', 'N/A')
            error_msg = payload.get('error', 'N/A')
            print(f"    Latest: {ts}")
            if exception_type != 'N/A':
                print(f"    Exception: {exception_type}")
            if error_msg != 'N/A' and len(error_msg) < 100:
                print(f"    Error: {error_msg}")
else:
    print("  [OK] No logging pipeline errors found")

# Summary
total_critical = len(error_events) + len(violation_events) + len(backpressure_events) + len(logging_errors)
print(f"\n=== SUMMARY ===")
print(f"Total critical events: {total_critical}")
if total_critical == 0:
    print("  [OK] No critical errors detected - system appears healthy")
else:
    print(f"  [WARNING] {total_critical} critical events detected - review above for details")

print("\n" + "="*80)
