#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Check order tracking timing - when orders are added to _orderMap vs when fills arrive"""
import json
from pathlib import Path
from datetime import datetime, timezone, timedelta
from collections import defaultdict

recent = datetime.now(timezone.utc) - timedelta(hours=4)

print("=" * 100)
print("ORDER TRACKING TIMING ANALYSIS")
print("=" * 100)
print()

# Track events by intent and broker order ID
intent_registered = {}
orders_submitted = {}  # broker_order_id -> (intent_id, timestamp, event)
fills_received = {}  # broker_order_id -> (intent_id, timestamp, event)
fills_unknown_order = {}  # broker_order_id -> (intent_id, tag, timestamp, event)

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
                                intent_id = data.get('intent_id', '')
                                if intent_id:
                                    intent_registered[intent_id] = {
                                        'timestamp': ts,
                                        'event': e
                                    }
                            
                            if 'ORDER_SUBMIT_SUCCESS' in event_type.upper() or 'ORDER_CREATED' in event_type.upper():
                                broker_order_id = data.get('broker_order_id') or data.get('order_id', '')
                                intent_id = data.get('intent_id', '')
                                if broker_order_id:
                                    orders_submitted[broker_order_id] = {
                                        'intent_id': intent_id,
                                        'timestamp': ts,
                                        'event': e
                                    }
                            
                            if 'EXECUTION_FILLED' in event_type.upper() or 'EXECUTION_UPDATE' in event_type.upper():
                                broker_order_id = data.get('broker_order_id') or data.get('order_id', '')
                                intent_id = data.get('intent_id', '')
                                fill_price = data.get('fill_price') or data.get('price', '')
                                
                                if broker_order_id:
                                    if broker_order_id in fills_received:
                                        # Multiple fills for same order
                                        fills_received[broker_order_id].append({
                                            'intent_id': intent_id,
                                            'timestamp': ts,
                                            'fill_price': fill_price,
                                            'event': e
                                        })
                                    else:
                                        fills_received[broker_order_id] = [{
                                            'intent_id': intent_id,
                                            'timestamp': ts,
                                            'fill_price': fill_price,
                                            'event': e
                                        }]
                            
                            if 'EXECUTION_UPDATE_UNKNOWN_ORDER' in event_type.upper():
                                broker_order_id = data.get('broker_order_id', '')
                                intent_id = data.get('intent_id', '')
                                tag = data.get('tag', '')
                                
                                if broker_order_id:
                                    fills_unknown_order[broker_order_id] = {
                                        'intent_id': intent_id,
                                        'tag': tag,
                                        'timestamp': ts,
                                        'event': e
                                    }
                except:
                    pass
    except:
        pass

print("1. INTENT REGISTRATION")
print("-" * 100)
print(f"Intents registered: {len(intent_registered)}")
for intent_id, info in list(intent_registered.items())[:3]:
    print(f"  Intent {intent_id[:30]}... registered at {info['timestamp']}")

print()

print("2. ORDER SUBMISSION")
print("-" * 100)
print(f"Orders submitted: {len(orders_submitted)}")

# Check if orders were submitted for registered intents
for intent_id in intent_registered.keys():
    orders_for_intent = [o for o in orders_submitted.values() if o['intent_id'] == intent_id]
    print(f"  Intent {intent_id[:30]}...: {len(orders_for_intent)} orders submitted")

print()

print("3. FILLS RECEIVED")
print("-" * 100)
print(f"Fills received: {len(fills_received)}")

# Check timing: order submit vs fill
for broker_order_id, fills in list(fills_received.items())[:5]:
    if broker_order_id in orders_submitted:
        order_submit = orders_submitted[broker_order_id]
        first_fill = fills[0]
        
        submit_ts = order_submit['timestamp']
        fill_ts = first_fill['timestamp']
        delay = (fill_ts - submit_ts).total_seconds()
        
        print(f"\n  Broker Order ID: {broker_order_id}")
        print(f"    Order Submitted: {submit_ts}")
        print(f"    First Fill: {fill_ts}")
        print(f"    Delay: {delay:.3f} seconds")
        print(f"    Intent ID (from order): {order_submit['intent_id']}")
        print(f"    Intent ID (from fill): {first_fill['intent_id']}")
        
        if order_submit['intent_id'] != first_fill['intent_id']:
            print(f"    [WARNING] Intent ID mismatch!")
    else:
        print(f"\n  Broker Order ID: {broker_order_id}")
        print(f"    [WARNING] Order submission not found for this fill")

print()

print("4. FILLS WITH UNKNOWN ORDER")
print("-" * 100)
print(f"Fills with unknown order: {len(fills_unknown_order)}")

if fills_unknown_order:
    print("\n  Analyzing fills with decoded intent_id but order not in map:")
    for broker_order_id, info in list(fills_unknown_order.items())[:5]:
        intent_id = info['intent_id']
        tag = info['tag']
        fill_ts = info['timestamp']
        
        print(f"\n    Broker Order ID: {broker_order_id}")
        print(f"      Tag: {tag}")
        print(f"      Decoded Intent ID: {intent_id}")
        print(f"      Fill Time: {fill_ts}")
        
        # Check if order was submitted
        if broker_order_id in orders_submitted:
            order_submit = orders_submitted[broker_order_id]
            submit_ts = order_submit['timestamp']
            delay = (fill_ts - submit_ts).total_seconds()
            
            print(f"      Order Submitted: {submit_ts}")
            print(f"      Delay: {delay:.3f} seconds")
            
            if delay < 0:
                print(f"      [CRITICAL] Fill arrived BEFORE order submission!")
            elif delay < 0.1:
                print(f"      [WARNING] Fill arrived very quickly after submission (race condition?)")
            
            # Check if intent matches
            if order_submit['intent_id'] == intent_id:
                print(f"      [OK] Intent ID matches")
            else:
                print(f"      [WARNING] Intent ID mismatch: order={order_submit['intent_id']}, fill={intent_id}")
        else:
            print(f"      [CRITICAL] Order submission not found!")
        
        # Check if intent was registered
        if intent_id in intent_registered:
            reg_ts = intent_registered[intent_id]['timestamp']
            print(f"      Intent Registered: {reg_ts}")
        else:
            print(f"      [WARNING] Intent not found in registration list")

print()

print("=" * 100)
print("ROOT CAUSE ANALYSIS")
print("=" * 100)
print()

issues = []

# Check for race conditions
for broker_order_id, info in fills_unknown_order.items():
    if broker_order_id in orders_submitted:
        order_submit = orders_submitted[broker_order_id]
        fill_ts = info['timestamp']
        submit_ts = order_submit['timestamp']
        delay = (fill_ts - submit_ts).total_seconds()
        
        if delay < 0.1:
            issues.append(f"[RACE CONDITION] Fill arrived {delay:.3f}s after order submission - order may not be in _orderMap yet")

# Check for missing order submissions
missing_submissions = [bid for bid in fills_unknown_order.keys() if bid not in orders_submitted]
if missing_submissions:
    issues.append(f"[CRITICAL] {len(missing_submissions)} fills have no corresponding order submission")

# Check for intent mismatches
for broker_order_id, info in fills_unknown_order.items():
    if broker_order_id in orders_submitted:
        order_submit = orders_submitted[broker_order_id]
        if order_submit['intent_id'] != info['intent_id']:
            issues.append(f"[WARNING] Intent ID mismatch for order {broker_order_id}")

if issues:
    print("ISSUES FOUND:")
    for issue in issues:
        print(f"  {issue}")
else:
    print("[OK] No obvious timing issues found")

print()
