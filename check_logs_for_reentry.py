#!/usr/bin/env python3
"""
Check logs for re-entry issues and verify if fixes are working.
"""

import json
import sys
from pathlib import Path
from datetime import datetime, timedelta
from collections import defaultdict

def parse_timestamp(ts_str):
    """Parse timestamp string to datetime."""
    if not ts_str:
        return None
    try:
        # Try ISO format first
        if 'T' in ts_str or '+' in ts_str or 'Z' in ts_str:
            return datetime.fromisoformat(ts_str.replace('Z', '+00:00'))
        # Try other formats
        for fmt in ['%Y-%m-%d %H:%M:%S.%f', '%Y-%m-%d %H:%M:%S', '%Y/%m/%d %H:%M:%S']:
            try:
                return datetime.strptime(ts_str[:19], fmt)
            except:
                continue
    except:
        pass
    return None

def load_log_events(log_file):
    """Load events from JSONL file."""
    events = []
    try:
        with open(log_file, 'r', encoding='utf-8') as f:
            for line in f:
                line = line.strip()
                if not line:
                    continue
                try:
                    event = json.loads(line)
                    events.append(event)
                except json.JSONDecodeError:
                    continue
    except Exception as e:
        print(f"Error reading {log_file}: {e}", file=sys.stderr)
    return events

def main():
    log_dir = Path("logs/robot")
    if not log_dir.exists():
        print(f"ERROR: Log directory not found: {log_dir}")
        return 1
    
    # Find recent log files
    cutoff = datetime.now() - timedelta(hours=24)
    log_files = []
    for f in log_dir.glob("*.jsonl"):
        try:
            mtime = datetime.fromtimestamp(f.stat().st_mtime)
            if mtime > cutoff:
                log_files.append((f, mtime))
        except:
            pass
    
    log_files.sort(key=lambda x: x[1], reverse=True)
    
    print(f"Found {len(log_files)} recent log file(s)\n")
    
    # Load events from ENGINE and instrument logs
    all_events = []
    for log_file, mtime in log_files[:10]:  # Check first 10 files
        if 'ENGINE' in log_file.name or any(inst in log_file.name for inst in ['ES', 'M2K', 'RTY', 'NQ', 'YM']):
            events = load_log_events(log_file)
            all_events.extend(events)
            print(f"Loaded {len(events)} events from {log_file.name}")
    
    print(f"\nTotal events: {len(all_events)}\n")
    
    # Check for key events
    print("=== CHECKING FOR FIX IMPLEMENTATION ===")
    
    check_events = [e for e in all_events if 'CHECK_ALL_INSTRUMENTS' in e.get('event_type', '')]
    print(f"CheckAllInstrumentsForFlatPositions calls: {len(check_events)}")
    if check_events:
        print("  Recent calls:")
        for e in check_events[-5:]:
            print(f"    {e.get('timestamp', '')[:19]} {e.get('event_type', '')}")
    else:
        print("  [WARN] No CheckAllInstrumentsForFlatPositions events found!")
    
    cancel_flat_events = [e for e in all_events if 'ENTRY_STOP_CANCELLED_ON_POSITION_FLAT' in e.get('event_type', '')]
    print(f"\nEntry stops cancelled on position flat: {len(cancel_flat_events)}")
    if cancel_flat_events:
        print("  Recent cancellations:")
        for e in cancel_flat_events[-5:]:
            print(f"    {e.get('timestamp', '')[:19]} {e.get('instrument', '')} intent={e.get('cancelled_entry_intent_id', '')[:30]}")
    
    defensive_events = [e for e in all_events if 'OPPOSITE_ENTRY_CANCELLED_DEFENSIVELY' in e.get('event_type', '')]
    print(f"\nDefensive opposite entry cancellations: {len(defensive_events)}")
    if defensive_events:
        print("  Recent cancellations:")
        for e in defensive_events[-5:]:
            print(f"    {e.get('timestamp', '')[:19]} {e.get('cancelled_intent_id', '')[:30]} -> {e.get('opposite_intent_id', '')[:30]}")
    
    print("\n=== CHECKING FOR RE-ENTRY ISSUES ===")
    
    # Group by instrument
    by_instrument = defaultdict(list)
    for event in all_events:
        instrument = event.get('instrument', event.get('Instrument', 'UNKNOWN'))
        timestamp = event.get('timestamp', event.get('Timestamp', event.get('timestamp_utc', '')))
        by_instrument[instrument].append((timestamp, event))
    
    issues_found = []
    
    for instrument, inst_events in by_instrument.items():
        if instrument == 'UNKNOWN':
            continue
        
        inst_events.sort(key=lambda x: x[0])
        
        # Look for closure -> entry fill pattern
        for i, (ts1, evt1) in enumerate(inst_events):
            event_type = evt1.get('event_type', evt1.get('EventType', ''))
            
            # Check if this is a closure
            is_closure = (
                'FLATTEN' in event_type.upper() or 
                'EXECUTION_EXIT_FILL' in event_type or
                ('EXECUTION_FILLED' in event_type and 
                 ('STOP' in event_type or 'TARGET' in event_type or 
                  evt1.get('exit_order_type', '') in ['STOP', 'TARGET']))
            )
            
            if not is_closure:
                continue
            
            closure_time = parse_timestamp(ts1)
            if not closure_time:
                continue
            
            # Look for entry fills within 10 seconds
            for j in range(i + 1, min(i + 200, len(inst_events))):
                ts2, evt2 = inst_events[j]
                evt2_time = parse_timestamp(ts2)
                if not evt2_time:
                    continue
                
                time_diff = (evt2_time - closure_time).total_seconds()
                if time_diff > 10:
                    break
                
                evt2_type = evt2.get('event_type', evt2.get('EventType', ''))
                evt2_tag = evt2.get('tag', evt2.get('Tag', ''))
                
                # Check if this is an entry fill
                is_entry = (
                    'EXECUTION_ENTRY_FILL' in evt2_type or
                    ('EXECUTION_FILLED' in evt2_type and 
                     evt2_tag and 
                     not evt2_tag.endswith(':STOP') and 
                     not evt2_tag.endswith(':TARGET') and
                     evt2.get('order_type', '') != 'STOP' and
                     evt2.get('order_type', '') != 'TARGET')
                )
                
                if is_entry:
                    # Check for cancellations between
                    cancellations = []
                    for k in range(i + 1, j):
                        ts3, evt3 = inst_events[k]
                        evt3_type = evt3.get('event_type', evt3.get('EventType', ''))
                        if any(x in evt3_type.upper() for x in ['CANCELLED', 'CANCEL']):
                            cancellations.append((ts3, evt3_type))
                    
                    issues_found.append({
                        'instrument': instrument,
                        'closure_time': ts1[:19] if len(ts1) > 19 else ts1,
                        'closure_type': event_type,
                        'reentry_time': ts2[:19] if len(ts2) > 19 else ts2,
                        'reentry_tag': evt2_tag[:50],
                        'time_diff': f"{time_diff:.2f}s",
                        'cancellations': len(cancellations)
                    })
                    break
    
    if issues_found:
        print(f"\n[ERROR] Found {len(issues_found)} re-entry issue(s):\n")
        for i, issue in enumerate(issues_found, 1):
            print(f"Issue #{i}:")
            print(f"  Instrument: {issue['instrument']}")
            print(f"  Closure: {issue['closure_type']} at {issue['closure_time']}")
            print(f"  Re-entry: Entry fill at {issue['reentry_time']} ({issue['time_diff']} later)")
            print(f"  Tag: {issue['reentry_tag']}")
            print(f"  Cancellations between: {issue['cancellations']}")
            if issue['cancellations'] == 0:
                print("  [ERROR] NO CANCELLATION EVENTS - Fix not working!")
            print()
    else:
        print("\n[OK] No re-entry issues found in recent logs")
    
    return 1 if issues_found else 0

if __name__ == '__main__':
    sys.exit(main())
