#!/usr/bin/env python3
"""
Check if ES1 is receiving bars and when the last bar was received.
"""
import json
from pathlib import Path
from datetime import datetime, timezone, timedelta
import pytz

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
    cutoff = datetime.now(timezone.utc) - timedelta(hours=2)
    
    print("="*80)
    print("ES1 BAR RECEPTION CHECK")
    print("="*80)
    
    # Load ES1 events
    es1_events = []
    for log_file in sorted(log_dir.glob("robot_*.jsonl"), reverse=True):
        try:
            with open(log_file, 'r', encoding='utf-8') as f:
                for line in f:
                    line = line.strip()
                    if line:
                        try:
                            e = json.loads(line)
                            if e.get('stream') == 'ES1':
                                ts = parse_timestamp(e.get('ts_utc', ''))
                                if ts and ts >= cutoff:
                                    es1_events.append(e)
                        except:
                            pass
        except:
            pass
    
    es1_events.sort(key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
    
    print(f"\nLoaded {len(es1_events):,} ES1 events from last 2 hours\n")
    
    # Check for bar reception events
    print("="*80)
    print("BAR RECEPTION EVENTS:")
    print("="*80)
    
    # Look for any bar-related events
    bar_received = [e for e in es1_events if 'BAR_RECEIVED' in e.get('event', '')]
    bar_admission_proof = [e for e in es1_events if e.get('event') == 'BAR_ADMISSION_PROOF']
    bar_admission_to_commit = [e for e in es1_events if e.get('event') == 'BAR_ADMISSION_TO_COMMIT_DECISION']
    bar_buffer_add_attempt = [e for e in es1_events if e.get('event') == 'BAR_BUFFER_ADD_ATTEMPT']
    bar_buffer_add_committed = [e for e in es1_events if e.get('event') == 'BAR_BUFFER_ADD_COMMITTED']
    bar_buffer_rejected = [e for e in es1_events if e.get('event') == 'BAR_BUFFER_REJECTED']
    onbarupdate_called = [e for e in es1_events if e.get('event') == 'ONBARUPDATE_CALLED']
    
    print(f"\n  BAR_RECEIVED events: {len(bar_received)}")
    print(f"  BAR_ADMISSION_PROOF: {len(bar_admission_proof)}")
    print(f"  BAR_ADMISSION_TO_COMMIT_DECISION: {len(bar_admission_to_commit)}")
    print(f"  BAR_BUFFER_ADD_ATTEMPT: {len(bar_buffer_add_attempt)}")
    print(f"  BAR_BUFFER_ADD_COMMITTED: {len(bar_buffer_add_committed)}")
    print(f"  BAR_BUFFER_REJECTED: {len(bar_buffer_rejected)}")
    print(f"  ONBARUPDATE_CALLED: {len(onbarupdate_called)}")
    
    # Get latest bar events
    now_utc = datetime.now(timezone.utc)
    chicago_tz = pytz.timezone('America/Chicago')
    now_chicago = datetime.now(chicago_tz)
    
    print(f"\n  Current Time:")
    print(f"    UTC: {now_utc.strftime('%Y-%m-%d %H:%M:%S')}")
    print(f"    Chicago: {now_chicago.strftime('%Y-%m-%d %H:%M:%S')}")
    
    # Check latest bar events
    all_bar_events = bar_admission_proof + bar_buffer_add_attempt + bar_buffer_add_committed + onbarupdate_called
    
    if all_bar_events:
        latest_bar_event = max(all_bar_events, key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
        latest_ts = parse_timestamp(latest_bar_event.get('ts_utc', ''))
        
        if latest_ts:
            age_seconds = (now_utc - latest_ts).total_seconds()
            age_minutes = age_seconds / 60
            
            print(f"\n  Latest Bar Event:")
            print(f"    Event: {latest_bar_event.get('event', 'N/A')}")
            print(f"    Time: {latest_bar_event.get('ts_utc', '')[:19]}")
            print(f"    Age: {age_minutes:.1f} minutes ago")
            
            if age_minutes > 10:
                print(f"    [WARN] No bars received for {age_minutes:.1f} minutes!")
            elif age_minutes > 5:
                print(f"    [WARN] Last bar was {age_minutes:.1f} minutes ago")
            else:
                print(f"    [OK] Bars being received recently")
    else:
        print(f"\n  [WARN] No bar events found in last 2 hours!")
    
    # Check ONBARUPDATE_CALLED (indicates NinjaTrader is calling OnBarUpdate)
    if onbarupdate_called:
        latest_onbar = max(onbarupdate_called, key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
        latest_ts = parse_timestamp(latest_onbar.get('ts_utc', ''))
        
        if latest_ts:
            age_minutes = (now_utc - latest_ts).total_seconds() / 60
            print(f"\n  Latest ONBARUPDATE_CALLED:")
            print(f"    Time: {latest_onbar.get('ts_utc', '')[:19]}")
            print(f"    Age: {age_minutes:.1f} minutes ago")
            
            data = latest_onbar.get('data', {})
            if isinstance(data, dict):
                current_bar = data.get('current_bar', 'N/A')
                engine_ready = data.get('engine_ready', 'N/A')
                print(f"    Current Bar: {current_bar}")
                print(f"    Engine Ready: {engine_ready}")
    else:
        print(f"\n  [WARN] No ONBARUPDATE_CALLED events found!")
    
    # Check recent bar activity timeline
    print("\n" + "="*80)
    print("RECENT BAR ACTIVITY TIMELINE:")
    print("="*80)
    
    # Get last 20 bar-related events
    recent_bar_events = sorted(all_bar_events, key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))[-20:]
    
    if recent_bar_events:
        print(f"\n  Last 20 bar-related events:")
        for e in recent_bar_events:
            ts = e.get('ts_utc', '')[:19] if len(e.get('ts_utc', '')) > 19 else e.get('ts_utc', '')
            event_type = e.get('event', 'N/A')
            data = e.get('data', {})
            
            # Try to extract bar time
            bar_time = None
            if isinstance(data, dict):
                bar_time = data.get('bar_time', data.get('bar_timestamp', data.get('timestamp')))
            
            if bar_time:
                print(f"    {ts} | {event_type} | bar_time={bar_time}")
            else:
                print(f"    {ts} | {event_type}")
    else:
        print(f"\n  [WARN] No recent bar events!")
    
    # Check if bars are being received but rejected
    print("\n" + "="*80)
    print("BAR REJECTION ANALYSIS:")
    print("="*80)
    
    if bar_buffer_rejected:
        latest_rejected = max(bar_buffer_rejected, key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
        latest_ts = parse_timestamp(latest_rejected.get('ts_utc', ''))
        
        if latest_ts:
            age_minutes = (now_utc - latest_ts).total_seconds() / 60
            print(f"\n  Latest BAR_BUFFER_REJECTED:")
            print(f"    Time: {latest_rejected.get('ts_utc', '')[:19]}")
            print(f"    Age: {age_minutes:.1f} minutes ago")
            
            if age_minutes < 5:
                print(f"    [INFO] Bars are being received but rejected (may be outside range window)")
    
    # Summary
    print("\n" + "="*80)
    print("SUMMARY:")
    print("="*80)
    
    if all_bar_events:
        latest_ts = parse_timestamp(max(all_bar_events, key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc)).get('ts_utc', ''))
        if latest_ts:
            age_minutes = (now_utc - latest_ts).total_seconds() / 60
            
            if age_minutes < 5:
                print(f"\n  [OK] ES1 IS receiving bars")
                print(f"    Last bar event: {age_minutes:.1f} minutes ago")
            elif age_minutes < 15:
                print(f"\n  [WARN] ES1 bars received but slowing down")
                print(f"    Last bar event: {age_minutes:.1f} minutes ago")
            else:
                print(f"\n  [CRITICAL] ES1 NOT receiving bars")
                print(f"    Last bar event: {age_minutes:.1f} minutes ago")
                print(f"    Possible issues:")
                print(f"      - NinjaTrader connection lost")
                print(f"      - ES1 strategy stopped")
                print(f"      - Data feed disconnected")
    else:
        print(f"\n  [CRITICAL] NO bar events found for ES1")
        print(f"    ES1 is not receiving bars")
        print(f"    Check:")
        print(f"      - NinjaTrader connection")
        print(f"      - ES1 strategy status")
        print(f"      - Data feed connection")

if __name__ == "__main__":
    main()
