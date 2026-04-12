#!/usr/bin/env python3
"""
Check NQ1 break-even detection and stop modification
"""

import json
import sys
from pathlib import Path
from datetime import datetime
import pytz

def parse_timestamp(ts_str):
    """Parse timestamp string to datetime."""
    if not ts_str:
        return None
    try:
        if 'T' in ts_str or '+' in ts_str or 'Z' in ts_str:
            return datetime.fromisoformat(ts_str.replace('Z', '+00:00'))
    except:
        pass
    return None

def main():
    chicago_tz = pytz.timezone('America/Chicago')
    today = datetime.now(chicago_tz).date()
    today_str = today.strftime("%Y-%m-%d")
    
    print("="*80)
    print(f"NQ1 BREAK-EVEN DETECTION CHECK ({today_str})")
    print("="*80)
    
    log_dir = Path("logs/robot")
    nq1_events = []
    
    # Collect all NQ1 events today
    for log_file in log_dir.glob("robot_*.jsonl"):
        try:
            with open(log_file, 'r', encoding='utf-8') as f:
                for line in f:
                    line = line.strip()
                    if not line:
                        continue
                    try:
                        event = json.loads(line)
                        stream = event.get('stream', event.get('stream_id', ''))
                        trading_date = event.get('trading_date', '')
                        
                        if stream == 'NQ1' and trading_date == today_str:
                            ts = parse_timestamp(event.get('ts_utc', event.get('timestamp', '')))
                            if ts:
                                nq1_events.append((ts, event))
                    except:
                        pass
        except:
            pass
    
    nq1_events.sort(key=lambda x: x[0])
    
    print(f"\nFound {len(nq1_events)} NQ1 events today")
    
    # Look for key break-even related events
    print("\n" + "="*80)
    print("BREAK-EVEN RELATED EVENTS")
    print("="*80)
    
    be_events = []
    entry_events = []
    stop_submit_events = []
    stop_modify_events = []
    
    for ts, event in nq1_events:
        event_type = event.get('event_type', event.get('EventType', ''))
        event_str = json.dumps(event).lower()
        
        if 'break' in event_str and 'even' in event_str:
            be_events.append((ts, event))
        if 'entry' in event_type.lower() and 'fill' in event_type.lower():
            entry_events.append((ts, event))
        if 'stop' in event_type.lower() and 'submit' in event_type.lower():
            stop_submit_events.append((ts, event))
        if 'stop' in event_type.lower() and 'modif' in event_type.lower():
            stop_modify_events.append((ts, event))
        if 'be_trigger' in event_str:
            be_events.append((ts, event))
    
    print(f"\nEntry Fill Events: {len(entry_events)}")
    for ts, event in entry_events[-5:]:
        ts_chicago = ts.astimezone(chicago_tz)
        data = event.get('data', {})
        intent_id = data.get('intent_id', event.get('intent_id', 'N/A'))
        fill_price = data.get('fill_price', data.get('actual_fill_price', 'N/A'))
        print(f"  {ts_chicago.strftime('%H:%M:%S')} | Intent: {intent_id} | Fill: {fill_price}")
    
    print(f"\nStop Submit Events: {len(stop_submit_events)}")
    for ts, event in stop_submit_events[-5:]:
        ts_chicago = ts.astimezone(chicago_tz)
        data = event.get('data', {})
        intent_id = data.get('intent_id', event.get('intent_id', 'N/A'))
        stop_price = data.get('stop_price', 'N/A')
        print(f"  {ts_chicago.strftime('%H:%M:%S')} | Intent: {intent_id} | Stop: {stop_price}")
    
    print(f"\nStop Modify Events: {len(stop_modify_events)}")
    for ts, event in stop_modify_events[-5:]:
        ts_chicago = ts.astimezone(chicago_tz)
        data = event.get('data', {})
        intent_id = data.get('intent_id', event.get('intent_id', 'N/A'))
        new_stop = data.get('new_stop_price', data.get('be_stop_price', 'N/A'))
        print(f"  {ts_chicago.strftime('%H:%M:%S')} | Intent: {intent_id} | New Stop: {new_stop}")
    
    print(f"\nBreak-Even Events: {len(be_events)}")
    for ts, event in be_events:
        ts_chicago = ts.astimezone(chicago_tz)
        event_type = event.get('event_type', event.get('EventType', ''))
        data = event.get('data', {})
        intent_id = data.get('intent_id', event.get('intent_id', 'N/A'))
        be_trigger = data.get('be_trigger_price', 'N/A')
        be_stop = data.get('be_stop_price', 'N/A')
        tick_price = data.get('tick_price', 'N/A')
        print(f"  {ts_chicago.strftime('%H:%M:%S')} | {event_type}")
        print(f"    Intent: {intent_id} | BE Trigger: {be_trigger} | BE Stop: {be_stop} | Tick: {tick_price}")
    
    # Check for intent registration with BE trigger
    print("\n" + "="*80)
    print("INTENT REGISTRATION EVENTS (with BE trigger)")
    print("="*80)
    
    intent_events = []
    for ts, event in nq1_events:
        event_type = event.get('event_type', event.get('EventType', ''))
        if 'intent' in event_type.lower() and 'register' in event_type.lower():
            intent_events.append((ts, event))
    
    for ts, event in intent_events[-5:]:
        ts_chicago = ts.astimezone(chicago_tz)
        data = event.get('data', {})
        intent_id = data.get('intent_id', 'N/A')
        be_trigger = data.get('be_trigger', data.get('be_trigger_price', 'N/A'))
        entry_price = data.get('entry_price', 'N/A')
        direction = data.get('direction', 'N/A')
        print(f"  {ts_chicago.strftime('%H:%M:%S')} | Intent: {intent_id} | Dir: {direction} | Entry: {entry_price} | BE Trigger: {be_trigger}")
    
    # Check for OnMarketData calls
    print("\n" + "="*80)
    print("MARKET DATA / TICK EVENTS")
    print("="*80)
    
    tick_count = 0
    for ts, event in nq1_events:
        event_type = event.get('event_type', event.get('EventType', ''))
        if 'market' in event_type.lower() or 'tick' in event_type.lower():
            tick_count += 1
    
    print(f"Found {tick_count} market data/tick events")
    
    return 0

if __name__ == '__main__':
    sys.exit(main())
