#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Check for specific issues mentioned by user"""
import json
from pathlib import Path
from datetime import datetime, timezone, timedelta

today = datetime.now(timezone.utc)
yesterday = today - timedelta(days=1)

# Check for CL2 position issues
cl2_events = []
flatten_events = []
position_mismatches = []

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
                            if ts >= yesterday:
                                event_type = e.get('event', '').upper()
                                data = e.get('data', {})
                                instrument = data.get('instrument', '') or data.get('execution_instrument', '')
                                
                                if 'CL2' in instrument.upper() or ('CL' in instrument.upper() and 'CL' in log_file.name.upper()):
                                    cl2_events.append(e)
                                
                                if 'FLATTEN' in event_type:
                                    flatten_events.append(e)
                                
                                if 'POSITION' in event_type and ('MISMATCH' in event_type or 'ERROR' in event_type):
                                    position_mismatches.append(e)
                        except:
                            pass
                except:
                    pass
    except:
        pass

print("=" * 100)
print("SPECIFIC ISSUES CHECK")
print("=" * 100)
print()

print(f"CL2/CL Events: {len(cl2_events)}")
if cl2_events:
    print("Recent CL2/CL events:")
    for e in cl2_events[-10:]:
        ts = e.get('ts_utc', '')[:19]
        event = e.get('event', 'N/A')
        data = e.get('data', {})
        print(f"  [{ts}] {event}")
        if data.get('intent_id'):
            print(f"    Intent: {data.get('intent_id', 'N/A')[:30]}...")
        if data.get('fill_quantity') or data.get('quantity'):
            print(f"    Fill Qty: {data.get('fill_quantity') or data.get('quantity', 'N/A')}")
        if data.get('filled_total'):
            print(f"    Filled Total: {data.get('filled_total', 'N/A')}")
    print()

print(f"Flatten Events: {len(flatten_events)}")
if flatten_events:
    failed = [e for e in flatten_events if 'FAILED' in e.get('event', '').upper()]
    print(f"  Failed: {len(failed)}")
    if failed:
        print("  Recent failures:")
        for e in failed[-5:]:
            ts = e.get('ts_utc', '')[:19]
            data = e.get('data', {})
            print(f"    [{ts}] Intent: {data.get('intent_id', 'N/A')[:30] if data.get('intent_id') else 'N/A'}...")
            print(f"      Instrument: {data.get('instrument', 'N/A')}")
            print(f"      Error: {data.get('error', 'N/A')}")
    print()

print(f"Position Mismatches: {len(position_mismatches)}")
if position_mismatches:
    print("  Recent mismatches:")
    for e in position_mismatches[-5:]:
        ts = e.get('ts_utc', '')[:19]
        data = e.get('data', {})
        print(f"    [{ts}] {e.get('event', 'N/A')}")
        print(f"      Intent: {data.get('intent_id', 'N/A')[:30] if data.get('intent_id') else 'N/A'}...")
        print(f"      Instrument: {data.get('instrument', 'N/A')}")
    print()

print("=" * 100)
