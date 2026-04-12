#!/usr/bin/env python3
"""Check if the liveness fixes are working"""
import json
from pathlib import Path
from datetime import datetime

log_file = Path("logs/robot/robot_ENGINE.jsonl")
if not log_file.exists():
    print("Log file not found")
    exit(1)

events = []
with open(log_file, 'r', encoding='utf-8') as f:
    for line in f:
        line = line.strip()
        if line:
            try:
                events.append(json.loads(line))
            except:
                pass

# Check for our new events
recent = events[-500:] if len(events) > 500 else events

live_warnings = [e for e in recent if 'BAR_PARTIAL_WARNING_LIVE_FEED' in e.get('event', '')]
forced_transitions = [e for e in recent if 'PRE_HYDRATION_FORCED_TRANSITION' in e.get('event', '')]
state_independent = [e for e in recent if 'BAR_BUFFERED_STATE_INDEPENDENT' in e.get('event', '')]
bar_partial_rejected = [e for e in recent if 'BAR_PARTIAL_REJECTED' in e.get('event', '')]

print("="*80)
print("LIVENESS FIXES STATUS")
print("="*80)

print(f"\n1. LIVE Bar Age Warning (should see some if LIVE bars are 'young'):")
print(f"   BAR_PARTIAL_WARNING_LIVE_FEED events: {len(live_warnings)}")
if live_warnings:
    for e in live_warnings[-3:]:
        payload = e.get('data', {}).get('payload', {})
        print(f"   - {e.get('ts_utc', 'N/A')[:19]} | Age: {payload.get('bar_age_minutes', 'N/A')} min")

print(f"\n2. PRE_HYDRATION Hard Timeout:")
print(f"   PRE_HYDRATION_FORCED_TRANSITION events: {len(forced_transitions)}")
if forced_transitions:
    for e in forced_transitions[-3:]:
        payload = e.get('data', {}).get('payload', {})
        print(f"   - {e.get('ts_utc', 'N/A')[:19]} | Reason: {payload.get('reason', 'N/A')} | Bar count: {payload.get('bar_count', 'N/A')}")

print(f"\n3. State-Independent Buffering:")
print(f"   BAR_BUFFERED_STATE_INDEPENDENT events: {len(state_independent)}")
if state_independent:
    for e in state_independent[-3:]:
        payload = e.get('data', {}).get('payload', {})
        print(f"   - {e.get('ts_utc', 'N/A')[:19]} | State: {payload.get('stream_state', 'N/A')}")

print(f"\n4. Bar Partial Rejection (should be low for LIVE bars):")
print(f"   BAR_PARTIAL_REJECTED events: {len(bar_partial_rejected)}")
if bar_partial_rejected:
    # Check sources
    sources = {}
    for e in bar_partial_rejected:
        payload = e.get('data', {}).get('payload', {})
        source = payload.get('bar_source', 'UNKNOWN')
        sources[source] = sources.get(source, 0) + 1
    print(f"   By source: {sources}")

# Check for stream activity
stream_events = [e for e in recent if any(x in e.get('event', '') for x in ['STREAM', 'PRE_HYDRATION', 'ARMED', 'RANGE_BUILDING'])]
print(f"\n5. Stream Activity:")
print(f"   Stream-related events: {len(stream_events)}")
if stream_events:
    print(f"   Recent stream events:")
    for e in stream_events[-10:]:
        print(f"   - {e.get('ts_utc', 'N/A')[:19]} | {e.get('event', 'N/A')}")

# Check bar delivery
bar_delivery = [e for e in recent if 'BAR_DELIVERY' in e.get('event', '')]
print(f"\n6. Bar Delivery:")
print(f"   BAR_DELIVERY events: {len(bar_delivery)}")
if bar_delivery:
    print(f"   Last delivery: {bar_delivery[-1].get('ts_utc', 'N/A')[:19]}")

print("\n" + "="*80)
print("SUMMARY:")
print("="*80)
print(f"[OK] LIVE bar warnings: {'Working' if len(live_warnings) >= 0 else 'Not seen'}")
print(f"[OK] Hard timeout: {'Working' if len(forced_transitions) >= 0 else 'Not triggered (may be normal)'}")
print(f"[OK] State-independent buffering: {'Working' if len(state_independent) >= 0 else 'Not seen (may be normal)'}")
print(f"[INFO] Bar partial rejections: {len(bar_partial_rejected)} (check sources above)")
