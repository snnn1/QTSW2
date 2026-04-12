#!/usr/bin/env python3
"""Comprehensive logging check script"""
import requests
import json
from datetime import datetime, timedelta
from pathlib import Path

print("=" * 100)
print("COMPREHENSIVE LOGGING CHECK")
print("=" * 100)
print()

# 1. Check Watchdog Status
print("1. WATCHDOG STATUS")
print("-" * 100)
try:
    r = requests.get('http://localhost:8002/api/watchdog/status', timeout=5)
    status = r.json()
    print(f"  Engine Alive: {status.get('engine_alive')}")
    print(f"  Activity State: {status.get('engine_activity_state')}")
    print(f"  Last Engine Tick: {status.get('last_engine_tick_chicago')}")
    print(f"  Stall Detected: {status.get('engine_tick_stall_detected')}")
except Exception as e:
    print(f"  ERROR: {e}")
print()

# 2. Check Stream States
print("2. STREAM STATES")
print("-" * 100)
try:
    r = requests.get('http://localhost:8002/api/watchdog/stream-states', timeout=5)
    data = r.json()
    streams = data.get('streams', [])
    print(f"  Total Streams: {len(streams)}")
    for s in sorted(streams, key=lambda x: x.get('stream', '')):
        print(f"    {s.get('stream'):<6} | {s.get('instrument'):<4} | {s.get('state'):<20} | Exec: {s.get('data', {}).get('execution_instrument', 'N/A')}")
except Exception as e:
    print(f"  ERROR: {e}")
print()

# 3. Check Recent Events in Feed
print("3. RECENT EVENTS IN FRONTEND_FEED (Last 5 minutes)")
print("-" * 100)
feed_file = Path("logs/robot/frontend_feed.jsonl")
if feed_file.exists():
    cutoff = datetime.utcnow() - timedelta(minutes=5)
    event_counts = {}
    diagnostic_events = []
    
    with open(feed_file, 'r', encoding='utf-8-sig') as f:
        lines = f.readlines()
        for line in lines[-1000:]:  # Check last 1000 lines
            try:
                event = json.loads(line.strip())
                ts_str = event.get('timestamp_utc', '')
                if ts_str:
                    ts = datetime.fromisoformat(ts_str.replace('Z', '+00:00').replace('+00:00', ''))
                    if ts.replace(tzinfo=None) >= cutoff.replace(tzinfo=None):
                        event_type = event.get('event_type', '')
                        event_counts[event_type] = event_counts.get(event_type, 0) + 1
                        
                        if event_type in ('ONBARUPDATE_CALLED', 'ONBARUPDATE_DIAGNOSTIC', 'BAR_ROUTING_DIAGNOSTIC', 
                                         'EXECUTION_INSTRUMENT_OVERRIDE', 'RANGE_LOCKED', 'ORDER_SUBMITTED'):
                            diagnostic_events.append({
                                'timestamp': ts_str,
                                'event_type': event_type,
                                'stream': event.get('stream'),
                                'instrument': event.get('instrument') or event.get('data', {}).get('instrument'),
                                'execution_instrument': event.get('data', {}).get('execution_instrument'),
                            })
            except:
                pass
    
    print(f"  Event Counts:")
    for event_type, count in sorted(event_counts.items(), key=lambda x: x[1], reverse=True)[:10]:
        print(f"    {event_type}: {count}")
    
    print(f"\n  Diagnostic Events (Last 10):")
    for evt in diagnostic_events[-10:]:
        ts = evt.get('timestamp', '') or ''
        ts_display = ts[11:19] if len(ts) > 19 else ts or 'N/A'
        stream = evt.get('stream') or 'N/A'
        instrument = evt.get('instrument') or 'N/A'
        exec_inst = evt.get('execution_instrument') or 'N/A'
        print(f"    {ts_display} | {evt.get('event_type', 'N/A'):<30} | Stream: {stream:<6} | Inst: {instrument:<4} | Exec: {exec_inst}")
else:
    print("  Feed file not found")
print()

# 4. Check Robot Log Files for Diagnostic Events
print("4. DIAGNOSTIC EVENTS IN ROBOT LOGS (Last 20)")
print("-" * 100)
log_dir = Path("logs/robot")
if log_dir.exists():
    diagnostic_found = []
    for log_file in log_dir.glob("robot_*.jsonl"):
        try:
            with open(log_file, 'r', encoding='utf-8-sig') as f:
                lines = f.readlines()
                for line in lines[-100:]:
                    try:
                        event = json.loads(line.strip())
                        event_type = event.get('event', '')
                        if event_type in ('ONBARUPDATE_CALLED', 'ONBARUPDATE_DIAGNOSTIC', 'BAR_ROUTING_DIAGNOSTIC', 
                                         'EXECUTION_INSTRUMENT_OVERRIDE', 'RANGE_LOCKED'):
                            ts = event.get('ts_utc', '')
                            data = event.get('data', {})
                            diagnostic_found.append({
                                'timestamp': ts,
                                'event_type': event_type,
                                'instrument': data.get('instrument') or data.get('raw_instrument'),
                                'execution_instrument': data.get('execution_instrument'),
                                'stream': event.get('stream'),
                            })
                    except:
                        pass
        except:
            pass
    
    for evt in sorted(diagnostic_found, key=lambda x: x.get('timestamp') or '')[-20:]:
        ts = evt.get('timestamp') or 'N/A'
        ts_display = ts[11:19] if len(ts) > 19 else ts
        print(f"    {ts_display:<19} | {evt.get('event_type', 'N/A'):<30} | Inst: {evt.get('instrument', 'N/A') or 'N/A':<4} | Exec: {evt.get('execution_instrument', 'N/A') or 'N/A':<4} | Stream: {evt.get('stream', 'N/A') or 'N/A'}")
else:
    print("  Log directory not found")
print()

# 5. Check for Recent RANGE_LOCKED
print("5. RECENT RANGE_LOCKED EVENTS")
print("-" * 100)
if feed_file.exists():
    range_locked = []
    with open(feed_file, 'r', encoding='utf-8-sig') as f:
        lines = f.readlines()
        for line in lines[-2000:]:
            try:
                event = json.loads(line.strip())
                if event.get('event_type') == 'RANGE_LOCKED':
                    ts_str = event.get('timestamp_utc', '')
                    if ts_str:
                        ts = datetime.fromisoformat(ts_str.replace('Z', '+00:00').replace('+00:00', ''))
                        if ts.replace(tzinfo=None) >= cutoff.replace(tzinfo=None):
                            data = event.get('data', {})
                            range_locked.append({
                                'timestamp': ts_str,
                                'stream': event.get('stream'),
                                'execution_instrument': data.get('execution_instrument'),
                                'range_high': data.get('range_high'),
                                'range_low': data.get('range_low'),
                            })
            except:
                pass
    
    for rl in range_locked[-5:]:
        print(f"    {rl['timestamp'][11:19]} | {rl['stream']:<6} | Exec: {rl.get('execution_instrument', 'N/A'):<4} | Range: {rl.get('range_low')} - {rl.get('range_high')}")
    if not range_locked:
        print("    No RANGE_LOCKED events in last 5 minutes")
print()

# 6. Check for Recent Orders
print("6. RECENT ORDER_SUBMITTED EVENTS")
print("-" * 100)
if feed_file.exists():
    orders = []
    with open(feed_file, 'r', encoding='utf-8-sig') as f:
        lines = f.readlines()
        for line in lines[-2000:]:
            try:
                event = json.loads(line.strip())
                if event.get('event_type') == 'ORDER_SUBMITTED':
                    ts_str = event.get('timestamp_utc', '')
                    if ts_str:
                        ts = datetime.fromisoformat(ts_str.replace('Z', '+00:00').replace('+00:00', ''))
                        if ts.replace(tzinfo=None) >= cutoff.replace(tzinfo=None):
                            orders.append({
                                'timestamp': ts_str,
                                'instrument': event.get('instrument'),
                                'intent_id': event.get('data', {}).get('intent_id'),
                            })
            except:
                pass
    
    for order in orders[-5:]:
        print(f"    {order['timestamp'][11:19]} | Inst: {order.get('instrument', 'N/A'):<4} | Intent: {order.get('intent_id', 'N/A')[:8]}")
    if not orders:
        print("    No ORDER_SUBMITTED events in last 5 minutes")
print()

print("=" * 100)
