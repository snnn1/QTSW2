#!/usr/bin/env python3
"""Check current status of each stream from robot logs"""

import json
import glob
import os
from datetime import datetime
from collections import defaultdict

def parse_timestamp(ts_str):
    """Parse timestamp string to datetime"""
    try:
        # Handle ISO format with timezone
        if 'T' in ts_str:
            return datetime.fromisoformat(ts_str.replace('Z', '+00:00'))
        return None
    except:
        return None

def analyze_stream_log(log_file):
    """Analyze a single stream log file"""
    stream_name = os.path.basename(log_file).replace('robot_', '').replace('.jsonl', '')
    
    events = []
    try:
        with open(log_file, 'r', encoding='utf-8') as f:
            for line in f:
                try:
                    event = json.loads(line.strip())
                    events.append(event)
                except json.JSONDecodeError:
                    continue
    except Exception as e:
        return None
    
    if not events:
        return None
    
    # Get latest event
    latest_event = events[-1]
    
    # Find latest state from STATE_TRANSITION events or state field
    latest_state = None
    latest_state_event = None
    
    # First try to find STATE_TRANSITION events
    for event in reversed(events):
        if event.get('event') == 'STATE_TRANSITION' or event.get('event') == 'STREAM_STATE_TRANSITION':
            data = event.get('data', {})
            new_state = data.get('new_state') or data.get('to_state')
            if new_state:
                latest_state = new_state
                latest_state_event = event
                break
    
    # If no transition found, look for state field in events
    if not latest_state:
        for event in reversed(events):
            state = event.get('state', '')
            if state and state != 'ENGINE':
                latest_state = state
                latest_state_event = event
                break
    
    # Fallback to latest event state
    if not latest_state:
        latest_state = latest_event.get('state', 'UNKNOWN')
        if latest_state == 'ENGINE':
            latest_state = 'UNKNOWN'
    
    # Collect recent important events
    important_events = []
    event_types = defaultdict(int)
    
    for event in events[-100:]:  # Last 100 events
        event_type = event.get('event', '')
        event_types[event_type] += 1
        
        # Collect important events
        if event_type in ['STATE_TRANSITION', 'RANGE_LOCKED', 'RANGE_BUILDING', 'ARMED', 
                          'PRE_HYDRATION_COMPLETE', 'EXECUTION_GATE_INVARIANT_VIOLATION',
                          'EXECUTION_GATE_EVAL', 'BREAKOUT_DETECTED', 'ENTRY_DETECTED',
                          'JOURNAL_COMMITTED', 'DONE', 'ERROR', 'WARNING']:
            important_events.append(event)
    
    # Get latest timestamp
    latest_ts = latest_event.get('ts_utc', latest_event.get('timestamp', ''))
    latest_dt = parse_timestamp(latest_ts)
    
    return {
        'stream': stream_name,
        'latest_state': latest_state,
        'latest_timestamp': latest_ts,
        'latest_datetime': latest_dt,
        'total_events': len(events),
        'event_types': dict(event_types),
        'recent_important_events': important_events[-10:],  # Last 10 important events
        'latest_event': latest_event
    }

def main():
    print("="*80)
    print("STREAM STATUS ANALYSIS")
    print("="*80)
    
    # Find all stream log files
    log_files = glob.glob('logs/robot/robot_*.jsonl')
    log_files = [f for f in log_files if 'ENGINE' not in f]  # Exclude ENGINE log
    
    if not log_files:
        print("No stream log files found!")
        return
    
    print(f"\nFound {len(log_files)} stream log files\n")
    
    stream_statuses = []
    for log_file in sorted(log_files):
        status = analyze_stream_log(log_file)
        if status:
            stream_statuses.append(status)
    
    # Sort by latest timestamp (most recent first)
    stream_statuses.sort(key=lambda x: x['latest_datetime'] or datetime.min, reverse=True)
    
    # Display status for each stream
    for status in stream_statuses:
        print(f"\n{'='*80}")
        print(f"STREAM: {status['stream']}")
        print(f"{'='*80}")
        print(f"Current State: {status['latest_state']}")
        print(f"Latest Event Time: {status['latest_timestamp']}")
        print(f"Total Events: {status['total_events']}")
        
        # Show top event types
        event_types = status['event_types']
        if event_types:
            print(f"\nTop Event Types:")
            for event_type, count in sorted(event_types.items(), key=lambda x: x[1], reverse=True)[:10]:
                print(f"  {event_type}: {count}")
        
        # Show recent important events
        if status['recent_important_events']:
            print(f"\nRecent Important Events (last 10):")
            for event in status['recent_important_events']:
                event_type = event.get('event', 'UNKNOWN')
                ts = event.get('ts_utc', event.get('timestamp', ''))
                state = event.get('state', '')
                data = event.get('data', {})
                
                # Extract key info from data
                info_parts = []
                if 'message' in data:
                    info_parts.append(f"msg: {data['message']}")
                if 'reason' in data:
                    info_parts.append(f"reason: {data['reason']}")
                if 'error' in data:
                    info_parts.append(f"error: {data['error']}")
                
                info_str = f" - {', '.join(info_parts)}" if info_parts else ""
                print(f"  {ts} | {event_type} | {state}{info_str}")
        
        # Show latest event details if it's important
        latest = status['latest_event']
        latest_type = latest.get('event', '')
        if latest_type in ['ERROR', 'WARNING', 'EXECUTION_GATE_INVARIANT_VIOLATION', 'STATE_TRANSITION']:
            print(f"\nLatest Event Details:")
            print(f"  Type: {latest_type}")
            print(f"  State: {latest.get('state', 'N/A')}")
            data = latest.get('data', {})
            if data:
                print(f"  Data: {json.dumps(data, indent=4)}")
    
    # Summary
    print(f"\n{'='*80}")
    print("SUMMARY")
    print(f"{'='*80}")
    print(f"Total Streams: {len(stream_statuses)}")
    
    # Count by state
    state_counts = defaultdict(int)
    for status in stream_statuses:
        state_counts[status['latest_state']] += 1
    
    print(f"\nStreams by State:")
    for state, count in sorted(state_counts.items()):
        print(f"  {state}: {count}")
    
    # Find streams with issues
    issue_streams = []
    for status in stream_statuses:
        latest = status['latest_event']
        event_type = latest.get('event', '')
        if event_type in ['ERROR', 'EXECUTION_GATE_INVARIANT_VIOLATION']:
            issue_streams.append(status['stream'])
    
    if issue_streams:
        print(f"\nWARNING: Streams with Issues: {', '.join(issue_streams)}")
    else:
        print(f"\nOK: No streams with obvious issues detected")

if __name__ == '__main__':
    main()
