#!/usr/bin/env python3
"""Check for PRE_HYDRATION watchdog and timeout events"""
import json
from pathlib import Path

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

recent = events[-1000:] if len(events) > 1000 else events

watchdog = [e for e in recent if 'PRE_HYDRATION_WATCHDOG' in e.get('event', '')]
timeout_skipped = [e for e in recent if 'PRE_HYDRATION_TIMEOUT_SKIPPED' in e.get('event', '')]
forced_transition = [e for e in recent if 'PRE_HYDRATION_FORCED_TRANSITION' in e.get('event', '')]
condition_check = [e for e in recent if 'PRE_HYDRATION_CONDITION_CHECK' in e.get('event', '')]

print("="*80)
print("PRE_HYDRATION TIMEOUT STATUS")
print("="*80)
print(f"PRE_HYDRATION_WATCHDOG_STUCK events: {len(watchdog)}")
print(f"PRE_HYDRATION_TIMEOUT_SKIPPED events: {len(timeout_skipped)}")
print(f"PRE_HYDRATION_FORCED_TRANSITION events: {len(forced_transition)}")
print(f"PRE_HYDRATION_CONDITION_CHECK events: {len(condition_check)}")

if watchdog:
    print(f"\n=== WATCHDOG EVENTS (streams stuck >1 min) ===")
    for e in watchdog[-5:]:
        payload = e.get('data', {}).get('payload', {})
        ts = e.get('ts_utc', 'N/A')[:19]
        stream = payload.get('stream_id', 'N/A')
        minutes_past = payload.get('minutes_past_range_start', 'N/A')
        bar_count = payload.get('bar_count', 'N/A')
        print(f"  {ts} | Stream: {stream} | {minutes_past} min past range start | Bars: {bar_count}")

if timeout_skipped:
    print(f"\n=== TIMEOUT SKIPPED (why timeout didn't trigger) ===")
    for e in timeout_skipped[-5:]:
        payload = e.get('data', {}).get('payload', {})
        ts = e.get('ts_utc', 'N/A')[:19]
        reason = payload.get('reason', 'N/A')
        stream = payload.get('stream_id', 'N/A')
        print(f"  {ts} | Stream: {stream} | Reason: {reason}")
        if reason == 'RANGE_START_INVALID':
            print(f"    -> RangeStartChicagoTime is invalid (default/zero/uninitialized)")
        elif reason == 'RANGE_START_DATE_MISMATCH':
            print(f"    -> RangeStartChicagoTime date doesn't match trading date")

if forced_transition:
    print(f"\n=== FORCED TRANSITIONS (hard timeout triggered) ===")
    for e in forced_transition[-5:]:
        payload = e.get('data', {}).get('payload', {})
        ts = e.get('ts_utc', 'N/A')[:19]
        stream = payload.get('stream_id', 'N/A')
        bar_count = payload.get('bar_count', 'N/A')
        minutes_past = payload.get('minutes_past_range_start', 'N/A')
        print(f"  {ts} | Stream: {stream} | Bars: {bar_count} | {minutes_past} min past range start")

if condition_check:
    print(f"\n=== CONDITION CHECKS (diagnostic) ===")
    for e in condition_check[-3:]:
        payload = e.get('data', {}).get('payload', {})
        ts = e.get('ts_utc', 'N/A')[:19]
        condition_met = payload.get('condition_met', False)
        bar_count = payload.get('bar_count', 'N/A')
        print(f"  {ts} | Condition met: {condition_met} | Bars: {bar_count}")

print("\n" + "="*80)
print("ANALYSIS:")
print("="*80)
if len(watchdog) == 0 and len(timeout_skipped) == 0 and len(forced_transition) == 0:
    print("[WARN] No PRE_HYDRATION timeout events found!")
    print("  -> This suggests Tick() may not be running for PRE_HYDRATION streams")
    print("  -> OR RangeStartChicagoTime is not initialized")
    print("  -> OR streams are not in PRE_HYDRATION state in Tick()")
elif len(timeout_skipped) > 0:
    print(f"[INFO] Timeout skipped {len(timeout_skipped)} times - check reasons above")
elif len(forced_transition) > 0:
    print(f"[OK] Hard timeout is working - {len(forced_transition)} forced transitions")
else:
    print("[INFO] Watchdog events found but no forced transitions")

print("\n" + "="*80)
