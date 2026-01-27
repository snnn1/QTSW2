#!/usr/bin/env python3
"""Check if all ranges have been computed properly and verify overall status"""
import json
from pathlib import Path
from datetime import datetime, timezone
from collections import defaultdict

def get_today_trading_date():
    """Get today's trading date in YYYY-MM-DD format"""
    return datetime.now(timezone.utc).strftime("%Y-%m-%d")

def load_events(log_dir):
    """Load all events from log files"""
    events = []
    for log_file in log_dir.glob("robot_*.jsonl"):
        try:
            with open(log_file, 'r', encoding='utf-8') as f:
                for line in f:
                    line = line.strip()
                    if line:
                        try:
                            events.append(json.loads(line))
                        except:
                            pass
        except:
            pass
    return events

def check_journals(journal_dir, trading_date):
    """Check journal status for all streams"""
    journals = {}
    pattern = f"{trading_date}_*.json"
    for journal_file in journal_dir.glob(pattern):
        try:
            with open(journal_file, 'r') as f:
                journal_data = json.load(f)
                stream = journal_data.get('Stream', 'UNKNOWN')
                journals[stream] = journal_data
        except Exception as e:
            print(f"  ERROR reading {journal_file.name}: {e}")
    return journals

def check_ranges_and_status(project_root=None, trading_date=None):
    """Comprehensive check of ranges and system status"""
    if project_root is None:
        project_root = Path.cwd()
    else:
        project_root = Path(project_root)
    
    if trading_date is None:
        trading_date = get_today_trading_date()
    
    log_dir = project_root / "logs" / "robot"
    journal_dir = project_root / "logs" / "robot" / "journal"
    
    print("="*80)
    print(f"RANGE & STATUS CHECK FOR TRADING DATE: {trading_date}")
    print("="*80)
    print()
    
    # Load events
    print("Loading events...")
    all_events = load_events(log_dir)
    today_events = [e for e in all_events if e.get('ts_utc', '').startswith(trading_date)]
    print(f"Found {len(today_events)} events for {trading_date}")
    print()
    
    # Check journals
    print("Checking journals...")
    journals = check_journals(journal_dir, trading_date)
    print(f"Found {len(journals)} journal files")
    print()
    
    # Group events by stream
    stream_events = defaultdict(list)
    for event in today_events:
        stream = event.get('stream')
        if stream:  # Filter out None values
            stream_events[stream].append(event)
    
    # Check each stream
    print("="*80)
    print("STREAM STATUS SUMMARY")
    print("="*80)
    print()
    
    # Filter out None values and sort
    journal_streams = [s for s in journals.keys() if s]
    event_streams = [s for s in stream_events.keys() if s]
    streams_to_check = sorted(set(journal_streams + event_streams))
    
    for stream_id in streams_to_check:
        print(f"{stream_id}:")
        
        # Journal status
        journal = journals.get(stream_id)
        if journal:
            committed = journal.get('Committed', False)
            commit_reason = journal.get('CommitReason', None)
            last_state = journal.get('LastState', 'UNKNOWN')
            print(f"  Journal: Committed={committed}, Reason={commit_reason}, LastState={last_state}")
        else:
            print(f"  Journal: Not found (stream may not have been initialized)")
        
        # Range computation events
        stream_evts = stream_events.get(stream_id, [])
        range_computed = [e for e in stream_evts if e.get('event') == 'RANGE_COMPUTE_COMPLETE']
        range_locked = [e for e in stream_evts if e.get('event') == 'RANGE_LOCKED']
        hydration_summary = [e for e in stream_evts if e.get('event') == 'HYDRATION_SUMMARY']
        gap_detected = [e for e in stream_evts if e.get('event') == 'BAR_GAP_DETECTED']
        gap_violation = [e for e in stream_evts if e.get('event') == 'GAP_TOLERANCE_VIOLATION']
        range_invalidated = [e for e in stream_evts if e.get('event') == 'RANGE_INVALIDATED']
        
        print(f"  Events:")
        print(f"    HYDRATION_SUMMARY: {len(hydration_summary)}")
        print(f"    RANGE_COMPUTE_COMPLETE: {len(range_computed)}")
        print(f"    RANGE_LOCKED: {len(range_locked)}")
        print(f"    BAR_GAP_DETECTED: {len(gap_detected)}")
        print(f"    GAP_TOLERANCE_VIOLATION: {len(gap_violation)}")
        print(f"    RANGE_INVALIDATED: {len(range_invalidated)}")
        
        # Range values
        if range_computed:
            latest_range = max(range_computed, key=lambda x: x.get('ts_utc', ''))
            data = latest_range.get('data', {})
            range_high = data.get('range_high')
            range_low = data.get('range_low')
            bar_count = data.get('bar_count', 0)
            print(f"  Range Values:")
            print(f"    Range High: {range_high}")
            print(f"    Range Low: {range_low}")
            if range_high is not None and range_low is not None:
                try:
                    range_high_val = float(range_high) if isinstance(range_high, str) else range_high
                    range_low_val = float(range_low) if isinstance(range_low, str) else range_low
                    range_size = range_high_val - range_low_val
                    print(f"    Range Size: {range_size}")
                except (ValueError, TypeError):
                    print(f"    Range Size: (could not calculate)")
            print(f"    Bar Count: {bar_count}")
        
        if range_locked:
            latest_lock = max(range_locked, key=lambda x: x.get('ts_utc', ''))
            lock_data = latest_lock.get('data', {})
            print(f"  Range Locked:")
            print(f"    Time: {latest_lock.get('ts_utc', '')[:19]}")
            print(f"    Range High: {lock_data.get('range_high')}")
            print(f"    Range Low: {lock_data.get('range_low')}")
        
        # Gap analysis
        if gap_detected:
            total_gaps = len(gap_detected)
            data_feed_failures = sum(1 for e in gap_detected 
                                    if e.get('data', {}).get('gap_type_preliminary') == 'DATA_FEED_FAILURE')
            low_liquidity = sum(1 for e in gap_detected 
                              if e.get('data', {}).get('gap_type_preliminary') == 'LOW_LIQUIDITY')
            print(f"  Gap Analysis:")
            print(f"    Total Gaps: {total_gaps}")
            print(f"    DATA_FEED_FAILURE: {data_feed_failures}")
            print(f"    LOW_LIQUIDITY: {low_liquidity}")
            
            if gap_detected:
                latest_gap = max(gap_detected, key=lambda x: x.get('ts_utc', ''))
                gap_data = latest_gap.get('data', {})
                print(f"    Latest Gap:")
                print(f"      Time: {latest_gap.get('ts_utc', '')[:19]}")
                print(f"      Delta Minutes: {gap_data.get('delta_minutes')}")
                print(f"      Missing Minutes: {gap_data.get('added_to_total_gap')}")
                print(f"      Total Gap Now: {gap_data.get('total_gap_now')}")
                print(f"      Largest Gap Now: {gap_data.get('largest_gap_now')}")
                print(f"      Bar Source: {gap_data.get('bar_source')}")
        
        if gap_violation:
            print(f"  WARNING: {len(gap_violation)} gap tolerance violations detected!")
            for violation in gap_violation:
                v_data = violation.get('data', {})
                print(f"    - {violation.get('ts_utc', '')[:19]}: {v_data.get('violation_reason', 'N/A')}")
        
        if range_invalidated:
            print(f"  ERROR: Range was invalidated!")
            for inv in range_invalidated:
                print(f"    - {inv.get('ts_utc', '')[:19]}: {inv.get('data', {}).get('reason', 'N/A')}")
        
        # Errors
        errors = [e for e in stream_evts if 'ERROR' in e.get('level', '') or 'error' in e.get('event', '').lower()]
        if errors:
            print(f"  Errors: {len(errors)} error events found")
            for err in errors[-5:]:  # Show last 5 errors
                print(f"    - {err.get('ts_utc', '')[:19]}: {err.get('event', 'UNKNOWN')}")
        
        print()
    
    # Overall summary
    print("="*80)
    print("OVERALL SUMMARY")
    print("="*80)
    
    committed_streams = [s for s, j in journals.items() if j.get('Committed', False)]
    uncommitted_streams = [s for s, j in journals.items() if not j.get('Committed', False)]
    streams_with_ranges = [s for s in streams_to_check if stream_events.get(s) and 
                          any(e.get('event') == 'RANGE_COMPUTE_COMPLETE' for e in stream_events[s])]
    streams_locked = [s for s in streams_to_check if stream_events.get(s) and 
                     any(e.get('event') == 'RANGE_LOCKED' for e in stream_events[s])]
    
    print(f"Total Streams: {len(streams_to_check)}")
    print(f"Committed: {len(committed_streams)}")
    print(f"Uncommitted: {len(uncommitted_streams)}")
    print(f"Ranges Computed: {len(streams_with_ranges)}")
    print(f"Ranges Locked: {len(streams_locked)}")
    
    if committed_streams:
        print(f"\nCommitted Streams: {', '.join(committed_streams)}")
        for stream in committed_streams:
            reason = journals[stream].get('CommitReason', 'UNKNOWN')
            print(f"  {stream}: {reason}")
    
    if len(streams_with_ranges) < len(streams_to_check):
        missing = set(streams_to_check) - set(streams_with_ranges)
        print(f"\nStreams WITHOUT ranges: {', '.join(sorted(missing))}")
    
    if len(streams_locked) < len(streams_with_ranges):
        not_locked = set(streams_with_ranges) - set(streams_locked)
        print(f"\nStreams with ranges but NOT locked: {', '.join(sorted(not_locked))}")
    
    print()
    print("="*80)

if __name__ == "__main__":
    import argparse
    
    parser = argparse.ArgumentParser(
        description="Check if all ranges have been computed properly and verify overall status"
    )
    
    parser.add_argument(
        "--project-root",
        type=str,
        help="Path to project root (defaults to current directory)"
    )
    
    parser.add_argument(
        "--trading-date",
        type=str,
        help="Trading date in YYYY-MM-DD format (defaults to today)"
    )
    
    args = parser.parse_args()
    
    check_ranges_and_status(
        project_root=args.project_root,
        trading_date=args.trading_date
    )
