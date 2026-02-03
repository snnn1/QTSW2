#!/usr/bin/env python3
"""Investigate CL2 position issue - limit order hit but position shows -6 instead of 0"""
import json
from pathlib import Path
from datetime import datetime, timezone, timedelta
from collections import defaultdict

print("=" * 100)
print("CL2 POSITION ISSUE INVESTIGATION")
print("=" * 100)
print()

today = datetime.now(timezone.utc)
yesterday = today - timedelta(days=1)

# Find CL2/CL related events
cl_events = []
position_events = []
fill_events = []
entry_events = []

for log_file in Path("logs/robot").glob("*.jsonl"):
    if 'CL' not in log_file.name.upper():
        continue
    
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
                                
                                # Filter for CL2 or CL
                                if 'CL2' in instrument.upper() or ('CL' in instrument.upper() and 'CL' in log_file.name.upper()):
                                    e['_log_file'] = log_file.name
                                    cl_events.append(e)
                                    
                                    # Categorize
                                    if 'POSITION' in event_type or 'FILL' in event_type or 'ENTRY' in event_type:
                                        if 'FILL' in event_type:
                                            fill_events.append(e)
                                        if 'ENTRY' in event_type:
                                            entry_events.append(e)
                                        if 'POSITION' in event_type:
                                            position_events.append(e)
                        except:
                            pass
                except:
                    pass
    except:
        pass

print(f"Found {len(cl_events)} CL2/CL events from last 24 hours")
print(f"  Fill events: {len(fill_events)}")
print(f"  Entry events: {len(entry_events)}")
print(f"  Position events: {len(position_events)}")
print()

# Group by intent
events_by_intent = defaultdict(list)
for e in cl_events:
    intent_id = e.get('data', {}).get('intent_id', 'UNKNOWN')
    events_by_intent[intent_id].append(e)

print("=" * 100)
print("RECENT CL2/CL EXECUTION EVENTS")
print("=" * 100)
print()

# Show recent events sorted by time
cl_events_sorted = sorted(cl_events, key=lambda x: x.get('ts_utc', ''))[-50:]  # Last 50

for e in cl_events_sorted:
    ts = e.get('ts_utc', '')[:19]
    event = e.get('event', 'N/A')
    data = e.get('data', {})
    intent_id = data.get('intent_id', 'N/A')[:20] if data.get('intent_id') else 'N/A'
    instrument = data.get('instrument', '') or data.get('execution_instrument', 'N/A')
    
    # Show key events
    if any(keyword in event.upper() for keyword in ['FILL', 'ENTRY', 'POSITION', 'LIMIT', 'ORDER', 'EXECUTION']):
        print(f"[{ts}] {event}")
        print(f"  Intent: {intent_id}...")
        print(f"  Instrument: {instrument}")
        
        # Show fill details
        if 'FILL' in event.upper():
            print(f"  Fill Price: {data.get('fill_price', data.get('price', 'N/A'))}")
            print(f"  Fill Quantity: {data.get('fill_quantity', data.get('quantity', data.get('qty', 'N/A')))}")
            print(f"  Filled Total: {data.get('filled_total', 'N/A')}")
            print(f"  Direction: {data.get('direction', 'N/A')}")
        
        # Show position details
        if 'POSITION' in event.upper():
            print(f"  Position: {data.get('position', data.get('qty', 'N/A'))}")
            print(f"  Market Position: {data.get('market_position', 'N/A')}")
        
        # Show order details
        if 'ORDER' in event.upper():
            print(f"  Order Type: {data.get('order_type', 'N/A')}")
            print(f"  Order Action: {data.get('order_action', 'N/A')}")
            print(f"  Quantity: {data.get('quantity', 'N/A')}")
        
        # Show errors
        if 'error' in str(data):
            print(f"  ERROR: {data.get('error', 'N/A')}")
        
        print()

print("=" * 100)
print("CHECKING FOR FILL QUANTITY ISSUES")
print("=" * 100)
print()

# Check for fill quantity mismatches
for e in fill_events:
    data = e.get('data', {})
    fill_qty = data.get('fill_quantity') or data.get('quantity') or data.get('qty')
    filled_total = data.get('filled_total')
    
    if fill_qty and filled_total:
        if abs(fill_qty) != abs(filled_total):
            ts = e.get('ts_utc', '')[:19]
            intent_id = data.get('intent_id', 'N/A')[:20] if data.get('intent_id') else 'N/A'
            print(f"[{ts}] Potential fill quantity mismatch:")
            print(f"  Intent: {intent_id}...")
            print(f"  Fill Quantity (delta): {fill_qty}")
            print(f"  Filled Total (cumulative): {filled_total}")
            print()

print("=" * 100)
print("CHECKING FOR POSITION TRACKING ISSUES")
print("=" * 100)
print()

# Look for position discrepancies
for intent_id, events in events_by_intent.items():
    if len(events) < 3:
        continue
    
    # Get fills and positions
    fills = [e for e in events if 'FILL' in e.get('event', '').upper()]
    positions = [e for e in events if 'POSITION' in e.get('event', '').upper()]
    
    if fills:
        print(f"Intent: {intent_id[:30]}...")
        print(f"  Fills: {len(fills)}")
        print(f"  Position events: {len(positions)}")
        
        # Show fill summary
        total_fill_qty = 0
        for f in fills:
            data = f.get('data', {})
            fill_qty = data.get('fill_quantity') or data.get('quantity') or data.get('qty', 0)
            direction = data.get('direction', '')
            ts = f.get('ts_utc', '')[:19]
            
            if isinstance(fill_qty, (int, float)):
                if direction == 'Long':
                    total_fill_qty += fill_qty
                elif direction == 'Short':
                    total_fill_qty -= fill_qty
                else:
                    total_fill_qty += fill_qty  # Assume positive = long
                
                print(f"    [{ts}] Fill: {fill_qty} ({direction})")
        
        print(f"  Expected Position: {total_fill_qty}")
        
        # Show actual positions
        for p in positions[-3:]:
            data = p.get('data', {})
            pos = data.get('position') or data.get('qty')
            ts = p.get('ts_utc', '')[:19]
            print(f"    [{ts}] Actual Position: {pos}")
        
        if abs(total_fill_qty) > 0 and len(positions) > 0:
            last_pos = positions[-1].get('data', {}).get('position') or positions[-1].get('data', {}).get('qty')
            if last_pos and abs(total_fill_qty - last_pos) > 0.1:
                print(f"  ⚠️  POSITION MISMATCH: Expected {total_fill_qty}, Got {last_pos}")
        print()

print("=" * 100)
