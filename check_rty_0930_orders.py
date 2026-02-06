#!/usr/bin/env python3
"""
Check RTY orders around 09:30 to find why extra order was taken
"""
import json
import datetime
from pathlib import Path
from collections import defaultdict

def load_rty_events_today():
    """Load RTY events from today"""
    events = []
    log_file = Path("logs/robot/robot_RTY.jsonl")
    
    if not log_file.exists():
        print(f"[ERROR] Log file not found: {log_file}")
        return []
    
    today_start = datetime.datetime.now(datetime.timezone.utc).replace(
        hour=0, minute=0, second=0, microsecond=0
    )
    
    try:
        with open(log_file, 'r', encoding='utf-8-sig', errors='ignore') as f:
            for line in f:
                line = line.strip()
                if not line:
                    continue
                try:
                    event = json.loads(line)
                    if 'ts_utc' in event:
                        ts = datetime.datetime.fromisoformat(
                            event['ts_utc'].replace('Z', '+00:00')
                        )
                        if ts >= today_start:
                            events.append(event)
                except json.JSONDecodeError:
                    continue
    except Exception as e:
        print(f"[ERROR] Error reading log file: {e}")
        return []
    
    return sorted(events, key=lambda x: x.get('ts_utc', ''))

def find_0930_events(events):
    """Find events around 09:30"""
    target_time = datetime.datetime.now(datetime.timezone.utc).replace(
        hour=14, minute=30, second=0, microsecond=0  # 09:30 CT = 14:30 UTC
    )
    
    window_start = target_time - datetime.timedelta(minutes=5)
    window_end = target_time + datetime.timedelta(minutes=5)
    
    relevant = []
    for event in events:
        if 'ts_utc' in event:
            ts = datetime.datetime.fromisoformat(
                event['ts_utc'].replace('Z', '+00:00')
            )
            if window_start <= ts <= window_end:
                relevant.append(event)
    
    return relevant

def analyze_orders(events):
    """Analyze order-related events"""
    print("\n" + "="*80)
    print("ORDER ANALYSIS - RTY around 09:30")
    print("="*80)
    
    # Find intent registrations
    intents = [e for e in events if 'INTENT_REGISTERED' in e.get('event', '')]
    print(f"\nIntent Registrations: {len(intents)}")
    for intent in intents:
        ts = intent.get('ts_utc', '')[:19]
        intent_id = intent.get('data', {}).get('intent_id', 'UNKNOWN')[:16]
        direction = intent.get('data', {}).get('direction', 'UNKNOWN')
        entry_price = intent.get('data', {}).get('entry_price', '?')
        stream = intent.get('stream', 'UNKNOWN')
        print(f"   [{ts}] Intent: {intent_id} | Direction: {direction} | Entry: {entry_price} | Stream: {stream}")
    
    # Find order submissions
    orders = [e for e in events if 'ORDER' in e.get('event', '') and 'SUBMIT' in e.get('event', '')]
    print(f"\nOrder Submissions: {len(orders)}")
    for order in orders:
        ts = order.get('ts_utc', '')[:19]
        event_type = order.get('event', 'UNKNOWN')
        intent_id = order.get('data', {}).get('intent_id', 'UNKNOWN')[:16]
        order_type = order.get('data', {}).get('order_type', 'UNKNOWN')
        price = order.get('data', {}).get('price', '?')
        quantity = order.get('data', {}).get('quantity', '?')
        print(f"   [{ts}] {event_type} | Intent: {intent_id} | Type: {order_type} | Price: {price} | Qty: {quantity}")
    
    # Find fills
    fills = [e for e in events if 'FILL' in e.get('event', '')]
    print(f"\nFill Events: {len(fills)}")
    for fill in fills:
        ts = fill.get('ts_utc', '')[:19]
        event_type = fill.get('event', 'UNKNOWN')
        intent_id = fill.get('data', {}).get('intent_id', 'UNKNOWN')[:16]
        fill_price = fill.get('data', {}).get('fill_price', fill.get('data', {}).get('price', '?'))
        fill_qty = fill.get('data', {}).get('fill_quantity', fill.get('data', {}).get('quantity', '?'))
        order_type = fill.get('data', {}).get('order_type', 'UNKNOWN')
        print(f"   [{ts}] {event_type} | Intent: {intent_id} | Type: {order_type} | Price: {fill_price} | Qty: {fill_qty}")
    
    # Find entry detection events
    entry_detections = [e for e in events if 'ENTRY' in e.get('event', '') and 'DETECT' in e.get('event', '')]
    print(f"\nEntry Detection Events: {len(entry_detections)}")
    for ed in entry_detections:
        ts = ed.get('ts_utc', '')[:19]
        event_type = ed.get('event', 'UNKNOWN')
        stream = ed.get('stream', 'UNKNOWN')
        direction = ed.get('data', {}).get('direction', 'UNKNOWN')
        price = ed.get('data', {}).get('price', ed.get('data', {}).get('breakout_level', '?'))
        print(f"   [{ts}] {event_type} | Stream: {stream} | Direction: {direction} | Price: {price}")
    
    # Find range/state events
    state_events = [e for e in events if 'STATE' in e.get('event', '') or 'RANGE' in e.get('event', '')]
    print(f"\nState/Range Events: {len(state_events)}")
    for se in state_events[:10]:
        ts = se.get('ts_utc', '')[:19]
        event_type = se.get('event', 'UNKNOWN')
        state = se.get('data', {}).get('state', se.get('data', {}).get('new_state', '?'))
        stream = se.get('stream', 'UNKNOWN')
        print(f"   [{ts}] {event_type} | Stream: {stream} | State: {state}")
    
    return {
        'intents': intents,
        'orders': orders,
        'fills': fills,
        'entry_detections': entry_detections
    }

def check_duplicate_orders(analysis):
    """Check for duplicate or unexpected orders"""
    print("\n" + "="*80)
    print("DUPLICATE/UNEXPECTED ORDER CHECK")
    print("="*80)
    
    # Group intents by stream
    intents_by_stream = defaultdict(list)
    for intent in analysis['intents']:
        stream = intent.get('stream', 'UNKNOWN')
        intents_by_stream[stream].append(intent)
    
    print(f"\nIntents by Stream:")
    for stream, intents in intents_by_stream.items():
        print(f"   {stream}: {len(intents)} intents")
        for intent in intents:
            intent_id = intent.get('data', {}).get('intent_id', 'UNKNOWN')[:16]
            direction = intent.get('data', {}).get('direction', 'UNKNOWN')
            ts = intent.get('ts_utc', '')[:19]
            print(f"      [{ts}] {intent_id} | {direction}")
    
    # Group orders by intent
    orders_by_intent = defaultdict(list)
    for order in analysis['orders']:
        intent_id = order.get('data', {}).get('intent_id', 'UNKNOWN')
        orders_by_intent[intent_id].append(order)
    
    print(f"\nOrders by Intent:")
    for intent_id, orders in orders_by_intent.items():
        print(f"   {intent_id[:16]}: {len(orders)} orders")
        for order in orders:
            ts = order.get('ts_utc', '')[:19]
            order_type = order.get('data', {}).get('order_type', 'UNKNOWN')
            print(f"      [{ts}] {order_type}")
    
    # Check for multiple entry orders
    entry_orders = [o for o in analysis['orders'] if 'ENTRY' in o.get('data', {}).get('order_type', '')]
    print(f"\nEntry Orders: {len(entry_orders)}")
    if len(entry_orders) > 2:
        print(f"   [WARNING] Found {len(entry_orders)} entry orders (expected 2)")
        for order in entry_orders:
            ts = order.get('ts_utc', '')[:19]
            intent_id = order.get('data', {}).get('intent_id', 'UNKNOWN')[:16]
            stream = order.get('stream', 'UNKNOWN')
            print(f"      [{ts}] Intent: {intent_id} | Stream: {stream}")

def main():
    print("="*80)
    print("RTY 09:30 ORDER INVESTIGATION")
    print("="*80)
    
    events = load_rty_events_today()
    print(f"\nLoaded {len(events)} RTY events from today")
    
    if not events:
        print("\n[WARNING] No RTY events found today")
        return
    
    # Find events around 09:30
    relevant_events = find_0930_events(events)
    print(f"\nEvents around 09:30 (14:30 UTC): {len(relevant_events)}")
    
    if not relevant_events:
        print("\n[INFO] No events found around 09:30")
        print("   Checking all events from last hour...")
        now = datetime.datetime.now(datetime.timezone.utc)
        hour_ago = now - datetime.timedelta(hours=1)
        relevant_events = [e for e in events if 'ts_utc' in e and 
                          datetime.datetime.fromisoformat(e['ts_utc'].replace('Z', '+00:00')) >= hour_ago]
        print(f"   Found {len(relevant_events)} events in last hour")
    
    # Analyze orders
    analysis = analyze_orders(relevant_events)
    
    # Check for duplicates
    check_duplicate_orders(analysis)
    
    print("\n" + "="*80)
    print("SUMMARY")
    print("="*80)
    print(f"\nIntents: {len(analysis['intents'])}")
    print(f"Orders: {len(analysis['orders'])}")
    print(f"Fills: {len(analysis['fills'])}")
    print(f"Entry Detections: {len(analysis['entry_detections'])}")

if __name__ == "__main__":
    main()
