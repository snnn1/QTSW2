#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Check latest log entries"""
import json
from pathlib import Path
from datetime import datetime, timezone, timedelta

# Check last 24 hours
recent = datetime.now(timezone.utc) - timedelta(hours=24)

print("=" * 100)
print("LATEST LOGGING CHECK")
print("=" * 100)
print()

# Get most recent log file
log_files = sorted(Path("logs/robot").glob("*.jsonl"), key=lambda p: p.stat().st_mtime, reverse=True)
if not log_files:
    print("No log files found")
    exit(0)

print(f"Checking {len(log_files)} log files")
print(f"Most recent: {log_files[0].name} (modified: {datetime.fromtimestamp(log_files[0].stat().st_mtime)})")
print()

# Check for INTENT_REGISTERED events
all_intents = []
all_be_events = []
all_recent_events = []

for log_file in log_files[:5]:  # Check 5 most recent files
    try:
        with open(log_file, 'r', encoding='utf-8-sig') as f:
            for line in f:
                try:
                    e = json.loads(line.strip())
                    ts_str = e.get('ts_utc', '')
                    if ts_str:
                        ts = datetime.fromisoformat(ts_str.replace('Z', '+00:00'))
                        if ts >= recent:
                            all_recent_events.append(e)
                            
                            if 'INTENT_REGISTERED' in e.get('event', ''):
                                all_intents.append(e)
                            
                            if 'BE_TRIGGER' in e.get('event', ''):
                                all_be_events.append(e)
                except:
                    pass
    except:
        pass

print(f"Found {len(all_recent_events)} total events in last 24 hours")
print(f"Found {len(all_intents)} INTENT_REGISTERED events")
print(f"Found {len(all_be_events)} BE trigger events")
print()

if all_intents:
    print("INTENT_REGISTERED Events:")
    print("-" * 100)
    
    # Check for new logging format
    with_field = sum(1 for e in all_intents if 'has_be_trigger' in str(e.get('data', {})))
    
    print(f"Events with BE trigger field: {with_field}/{len(all_intents)}")
    print()
    
    for e in all_intents[-5:]:
        ts = e.get('ts_utc', '')[:19]
        data = e.get('data', {})
        print(f"  [{ts}] Intent: {data.get('intent_id', 'N/A')[:30]}...")
        print(f"    Instrument: {data.get('instrument', 'N/A')}")
        print(f"    Direction: {data.get('direction', 'N/A')}")
        print(f"    Entry Price: {data.get('entry_price', 'N/A')}")
        
        if 'has_be_trigger' in str(data):
            print(f"    Has BE Trigger: {data.get('has_be_trigger', 'N/A')}")
            if data.get('be_trigger'):
                print(f"    BE Trigger Price: {data.get('be_trigger', 'N/A')}")
        else:
            print(f"    [OLD FORMAT] No BE trigger fields")
        print()
    
    if with_field > 0:
        print("[OK] NEW LOGGING FORMAT IS ACTIVE")
    else:
        print("[WARNING] OLD LOGGING FORMAT - DLL needs restart")
else:
    print("No INTENT_REGISTERED events in last 24 hours")
    print("This may be normal if:")
    print("  - No trades have been placed")
    print("  - NinjaTrader hasn't been restarted since DLL rebuild")
    print("  - System is not actively trading")
    print()

if all_be_events:
    print("BE Trigger Events:")
    print("-" * 100)
    reached = [e for e in all_be_events if 'REACHED' in e.get('event', '').upper()]
    failed = [e for e in all_be_events if 'FAILED' in e.get('event', '').upper()]
    
    print(f"BE Triggers Reached: {len(reached)}")
    print(f"BE Triggers Failed: {len(failed)}")
    print()
    
    if reached:
        print("Recent BE Triggers Reached:")
        for e in reached[-3:]:
            ts = e.get('ts_utc', '')[:19]
            data = e.get('data', {})
            print(f"  [{ts}] Intent: {data.get('intent_id', 'N/A')[:30]}...")
            print(f"    Breakout Level: {data.get('breakout_level', 'N/A')}")
            print(f"    BE Stop Price: {data.get('be_stop_price', 'N/A')}")
            print()

# Check most recent events overall
print("Most Recent Events (any type):")
print("-" * 100)
recent_sorted = sorted(all_recent_events, key=lambda e: e.get('ts_utc', ''), reverse=True)
for e in recent_sorted[:10]:
    ts = e.get('ts_utc', '')[:19]
    event_type = e.get('event', 'UNKNOWN')
    print(f"  [{ts}] {event_type}")

print()
print("=" * 100)
