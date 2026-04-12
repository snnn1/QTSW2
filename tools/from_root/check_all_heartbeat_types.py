#!/usr/bin/env python3
"""
Check for all types of heartbeat events.
"""
import json
from pathlib import Path
from datetime import datetime, timezone, timedelta
from collections import defaultdict
import pytz

def parse_timestamp(ts_str: str):
    """Parse ISO timestamp"""
    if not ts_str:
        return None
    try:
        if 'T' in ts_str:
            if ts_str.endswith('Z'):
                ts_str = ts_str[:-1] + '+00:00'
            elif '+' not in ts_str:
                ts_str = ts_str + '+00:00'
            dt = datetime.fromisoformat(ts_str)
            if dt.tzinfo is None:
                dt = dt.replace(tzinfo=timezone.utc)
            return dt
    except:
        pass
    return None

def main():
    log_dir = Path("logs/robot")
    cutoff = datetime.now(timezone.utc) - timedelta(hours=6)
    chicago_tz = pytz.timezone('America/Chicago')
    
    print("="*80)
    print("ALL HEARTBEAT TYPES CHECK")
    print("="*80)
    
    # Load all events
    events = []
    for log_file in sorted(log_dir.glob("robot_*.jsonl"), reverse=True):
        try:
            with open(log_file, 'r', encoding='utf-8') as f:
                for line in f:
                    line = line.strip()
                    if line:
                        try:
                            e = json.loads(line)
                            ts = parse_timestamp(e.get('ts_utc', ''))
                            if ts and ts >= cutoff:
                                events.append(e)
                        except:
                            pass
        except:
            pass
    
    events.sort(key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
    
    print(f"\nLoaded {len(events):,} events from last 6 hours\n")
    
    # Check for specific heartbeat event types
    heartbeat_types = [
        'HEARTBEAT',
        'ENGINE_TICK_HEARTBEAT',
        'ENGINE_BAR_HEARTBEAT',
        'ENGINE_TICK_HEARTBEAT_AUDIT',
        'SUSPENDED_STREAM_HEARTBEAT'
    ]
    
    print("="*80)
    print("HEARTBEAT EVENT TYPES:")
    print("="*80)
    
    total_heartbeats = 0
    for event_type in heartbeat_types:
        matching = [e for e in events if e.get('event') == event_type]
        count = len(matching)
        total_heartbeats += count
        
        if matching:
            latest = max(matching, key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
            ts = parse_timestamp(latest.get('ts_utc', ''))
            if ts:
                ts_chicago = ts.astimezone(chicago_tz)
                age_minutes = (datetime.now(timezone.utc) - ts).total_seconds() / 60
                print(f"    {event_type}: {count:6} events | Latest: {ts_chicago.strftime('%H:%M:%S')} CT ({age_minutes:.1f} min ago)")
            else:
                print(f"    {event_type}: {count:6} events")
        else:
            print(f"    {event_type}: {count:6} events | NOT FOUND")
    
    print(f"\n  TOTAL HEARTBEAT EVENTS: {total_heartbeats}")
    
    # Check for any event with "heartbeat" in the name (case insensitive)
    all_heartbeat_like = [e for e in events if 'heartbeat' in e.get('event', '').lower()]
    
    if all_heartbeat_like:
        print(f"\n  Events with 'heartbeat' in name (case-insensitive): {len(all_heartbeat_like)}")
        by_type = defaultdict(int)
        for e in all_heartbeat_like:
            by_type[e.get('event')] += 1
        
        for event_type, count in sorted(by_type.items()):
            print(f"    {event_type}: {count}")
    
    # Check what events ARE being logged (top 20)
    print("\n" + "="*80)
    print("TOP EVENT TYPES (to see what IS being logged):")
    print("="*80)
    
    all_event_types = defaultdict(int)
    for e in events:
        all_event_types[e.get('event', 'UNKNOWN')] += 1
    
    print(f"\n  Top 20 event types:")
    for event_type, count in sorted(all_event_types.items(), key=lambda x: x[1], reverse=True)[:20]:
        print(f"    {event_type}: {count:,}")

if __name__ == "__main__":
    main()
