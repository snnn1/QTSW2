#!/usr/bin/env python3
"""
Check NQ1 fill and protective order placement
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
    print("NQ1 FILL AND PROTECTIVE ORDER CHECK")
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
    
    # Filter for NQ1-related events
    nq1_events = []
    for evt in events:
        event_type = evt.get("event_type", "")
        stream = evt.get("stream", "")
        instrument = evt.get("instrument", "")
        intent_id = evt.get("intent_id", "")
        
        # Look for NQ1 stream or NQ instrument events
        if stream == "NQ1" or (instrument == "NQ" and ("fill" in event_type.lower() or "order" in event_type.lower() or "execution" in event_type.lower())):
            nq1_events.append(evt)
    
    # Also look for recent fill/order events
    fill_events = [e for e in events if "fill" in e.get("event_type", "").lower() or "execution" in e.get("event_type", "").lower()]
    order_events = [e for e in events if "order" in e.get("event_type", "").lower()]
    
    print(f"\n[RECENT FILL EVENTS] (last 10)")
    recent_fills = sorted([e for e in fill_events if e.get("ts_utc")], 
                          key=lambda x: x.get("ts_utc", ""))[-10:]
    for evt in reversed(recent_fills):
        ts_str = evt.get("ts_utc", "")
        ts = parse_timestamp(ts_str)
        if ts:
            chicago_time = ts.astimezone(CHICAGO_TZ)
            elapsed = (datetime.now(timezone.utc) - ts).total_seconds()
            event_type = evt.get("event_type", "")
            intent_id = evt.get("intent_id", "")
            instrument = evt.get("instrument", "")
            data = evt.get("data", {})
            
            print(f"\n  {chicago_time.strftime('%H:%M:%S')} CT ({elapsed:.0f}s ago)")
            print(f"    Event: {event_type}")
            print(f"    Intent ID: {intent_id}")
            print(f"    Instrument: {instrument}")
            if data:
                print(f"    Data: {json.dumps(data, indent=6)[:200]}")
    
    print(f"\n[PROTECTIVE ORDER EVENTS] (last 20)")
    protective_events = [e for e in order_events if "protective" in e.get("event_type", "").lower() or "stop" in e.get("event_type", "").lower() or "target" in e.get("event_type", "").lower()]
    recent_protective = sorted([e for e in protective_events if e.get("ts_utc")], 
                               key=lambda x: x.get("ts_utc", ""))[-20:]
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
                print(f"    Data: {json.dumps(data, indent=6)[:300]}")
    
    # Check for failures
    print(f"\n[ORDER SUBMISSION FAILURES] (last 10)")
    failures = [e for e in events if "fail" in e.get("event_type", "").lower() and "order" in e.get("event_type", "").lower()]
    recent_failures = sorted([e for e in failures if e.get("ts_utc")], 
                             key=lambda x: x.get("ts_utc", ""))[-10:]
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
                print(f"    Error: {error}")
                print(f"    Reason: {reason}")

if __name__ == "__main__":
    main()
