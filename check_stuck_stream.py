#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Check for stuck streams from previous trading dates"""
import json
from pathlib import Path
from datetime import datetime, timezone, timedelta
import pytz

CHICAGO_TZ = pytz.timezone("America/Chicago")

print("=" * 100)
print("STUCK STREAM CHECK - RTY2 from Yesterday")
print("=" * 100)
print()

# Get current trading date
chicago_now = datetime.now(CHICAGO_TZ)
current_date = chicago_now.strftime("%Y-%m-%d")
print(f"Current Date: {current_date}")
print(f"Chicago Time: {chicago_now.strftime('%Y-%m-%d %H:%M:%S %Z')}")
print()

# Check robot logs for RTY2 stream events
print("1. Checking Robot Logs for RTY2 Stream")
print("-" * 100)

rty_logs = list(Path("logs/robot").glob("robot_RTY*.jsonl"))
if not rty_logs:
    print("No RTY log files found")
else:
    print(f"Found {len(rty_logs)} RTY log file(s)")
    
    # Look for RTY2 stream events
    rty2_events = []
    for log_file in rty_logs:
        try:
            with open(log_file, 'r', encoding='utf-8-sig') as f:
                for line in f:
                    try:
                        e = json.loads(line.strip())
                        stream = e.get('stream', '')
                        if 'RTY2' in stream.upper():
                            rty2_events.append(e)
                    except:
                        pass
        except:
            pass
    
    if rty2_events:
        print(f"Found {len(rty2_events)} RTY2 stream events")
        
        # Group by event type
        by_type = {}
        for e in rty2_events:
            event_type = e.get('event', '')
            if event_type not in by_type:
                by_type[event_type] = []
            by_type[event_type].append(e)
        
        print("\nEvent Types:")
        for event_type, events in sorted(by_type.items()):
            print(f"  {event_type}: {len(events)}")
        
        # Check for RANGE_LOCKED and DONE events
        range_locked = [e for e in rty2_events if 'RANGE_LOCKED' in e.get('event', '').upper()]
        done_events = [e for e in rty2_events if 'DONE' in e.get('event', '').upper() or 'MARKET_CLOSE' in e.get('event', '').upper()]
        
        print(f"\nRANGE_LOCKED events: {len(range_locked)}")
        if range_locked:
            latest = max(range_locked, key=lambda e: e.get('ts_utc', ''))
            ts = latest.get('ts_utc', '')[:19]
            trading_date = latest.get('trading_date', 'N/A')
            print(f"  Latest: {ts}, Trading Date: {trading_date}")
        
        print(f"\nDONE/MARKET_CLOSE events: {len(done_events)}")
        if done_events:
            latest = max(done_events, key=lambda e: e.get('ts_utc', ''))
            ts = latest.get('ts_utc', '')[:19]
            trading_date = latest.get('trading_date', 'N/A')
            print(f"  Latest: {ts}, Trading Date: {trading_date}")
        else:
            print("  [WARNING] No DONE/MARKET_CLOSE events found - stream may not have completed")
    else:
        print("No RTY2 stream events found")

print()

# Check watchdog state
print("2. Checking Watchdog State")
print("-" * 100)

# Try to get watchdog status via API or check state directly
# For now, check if we can see what the watchdog sees
print("Watchdog should filter streams by current trading date")
print(f"Expected: Only show streams for {current_date}")
print("If RTY2 from yesterday is showing, watchdog may not be filtering correctly")
print()

# Check frontend feed for RTY2 events
print("3. Checking Frontend Feed for RTY2 Events")
print("-" * 100)

feed_file = Path("logs/robot/frontend_feed.jsonl")
if feed_file.exists():
    rty2_feed_events = []
    try:
        with open(feed_file, 'r', encoding='utf-8-sig') as f:
            for line in f.readlines()[-1000:]:  # Last 1000 lines
                try:
                    e = json.loads(line.strip())
                    stream = e.get('stream', '') or (e.get('data', {}) or {}).get('stream', '')
                    if 'RTY2' in str(stream).upper():
                        rty2_feed_events.append(e)
                except:
                    pass
    except:
        pass
    
    if rty2_feed_events:
        print(f"Found {len(rty2_feed_events)} RTY2 events in frontend feed")
        
        # Check trading dates
        trading_dates = set()
        for e in rty2_feed_events:
            td = e.get('trading_date', '') or (e.get('data', {}) or {}).get('trading_date', '')
            if td:
                trading_dates.add(td)
        
        print(f"Trading dates: {sorted(trading_dates)}")
        if current_date not in trading_dates:
            print(f"  [WARNING] No events for current date {current_date}")
            print(f"  Events are from: {sorted(trading_dates)}")
    else:
        print("No RTY2 events found in frontend feed")
else:
    print("Frontend feed file not found")

print()
print("=" * 100)
print("DIAGNOSIS")
print("=" * 100)
print()
print("If RTY2 from yesterday is showing in watchdog UI:")
print("1. Check if stream transitioned to DONE state")
print("2. Check if watchdog is filtering by current trading date")
print("3. Check if stream state was committed properly")
print()
print("Solution: Watchdog should only show streams for current trading date.")
print("If a stream from yesterday is stuck in RANGE_LOCKED, it should be filtered out.")
