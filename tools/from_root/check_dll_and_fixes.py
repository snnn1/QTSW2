#!/usr/bin/env python3
"""
Check if new DLL is being used and if fixes are working
"""
import json
import os
import datetime
from pathlib import Path
from collections import defaultdict

def load_recent_events(hours=2):
    """Load events from last N hours"""
    events = []
    log_dir = Path("logs/robot")
    
    log_files = [
        "robot_ENGINE.jsonl",
        "frontend_feed.jsonl",
    ]
    
    cutoff = datetime.datetime.now(datetime.timezone.utc) - datetime.timedelta(hours=hours)
    
    for log_file in log_files:
        log_path = log_dir / log_file
        if not log_path.exists():
            continue
            
        try:
            with open(log_path, 'r', encoding='utf-8', errors='ignore') as f:
                for line in f:
                    line = line.strip()
                    if not line:
                        continue
                    try:
                        event = json.loads(line)
                        if 'timestamp_utc' in event:
                            ts = datetime.datetime.fromisoformat(
                                event['timestamp_utc'].replace('Z', '+00:00')
                            )
                            if ts > cutoff:
                                events.append(event)
                    except json.JSONDecodeError:
                        continue
        except Exception as e:
            print(f"[WARNING] Error reading {log_file}: {e}")
    
    return sorted(events, key=lambda x: x.get('timestamp_utc', ''))

def check_dll_evidence(events):
    """Check for evidence of new DLL (fix-related events)"""
    print("\n" + "="*80)
    print("DLL VERSION CHECK - Evidence of New Fixes")
    print("="*80)
    
    # Check for fix-specific events
    fix_events = {
        'RACE_CONDITION_RESOLVED': [],
        'UNTrackED_FILL_FLATTENED': [],
        'UNKNOWN_ORDER_FILL_FLATTENED': [],
        'ORPHAN_FILL_CRITICAL': [],
        'INTENT_NOT_FOUND_FLATTENED': [],
        'PROTECTIVE_STOP_SUBMITTED': [],
        'PROTECTIVE_TARGET_SUBMITTED': [],
        'BE_STOP_MODIFIED': [],
        'BE_TRIGGER_DETECTED': [],
    }
    
    for event in events:
        event_type = event.get('event_type', '')
        for fix_type in fix_events.keys():
            if fix_type in event_type:
                fix_events[fix_type].append(event)
    
    # Report findings
    found_any = False
    for fix_type, found_events in fix_events.items():
        if found_events:
            found_any = True
            print(f"\n✅ {fix_type}: {len(found_events)} events found")
            if len(found_events) <= 3:
                for e in found_events:
                    ts = e.get('timestamp_utc', '')[:19]
                    print(f"   - {ts}")
            else:
                first = found_events[0].get('timestamp_utc', '')[:19]
                last = found_events[-1].get('timestamp_utc', '')[:19]
                print(f"   - First: {first}, Last: {last}")
        else:
            print(f"\n[INFO] {fix_type}: No events found (may be normal if no triggers)")
    
    if not found_any:
        print("\n[INFO] No fix-specific events found in recent logs")
        print("   This could mean:")
        print("   1. DLL not restarted yet (old DLL still loaded)")
        print("   2. No conditions triggering these fixes yet")
        print("   3. System running normally (no issues to fix)")
    
    return fix_events

def check_entry_fills_and_protective(events):
    """Check entry fills and protective order submission"""
    print("\n" + "="*80)
    print("ENTRY FILLS AND PROTECTIVE ORDERS")
    print("="*80)
    
    entry_fills = [e for e in events if 'ENTRY_FILL' in e.get('event_type', '') or 
                   ('EXECUTION' in e.get('event_type', '') and 'FILL' in e.get('event_type', '') and 
                    e.get('order_type') == 'ENTRY')]
    
    protective_stops = [e for e in events if 'PROTECTIVE_STOP' in e.get('event_type', '')]
    protective_targets = [e for e in events if 'PROTECTIVE_TARGET' in e.get('event_type', '')]
    
    print(f"\nEntry Fills: {len(entry_fills)}")
    print(f"Protective Stop Orders: {len(protective_stops)}")
    print(f"Protective Target Orders: {len(protective_targets)}")
    
    if entry_fills:
        print(f"\nRecent Entry Fills:")
        for e in entry_fills[-5:]:
            ts = e.get('timestamp_utc', '')[:19]
            intent = e.get('intent_id', 'UNKNOWN')[:12]
            qty = e.get('fill_quantity', e.get('filled_total', '?'))
            print(f"   - {ts} | Intent: {intent} | Qty: {qty}")
    
    if protective_stops:
        print(f"\nRecent Protective Stops:")
        for e in protective_stops[-5:]:
            ts = e.get('timestamp_utc', '')[:19]
            intent = e.get('intent_id', 'UNKNOWN')[:12]
            qty = e.get('quantity', '?')
            print(f"   - {ts} | Intent: {intent} | Qty: {qty}")
    
    # Check ratio
    if entry_fills and protective_stops:
        ratio = len(protective_stops) / len(entry_fills)
        if ratio >= 0.8:
            print(f"\n[OK] Protective orders being submitted ({ratio:.1%} of entry fills)")
        else:
            print(f"\n[WARNING] Low protective order ratio ({ratio:.1%} of entry fills)")

def check_errors_and_critical(events):
    """Check for errors and critical issues"""
    print("\n" + "="*80)
    print("ERRORS AND CRITICAL EVENTS")
    print("="*80)
    
    error_types = defaultdict(int)
    critical_events = []
    
    for event in events:
        event_type = event.get('event_type', '')
        if any(x in event_type for x in ['ERROR', 'FAILED', 'CRITICAL', 'EXCEPTION']):
            error_types[event_type] += 1
            if 'CRITICAL' in event_type or 'FAILED' in event_type:
                critical_events.append(event)
    
    print(f"\nTotal Error/Critical Events: {sum(error_types.values())}")
    
    if error_types:
        print("\nError Types:")
        for err_type, count in sorted(error_types.items(), key=lambda x: -x[1])[:10]:
            print(f"   - {err_type}: {count}")
    
    if critical_events:
        print(f"\n[WARNING] Critical Events ({len(critical_events)}):")
        for e in critical_events[-10:]:
            ts = e.get('timestamp_utc', '')[:19]
            event_type = e.get('event_type', 'UNKNOWN')
            note = e.get('note', e.get('error', ''))[:60]
            print(f"   - {ts} | {event_type}")
            if note:
                print(f"     {note}")
    else:
        print("\n[OK] No critical events in recent logs")

def check_dll_timestamp():
    """Check DLL file timestamps"""
    print("\n" + "="*80)
    print("DLL FILE STATUS")
    print("="*80)
    
    source_dll = Path("RobotCore_For_NinjaTrader/bin/Release/net48/Robot.Core.dll")
    nt_dll = Path("C:/Users/jakej/Documents/NinjaTrader 8/bin/Custom/Robot.Core.dll")
    
    if source_dll.exists():
        source_time = datetime.datetime.fromtimestamp(source_dll.stat().st_mtime)
        source_size = source_dll.stat().st_size
        print(f"\nSource DLL:")
        print(f"   Path: {source_dll}")
        print(f"   Last Modified: {source_time}")
        print(f"   Size: {source_size:,} bytes")
    else:
        print(f"\n[WARNING] Source DLL not found: {source_dll}")
    
    if nt_dll.exists():
        nt_time = datetime.datetime.fromtimestamp(nt_dll.stat().st_mtime)
        nt_size = nt_dll.stat().st_size
        print(f"\nNinjaTrader DLL:")
        print(f"   Path: {nt_dll}")
        print(f"   Last Modified: {nt_time}")
        print(f"   Size: {nt_size:,} bytes")
        
        if source_dll.exists():
            if abs((source_time - nt_time).total_seconds()) < 60:
                print(f"\n[OK] DLLs are synced (timestamps match)")
            else:
                print(f"\n[WARNING] DLL timestamps differ by {(source_time - nt_time).total_seconds():.0f} seconds")
            
            if source_size == nt_size:
                print(f"[OK] DLL sizes match")
            else:
                print(f"[WARNING] DLL sizes differ (source: {source_size}, NT: {nt_size})")
    else:
        print(f"\n[WARNING] NinjaTrader DLL not found: {nt_dll}")

def main():
    print("="*80)
    print("DLL AND FIXES VERIFICATION")
    print("="*80)
    print(f"Checking logs from last 2 hours...")
    print(f"Current time: {datetime.datetime.now(datetime.timezone.utc)}")
    
    # Check DLL files
    check_dll_timestamp()
    
    # Load recent events
    events = load_recent_events(hours=2)
    print(f"\nLoaded {len(events)} events from last 2 hours")
    
    if not events:
        print("\n[WARNING] No recent events found. Is NinjaTrader running?")
        return
    
    # Check for fix evidence
    fix_events = check_dll_evidence(events)
    
    # Check entry fills and protective orders
    check_entry_fills_and_protective(events)
    
    # Check for errors
    check_errors_and_critical(events)
    
    print("\n" + "="*80)
    print("SUMMARY")
    print("="*80)
    
    # Determine if new DLL is likely in use
    has_fix_events = any(len(events) > 0 for events in fix_events.values())
    has_recent_activity = len(events) > 100
    
    if has_fix_events:
        print("\n[OK] Evidence suggests NEW DLL is in use (fix-related events found)")
    elif has_recent_activity:
        print("\n[INFO] Recent activity found but no fix-specific events")
        print("   This could mean:")
        print("   1. DLL not restarted yet (restart NinjaTrader)")
        print("   2. No conditions triggering fixes (normal operation)")
    else:
        print("\n[WARNING] No recent activity found")
        print("   Is NinjaTrader running?")

if __name__ == "__main__":
    main()
