#!/usr/bin/env python3
"""
Check what instrument names are in bar events vs what watchdog expects.
"""
import json
from pathlib import Path
from datetime import datetime, timezone, timedelta
import pytz

def parse_timestamp(ts_str: str):
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
    chicago_tz = pytz.timezone('America/Chicago')
    now = datetime.now(timezone.utc)
    cutoff = now - timedelta(minutes=10)
    
    print("="*80)
    print("CHECKING BAR EVENT INSTRUMENT NAMES")
    print("="*80)
    
    # Load recent bar events
    events = []
    log_file = log_dir / "robot_ENGINE.jsonl"
    
    if log_file.exists():
        try:
            with open(log_file, 'r', encoding='utf-8') as f:
                for line in f:
                    line = line.strip()
                    if line:
                        try:
                            e = json.loads(line)
                            ts = parse_timestamp(e.get('ts_utc', ''))
                            if ts and ts >= cutoff:
                                if 'BAR' in e.get('event', '') or e.get('event') == 'ONBARUPDATE_CALLED':
                                    events.append(e)
                        except:
                            pass
        except Exception as e:
            print(f"[ERROR] Failed to read log file: {e}")
            return
    
    print(f"\nFound {len(events)} bar events in last 10 minutes\n")
    
    # Check instrument names in events
    print("="*80)
    print("INSTRUMENT NAMES IN BAR EVENTS")
    print("="*80)
    
    instruments = {}
    for e in events:
        if e.get('event') in ['BAR_RECEIVED_NO_STREAMS', 'BAR_ACCEPTED', 'ONBARUPDATE_CALLED']:
            inst = e.get('instrument', '')
            exec_inst = e.get('data', {}).get('execution_instrument', '')
            canonical_inst = e.get('data', {}).get('canonical_instrument', '')
            
            if inst or exec_inst or canonical_inst:
                key = f"inst={inst}, exec={exec_inst}, canon={canonical_inst}"
                if key not in instruments:
                    instruments[key] = []
                ts = parse_timestamp(e.get('ts_utc', ''))
                if ts:
                    instruments[key].append(ts)
    
    if instruments:
        print(f"\nFound {len(instruments)} unique instrument combinations:\n")
        for key, timestamps in sorted(instruments.items(), key=lambda x: max(x[1]) if x[1] else datetime.min.replace(tzinfo=timezone.utc), reverse=True):
            latest = max(timestamps) if timestamps else None
            if latest:
                age = (now - latest).total_seconds()
                ts_chicago = latest.astimezone(chicago_tz)
                print(f"  {key}")
                print(f"    Latest: {ts_chicago.strftime('%H:%M:%S')} CT ({age:.0f}s ago) | Count: {len(timestamps)}")
    else:
        print("\n[WARN] No instrument names found in bar events")
        print("       Checking event structure...")
        
        # Show sample event
        if events:
            sample = events[0]
            print(f"\nSample event structure:")
            print(f"  Event: {sample.get('event')}")
            print(f"  Instrument field: {sample.get('instrument', 'MISSING')}")
            print(f"  Data keys: {list(sample.get('data', {}).keys())}")
            if sample.get('data'):
                print(f"  Data content: {json.dumps(sample.get('data'), indent=2)[:500]}")
    
    # Check what watchdog expects
    print("\n" + "="*80)
    print("WATCHDOG EXPECTED INSTRUMENT NAMES")
    print("="*80)
    
    stalled_instruments = ['MNQ 03-26', 'MGC 04-26', 'MYM 03-26', 'MES 03-26']
    print("\nWatchdog is looking for:")
    for inst in stalled_instruments:
        print(f"  {inst}")
    
    print("\n[INFO] Watchdog tracks bars by execution_instrument full name (e.g., 'MES 03-26')")
    print("       Bar events need to include execution_instrument in data field")
    print("       for watchdog to track them properly")
    
    print("\n" + "="*80)
    print("SUMMARY")
    print("="*80)
    
    if instruments:
        print("\n[OK] Bars are being logged")
        print("[INFO] Check if execution_instrument field is present in bar events")
        print("       Watchdog needs execution_instrument to track bars per contract")
    else:
        print("\n[WARN] Instrument names not found in bar events")
        print("       Watchdog may not be able to track bars properly")
    
    print("="*80)

if __name__ == "__main__":
    main()
