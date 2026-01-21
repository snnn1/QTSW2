#!/usr/bin/env python3
"""Check for new diagnostic instrumentation logs"""
import json
from pathlib import Path

log_dir = Path("logs/robot")
if not log_dir.exists():
    print("Log directory not found")
    exit(1)

# Collect events from all log files (ENGINE and per-instrument)
events = []
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
    except:
        pass

# Sort by timestamp and get recent events
events.sort(key=lambda e: e.get('ts_utc', ''))
recent = events[-2000:] if len(events) > 2000 else events

print("="*80)
print("DIAGNOSTIC INSTRUMENTATION STATUS")
print("="*80)

# Check for new diagnostic events
tick_method_entered = [e for e in recent if 'TICK_METHOD_ENTERED' in e.get('event', '')]
tick_called = [e for e in recent if 'TICK_CALLED' in e.get('event', '')]
tick_trace = [e for e in recent if 'TICK_TRACE' in e.get('event', '')]
pre_hydration_handler = [e for e in recent if 'PRE_HYDRATION_HANDLER_TRACE' in e.get('event', '')]
range_start_diagnostic = [e for e in recent if 'PRE_HYDRATION_RANGE_START_DIAGNOSTIC' in e.get('event', '')]
range_start_initialized = [e for e in recent if 'RANGE_START_INITIALIZED' in e.get('event', '')]

print(f"\n0. TICK_METHOD_ENTERED events: {len(tick_method_entered)} (unconditional - should appear immediately)")
print(f"1. TICK_CALLED events: {len(tick_called)} (every 1 min if Tick() is called)")
print(f"2. TICK_TRACE events: {len(tick_trace)} (every 5 min)")
print(f"3. PRE_HYDRATION_HANDLER_TRACE events: {len(pre_hydration_handler)}")
print(f"4. PRE_HYDRATION_RANGE_START_DIAGNOSTIC events: {len(range_start_diagnostic)}")
print(f"5. RANGE_START_INITIALIZED events: {len(range_start_initialized)}")

if tick_method_entered:
    print(f"\n=== TICK_METHOD_ENTERED (last 10) ===")
    for e in tick_method_entered[-10:]:
        ts = e.get('ts_utc', 'N/A')[:19] if isinstance(e.get('ts_utc'), str) else 'N/A'
        stream = e.get('stream', 'N/A')
        payload = e.get('data', {}).get('payload', {})
        if payload:
            stream = payload.get('stream_id', stream)
        state = payload.get('current_state', e.get('state', 'N/A'))
        print(f"  {ts} | Stream: {stream} | State: {state}")

if tick_called:
    print(f"\n=== TICK_CALLED (last 10) ===")
    for e in tick_called[-10:]:
        payload = e.get('data', {}).get('payload', {})
        ts = e.get('ts_utc', 'N/A')[:19]
        stream = payload.get('stream_id', 'N/A')
        state = payload.get('current_state', 'N/A')
        print(f"  {ts} | Stream: {stream} | State: {state}")

if tick_trace:
    print(f"\n=== TICK_TRACE (last 5) ===")
    for e in tick_trace[-5:]:
        payload = e.get('data', {}).get('payload', {})
        ts = e.get('ts_utc', 'N/A')[:19]
        stream = payload.get('stream_id', 'N/A')
        state = payload.get('current_state', 'N/A')
        print(f"  {ts} | Stream: {stream} | State: {state}")

if pre_hydration_handler:
    print(f"\n=== PRE_HYDRATION_HANDLER_TRACE (last 5) ===")
    for e in pre_hydration_handler[-5:]:
        payload = e.get('data', {}).get('payload', {})
        ts = e.get('ts_utc', 'N/A')[:19]
        stream = payload.get('stream_id', 'N/A')
        bar_count = payload.get('bar_count', 'N/A')
        print(f"  {ts} | Stream: {stream} | Bar count: {bar_count}")

if range_start_diagnostic:
    print(f"\n=== PRE_HYDRATION_RANGE_START_DIAGNOSTIC (last 5) ===")
    for e in range_start_diagnostic[-5:]:
        payload = e.get('data', {}).get('payload', {})
        ts = e.get('ts_utc', 'N/A')[:19]
        stream = payload.get('stream_id', 'N/A')
        is_default = payload.get('range_start_is_default', 'N/A')
        range_start = payload.get('range_start_chicago_raw', 'N/A')
        range_start_year = payload.get('range_start_year', 'N/A')
        trading_date = payload.get('trading_date', 'N/A')
        print(f"  {ts} | Stream: {stream}")
        print(f"    RangeStart is default: {is_default}")
        print(f"    RangeStart year: {range_start_year}")
        print(f"    RangeStart: {range_start}")
        print(f"    Trading date: {trading_date}")

if range_start_initialized:
    print(f"\n=== RANGE_START_INITIALIZED (last 10) ===")
    for e in range_start_initialized[-10:]:
        payload = e.get('data', {}).get('payload', {})
        ts = e.get('ts_utc', 'N/A')[:19]
        stream = payload.get('stream_id', 'N/A')
        range_start = payload.get('range_start_chicago', 'N/A')
        trading_date = payload.get('trading_date', 'N/A')
        source = payload.get('source', 'N/A')
        print(f"  {ts} | Stream: {stream} | Trading date: {trading_date} | Source: {source}")
        print(f"    RangeStart: {range_start}")

# Check for timeout events
timeout_skipped = [e for e in recent if 'PRE_HYDRATION_TIMEOUT_SKIPPED' in e.get('event', '')]
forced_transition = [e for e in recent if 'PRE_HYDRATION_FORCED_TRANSITION' in e.get('event', '')]

print(f"\n=== TIMEOUT STATUS ===")
print(f"PRE_HYDRATION_TIMEOUT_SKIPPED events: {len(timeout_skipped)}")
print(f"PRE_HYDRATION_FORCED_TRANSITION events: {len(forced_transition)}")

if timeout_skipped:
    print(f"\n=== TIMEOUT SKIPPED (last 5) ===")
    for e in timeout_skipped[-5:]:
        payload = e.get('data', {}).get('payload', {})
        ts = e.get('ts_utc', 'N/A')[:19]
        stream = payload.get('stream_id', 'N/A')
        reason = payload.get('reason', 'N/A')
        print(f"  {ts} | Stream: {stream} | Reason: {reason}")

print("\n" + "="*80)
print("ANALYSIS:")
print("="*80)

if len(tick_trace) == 0:
    print("[WARN] No TICK_TRACE events found!")
    print("  -> Diagnostic logs may be disabled (check enable_diagnostic_logs)")
    print("  -> OR Tick() is not being called")
elif len(pre_hydration_handler) == 0:
    print("[WARN] No PRE_HYDRATION_HANDLER_TRACE events found!")
    print("  -> Streams may not be in PRE_HYDRATION state")
    print("  -> OR HandlePreHydrationState() is not being entered")
elif len(range_start_diagnostic) == 0:
    print("[WARN] No PRE_HYDRATION_RANGE_START_DIAGNOSTIC events found!")
    print("  -> Diagnostic logs may be disabled")
    print("  -> OR _preHydrationComplete is false")
else:
    print("[OK] Diagnostic logs are appearing")
    if len(range_start_initialized) > 0:
        print(f"[OK] RangeStartChicagoTime initialized {len(range_start_initialized)} times")
    else:
        print("[WARN] No RANGE_START_INITIALIZED events found")
        print("  -> RangeStartChicagoTime may not be initialized")

print("\n" + "="*80)
