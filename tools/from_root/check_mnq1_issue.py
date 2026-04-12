#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Check for serious issues with MNQ1 stream"""
import json
from pathlib import Path
from datetime import datetime, timezone, timedelta
from collections import defaultdict

recent = datetime.now(timezone.utc) - timedelta(hours=2)

print("=" * 100)
print("MNQ1 SERIOUS ISSUE CHECK")
print("=" * 100)
print(f"Checking logs from last 2 hours (since {recent.strftime('%Y-%m-%d %H:%M:%S UTC')})")
print()

# Track events
mnq1_events = []
errors = []
warnings = []
critical_events = []

# Read robot logs
print("1. SEARCHING FOR MNQ1 EVENTS")
print("-" * 100)

log_files = list(Path("logs/robot").glob("*.jsonl"))
found_mnq1 = False

for log_file in log_files:
    try:
        with open(log_file, 'r', encoding='utf-8-sig') as f:
            for line_num, line in enumerate(f, 1):
                try:
                    e = json.loads(line.strip())
                    ts_str = e.get('ts_utc', '')
                    if ts_str:
                        ts = datetime.fromisoformat(ts_str.replace('Z', '+00:00'))
                        if ts >= recent:
                            # Check if this is MNQ1 related
                            stream = e.get('stream', '') or (e.get('data', {}) or {}).get('stream', '')
                            instrument = e.get('instrument', '') or (e.get('data', {}) or {}).get('instrument', '')
                            execution_instrument = (e.get('data', {}) or {}).get('execution_instrument', '')
                            
                            is_mnq1 = (
                                'MNQ1' in str(stream).upper() or
                                'MNQ' in str(instrument).upper() and '1' in str(stream) or
                                'MNQ' in str(execution_instrument).upper()
                            )
                            
                            if is_mnq1:
                                found_mnq1 = True
                                mnq1_events.append(e)
                                
                                event_type = e.get('event', '')
                                
                                if 'ERROR' in event_type.upper() or 'FAIL' in event_type.upper():
                                    errors.append(e)
                                
                                if 'WARN' in event_type.upper():
                                    warnings.append(e)
                                
                                if any(k in event_type.upper() for k in ['CRITICAL', 'FATAL', 'EXCEPTION', 'STALLED', 'STUCK']):
                                    critical_events.append(e)
                except:
                    pass
    except Exception as e:
        print(f"Error reading {log_file.name}: {e}")

if not found_mnq1:
    print("[WARNING] No MNQ1 events found in recent logs")
    print("Checking all MNQ-related events...")
    
    # Broader search
    for log_file in log_files:
        if 'MNQ' in log_file.name.upper():
            print(f"  Found: {log_file.name}")
            try:
                with open(log_file, 'r', encoding='utf-8-sig') as f:
                    lines = f.readlines()
                    recent_lines = []
                    for line in lines[-100:]:
                        try:
                            e = json.loads(line.strip())
                            ts_str = e.get('ts_utc', '')
                            if ts_str:
                                ts = datetime.fromisoformat(ts_str.replace('Z', '+00:00'))
                                if ts >= recent:
                                    recent_lines.append(e)
                        except:
                            pass
                    print(f"    Recent events: {len(recent_lines)}")
            except:
                pass
else:
    print(f"Found {len(mnq1_events)} MNQ1-related events")
    print(f"  Errors: {len(errors)}")
    print(f"  Warnings: {len(warnings)}")
    print(f"  Critical: {len(critical_events)}")

print()

# Analyze errors
print("2. ERRORS")
print("-" * 100)
if errors:
    print(f"Found {len(errors)} error events:")
    print()
    
    error_types = defaultdict(int)
    for e in errors:
        error_types[e.get('event', 'UNKNOWN')] += 1
    
    for event_type, count in sorted(error_types.items(), key=lambda x: x[1], reverse=True):
        print(f"  {event_type}: {count}")
    
    print()
    print("Recent Errors:")
    for e in errors[-10:]:
        ts = e.get('ts_utc', '')[:19]
        event_type = e.get('event', 'UNKNOWN')
        data = e.get('data', {})
        error_msg = data.get('error') or data.get('message') or data.get('reason') or 'N/A'
        print(f"    [{ts}] {event_type}")
        print(f"      Error: {str(error_msg)[:100]}")
        print()
else:
    print("[OK] No errors found")

print()

# Analyze critical events
print("3. CRITICAL EVENTS")
print("-" * 100)
if critical_events:
    print(f"Found {len(critical_events)} critical events:")
    print()
    
    for e in critical_events[-10:]:
        ts = e.get('ts_utc', '')[:19]
        event_type = e.get('event', 'UNKNOWN')
        data = e.get('data', {})
        message = data.get('message') or data.get('error') or data.get('reason') or 'N/A'
        print(f"    [{ts}] {event_type}")
        print(f"      Message: {str(message)[:150]}")
        print()
else:
    print("[OK] No critical events found")

print()

# Check for position issues
print("4. POSITION/EXECUTION ISSUES")
print("-" * 100)

position_issues = []
for e in mnq1_events:
    event_type = e.get('event', '')
    if any(k in event_type.upper() for k in ['POSITION', 'FILL', 'EXECUTION', 'ORDER', 'QUANTITY']):
        data = e.get('data', {})
        # Look for unusual quantities or errors
        quantity = data.get('quantity') or data.get('filled_quantity') or data.get('fill_quantity')
        if quantity and isinstance(quantity, (int, float)) and abs(quantity) > 10:
            position_issues.append(e)
        if 'error' in str(data).lower() or 'fail' in str(data).lower():
            position_issues.append(e)

if position_issues:
    print(f"Found {len(position_issues)} potential position/execution issues:")
    print()
    for e in position_issues[-10:]:
        ts = e.get('ts_utc', '')[:19]
        event_type = e.get('event', 'UNKNOWN')
        data = e.get('data', {})
        print(f"    [{ts}] {event_type}")
        print(f"      Data: {str(data)[:200]}")
        print()
else:
    print("[OK] No obvious position issues found")

print()

# Check for state issues
print("5. STATE TRANSITIONS")
print("-" * 100)

state_events = [e for e in mnq1_events if 'STATE' in e.get('event', '').upper() or 'TRANSITION' in e.get('event', '').upper()]
if state_events:
    print(f"Found {len(state_events)} state-related events:")
    print()
    for e in state_events[-10:]:
        ts = e.get('ts_utc', '')[:19]
        event_type = e.get('event', 'UNKNOWN')
        data = e.get('data', {})
        state = data.get('state') or data.get('new_state') or 'N/A'
        print(f"    [{ts}] {event_type} -> State: {state}")
else:
    print("[OK] No state transition issues found")

print()

# Most recent MNQ1 events
print("6. MOST RECENT MNQ1 EVENTS")
print("-" * 100)
if mnq1_events:
    recent_sorted = sorted(mnq1_events, key=lambda e: e.get('ts_utc', ''), reverse=True)
    print("Last 20 events:")
    print()
    for e in recent_sorted[:20]:
        ts = e.get('ts_utc', '')[:19]
        event_type = e.get('event', 'UNKNOWN')
        stream = e.get('stream', 'N/A')
        instrument = e.get('instrument', 'N/A')
        print(f"    [{ts}] {event_type}")
        print(f"      Stream: {stream}, Instrument: {instrument}")
        
        # Show key data fields
        data = e.get('data', {})
        if data:
            important_fields = ['error', 'message', 'reason', 'quantity', 'position', 'state', 'direction']
            for field in important_fields:
                if field in data:
                    print(f"      {field}: {data[field]}")
        print()
else:
    print("No recent MNQ1 events found")

print()
print("=" * 100)
print("SUMMARY")
print("=" * 100)
print()

if errors:
    print(f"[CRITICAL] {len(errors)} errors found")
else:
    print("[OK] No errors")

if critical_events:
    print(f"[CRITICAL] {len(critical_events)} critical events found")
else:
    print("[OK] No critical events")

if position_issues:
    print(f"[WARNING] {len(position_issues)} potential position issues")
else:
    print("[OK] No position issues")

if not mnq1_events:
    print("[WARNING] No MNQ1 events found - may need to check broader MNQ logs")

print()
