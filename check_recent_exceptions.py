#!/usr/bin/env python3
"""Check recent exceptions and errors in logs"""
import json
from pathlib import Path
from datetime import datetime, timezone
from collections import defaultdict

log_dir = Path("logs/robot")
if not log_dir.exists():
    print("Log directory not found")
    exit(1)

print("="*80)
print("RECENT EXCEPTIONS AND ERRORS ANALYSIS")
print("="*80)

# Find all recent log files
log_files = list(log_dir.glob("robot_*.jsonl"))
log_files.sort(key=lambda p: p.stat().st_mtime, reverse=True)

# Process recent log files (last 2 hours)
cutoff_time = datetime.now(timezone.utc).timestamp() - (2 * 3600)

all_exceptions = []
all_errors = []
all_critical = []

for log_file in log_files[:15]:  # Check top 15 most recent files
    try:
        mtime = log_file.stat().st_mtime
        if mtime < cutoff_time:
            continue
            
        with open(log_file, 'r', encoding='utf-8-sig') as f:
            for line in f:
                line = line.strip()
                if not line:
                    continue
                try:
                    event = json.loads(line)
                    ts_str = event.get('ts_utc', '')
                    if not ts_str:
                        continue
                    
                    # Parse timestamp
                    try:
                        ts = datetime.fromisoformat(ts_str.replace('Z', '+00:00'))
                        if ts.timestamp() < cutoff_time:
                            continue
                    except:
                        continue
                    
                    event_type = event.get('event', '')
                    level = event.get('level', '')
                    message = event.get('message', '')
                    data = event.get('data', {})
                    
                    # Track exceptions
                    if 'EXCEPTION' in event_type or 'exception' in str(data).lower():
                        all_exceptions.append({
                            'ts': ts,
                            'file': log_file.name,
                            'event': event_type,
                            'level': level,
                            'message': message,
                            'data': data
                        })
                    
                    # Track errors
                    if level == 'ERROR' or 'ERROR' in event_type:
                        all_errors.append({
                            'ts': ts,
                            'file': log_file.name,
                            'event': event_type,
                            'level': level,
                            'message': message,
                            'data': data
                        })
                    
                    # Track critical events
                    if 'CRITICAL' in event_type or 'CRITICAL' in message:
                        all_critical.append({
                            'ts': ts,
                            'file': log_file.name,
                            'event': event_type,
                            'level': level,
                            'message': message,
                            'data': data
                        })
                except:
                    pass
    except Exception as e:
        print(f"Error reading {log_file.name}: {e}")

# Sort all events by time
all_exceptions.sort(key=lambda x: x['ts'], reverse=True)
all_errors.sort(key=lambda x: x['ts'], reverse=True)
all_critical.sort(key=lambda x: x['ts'], reverse=True)

print(f"\nFound {len(all_exceptions)} exception events in last 2 hours")
print(f"Found {len(all_errors)} error events in last 2 hours")
print(f"Found {len(all_critical)} critical events in last 2 hours\n")

# Show most recent exceptions
if all_exceptions:
    print("="*80)
    print("MOST RECENT EXCEPTIONS (last 20)")
    print("="*80)
    for exc in all_exceptions[:20]:
        ts_str = exc['ts'].strftime('%Y-%m-%d %H:%M:%S UTC')
        print(f"\n{ts_str} | {exc['file']:20} | {exc['event']}")
        print(f"  Level: {exc['level']}")
        print(f"  Message: {exc['message']}")
        if exc['data']:
            error_msg = exc['data'].get('error', exc['data'].get('exception_message', exc['data'].get('error_message', '')))
            if error_msg:
                print(f"  Error: {error_msg[:200]}")

# Show most recent errors
if all_errors:
    print("\n" + "="*80)
    print("MOST RECENT ERRORS (last 20)")
    print("="*80)
    for err in all_errors[:20]:
        ts_str = err['ts'].strftime('%Y-%m-%d %H:%M:%S UTC')
        print(f"\n{ts_str} | {err['file']:20} | {err['event']}")
        print(f"  Message: {err['message']}")
        if err['data']:
            error_msg = err['data'].get('error', err['data'].get('error_message', ''))
            if error_msg:
                print(f"  Error: {error_msg[:200]}")

# Show most recent critical events
if all_critical:
    print("\n" + "="*80)
    print("MOST RECENT CRITICAL EVENTS (last 10)")
    print("="*80)
    for crit in all_critical[:10]:
        ts_str = crit['ts'].strftime('%Y-%m-%d %H:%M:%S UTC')
        print(f"\n{ts_str} | {crit['file']:20} | {crit['event']}")
        print(f"  Message: {crit['message']}")

# Group exceptions by type
if all_exceptions:
    print("\n" + "="*80)
    print("EXCEPTION TYPES SUMMARY")
    print("="*80)
    exception_types = defaultdict(int)
    for exc in all_exceptions:
        exc_type = exc['data'].get('exception_type', exc['event'])
        exception_types[exc_type] += 1
    
    for exc_type, count in sorted(exception_types.items(), key=lambda x: x[1], reverse=True):
        print(f"  {exc_type}: {count} occurrences")

print("\n" + "="*80)
