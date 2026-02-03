#!/usr/bin/env python3
"""Check INTENT_REGISTERED events for BE trigger status"""
import json
from pathlib import Path
from datetime import datetime, timezone, timedelta

today = datetime.now(timezone.utc)
yesterday = today - timedelta(days=1)

intent_registered = []

for log_file in Path("logs/robot").glob("*.jsonl"):
    try:
        with open(log_file, 'r', encoding='utf-8-sig') as f:
            for line in f:
                try:
                    e = json.loads(line.strip())
                    ts_str = e.get('ts_utc', '')
                    if ts_str:
                        try:
                            ts = datetime.fromisoformat(ts_str.replace('Z', '+00:00'))
                            if ts >= yesterday and 'INTENT_REGISTERED' in e.get('event', ''):
                                intent_registered.append(e)
                        except:
                            pass
                except:
                    pass
    except:
        pass

print(f"Found {len(intent_registered)} INTENT_REGISTERED events")
print()

for e in intent_registered[-10:]:  # Last 10
    ts = e.get('ts_utc', '')[:19]
    data = e.get('data', {})
    print(f"[{ts}] Intent: {data.get('intent_id', 'N/A')[:30]}...")
    print(f"  Instrument: {data.get('instrument', 'N/A')}")
    print(f"  Direction: {data.get('direction', 'N/A')}")
    print(f"  Entry Price: {data.get('entry_price', 'N/A')}")
    print(f"  Has BE Trigger Field: {'has_be_trigger' in str(data)}")
    if 'be_trigger' in str(data):
        print(f"  BE Trigger: {data.get('be_trigger', 'N/A')}")
    if 'has_be_trigger' in str(data):
        print(f"  Has BE Trigger: {data.get('has_be_trigger', 'N/A')}")
    print()
