#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Check why stop orders aren't found for BE modification"""
import json
from pathlib import Path
from datetime import datetime, timezone, timedelta
from collections import defaultdict

recent = datetime.now(timezone.utc) - timedelta(hours=4)

print("=" * 100)
print("STOP ORDER TRACKING DIAGNOSIS")
print("=" * 100)
print()

# Track events
intent_registered = []
entry_fills = []
stop_submitted = []
stop_rejected = []
be_trigger_retry = []

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
                            event_type = e.get('event', '')
                            data = e.get('data', {}) or {}
                            
                            if 'INTENT_REGISTERED' in event_type.upper():
                                intent_registered.append(e)
                            
                            if 'EXECUTION_FILLED' in event_type.upper() and 'ENTRY' in str(data.get('order_type', '')).upper():
                                entry_fills.append(e)
                            
                            if 'PROTECTIVE' in event_type.upper() and 'STOP' in event_type.upper():
                                if 'SUBMITTED' in event_type.upper() or 'SUCCESS' in event_type.upper():
                                    stop_submitted.append(e)
                                elif 'REJECTED' in event_type.upper() or 'FAIL' in event_type.upper():
                                    stop_rejected.append(e)
                            
                            if 'BE_TRIGGER_RETRY' in event_type.upper():
                                be_trigger_retry.append(e)
                except:
                    pass
    except:
        pass

print("1. INTENT REGISTRATION")
print("-" * 100)
intents_by_id = {}
for reg in intent_registered:
    data = reg.get('data', {})
    intent_id = data.get('intent_id', '')
    if intent_id:
        intents_by_id[intent_id] = {
            'be_trigger': data.get('be_trigger'),
            'entry_price': data.get('entry_price'),
            'direction': data.get('direction'),
            'registered_time': reg.get('ts_utc', '')
        }

print(f"Intents registered: {len(intents_by_id)}")
for intent_id, info in list(intents_by_id.items())[:3]:
    print(f"\n  Intent: {intent_id[:30]}...")
    print(f"    BE Trigger: {info['be_trigger']}")
    print(f"    Entry Price: {info['entry_price']}")
    print(f"    Direction: {info['direction']}")

print()

print("2. ENTRY FILLS")
print("-" * 100)
fills_by_intent = defaultdict(list)
for fill in entry_fills:
    data = fill.get('data', {})
    intent_id = data.get('intent_id', '') or 'UNKNOWN'
    fills_by_intent[intent_id].append(fill)

print(f"Total entry fills: {len(entry_fills)}")
print(f"Fills with valid intent_id: {sum(len(v) for k, v in fills_by_intent.items() if k != 'UNKNOWN')}")
print(f"Fills with UNKNOWN intent_id: {len(fills_by_intent.get('UNKNOWN', []))}")

# Check fills for registered intents
for intent_id in intents_by_id.keys():
    if intent_id in fills_by_intent:
        fills = fills_by_intent[intent_id]
        print(f"\n  Intent {intent_id[:30]}... has {len(fills)} fills")
        if fills:
            first_fill = fills[0].get('ts_utc', '')[:19]
            print(f"    First fill: {first_fill}")

print()

print("3. PROTECTIVE STOP ORDERS")
print("-" * 100)
print(f"Stop orders submitted: {len(stop_submitted)}")
print(f"Stop orders rejected: {len(stop_rejected)}")

if stop_submitted:
    print("\n  Recent stop submissions:")
    for s in stop_submitted[-5:]:
        ts = s.get('ts_utc', '')[:19]
        data = s.get('data', {})
        intent_id = data.get('intent_id', 'N/A')[:30]
        broker_order_id = data.get('broker_order_id') or data.get('stop_order_id', 'N/A')
        stop_price = data.get('stop_price', 'N/A')
        print(f"    [{ts}] Intent: {intent_id}...")
        print(f"      Broker Order ID: {broker_order_id}")
        print(f"      Stop Price: {stop_price}")

if stop_rejected:
    print("\n  Recent stop rejections:")
    for r in stop_rejected[-5:]:
        ts = r.get('ts_utc', '')[:19]
        data = r.get('data', {})
        intent_id = data.get('intent_id', 'N/A')[:30]
        error = data.get('error', 'N/A')
        print(f"    [{ts}] Intent: {intent_id}...")
        print(f"      Error: {error[:100]}")

print()

print("4. BE TRIGGER RETRY EVENTS")
print("-" * 100)
print(f"BE Trigger Retry events: {len(be_trigger_retry)}")

if be_trigger_retry:
    # Group by intent
    retry_by_intent = defaultdict(list)
    for r in be_trigger_retry:
        data = r.get('data', {})
        intent_id = data.get('intent_id', 'UNKNOWN')
        retry_by_intent[intent_id].append(r)
    
    print(f"\n  Retries by intent:")
    for intent_id, retries in list(retry_by_intent.items())[:3]:
        print(f"    Intent {intent_id[:30]}...: {len(retries)} retries")
        
        # Check if stop was submitted for this intent
        stop_submitted_for_intent = [s for s in stop_submitted if (s.get('data', {}).get('intent_id') == intent_id)]
        print(f"      Stop orders submitted: {len(stop_submitted_for_intent)}")
        
        if len(stop_submitted_for_intent) == 0:
            print(f"      [CRITICAL] No stop orders submitted for this intent!")
        
        # Check timing
        if retries:
            first_retry = retries[0].get('ts_utc', '')[:19]
            print(f"      First retry: {first_retry}")
            
            if stop_submitted_for_intent:
                last_stop_submit = stop_submitted_for_intent[-1].get('ts_utc', '')[:19]
                print(f"      Last stop submit: {last_stop_submit}")
                
                # Check if retry happened before stop submit
                retry_ts = datetime.fromisoformat(retries[0].get('ts_utc', '').replace('Z', '+00:00'))
                submit_ts = datetime.fromisoformat(stop_submitted_for_intent[-1].get('ts_utc', '').replace('Z', '+00:00'))
                
                if retry_ts < submit_ts:
                    print(f"      [WARNING] BE retry happened BEFORE stop submit - race condition!")

print()

print("=" * 100)
print("ROOT CAUSE ANALYSIS")
print("=" * 100)
print()

if len(stop_submitted) < len([f for f in entry_fills if (f.get('data', {}).get('intent_id', '') != 'UNKNOWN')]):
    print("[CRITICAL] Not all entry fills have protective stop orders submitted")
    print(f"  Entry fills (with valid intent): {len([f for f in entry_fills if (f.get('data', {}).get('intent_id', '') != 'UNKNOWN')])}")
    print(f"  Stop orders submitted: {len(stop_submitted)}")

if len(be_trigger_retry) > 0 and len(stop_submitted) == 0:
    print("[CRITICAL] BE trigger detected but no stop orders were ever submitted")
    print("  This suggests protective orders failed to submit after entry fill")

if len(be_trigger_retry) > 0 and len(stop_submitted) > 0:
    print("[CRITICAL] BE trigger detected but stop order not found")
    print("  Possible causes:")
    print("    1. Stop order was rejected after submission")
    print("    2. Stop order was filled/cancelled before BE trigger")
    print("    3. Stop order tag doesn't match (tag encoding issue)")
    print("    4. Stop order is in different state (not Working/Accepted)")

print()
