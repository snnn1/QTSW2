#!/usr/bin/env python3
"""
Check if immediate entry was detected at lock for RTY2
"""
import json
import datetime
from pathlib import Path

def check_immediate_entry():
    """Check if immediate entry was detected"""
    log_file = Path("logs/robot/robot_RTY.jsonl")
    
    today_start = datetime.datetime.now(datetime.timezone.utc).replace(
        hour=0, minute=0, second=0, microsecond=0
    )
    
    events = []
    with open(log_file, 'r', encoding='utf-8-sig', errors='ignore') as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            try:
                event = json.loads(line)
                if 'ts_utc' in event:
                    ts = datetime.datetime.fromisoformat(
                        event['ts_utc'].replace('Z', '+00:00')
                    )
                    if ts >= today_start:
                        events.append(event)
            except:
                continue
    
    # Find range lock event
    lock_events = [e for e in events if 'RANGE_LOCKED' in e.get('event', '') and '15:30:01' in e.get('ts_utc', '')]
    
    # Find SLOT_END_SUMMARY around lock time
    summary_events = [e for e in events if 'SLOT_END_SUMMARY' in e.get('event', '') and '15:30:01' in e.get('ts_utc', '')]
    
    print("="*80)
    print("RTY2 IMMEDIATE ENTRY CHECK")
    print("="*80)
    
    if summary_events:
        for event in summary_events:
            data = event.get('data', {})
            payload = data.get('payload', '')
            print(f"\nSLOT_END_SUMMARY at {event.get('ts_utc', '')[:19]}:")
            print(f"  Payload: {payload[:200]}")
            
            # Check entry_detected in payload
            if 'entry_detected' in payload.lower():
                if 'False' in payload:
                    print("  [INFO] entry_detected = False (no immediate entry detected)")
                elif 'True' in payload:
                    print("  [WARNING] entry_detected = True (immediate entry detected)")
    
    # Check for immediate entry detection
    immediate_entries = [e for e in events if 'IMMEDIATE_AT_LOCK' in str(e) or ('RecordIntendedEntry' in str(e) and 'IMMEDIATE' in str(e))]
    
    print(f"\nImmediate Entry Events: {len(immediate_entries)}")
    for event in immediate_entries:
        print(f"  {event.get('ts_utc', '')[:19]} | {event.get('event', 'UNKNOWN')}")
    
    # Check for DRYRUN_INTENDED_ENTRY with IMMEDIATE_AT_LOCK
    dryrun_entries = [e for e in events if 'DRYRUN_INTENDED_ENTRY' in e.get('event', '')]
    immediate_dryrun = [e for e in dryrun_entries if e.get('data', {}).get('trigger_reason') == 'IMMEDIATE_AT_LOCK']
    
    print(f"\nDRYRUN_INTENDED_ENTRY with IMMEDIATE_AT_LOCK: {len(immediate_dryrun)}")
    for event in immediate_dryrun:
        ts = event.get('ts_utc', '')[:19]
        direction = event.get('data', {}).get('direction', 'UNKNOWN')
        price = event.get('data', {}).get('entry_price', '?')
        print(f"  [{ts}] Direction: {direction} | Price: {price}")

if __name__ == "__main__":
    check_immediate_entry()
