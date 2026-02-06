#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Check intent resolution and protective order submission"""
import json
from pathlib import Path
from datetime import datetime, timezone, timedelta
from collections import defaultdict

recent = datetime.now(timezone.utc) - timedelta(hours=2)

print("=" * 100)
print("INTENT RESOLUTION AND PROTECTIVE ORDER ANALYSIS")
print("=" * 100)
print()

# Track events
fills = []
intent_registered = []
protective_submitted = []
intent_resolution_failures = []
order_tracking = defaultdict(list)

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
                            instrument = e.get('instrument', '') or (e.get('data', {}) or {}).get('instrument', '')
                            
                            if 'MNQ' in str(instrument).upper():
                                event_type = e.get('event', '')
                                data = e.get('data', {}) or {}
                                intent_id = data.get('intent_id', '')
                                
                                if 'FILL' in event_type.upper():
                                    fills.append(e)
                                
                                if 'INTENT_REGISTERED' in event_type.upper():
                                    intent_registered.append(e)
                                
                                if 'PROTECTIVE' in event_type.upper() and 'SUBMITTED' in event_type.upper():
                                    protective_submitted.append(e)
                                
                                if 'RESOLUTION' in event_type.upper() or 'RESOLVE' in event_type.upper():
                                    if 'FAIL' in event_type.upper() or 'ERROR' in event_type.upper():
                                        intent_resolution_failures.append(e)
                                
                                if 'ORDER' in event_type.upper():
                                    broker_order_id = data.get('broker_order_id') or data.get('order_id', '')
                                    if broker_order_id:
                                        order_tracking[broker_order_id].append(e)
                except:
                    pass
    except:
        pass

print("1. INTENT REGISTRATION")
print("-" * 100)
print(f"Intent registrations: {len(intent_registered)}")
for reg in intent_registered:
    data = reg.get('data', {})
    print(f"  [{reg.get('ts_utc', '')[:19]}] Intent: {data.get('intent_id', 'N/A')[:40]}...")
    print(f"    Instrument: {data.get('instrument', 'N/A')}")
    print(f"    Stream: {data.get('stream', 'N/A')}")
    print()

print("2. FILL ANALYSIS - INTENT RESOLUTION")
print("-" * 100)

fills_by_intent = defaultdict(list)
fills_unknown = []

for f in fills:
    data = f.get('data', {})
    intent_id = data.get('intent_id', '')
    broker_order_id = data.get('broker_order_id', '')
    
    if not intent_id or intent_id == 'UNKNOWN':
        fills_unknown.append(f)
    else:
        fills_by_intent[intent_id].append(f)

print(f"Total fills: {len(fills)}")
print(f"Fills with valid intent_id: {sum(len(v) for v in fills_by_intent.values())}")
print(f"Fills with UNKNOWN/missing intent_id: {len(fills_unknown)}")
print()

if fills_unknown:
    print("[CRITICAL] Fills without proper intent_id:")
    print(f"  Count: {len(fills_unknown)}")
    print()
    
    # Check first few
    for f in fills_unknown[:5]:
        ts = f.get('ts_utc', '')[:19]
        data = f.get('data', {})
        broker_order_id = data.get('broker_order_id', 'N/A')
        order_type = data.get('order_type', 'N/A')
        print(f"  [{ts}] Broker Order ID: {broker_order_id}")
        print(f"    Order Type: {order_type}")
        print(f"    Fill Quantity: {data.get('fill_quantity') or data.get('quantity', 'N/A')}")
        print()

print("3. PROTECTIVE ORDER SUBMISSION")
print("-" * 100)
print(f"Protective orders submitted: {len(protective_submitted)}")
print()

if len(protective_submitted) < len(fills) / 10:
    print("[CRITICAL] Very few protective orders submitted!")
    print(f"  Fills: {len(fills)}")
    print(f"  Protective orders: {len(protective_submitted)}")
    print(f"  Ratio: {len(protective_submitted)/max(len(fills), 1):.2%}")
    print()

if protective_submitted:
    print("Recent protective order submissions:")
    for p in protective_submitted[-10:]:
        ts = p.get('ts_utc', '')[:19]
        data = p.get('data', {})
        intent_id = data.get('intent_id', 'N/A')[:30]
        qty = data.get('quantity') or data.get('protected_quantity') or data.get('fill_quantity', 'N/A')
        print(f"  [{ts}] Intent: {intent_id}..., Quantity: {qty}")
    print()

print("4. INTENT RESOLUTION FAILURES")
print("-" * 100)
print(f"Resolution failures: {len(intent_resolution_failures)}")
if intent_resolution_failures:
    for f in intent_resolution_failures[:5]:
        ts = f.get('ts_utc', '')[:19]
        data = f.get('data', {})
        error = data.get('error') or data.get('message', 'N/A')
        print(f"  [{ts}] {error[:100]}")
    print()

print("5. ORDER TRACKING")
print("-" * 100)
print(f"Unique broker order IDs: {len(order_tracking)}")

# Check for orders that filled but weren't tracked
fills_with_order_id = [f for f in fills if (f.get('data', {}) or {}).get('broker_order_id')]
print(f"Fills with broker_order_id: {len(fills_with_order_id)}")

# Check if order IDs match between fills and tracking
fill_order_ids = set((f.get('data', {}) or {}).get('broker_order_id', '') for f in fills_with_order_id)
tracked_order_ids = set(order_tracking.keys())
missing_tracking = fill_order_ids - tracked_order_ids

if missing_tracking:
    print(f"[WARNING] {len(missing_tracking)} order IDs in fills but not in tracking")
    print(f"  Sample: {list(missing_tracking)[:5]}")

print()

print("6. CHECK FOR HANDLEENTRYFILL CALLS")
print("-" * 100)

# Look for events that indicate HandleEntryFill was called
handle_entry_fill_indicators = []
for e in fills:
    # Check if there are protective order events after this fill
    fill_ts = e.get('ts_utc', '')
    fill_intent = (e.get('data', {}) or {}).get('intent_id', '')
    
    # Look for protective orders within 5 seconds
    for p in protective_submitted:
        p_ts = p.get('ts_utc', '')
        p_intent = (p.get('data', {}) or {}).get('intent_id', '')
        
        if fill_intent == p_intent and fill_intent:
            fill_dt = datetime.fromisoformat(fill_ts.replace('Z', '+00:00'))
            p_dt = datetime.fromisoformat(p_ts.replace('Z', '+00:00'))
            if abs((p_dt - fill_dt).total_seconds()) < 5:
                handle_entry_fill_indicators.append((e, p))
                break

print(f"Fills with protective orders submitted within 5s: {len(handle_entry_fill_indicators)}")
print(f"Total fills: {len(fills)}")
print(f"Coverage: {len(handle_entry_fill_indicators)/max(len(fills), 1):.2%}")

if len(handle_entry_fill_indicators) < len(fills) / 2:
    print("[CRITICAL] Most fills don't have protective orders submitted!")
    print("This suggests HandleEntryFill is not being called or is failing silently")

print()

print("=" * 100)
print("SUMMARY")
print("=" * 100)
print()

if fills_unknown:
    print(f"[CRITICAL] {len(fills_unknown)} fills with UNKNOWN/missing intent_id")
    print("  → Intent resolution is failing")
    print("  → Protective orders cannot be submitted without intent_id")
else:
    print("[OK] All fills have valid intent_id")

if len(protective_submitted) < len(fills) / 10:
    print(f"[CRITICAL] Only {len(protective_submitted)} protective orders for {len(fills)} fills")
    print("  → Protective orders are not being submitted")
    print("  → Positions are unprotected")
else:
    print(f"[OK] Protective orders submitted: {len(protective_submitted)}")

if intent_resolution_failures:
    print(f"[CRITICAL] {len(intent_resolution_failures)} intent resolution failures")
else:
    print("[OK] No intent resolution failures")

print()
