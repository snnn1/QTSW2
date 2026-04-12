#!/usr/bin/env python3
"""Debug why bars aren't being accepted."""

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

# Load all bar events
print("Loading bar events...")
bar_events = []
with open(engine_log, 'r', encoding='utf-8') as f:
    for line in f:
        line = line.strip()
        if not line:
            continue
        try:
            event = json.loads(line)
            event_type = event.get("event", "")
            if event_type in ["BAR_ACCEPTED", "BAR_DATE_MISMATCH", "BAR_PARTIAL_REJECTED", "BAR_REJECTED"]:
                bar_events.append(event)
        except:
            pass

print(f"Total bar events: {len(bar_events)}")

# Get trading date from first event
if bar_events:
    first_event = bar_events[0]
    payload = first_event.get("data", {}).get("payload", {})
    trading_date_str = payload.get("active_trading_date") or payload.get("locked_trading_date") or "2026-01-20"
    
    print(f"\nTrading date: {trading_date_str}")
    session_start, session_end = get_session_window(trading_date_str)
    print(f"Session window: [{session_start.strftime('%Y-%m-%d %H:%M:%S %Z')}, {session_end.strftime('%Y-%m-%d %H:%M:%S %Z')})")
    
    # Analyze recent BAR_DATE_MISMATCH events
    mismatch_events = [e for e in bar_events if e.get("event") == "BAR_DATE_MISMATCH"]
    print(f"\nBAR_DATE_MISMATCH events: {len(mismatch_events)}")
    
    if mismatch_events:
        # Check last 10 events
        print("\nLast 10 BAR_DATE_MISMATCH events:")
        for i, event in enumerate(mismatch_events[-10:], 1):
            payload = event.get("data", {}).get("payload", {})
            bar_chicago_str = payload.get("bar_chicago") or payload.get("bar_timestamp_chicago")
            rejection_reason = payload.get("rejection_reason", "")
            instrument = payload.get("instrument", "UNKNOWN")
            
            if bar_chicago_str:
                bar_chicago = parse_iso_timestamp(bar_chicago_str)
                if bar_chicago:
                    is_in_window = session_start <= bar_chicago < session_end
                    print(f"\n{i}. {instrument}: {bar_chicago.strftime('%Y-%m-%d %H:%M:%S %Z')}")
                    print(f"   Rejection reason: {rejection_reason}")
                    print(f"   Should be in window: {is_in_window}")
                    if not is_in_window:
                        if bar_chicago < session_start:
                            print(f"   (Before session start by {(session_start - bar_chicago).total_seconds()/60:.1f} minutes)")
                        else:
                            print(f"   (After session end by {(bar_chicago - session_end).total_seconds()/60:.1f} minutes)")
        
        # Count by rejection reason
        reasons = Counter(payload.get("rejection_reason", "UNKNOWN") for e in mismatch_events for payload in [e.get("data", {}).get("payload", {})])
        print(f"\nRejection reasons:")
        for reason, count in reasons.most_common():
            print(f"  {reason}: {count}")
        
        # Check if any bars SHOULD be accepted but aren't
        print("\nChecking for bars that SHOULD be accepted...")
        should_be_accepted = []
        for event in mismatch_events:
            payload = event.get("data", {}).get("payload", {})
            bar_chicago_str = payload.get("bar_chicago") or payload.get("bar_timestamp_chicago")
            if bar_chicago_str:
                bar_chicago = parse_iso_timestamp(bar_chicago_str)
                if bar_chicago and session_start <= bar_chicago < session_end:
                    should_be_accepted.append({
                        "event": event,
                        "bar_chicago": bar_chicago,
                        "instrument": payload.get("instrument", "UNKNOWN")
                    })
        
        if should_be_accepted:
            print(f"[ERROR] Found {len(should_be_accepted)} bars that SHOULD be accepted but were rejected!")
            print("This indicates the fix is not working correctly.")
            for item in should_be_accepted[:5]:
                print(f"  {item['instrument']}: {item['bar_chicago'].strftime('%Y-%m-%d %H:%M:%S %Z')}")
        else:
            print("All rejected bars are correctly outside the session window.")
    
    # Check for BAR_ACCEPTED events
    accepted_events = [e for e in bar_events if e.get("event") == "BAR_ACCEPTED"]
    print(f"\nBAR_ACCEPTED events: {len(accepted_events)}")
    
    if accepted_events:
        print("\nSample BAR_ACCEPTED events:")
        for i, event in enumerate(accepted_events[:5], 1):
            payload = event.get("data", {}).get("payload", {})
            bar_chicago_str = payload.get("bar_chicago") or payload.get("bar_timestamp_chicago")
            instrument = payload.get("instrument", "UNKNOWN")
            if bar_chicago_str:
                bar_chicago = parse_iso_timestamp(bar_chicago_str)
                if bar_chicago:
                    print(f"{i}. {instrument}: {bar_chicago.strftime('%Y-%m-%d %H:%M:%S %Z')}")
    else:
        print("\n[ISSUE] No BAR_ACCEPTED events found!")
        print("Possible reasons:")
        print("1. All bars are outside session window (check rejection reasons above)")
        print("2. Strategy just restarted and hasn't received bars yet")
        print("3. Bars are being rejected by other filters (BAR_PARTIAL_REJECTED, etc.)")
        
        # Check for other rejection types
        partial_rejected = [e for e in bar_events if e.get("event") == "BAR_PARTIAL_REJECTED"]
        other_rejected = [e for e in bar_events if e.get("event") == "BAR_REJECTED"]
        print(f"\nBAR_PARTIAL_REJECTED: {len(partial_rejected)}")
        print(f"BAR_REJECTED: {len(other_rejected)}")
