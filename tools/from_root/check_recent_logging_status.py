#!/usr/bin/env python3
"""
Check recent logging status for tick and heartbeat events
"""
import json
import glob
from datetime import datetime, timezone
from collections import defaultdict

def load_events(pattern, event_filter=None, hours=2):
    """Load events from log files"""
    events = []
    cutoff = datetime.now(timezone.utc).timestamp() - (hours * 3600)
    
    for log_file in glob.glob(pattern):
        try:
            with open(log_file, 'r', encoding='utf-8') as f:
                for line in f:
                    line = line.strip()
                    if not line:
                        continue
                    try:
                        event = json.loads(line)
                        # Filter by time
                        utc_time_str = event.get('utc_time', '')
                        if utc_time_str:
                            try:
                                event_time = datetime.fromisoformat(utc_time_str.replace('Z', '+00:00'))
                                if event_time.timestamp() < cutoff:
                                    continue
                            except:
                                pass
                        
                        # Filter by event type if specified
                        if event_filter:
                            event_type = event.get('event_type', '')
                            if event_filter.lower() not in event_type.lower():
                                continue
                        
                        events.append(event)
                    except json.JSONDecodeError:
                        continue
        except Exception as e:
            print(f"  Error reading {log_file}: {e}")
    
    return events

def main():
    print("=" * 80)
    print("RECENT LOGGING STATUS CHECK")
    print("=" * 80)
    print()
    
    # Check ENGINE_TICK_CALLSITE events
    print("Checking ENGINE_TICK_CALLSITE events...")
    engine_events = load_events('logs/robot/robot_ENGINE*.jsonl', 'ENGINE_TICK_CALLSITE', hours=2)
    print(f"  Found {len(engine_events)} ENGINE_TICK_CALLSITE events in last 2 hours")
    
    if engine_events:
        print("  Recent ENGINE_TICK_CALLSITE events:")
        for event in engine_events[-5:]:
            utc_time = event.get('utc_time', 'N/A')[:19] if event.get('utc_time') else 'N/A'
            note = event.get('note', 'N/A')
            print(f"    {utc_time} | {note[:60]}")
    else:
        print("  [WARN] No ENGINE_TICK_CALLSITE events found!")
        print("         This could mean:")
        print("         - Code not synced/deployed yet")
        print("         - Tick() not being called")
        print("         - Events filtered out")
    print()
    
    # Check all ENGINE events
    print("Checking all ENGINE events (last 2 hours)...")
    all_engine_events = load_events('logs/robot/robot_ENGINE*.jsonl', None, hours=2)
    event_types = defaultdict(int)
    for event in all_engine_events:
        event_type = event.get('event_type', 'UNKNOWN')
        event_types[event_type] += 1
    
    print(f"  Total ENGINE events: {len(all_engine_events)}")
    print("  Top event types:")
    for event_type, count in sorted(event_types.items(), key=lambda x: x[1], reverse=True)[:10]:
        print(f"    {event_type}: {count}")
    print()
    
    # Check HEARTBEAT events
    print("Checking HEARTBEAT events...")
    heartbeat_events = load_events('logs/robot/robot_*.jsonl', 'HEARTBEAT', hours=2)
    print(f"  Found {len(heartbeat_events)} HEARTBEAT events in last 2 hours")
    
    if heartbeat_events:
        heartbeat_types = defaultdict(int)
        for event in heartbeat_events:
            event_type = event.get('event_type', 'UNKNOWN')
            heartbeat_types[event_type] += 1
        
        print("  By type:")
        for event_type, count in sorted(heartbeat_types.items(), key=lambda x: x[1], reverse=True):
            print(f"    {event_type}: {count}")
        
        print("  Recent HEARTBEAT events:")
        for event in heartbeat_events[-10:]:
            utc_time = event.get('utc_time', 'N/A')[:19] if event.get('utc_time') else 'N/A'
            event_type = event.get('event_type', 'N/A')
            stream = event.get('stream', 'N/A')
            print(f"    {utc_time} | {event_type} | {stream}")
    else:
        print("  [WARN] No HEARTBEAT events found!")
        print("         Stream heartbeats are rate-limited to 7 minutes")
        print("         They may not have fired yet")
    print()
    
    # Check TICK_CALLED_FROM_ONMARKETDATA
    print("Checking TICK_CALLED_FROM_ONMARKETDATA events...")
    tick_events = load_events('logs/robot/robot_*.jsonl', 'TICK_CALLED_FROM_ONMARKETDATA', hours=2)
    print(f"  Found {len(tick_events)} TICK_CALLED_FROM_ONMARKETDATA events in last 2 hours")
    
    if tick_events:
        print("  Recent events:")
        for event in tick_events[-5:]:
            utc_time = event.get('utc_time', 'N/A')[:19] if event.get('utc_time') else 'N/A'
            instrument = event.get('instrument', 'N/A')
            print(f"    {utc_time} | {instrument}")
    print()
    
    # Check stream-level tick events
    print("Checking stream-level TICK events...")
    stream_tick_events = load_events('logs/robot/robot_*.jsonl', None, hours=2)
    stream_tick_types = defaultdict(int)
    for event in stream_tick_events:
        event_type = event.get('event_type', '')
        if 'TICK' in event_type and event.get('stream') and event.get('stream') != '__engine__':
            stream_tick_types[event_type] += 1
    
    if stream_tick_types:
        print("  Stream-level TICK events:")
        for event_type, count in sorted(stream_tick_types.items(), key=lambda x: x[1], reverse=True):
            print(f"    {event_type}: {count}")
    else:
        print("  [INFO] No stream-level TICK events found (may be rate-limited)")
    print()
    
    # Summary
    print("=" * 80)
    print("SUMMARY")
    print("=" * 80)
    print(f"ENGINE_TICK_CALLSITE: {'[OK]' if engine_events else '[MISSING]'}")
    print(f"HEARTBEAT events: {'[OK]' if heartbeat_events else '[MISSING]'}")
    print(f"TICK_CALLED_FROM_ONMARKETDATA: {'[OK]' if tick_events else '[MISSING]'}")
    print(f"Stream-level TICK events: {'[OK]' if stream_tick_types else '[MISSING]'}")

if __name__ == '__main__':
    main()
