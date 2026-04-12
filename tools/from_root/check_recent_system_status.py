#!/usr/bin/env python3
"""Comprehensive check of recent system status"""
import json
from pathlib import Path
from datetime import datetime, timezone, timedelta
from collections import defaultdict

def parse_timestamp(ts_str: str):
    """Parse ISO timestamp"""
    if not ts_str:
        return None
    try:
        if 'T' in ts_str:
            if ts_str.endswith('Z'):
                ts_str = ts_str[:-1] + '+00:00'
            elif '+' not in ts_str:
                ts_str = ts_str + '+00:00'
            dt = datetime.fromisoformat(ts_str)
            if dt.tzinfo is None:
                dt = dt.replace(tzinfo=timezone.utc)
            return dt
    except:
        pass
    return None

def main():
    log_dir = Path("logs/robot")
    cutoff = datetime.now(timezone.utc) - timedelta(hours=1)  # Last hour
    
    print("="*80)
    print("RECENT SYSTEM STATUS CHECK")
    print("="*80)
    print(f"Checking logs from last hour (since {cutoff.strftime('%Y-%m-%d %H:%M:%S')} UTC)\n")
    
    # Load events
    events = []
    for log_file in sorted(log_dir.glob("robot_*.jsonl"), reverse=True):
        try:
            with open(log_file, 'r', encoding='utf-8') as f:
                for line in f:
                    line = line.strip()
                    if line:
                        try:
                            e = json.loads(line)
                            ts = parse_timestamp(e.get('ts_utc', ''))
                            if ts and ts >= cutoff:
                                events.append(e)
                        except:
                            pass
        except:
            pass
    
    if not events:
        print("No events found in last hour")
        return
    
    events.sort(key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
    
    print(f"Total events: {len(events)}\n")
    
    # 1. Stop Bracket Submission
    print("1. STOP BRACKET SUBMISSION")
    print("-"*80)
    bracket_submitted = [e for e in events if 'STOP_BRACKETS_SUBMITTED' in e.get('event', '')]
    bracket_failed = [e for e in events if 'STOP_BRACKETS_SUBMIT_FAILED' in e.get('event', '')]
    bracket_entered = [e for e in events if 'STOP_BRACKETS_SUBMIT_ENTERED' in e.get('event', '')]
    
    print(f"STOP_BRACKETS_SUBMITTED: {len(bracket_submitted)}")
    print(f"STOP_BRACKETS_SUBMIT_FAILED: {len(bracket_failed)}")
    print(f"STOP_BRACKETS_SUBMIT_ENTERED: {len(bracket_entered)}")
    
    if bracket_submitted:
        print("\n  Recent submissions:")
        for e in bracket_submitted[-3:]:
            ts = parse_timestamp(e.get('ts_utc', ''))
            data = e.get('data', {})
            stream = data.get('stream_id', 'N/A')
            print(f"    {stream} | {ts.strftime('%H:%M:%S') if ts else 'N/A'} UTC")
    
    # 2. Entry Fills
    print("\n2. ENTRY FILLS")
    print("-"*80)
    entry_fills = [e for e in events if 'EXECUTION_FILLED' in e.get('event', '')]
    print(f"Entry fills: {len(entry_fills)}")
    
    if entry_fills:
        print("\n  Recent fills:")
        for e in entry_fills[-5:]:
            ts = parse_timestamp(e.get('ts_utc', ''))
            data = e.get('data', {})
            stream = data.get('stream', data.get('stream_id', 'N/A'))
            intent_id = data.get('intent_id', 'N/A')[:8] if data.get('intent_id') else 'N/A'
            fill_price = data.get('fill_price', data.get('actual_fill_price', 'N/A'))
            direction = data.get('direction', 'N/A')
            print(f"    {stream} | {direction} | Intent: {intent_id} | Price: {fill_price} | {ts.strftime('%H:%M:%S') if ts else 'N/A'} UTC")
    
    # 3. Break-Even Detection
    print("\n3. BREAK-EVEN DETECTION")
    print("-"*80)
    be_reached = [e for e in events if 'BE_TRIGGER_REACHED' in e.get('event', '')]
    be_retry = [e for e in events if 'BE_TRIGGER_RETRY_NEEDED' in e.get('event', '')]
    be_failed = [e for e in events if 'BE_TRIGGER_FAILED' in e.get('event', '')]
    
    print(f"BE_TRIGGER_REACHED: {len(be_reached)}")
    print(f"BE_TRIGGER_RETRY_NEEDED: {len(be_retry)}")
    print(f"BE_TRIGGER_FAILED: {len(be_failed)}")
    
    if be_reached:
        print("\n  BE triggers reached:")
        for e in be_reached[-5:]:
            ts = parse_timestamp(e.get('ts_utc', ''))
            data = e.get('data', {})
            stream = data.get('stream', data.get('stream_id', 'N/A'))
            intent_id = data.get('intent_id', 'N/A')[:8] if data.get('intent_id') else 'N/A'
            be_stop = data.get('be_stop_price', 'N/A')
            print(f"    {stream} | Intent: {intent_id} | BE Stop: {be_stop} | {ts.strftime('%H:%M:%S') if ts else 'N/A'} UTC")
    
    if be_retry:
        print(f"\n  BE retry needed ({len(be_retry)} events):")
        for e in be_retry[-3:]:
            ts = parse_timestamp(e.get('ts_utc', ''))
            data = e.get('data', {})
            stream = data.get('stream', data.get('stream_id', 'N/A'))
            intent_id = data.get('intent_id', 'N/A')[:8] if data.get('intent_id') else 'N/A'
            error = data.get('error', 'N/A')[:50]
            print(f"    {stream} | Intent: {intent_id} | {error} | {ts.strftime('%H:%M:%S') if ts else 'N/A'} UTC")
    
    # 4. Stop Modifications
    print("\n4. STOP MODIFICATIONS")
    print("-"*80)
    stop_modify_success = [e for e in events if 'STOP_MODIFY_SUCCESS' in e.get('event', '')]
    stop_modify_fail = [e for e in events if 'STOP_MODIFY_FAIL' in e.get('event', '')]
    
    print(f"STOP_MODIFY_SUCCESS: {len(stop_modify_success)}")
    print(f"STOP_MODIFY_FAIL: {len(stop_modify_fail)}")
    
    if stop_modify_success:
        print("\n  Successful modifications:")
        for e in stop_modify_success[-5:]:
            ts = parse_timestamp(e.get('ts_utc', ''))
            data = e.get('data', {})
            intent_id = data.get('intent_id', 'N/A')[:8] if data.get('intent_id') else 'N/A'
            be_stop = data.get('be_stop_price', 'N/A')
            print(f"    Intent: {intent_id} | BE Stop: {be_stop} | {ts.strftime('%H:%M:%S') if ts else 'N/A'} UTC")
    
    # 5. Protective Orders
    print("\n5. PROTECTIVE ORDERS")
    print("-"*80)
    protective_submitted = [e for e in events if 'PROTECTIVE_ORDERS_SUBMITTED' in e.get('event', '')]
    print(f"PROTECTIVE_ORDERS_SUBMITTED: {len(protective_submitted)}")
    
    if protective_submitted:
        print("\n  Recent protective orders:")
        for e in protective_submitted[-3:]:
            ts = parse_timestamp(e.get('ts_utc', ''))
            data = e.get('data', {})
            stream = data.get('stream', data.get('stream_id', 'N/A'))
            intent_id = data.get('intent_id', 'N/A')[:8] if data.get('intent_id') else 'N/A'
            print(f"    {stream} | Intent: {intent_id} | {ts.strftime('%H:%M:%S') if ts else 'N/A'} UTC")
    
    # 6. Errors and Warnings
    print("\n6. ERRORS AND WARNINGS")
    print("-"*80)
    errors = [e for e in events if e.get('level', '').upper() == 'ERROR' or 'ERROR' in e.get('event', '').upper() or 'FAILED' in e.get('event', '').upper() or 'CRITICAL' in e.get('event', '').upper()]
    critical_errors = [e for e in errors if 'CRITICAL' in e.get('event', '').upper() or 'CRITICAL' in str(e.get('data', {})).upper()]
    
    print(f"Total errors/warnings: {len(errors)}")
    print(f"Critical errors: {len(critical_errors)}")
    
    if critical_errors:
        print("\n  Critical errors:")
        for e in critical_errors[-5:]:
            ts = parse_timestamp(e.get('ts_utc', ''))
            event_type = e.get('event', 'UNKNOWN')
            data = e.get('data', {})
            error_msg = data.get('error', data.get('exception_message', 'N/A'))[:60]
            print(f"    {event_type} | {error_msg} | {ts.strftime('%H:%M:%S') if ts else 'N/A'} UTC")
    
    # 7. Summary
    print("\n7. SUMMARY")
    print("-"*80)
    
    # Check if break-even is working
    if entry_fills:
        if be_reached:
            if stop_modify_success:
                print("[OK] Break-even detection: WORKING")
                print("  - Entry fills detected")
                print("  - BE triggers reached")
                print("  - Stop modifications successful")
            elif be_retry:
                print("[WARN] Break-even detection: PARTIALLY WORKING")
                print("  - Entry fills detected")
                print("  - BE triggers reached but modifications retrying")
                print("  - May be race condition (stop order not found yet)")
            else:
                print("[WARN] Break-even detection: TRIGGERS WORKING, MODIFICATIONS NOT")
                print("  - Entry fills detected")
                print("  - BE triggers reached but no modifications")
        else:
            print("[INFO] Break-even detection: NO TRIGGERS YET")
            print("  - Entry fills detected but BE trigger price not reached")
    else:
        print("[INFO] No entry fills in last hour - cannot verify BE detection")
    
    # Check stop brackets
    if bracket_submitted:
        print("\n[OK] Stop bracket submission: WORKING")
    elif bracket_failed:
        print("\n[ERROR] Stop bracket submission: FAILING")
        print(f"  - {len(bracket_failed)} failures")
    else:
        print("\n[INFO] No stop bracket submissions in last hour")
    
    # Overall status
    print("\n" + "="*80)
    if critical_errors:
        print("STATUS: ISSUES DETECTED - Check critical errors above")
    elif errors:
        print("STATUS: MINOR ISSUES - Some errors/warnings but no critical issues")
    else:
        print("STATUS: HEALTHY - No errors detected")
    print("="*80)

if __name__ == "__main__":
    main()
