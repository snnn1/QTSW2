#!/usr/bin/env python3
"""Check what live bars are currently being received."""

import json
from pathlib import Path
from datetime import datetime, timedelta
from collections import Counter
import pytz

logs_dir = Path("logs/robot")
engine_log = logs_dir / "robot_ENGINE.jsonl"
CHICAGO_TZ = pytz.timezone("America/Chicago")

def parse_iso_timestamp(ts_str: str):
    """Parse ISO timestamp string to datetime."""
    if not ts_str:
        return None
    try:
        if ts_str.endswith('Z'):
            ts_str = ts_str[:-1] + '+00:00'
        return datetime.fromisoformat(ts_str.replace('Z', '+00:00'))
    except:
        return None

def get_session_window(trading_date_str: str):
    """Compute session window for a trading date."""
    trading_date = datetime.strptime(trading_date_str, "%Y-%m-%d").date()
    previous_day = trading_date - timedelta(days=1)
    
    session_start = CHICAGO_TZ.localize(datetime.combine(previous_day, datetime.min.time().replace(hour=17)))
    session_end = CHICAGO_TZ.localize(datetime.combine(trading_date, datetime.min.time().replace(hour=16)))
    
    return session_start, session_end

# Get current time
now_utc = datetime.now(pytz.UTC)
now_chicago = now_utc.astimezone(CHICAGO_TZ)
print(f"Current time (Chicago): {now_chicago.strftime('%Y-%m-%d %H:%M:%S %Z')}")
print(f"Current time (UTC): {now_utc.strftime('%Y-%m-%d %H:%M:%S %Z')}")

# Load recent bar events (last 1000 lines)
print("\nLoading recent bar events...")
bar_events = []
with open(engine_log, 'r', encoding='utf-8') as f:
    lines = f.readlines()
    # Get last 1000 lines
    recent_lines = lines[-1000:] if len(lines) > 1000 else lines
    
    for line in recent_lines:
        line = line.strip()
        if not line:
            continue
        try:
            event = json.loads(line)
            event_type = event.get("event", "")
            if event_type in ["BAR_ACCEPTED", "BAR_DATE_MISMATCH", "BAR_PARTIAL_REJECTED", "BAR_REJECTED", "BAR_RECEIVED"]:
                bar_events.append(event)
        except:
            pass

print(f"Recent bar events (last 1000 log lines): {len(bar_events)}")

if bar_events:
    # Get trading date from events
    first_event = bar_events[0]
    payload = first_event.get("data", {}).get("payload", {})
    trading_date_str = payload.get("active_trading_date") or payload.get("locked_trading_date") or "2026-01-20"
    
    print(f"\nTrading date: {trading_date_str}")
    session_start, session_end = get_session_window(trading_date_str)
    
    print(f"\n{'='*80}")
    print("SESSION WINDOW EXPLANATION")
    print(f"{'='*80}")
    print(f"\nFor trading date {trading_date_str}, the session window is:")
    print(f"  Start: {session_start.strftime('%Y-%m-%d %H:%M:%S %Z')} (previous day at 17:00 CST)")
    print(f"  End:   {session_end.strftime('%Y-%m-%d %H:%M:%S %Z')} (trading date at 16:00 CST)")
    print(f"\nThis means bars are accepted if they fall within:")
    print(f"  [{session_start.strftime('%Y-%m-%d %H:%M')}, {session_end.strftime('%Y-%m-%d %H:%M')})")
    print(f"\nExample: For trading date Jan 20:")
    print(f"  - Bars from Jan 19 17:00-23:59 CST are ACCEPTED")
    print(f"  - Bars from Jan 20 00:00-15:59 CST are ACCEPTED")
    print(f"  - Bars from Jan 19 before 17:00 CST are REJECTED")
    print(f"  - Bars from Jan 20 at/after 16:00 CST are REJECTED")
    
    # Analyze recent events
    print(f"\n{'='*80}")
    print("RECENT BAR EVENTS")
    print(f"{'='*80}")
    
    # Sort by timestamp
    bar_events.sort(key=lambda e: e.get("ts_utc", ""), reverse=True)
    
    print(f"\nLast 20 bar events:")
    for i, event in enumerate(bar_events[:20], 1):
        event_type = event.get("event", "")
        ts_utc_str = event.get("ts_utc", "")
        payload = event.get("data", {}).get("payload", {})
        instrument = payload.get("instrument", "UNKNOWN")
        bar_chicago_str = payload.get("bar_chicago") or payload.get("bar_timestamp_chicago")
        
        # Parse timestamps
        event_time_utc = parse_iso_timestamp(ts_utc_str)
        bar_chicago = parse_iso_timestamp(bar_chicago_str) if bar_chicago_str else None
        
        # Check if bar is in session window
        in_window = False
        if bar_chicago:
            in_window = session_start <= bar_chicago < session_end
        
        print(f"\n{i}. {event_type} - {instrument}")
        if event_time_utc:
            event_time_chicago = event_time_utc.astimezone(CHICAGO_TZ)
            print(f"   Event time: {event_time_chicago.strftime('%Y-%m-%d %H:%M:%S %Z')}")
        if bar_chicago:
            print(f"   Bar time: {bar_chicago.strftime('%Y-%m-%d %H:%M:%S %Z')}")
            print(f"   In session window: {in_window}")
            if not in_window:
                if bar_chicago < session_start:
                    mins_before = (session_start - bar_chicago).total_seconds() / 60
                    print(f"   (Rejected: {mins_before:.0f} minutes before session start)")
                elif bar_chicago >= session_end:
                    mins_after = (bar_chicago - session_end).total_seconds() / 60
                    print(f"   (Rejected: {mins_after:.0f} minutes after session end)")
        
        if event_type == "BAR_DATE_MISMATCH":
            rejection_reason = payload.get("rejection_reason", "")
            print(f"   Rejection reason: {rejection_reason}")
    
    # Summary
    print(f"\n{'='*80}")
    print("SUMMARY")
    print(f"{'='*80}")
    
    event_types = Counter(e.get("event", "UNKNOWN") for e in bar_events)
    print(f"\nEvent types in recent logs:")
    for event_type, count in event_types.most_common():
        print(f"  {event_type}: {count}")
    
    # Check if current time is within session window
    current_in_window = session_start <= now_chicago < session_end
    print(f"\nCurrent time status:")
    print(f"  Current Chicago time: {now_chicago.strftime('%Y-%m-%d %H:%M:%S %Z')}")
    print(f"  Within session window: {current_in_window}")
    
    if current_in_window:
        print(f"  ✓ Bars received now should be ACCEPTED")
    else:
        if now_chicago < session_start:
            print(f"  ✗ Before session start - bars will be REJECTED")
            print(f"    Session starts in: {(session_start - now_chicago).total_seconds()/3600:.1f} hours")
        else:
            print(f"  ✗ After session end - bars will be REJECTED")
            print(f"    Session ended: {(now_chicago - session_end).total_seconds()/3600:.1f} hours ago")
