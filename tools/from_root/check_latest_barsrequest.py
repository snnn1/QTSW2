#!/usr/bin/env python3
"""Check latest BarsRequest events to see if code changes took effect"""
import json
from pathlib import Path
from datetime import datetime

log_dir = Path("logs/robot")
events = []

for log_file in log_dir.glob("robot_*.jsonl"):
    try:
        with open(log_file, 'r', encoding='utf-8') as f:
            for line in f:
                if line.strip():
                    try:
                        events.append(json.loads(line))
                    except:
                        pass
    except:
        pass

# Get latest events (last hour)
now = datetime.now()
recent_events = []
for e in events:
    ts_str = e.get('ts_utc', '')
    if ts_str:
        try:
            ts = datetime.fromisoformat(ts_str.replace('Z', '+00:00').replace('+00:00', ''))
            if (now - ts.replace(tzinfo=None)).total_seconds() < 3600:  # Last hour
                recent_events.append(e)
        except:
            pass

print("="*80)
print("RECENT BARSREQUEST EVENTS (Last Hour):")
print("="*80)

barsrequest_recent = [e for e in recent_events if 'BARSREQUEST' in e.get('event', '')]
barsrequest_recent.sort(key=lambda x: x.get('ts_utc', ''), reverse=True)

for e in barsrequest_recent[:20]:  # Show latest 20
    event_type = e.get('event', '')
    ts = e.get('ts_utc', '')[:19]
    data = e.get('data', {})
    instrument = data.get('instrument', 'N/A')
    
    print(f"\n  {ts} - {event_type} ({instrument}):")
    if event_type == 'BARSREQUEST_EXECUTED':
        print(f"    Bars returned: {data.get('bars_returned', 'N/A')}")
    elif event_type == 'BARSREQUEST_FILTER_SUMMARY':
        print(f"    Raw: {data.get('raw_bar_count', 'N/A')}, Accepted: {data.get('accepted_bar_count', 'N/A')}")
    elif event_type == 'PRE_HYDRATION_BARS_LOADED':
        print(f"    Bars loaded: {data.get('bar_count', 'N/A')}, Streams fed: {data.get('streams_fed', 'N/A')}")
    elif event_type in ['BARSREQUEST_SKIPPED', 'BARSREQUEST_FAILED']:
        print(f"    Reason: {data.get('reason', 'N/A')}")

# Check for MYM specifically in recent events
print(f"\n{'='*80}")
print("RECENT MYM EVENTS:")
print(f"{'='*80}")

mym_recent = [e for e in recent_events 
             if e.get('data', {}).get('instrument') == 'MYM' and 
             'BARSREQUEST' in e.get('event', '')]
mym_recent.sort(key=lambda x: x.get('ts_utc', ''), reverse=True)

if mym_recent:
    for e in mym_recent[:10]:
        print(f"  {e.get('ts_utc', '')[:19]} - {e.get('event')}")
else:
    print("  No recent MYM BarsRequest events")

print(f"\n{'='*80}")
