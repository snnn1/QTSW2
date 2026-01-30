#!/usr/bin/env python3
"""Check if OnMarketData is being called"""
import json
import glob
from datetime import datetime, timezone

def main():
    print("=" * 80)
    print("ONMARKETDATA CHECK")
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
    
    # Check for TICK_CALLED_FROM_ONMARKETDATA
    tick_from_marketdata = [e for e in events if e.get('event') == 'TICK_CALLED_FROM_ONMARKETDATA']
    
    print(f"TICK_CALLED_FROM_ONMARKETDATA events: {len(tick_from_marketdata)}")
    
    if tick_from_marketdata:
        print("\nRecent TICK_CALLED_FROM_ONMARKETDATA events:")
        for e in tick_from_marketdata[-10:]:
            ts = e.get('ts_utc', '')[:19] if e.get('ts_utc') else 'N/A'
            instrument = e.get('data', {}).get('instrument', 'N/A') if isinstance(e.get('data'), dict) else 'N/A'
            print(f"  {ts} | {instrument}")
    else:
        print("\n[ISSUE] No TICK_CALLED_FROM_ONMARKETDATA events found!")
        print("        This means OnMarketData() is either:")
        print("        1. Not being called")
        print("        2. Returning early (not Last price ticks)")
        print("        3. Instrument mismatch")
        print("        4. Exception before Tick() call")
    
    # Check for any errors
    errors = [e for e in events if any(x in e.get('event', '').upper() for x in ['ERROR', 'EXCEPTION', 'FAIL'])]
    print(f"\nError events: {len(errors)}")
    if errors:
        print("Recent errors:")
        for e in errors[-10:]:
            ts = e.get('ts_utc', '')[:19] if e.get('ts_utc') else 'N/A'
            event_name = e.get('event', 'N/A')
            print(f"  {ts} | {event_name}")
    
    # Check ONBARUPDATE
    onbarupdate = [e for e in events if 'ONBARUPDATE' in e.get('event', '')]
    print(f"\nONBARUPDATE_CALLED events: {len(onbarupdate)}")
    if onbarupdate:
        print("Most recent:")
        for e in onbarupdate[-5:]:
            ts = e.get('ts_utc', '')[:19] if e.get('ts_utc') else 'N/A'
            print(f"  {ts}")

if __name__ == '__main__':
    main()
