#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Deep analysis of MNQ1 position accumulation issue"""
import json
from pathlib import Path
from datetime import datetime, timezone, timedelta
from collections import defaultdict

recent = datetime.now(timezone.utc) - timedelta(hours=2)

print("=" * 100)
print("MNQ1 DEEP ROOT CAUSE ANALYSIS")
print("=" * 100)
print()

# Track all events
all_events = []
intent_events = defaultdict(list)
fill_events = []
protective_order_events = []
coordinator_events = []

print("1. COLLECTING ALL MNQ EVENTS")
print("-" * 100)

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
                            stream = e.get('stream', '') or (e.get('data', {}) or {}).get('stream', '')
                            instrument = e.get('instrument', '') or (e.get('data', {}) or {}).get('instrument', '')
                            
                            if 'MNQ' in str(instrument).upper():
                                all_events.append(e)
                                
                                event_type = e.get('event', '')
                                intent_id = (e.get('data', {}) or {}).get('intent_id', '')
                                
                                if intent_id:
                                    intent_events[intent_id].append(e)
                                
                                if 'FILL' in event_type.upper():
                                    fill_events.append(e)
                                
                                if any(k in event_type.upper() for k in ['PROTECTIVE', 'STOP', 'TARGET']):
                                    protective_order_events.append(e)
                                
                                if 'COORDINATOR' in event_type.upper() or 'EXPOSURE' in event_type.upper():
                                    coordinator_events.append(e)
                except:
                    pass
    except:
        pass

print(f"Total MNQ events: {len(all_events)}")
print(f"Unique intents: {len(intent_events)}")
print(f"Fill events: {len(fill_events)}")
print(f"Protective order events: {len(protective_order_events)}")
print(f"Coordinator events: {len(coordinator_events)}")
print()

# Analyze intents
print("2. INTENT ANALYSIS")
print("-" * 100)

for intent_id, events in list(intent_events.items())[:5]:
    print(f"\nIntent: {intent_id[:40]}...")
    print(f"  Total events: {len(events)}")
    
    # Check for registration
    registered = [e for e in events if 'REGISTER' in e.get('event', '').upper()]
    if registered:
        reg_data = registered[0].get('data', {})
        print(f"  Registered: {registered[0].get('ts_utc', '')[:19]}")
        print(f"    Instrument: {reg_data.get('instrument', 'N/A')}")
        print(f"    Stream: {reg_data.get('stream', 'N/A')}")
        print(f"    Direction: {reg_data.get('direction', 'N/A')}")
        print(f"    Quantity: {reg_data.get('quantity', 'N/A')}")
    
    # Check fills
    fills = [e for e in events if 'FILL' in e.get('event', '').upper()]
    print(f"  Fills: {len(fills)}")
    
    if fills:
        fill_quantities = []
        filled_totals = []
        for f in fills:
            data = f.get('data', {})
            fill_qty = data.get('fill_quantity') or data.get('quantity') or 0
            filled_total = data.get('filled_total') or data.get('filled_quantity_total') or 0
            fill_quantities.append(fill_qty)
            filled_totals.append(filled_total)
        
        print(f"    Fill quantities: {fill_quantities[:10]}")
        print(f"    Filled totals: {filled_totals[:10]}")
        
        # Check if filled_total is accumulating correctly
        if filled_totals:
            try:
                totals_numeric = [int(t) if isinstance(t, (int, str)) and str(t).isdigit() else 0 for t in filled_totals]
                if totals_numeric:
                    print(f"    Max filled_total: {max(totals_numeric)}")
                    if max(totals_numeric) > 10:
                        print(f"    [WARNING] Large cumulative quantity!")
            except:
                pass
    
    # Check protective orders
    protective = [e for e in events if any(k in e.get('event', '').upper() for k in ['PROTECTIVE', 'STOP', 'TARGET'])]
    print(f"  Protective order events: {len(protective)}")
    
    if protective:
        protective_quantities = []
        for p in protective:
            data = p.get('data', {})
            qty = data.get('quantity') or data.get('protected_quantity') or data.get('fill_quantity') or 0
            protective_quantities.append(qty)
        print(f"    Protective quantities: {protective_quantities[:10]}")

print()

# Analyze fill pattern
print("3. FILL PATTERN ANALYSIS")
print("-" * 100)

# Group fills by intent
fills_by_intent = defaultdict(list)
for e in fill_events:
    intent_id = (e.get('data', {}) or {}).get('intent_id', 'UNKNOWN')
    fills_by_intent[intent_id].append(e)

print(f"Fills by intent:")
for intent_id, fills in fills_by_intent.items():
    print(f"\n  Intent: {intent_id[:40]}...")
    print(f"    Total fills: {len(fills)}")
    
    # Sort by time
    sorted_fills = sorted(fills, key=lambda e: e.get('ts_utc', ''))
    
    # Check timing
    if len(sorted_fills) > 1:
        first_ts = datetime.fromisoformat(sorted_fills[0].get('ts_utc', '').replace('Z', '+00:00'))
        last_ts = datetime.fromisoformat(sorted_fills[-1].get('ts_utc', '').replace('Z', '+00:00'))
        duration = (last_ts - first_ts).total_seconds()
        print(f"    Duration: {duration:.1f} seconds")
        print(f"    Rate: {len(fills)/max(duration, 1):.2f} fills/second")
    
    # Check quantities
    quantities = []
    filled_totals = []
    for f in sorted_fills[:20]:  # First 20
        data = f.get('data', {})
        qty = data.get('fill_quantity') or data.get('quantity') or 0
        total = data.get('filled_total') or data.get('filled_quantity_total') or 0
        quantities.append(qty)
        filled_totals.append(total)
    
    print(f"    First 20 fill quantities: {quantities}")
    print(f"    First 20 filled totals: {filled_totals}")

print()

# Check for HandleEntryFill calls
print("4. HANDLEENTRYFILL CALLS")
print("-" * 100)

handle_entry_fill_calls = []
for e in all_events:
    event_type = e.get('event', '')
    if 'PROTECTIVE' in event_type.upper() and 'SUBMITTED' in event_type.upper():
        handle_entry_fill_calls.append(e)

print(f"Protective orders submitted events: {len(handle_entry_fill_calls)}")

if handle_entry_fill_calls:
    print("\nRecent protective order submissions:")
    for e in handle_entry_fill_calls[-10:]:
        ts = e.get('ts_utc', '')[:19]
        data = e.get('data', {})
        intent_id = data.get('intent_id', 'N/A')[:30]
        qty = data.get('quantity') or data.get('protected_quantity') or data.get('fill_quantity') or 'N/A'
        total_qty = data.get('total_filled_quantity', 'N/A')
        print(f"  [{ts}] Intent: {intent_id}...")
        print(f"    Quantity: {qty}, Total: {total_qty}")

print()

# Check for multiple intents
print("5. MULTIPLE INTENT CHECK")
print("-" * 100)

if len(intent_events) > 1:
    print(f"[WARNING] Multiple intents found: {len(intent_events)}")
    print("This could cause position accumulation if multiple intents are filling simultaneously")
    
    for intent_id, events in list(intent_events.items())[:5]:
        fills = [e for e in events if 'FILL' in e.get('event', '').upper()]
        if fills:
            print(f"\n  Intent: {intent_id[:40]}...")
            print(f"    Fills: {len(fills)}")
            first_fill = fills[0].get('ts_utc', '')[:19]
            last_fill = fills[-1].get('ts_utc', '')[:19]
            print(f"    First fill: {first_fill}")
            print(f"    Last fill: {last_fill}")
else:
    print(f"[OK] Single intent: {len(intent_events)}")

print()

# Check execution journal
print("6. EXECUTION JOURNAL CHECK")
print("-" * 100)

journal_files = list(Path("data/execution_journal").glob("**/*.json")) if Path("data/execution_journal").exists() else []
print(f"Journal files found: {len(journal_files)}")

if journal_files:
    for journal_file in journal_files[-5:]:  # Last 5
        try:
            with open(journal_file, 'r', encoding='utf-8') as f:
                journal_data = json.load(f)
                if isinstance(journal_data, dict):
                    entry_filled_qty = journal_data.get('EntryFilledQuantityTotal', 0)
                    if entry_filled_qty > 0:
                        print(f"\n  {journal_file.name}:")
                        print(f"    EntryFilledQuantityTotal: {entry_filled_qty}")
                        print(f"    IntentId: {journal_data.get('IntentId', 'N/A')[:40]}")
        except:
            pass

print()

# Check for order tracking issues
print("7. ORDER TRACKING CHECK")
print("-" * 100)

order_map_events = [e for e in all_events if 'ORDER' in e.get('event', '').upper() and 'MAP' in e.get('event', '').upper()]
print(f"Order map events: {len(order_map_events)}")

# Check for duplicate fills
print("\n8. DUPLICATE FILL CHECK")
print("-" * 100)

fill_signatures = defaultdict(list)
for e in fill_events:
    data = e.get('data', {})
    broker_order_id = data.get('broker_order_id', '')
    fill_price = data.get('fill_price') or data.get('price', '')
    fill_qty = data.get('fill_quantity') or data.get('quantity', '')
    ts = e.get('ts_utc', '')
    
    # Create signature
    sig = f"{broker_order_id}_{fill_price}_{fill_qty}_{ts}"
    fill_signatures[sig].append(e)

duplicates = {sig: events for sig, events in fill_signatures.items() if len(events) > 1}
if duplicates:
    print(f"[WARNING] Found {len(duplicates)} potential duplicate fills")
    for sig, events in list(duplicates.items())[:5]:
        print(f"  Signature: {sig[:60]}...")
        print(f"    Occurrences: {len(events)}")
else:
    print("[OK] No duplicate fills detected")

print()

# Summary
print("=" * 100)
print("SUMMARY")
print("=" * 100)
print()

if len(intent_events) > 1:
    print(f"[CRITICAL] Multiple intents ({len(intent_events)}) - could cause accumulation")
else:
    print(f"[OK] Single intent")

if len(fill_events) > 100:
    print(f"[WARNING] High fill count ({len(fill_events)}) - check for rapid fills")
else:
    print(f"[OK] Fill count: {len(fill_events)}")

if duplicates:
    print(f"[CRITICAL] Duplicate fills detected - could cause double-counting")
else:
    print("[OK] No duplicate fills")

print()
