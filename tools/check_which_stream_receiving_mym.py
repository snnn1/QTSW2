"""Check which stream is actually receiving MYM bars"""
import json
from pathlib import Path
from datetime import datetime

def main():
    qtsw2_root = Path(__file__).parent.parent
    log_file = qtsw2_root / "logs" / "robot" / "robot_ENGINE.jsonl"
    
    print("="*80)
    print("WHICH STREAM IS RECEIVING MYM BARS?")
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
    
    # Check BAR_DELIVERY_TO_STREAM events for MYM
    print(f"\n[BAR_DELIVERY_TO_STREAM - MYM]")
    deliveries = []
    for e in events:
        if e.get('event') == 'BAR_DELIVERY_TO_STREAM':
            inst = e.get('data', {}).get('instrument', '').upper()
            if 'MYM' in inst:
                deliveries.append(e)
    
    if deliveries:
        print(f"\n  Found {len(deliveries)} BAR_DELIVERY_TO_STREAM events for MYM:")
        for e in deliveries[-10:]:
            ts = e.get('ts_utc', '')[:19]
            data = e.get('data', {})
            stream = data.get('stream', 'N/A')
            inst = data.get('instrument', 'N/A')
            print(f"    {ts} | Stream: {stream} | Inst: {inst}")
    else:
        print("  No BAR_DELIVERY_TO_STREAM events found for MYM")
        print("  (This event is rate-limited to every 5 minutes, so may not appear)")
    
    # Check BAR_ADMISSION_PROOF for any YM stream
    print(f"\n[BAR_ADMISSION_PROOF - All YM Streams]")
    proofs = []
    for e in events:
        if e.get('event') == 'BAR_ADMISSION_PROOF':
            stream = e.get('stream', '').upper()
            if 'YM' in stream:
                proofs.append(e)
    
    if proofs:
        print(f"\n  Found {len(proofs)} BAR_ADMISSION_PROOF events for YM streams:")
        streams_seen = set()
        for e in proofs[-20:]:
            stream = e.get('stream', 'N/A')
            streams_seen.add(stream)
            ts = e.get('ts_utc', '')[:19]
            data = e.get('data', {})
            comparison = data.get('comparison_result', 'N/A')
            bar_chicago = data.get('bar_time_chicago', 'N/A')[:19] if data.get('bar_time_chicago') else 'N/A'
            print(f"    {ts} | Stream: {stream} | Bar: {bar_chicago} | In Range: {comparison}")
        
        print(f"\n  Streams receiving bars: {sorted(streams_seen)}")
    else:
        print("  No BAR_ADMISSION_PROOF events found for any YM stream")
        print("  This suggests bars are not reaching any YM stream's OnBar method")
    
    # Check STREAMS_CREATED to see YM1/YM2 configuration
    print(f"\n[STREAMS_CREATED - YM Streams]")
    streams_created = []
    for e in events:
        if e.get('event') == 'STREAMS_CREATED':
            payload = e.get('data', {}).get('payload', '')
            if 'YM' in payload.upper():
                streams_created.append(e)
    
    if streams_created:
        latest = streams_created[-1]
        ts = latest.get('ts_utc', '')[:19]
        print(f"\n  Latest STREAMS_CREATED ({ts}):")
        print(f"    Payload: {latest.get('data', {}).get('payload', 'N/A')[:500]}...")

if __name__ == '__main__':
    main()
