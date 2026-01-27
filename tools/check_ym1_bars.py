"""Check if YM bars are being received and routed to YM1"""
import json
from pathlib import Path
from datetime import datetime

def main():
    qtsw2_root = Path(__file__).parent.parent
    log_file = qtsw2_root / "logs" / "robot" / "robot_ENGINE.jsonl"
    
    print("="*80)
    print("YM1 BAR RECEPTION CHECK")
    print("="*80)
    
    if not log_file.exists():
        print("  No log file found")
        return
    
    events = []
    with open(log_file, 'r', encoding='utf-8-sig') as f:
        for line in f:
            line = line.strip()
            if line:
                try:
                    events.append(json.loads(line))
                except:
                    pass
    
    # Find BAR_ACCEPTED events for YM/MYM
    print(f"\n[BAR ACCEPTED EVENTS FOR YM/MYM]")
    bar_accepted = []
    for e in events:
        if e.get('event') == 'BAR_ACCEPTED':
            inst = e.get('instrument', '').upper()
            stream = e.get('stream', '')
            if 'YM' in inst or 'YM' in stream:
                bar_accepted.append(e)
    
    if bar_accepted:
        print(f"\n  Found {len(bar_accepted)} BAR_ACCEPTED events:")
        for e in bar_accepted[-10:]:
            ts = e.get('ts_utc', '')[:19]
            inst = e.get('instrument', 'N/A')
            stream = e.get('stream', 'N/A')
            print(f"    {ts} | Inst: {inst} | Stream: {stream}")
    else:
        print("  No BAR_ACCEPTED events found for YM/MYM")
    
    # Find BAR_DELIVERY_SUMMARY events
    print(f"\n[BAR DELIVERY SUMMARY - YM]")
    summaries = []
    for e in events:
        if e.get('event') == 'BAR_DELIVERY_SUMMARY':
            payload = e.get('data', {}).get('payload', '')
            if 'YM' in payload.upper():
                summaries.append(e)
    
    if summaries:
        print(f"\n  Found {len(summaries)} BAR_DELIVERY_SUMMARY events:")
        for e in summaries[-5:]:
            ts = e.get('ts_utc', '')[:19]
            payload = e.get('data', {}).get('payload', '')
            print(f"    {ts}: {payload[:200]}...")
    else:
        print("  No BAR_DELIVERY_SUMMARY events found for YM")
    
    # Check for ON_BAR events
    print(f"\n[ON_BAR EVENTS FOR YM1]")
    on_bar = []
    for e in events:
        if e.get('event') == 'ON_BAR':
            stream = e.get('stream', '')
            if 'YM1' in stream:
                on_bar.append(e)
    
    if on_bar:
        print(f"\n  Found {len(on_bar)} ON_BAR events for YM1:")
        for e in on_bar[-10:]:
            ts = e.get('ts_utc', '')[:19]
            print(f"    {ts}")
    else:
        print("  No ON_BAR events found for YM1")
    
    # Check for ENGINE_TICK_HEARTBEAT
    print(f"\n[ENGINE TICK HEARTBEAT - Recent]")
    heartbeats = [e for e in events if e.get('event') == 'ENGINE_TICK_HEARTBEAT']
    if heartbeats:
        print(f"\n  Found {len(heartbeats)} ENGINE_TICK_HEARTBEAT events")
        latest = heartbeats[-1]
        ts = latest.get('ts_utc', '')[:19]
        print(f"  Latest: {ts}")
    else:
        print("  No ENGINE_TICK_HEARTBEAT events found")

if __name__ == '__main__':
    main()
