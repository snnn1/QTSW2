#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Check recent logging to verify fixes"""
import json
from pathlib import Path
from datetime import datetime, timezone, timedelta

recent = datetime.now(timezone.utc) - timedelta(hours=6)

intents = []
be_events = []

for log_file in Path("logs/robot").glob("*.jsonl"):
    try:
        with open(log_file, 'r', encoding='utf-8-sig') as f:
            for line in f:
                try:
                    e = json.loads(line.strip())
                    ts_str = e.get('ts_utc', '')
                    if ts_str:
                        ts = datetime.fromisoformat(ts_str.replace('Z', '+00:00'))
                        if ts >= recent:
                            if 'INTENT_REGISTERED' in e.get('event', ''):
                                intents.append(e)
                            if 'BE_TRIGGER' in e.get('event', ''):
                                be_events.append(e)
                except:
                    pass
    except:
        pass

print("=" * 100)
print("LOGGING STATUS CHECK")
print("=" * 100)
print()

print(f"Found {len(intents)} INTENT_REGISTERED events in last 6 hours")
if intents:
    with_field = sum(1 for e in intents if 'has_be_trigger' in str(e.get('data', {})))
    print(f"  Events with BE trigger field: {with_field}/{len(intents)}")
    
    if with_field > 0:
        print("  [OK] NEW LOGGING FORMAT ACTIVE")
        for e in intents[-3:]:
            data = e.get('data', {})
            print(f"    Intent: {data.get('intent_id', 'N/A')[:25]}...")
            print(f"      Has BE Trigger: {data.get('has_be_trigger', 'N/A')}")
            print(f"      BE Trigger Price: {data.get('be_trigger', 'N/A')}")
    else:
        print("  WARNING: OLD LOGGING FORMAT (DLL needs restart)")
        print("    Sample event:")
        if intents:
            data = intents[-1].get('data', {})
            print(f"      Intent: {data.get('intent_id', 'N/A')[:25]}...")
            print(f"      Fields: {list(data.keys())[:5]}")
else:
    print("  No recent intents (may be normal)")

print()
print(f"Found {len(be_events)} BE trigger events in last 6 hours")
if be_events:
    reached = [e for e in be_events if 'REACHED' in e.get('event', '').upper()]
    print(f"  BE Triggers Reached: {len(reached)}")
    if reached:
        print("  [OK] BE Detection Working")
        for e in reached[-2:]:
            data = e.get('data', {})
            print(f"    Breakout Level: {data.get('breakout_level', 'N/A')}")
            print(f"    BE Stop: {data.get('be_stop_price', 'N/A')}")

print()
print("=" * 100)
