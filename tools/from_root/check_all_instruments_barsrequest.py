#!/usr/bin/env python3
"""Check if bars are being requested for all execution instruments"""
import json
from pathlib import Path
from collections import defaultdict

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
print("BARSREQUEST STATUS FOR ALL INSTRUMENTS:")
print("="*80)

# Get all BarsRequest events
barsrequest_events = defaultdict(lambda: {'requested': 0, 'executed': 0, 'skipped': 0, 'failed': 0})

for e in events:
    if e.get('ts_utc', '').startswith('2026-01-26'):
        event_type = e.get('event', '')
        if 'BARSREQUEST' in event_type:
            data = e.get('data', {})
            if isinstance(data, dict):
                instrument = data.get('instrument', '')
                if instrument:
                    if event_type == 'BARSREQUEST_REQUESTED':
                        barsrequest_events[instrument]['requested'] += 1
                    elif event_type == 'BARSREQUEST_EXECUTED':
                        barsrequest_events[instrument]['executed'] += 1
                    elif event_type == 'BARSREQUEST_SKIPPED':
                        barsrequest_events[instrument]['skipped'] += 1
                    elif event_type == 'BARSREQUEST_FAILED':
                        barsrequest_events[instrument]['failed'] += 1

print(f"\n  Instruments with BarsRequest activity:")
for instrument in sorted(barsrequest_events.keys()):
    stats = barsrequest_events[instrument]
    total = sum(stats.values())
    if total > 0:
        print(f"    {instrument}:")
        print(f"      Requested: {stats['requested']}")
        print(f"      Executed: {stats['executed']}")
        print(f"      Skipped: {stats['skipped']}")
        print(f"      Failed: {stats['failed']}")

# Check bars loaded for each instrument
print(f"\n{'='*80}")
print("BARS LOADED STATUS:")
print(f"{'='*80}")

bars_loaded = defaultdict(int)
for e in events:
    if e.get('ts_utc', '').startswith('2026-01-26'):
        if e.get('event') == 'PRE_HYDRATION_BARS_LOADED':
            data = e.get('data', {})
            if isinstance(data, dict):
                instrument = data.get('instrument', '')
                bar_count = data.get('bar_count', 0)
                if instrument:
                    bars_loaded[instrument] += bar_count

print(f"\n  Bars loaded per instrument:")
for instrument in sorted(bars_loaded.keys()):
    print(f"    {instrument}: {bars_loaded[instrument]} bars")

# Check stream status
print(f"\n{'='*80}")
print("STREAM STATUS (Bars Committed):")
print(f"{'='*80}")

stream_bars = defaultdict(int)
for e in events:
    if e.get('ts_utc', '').startswith('2026-01-26'):
        if e.get('event') == 'BAR_BUFFER_ADD_COMMITTED':
            stream = e.get('stream', '')
            if stream:
                stream_bars[stream] += 1

print(f"\n  Bars committed per stream:")
for stream in sorted(stream_bars.keys()):
    print(f"    {stream}: {stream_bars[stream]} bars")

# Check ranges computed
print(f"\n{'='*80}")
print("RANGES COMPUTED:")
print(f"{'='*80}")

ranges_computed = {}
for e in events:
    if e.get('ts_utc', '').startswith('2026-01-26'):
        if e.get('event') == 'RANGE_INITIALIZED_FROM_HISTORY':
            stream = e.get('stream', '')
            data = e.get('data', {})
            if stream and isinstance(data, dict):
                range_high = data.get('range_high')
                range_low = data.get('range_low')
                if range_high is not None and range_low is not None:
                    ranges_computed[stream] = {
                        'high': range_high,
                        'low': range_low,
                        'bars_used': data.get('bars_used', 'N/A')
                    }

print(f"\n  Streams with computed ranges:")
if ranges_computed:
    for stream in sorted(ranges_computed.keys()):
        r = ranges_computed[stream]
        print(f"    {stream}: High={r['high']}, Low={r['low']}, Bars={r['bars_used']}")
else:
    print(f"    No ranges computed yet")

print(f"\n{'='*80}")
