#!/usr/bin/env python3
"""Comprehensive log analysis - check for anything important"""
import json
from pathlib import Path
from datetime import datetime
import pytz
from collections import defaultdict

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

if not events:
    print("No events found")
    exit(1)

# Get recent events (last hour worth)
recent = events[-1000:] if len(events) > 1000 else events

chicago_tz = pytz.timezone('America/Chicago')
now_chicago = datetime.now(chicago_tz)

print("="*80)
print("COMPREHENSIVE LOG ANALYSIS")
print("="*80)

# 1. Check for errors and warnings
errors = [e for e in recent if any(x in e.get('event', '') for x in ['ERROR', 'FAILED', 'INVALID', 'STALLED', 'STUCK'])]
warnings = [e for e in recent if 'WARNING' in e.get('event', '') or 'WARN' in e.get('event', '')]

print(f"\n1. ERRORS & WARNINGS:")
print(f"   Errors/Issues: {len(errors)}")
print(f"   Warnings: {len(warnings)}")

if errors:
    print(f"\n   Recent errors/issues:")
    error_types = defaultdict(int)
    for e in errors[-10:]:
        event_type = e.get('event', 'UNKNOWN')
        error_types[event_type] += 1
        ts = e.get('ts_utc', 'N/A')[:19]
        print(f"   - {ts} | {event_type}")
    print(f"\n   Error summary: {dict(error_types)}")

if warnings:
    print(f"\n   Recent warnings:")
    for e in warnings[-5:]:
        ts = e.get('ts_utc', 'N/A')[:19]
        print(f"   - {ts} | {e.get('event', 'N/A')}")

# 2. Check trading date status
trading_date_locked = [e for e in recent if 'TRADING_DATE_LOCKED' in e.get('event', '')]
engine_start = [e for e in recent if 'ENGINE_START' in e.get('event', '')]
timetable_validated = [e for e in recent if 'TIMETABLE_VALIDATED' in e.get('event', '')]

print(f"\n2. ENGINE STATUS:")
print(f"   ENGINE_START events: {len(engine_start)}")
print(f"   TRADING_DATE_LOCKED events: {len(trading_date_locked)}")
print(f"   TIMETABLE_VALIDATED events: {len(timetable_validated)}")

if trading_date_locked:
    latest = trading_date_locked[-1]
    payload = latest.get('data', {}).get('payload', {})
    print(f"   Latest trading date: {payload.get('trading_date', 'N/A')}")
    print(f"   Source: {payload.get('source', 'N/A')}")

# 3. Check bar processing
bar_delivery = [e for e in recent if 'BAR_DELIVERY' in e.get('event', '')]
bar_mismatch = [e for e in recent if 'BAR_DATE_MISMATCH' in e.get('event', '')]
bar_partial = [e for e in recent if 'BAR_PARTIAL' in e.get('event', '')]
bar_accepted = [e for e in recent if 'BAR_ACCEPTED' in e.get('event', '')]

print(f"\n3. BAR PROCESSING:")
print(f"   BAR_DELIVERY events: {len(bar_delivery)}")
print(f"   BAR_ACCEPTED events: {len(bar_accepted)}")
print(f"   BAR_DATE_MISMATCH events: {len(bar_mismatch)}")
print(f"   BAR_PARTIAL events: {len(bar_partial)}")

# Check BAR_DATE_MISMATCH reasons
if bar_mismatch:
    reasons = defaultdict(int)
    for e in bar_mismatch:
        payload = e.get('data', {}).get('payload', {})
        reason = payload.get('rejection_reason', 'UNKNOWN')
        reasons[reason] += 1
    print(f"   BAR_DATE_MISMATCH reasons: {dict(reasons)}")

# Check if bars are being accepted after session start
if bar_delivery:
    print(f"\n   Recent bar deliveries:")
    for e in bar_delivery[-5:]:
        payload = e.get('data', {}).get('payload', {})
        ts = e.get('ts_utc', 'N/A')[:19]
        instrument = payload.get('instrument', 'N/A')
        stream = payload.get('stream', 'N/A')
        bar_chicago = payload.get('bar_timestamp_chicago', 'N/A')
        print(f"   - {ts} | {instrument} {stream} | Bar: {bar_chicago[:19] if len(str(bar_chicago)) > 19 else bar_chicago}")

# 4. Check stream states
stream_armed = [e for e in recent if 'STREAM_ARMED' in e.get('event', '')]
pre_hydration = [e for e in recent if 'PRE_HYDRATION' in e.get('event', '')]
armed_state = [e for e in recent if 'ARMED' in e.get('event', '') and 'STREAM_ARMED' not in e.get('event', '')]
range_building = [e for e in recent if 'RANGE_BUILDING' in e.get('event', '')]
range_locked = [e for e in recent if 'RANGE_LOCKED' in e.get('event', '')]
state_transitions = [e for e in recent if 'TRANSITION' in e.get('event', '')]

print(f"\n4. STREAM STATES:")
print(f"   STREAM_ARMED events: {len(stream_armed)}")
print(f"   PRE_HYDRATION events: {len(pre_hydration)}")
print(f"   ARMED state events: {len(armed_state)}")
print(f"   RANGE_BUILDING events: {len(range_building)}")
print(f"   RANGE_LOCKED events: {len(range_locked)}")
print(f"   State TRANSITION events: {len(state_transitions)}")

if state_transitions:
    print(f"\n   Recent state transitions:")
    for e in state_transitions[-5:]:
        payload = e.get('data', {}).get('payload', {})
        ts = e.get('ts_utc', 'N/A')[:19]
        from_state = payload.get('from_state', 'N/A')
        to_state = payload.get('to_state', 'N/A')
        print(f"   - {ts} | {from_state} -> {to_state}")

# 5. Check our new liveness fixes
live_warnings = [e for e in recent if 'BAR_PARTIAL_WARNING_LIVE_FEED' in e.get('event', '')]
forced_transitions = [e for e in recent if 'PRE_HYDRATION_FORCED_TRANSITION' in e.get('event', '')]
state_independent = [e for e in recent if 'BAR_BUFFERED_STATE_INDEPENDENT' in e.get('event', '')]

print(f"\n5. LIVENESS FIXES STATUS:")
print(f"   LIVE bar age warnings: {len(live_warnings)}")
print(f"   Forced PRE_HYDRATION transitions: {len(forced_transitions)}")
print(f"   State-independent buffering logs: {len(state_independent)}")

if forced_transitions:
    print(f"\n   Forced transitions (streams were stuck):")
    for e in forced_transitions[-3:]:
        payload = e.get('data', {}).get('payload', {})
        ts = e.get('ts_utc', 'N/A')[:19]
        bar_count = payload.get('bar_count', 'N/A')
        minutes_past = payload.get('minutes_past_range_start', 'N/A')
        print(f"   - {ts} | Bar count: {bar_count} | {minutes_past} min past range start")

# 6. Check for any stand-downs or engine issues
stand_down = [e for e in recent if 'STAND_DOWN' in e.get('event', '') or 'ENGINE_STOP' in e.get('event', '')]
kill_switch = [e for e in recent if 'KILL_SWITCH' in e.get('event', '')]

print(f"\n6. ENGINE HEALTH:")
print(f"   STAND_DOWN/STOP events: {len(stand_down)}")
print(f"   KILL_SWITCH events: {len(kill_switch)}")

if stand_down:
    print(f"\n   Stand-down events:")
    for e in stand_down[-3:]:
        payload = e.get('data', {}).get('payload', {})
        ts = e.get('ts_utc', 'N/A')[:19]
        reason = payload.get('reason', 'N/A')
        print(f"   - {ts} | Reason: {reason}")

# 7. Check BarsRequest status
barsrequest = [e for e in recent if 'BARSREQUEST' in e.get('event', '')]
print(f"\n7. BARSREQUEST STATUS:")
print(f"   BARSREQUEST events: {len(barsrequest)}")

if barsrequest:
    barsrequest_types = defaultdict(int)
    for e in barsrequest:
        event_type = e.get('event', 'UNKNOWN')
        barsrequest_types[event_type] += 1
    print(f"   BARSREQUEST summary: {dict(barsrequest_types)}")

# 8. Check for any unusual patterns
print(f"\n8. UNUSUAL PATTERNS:")
# Check if we're seeing bars after session start
if bar_delivery:
    bars_after_18 = 0
    for e in bar_delivery:
        payload = e.get('data', {}).get('payload', {})
        bar_chicago_str = str(payload.get('bar_timestamp_chicago', ''))
        if bar_chicago_str and '18:' in bar_chicago_str or '19:' in bar_chicago_str or '20:' in bar_chicago_str:
            bars_after_18 += 1
    print(f"   Bars delivered after 18:00 CST: {bars_after_18}/{len(bar_delivery)}")

# Check heartbeat frequency
heartbeats = [e for e in recent if 'HEARTBEAT' in e.get('event', '')]
if heartbeats:
    print(f"   Heartbeat events: {len(heartbeats)} (engine is alive)")

# 9. Summary and recommendations
print(f"\n" + "="*80)
print("SUMMARY & RECOMMENDATIONS:")
print("="*80)

issues_found = []

if len(errors) > 10:
    issues_found.append(f"High error count: {len(errors)} errors in recent logs")

if len(bar_mismatch) > 50:
    issues_found.append(f"Many BAR_DATE_MISMATCH events: {len(bar_mismatch)} (may be normal if bars are outside session window)")

if len(bar_delivery) == 0:
    issues_found.append("No bars being delivered to streams - check if bars are arriving")

if len(stream_armed) == 0 and len(state_transitions) == 0:
    issues_found.append("No stream arming or state transitions - streams may not be initializing")

if len(forced_transitions) > 0:
    issues_found.append(f"Streams were stuck and forced to transition: {len(forced_transitions)} times")

if len(stand_down) > 0:
    issues_found.append(f"Engine stand-down occurred: {len(stand_down)} times - check reasons")

if not issues_found:
    print("[OK] No major issues detected")
    print("[OK] Engine is running and processing bars")
    print("[OK] Liveness fixes are in place")
    if len(bar_delivery) > 0:
        print(f"[OK] Bars are being delivered ({len(bar_delivery)} recent deliveries)")
else:
    print("[WARN] Issues to investigate:")
    for issue in issues_found:
        print(f"  - {issue}")

print("\n" + "="*80)
