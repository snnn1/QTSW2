"""Check if MYM bars are being rejected before reaching YM1"""
import json
from pathlib import Path
from datetime import datetime

def main():
    qtsw2_root = Path(__file__).parent.parent
    log_file = qtsw2_root / "logs" / "robot" / "robot_ENGINE.jsonl"
    
    print("="*80)
    print("MYM BAR REJECTION CHECK")
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
    
    # Check BAR_DATE_MISMATCH events for MYM
    print(f"\n[BAR_DATE_MISMATCH - MYM]")
    mismatches = []
    for e in events:
        if e.get('event') == 'BAR_DATE_MISMATCH':
            inst = e.get('instrument', '').upper()
            if 'MYM' in inst:
                mismatches.append(e)
    
    if mismatches:
        print(f"\n  Found {len(mismatches)} BAR_DATE_MISMATCH events for MYM:")
        for e in mismatches[-5:]:
            ts = e.get('ts_utc', '')[:19]
            data = e.get('data', {})
            reason = data.get('rejection_reason', 'N/A')
            bar_chicago = data.get('bar_chicago', 'N/A')
            session_start = data.get('session_start_chicago', 'N/A')
            session_end = data.get('session_end_chicago', 'N/A')
            print(f"\n    {ts}:")
            print(f"      Reason: {reason}")
            print(f"      Bar Chicago: {bar_chicago}")
            print(f"      Session Start: {session_start}")
            print(f"      Session End: {session_end}")
            print(f"      Streams: {data.get('stream_id', 'N/A')}")
    else:
        print("  No BAR_DATE_MISMATCH events found for MYM")
    
    # Check BAR_ACCEPTED events for MYM
    print(f"\n[BAR_ACCEPTED - MYM]")
    accepted = []
    for e in events:
        if e.get('event') == 'BAR_ACCEPTED':
            inst = e.get('instrument', '').upper()
            if 'MYM' in inst:
                accepted.append(e)
    
    if accepted:
        print(f"\n  Found {len(accepted)} BAR_ACCEPTED events for MYM:")
        for e in accepted[-5:]:
            ts = e.get('ts_utc', '')[:19]
            data = e.get('data', {})
            bar_chicago = data.get('bar_timestamp_chicago', 'N/A')
            print(f"    {ts} | Bar Chicago: {bar_chicago}")
    else:
        print("  No BAR_ACCEPTED events found for MYM")
        print("  This suggests MYM bars are being rejected before routing to streams")
    
    # Check recent MYM events around 15:00 UTC
    print(f"\n[RECENT MYM EVENTS AROUND 15:00 UTC]")
    recent = []
    for e in events:
        ts_str = e.get('ts_utc', '')
        if ts_str.startswith('2026-01-26T15:0'):
            event_str = json.dumps(e).upper()
            if 'MYM' in event_str:
                recent.append(e)
    
    if recent:
        print(f"\n  Found {len(recent)} MYM events around 15:00 UTC:")
        for e in recent[-10:]:
            ts = e.get('ts_utc', '')[:19]
            event_type = e.get('event', 'N/A')
            inst = e.get('instrument', 'N/A')
            print(f"    {ts} | {event_type} | Inst: {inst}")
    else:
        print("  No MYM events found around 15:00 UTC")

if __name__ == '__main__':
    main()
