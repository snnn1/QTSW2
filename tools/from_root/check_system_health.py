#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Comprehensive system health check"""
import json
from pathlib import Path
from datetime import datetime, timezone, timedelta
from collections import defaultdict

recent = datetime.now(timezone.utc) - timedelta(minutes=10)
recent_hour = datetime.now(timezone.utc) - timedelta(hours=1)

print("=" * 100)
print("SYSTEM HEALTH CHECK")
print("=" * 100)
print(f"Checking logs from last 10 minutes (since {recent.strftime('%Y-%m-%d %H:%M:%S UTC')})")
print()

# Track events
events_by_type = defaultdict(list)
errors = []
warnings = []
bar_events = []
tick_events = []

# Read robot logs
print("1. ROBOT LOGS")
print("-" * 100)

log_files = list(Path("logs/robot").glob("*.jsonl"))
if not log_files:
    print("[ERROR] No robot log files found!")
else:
    print(f"Found {len(log_files)} log file(s)")
    
    for log_file in sorted(log_files, key=lambda p: p.stat().st_mtime, reverse=True)[:5]:
        try:
            with open(log_file, 'r', encoding='utf-8-sig') as f:
                lines = f.readlines()
                recent_lines = []
                for line in lines[-100:]:  # Check last 100 lines
                    try:
                        e = json.loads(line.strip())
                        ts_str = e.get('ts_utc', '')
                        if ts_str:
                            ts = datetime.fromisoformat(ts_str.replace('Z', '+00:00'))
                            if ts >= recent:
                                recent_lines.append(e)
                                event_type = e.get('event', '')
                                events_by_type[event_type].append(e)
                                
                                if 'BAR' in event_type.upper() or 'ONBARUPDATE' in event_type.upper():
                                    bar_events.append(e)
                                
                                if 'TICK' in event_type.upper() or 'ENGINE_TICK' in event_type.upper():
                                    tick_events.append(e)
                                
                                if 'ERROR' in event_type.upper() or 'FAIL' in event_type.upper():
                                    errors.append(e)
                                
                                if 'WARN' in event_type.upper():
                                    warnings.append(e)
                    except:
                        pass
                
                if recent_lines:
                    print(f"  {log_file.name}: {len(recent_lines)} recent events")
                else:
                    print(f"  {log_file.name}: No recent events")
        except Exception as e:
            print(f"  {log_file.name}: Error reading - {e}")

print()

# Check bar events
print("2. BAR EVENTS (Data Flow)")
print("-" * 100)
if bar_events:
    print(f"Found {len(bar_events)} bar-related events in last 10 minutes")
    
    by_instrument = defaultdict(list)
    for e in bar_events:
        data = e.get('data', {})
        instrument = data.get('execution_instrument_full_name') or data.get('instrument') or 'UNKNOWN'
        by_instrument[instrument].append(e)
    
    print(f"  Instruments receiving bars: {len(by_instrument)}")
    for instrument, events in sorted(by_instrument.items()):
        latest = max(events, key=lambda e: e.get('ts_utc', ''))
        ts = latest.get('ts_utc', '')[:19]
        print(f"    {instrument}: {len(events)} events, latest at {ts}")
    
    # Check most recent bar
    if bar_events:
        latest_bar = max(bar_events, key=lambda e: e.get('ts_utc', ''))
        latest_ts = datetime.fromisoformat(latest_bar.get('ts_utc', '').replace('Z', '+00:00'))
        age_seconds = (datetime.now(timezone.utc) - latest_ts).total_seconds()
        print(f"  Most recent bar: {age_seconds:.1f} seconds ago")
        if age_seconds > 120:
            print(f"  [WARNING] Last bar was {age_seconds/60:.1f} minutes ago (> 2 minutes)")
        else:
            print(f"  [OK] Bars are flowing normally")
else:
    print("[WARNING] No bar events found in last 10 minutes")
    print("  This could indicate:")
    print("    - Market is closed")
    print("    - Robot is not receiving data")
    print("    - Robot is not running")

print()

# Check engine tick events
print("3. ENGINE ACTIVITY")
print("-" * 100)
if tick_events:
    print(f"Found {len(tick_events)} engine tick events in last 10 minutes")
    
    if tick_events:
        latest_tick = max(tick_events, key=lambda e: e.get('ts_utc', ''))
        latest_ts = datetime.fromisoformat(latest_tick.get('ts_utc', '').replace('Z', '+00:00'))
        age_seconds = (datetime.now(timezone.utc) - latest_ts).total_seconds()
        print(f"  Most recent tick: {age_seconds:.1f} seconds ago")
        if age_seconds < 30:
            print(f"  [OK] Engine is active and processing ticks")
        else:
            print(f"  [WARNING] Engine may be stalled (last tick {age_seconds:.1f}s ago)")
else:
    print("[WARNING] No engine tick events found")
    print("  This could indicate the robot is not running")

print()

# Check errors
print("4. ERRORS")
print("-" * 100)
if errors:
    print(f"Found {len(errors)} error events in last 10 minutes:")
    error_types = defaultdict(int)
    for e in errors:
        error_types[e.get('event', 'UNKNOWN')] += 1
    
    for event_type, count in sorted(error_types.items(), key=lambda x: x[1], reverse=True)[:10]:
        print(f"  {event_type}: {count}")
    
    print()
    print("  Recent errors:")
    for e in errors[-5:]:
        ts = e.get('ts_utc', '')[:19]
        data = e.get('data', {})
        error_msg = data.get('error') or data.get('message') or 'N/A'
        print(f"    [{ts}] {e.get('event', 'UNKNOWN')}: {str(error_msg)[:80]}")
else:
    print("[OK] No errors found")

print()

# Check warnings
print("5. WARNINGS")
print("-" * 100)
if warnings:
    print(f"Found {len(warnings)} warning events in last 10 minutes:")
    warning_types = defaultdict(int)
    for e in warnings:
        warning_types[e.get('event', 'UNKNOWN')] += 1
    
    for event_type, count in sorted(warning_types.items(), key=lambda x: x[1], reverse=True)[:10]:
        print(f"  {event_type}: {count}")
else:
    print("[OK] No warnings found")

print()

# Check watchdog state
print("6. WATCHDOG STATE")
print("-" * 100)
state_file = Path("automation/logs/orchestrator_state.json")
if state_file.exists():
    try:
        with open(state_file, 'r') as f:
            state = json.load(f)
        
        market_open = state.get('market_open')
        data_status = state.get('data_status')
        worst_bar_age = state.get('worst_last_bar_age_seconds')
        engine_state = state.get('engine_activity_state')
        stalls = state.get('data_stall_detected', {})
        
        print(f"  Market Open: {market_open}")
        print(f"  Data Status: {data_status}")
        print(f"  Worst Bar Age: {worst_bar_age:.1f} seconds" if worst_bar_age else "  Worst Bar Age: None")
        print(f"  Engine State: {engine_state}")
        print(f"  Data Stalls: {len(stalls)} instrument(s)")
        
        if stalls:
            print("  Stalled Instruments:")
            for instrument, info in stalls.items():
                stall_detected = info.get('stall_detected', False)
                last_bar = info.get('last_bar_chicago', 'N/A')
                print(f"    {instrument}: {'STALLED' if stall_detected else 'OK'}, last bar: {last_bar}")
        
        if worst_bar_age and worst_bar_age > 120 and market_open:
            print(f"  [WARNING] Data stall detected (> 120s)")
        elif worst_bar_age and worst_bar_age < 120:
            print(f"  [OK] Data flowing normally")
        else:
            print(f"  [?] Status unclear (market may be closed)")
    except Exception as e:
        print(f"  [ERROR] Failed to read watchdog state: {e}")
else:
    print("  [WARNING] Watchdog state file not found")

print()

# Check frontend feed
print("7. FRONTEND FEED (Watchdog Processing)")
print("-" * 100)
feed_file = Path("logs/robot/frontend_feed.jsonl")
if feed_file.exists():
    try:
        # Check file size and modification time
        file_size = feed_file.stat().st_size
        mod_time = datetime.fromtimestamp(feed_file.stat().st_mtime, tz=timezone.utc)
        age_seconds = (datetime.now(timezone.utc) - mod_time).total_seconds()
        
        print(f"  File size: {file_size / (1024*1024):.2f} MB")
        print(f"  Last modified: {age_seconds:.1f} seconds ago")
        
        if age_seconds < 60:
            print(f"  [OK] Feed is being updated")
        else:
            print(f"  [WARNING] Feed hasn't been updated in {age_seconds/60:.1f} minutes")
        
        # Check last few events
        with open(feed_file, 'r', encoding='utf-8-sig') as f:
            lines = f.readlines()
            if lines:
                recent_feed_events = []
                for line in lines[-20:]:  # Last 20 lines
                    try:
                        e = json.loads(line.strip())
                        ts_str = e.get('timestamp_utc', '') or e.get('ts_utc', '')
                        if ts_str:
                            ts = datetime.fromisoformat(ts_str.replace('Z', '+00:00'))
                            if ts >= recent:
                                recent_feed_events.append(e)
                    except:
                        pass
                
                print(f"  Recent events in feed: {len(recent_feed_events)}")
                if recent_feed_events:
                    latest = max(recent_feed_events, key=lambda e: e.get('timestamp_utc', '') or e.get('ts_utc', ''))
                    event_type = latest.get('event_type') or latest.get('event', 'UNKNOWN')
                    ts = (latest.get('timestamp_utc') or latest.get('ts_utc', ''))[:19]
                    print(f"  Latest event: {event_type} at {ts}")
    except Exception as e:
        print(f"  [ERROR] Failed to read feed file: {e}")
else:
    print("  [WARNING] Frontend feed file not found")

print()

# Summary
print("=" * 100)
print("SUMMARY")
print("=" * 100)
print()

checks = []

# Bar flow check
if bar_events:
    latest_bar = max(bar_events, key=lambda e: e.get('ts_utc', ''))
    latest_ts = datetime.fromisoformat(latest_bar.get('ts_utc', '').replace('Z', '+00:00'))
    age_seconds = (datetime.now(timezone.utc) - latest_ts).total_seconds()
    if age_seconds < 120:
        checks.append(("[OK]", f"Bar Flow: Active ({age_seconds:.0f}s ago)"))
    else:
        checks.append(("[WARNING]", f"Bar Flow: Stalled ({age_seconds/60:.1f} min ago)"))
else:
    checks.append(("[WARNING]", "Bar Flow: No bars received"))

# Engine activity check
if tick_events:
    latest_tick = max(tick_events, key=lambda e: e.get('ts_utc', ''))
    latest_ts = datetime.fromisoformat(latest_tick.get('ts_utc', '').replace('Z', '+00:00'))
    age_seconds = (datetime.now(timezone.utc) - latest_ts).total_seconds()
    if age_seconds < 30:
        checks.append(("[OK]", f"Engine Activity: Active ({age_seconds:.0f}s ago)"))
    else:
        checks.append(("[WARNING]", f"Engine Activity: Stalled ({age_seconds:.0f}s ago)"))
else:
    checks.append(("[WARNING]", "Engine Activity: No ticks"))

# Errors check
if errors:
    checks.append(("[WARNING]", f"Errors: {len(errors)} found"))
else:
    checks.append(("[OK]", "Errors: None"))

# Feed processing check
if feed_file.exists():
    mod_time = datetime.fromtimestamp(feed_file.stat().st_mtime, tz=timezone.utc)
    age_seconds = (datetime.now(timezone.utc) - mod_time).total_seconds()
    if age_seconds < 60:
        checks.append(("[OK]", f"Feed Processing: Active ({age_seconds:.0f}s ago)"))
    else:
        checks.append(("[WARNING]", f"Feed Processing: Stalled ({age_seconds/60:.1f} min ago)"))
else:
    checks.append(("[WARNING]", "Feed Processing: File not found"))

for status, message in checks:
    print(f"{status} {message}")

print()
