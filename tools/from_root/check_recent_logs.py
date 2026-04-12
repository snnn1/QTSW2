#!/usr/bin/env python3
"""Quick check of recent robot logs"""
import json
import sys

log_file = "logs/robot/robot_ENGINE.jsonl"

with open(log_file, 'r', encoding='utf-8-sig') as f:
    events = [json.loads(l) for l in f if l.strip()]

print("=" * 80)
print("RECENT LOG ANALYSIS")
print("=" * 80)
print(f"Total events: {len(events)}")
print()

# Check STREAMS_CREATION_ATTEMPT
creation_attempts = [e for e in events if e.get('event_type') == 'STREAMS_CREATION_ATTEMPT' or e.get('event') == 'STREAMS_CREATION_ATTEMPT']
print(f"[OK] STREAMS_CREATION_ATTEMPT: {len(creation_attempts)} events")
if creation_attempts:
    print("\nLast 3 STREAMS_CREATION_ATTEMPT events:")
    for e in creation_attempts[-3:]:
        payload = e.get('payload') or e.get('data', {}).get('payload', {})
        ts = e.get('ts') or e.get('ts_utc', 'N/A')
        if isinstance(payload, str):
            print(f"  [{ts}]")
            print(f"    Payload: {payload}")
        else:
            print(f"  [{ts}]")
            print(f"    Trading Date: {payload.get('trading_date', 'N/A')}")
            print(f"    Streams Count: {payload.get('streams_count', 'N/A')}")
            print(f"    Spec is null: {payload.get('spec_is_null', 'N/A')}")
            print(f"    Time is null: {payload.get('time_is_null', 'N/A')}")
            print(f"    Last timetable is null: {payload.get('last_timetable_is_null', 'N/A')}")
print()

# Check TIMETABLE_PARSING_COMPLETE
parsing_complete = [e for e in events if e.get('event_type') == 'TIMETABLE_PARSING_COMPLETE' or e.get('event') == 'TIMETABLE_PARSING_COMPLETE']
print(f"[OK] TIMETABLE_PARSING_COMPLETE: {len(parsing_complete)} events")
if parsing_complete:
    print("\nLast 3 TIMETABLE_PARSING_COMPLETE events:")
    for e in parsing_complete[-3:]:
        payload = e.get('payload') or e.get('data', {}).get('payload', {})
        ts = e.get('ts') or e.get('ts_utc', 'N/A')
        if isinstance(payload, str):
            print(f"  [{ts}] Payload: {payload[:300]}")
        else:
            print(f"  [{ts}]")
            print(f"    Accepted: {payload.get('accepted', 'N/A')}")
            print(f"    Skipped: {payload.get('skipped', 'N/A')}")
            skipped_reasons = payload.get('skipped_reasons', {})
            if skipped_reasons:
                print(f"    Skipped Reasons: {skipped_reasons}")
print()

# Check STREAM_SKIPPED with CANONICAL_MISMATCH
stream_skipped = [e for e in events if e.get('event_type') == 'STREAM_SKIPPED' or e.get('event') == 'STREAM_SKIPPED']
canonical_mismatches = [e for e in stream_skipped if 'CANONICAL_MISMATCH' in str(e.get('payload', {})) or 'CANONICAL_MISMATCH' in str(e.get('data', {}).get('payload', {}))]
print(f"[WARN] STREAM_SKIPPED with CANONICAL_MISMATCH: {len(canonical_mismatches)} events")
if canonical_mismatches:
    print("\nLast 5 CANONICAL_MISMATCH events:")
    for e in canonical_mismatches[-5:]:
        payload = e.get('payload') or e.get('data', {}).get('payload', {})
        ts = e.get('ts') or e.get('ts_utc', 'N/A')
        if isinstance(payload, str):
            print(f"  [{ts}] Payload: {payload[:300]}")
        else:
            print(f"  [{ts}]")
            print(f"    Reason: {payload.get('reason', 'N/A')}")
            print(f"    Timetable Canonical: {payload.get('timetable_canonical', 'N/A')}")
            print(f"    NinjaTrader Master Instrument: {payload.get('ninjatrader_master_instrument', 'N/A')}")
            print(f"    NinjaTrader Execution Instrument: {payload.get('ninjatrader_execution_instrument', 'N/A')}")
print()

# Check STREAM_CREATED
stream_created = [e for e in events if e.get('event_type') == 'STREAM_CREATED' or e.get('event') == 'STREAM_CREATED']
print(f"[{'OK' if stream_created else 'MISSING'}] STREAM_CREATED: {len(stream_created)} events")
if stream_created:
    print("\nLast 5 STREAM_CREATED events:")
    for e in stream_created[-5:]:
        print(f"  [{e.get('ts', 'N/A')}] Stream: {e.get('stream', 'N/A')}, Instrument: {e.get('instrument', 'N/A')}")
print()

# Check STREAMS_CREATED
streams_created = [e for e in events if e.get('event_type') == 'STREAMS_CREATED' or e.get('event') == 'STREAMS_CREATED']
print(f"[{'OK' if streams_created else 'MISSING'}] STREAMS_CREATED: {len(streams_created)} events")
if streams_created:
    print("\nLast STREAMS_CREATED event:")
    e = streams_created[-1]
    payload = e.get('payload') or e.get('data', {}).get('payload', {})
    ts = e.get('ts') or e.get('ts_utc', 'N/A')
    if isinstance(payload, str):
        print(f"  [{ts}] Payload: {payload[:500]}")
    else:
        print(f"  [{ts}]")
        print(f"    Stream Count: {payload.get('stream_count', 'N/A')}")
        streams = payload.get('streams', [])
        if streams:
            print(f"    Streams: {len(streams)} streams created")
            for s in streams[:5]:
                print(f"      - {s.get('stream_id', 'N/A')} ({s.get('instrument', 'N/A')})")
print()

# Check BARSREQUEST events
barsrequest_requested = [e for e in events if e.get('event_type') == 'BARSREQUEST_REQUESTED' or e.get('event') == 'BARSREQUEST_REQUESTED']
barsrequest_executed = [e for e in events if e.get('event_type') == 'BARSREQUEST_EXECUTED' or e.get('event') == 'BARSREQUEST_EXECUTED']
barsrequest_skipped = [e for e in events if e.get('event_type') == 'BARSREQUEST_SKIPPED' or e.get('event') == 'BARSREQUEST_SKIPPED']

print(f"[{'OK' if barsrequest_requested else 'MISSING'}] BARSREQUEST_REQUESTED: {len(barsrequest_requested)} events")
print(f"[{'OK' if barsrequest_executed else 'MISSING'}] BARSREQUEST_EXECUTED: {len(barsrequest_executed)} events")
print(f"[{'OK' if barsrequest_skipped else 'MISSING'}] BARSREQUEST_SKIPPED: {len(barsrequest_skipped)} events")

if barsrequest_executed:
    print("\nLast BARSREQUEST_EXECUTED events:")
    for e in barsrequest_executed[-3:]:
        payload = e.get('payload') or e.get('data', {}).get('payload', {})
        ts = e.get('ts') or e.get('ts_utc', 'N/A')
        if isinstance(payload, str):
            print(f"  [{ts}] Payload: {payload[:300]}")
        else:
            print(f"  [{ts}] Instrument: {payload.get('instrument', 'N/A')}, Bars: {payload.get('bars_returned', 'N/A')}")

if barsrequest_skipped:
    print("\nLast BARSREQUEST_SKIPPED events:")
    for e in barsrequest_skipped[-3:]:
        payload = e.get('payload') or e.get('data', {}).get('payload', {})
        ts = e.get('ts') or e.get('ts_utc', 'N/A')
        if isinstance(payload, str):
            print(f"  [{ts}] Payload: {payload[:300]}")
        else:
            print(f"  [{ts}] Reason: {payload.get('reason', 'N/A')}, Note: {payload.get('note', 'N/A')}")

print()
print("=" * 80)
