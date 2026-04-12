#!/usr/bin/env python3
"""Check recent ENGINE events after reset"""
import json
import glob
from datetime import datetime, timezone

def main():
    print("=" * 80)
    print("RECENT ENGINE EVENTS CHECK (After Reset)")
    print("=" * 80)
    print()
    
    # Load recent events
    events = []
    cutoff = datetime.now(timezone.utc).timestamp() - 600  # Last 10 minutes
    
    for log_file in glob.glob('logs/robot/robot_ENGINE*.jsonl'):
        try:
            with open(log_file, 'r', encoding='utf-8') as f:
                for line in f:
                    line = line.strip()
                    if not line:
                        continue
                    try:
                        event = json.loads(line)
                        ts_utc = event.get('ts_utc', '')
                        if ts_utc:
                            try:
                                event_time = datetime.fromisoformat(ts_utc.replace('Z', '+00:00'))
                                if event_time.timestamp() >= cutoff:
                                    events.append(event)
                            except:
                                pass
                    except:
                        continue
        except Exception as e:
            print(f"  Error reading {log_file}: {e}")
    
    # Sort by timestamp
    events.sort(key=lambda x: x.get('ts_utc', ''), reverse=True)
    
    print(f"Total ENGINE events in last 10 minutes: {len(events)}")
    print()
    
    # Check for ENGINE_TICK_CALLSITE
    tick_callsite = [e for e in events if e.get('event') == 'ENGINE_TICK_CALLSITE']
    print(f"ENGINE_TICK_CALLSITE events: {len(tick_callsite)}")
    
    # Show recent events
    print("\nMost recent ENGINE events:")
    for e in events[:30]:
        ts = e.get('ts_utc', '')[:19] if e.get('ts_utc') else 'N/A'
        event_name = e.get('event', 'N/A')
        print(f"  {ts} | {event_name}")
    
    # Check event types
    print("\nEvent type counts:")
    event_counts = {}
    for e in events:
        event_name = e.get('event', 'UNKNOWN')
        event_counts[event_name] = event_counts.get(event_name, 0) + 1
    
    for event_name, count in sorted(event_counts.items(), key=lambda x: x[1], reverse=True)[:15]:
        print(f"  {event_name}: {count}")

if __name__ == '__main__':
    main()
