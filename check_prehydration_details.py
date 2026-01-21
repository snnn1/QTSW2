#!/usr/bin/env python3
"""Check PRE_HYDRATION stream details"""
import json
from pathlib import Path
from datetime import datetime
import pytz

chicago_tz = pytz.timezone('America/Chicago')
now_chicago = datetime.now(chicago_tz)

print("="*80)
print("PRE_HYDRATION STREAM DETAILS")
print("="*80)
print(f"Current Chicago time: {now_chicago.strftime('%Y-%m-%d %H:%M:%S %Z')}\n")

journal_dir = Path("logs/robot/journal")
if journal_dir.exists():
    journals = list(journal_dir.glob("2026-01-21_*.json"))
    
    for j in sorted(journals):
        try:
            data = json.loads(j.read_text())
            stream = data.get('Stream', 'N/A')
            state = data.get('LastState', 'N/A')
            last_update_str = data.get('LastUpdateUtc', '')
            trading_date = data.get('TradingDate', 'N/A')
            
            if last_update_str:
                try:
                    if last_update_str.endswith('Z'):
                        last_update_str = last_update_str[:-1] + '+00:00'
                    dt_utc = datetime.fromisoformat(last_update_str)
                    if dt_utc.tzinfo is None:
                        dt_utc = pytz.UTC.localize(dt_utc)
                    dt_chicago = dt_utc.astimezone(chicago_tz)
                    time_since_update = (now_chicago - dt_chicago).total_seconds() / 60
                    
                    print(f"{stream}:")
                    print(f"  State: {state}")
                    print(f"  Trading Date: {trading_date}")
                    print(f"  Last Update: {dt_chicago.strftime('%Y-%m-%d %H:%M:%S %Z')}")
                    print(f"  Time since update: {time_since_update:.1f} minutes")
                    if state == 'PRE_HYDRATION' and time_since_update > 1:
                        print(f"  [WARN] Stuck in PRE_HYDRATION for {time_since_update:.1f} minutes")
                    print()
                except Exception as e:
                    print(f"{stream}: Error parsing time - {e}\n")
        except Exception as e:
            print(f"Error reading {j.name}: {e}\n")

# Check log file for PRE_HYDRATION-related events
log_file = Path("logs/robot/robot_ENGINE.jsonl")
if log_file.exists():
    events = []
    with open(log_file, 'r', encoding='utf-8') as f:
        for line in f:
            line = line.strip()
            if line:
                try:
                    events.append(json.loads(line))
                except:
                    pass
    
    recent = events[-1000:] if len(events) > 1000 else events
    
    # Look for any PRE_HYDRATION events
    pre_hydration_events = [e for e in recent if 'PRE_HYDRATION' in e.get('event', '')]
    
    print("="*80)
    print("PRE_HYDRATION EVENTS IN LOGS")
    print("="*80)
    print(f"Total PRE_HYDRATION events: {len(pre_hydration_events)}")
    
    if pre_hydration_events:
        print("\nRecent PRE_HYDRATION events:")
        for e in pre_hydration_events[-10:]:
            ts = e.get('ts_utc', 'N/A')[:19]
            event = e.get('event', 'N/A')
            payload = e.get('data', {}).get('payload', {})
            stream = payload.get('stream', payload.get('stream_id', 'N/A'))
            print(f"  {ts} | {event} | Stream: {stream}")
    else:
        print("\n[WARN] No PRE_HYDRATION events found in logs!")
        print("  This suggests Tick() may not be running for PRE_HYDRATION streams")
        print("  OR streams haven't reached PRE_HYDRATION state yet")

print("\n" + "="*80)
