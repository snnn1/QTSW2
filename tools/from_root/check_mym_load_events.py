#!/usr/bin/env python3
import json
from pathlib import Path

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

mym_events = []
for e in events:
    if e.get('ts_utc', '').startswith('2026-01-26'):
        data = e.get('data', {})
        if isinstance(data, dict):
            instrument = data.get('instrument', '')
            if instrument == 'MYM':
                event_type = e.get('event', '')
                if event_type in ['BARSREQUEST_FILTER_SUMMARY', 'PRE_HYDRATION_BARS_LOADED', 'BARSREQUEST_EXECUTED', 'PRE_HYDRATION_BARS_SKIPPED_STREAM_STATE']:
                    mym_events.append(e)

mym_events.sort(key=lambda x: x.get('ts_utc', ''))

print("MYM Bars Loading Events:")
for e in mym_events:
    print(f"{e.get('ts_utc', '')[:19]} - {e.get('event')}")
    data = e.get('data', {})
    if e.get('event') == 'BARSREQUEST_FILTER_SUMMARY':
        print(f"  Raw: {data.get('raw_bar_count')}, Accepted: {data.get('accepted_bar_count')}")
    elif e.get('event') == 'PRE_HYDRATION_BARS_LOADED':
        print(f"  Bars: {data.get('bar_count')}, Streams fed: {data.get('streams_fed')}")
    elif e.get('event') == 'PRE_HYDRATION_BARS_SKIPPED_STREAM_STATE':
        print(f"  Stream: {data.get('stream_id')}, State: {data.get('stream_state')}")
