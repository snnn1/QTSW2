#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Check if orders are being rejected and removed from _orderMap"""
import json
from pathlib import Path
from datetime import datetime, timezone, timedelta
from collections import defaultdict

recent = datetime.now(timezone.utc) - timedelta(hours=4)

print("=" * 100)
print("ORDER REJECTION ANALYSIS")
print("=" * 100)
print()

# Track events
order_rejected = []
order_acknowledged = []
order_cancelled = []
fills_unknown_order = {}

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
                            
                            if 'REJECTED' in event_type.upper() or 'ORDER_REJECTED' in event_type.upper():
                                order_rejected.append(e)
                            
                            if 'ACKNOWLEDGED' in event_type.upper():
                                order_acknowledged.append(e)
                            
                            if 'CANCELLED' in event_type.upper():
                                order_cancelled.append(e)
                            
                            if 'EXECUTION_UPDATE_UNKNOWN_ORDER' in event_type.upper():
                                broker_order_id = data.get('broker_order_id', '')
                                intent_id = data.get('intent_id', '')
                                tag = data.get('tag', '')
                                order_state = data.get('order_state', '')
                                
                                if broker_order_id:
                                    fills_unknown_order[broker_order_id] = {
                                        'intent_id': intent_id,
                                        'tag': tag,
                                        'order_state': order_state,
                                        'timestamp': ts,
                                        'event': e
                                    }
                except:
                    pass
    except:
        pass

print("1. ORDER REJECTIONS")
print("-" * 100)
print(f"Orders rejected: {len(order_rejected)}")

if order_rejected:
    print("\n  Recent rejections:")
    for r in order_rejected[-10:]:
        ts = r.get('ts_utc', '')[:19]
        data = r.get('data', {})
        intent_id = data.get('intent_id', 'N/A')[:30]
        broker_order_id = data.get('broker_order_id', 'N/A')
        error = data.get('error', 'N/A')[:100]
        print(f"    [{ts}] Intent: {intent_id}...")
        print(f"      Broker Order ID: {broker_order_id}")
        print(f"      Error: {error}")
        print()

print()

print("2. ORDER ACKNOWLEDGMENTS")
print("-" * 100)
print(f"Orders acknowledged: {len(order_acknowledged)}")

if order_acknowledged:
    print("\n  Recent acknowledgments:")
    for a in order_acknowledged[-5:]:
        ts = a.get('ts_utc', '')[:19]
        data = a.get('data', {})
        intent_id = data.get('intent_id', 'N/A')[:30]
        broker_order_id = data.get('broker_order_id', 'N/A')
        print(f"    [{ts}] Intent: {intent_id}..., Order ID: {broker_order_id}")

print()

print("3. FILLS WITH UNKNOWN ORDER - ORDER STATE")
print("-" * 100)
print(f"Fills with unknown order: {len(fills_unknown_order)}")

if fills_unknown_order:
    print("\n  Order states when fill arrived:")
    states = defaultdict(int)
    for info in fills_unknown_order.values():
        state = info['order_state'] or 'UNKNOWN'
        states[state] += 1
    
    for state, count in sorted(states.items(), key=lambda x: x[1], reverse=True):
        print(f"    {state}: {count}")
    
    print("\n  Sample fills with unknown order:")
    for broker_order_id, info in list(fills_unknown_order.items())[:5]:
        print(f"\n    Broker Order ID: {broker_order_id}")
        print(f"      Intent ID: {info['intent_id']}")
        print(f"      Tag: {info['tag']}")
        print(f"      Order State: {info['order_state']}")
        print(f"      Time: {info['timestamp']}")

print()

print("=" * 100)
print("ROOT CAUSE ANALYSIS")
print("=" * 100)
print()

# Check if rejected orders are filling
if order_rejected:
    rejected_order_ids = set()
    for r in order_rejected:
        broker_order_id = (r.get('data', {}) or {}).get('broker_order_id', '')
        if broker_order_id:
            rejected_order_ids.add(broker_order_id)
    
    fills_from_rejected = [bid for bid in fills_unknown_order.keys() if bid in rejected_order_ids]
    if fills_from_rejected:
        print(f"[CRITICAL] {len(fills_from_rejected)} fills from rejected orders!")
        print("  Rejected orders are still filling in NinjaTrader")
        print("  These fills cannot be tracked because order was removed from _orderMap")

# Check order state
if fills_unknown_order:
    rejected_states = [info for info in fills_unknown_order.values() if 'REJECTED' in str(info['order_state']).upper()]
    if rejected_states:
        print(f"[CRITICAL] {len(rejected_states)} fills arrived when order was in REJECTED state")
        print("  Orders may be rejected but still fill in NinjaTrader SIM")

print()
