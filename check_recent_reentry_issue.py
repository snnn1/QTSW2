#!/usr/bin/env python3
"""
Check recent logs for flatten -> re-entry issues.
Looks for:
1. Position closure events (flatten, exit fills)
2. Entry stop cancellation events
3. Re-entry fills after closure
4. Missing cancellation events
"""

import json
import sys
from pathlib import Path
from datetime import datetime, timedelta
from collections import defaultdict

def parse_timestamp(ts_str):
    """Parse timestamp string to datetime."""
    try:
        # Try ISO format first
        if 'T' in ts_str or '+' in ts_str or 'Z' in ts_str:
            return datetime.fromisoformat(ts_str.replace('Z', '+00:00'))
        # Try other formats
        for fmt in ['%Y-%m-%d %H:%M:%S.%f', '%Y-%m-%d %H:%M:%S', '%Y/%m/%d %H:%M:%S']:
            try:
                return datetime.strptime(ts_str, fmt)
            except:
                continue
    except:
        pass
    return None

def find_log_files(log_dir):
    """Find recent log files."""
    log_path = Path(log_dir)
    if not log_path.exists():
        return []
    
    # Find all .jsonl files modified in last 24 hours
    cutoff = datetime.now() - timedelta(hours=24)
    files = []
    for f in log_path.glob("*.jsonl"):
        try:
            mtime = datetime.fromtimestamp(f.stat().st_mtime)
            if mtime > cutoff:
                files.append((f, mtime))
        except:
            pass
    
    return sorted(files, key=lambda x: x[1], reverse=True)

def load_log_events(log_file):
    """Load events from JSONL file."""
    events = []
    try:
        with open(log_file, 'r', encoding='utf-8') as f:
            for line in f:
                line = line.strip()
                if not line:
                    continue
                try:
                    event = json.loads(line)
                    events.append(event)
                except json.JSONDecodeError:
                    continue
    except Exception as e:
        print(f"Error reading {log_file}: {e}", file=sys.stderr)
    return events

def analyze_reentry_issues(events):
    """Analyze events for flatten -> re-entry patterns."""
    issues = []
    
    # Group events by instrument and time
    by_instrument = defaultdict(list)
    for event in events:
        instrument = event.get('instrument', event.get('Instrument', 'UNKNOWN'))
        timestamp = event.get('timestamp', event.get('Timestamp', event.get('timestamp_utc', '')))
        by_instrument[instrument].append((timestamp, event))
    
    for instrument, inst_events in by_instrument.items():
        # Sort by timestamp
        inst_events.sort(key=lambda x: x[0])
        
        # Look for patterns: flatten/exit -> entry fill
        for i, (ts1, evt1) in enumerate(inst_events):
            # Check if this is a closure event
            is_closure = False
            closure_type = None
            
            event_type = evt1.get('event_type', evt1.get('EventType', ''))
            if 'FLATTEN' in event_type.upper() or 'EXECUTION_EXIT_FILL' in event_type:
                is_closure = True
                closure_type = event_type
            
            if not is_closure:
                continue
            
            # Look for entry fills within 10 seconds
            closure_time = parse_timestamp(ts1)
            if not closure_time:
                continue
            
            for j in range(i + 1, min(i + 100, len(inst_events))):  # Check next 100 events
                ts2, evt2 = inst_events[j]
                evt2_time = parse_timestamp(ts2)
                if not evt2_time:
                    continue
                
                time_diff = (evt2_time - closure_time).total_seconds()
                if time_diff > 10:  # Only check within 10 seconds
                    break
                
                evt2_type = evt2.get('event_type', evt2.get('EventType', ''))
                evt2_tag = evt2.get('tag', evt2.get('Tag', ''))
                
                # Check if this is an entry fill
                is_entry_fill = (
                    'EXECUTION_ENTRY_FILL' in evt2_type or
                    ('EXECUTION_FILLED' in evt2_type and 
                     evt2_tag and 
                     not evt2_tag.endswith(':STOP') and 
                     not evt2_tag.endswith(':TARGET'))
                )
                
                if is_entry_fill:
                    # Check for cancellation events between closure and re-entry
                    cancellations = []
                    for k in range(i + 1, j):
                        ts3, evt3 = inst_events[k]
                        evt3_type = evt3.get('event_type', evt3.get('EventType', ''))
                        if 'CANCELLED' in evt3_type.upper() or 'CANCEL' in evt3_type.upper():
                            cancellations.append((ts3, evt3_type))
                    
                    issues.append({
                        'instrument': instrument,
                        'closure_time': ts1,
                        'closure_type': closure_type,
                        'closure_intent': evt1.get('intent_id', evt1.get('IntentId', '')),
                        'reentry_time': ts2,
                        'reentry_intent': evt2.get('intent_id', evt2.get('IntentId', '')),
                        'reentry_tag': evt2_tag,
                        'time_diff_seconds': time_diff,
                        'cancellations': cancellations,
                        'has_cancellation': len(cancellations) > 0
                    })
                    break
    
    return issues

def main():
    # Find log directory
    log_dir = Path("logs/robot")
    if not log_dir.exists():
        # Try environment variable
        import os
        log_dir = Path(os.getenv("QTSW2_LOG_DIR", "logs/robot"))
    
    if not log_dir.exists():
        print(f"ERROR: Log directory not found: {log_dir}")
        print("Please check logs/robot or set QTSW2_LOG_DIR environment variable")
        return 1
    
    print(f"Searching for log files in: {log_dir}")
    log_files = find_log_files(log_dir)
    
    if not log_files:
        print("No recent log files found (last 24 hours)")
        return 1
    
    print(f"Found {len(log_files)} recent log file(s)")
    print()
    
    # Load all events
    all_events = []
    for log_file, mtime in log_files:
        print(f"Loading: {log_file.name} (modified: {mtime})")
        events = load_log_events(log_file)
        all_events.extend(events)
        print(f"  Loaded {len(events)} events")
    
    print(f"\nTotal events: {len(all_events)}")
    print()
    
    # Analyze for re-entry issues
    issues = analyze_reentry_issues(all_events)
    
    if not issues:
        print("[OK] No flatten -> re-entry issues found in recent logs")
        return 0
    
        print(f"[WARN] Found {len(issues)} potential re-entry issue(s):\n")
    
    for i, issue in enumerate(issues, 1):
        print(f"Issue #{i}:")
        print(f"  Instrument: {issue['instrument']}")
        print(f"  Closure: {issue['closure_type']} at {issue['closure_time']}")
        print(f"    Intent: {issue['closure_intent']}")
        print(f"  Re-entry: Entry fill at {issue['reentry_time']} ({issue['time_diff_seconds']:.2f}s later)")
        print(f"    Intent: {issue['reentry_intent']}")
        print(f"    Tag: {issue['reentry_tag']}")
        print(f"  Cancellations between closure and re-entry: {len(issue['cancellations'])}")
        if issue['cancellations']:
            for ts, evt_type in issue['cancellations']:
                print(f"    - {evt_type} at {ts}")
        else:
            print("    [ERROR] NO CANCELLATION EVENTS FOUND - This is the problem!")
        print()
    
    return 1

if __name__ == '__main__':
    sys.exit(main())
