#!/usr/bin/env python3
"""
Comprehensive BarsRequest diagnostic - traces full execution flow
"""

import json
import sys
from pathlib import Path
from datetime import datetime, timezone
from collections import defaultdict, OrderedDict

def parse_timestamp(ts_str):
    """Parse ISO timestamp string."""
    try:
        ts_str = ts_str.replace('Z', '+00:00')
        return datetime.fromisoformat(ts_str)
    except:
        return None

def check_log_file(log_path):
    """Check a log file for events."""
    if not log_path.exists():
        return []
    
    events = []
    
    try:
        with open(log_path, 'r', encoding='utf-8-sig') as f:
            for line in f:
                line = line.strip()
                if not line:
                    continue
                
                try:
                    event = json.loads(line)
                    events.append(event)
                except json.JSONDecodeError:
                    continue
    
    except Exception as e:
        print(f"Error reading {log_path}: {e}", file=sys.stderr)
        return []
    
    return events

def main():
    log_dir = Path("logs/robot")
    
    print("=" * 100)
    print("COMPREHENSIVE BARSREQUEST DIAGNOSTIC")
    print("=" * 100)
    print()
    
    # Collect all events
    all_events = []
    
    # Check frontend feed
    frontend_feed = log_dir / "frontend_feed.jsonl"
    if frontend_feed.exists():
        print(f"Reading: {frontend_feed.name}")
        events = check_log_file(frontend_feed)
        all_events.extend(events)
    
    # Check robot logs
    robot_logs = list(log_dir.glob("robot_*.jsonl"))
    for log_file in sorted(robot_logs, key=lambda p: p.stat().st_mtime, reverse=True)[:5]:
        print(f"Reading: {log_file.name}")
        events = check_log_file(log_file)
        all_events.extend(events)
    
    if not all_events:
        print("\nNo events found in logs.")
        return
    
    # Sort by timestamp
    all_events.sort(key=lambda e: parse_timestamp(e.get('timestamp_utc') or e.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
    
    print(f"\nTotal events: {len(all_events)}")
    print()
    
    # Find last ENGINE_START
    engine_starts = [e for e in all_events if e.get('event_type') == 'ENGINE_START' or e.get('event') == 'ENGINE_START']
    if not engine_starts:
        print("[ERROR] No ENGINE_START events found")
        return
    
    last_start = engine_starts[-1]
    start_time = parse_timestamp(last_start.get('timestamp_utc') or last_start.get('ts_utc', ''))
    
    print("=" * 100)
    print("EXECUTION FLOW ANALYSIS (After Last ENGINE_START)")
    print("=" * 100)
    print(f"Last ENGINE_START: {start_time}")
    print()
    
    # Filter events after last start
    events_after_start = [e for e in all_events 
                         if parse_timestamp(e.get('timestamp_utc') or e.get('ts_utc', '')) > start_time]
    
    # Key events to track
    key_events = [
        'ENGINE_START',
        'SPEC_LOADED',
        'TRADING_DATE_LOCKED',
        'TIMETABLE_LOADED',
        'TIMETABLE_VALIDATED',
        'TIMETABLE_UPDATED',
        'STREAM_CREATED',
        'STREAM_ARMED',
        'STREAMS_CREATION_ATTEMPT',
        'STREAMS_CREATION_NOT_ATTEMPTED',
        'STREAMS_CREATION_SKIPPED',
        'STREAMS_CREATION_FAILED',
        'BARSREQUEST_REQUESTED',
        'BARSREQUEST_EXECUTED',
        'BARSREQUEST_FAILED',
        'BARSREQUEST_SKIPPED',
        'BARSREQUEST_RANGE_CHECK',
        'BARSREQUEST_STREAM_STATUS',
        'TIMETABLE_RELOAD_SKIPPED'
    ]
    
    print("KEY EVENTS CHECKLIST:")
    print("-" * 100)
    
    found_events = {}
    for event_type in key_events:
        matching = [e for e in events_after_start 
                   if e.get('event_type') == event_type or e.get('event') == event_type]
        found_events[event_type] = matching
        status = "[OK]" if matching else "[MISSING]"
        count = len(matching)
        print(f"{status} {event_type}: {count} event(s)")
    
    print()
    print("=" * 100)
    print("DETAILED EVENT ANALYSIS")
    print("=" * 100)
    print()
    
    # Check SPEC_LOADED
    spec_loaded = found_events.get('SPEC_LOADED', [])
    if spec_loaded:
        print("[OK] SPEC_LOADED:")
        for e in spec_loaded[-3:]:
            ts = e.get('timestamp_utc') or e.get('ts_utc', 'Unknown')
            print(f"  {ts}")
    else:
        print("[MISSING] SPEC_LOADED: NOT FOUND")
        print("  [ISSUE] Spec not loaded - streams cannot be created")
    
    print()
    
    # Check TRADING_DATE_LOCKED
    trading_date_locked = found_events.get('TRADING_DATE_LOCKED', [])
    if trading_date_locked:
        print("[OK] TRADING_DATE_LOCKED:")
        for e in trading_date_locked[-3:]:
            ts = e.get('timestamp_utc') or e.get('ts_utc', 'Unknown')
            payload = e.get('payload') or e.get('data', {}).get('payload', {})
            trading_date = payload.get('trading_date', 'N/A')
            print(f"  {ts} - Trading Date: {trading_date}")
    else:
        print("[MISSING] TRADING_DATE_LOCKED: NOT FOUND")
        print("  [ISSUE] Trading date not locked - streams cannot be created")
        print("  Checking if trading date was already locked...")
        # Check TIMETABLE_UPDATED for date_locked field
        timetable_updated = found_events.get('TIMETABLE_UPDATED', [])
        for e in timetable_updated[-3:]:
            payload = e.get('payload') or {}
            if isinstance(payload, dict):
                date_locked = payload.get('date_locked', False)
                if date_locked:
                    print(f"  → Trading date was locked during TIMETABLE_UPDATED (date_locked=true)")
    
    print()
    
    # Check TIMETABLE_LOADED
    timetable_loaded = found_events.get('TIMETABLE_LOADED', [])
    if timetable_loaded:
        print("[OK] TIMETABLE_LOADED:")
        for e in timetable_loaded[-3:]:
            ts = e.get('timestamp_utc') or e.get('ts_utc', 'Unknown')
            payload = e.get('payload') or e.get('data', {}).get('payload', {})
            streams = payload.get('streams', 'N/A')
            print(f"  {ts} - Streams in timetable: {streams}")
    else:
        print("[MISSING] TIMETABLE_LOADED: NOT FOUND")
    
    print()
    
    # Check STREAMS_CREATION events
    creation_attempt = found_events.get('STREAMS_CREATION_ATTEMPT', [])
    creation_not_attempted = found_events.get('STREAMS_CREATION_NOT_ATTEMPTED', [])
    creation_skipped = found_events.get('STREAMS_CREATION_SKIPPED', [])
    creation_failed = found_events.get('STREAMS_CREATION_FAILED', [])
    
    if creation_attempt:
        print("[OK] STREAMS_CREATION_ATTEMPT:")
        for e in creation_attempt[-3:]:
            ts = e.get('timestamp_utc') or e.get('ts_utc', 'Unknown')
            payload = e.get('payload') or e.get('data', {}).get('payload', {})
            print(f"  {ts}")
            print(f"    Trading Date: {payload.get('trading_date', 'N/A')}")
            print(f"    Streams Count: {payload.get('streams_count', 'N/A')}")
            print(f"    Spec is null: {payload.get('spec_is_null', 'N/A')}")
            print(f"    Time is null: {payload.get('time_is_null', 'N/A')}")
            print(f"    Last timetable is null: {payload.get('last_timetable_is_null', 'N/A')}")
    elif creation_not_attempted:
        print("[WARN] STREAMS_CREATION_NOT_ATTEMPTED:")
        for e in creation_not_attempted[-3:]:
            ts = e.get('timestamp_utc') or e.get('ts_utc', 'Unknown')
            payload = e.get('payload') or e.get('data', {}).get('payload', {})
            print(f"  {ts}")
            print(f"    Trading Date Has Value: {payload.get('trading_date_has_value', 'N/A')}")
            print(f"    Trading Date: {payload.get('trading_date', 'N/A')}")
            print(f"    Streams Count: {payload.get('streams_count', 'N/A')}")
            print(f"    Note: {payload.get('note', 'N/A')}")
    elif creation_skipped:
        print("[MISSING] STREAMS_CREATION_SKIPPED:")
        for e in creation_skipped[-3:]:
            ts = e.get('timestamp_utc') or e.get('ts_utc', 'Unknown')
            payload = e.get('payload') or e.get('data', {}).get('payload', {})
            reason = payload.get('reason', 'N/A')
            print(f"  {ts} - Reason: {reason}")
            print(f"    Spec is null: {payload.get('spec_is_null', 'N/A')}")
            print(f"    Time is null: {payload.get('time_is_null', 'N/A')}")
            print(f"    Trading Date Has Value: {payload.get('trading_date_has_value', 'N/A')}")
    elif creation_failed:
        print("[MISSING] STREAMS_CREATION_FAILED:")
        for e in creation_failed[-3:]:
            ts = e.get('timestamp_utc') or e.get('ts_utc', 'Unknown')
            payload = e.get('payload') or e.get('data', {}).get('payload', {})
            reason = payload.get('reason', 'N/A')
            print(f"  {ts} - Reason: {reason}")
    else:
        print("✗ STREAMS_CREATION events: NOT FOUND")
        print("  [ISSUE] EnsureStreamsCreated() may not be called or events not logged")
    
    print()
    
    # Check STREAM_CREATED
    stream_created = found_events.get('STREAM_CREATED', [])
    if stream_created:
        print(f"[OK] STREAM_CREATED: {len(stream_created)} event(s)")
        streams_by_instrument = defaultdict(list)
        for e in stream_created:
            stream = e.get('stream', 'N/A')
            instrument = e.get('instrument', 'N/A')
            streams_by_instrument[instrument].append(stream)
        
        for instrument, streams in streams_by_instrument.items():
            print(f"  {instrument}: {', '.join(streams)}")
    else:
        print("[MISSING] STREAM_CREATED: NOT FOUND")
        print("  [ISSUE] Streams are not being created")
    
    print()
    
    # Check BARSREQUEST events
    barsrequest_requested = found_events.get('BARSREQUEST_REQUESTED', [])
    barsrequest_executed = found_events.get('BARSREQUEST_EXECUTED', [])
    barsrequest_failed = found_events.get('BARSREQUEST_FAILED', [])
    barsrequest_skipped = found_events.get('BARSREQUEST_SKIPPED', [])
    barsrequest_range_check = found_events.get('BARSREQUEST_RANGE_CHECK', [])
    
    if barsrequest_requested:
        print("[OK] BARSREQUEST_REQUESTED:")
        for e in barsrequest_requested[-3:]:
            ts = e.get('timestamp_utc') or e.get('ts_utc', 'Unknown')
            payload = e.get('payload') or e.get('data', {}).get('payload', {})
            instrument = payload.get('instrument', 'N/A')
            print(f"  {ts} - Instrument: {instrument}")
    else:
        print("[MISSING] BARSREQUEST_REQUESTED: NOT FOUND")
        print("  [ISSUE] BarsRequest is not being initiated")
    
    if barsrequest_executed:
        print("[OK] BARSREQUEST_EXECUTED:")
        for e in barsrequest_executed[-3:]:
            ts = e.get('timestamp_utc') or e.get('ts_utc', 'Unknown')
            payload = e.get('payload') or e.get('data', {}).get('payload', {})
            instrument = payload.get('instrument', 'N/A')
            bars_returned = payload.get('bars_returned', 'N/A')
            print(f"  {ts} - Instrument: {instrument}, Bars: {bars_returned}")
            if bars_returned == 0:
                print(f"    [ISSUE] Zero bars returned!")
                note = payload.get('note', '')
                if note:
                    print(f"    Note: {note}")
    else:
        print("[MISSING] BARSREQUEST_EXECUTED: NOT FOUND")
    
    if barsrequest_failed:
        print("[MISSING] BARSREQUEST_FAILED:")
        for e in barsrequest_failed[-3:]:
            ts = e.get('timestamp_utc') or e.get('ts_utc', 'Unknown')
            payload = e.get('payload') or e.get('data', {}).get('payload', {})
            instrument = payload.get('instrument', 'N/A')
            error = payload.get('error', 'N/A')
            print(f"  {ts} - Instrument: {instrument}, Error: {error}")
    
    if barsrequest_skipped:
        print("[WARN] BARSREQUEST_SKIPPED:")
        for e in barsrequest_skipped[-3:]:
            ts = e.get('timestamp_utc') or e.get('ts_utc', 'Unknown')
            payload = e.get('payload') or e.get('data', {}).get('payload', {})
            instrument = payload.get('instrument', 'N/A')
            reason = payload.get('reason', 'N/A')
            note = payload.get('note', 'N/A')
            print(f"  {ts} - Instrument: {instrument}")
            print(f"    Reason: {reason}")
            print(f"    Note: {note}")
    
    if barsrequest_range_check:
        print("[WARN] BARSREQUEST_RANGE_CHECK:")
        for e in barsrequest_range_check[-3:]:
            ts = e.get('timestamp_utc') or e.get('ts_utc', 'Unknown')
            payload = e.get('payload') or e.get('data', {}).get('payload', {})
            instrument = payload.get('instrument', 'N/A')
            result = payload.get('result', 'N/A')
            print(f"  {ts} - Instrument: {instrument}, Result: {result}")
            if result in ['NO_STREAMS_FOUND', 'ALL_STREAMS_COMMITTED']:
                print(f"    [ISSUE] {result}")
    
    print()
    print("=" * 100)
    print("ROOT CAUSE ANALYSIS")
    print("=" * 100)
    print()
    
    # Determine root cause
    issues = []
    
    if not spec_loaded:
        issues.append("SPEC not loaded - spec file may be missing or invalid")
    
    if not trading_date_locked and not any(e.get('payload', {}).get('date_locked', False) for e in found_events.get('TIMETABLE_UPDATED', [])):
        issues.append("Trading date not locked - timetable may be missing trading_date field")
    
    if not timetable_loaded:
        issues.append("Timetable not loaded - timetable file may be missing or invalid")
    
    if not creation_attempt and not creation_not_attempted:
        issues.append("EnsureStreamsCreated() not being called - condition check may be failing")
    
    if creation_not_attempted:
        for e in creation_not_attempted:
            payload = e.get('payload') or e.get('data', {}).get('payload', {})
            if not payload.get('trading_date_has_value', False):
                issues.append("Trading date not set when EnsureStreamsCreated() check runs")
            if payload.get('streams_count', 0) > 0:
                issues.append("Streams already exist (unexpected)")
    
    if creation_skipped:
        for e in creation_skipped:
            payload = e.get('payload') or e.get('data', {}).get('payload', {})
            if payload.get('spec_is_null', False):
                issues.append("Spec is null when EnsureStreamsCreated() is called")
            if payload.get('time_is_null', False):
                issues.append("Time service is null when EnsureStreamsCreated() is called")
            if not payload.get('trading_date_has_value', False):
                issues.append("Trading date not set when EnsureStreamsCreated() is called")
    
    if creation_failed:
        for e in creation_failed:
            payload = e.get('payload') or e.get('data', {}).get('payload', {})
            reason = payload.get('reason', '')
            if reason == 'NO_TIMETABLE_LOADED':
                issues.append("Timetable not loaded when EnsureStreamsCreated() is called")
            elif reason == 'TIMEZONE_MISMATCH':
                issues.append(f"Timezone mismatch: {payload.get('timezone', 'N/A')}")
    
    if not stream_created:
        issues.append("Streams are not being created - ApplyTimetable() may not be executing")
    
    if not barsrequest_requested:
        if not stream_created:
            issues.append("BarsRequest not requested because no streams exist")
        else:
            issues.append("BarsRequest not requested despite streams existing")
    
    if barsrequest_executed:
        zero_bars = [e for e in barsrequest_executed 
                    if (e.get('payload') or e.get('data', {}).get('payload', {})).get('bars_returned', 0) == 0]
        if zero_bars:
            issues.append("BarsRequest executed but returned zero bars - check NinjaTrader data availability")
    
    if issues:
        print("IDENTIFIED ISSUES:")
        for i, issue in enumerate(issues, 1):
            print(f"  {i}. {issue}")
    else:
        print("No obvious issues found - check event timing and sequence")
    
    print()
    print("=" * 100)
    print("RECOMMENDED FIXES")
    print("=" * 100)
    print()
    
    if not spec_loaded:
        print("1. Check if spec file exists: configs/analyzer_robot_parity.json")
        print("2. Verify spec file is valid JSON")
        print("3. Check for SPEC_INVALID events in logs")
    
    if not trading_date_locked:
        print("1. Check timetable file: data/timetable/timetable_current.json")
        print("2. Verify timetable has 'trading_date' field")
        print("3. Verify trading_date format is YYYY-MM-DD")
        print("4. Check for TIMETABLE_MISSING_TRADING_DATE events")
    
    if not stream_created:
        print("1. Ensure spec is loaded before timetable")
        print("2. Ensure trading date is locked before stream creation")
        print("3. Check if EnsureStreamsCreated() is being called")
        print("4. Check diagnostic events (STREAMS_CREATION_*) for details")
    
    if not barsrequest_requested:
        print("1. Ensure streams are created first")
        print("2. Check GetAllExecutionInstrumentsForBarsRequest() return value")
        print("3. Check for BARSREQUEST_SKIPPED events")
    
    print()

if __name__ == "__main__":
    main()
