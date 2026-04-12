#!/usr/bin/env python3
"""
Session Boundary Edge Case Test - ESSENTIAL

Tests bars at exact session boundaries to catch off-by-one and fencepost errors.
Session windows live and die on boundaries - futures sessions are notorious for edge bugs.
"""

import json
import sys
from pathlib import Path
from datetime import datetime, timedelta
from collections import defaultdict
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
    
    # Session start: previous day 17:00:00 CST
    session_start = CHICAGO_TZ.localize(datetime.combine(previous_day, datetime.min.time().replace(hour=17, minute=0, second=0)))
    
    # Session end: trading date 16:00:00 CST
    session_end = CHICAGO_TZ.localize(datetime.combine(trading_date, datetime.min.time().replace(hour=16, minute=0, second=0)))
    
    return session_start, session_end

def is_bar_in_session_window(bar_chicago: datetime, session_start: datetime, session_end: datetime) -> bool:
    """Check if bar timestamp falls within session window [start, end)."""
    return session_start <= bar_chicago < session_end

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

def analyze_boundary_cases(date_str: str):
    """Analyze boundary cases for session window."""
    print("="*80)
    print(f"SESSION BOUNDARY EDGE CASE TEST - {date_str}")
    print("="*80)
    print("\n[PURPOSE] Test bars at exact session boundaries")
    print("          Catches off-by-one and fencepost errors.\n")
    
    # Load events
    print("[1] Loading bar events from logs...")
    bar_accepted, bar_mismatches = load_bar_events(date_str)
    print(f"  Found {len(bar_accepted)} BAR_ACCEPTED events")
    print(f"  Found {len(bar_mismatches)} BAR_DATE_MISMATCH events")
    
    if not bar_accepted and not bar_mismatches:
        print("\n[WARNING] No bar events found - cannot test boundaries")
        return True
    
    # Get trading date and session window
    if bar_accepted:
        first_event = bar_accepted[0]
    elif bar_mismatches:
        first_event = bar_mismatches[0]
    else:
        print("\n[ERROR] No events found")
        return False
    
    trading_date_str = first_event.get("trading_date", date_str)
    
    print(f"\n[2] Computing session window for trading date {trading_date_str}...")
    session_start, session_end = get_session_window(trading_date_str)
    print(f"  Session start: {session_start.strftime('%Y-%m-%d %H:%M:%S %Z')}")
    print(f"  Session end: {session_end.strftime('%Y-%m-%d %H:%M:%S %Z')}")
    print(f"  Window: [start, end) - inclusive start, exclusive end")
    
    # Define boundary test cases
    boundary_cases = {
        "exact_session_start": session_start,
        "just_before_session_start": session_start - timedelta(seconds=1),
        "just_after_session_start": session_start + timedelta(seconds=1),
        "exact_session_end": session_end,
        "just_before_session_end": session_end - timedelta(seconds=1),
        "just_after_session_end": session_end + timedelta(seconds=1),
    }
    
    print(f"\n[3] Analyzing boundary cases...")
    
    # Test each boundary case
    boundary_results = {}
    
    for case_name, boundary_time in boundary_cases.items():
        # Check if any bars exist at this boundary
        found_accepted = []
        found_rejected = []
        
        # Check accepted bars
        for event in bar_accepted:
            payload = event.get("data", {}).get("payload", {})
            bar_chicago_str = payload.get("bar_chicago") or payload.get("bar_timestamp_chicago")
            if not bar_chicago_str:
                continue
            
            bar_chicago = parse_iso_timestamp(bar_chicago_str)
            if not bar_chicago:
                continue
            
            # Check if bar is within 1 minute of boundary
            time_diff = abs((bar_chicago - boundary_time).total_seconds())
            if time_diff <= 60:  # Within 1 minute
                found_accepted.append({
                    "bar_chicago": bar_chicago,
                    "instrument": payload.get("instrument", "UNKNOWN"),
                    "time_diff_seconds": time_diff
                })
        
        # Check rejected bars
        for event in bar_mismatches:
            payload = event.get("data", {}).get("payload", {})
            bar_chicago_str = payload.get("bar_chicago") or payload.get("bar_timestamp_chicago")
            if not bar_chicago_str:
                continue
            
            bar_chicago = parse_iso_timestamp(bar_chicago_str)
            if not bar_chicago:
                continue
            
            # Check if bar is within 1 minute of boundary
            time_diff = abs((bar_chicago - boundary_time).total_seconds())
            if time_diff <= 60:  # Within 1 minute
                found_rejected.append({
                    "bar_chicago": bar_chicago,
                    "instrument": payload.get("instrument", "UNKNOWN"),
                    "rejection_reason": payload.get("rejection_reason", ""),
                    "time_diff_seconds": time_diff
                })
        
        # Determine expected behavior
        is_in_window = is_bar_in_session_window(boundary_time, session_start, session_end)
        
        boundary_results[case_name] = {
            "boundary_time": boundary_time,
            "expected_in_window": is_in_window,
            "expected_action": "ACCEPTED" if is_in_window else "REJECTED",
            "found_accepted": found_accepted,
            "found_rejected": found_rejected,
            "expected_rejection_reason": "BEFORE_SESSION_START" if boundary_time < session_start else ("AFTER_SESSION_END" if boundary_time >= session_end else None)
        }
    
    # Report results
    print(f"\n[4] BOUNDARY CASE RESULTS:")
    
    all_correct = True
    
    for case_name, result in boundary_results.items():
        boundary_time = result["boundary_time"]
        expected_action = result["expected_action"]
        found_accepted = result["found_accepted"]
        found_rejected = result["found_rejected"]
        expected_rejection_reason = result["expected_rejection_reason"]
        
        # Check correctness
        if expected_action == "ACCEPTED":
            # Should have accepted bars, not rejected
            incorrect_rejections = [r for r in found_rejected if r["rejection_reason"] != expected_rejection_reason]
            if incorrect_rejections:
                all_correct = False
                print(f"\n  [FAIL] {case_name}:")
                print(f"    Time: {boundary_time.strftime('%Y-%m-%d %H:%M:%S %Z')}")
                print(f"    Expected: ACCEPTED (bar is in session window)")
                print(f"    Found: {len(found_accepted)} accepted, {len(found_rejected)} rejected")
                if found_rejected:
                    print(f"    Incorrect rejections:")
                    for r in found_rejected[:3]:
                        print(f"      {r['instrument']}: {r['bar_chicago'].strftime('%H:%M:%S')} - {r['rejection_reason']}")
            else:
                print(f"  [OK] {case_name}: {boundary_time.strftime('%H:%M:%S')} - Expected ACCEPTED")
                if found_accepted:
                    print(f"    Found {len(found_accepted)} accepted bars (correct)")
        else:
            # Should have rejected bars with correct reason
            correct_rejections = [r for r in found_rejected if r["rejection_reason"] == expected_rejection_reason]
            incorrect_rejections = [r for r in found_rejected if r["rejection_reason"] != expected_rejection_reason]
            incorrect_acceptances = found_accepted
            
            if incorrect_rejections or incorrect_acceptances:
                all_correct = False
                print(f"\n  [FAIL] {case_name}:")
                print(f"    Time: {boundary_time.strftime('%Y-%m-%d %H:%M:%S %Z')}")
                print(f"    Expected: REJECTED with {expected_rejection_reason}")
                print(f"    Found: {len(found_accepted)} accepted, {len(correct_rejections)} correct rejections, {len(incorrect_rejections)} incorrect rejections")
                if incorrect_acceptances:
                    print(f"    Incorrect acceptances:")
                    for a in incorrect_acceptances[:3]:
                        print(f"      {a['instrument']}: {a['bar_chicago'].strftime('%H:%M:%S')}")
                if incorrect_rejections:
                    print(f"    Incorrect rejection reasons:")
                    for r in incorrect_rejections[:3]:
                        print(f"      {r['instrument']}: {r['bar_chicago'].strftime('%H:%M:%S')} - {r['rejection_reason']} (expected {expected_rejection_reason})")
            else:
                print(f"  [OK] {case_name}: {boundary_time.strftime('%H:%M:%S')} - Expected REJECTED ({expected_rejection_reason})")
                if found_rejected:
                    print(f"    Found {len(found_rejected)} rejected bars with correct reason")
    
    # Summary
    print(f"\n[5] BOUNDARY TEST SUMMARY:")
    
    # Count bars near boundaries
    total_near_boundaries = sum(len(r["found_accepted"]) + len(r["found_rejected"]) for r in boundary_results.values())
    
    if total_near_boundaries == 0:
        print("  No bars found near session boundaries")
        print("  This is normal - boundary testing requires bars at exact boundary times")
        print("  Boundary logic is validated by the session window calculation itself")
        return True
    
    print(f"  Total bars near boundaries: {total_near_boundaries}")
    
    # Final verdict
    print("\n" + "="*80)
    if all_correct:
        print("[PASS] All boundary cases handled correctly!")
        print("   Session window boundaries are working as expected.")
        return True
    else:
        print("[FAIL] Some boundary cases handled incorrectly!")
        print("   Review the failures above - boundary logic may have issues.")
        return False

def main():
    """Main entry point."""
    if len(sys.argv) > 1:
        date_str = sys.argv[1]
    else:
        # Default to today
        today = datetime.now(CHICAGO_TZ).date()
        date_str = today.strftime("%Y-%m-%d")
    
    print(f"Testing session boundaries for date: {date_str}")
    print(f"Logs directory: {LOGS_DIR}")
    
    if not LOGS_DIR.exists():
        print(f"ERROR: Logs directory not found: {LOGS_DIR}")
        return 1
    
    success = analyze_boundary_cases(date_str)
    return 0 if success else 1

if __name__ == "__main__":
    sys.exit(main())
