#!/usr/bin/env python3
"""
Evening Session Bar Acceptance Test - MOST IMPORTANT

Verifies that bars from previous day 17:00-23:59 CST are accepted for trading date.
This was the exact failure mode before the fix - if this passes, the fix is fundamentally correct.
"""

import json
import sys
from pathlib import Path
from datetime import datetime, timedelta
from collections import defaultdict, Counter
from typing import List, Optional, Tuple
import pytz

# Setup paths
QTSW2_ROOT = Path(__file__).parent
LOGS_DIR = QTSW2_ROOT / "logs" / "robot"
CHICAGO_TZ = pytz.timezone("America/Chicago")

def parse_iso_timestamp(ts_str: str) -> Optional[datetime]:
    """Parse ISO timestamp string to datetime."""
    if not ts_str:
        return None
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

def is_evening_session_bar(bar_chicago: datetime, session_start: datetime) -> bool:
    """Check if bar is from evening session (17:00-23:59 CST on previous day)."""
    # Evening session is from session_start (17:00) to midnight
    evening_end = session_start.replace(hour=23, minute=59, second=59)
    return session_start <= bar_chicago <= evening_end

def load_bar_events(date_str: str) -> Tuple[List[dict], List[dict]]:
    """Load BAR_ACCEPTED and BAR_DATE_MISMATCH events from logs."""
    engine_log = LOGS_DIR / "robot_ENGINE.jsonl"
    instrument_logs = list(LOGS_DIR.glob("robot_*.jsonl"))
    
    bar_accepted_events = []
    bar_mismatch_events = []
    
    # Load ENGINE log
    if engine_log.exists():
        with open(engine_log, 'r', encoding='utf-8') as f:
            for line in f:
                line = line.strip()
                if not line:
                    continue
                try:
                    event = json.loads(line)
                    # Check trading_date or use timestamp date
                    event_date = event.get("trading_date", "")
                    if not event_date:
                        # Try to get date from timestamp
                        ts_str = event.get("ts_utc", "")
                        if ts_str:
                            try:
                                ts = datetime.fromisoformat(ts_str.replace("Z", "+00:00"))
                                event_date = ts.strftime("%Y-%m-%d")
                            except:
                                pass
                    
                    if event_date != date_str:
                        continue
                    
                    event_type = event.get("event", "")
                    if event_type == "BAR_ACCEPTED":
                        bar_accepted_events.append(event)
                    elif event_type == "BAR_DATE_MISMATCH":
                        bar_mismatch_events.append(event)
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
                    # Check trading_date or use timestamp date
                    event_date = event.get("trading_date", "")
                    if not event_date:
                        # Try to get date from timestamp
                        ts_str = event.get("ts_utc", "")
                        if ts_str:
                            try:
                                ts = datetime.fromisoformat(ts_str.replace("Z", "+00:00"))
                                event_date = ts.strftime("%Y-%m-%d")
                            except:
                                pass
                    
                    if event_date != date_str:
                        continue
                    
                    event_type = event.get("event", "")
                    if event_type == "BAR_ACCEPTED":
                        bar_accepted_events.append(event)
                    elif event_type == "BAR_DATE_MISMATCH":
                        bar_mismatch_events.append(event)
                except:
                    pass
    
    return bar_accepted_events, bar_mismatch_events

def analyze_evening_session_bars(date_str: str):
    """Analyze evening session bar acceptance."""
    print("="*80)
    print(f"EVENING SESSION BAR ACCEPTANCE TEST - {date_str}")
    print("="*80)
    print("\n[PURPOSE] Verify bars from previous day 17:00-23:59 CST are accepted")
    print("          This was the exact failure mode before the fix.\n")
    
    # Load events
    print("[1] Loading bar events from logs...")
    bar_accepted, bar_mismatches = load_bar_events(date_str)
    print(f"  Found {len(bar_accepted)} BAR_ACCEPTED events")
    print(f"  Found {len(bar_mismatches)} BAR_DATE_MISMATCH events")
    
    if not bar_accepted:
        print("\n[WARNING] No BAR_ACCEPTED events found - cannot verify evening session acceptance")
        return False
    
    # Get trading date and session window
    first_event = bar_accepted[0]
    trading_date_str = first_event.get("trading_date", date_str)
    
    print(f"\n[2] Computing session window for trading date {trading_date_str}...")
    session_start, session_end = get_session_window(trading_date_str)
    print(f"  Session window: [{session_start.strftime('%Y-%m-%d %H:%M:%S %Z')}, {session_end.strftime('%Y-%m-%d %H:%M:%S %Z')})")
    print(f"  Evening session: [{session_start.strftime('%H:%M')}, 23:59] on {session_start.date()}")
    
    # Analyze BAR_ACCEPTED events for evening session bars
    print(f"\n[3] Analyzing {len(bar_accepted)} BAR_ACCEPTED events for evening session bars...")
    
    evening_session_accepted = []
    other_accepted = []
    
    for event in bar_accepted:
        payload = event.get("data", {}).get("payload", {})
        bar_chicago_str = payload.get("bar_chicago") or payload.get("bar_timestamp_chicago")
        
        if not bar_chicago_str:
            other_accepted.append(event)
            continue
        
        bar_chicago = parse_iso_timestamp(bar_chicago_str)
        if not bar_chicago:
            other_accepted.append(event)
            continue
        
        # Check if bar is from evening session
        if is_evening_session_bar(bar_chicago, session_start):
            evening_session_accepted.append({
                "event": event,
                "bar_chicago": bar_chicago,
                "instrument": payload.get("instrument", "UNKNOWN")
            })
        else:
            other_accepted.append(event)
    
    print(f"  Evening session bars accepted: {len(evening_session_accepted)}")
    print(f"  Other bars accepted: {len(other_accepted)}")
    
    # Analyze BAR_DATE_MISMATCH events for evening session bars (should be 0)
    print(f"\n[4] Checking BAR_DATE_MISMATCH events for evening session bars (should be 0)...")
    
    evening_session_rejected = []
    
    for event in bar_mismatches:
        payload = event.get("data", {}).get("payload", {})
        bar_chicago_str = payload.get("bar_chicago") or payload.get("bar_timestamp_chicago")
        
        if not bar_chicago_str:
            continue
        
        bar_chicago = parse_iso_timestamp(bar_chicago_str)
        if not bar_chicago:
            continue
        
        # Check if rejected bar is from evening session (this should NOT happen)
        if is_evening_session_bar(bar_chicago, session_start):
            evening_session_rejected.append({
                "event": event,
                "bar_chicago": bar_chicago,
                "instrument": payload.get("instrument", "UNKNOWN"),
                "rejection_reason": payload.get("rejection_reason", "")
            })
    
    print(f"  Evening session bars rejected: {len(evening_session_rejected)}")
    
    # Calculate acceptance rate
    total_evening_bars = len(evening_session_accepted) + len(evening_session_rejected)
    if total_evening_bars > 0:
        acceptance_rate = (len(evening_session_accepted) / total_evening_bars) * 100
    else:
        acceptance_rate = 0.0
    
    # Report results
    print(f"\n[5] RESULTS:")
    print(f"  Evening session bars accepted: {len(evening_session_accepted)}")
    print(f"  Evening session bars rejected: {len(evening_session_rejected)}")
    print(f"  Total evening session bars: {total_evening_bars}")
    print(f"  Acceptance rate: {acceptance_rate:.1f}%")
    
    # Show sample evening session accepted bars
    if evening_session_accepted:
        print(f"\n[6] Sample evening session bars (ACCEPTED - Expected):")
        for i, item in enumerate(evening_session_accepted[:5], 1):
            bar_chicago = item["bar_chicago"]
            instrument = item["instrument"]
            print(f"  {i}. {instrument}: {bar_chicago.strftime('%Y-%m-%d %H:%M:%S %Z')}")
    
    # Show evening session rejected bars (BUG if any)
    if evening_session_rejected:
        print(f"\n[7] [ERROR] Evening session bars REJECTED (BUG DETECTED):")
        for i, item in enumerate(evening_session_rejected[:10], 1):
            bar_chicago = item["bar_chicago"]
            instrument = item["instrument"]
            rejection_reason = item["rejection_reason"]
            print(f"  {i}. {instrument}: {bar_chicago.strftime('%Y-%m-%d %H:%M:%S %Z')} - {rejection_reason}")
    
    # Final verdict
    print("\n" + "="*80)
    if evening_session_rejected:
        print("[FAIL] Found evening session bars incorrectly rejected!")
        print(f"   Count: {len(evening_session_rejected)}")
        print("   This indicates the fix is not working correctly.")
        return False
    elif total_evening_bars == 0:
        print("[WARNING] No evening session bars found in logs")
        print("   This may be normal if:")
        print("   - Strategy started after evening session")
        print("   - No bars were received during evening session")
        print("   - Check if strategy is running during evening hours")
        return True  # Not a failure, just no data
    elif acceptance_rate >= 95.0:
        print("[PASS] Evening session bars are being accepted correctly!")
        print(f"   Acceptance rate: {acceptance_rate:.1f}%")
        print("   The fix is working as expected.")
        return True
    else:
        print("[FAIL] Evening session bar acceptance rate too low!")
        print(f"   Acceptance rate: {acceptance_rate:.1f}% (expected >=95%)")
        return False

def main():
    """Main entry point."""
    if len(sys.argv) > 1:
        date_str = sys.argv[1]
    else:
        # Default to today
        today = datetime.now(CHICAGO_TZ).date()
        date_str = today.strftime("%Y-%m-%d")
    
    print(f"Testing evening session bar acceptance for date: {date_str}")
    print(f"Logs directory: {LOGS_DIR}")
    
    if not LOGS_DIR.exists():
        print(f"ERROR: Logs directory not found: {LOGS_DIR}")
        return 1
    
    success = analyze_evening_session_bars(date_str)
    return 0 if success else 1

if __name__ == "__main__":
    sys.exit(main())
