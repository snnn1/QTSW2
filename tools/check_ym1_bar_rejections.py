"""Check why YM1 bars are being rejected"""
import json
from pathlib import Path
from datetime import datetime

def main():
    qtsw2_root = Path(__file__).parent.parent
    log_file = qtsw2_root / "logs" / "robot" / "robot_ENGINE.jsonl"
    
    print("="*80)
    print("YM1 BAR REJECTION ANALYSIS")
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
    
    # Find bar rejection events
    print(f"\n[BAR REJECTION EVENTS FOR YM/MYM]")
    rejections = []
    for e in events:
        event_type = e.get('event', '')
        if 'BAR' in event_type.upper() and 'REJECT' in event_type.upper():
            event_str = json.dumps(e).upper()
            if 'YM' in event_str or 'MYM' in event_str:
                rejections.append(e)
    
    if rejections:
        print(f"\n  Found {len(rejections)} rejection events:")
        for e in rejections[-10:]:
            ts = e.get('ts_utc', '')[:19]
            event_type = e.get('event', 'N/A')
            inst = e.get('instrument', 'N/A')
            data = e.get('data', {})
            reason = data.get('rejection_reason', data.get('reason', 'N/A'))
            print(f"\n    {ts} | {event_type}")
            print(f"      Instrument: {inst}")
            print(f"      Reason: {reason}")
            if 'stream_id' in data:
                print(f"      Streams: {data.get('stream_id', 'N/A')}")
    else:
        print("  No bar rejection events found for YM/MYM")
    
    # Check BAR_DATE_MISMATCH events
    print(f"\n[BAR_DATE_MISMATCH EVENTS]")
    mismatches = []
    for e in events:
        if e.get('event') == 'BAR_DATE_MISMATCH':
            event_str = json.dumps(e).upper()
            if 'YM' in event_str or 'MYM' in event_str:
                mismatches.append(e)
    
    if mismatches:
        print(f"\n  Found {len(mismatches)} BAR_DATE_MISMATCH events:")
        for e in mismatches[-5:]:
            ts = e.get('ts_utc', '')[:19]
            inst = e.get('instrument', 'N/A')
            data = e.get('data', {})
            print(f"\n    {ts} | Inst: {inst}")
            print(f"      Reason: {data.get('rejection_reason', 'N/A')}")
            print(f"      Bar Date: {data.get('bar_trading_date', 'N/A')}")
            print(f"      Trading Date: {data.get('locked_trading_date', 'N/A')}")
            print(f"      Streams: {data.get('stream_id', 'N/A')}")
    else:
        print("  No BAR_DATE_MISMATCH events found")
    
    # Check most recent events around 15:00 UTC
    print(f"\n[EVENTS AROUND 15:00 UTC]")
    recent_events = []
    for e in events:
        ts_str = e.get('ts_utc', '')
        if ts_str.startswith('2026-01-26T15:0') or ts_str.startswith('2026-01-26T14:59'):
            event_str = json.dumps(e).upper()
            if 'YM' in event_str or 'MYM' in event_str or 'BAR' in e.get('event', '').upper():
                recent_events.append(e)
    
    if recent_events:
        print(f"\n  Found {len(recent_events)} YM/MYM/Bar events around 15:00 UTC:")
        for e in recent_events[-10:]:
            ts = e.get('ts_utc', '')[:19]
            event_type = e.get('event', 'N/A')
            inst = e.get('instrument', 'N/A')
            stream = e.get('stream', 'N/A')
            print(f"    {ts} | {event_type} | Inst: {inst} | Stream: {stream}")
    else:
        print("  No events found around 15:00 UTC")

if __name__ == '__main__':
    main()
