#!/usr/bin/env python3
"""Check if Tick() is being called after reset"""
import json
import glob
from datetime import datetime, timezone

def main():
    print("=" * 80)
    print("TICK() CALLS CHECK (After Reset)")
    print("=" * 80)
    print()
    
    # Load recent events
    events = []
    reset_time = datetime(2026, 1, 30, 21, 38, 0, tzinfo=timezone.utc).timestamp()
    
    for log_file in glob.glob('logs/robot/robot_*.jsonl'):
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
                                if event_time.timestamp() >= reset_time:
                                    events.append(event)
                            except:
                                pass
                    except:
                        continue
        except Exception as e:
            print(f"  Error reading {log_file}: {e}")
    
    # Sort by timestamp
    events.sort(key=lambda x: x.get('ts_utc', ''))
    
    print(f"Events after reset (21:38:00): {len(events)}")
    print()
    
    # Check for TICK-related events
    tick_callsite = [e for e in events if e.get('event') == 'ENGINE_TICK_CALLSITE']
    tick_onmarket = [e for e in events if e.get('event') == 'TICK_CALLED_FROM_ONMARKETDATA']
    
    print(f"ENGINE_TICK_CALLSITE: {len(tick_callsite)}")
    print(f"TICK_CALLED_FROM_ONMARKETDATA: {len(tick_onmarket)}")
    print()
    
    if tick_onmarket:
        print("TICK_CALLED_FROM_ONMARKETDATA events:")
        for e in tick_onmarket[:10]:
            print(f"  {e.get('ts_utc', '')[:19]}")
    else:
        print("[INFO] No TICK_CALLED_FROM_ONMARKETDATA events after reset")
        print("       This means OnMarketData hasn't called Tick() yet")
        print("       (Market might be closed or no market data flowing)")
    
    print()
    print("Most recent events (all types):")
    for e in events[-20:]:
        ts = e.get('ts_utc', '')[:19] if e.get('ts_utc') else 'N/A'
        event_name = e.get('event', 'N/A')
        print(f"  {ts} | {event_name}")

if __name__ == '__main__':
    main()
