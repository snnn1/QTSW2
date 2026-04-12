#!/usr/bin/env python3
"""Check if streams are active and receiving ticks"""
import json
from pathlib import Path

log_file = Path("logs/robot/robot_ENGINE.jsonl")
if not log_file.exists():
    print("Log file not found")
    exit(1)

events = []
with open(log_file, 'r', encoding='utf-8-sig') as f:
    for line in f:
        line = line.strip()
        if line:
            try:
                events.append(json.loads(line))
            except:
                pass

recent = events[-1000:] if len(events) > 1000 else events

print("="*80)
print("STREAM ACTIVITY CHECK")
print("="*80)

# Check BAR_DELIVERY events (indicates streams are receiving bars)
bar_delivery = [e for e in recent if 'BAR_DELIVERY_TO_STREAM' in e.get('event', '')]
print(f"\nBAR_DELIVERY_TO_STREAM events: {len(bar_delivery)}")

if bar_delivery:
    print("\nRecent bar deliveries (streams are active):")
    streams_receiving = {}
    for e in bar_delivery[-20:]:
        payload = e.get('data', {}).get('payload', {})
        stream = payload.get('stream', 'N/A')
        instrument = payload.get('instrument', 'N/A')
        ts = e.get('ts_utc', 'N/A')[:19]
        if stream not in streams_receiving:
            streams_receiving[stream] = []
        streams_receiving[stream].append(ts)
    
    for stream, timestamps in sorted(streams_receiving.items()):
        print(f"  {stream}: {len(timestamps)} deliveries (latest: {timestamps[-1]})")

# Check for any events with stream_id in payload
stream_events = []
for e in recent:
    payload = e.get('data', {}).get('payload', {})
    if 'stream' in payload or 'stream_id' in payload:
        stream_events.append(e)

print(f"\nTotal events with stream info: {len(stream_events)}")

# Check if we see any TICK_TRACE or diagnostic events at all
all_diagnostic = [e for e in events if any(x in e.get('event', '') for x in ['TICK_TRACE', 'PRE_HYDRATION_HANDLER_TRACE', 'RANGE_START_INITIALIZED', 'PRE_HYDRATION_RANGE_START_DIAGNOSTIC'])]
print(f"\nTotal diagnostic events in entire log: {len(all_diagnostic)}")

# Check journal update times vs log timestamps
print(f"\n=== JOURNAL VS LOG TIMING ===")
journal_dir = Path("logs/robot/journal")
if journal_dir.exists():
    journals = list(journal_dir.glob("2026-01-21_*.json"))
    for j in sorted(journals)[:5]:
        try:
            journal_data = json.loads(j.read_text())
            stream = journal_data.get('Stream', 'N/A')
            last_update = journal_data.get('LastUpdateUtc', '')
            state = journal_data.get('LastState', 'N/A')
            
            # Find most recent log event for this stream
            stream_logs = [e for e in recent if e.get('data', {}).get('payload', {}).get('stream', '') == stream or 
                          e.get('data', {}).get('payload', {}).get('stream_id', '') == stream]
            
            print(f"  {stream}: State={state}, Journal updated={last_update[:19] if last_update else 'N/A'}, Log events={len(stream_logs)}")
        except:
            pass

print("\n" + "="*80)
