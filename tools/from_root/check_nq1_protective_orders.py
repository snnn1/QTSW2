#!/usr/bin/env python3
"""
Check NQ1 fill and why protective orders weren't placed
"""

import json
from pathlib import Path
from datetime import datetime, timezone
import pytz

CHICAGO_TZ = pytz.timezone("America/Chicago")
NQ_LOG_FILE = Path("logs/robot/robot_NQ.jsonl")

def parse_timestamp(ts_str):
    """Parse ISO timestamp."""
    if not ts_str:
        return None
    try:
        ts_str = ts_str.replace('Z', '+00:00')
        dt = datetime.fromisoformat(ts_str)
        if dt.tzinfo is None:
            dt = dt.replace(tzinfo=timezone.utc)
        return dt
    except:
        return None

def main():
    print("=" * 80)
    print("NQ1 FILL AND PROTECTIVE ORDER ANALYSIS")
    print("=" * 80)
    
    if not NQ_LOG_FILE.exists():
        print(f"[X] File not found: {NQ_LOG_FILE}")
        return
    
    # Read all events
    events = []
    try:
        with open(NQ_LOG_FILE, 'r', encoding='utf-8-sig') as f:
            for line in f:
                line = line.strip()
                if not line:
                    continue
                try:
                    events.append(json.loads(line))
                except:
                    continue
    except Exception as e:
        print(f"[X] Error reading file: {e}")
        return
    
    print(f"Total events in log: {len(events)}")
    
    # Find recent fill events
    fill_events = []
    for evt in events:
        event_type = evt.get("event_type", "").lower()
        if "fill" in event_type or "execution" in event_type:
            fill_events.append(evt)
    
    print(f"\n[FILL EVENTS] Found {len(fill_events)} fill events")
    
    # Show most recent fills
    if fill_events:
        recent_fills = sorted([e for e in fill_events if e.get("ts_utc")], 
                             key=lambda x: x.get("ts_utc", ""))[-5:]
        print("\nMost recent fills:")
        for evt in reversed(recent_fills):
            ts_str = evt.get("ts_utc", "")
            ts = parse_timestamp(ts_str)
            if ts:
                chicago_time = ts.astimezone(CHICAGO_TZ)
                elapsed = (datetime.now(timezone.utc) - ts).total_seconds()
                event_type = evt.get("event_type", "")
                intent_id = evt.get("intent_id", "")
                instrument = evt.get("instrument", "")
                stream = evt.get("stream", "")
                data = evt.get("data", {})
                
                print(f"\n  {chicago_time.strftime('%Y-%m-%d %H:%M:%S')} CT ({elapsed:.0f}s ago)")
                print(f"    Event: {event_type}")
                print(f"    Stream: {stream}")
                print(f"    Instrument: {instrument}")
                print(f"    Intent ID: {intent_id}")
                if data:
                    fill_price = data.get("fill_price") or data.get("price")
                    fill_quantity = data.get("fill_quantity") or data.get("quantity")
                    print(f"    Fill Price: {fill_price}")
                    print(f"    Fill Quantity: {fill_quantity}")
    
    # Find protective order events
    protective_events = []
    for evt in events:
        event_type = evt.get("event_type", "").lower()
        intent_id = evt.get("intent_id", "")
        if ("protective" in event_type or "stop" in event_type or "target" in event_type) and intent_id:
            protective_events.append(evt)
    
    print(f"\n[PROTECTIVE ORDER EVENTS] Found {len(protective_events)} events")
    
    if protective_events:
        recent_protective = sorted([e for e in protective_events if e.get("ts_utc")], 
                                  key=lambda x: x.get("ts_utc", ""))[-10:]
        print("\nMost recent protective order events:")
        for evt in reversed(recent_protective):
            ts_str = evt.get("ts_utc", "")
            ts = parse_timestamp(ts_str)
            if ts:
                chicago_time = ts.astimezone(CHICAGO_TZ)
                elapsed = (datetime.now(timezone.utc) - ts).total_seconds()
                event_type = evt.get("event_type", "")
                intent_id = evt.get("intent_id", "")
                data = evt.get("data", {})
                
                print(f"\n  {chicago_time.strftime('%H:%M:%S')} CT ({elapsed:.0f}s ago)")
                print(f"    Event: {event_type}")
                print(f"    Intent ID: {intent_id}")
                if data:
                    error = data.get("error") or data.get("reason")
                    success = data.get("success")
                    if error:
                        print(f"    Error: {error}")
                    if success is not None:
                        print(f"    Success: {success}")
    
    # Find order submission failures
    failure_events = []
    for evt in events:
        event_type = evt.get("event_type", "").lower()
        if "fail" in event_type and "order" in event_type:
            failure_events.append(evt)
    
    print(f"\n[ORDER SUBMISSION FAILURES] Found {len(failure_events)} failures")
    
    if failure_events:
        recent_failures = sorted([e for e in failure_events if e.get("ts_utc")], 
                                key=lambda x: x.get("ts_utc", ""))[-10:]
        print("\nMost recent failures:")
        for evt in reversed(recent_failures):
            ts_str = evt.get("ts_utc", "")
            ts = parse_timestamp(ts_str)
            if ts:
                chicago_time = ts.astimezone(CHICAGO_TZ)
                elapsed = (datetime.now(timezone.utc) - ts).total_seconds()
                event_type = evt.get("event_type", "")
                intent_id = evt.get("intent_id", "")
                data = evt.get("data", {})
                
                print(f"\n  {chicago_time.strftime('%H:%M:%S')} CT ({elapsed:.0f}s ago)")
                print(f"    Event: {event_type}")
                print(f"    Intent ID: {intent_id}")
                if data:
                    error = data.get("error", "")
                    reason = data.get("reason", "")
                    order_type = data.get("order_type", "")
                    print(f"    Order Type: {order_type}")
                    print(f"    Error: {error}")
                    print(f"    Reason: {reason}")
    
    # Check for execution errors
    execution_errors = []
    for evt in events:
        event_type = evt.get("event_type", "").lower()
        if "execution_error" in event_type:
            execution_errors.append(evt)
    
    print(f"\n[EXECUTION ERRORS] Found {len(execution_errors)} errors")
    
    if execution_errors:
        recent_errors = sorted([e for e in execution_errors if e.get("ts_utc")], 
                              key=lambda x: x.get("ts_utc", ""))[-10:]
        print("\nMost recent execution errors:")
        for evt in reversed(recent_errors):
            ts_str = evt.get("ts_utc", "")
            ts = parse_timestamp(ts_str)
            if ts:
                chicago_time = ts.astimezone(CHICAGO_TZ)
                elapsed = (datetime.now(timezone.utc) - ts).total_seconds()
                event_type = evt.get("event_type", "")
                intent_id = evt.get("intent_id", "")
                data = evt.get("data", {})
                
                print(f"\n  {chicago_time.strftime('%H:%M:%S')} CT ({elapsed:.0f}s ago)")
                print(f"    Event: {event_type}")
                print(f"    Intent ID: {intent_id}")
                if data:
                    error = data.get("error", "")
                    print(f"    Error: {error}")

if __name__ == "__main__":
    main()
