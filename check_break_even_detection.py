#!/usr/bin/env python3
"""Check if break-even detection and modification is working"""
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
    cutoff = datetime.now(timezone.utc) - timedelta(hours=24)  # Check last 24 hours
    
    print("="*80)
    print("BREAK-EVEN DETECTION VERIFICATION")
    print("="*80)
    print(f"Checking logs from last 24 hours (since {cutoff.strftime('%Y-%m-%d %H:%M:%S')} UTC)\n")
    
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
    
    events.sort(key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
    
    # Find break-even related events
    be_events = [e for e in events if 'BREAK' in e.get('event', '').upper() and 'EVEN' in e.get('event', '').upper()]
    be_trigger = [e for e in events if 'BE_TRIGGER' in e.get('event', '').upper() or 'BREAK_EVEN_TRIGGER' in e.get('event', '').upper()]
    be_trigger_reached = [e for e in be_trigger if 'REACHED' in e.get('event', '').upper()]
    be_trigger_failed = [e for e in be_trigger if 'FAILED' in e.get('event', '').upper()]
    be_trigger_retry = [e for e in be_trigger if 'RETRY' in e.get('event', '').upper()]
    be_modify = [e for e in events if 'BE_MODIFY' in e.get('event', '').upper() or 'BREAK_EVEN_MODIFY' in e.get('event', '').upper() or 'MODIFY_STOP_BE' in e.get('event', '').upper()]
    be_stop = [e for e in events if 'BE_STOP' in e.get('event', '').upper()]
    stop_modify = [e for e in events if 'STOP_MODIFY' in e.get('event', '').upper()]
    
    # Find entry fills (which should trigger BE detection)
    entry_fills = [e for e in events if 'EXECUTION_FILLED' in e.get('event', '') or 'ENTRY_FILL' in e.get('event', '').upper()]
    
    # Find protective order modifications
    stop_modifications = [e for e in events if 'MODIFY' in e.get('event', '').upper() and 'STOP' in e.get('event', '').upper()]
    
    print("1. ENTRY FILLS (should trigger BE detection)")
    print("-"*80)
    if entry_fills:
        print(f"Found {len(entry_fills)} entry fill events:")
        for e in sorted(entry_fills, key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))[-10:]:
            ts = parse_timestamp(e.get('ts_utc', ''))
            data = e.get('data', {})
            stream = data.get('stream', data.get('stream_id', 'N/A'))
            intent_id = data.get('intent_id', 'N/A')[:8] if data.get('intent_id') else 'N/A'
            fill_price = data.get('fill_price', data.get('actual_fill_price', 'N/A'))
            direction = data.get('direction', 'N/A')
            print(f"  {stream} | {direction} | Intent: {intent_id} | Price: {fill_price} | {ts.strftime('%H:%M:%S') if ts else 'N/A'} UTC")
    else:
        print("  No entry fills found")
    
    print("\n2. BREAK-EVEN TRIGGER DETECTION")
    print("-"*80)
    print(f"BE_TRIGGER_REACHED: {len(be_trigger_reached)} events")
    print(f"BE_TRIGGER_FAILED: {len(be_trigger_failed)} events")
    print(f"BE_TRIGGER_RETRY_NEEDED: {len(be_trigger_retry)} events")
    
    if be_trigger_reached:
        print(f"\n  Recent BE_TRIGGER_REACHED events:")
        for e in sorted(be_trigger_reached, key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))[-10:]:
            ts = parse_timestamp(e.get('ts_utc', ''))
            data = e.get('data', {})
            stream = data.get('stream', data.get('stream_id', 'N/A'))
            intent_id = data.get('intent_id', 'N/A')[:8] if data.get('intent_id') else 'N/A'
            trigger_price = data.get('be_trigger_price', data.get('trigger_price', 'N/A'))
            be_stop_price = data.get('be_stop_price', 'N/A')
            print(f"    {stream} | Intent: {intent_id} | Trigger: {trigger_price} | BE Stop: {be_stop_price} | {ts.strftime('%H:%M:%S') if ts else 'N/A'} UTC")
    
    if be_trigger_failed:
        print(f"\n  Recent BE_TRIGGER_FAILED events:")
        for e in sorted(be_trigger_failed, key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))[-5:]:
            ts = parse_timestamp(e.get('ts_utc', ''))
            data = e.get('data', {})
            stream = data.get('stream', data.get('stream_id', 'N/A'))
            intent_id = data.get('intent_id', 'N/A')[:8] if data.get('intent_id') else 'N/A'
            error = data.get('error', 'N/A')
            print(f"    {stream} | Intent: {intent_id} | Error: {error[:60]} | {ts.strftime('%H:%M:%S') if ts else 'N/A'} UTC")
    
    if be_trigger_retry:
        print(f"\n  Recent BE_TRIGGER_RETRY_NEEDED events:")
        for e in sorted(be_trigger_retry, key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))[-5:]:
            ts = parse_timestamp(e.get('ts_utc', ''))
            data = e.get('data', {})
            stream = data.get('stream', data.get('stream_id', 'N/A'))
            intent_id = data.get('intent_id', 'N/A')[:8] if data.get('intent_id') else 'N/A'
            error = data.get('error', 'N/A')
            print(f"    {stream} | Intent: {intent_id} | Error: {error[:60]} | {ts.strftime('%H:%M:%S') if ts else 'N/A'} UTC")
    
    if not be_trigger:
        print("  No break-even trigger events found")
        if entry_fills:
            print("  [WARNING] Entry fills exist but no BE triggers detected!")
    
    print("\n3. BREAK-EVEN STOP MODIFICATION")
    print("-"*80)
    if be_modify:
        print(f"Found {len(be_modify)} break-even modification events:")
        for e in sorted(be_modify, key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))[-10:]:
            ts = parse_timestamp(e.get('ts_utc', ''))
            data = e.get('data', {})
            stream = data.get('stream', data.get('stream_id', 'N/A'))
            old_stop = data.get('old_stop_price', data.get('current_stop', 'N/A'))
            new_stop = data.get('new_stop_price', data.get('be_stop_price', 'N/A'))
            intent_id = data.get('intent_id', 'N/A')[:8] if data.get('intent_id') else 'N/A'
            print(f"  {stream} | Intent: {intent_id} | Old Stop: {old_stop} -> New Stop: {new_stop} | {ts.strftime('%H:%M:%S') if ts else 'N/A'} UTC")
    else:
        print("  No break-even modification events found")
        if be_trigger:
            print("  [WARNING] BE triggers detected but no modifications submitted!")
    
    print("\n4. ALL BREAK-EVEN RELATED EVENTS")
    print("-"*80)
    if be_events:
        print(f"Found {len(be_events)} break-even related events:")
        event_types = defaultdict(int)
        for e in be_events:
            event_types[e.get('event', 'UNKNOWN')] += 1
        for event_type, count in sorted(event_types.items()):
            print(f"  {event_type}: {count}")
        
        print("\n  Recent BE events:")
        for e in sorted(be_events, key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))[-15:]:
            ts = parse_timestamp(e.get('ts_utc', ''))
            data = e.get('data', {})
            stream = data.get('stream', data.get('stream_id', 'N/A'))
            print(f"    {e.get('event')} | {stream} | {ts.strftime('%H:%M:%S') if ts else 'N/A'} UTC")
    else:
        print("  No break-even related events found")
    
    print("\n5. STOP MODIFICATION EVENTS (any type)")
    print("-"*80)
    if stop_modify:
        print(f"Found {len(stop_modify)} STOP_MODIFY events:")
        event_types = defaultdict(int)
        for e in stop_modify:
            event_types[e.get('event', 'UNKNOWN')] += 1
        for event_type, count in sorted(event_types.items()):
            print(f"  {event_type}: {count}")
        
        print("\n  Recent STOP_MODIFY events:")
        for e in sorted(stop_modify, key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))[-10:]:
            ts = parse_timestamp(e.get('ts_utc', ''))
            data = e.get('data', {})
            stream = data.get('stream', data.get('stream_id', 'N/A'))
            intent_id = data.get('intent_id', 'N/A')[:8] if data.get('intent_id') else 'N/A'
            error = data.get('error', '')
            print(f"    {e.get('event')} | {stream} | Intent: {intent_id} | {error[:40] if error else 'Success'} | {ts.strftime('%H:%M:%S') if ts else 'N/A'} UTC")
    else:
        print("  No STOP_MODIFY events found")
    
    if stop_modifications:
        print(f"\n  Other stop modification events: {len(stop_modifications)}")
        for e in sorted(stop_modifications, key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))[-5:]:
            ts = parse_timestamp(e.get('ts_utc', ''))
            data = e.get('data', {})
            stream = data.get('stream', data.get('stream_id', 'N/A'))
            print(f"    {e.get('event')} | {stream} | {ts.strftime('%H:%M:%S') if ts else 'N/A'} UTC")
    
    print("\n6. SUMMARY")
    print("-"*80)
    
    if entry_fills:
        print(f"[OK] Entry fills: {len(entry_fills)} events")
        if be_trigger:
            print(f"[OK] BE triggers detected: {len(be_trigger)} events")
            if be_modify:
                print(f"[OK] BE modifications submitted: {len(be_modify)} events")
                print("   Status: Break-even detection and modification working correctly")
            else:
                print("[WARN] BE modifications: 0 events")
                print("   Status: BE triggers detected but modifications not submitted")
        else:
            print("[ERROR] BE triggers: 0 events")
            print("   Status: Entry fills exist but BE triggers not detected")
    else:
        print("[INFO] No entry fills in last 24 hours - cannot verify BE detection")
    
    print("\n" + "="*80)
    print("VERIFICATION COMPLETE")
    print("="*80)

if __name__ == "__main__":
    main()
