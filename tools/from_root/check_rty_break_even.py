#!/usr/bin/env python3
"""Check RTY break-even detection specifically"""
import json
from pathlib import Path
from datetime import datetime, timezone, timedelta

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
    cutoff = datetime.now(timezone.utc) - timedelta(hours=24)
    
    print("="*80)
    print("RTY BREAK-EVEN DETECTION CHECK")
    print("="*80)
    print(f"Checking logs from last 24 hours\n")
    
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
    
    # Find RTY events
    rty_events = [e for e in events if 'RTY' in str(e.get('data', {}).get('stream', '')) or 'RTY' in str(e.get('data', {}).get('stream_id', ''))]
    
    # RTY entry fills
    rty_fills = [e for e in rty_events if 'EXECUTION_FILLED' in e.get('event', '') or 'ENTRY_FILL' in e.get('event', '').upper()]
    
    # RTY BE events
    rty_be = [e for e in rty_events if 'BE' in e.get('event', '').upper() or 'BREAK' in e.get('event', '').upper()]
    rty_be_reached = [e for e in rty_be if 'REACHED' in e.get('event', '').upper()]
    rty_be_failed = [e for e in rty_be if 'FAILED' in e.get('event', '').upper()]
    rty_be_retry = [e for e in rty_be if 'RETRY' in e.get('event', '').upper()]
    
    # RTY stop modifications
    rty_stop_modify = [e for e in rty_events if 'STOP_MODIFY' in e.get('event', '').upper()]
    
    print("1. RTY ENTRY FILLS")
    print("-"*80)
    if rty_fills:
        for e in rty_fills:
            ts = parse_timestamp(e.get('ts_utc', ''))
            data = e.get('data', {})
            intent_id = data.get('intent_id', 'N/A')[:8] if data.get('intent_id') else 'N/A'
            fill_price = data.get('fill_price', data.get('actual_fill_price', 'N/A'))
            direction = data.get('direction', 'N/A')
            print(f"  {direction} | Intent: {intent_id} | Price: {fill_price} | {ts.strftime('%H:%M:%S') if ts else 'N/A'} UTC")
    else:
        print("  No RTY entry fills found")
    
    print("\n2. RTY BREAK-EVEN TRIGGER EVENTS")
    print("-"*80)
    print(f"BE_TRIGGER_REACHED: {len(rty_be_reached)}")
    print(f"BE_TRIGGER_FAILED: {len(rty_be_failed)}")
    print(f"BE_TRIGGER_RETRY_NEEDED: {len(rty_be_retry)}")
    
    if rty_be_reached:
        print("\n  BE_TRIGGER_REACHED events:")
        for e in rty_be_reached:
            ts = parse_timestamp(e.get('ts_utc', ''))
            data = e.get('data', {})
            intent_id = data.get('intent_id', 'N/A')[:8] if data.get('intent_id') else 'N/A'
            be_stop = data.get('be_stop_price', 'N/A')
            trigger = data.get('be_trigger_price', 'N/A')
            print(f"    Intent: {intent_id} | Trigger: {trigger} | BE Stop: {be_stop} | {ts.strftime('%H:%M:%S') if ts else 'N/A'} UTC")
    
    if rty_be_retry:
        print(f"\n  BE_TRIGGER_RETRY_NEEDED events ({len(rty_be_retry)}):")
        for e in sorted(rty_be_retry, key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))[-5:]:
            ts = parse_timestamp(e.get('ts_utc', ''))
            data = e.get('data', {})
            intent_id = data.get('intent_id', 'N/A')[:8] if data.get('intent_id') else 'N/A'
            error = data.get('error', 'N/A')[:60]
            print(f"    Intent: {intent_id} | {error} | {ts.strftime('%H:%M:%S') if ts else 'N/A'} UTC")
    
    if rty_be_failed:
        print(f"\n  BE_TRIGGER_FAILED events:")
        for e in rty_be_failed:
            ts = parse_timestamp(e.get('ts_utc', ''))
            data = e.get('data', {})
            intent_id = data.get('intent_id', 'N/A')[:8] if data.get('intent_id') else 'N/A'
            error = data.get('error', 'N/A')[:60]
            print(f"    Intent: {intent_id} | {error} | {ts.strftime('%H:%M:%S') if ts else 'N/A'} UTC")
    
    print("\n3. RTY STOP MODIFICATION EVENTS")
    print("-"*80)
    if rty_stop_modify:
        for e in sorted(rty_stop_modify, key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc)):
            ts = parse_timestamp(e.get('ts_utc', ''))
            data = e.get('data', {})
            intent_id = data.get('intent_id', 'N/A')[:8] if data.get('intent_id') else 'N/A'
            be_stop = data.get('be_stop_price', 'N/A')
            error = data.get('error', '')
            status = 'SUCCESS' if 'SUCCESS' in e.get('event', '') else 'FAILED' if error else 'UNKNOWN'
            print(f"  {e.get('event')} | Intent: {intent_id} | BE Stop: {be_stop} | {status} | {ts.strftime('%H:%M:%S') if ts else 'N/A'} UTC")
    else:
        print("  No RTY stop modification events found")
    
    print("\n4. SUMMARY")
    print("-"*80)
    if rty_fills:
        print(f"[OK] RTY entry fills: {len(rty_fills)}")
        if rty_be_reached:
            print(f"[OK] BE triggers reached: {len(rty_be_reached)}")
            if rty_stop_modify and any('SUCCESS' in e.get('event', '') for e in rty_stop_modify):
                print("[OK] BE stop modifications: SUCCESS")
                print("   Status: Break-even detection and modification working on RTY")
            else:
                print("[WARN] BE stop modifications: FAILED or NOT FOUND")
                print("   Status: BE triggers detected but modifications not successful")
        elif rty_be_retry:
            print(f"[WARN] BE triggers: {len(rty_be_retry)} retry attempts (stop order not found)")
            print("   Status: BE detection working but stop order tag mismatch (fixed in code)")
        else:
            print("[ERROR] BE triggers: 0 events")
            print("   Status: BE detection not working")
    else:
        print("[INFO] No RTY entry fills - cannot verify BE detection")
    
    print("\n" + "="*80)

if __name__ == "__main__":
    main()
