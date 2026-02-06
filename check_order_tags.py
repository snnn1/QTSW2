#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Check order tag encoding/decoding issues"""
import json
from pathlib import Path
from datetime import datetime, timezone, timedelta
from collections import defaultdict

recent = datetime.now(timezone.utc) - timedelta(hours=4)

print("=" * 100)
print("ORDER TAG ENCODING/DECODING DIAGNOSIS")
print("=" * 100)
print()

# Track events
tag_set_failed = []
tag_verification = []
fills_no_tag = []
fills_unknown_order = []
order_submitted = []
order_created = []

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
                            
                            if 'TAG_SET_FAILED' in event_type.upper():
                                tag_set_failed.append(e)
                            
                            if 'ORDER_CREATED' in event_type.upper() or 'ORDER_SUBMIT' in event_type.upper():
                                if 'ENTRY' in str(data.get('order_type', '')).upper() or 'STOP' in str(data.get('order_type', '')).upper():
                                    order_created.append(e)
                            
                            if 'EXECUTION_UPDATE_IGNORED_NO_TAG' in event_type.upper() or 'UNTrackED_FILL' in event_type.upper():
                                fills_no_tag.append(e)
                            
                            if 'EXECUTION_UPDATE_UNKNOWN_ORDER' in event_type.upper():
                                fills_unknown_order.append(e)
                except:
                    pass
    except:
        pass

print("1. ORDER TAG SET FAILURES")
print("-" * 100)
print(f"Tag set failures: {len(tag_set_failed)}")

if tag_set_failed:
    print("\n  Recent tag set failures:")
    for e in tag_set_failed[-10:]:
        ts = e.get('ts_utc', '')[:19]
        data = e.get('data', {})
        intent_id = data.get('intent_id', 'N/A')[:30]
        expected_tag = data.get('expected_tag', 'N/A')
        actual_tag = data.get('actual_tag', 'N/A')
        broker_order_id = data.get('broker_order_id', 'N/A')
        print(f"    [{ts}] Intent: {intent_id}...")
        print(f"      Expected Tag: {expected_tag}")
        print(f"      Actual Tag: {actual_tag}")
        print(f"      Broker Order ID: {broker_order_id}")
        print()
else:
    print("  [OK] No tag set failures found")

print()

print("2. FILLS WITH NO TAG")
print("-" * 100)
print(f"Fills ignored (no tag): {len(fills_no_tag)}")

if fills_no_tag:
    print("\n  Recent fills with no tag:")
    for e in fills_no_tag[-10:]:
        ts = e.get('ts_utc', '')[:19]
        data = e.get('data', {})
        broker_order_id = data.get('broker_order_id', 'N/A')
        order_tag = data.get('order_tag', 'N/A')
        fill_price = data.get('fill_price', 'N/A')
        fill_quantity = data.get('fill_quantity', 'N/A')
        instrument = data.get('instrument', 'N/A')
        print(f"    [{ts}] Broker Order ID: {broker_order_id}")
        print(f"      Order Tag: {order_tag}")
        print(f"      Fill Price: {fill_price}, Quantity: {fill_quantity}")
        print(f"      Instrument: {instrument}")
        print()
else:
    print("  [OK] No fills with missing tags")

print()

print("3. FILLS WITH UNKNOWN ORDER")
print("-" * 100)
print(f"Fills with unknown order (tag decoded but order not in map): {len(fills_unknown_order)}")

if fills_unknown_order:
    print("\n  Recent fills with unknown order:")
    for e in fills_unknown_order[-10:]:
        ts = e.get('ts_utc', '')[:19]
        data = e.get('data', {})
        intent_id = data.get('intent_id', 'N/A')[:30]
        broker_order_id = data.get('broker_order_id', 'N/A')
        tag = data.get('tag', 'N/A')
        fill_price = data.get('fill_price', 'N/A')
        print(f"    [{ts}] Intent: {intent_id}...")
        print(f"      Broker Order ID: {broker_order_id}")
        print(f"      Tag: {tag}")
        print(f"      Fill Price: {fill_price}")
        print()
else:
    print("  [OK] No fills with unknown orders")

print()

print("4. ORDER CREATION/SUBMISSION")
print("-" * 100)
print(f"Orders created/submitted: {len(order_created)}")

if order_created:
    # Group by intent
    orders_by_intent = defaultdict(list)
    for o in order_created:
        data = o.get('data', {})
        intent_id = data.get('intent_id', 'UNKNOWN')
        orders_by_intent[intent_id].append(o)
    
    print(f"\n  Orders by intent:")
    for intent_id, orders in list(orders_by_intent.items())[:5]:
        print(f"    Intent {intent_id[:30]}...: {len(orders)} orders")
        
        # Check for tag in order name
        for order in orders[:2]:
            data = order.get('data', {})
            order_name = data.get('order_name', 'N/A')
            broker_order_id = data.get('broker_order_id', 'N/A')
            print(f"      Order Name: {order_name}")
            print(f"      Broker Order ID: {broker_order_id}")

print()

print("5. CHECKING ORDER TAG FORMAT")
print("-" * 100)

# Check what format tags use
if order_created:
    sample_order = order_created[0]
    data = sample_order.get('data', {})
    order_name = data.get('order_name', '')
    
    print(f"Sample order name format: {order_name}")
    
    # Check if it starts with QTSW2:
    if order_name.startswith('QTSW2:'):
        print("  [OK] Order name starts with QTSW2: prefix")
    else:
        print("  [WARNING] Order name doesn't start with QTSW2: prefix")
    
    # Check length
    print(f"  Order name length: {len(order_name)}")

print()

print("=" * 100)
print("ROOT CAUSE ANALYSIS")
print("=" * 100)
print()

if len(tag_set_failed) > 0:
    print("[CRITICAL] Order tags are not being set correctly")
    print("  This will cause fills to have UNKNOWN intent_id")
    print("  Check SetOrderTag() implementation")

if len(fills_no_tag) > 0:
    print(f"[CRITICAL] {len(fills_no_tag)} fills ignored due to missing/invalid tags")
    print("  These fills cannot be tracked and positions are unprotected")

if len(fills_unknown_order) > 0:
    print(f"[WARNING] {len(fills_unknown_order)} fills have decoded intent_id but order not in tracking map")
    print("  Possible causes:")
    print("    1. Order was rejected before being added to _orderMap")
    print("    2. Order tracking race condition")
    print("    3. Order was removed from _orderMap before fill arrived")

if len(order_created) == 0:
    print("[CRITICAL] No orders created/submitted found")
    print("  This suggests orders aren't being submitted at all")

print()
