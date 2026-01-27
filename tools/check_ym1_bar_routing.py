"""Check if MYM bars are being routed to YM1 stream"""
import json
from pathlib import Path
from datetime import datetime

def main():
    qtsw2_root = Path(__file__).parent.parent
    log_file = qtsw2_root / "logs" / "robot" / "robot_ENGINE.jsonl"
    
    print("="*80)
    print("YM1 BAR ROUTING CHECK")
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
    
    # Check BAR_DELIVERY_SUMMARY to see which streams are receiving MYM bars
    print(f"\n[BAR DELIVERY SUMMARY - MYM]")
    summaries = []
    for e in events:
        if e.get('event') == 'BAR_DELIVERY_SUMMARY':
            payload = e.get('data', {}).get('payload', '')
            if 'MYM' in payload.upper():
                summaries.append(e)
    
    if summaries:
        latest = summaries[-1]
        ts = latest.get('ts_utc', '')[:19]
        payload = latest.get('data', {}).get('payload', '')
        print(f"\n  Latest Summary ({ts}):")
        print(f"    {payload}")
    
    # Check for BAR_ADMISSION_PROOF events (these log every bar admission decision)
    print(f"\n[BAR ADMISSION PROOF - YM1]")
    proofs = []
    for e in events:
        if e.get('event') == 'BAR_ADMISSION_PROOF':
            stream = e.get('stream', '')
            if 'YM1' in stream.upper():
                proofs.append(e)
    
    if proofs:
        print(f"\n  Found {len(proofs)} BAR_ADMISSION_PROOF events for YM1:")
        for e in proofs[-5:]:
            ts = e.get('ts_utc', '')[:19]
            data = e.get('data', {})
            comparison = data.get('comparison_result', 'N/A')
            bar_chicago = data.get('bar_time_chicago', 'N/A')[:19] if data.get('bar_time_chicago') else 'N/A'
            slot_time = data.get('slot_time_chicago', 'N/A')[:19] if data.get('slot_time_chicago') else 'N/A'
            print(f"    {ts} | Bar: {bar_chicago} | Slot: {slot_time} | In Range: {comparison}")
    else:
        print("  No BAR_ADMISSION_PROOF events found for YM1")
        print("  This suggests bars are not reaching YM1's OnBar method")
    
    # Check for BAR_FILTERED_OUT events
    print(f"\n[BAR FILTERED OUT - YM1]")
    filtered = []
    for e in events:
        if e.get('event') == 'BAR_FILTERED_OUT':
            stream = e.get('stream', '')
            if 'YM1' in stream.upper():
                filtered.append(e)
    
    if filtered:
        print(f"\n  Found {len(filtered)} BAR_FILTERED_OUT events for YM1:")
        for e in filtered[-5:]:
            ts = e.get('ts_utc', '')[:19]
            data = e.get('data', {})
            reason = data.get('reason', 'N/A')
            bar_chicago = data.get('bar_chicago', 'N/A')[:19] if data.get('bar_chicago') else 'N/A'
            print(f"    {ts} | Bar: {bar_chicago} | Reason: {reason}")
    else:
        print("  No BAR_FILTERED_OUT events found for YM1")
    
    # Check recent events around 15:00 UTC
    print(f"\n[RECENT EVENTS AROUND 15:00 UTC]")
    recent = []
    for e in events:
        ts_str = e.get('ts_utc', '')
        if ts_str.startswith('2026-01-26T15:0'):
            event_str = json.dumps(e).upper()
            if 'YM' in event_str or 'MYM' in event_str:
                recent.append(e)
    
    if recent:
        print(f"\n  Found {len(recent)} YM/MYM events around 15:00 UTC:")
        for e in recent[-10:]:
            ts = e.get('ts_utc', '')[:19]
            event_type = e.get('event', 'N/A')
            stream = e.get('stream', 'N/A')
            print(f"    {ts} | {event_type} | Stream: {stream}")

if __name__ == '__main__':
    main()
