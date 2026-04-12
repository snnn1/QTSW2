#!/usr/bin/env python3
"""
Analyze robot logs to find evidence of multiple range calculations per stream.
"""

import json
import glob
from collections import defaultdict
from datetime import datetime
from pathlib import Path

def analyze_range_calculations():
    # Check multiple possible log directories
    log_dirs = [
        Path("logs/robot"),
        Path("automation/logs"),
    ]
    
    robot_logs = []
    for log_dir in log_dirs:
        if log_dir.exists():
            robot_logs.extend(list(log_dir.glob("robot_*.jsonl")))
    
    # Also check for frontend_feed.jsonl which contains stream events
    for log_dir in log_dirs:
        if log_dir.exists():
            feed_file = log_dir / "frontend_feed.jsonl"
            if feed_file.exists():
                robot_logs.append(feed_file)
    
    if not robot_logs:
        print("No robot log files found in automation/logs/")
        return
    
    print(f"Found {len(robot_logs)} robot log files\n")
    
    # Track range calculation events per stream
    range_compute_starts = defaultdict(list)  # stream -> list of events
    range_compute_completes = defaultdict(list)
    range_initialized = defaultdict(list)
    range_computed_late = defaultdict(list)
    
    # Track state transitions
    state_transitions = defaultdict(list)
    
    # Process all log files
    for log_file in sorted(robot_logs):
        print(f"Processing {log_file.name}...")
        try:
            with open(log_file, 'r', encoding='utf-8') as f:
                for line_num, line in enumerate(f, 1):
                    if not line.strip():
                        continue
                    try:
                        event = json.loads(line)
                        event_type = event.get('event', '')
                        stream = event.get('stream', 'UNKNOWN')
                        trading_date = event.get('trading_date', '')
                        timestamp = event.get('ts_utc', '')
                        
                        # Track range calculation events
                        if 'RANGE_COMPUTE_START' in event_type:
                            range_compute_starts[stream].append({
                                'file': log_file.name,
                                'line': line_num,
                                'timestamp': timestamp,
                                'trading_date': trading_date,
                                'state': event.get('state', ''),
                                'event': event
                            })
                        
                        if 'RANGE_COMPUTE_COMPLETE' in event_type:
                            range_compute_completes[stream].append({
                                'file': log_file.name,
                                'line': line_num,
                                'timestamp': timestamp,
                                'trading_date': trading_date,
                                'state': event.get('state', ''),
                                'event': event
                            })
                        
                        if 'RANGE_INITIALIZED_FROM_HISTORY' in event_type:
                            range_initialized[stream].append({
                                'file': log_file.name,
                                'line': line_num,
                                'timestamp': timestamp,
                                'trading_date': trading_date,
                                'state': event.get('state', ''),
                                'event': event
                            })
                        
                        if 'RANGE_COMPUTED_LATE' in event_type:
                            range_computed_late[stream].append({
                                'file': log_file.name,
                                'line': line_num,
                                'timestamp': timestamp,
                                'trading_date': trading_date,
                                'state': event.get('state', ''),
                                'event': event
                            })
                        
                        if 'STREAM_STATE_TRANSITION' in event_type:
                            state_transitions[stream].append({
                                'file': log_file.name,
                                'line': line_num,
                                'timestamp': timestamp,
                                'trading_date': trading_date,
                                'previous_state': event.get('previous_state', ''),
                                'new_state': event.get('new_state', ''),
                                'event': event
                            })
                    
                    except json.JSONDecodeError:
                        continue
        
        except Exception as e:
            print(f"  Error reading {log_file.name}: {e}")
            continue
    
    # Analyze results
    print("\n" + "="*80)
    print("RANGE CALCULATION ANALYSIS")
    print("="*80)
    
    # Find streams with multiple RANGE_COMPUTE_START events
    print("\n1. STREAMS WITH MULTIPLE RANGE_COMPUTE_START EVENTS:")
    print("-" * 80)
    duplicates_found = False
    for stream in sorted(range_compute_starts.keys()):
        events = range_compute_starts[stream]
        if len(events) > 1:
            duplicates_found = True
            print(f"\n  Stream: {stream}")
            print(f"  Total RANGE_COMPUTE_START events: {len(events)}")
            
            # Group by trading date
            by_date = defaultdict(list)
            for event in events:
                date = event['trading_date']
                by_date[date].append(event)
            
            for date, date_events in sorted(by_date.items()):
                if len(date_events) > 1:
                    print(f"\n    Trading Date: {date} ({len(date_events)} occurrences)")
                    for i, event in enumerate(date_events, 1):
                        print(f"      {i}. {event['timestamp']} | State: {event['state']} | File: {event['file']}:{event['line']}")
                        if 'range_start_utc' in event['event']:
                            print(f"         Range window: {event['event'].get('range_start_utc', 'N/A')} to {event['event'].get('range_end_utc', 'N/A')}")
    
    if not duplicates_found:
        print("  [OK] No duplicate RANGE_COMPUTE_START events found")
    
    # Check for RANGE_INITIALIZED_FROM_HISTORY followed by RANGE_COMPUTE_START
    print("\n\n2. STREAMS WITH RANGE_INITIALIZED_FROM_HISTORY FOLLOWED BY RANGE_COMPUTE_START:")
    print("-" * 80)
    pattern_found = False
    for stream in sorted(set(list(range_initialized.keys()) + list(range_compute_starts.keys()))):
        init_events = range_initialized.get(stream, [])
        compute_events = range_compute_starts.get(stream, [])
        
        if init_events and compute_events:
            # Check if there's an init event followed by a compute event on the same date
            for init_event in init_events:
                init_date = init_event['trading_date']
                init_time = init_event['timestamp']
                
                for compute_event in compute_events:
                    compute_date = compute_event['trading_date']
                    compute_time = compute_event['timestamp']
                    
                    if init_date == compute_date and compute_time > init_time:
                        pattern_found = True
                        print(f"\n  Stream: {stream} | Date: {init_date}")
                        print(f"    RANGE_INITIALIZED_FROM_HISTORY: {init_time} | State: {init_event['state']}")
                        print(f"    RANGE_COMPUTE_START:            {compute_time} | State: {compute_event['state']}")
                        print(f"    Time difference: {compute_time} - {init_time}")
    
    if not pattern_found:
        print("  [OK] No pattern of RANGE_INITIALIZED followed by RANGE_COMPUTE_START found")
    
    # Check state transitions around range calculations
    print("\n\n3. STATE TRANSITIONS AROUND RANGE CALCULATIONS:")
    print("-" * 80)
    for stream in sorted(range_compute_starts.keys()):
        compute_events = range_compute_starts[stream]
        transitions = state_transitions.get(stream, [])
        
        if len(compute_events) > 1:
            print(f"\n  Stream: {stream}")
            for compute_event in compute_events:
                compute_time = compute_event['timestamp']
                compute_date = compute_event['trading_date']
                
                # Find nearby state transitions
                nearby_transitions = [
                    t for t in transitions
                    if t['trading_date'] == compute_date and abs(
                        (datetime.fromisoformat(t['timestamp'].replace('Z', '+00:00')) - 
                         datetime.fromisoformat(compute_time.replace('Z', '+00:00'))).total_seconds()
                    ) < 60  # Within 60 seconds
                ]
                
                if nearby_transitions:
                    print(f"\n    RANGE_COMPUTE_START at {compute_time}:")
                    for trans in nearby_transitions:
                        print(f"      {trans['previous_state']} → {trans['new_state']} at {trans['timestamp']}")
    
    # Summary statistics
    print("\n\n4. SUMMARY STATISTICS:")
    print("-" * 80)
    print(f"  Total streams analyzed: {len(set(list(range_compute_starts.keys()) + list(range_initialized.keys())))}")
    print(f"  Streams with RANGE_COMPUTE_START: {len(range_compute_starts)}")
    print(f"  Streams with RANGE_INITIALIZED_FROM_HISTORY: {len(range_initialized)}")
    print(f"  Streams with RANGE_COMPUTED_LATE: {len(range_computed_late)}")
    
    total_compute_starts = sum(len(events) for events in range_compute_starts.values())
    print(f"\n  Total RANGE_COMPUTE_START events: {total_compute_starts}")
    
    streams_with_multiple = sum(1 for events in range_compute_starts.values() if len(events) > 1)
    print(f"  Streams with multiple RANGE_COMPUTE_START: {streams_with_multiple}")
    
    if streams_with_multiple > 0:
        print(f"\n  ⚠️  ISSUE DETECTED: {streams_with_multiple} stream(s) have multiple range calculations")

if __name__ == "__main__":
    analyze_range_calculations()
