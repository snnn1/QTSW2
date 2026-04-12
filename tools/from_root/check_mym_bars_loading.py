#!/usr/bin/env python3
"""Check why MYM bars aren't being loaded"""
import json
from pathlib import Path

log_dir = Path("logs/robot")
events = []

# Read all robot log files
for log_file in log_dir.glob("robot_*.jsonl"):
    try:
        with open(log_file, 'r', encoding='utf-8') as f:
            for line in f:
                line = line.strip()
                if line:
                    try:
                        events.append(json.loads(line))
                    except:
                        pass
    except Exception as e:
        pass

print("="*80)
print("MYM BARS LOADING ANALYSIS:")
print("="*80)

# Check all events related to MYM bars loading
mym_related = []
for e in events:
    if e.get('ts_utc', '').startswith('2026-01-26'):
        data = e.get('data', {})
        if isinstance(data, dict):
            instrument = data.get('instrument', '')
            if instrument == 'MYM':
                mym_related.append(e)

# Sort by timestamp
mym_related.sort(key=lambda x: x.get('ts_utc', ''))

print(f"\n  All MYM-related events ({len(mym_related)}):")
for e in mym_related[-20:]:  # Show last 20
    event_type = e.get('event', '')
    ts = e.get('ts_utc', '')[:19]
    data = e.get('data', {})
    
    if event_type == 'BARSREQUEST_EXECUTED':
        print(f"    {ts} - {event_type}: {data.get('bars_returned', 'N/A')} bars")
    elif event_type == 'BARSREQUEST_FILTER_SUMMARY':
        print(f"    {ts} - {event_type}:")
        print(f"      Raw: {data.get('raw_bar_count', 'N/A')}, Accepted: {data.get('accepted_bar_count', 'N/A')}")
        print(f"      Filtered future: {data.get('filtered_future_count', 'N/A')}")
        print(f"      Filtered partial: {data.get('filtered_partial_count', 'N/A')}")
    elif event_type == 'PRE_HYDRATION_BARS_LOADED':
        print(f"    {ts} - {event_type}:")
        print(f"      Bars loaded: {data.get('bar_count', 'N/A')}")
        print(f"      Streams fed: {data.get('streams_fed', 'N/A')}")
    elif event_type in ['BARSREQUEST_SKIPPED', 'BARSREQUEST_FAILED']:
        print(f"    {ts} - {event_type}: {data.get('reason', 'N/A')}")
    else:
        print(f"    {ts} - {event_type}")

# Check if LoadPreHydrationBars was called but filtered all bars
filter_summary = [e for e in mym_related if e.get('event') == 'BARSREQUEST_FILTER_SUMMARY']
if filter_summary:
    latest = filter_summary[-1]
    data = latest.get('data', {})
    print(f"\n  Latest BARSREQUEST_FILTER_SUMMARY:")
    print(f"    Raw bars: {data.get('raw_bar_count', 'N/A')}")
    print(f"    Accepted bars: {data.get('accepted_bar_count', 'N/A')}")
    print(f"    Filtered future: {data.get('filtered_future_count', 'N/A')}")
    print(f"    Filtered partial: {data.get('filtered_partial_count', 'N/A')}")

print(f"\n{'='*80}")
