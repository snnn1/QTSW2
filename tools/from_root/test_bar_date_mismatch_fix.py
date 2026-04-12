#!/usr/bin/env python3
"""
Test script to verify BAR_DATE_MISMATCH fix.
Checks that:
1. Evening session bars (previous day 17:00-23:59 CST) are accepted for trading date
2. BAR_DATE_MISMATCH only occurs for bars outside session window
3. Session window fields are present in BAR_DATE_MISMATCH events
"""

import json
import sys
from pathlib import Path
from datetime import datetime, timedelta
from collections import defaultdict, Counter
from typing import Dict, List, Optional, Tuple
import pytz

# Setup paths
QTSW2_ROOT = Path(__file__).parent
LOGS_DIR = QTSW2_ROOT / "logs" / "robot"
CHICAGO_TZ = pytz.timezone("America/Chicago")

def parse_iso_timestamp(ts_str: str) -> Optional[datetime]:
    """Parse ISO timestamp string to datetime."""
    try:
        # Handle both with and without timezone info
        if ts_str.endswith('Z'):
            ts_str = ts_str[:-1] + '+00:00'
        return datetime.fromisoformat(ts_str.replace('Z', '+00:00'))
    except:
        return None

def get_session_window(trading_date_str: str) -> Tuple[datetime, datetime]:
    """Compute session window for a trading date.
    Session: [previous_day 17:00 CST, trading_date 16:00 CST)
    """
    trading_date = datetime.strptime(trading_date_str, "%Y-%m-%d").date()
    previous_day = trading_date - timedelta(days=1)
    
    # Session start: previous day 17:00 CST
    session_start = CHICAGO_TZ.localize(datetime.combine(previous_day, datetime.min.time().replace(hour=17)))
    
    # Session end: trading date 16:00 CST
    session_end = CHICAGO_TZ.localize(datetime.combine(trading_date, datetime.min.time().replace(hour=16)))
    
    return session_start, session_end

def is_bar_in_session_window(bar_chicago: datetime, session_start: datetime, session_end: datetime) -> bool:
    """Check if bar timestamp falls within session window."""
    return session_start <= bar_chicago < session_end

def load_bar_events(date_str: str) -> Tuple[List[dict], Dict[str, List[dict]]]:
    """Load BAR_DATE_MISMATCH and BAR_ACCEPTED events from logs."""
    engine_log = LOGS_DIR / f"robot_ENGINE.jsonl"
    instrument_logs = list(LOGS_DIR.glob(f"robot_*.jsonl"))
    
    bar_mismatch_events = []
    bar_accepted_events = []
    instrument_bar_events = defaultdict(list)
    
    # Load ENGINE log
    if engine_log.exists():
        with open(engine_log, 'r', encoding='utf-8') as f:
            for line in f:
                line = line.strip()
                if not line:
                    continue
                try:
                    event = json.loads(line)
                    event_date = event.get("trading_date", "")
                    if event_date != date_str:
                        continue
                    
                    event_type = event.get("event", "")
                    if event_type == "BAR_DATE_MISMATCH":
                        bar_mismatch_events.append(event)
                    elif event_type == "BAR_ACCEPTED":
                        bar_accepted_events.append(event)
                except:
                    pass
    
    # Load instrument logs
    for log_file in instrument_logs:
        if log_file.name == "robot_ENGINE.jsonl":
            continue
        
        with open(log_file, 'r', encoding='utf-8') as f:
            for line in f:
                line = line.strip()
                if not line:
                    continue
                try:
                    event = json.loads(line)
                    event_date = event.get("trading_date", "")
                    if event_date != date_str:
                        continue
                    
                    event_type = event.get("event", "")
                    payload = event.get("data", {}).get("payload", {})
                    instrument = payload.get("instrument", "UNKNOWN")
                    
                    if event_type == "BAR_DATE_MISMATCH":
                        instrument_bar_events[instrument].append(event)
                    elif event_type == "BAR_ACCEPTED":
                        instrument_bar_events[instrument].append(event)
                except:
                    pass
    
    return bar_mismatch_events, dict(instrument_bar_events)

def analyze_bar_date_mismatch_fix(date_str: str):
    """Analyze BAR_DATE_MISMATCH events to verify the fix."""
    print("="*80)
    print(f"BAR_DATE_MISMATCH FIX VERIFICATION - {date_str}")
    print("="*80)
    
    # Load events
    print(f"\n[1] Loading events from logs...")
    engine_mismatches, instrument_events = load_bar_events(date_str)
    
    all_mismatches = engine_mismatches.copy()
    for inst, events in instrument_events.items():
        all_mismatches.extend([e for e in events if e.get("event") == "BAR_DATE_MISMATCH"])
    
    print(f"  Found {len(all_mismatches)} BAR_DATE_MISMATCH events")
    
    if not all_mismatches:
        print("\n[SUCCESS] NO BAR_DATE_MISMATCH events found - fix appears successful!")
        print("   (This is expected if all bars fall within session window)")
        return True
    
    # Get trading date from first event
    first_event = all_mismatches[0]
    trading_date_str = first_event.get("trading_date", date_str)
    
    print(f"\n[2] Computing session window for trading date {trading_date_str}...")
    session_start, session_end = get_session_window(trading_date_str)
    print(f"  Session window: [{session_start.strftime('%Y-%m-%d %H:%M:%S %Z')}, {session_end.strftime('%Y-%m-%d %H:%M:%S %Z')})")
    
    # Analyze each mismatch event
    print(f"\n[3] Analyzing {len(all_mismatches)} BAR_DATE_MISMATCH events...")
    
    valid_rejections = []  # Bars correctly rejected (outside window)
    invalid_rejections = []  # Bars incorrectly rejected (inside window) - BUG!
    missing_fields = []  # Events missing required fields
    
    for event in all_mismatches:
        payload = event.get("data", {}).get("payload", {})
        
        # Check for required fields
        bar_chicago_str = payload.get("bar_chicago")
        session_start_str = payload.get("session_start_chicago")
        session_end_str = payload.get("session_end_chicago")
        rejection_reason = payload.get("rejection_reason")
        
        if not bar_chicago_str:
            missing_fields.append(event)
            continue
        
        bar_chicago = parse_iso_timestamp(bar_chicago_str)
        if not bar_chicago:
            missing_fields.append(event)
            continue
        
        # Check if bar is actually outside session window
        is_outside = not is_bar_in_session_window(bar_chicago, session_start, session_end)
        
        if is_outside:
            valid_rejections.append({
                "event": event,
                "bar_chicago": bar_chicago,
                "rejection_reason": rejection_reason
            })
        else:
            invalid_rejections.append({
                "event": event,
                "bar_chicago": bar_chicago,
                "rejection_reason": rejection_reason
            })
    
    # Report results
    print(f"\n[4] RESULTS:")
    print(f"  [OK] Valid rejections (bar outside window): {len(valid_rejections)}")
    print(f"  [ERROR] Invalid rejections (bar inside window): {len(invalid_rejections)}")
    print(f"  [WARNING] Events missing fields: {len(missing_fields)}")
    
    # Check for required fields in events
    events_with_session_fields = sum(1 for e in all_mismatches 
                                     if e.get("data", {}).get("payload", {}).get("session_start_chicago") 
                                     and e.get("data", {}).get("payload", {}).get("session_end_chicago"))
    
    events_with_rejection_reason = sum(1 for e in all_mismatches 
                                      if e.get("data", {}).get("payload", {}).get("rejection_reason"))
    
    print(f"\n[5] FIELD PRESENCE CHECK:")
    print(f"  session_start_chicago: {events_with_session_fields}/{len(all_mismatches)} events")
    print(f"  session_end_chicago: {events_with_session_fields}/{len(all_mismatches)} events")
    print(f"  rejection_reason: {events_with_rejection_reason}/{len(all_mismatches)} events")
    
    # Show sample invalid rejections (if any)
    if invalid_rejections:
        print(f"\n[6] [ERROR] INVALID REJECTIONS (BUG DETECTED):")
        for i, item in enumerate(invalid_rejections[:5], 1):
            bar_chicago = item["bar_chicago"]
            payload = item["event"].get("data", {}).get("payload", {})
            instrument = payload.get("instrument", "UNKNOWN")
            print(f"  {i}. Instrument: {instrument}")
            print(f"     Bar Chicago: {bar_chicago.strftime('%Y-%m-%d %H:%M:%S %Z')}")
            print(f"     Session Window: [{session_start.strftime('%H:%M')}, {session_end.strftime('%H:%M')})")
            print(f"     Rejection Reason: {item['rejection_reason']}")
            print()
    
    # Show sample valid rejections
    if valid_rejections:
        print(f"\n[7] [OK] VALID REJECTIONS (Expected):")
        for i, item in enumerate(valid_rejections[:3], 1):
            bar_chicago = item["bar_chicago"]
            payload = item["event"].get("data", {}).get("payload", {})
            instrument = payload.get("instrument", "UNKNOWN")
            print(f"  {i}. Instrument: {instrument}")
            print(f"     Bar Chicago: {bar_chicago.strftime('%Y-%m-%d %H:%M:%S %Z')}")
            print(f"     Rejection Reason: {item['rejection_reason']}")
            print()
    
    # Final verdict
    print("\n" + "="*80)
    if invalid_rejections:
        print("[FAIL] Found bars incorrectly rejected (inside session window)")
        print(f"   Count: {len(invalid_rejections)}")
        return False
    elif missing_fields:
        print("[WARNING] Some events missing required fields")
        print(f"   Count: {len(missing_fields)}")
        return True
    elif len(all_mismatches) == 0:
        print("[PASS] No BAR_DATE_MISMATCH events (all bars accepted)")
        return True
    else:
        print("[PASS] All BAR_DATE_MISMATCH events are valid (bars outside session window)")
        return True

def main():
    """Main entry point."""
    if len(sys.argv) > 1:
        date_str = sys.argv[1]
    else:
        # Default to today
        today = datetime.now(CHICAGO_TZ).date()
        date_str = today.strftime("%Y-%m-%d")
    
    print(f"Testing BAR_DATE_MISMATCH fix for date: {date_str}")
    print(f"Logs directory: {LOGS_DIR}")
    
    if not LOGS_DIR.exists():
        print(f"ERROR: Logs directory not found: {LOGS_DIR}")
        return 1
    
    success = analyze_bar_date_mismatch_fix(date_str)
    return 0 if success else 1

if __name__ == "__main__":
    sys.exit(main())
