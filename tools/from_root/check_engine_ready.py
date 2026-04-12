#!/usr/bin/env python3
"""Check if engine reached Realtime state"""
import json
import glob
from datetime import datetime, timezone

def main():
    print("=" * 80)
    print("ENGINE READY STATE CHECK")
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
    
    # Check for REALTIME_STATE_REACHED
    realtime = [e for e in events if 'REALTIME_STATE_REACHED' in e.get('event', '')]
    
    print(f"REALTIME_STATE_REACHED events: {len(realtime)}")
    if realtime:
        print("Found REALTIME events:")
        for e in realtime:
            ts = e.get('ts_utc', '')[:19] if e.get('ts_utc') else 'N/A'
            data = e.get('data', {})
            engine_ready = data.get('engine_ready', 'N/A') if isinstance(data, dict) else 'N/A'
            print(f"  {ts} | engine_ready={engine_ready}")
    else:
        print("[ISSUE] No REALTIME_STATE_REACHED events found!")
        print("        This means the strategy hasn't reached Realtime state yet")
        print("        OnMarketData() won't call Tick() until _engineReady = true")
    
    print()
    print("Most recent events (checking state transitions):")
    for e in events[-20:]:
        ts = e.get('ts_utc', '')[:19] if e.get('ts_utc') else 'N/A'
        event_name = e.get('event', 'N/A')
        print(f"  {ts} | {event_name}")

if __name__ == '__main__':
    main()
