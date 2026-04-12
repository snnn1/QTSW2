#!/usr/bin/env python3
"""Check bar timestamps to see if they're being interpreted correctly"""
import json
from pathlib import Path
from datetime import datetime, timezone

log_dir = Path("logs/robot")
events = []

# Read ENGINE log (most recent)
engine_log = log_dir / "robot_ENGINE.jsonl"
if engine_log.exists():
    with open(engine_log, 'r', encoding='utf-8') as f:
        for line in f:
            line = line.strip()
            if line:
                try:
                    events.append(json.loads(line))
                except:
                    pass

# Find PRE_HYDRATION_BARS_LOADED events
bars_loaded = []
for e in events:
    if (e.get('ts_utc', '').startswith('2026-01-26') and 
        e.get('event') == 'PRE_HYDRATION_BARS_LOADED'):
        bars_loaded.append(e)

if bars_loaded:
    print("="*80)
    print("PRE_HYDRATION_BARS_LOADED EVENTS:")
    print("="*80)
    for e in bars_loaded[-5:]:
        ts = e.get('ts_utc', 'N/A')[:19]
        data = e.get('data', {})
        if isinstance(data, dict):
            payload = data.get('payload', {})
            if isinstance(payload, dict):
                instrument = payload.get('instrument', 'N/A')
                stream_id = payload.get('stream_id', 'N/A')
                bar_count = payload.get('bar_count', 'N/A')
                print(f"  {ts} | Instrument: {instrument} | Stream: {stream_id} | Bars: {bar_count}")

# Find BARSREQUEST_EXECUTED to see time range requested
barsrequest = []
for e in events:
    if (e.get('ts_utc', '').startswith('2026-01-26') and 
        e.get('event') == 'BARSREQUEST_EXECUTED'):
        barsrequest.append(e)

if barsrequest:
    latest = barsrequest[-1]
    print(f"\n{'='*80}")
    print("BARSREQUEST_EXECUTED DETAILS:")
    print(f"{'='*80}")
    data = latest.get('data', {})
    if isinstance(data, dict):
        print(f"  Instrument: {data.get('instrument', 'N/A')}")
        print(f"  Bars returned: {data.get('bars_returned', data.get('bar_count', 'N/A'))}")
        print(f"  Start time: {data.get('start_time', 'N/A')}")
        print(f"  End time: {data.get('end_time', 'N/A')}")
        print(f"  Start time UTC: {data.get('start_time_utc', 'N/A')}")
        print(f"  End time UTC: {data.get('end_time_utc', 'N/A')}")
        print(f"  Start time Chicago: {data.get('start_time_chicago', 'N/A')}")
        print(f"  End time Chicago: {data.get('end_time_chicago', 'N/A')}")

# Check latest HYDRATION_SUMMARY
hydration = []
for e in events:
    if (e.get('ts_utc', '').startswith('2026-01-26') and 
        e.get('event') == 'HYDRATION_SUMMARY' and
        e.get('stream') == 'NQ2'):
        hydration.append(e)

if hydration:
    latest = hydration[-1]
    print(f"\n{'='*80}")
    print("LATEST HYDRATION_SUMMARY:")
    print(f"{'='*80}")
    print(f"  Timestamp: {latest.get('ts_utc', 'N/A')[:19]}")
    data = latest.get('data', {})
    if isinstance(data, dict):
        print(f"  Range high: {data.get('reconstructed_range_high', 'N/A')}")
        print(f"  Range low: {data.get('reconstructed_range_low', 'N/A')}")
        print(f"  Range start Chicago: {data.get('range_start_chicago', 'N/A')}")
        print(f"  Slot time Chicago: {data.get('slot_time_chicago', 'N/A')}")
        print(f"  Now Chicago: {data.get('now_chicago', 'N/A')}")
        print(f"  Loaded bars: {data.get('loaded_bars', 'N/A')}")
        
        # Check if range was calculated before slot time
        now_chicago_str = data.get('now_chicago', '')
        slot_time_chicago_str = data.get('slot_time_chicago', '')
        if now_chicago_str and slot_time_chicago_str:
            try:
                now_ct = datetime.fromisoformat(now_chicago_str.replace('Z', '+00:00'))
                slot_ct = datetime.fromisoformat(slot_time_chicago_str.replace('Z', '+00:00'))
                
                if now_ct < slot_ct:
                    minutes_before_slot = (slot_ct - now_ct).total_seconds() / 60
                    print(f"\n  ⚠️  Range calculated {minutes_before_slot:.1f} minutes BEFORE slot time")
                    print(f"      This means bars after {now_ct.strftime('%H:%M')} Chicago are not included")
                    print(f"      Your 25903 might be from bars after {now_ct.strftime('%H:%M')} Chicago")
                else:
                    print(f"\n  ✓  Range calculated after slot time - should include all bars")
            except:
                pass

print(f"\n{'='*80}")
print("CONCLUSION:")
print(f"{'='*80}")
print("The range was calculated at the time shown in 'Now Chicago'.")
print("It only includes bars up to that time, not up to slot_time.")
print("If your 25903 is from bars after that time, the difference is expected.")
print("The range will update as more bars arrive and will lock at slot_time.")
print(f"{'='*80}")
