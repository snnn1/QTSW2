#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Detailed check of MNQ1 fills"""
import json
from pathlib import Path
from datetime import datetime, timezone, timedelta
from collections import defaultdict

recent = datetime.now(timezone.utc) - timedelta(hours=1)

print("=" * 100)
print("MNQ1 FILL DETAIL ANALYSIS")
print("=" * 100)
print()

# Find MNQ1 fills
fills = []
all_mnq_events = []

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
                                all_mnq_events.append(e)
                                
                                if 'FILL' in e.get('event', '').upper():
                                    fills.append(e)
                except:
                    pass
    except:
        pass

print(f"Found {len(fills)} fill events in last hour")
print(f"Found {len(all_mnq_events)} total MNQ events")
print()

if fills:
    print("1. FILL EVENT ANALYSIS")
    print("-" * 100)
    
    # Group by intent_id
    by_intent = defaultdict(list)
    by_order_type = defaultdict(int)
    total_quantity = 0
    
    for e in fills:
        data = e.get('data', {})
        intent_id = data.get('intent_id') or 'UNKNOWN'
        order_type = data.get('order_type') or 'UNKNOWN'
        quantity = data.get('quantity') or data.get('filled_quantity') or data.get('fill_quantity') or 0
        
        by_intent[intent_id].append(e)
        by_order_type[order_type] += 1
        
        if isinstance(quantity, (int, float)):
            total_quantity += abs(quantity)
    
    print(f"Unique Intents: {len(by_intent)}")
    print(f"Total Fill Quantity: {total_quantity}")
    print()
    
    print("Fills by Order Type:")
    for order_type, count in sorted(by_order_type.items(), key=lambda x: x[1], reverse=True):
        print(f"  {order_type}: {count}")
    print()
    
    # Check for rapid fills (same intent, multiple fills quickly)
    print("2. RAPID FILL PATTERNS")
    print("-" * 100)
    
    rapid_fills = []
    for intent_id, intent_fills in by_intent.items():
        if len(intent_fills) > 5:  # More than 5 fills for same intent
            rapid_fills.append((intent_id, intent_fills))
    
    if rapid_fills:
        print(f"Found {len(rapid_fills)} intent(s) with rapid fills (>5 fills):")
        print()
        
        for intent_id, intent_fills in rapid_fills[:5]:
            sorted_fills = sorted(intent_fills, key=lambda e: e.get('ts_utc', ''))
            first_ts = datetime.fromisoformat(sorted_fills[0].get('ts_utc', '').replace('Z', '+00:00'))
            last_ts = datetime.fromisoformat(sorted_fills[-1].get('ts_utc', '').replace('Z', '+00:00'))
            duration = (last_ts - first_ts).total_seconds()
            
            print(f"  Intent: {intent_id[:30]}...")
            print(f"    Fills: {len(intent_fills)}")
            print(f"    Duration: {duration:.1f} seconds")
            print(f"    Rate: {len(intent_fills)/max(duration, 1):.1f} fills/second")
            
            # Check quantities
            quantities = []
            directions = []
            for e in sorted_fills:
                data = e.get('data', {})
                qty = data.get('quantity') or data.get('filled_quantity') or data.get('fill_quantity') or 0
                direction = data.get('direction') or 'UNKNOWN'
                quantities.append(qty)
                directions.append(direction)
            
            print(f"    Quantities: {quantities[:10]}")
            print(f"    Directions: {set(directions)}")
            print()
    else:
        print("[OK] No rapid fill patterns detected")
    
    print()
    
    # Show recent fills with details
    print("3. RECENT FILLS (Last 10)")
    print("-" * 100)
    
    recent_fills = sorted(fills, key=lambda e: e.get('ts_utc', ''), reverse=True)[:10]
    for e in recent_fills:
        ts = e.get('ts_utc', '')[:19]
        data = e.get('data', {})
        intent_id = data.get('intent_id', 'N/A')[:30]
        order_type = data.get('order_type', 'N/A')
        quantity = data.get('quantity') or data.get('filled_quantity') or data.get('fill_quantity') or 'N/A'
        direction = data.get('direction', 'N/A')
        price = data.get('price') or data.get('fill_price') or 'N/A'
        
        print(f"  [{ts}] Intent: {intent_id}...")
        print(f"    Order Type: {order_type}")
        print(f"    Direction: {direction}")
        print(f"    Quantity: {quantity}")
        print(f"    Price: {price}")
        print()

# Check for position accumulation
print("4. POSITION ACCUMULATION CHECK")
print("-" * 100)

# Look for ENTRY fills and check if quantities are accumulating incorrectly
entry_fills = [e for e in fills if 'ENTRY' in str(e.get('data', {}).get('order_type', '')).upper()]
if entry_fills:
    print(f"Found {len(entry_fills)} entry fills")
    
    # Group by intent and check cumulative quantity
    by_intent_entry = defaultdict(list)
    for e in entry_fills:
        data = e.get('data', {})
        intent_id = data.get('intent_id') or 'UNKNOWN'
        by_intent_entry[intent_id].append(e)
    
    print(f"Unique entry intents: {len(by_intent_entry)}")
    print()
    
    for intent_id, fills_list in list(by_intent_entry.items())[:5]:
        sorted_fills = sorted(fills_list, key=lambda e: e.get('ts_utc', ''))
        print(f"  Intent: {intent_id[:30]}...")
        
        quantities = []
        filled_totals = []
        fill_quantities = []
        
        for e in sorted_fills:
            data = e.get('data', {})
            qty = data.get('quantity', 0)
            filled_total = data.get('filled_total') or data.get('filled_quantity_total', 0)
            fill_qty = data.get('fill_quantity') or data.get('filled_quantity', 0)
            
            quantities.append(qty)
            filled_totals.append(filled_total)
            fill_quantities.append(fill_qty)
        
        print(f"    Total fills: {len(sorted_fills)}")
        print(f"    Quantities: {quantities}")
        print(f"    Filled Totals: {filled_totals}")
        print(f"    Fill Quantities: {fill_quantities}")
        
        # Check if filled_total is accumulating incorrectly
        if filled_totals and len(set(filled_totals)) > 1:
            try:
                last_total = int(filled_totals[-1]) if isinstance(filled_totals[-1], str) else filled_totals[-1]
                if last_total > 10:
                    print(f"    [CRITICAL] Large cumulative quantity: {last_total}")
            except:
                pass
        print()
else:
    print("No entry fills found")

print()
print("=" * 100)
