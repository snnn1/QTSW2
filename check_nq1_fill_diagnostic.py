#!/usr/bin/env python3
"""
Diagnostic: Check why NQ1 fill didn't trigger protective orders
"""

import json
from pathlib import Path
from datetime import datetime, timezone
import pytz

CHICAGO_TZ = pytz.timezone("America/Chicago")
NQ_LOG_FILE = Path("logs/robot/robot_NQ.jsonl")
ENGINE_LOG_FILE = Path("logs/robot/robot_ENGINE.jsonl")

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
    print("NQ1 FILL DIAGNOSTIC")
    print("=" * 80)
    
    # Read NQ log
    nq_events = []
    if NQ_LOG_FILE.exists():
        with open(NQ_LOG_FILE, 'r', encoding='utf-8-sig') as f:
            for line in f:
                if line.strip():
                    try:
                        nq_events.append(json.loads(line))
                    except:
                        pass
    
    # Read ENGINE log
    engine_events = []
    if ENGINE_LOG_FILE.exists():
        with open(ENGINE_LOG_FILE, 'r', encoding='utf-8-sig') as f:
            for line in f:
                if line.strip():
                    try:
                        engine_events.append(json.loads(line))
                    except:
                        pass
    
    print(f"NQ log events: {len(nq_events)}")
    print(f"ENGINE log events: {len(engine_events)}")
    
    # Find NQ1 stream events
    nq1_events = [e for e in nq_events if e.get('stream') == 'NQ1']
    print(f"\nNQ1 stream events: {len(nq1_events)}")
    
    # Show recent NQ1 events
    if nq1_events:
        recent_nq1 = sorted([e for e in nq1_events if e.get('ts_utc')], 
                           key=lambda x: x.get('ts_utc', ''))[-20:]
        print("\n[RECENT NQ1 EVENTS] (last 20)")
        for evt in reversed(recent_nq1):
            ts_str = evt.get('ts_utc', '')
            ts = parse_timestamp(ts_str)
            if ts:
                chicago_time = ts.astimezone(CHICAGO_TZ)
                elapsed = (datetime.now(timezone.utc) - ts).total_seconds()
                event_type = evt.get('event_type', evt.get('event', 'NO_TYPE'))
                intent_id = evt.get('intent_id', '')
                data = evt.get('data', {})
                
                print(f"\n  {chicago_time.strftime('%H:%M:%S')} CT ({elapsed:.0f}s ago)")
                print(f"    Event: {event_type}")
                if intent_id:
                    print(f"    Intent ID: {intent_id}")
                if data and len(str(data)) < 200:
                    print(f"    Data: {data}")
    
    # Check for execution-related events in ENGINE log
    execution_engine_events = [e for e in engine_events if 'execution' in e.get('event_type', '').lower() or 'fill' in e.get('event_type', '').lower()]
    print(f"\n[ENGINE EXECUTION EVENTS] Found {len(execution_engine_events)}")
    
    if execution_engine_events:
        recent_exec = sorted([e for e in execution_engine_events if e.get('ts_utc')], 
                            key=lambda x: x.get('ts_utc', ''))[-10:]
        print("\nMost recent execution events in ENGINE log:")
        for evt in reversed(recent_exec):
            ts_str = evt.get('ts_utc', '')
            ts = parse_timestamp(ts_str)
            if ts:
                chicago_time = ts.astimezone(CHICAGO_TZ)
                elapsed = (datetime.now(timezone.utc) - ts).total_seconds()
                event_type = evt.get('event_type', evt.get('event', 'NO_TYPE'))
                print(f"  {chicago_time.strftime('%H:%M:%S')} CT ({elapsed:.0f}s ago) - {event_type}")
    
    # Check for any events with "NQ" in them
    nq_related = [e for e in nq_events if 'nq' in str(e).lower() and ('fill' in str(e).lower() or 'execution' in str(e).lower() or 'order' in str(e).lower())]
    print(f"\n[NQ-RELATED FILL/ORDER EVENTS] Found {len(nq_related)}")
    
    if nq_related:
        recent_nq = sorted([e for e in nq_related if e.get('ts_utc')], 
                           key=lambda x: x.get('ts_utc', ''))[-10:]
        for evt in reversed(recent_nq):
            ts_str = evt.get('ts_utc', '')
            ts = parse_timestamp(ts_str)
            if ts:
                chicago_time = ts.astimezone(CHICAGO_TZ)
                elapsed = (datetime.now(timezone.utc) - ts).total_seconds()
                event_type = evt.get('event_type', evt.get('event', 'NO_TYPE'))
                print(f"  {chicago_time.strftime('%H:%M:%S')} CT ({elapsed:.0f}s ago) - {event_type}")

if __name__ == "__main__":
    main()
