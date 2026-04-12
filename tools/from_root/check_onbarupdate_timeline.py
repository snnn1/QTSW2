#!/usr/bin/env python3
"""
Check timeline of OnBarUpdate calls to see exactly when they stopped.
"""
import json
from pathlib import Path
from datetime import datetime, timezone, timedelta
from collections import defaultdict
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
    cutoff = datetime.now(timezone.utc) - timedelta(hours=6)
    
    print("="*80)
    print("ONBARUPDATE CALL TIMELINE ANALYSIS")
    print("="*80)
    
    # Load all events
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
    
    # Get all OnBarUpdate events
    onbarupdate_events = [e for e in events if e.get('event') == 'ONBARUPDATE_CALLED']
    
    print(f"\nFound {len(onbarupdate_events)} ONBARUPDATE_CALLED events\n")
    
    if not onbarupdate_events:
        print("[CRITICAL] No OnBarUpdate events found at all!")
        return
    
    # Group by instrument
    by_instrument = defaultdict(list)
    for e in onbarupdate_events:
        data = e.get('data', {})
        if isinstance(data, dict):
            instrument = data.get('instrument', 'UNKNOWN')
            by_instrument[instrument].append(e)
    
    # Analyze each instrument
    print("="*80)
    print("ONBARUPDATE CALLS BY INSTRUMENT:")
    print("="*80)
    
    chicago_tz = pytz.timezone('America/Chicago')
    now_utc = datetime.now(timezone.utc)
    
    for instrument in sorted(by_instrument.keys()):
        inst_events = sorted(by_instrument[instrument], key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
        
        print(f"\n{instrument}:")
        print(f"  Total calls: {len(inst_events)}")
        
        if inst_events:
            first = inst_events[0]
            last = inst_events[-1]
            
            first_ts = parse_timestamp(first.get('ts_utc', ''))
            last_ts = parse_timestamp(last.get('ts_utc', ''))
            
            if first_ts and last_ts:
                first_chicago = first_ts.astimezone(chicago_tz)
                last_chicago = last_ts.astimezone(chicago_tz)
                
                print(f"  First call: {first_chicago.strftime('%H:%M:%S')} CT")
                print(f"  Last call: {last_chicago.strftime('%H:%M:%S')} CT")
                
                age_minutes = (now_utc - last_ts).total_seconds() / 60
                print(f"  Age: {age_minutes:.1f} minutes ago")
                
                if age_minutes > 30:
                    print(f"  [WARN] OnBarUpdate stopped {age_minutes:.1f} minutes ago")
                
                # Check data from last call
                last_data = last.get('data', {})
                if isinstance(last_data, dict):
                    print(f"  Last call details:")
                    print(f"    Engine Ready: {last_data.get('engine_ready', 'N/A')}")
                    print(f"    Current Bar: {last_data.get('current_bar', 'N/A')}")
                    print(f"    State: {last_data.get('state', 'N/A')}")
            
            # Show gap analysis
            if len(inst_events) > 1:
                gaps = []
                for i in range(1, len(inst_events)):
                    prev_ts = parse_timestamp(inst_events[i-1].get('ts_utc', ''))
                    curr_ts = parse_timestamp(inst_events[i].get('ts_utc', ''))
                    if prev_ts and curr_ts:
                        gap_minutes = (curr_ts - prev_ts).total_seconds() / 60
                        if gap_minutes > 5:  # Gaps > 5 minutes
                            gaps.append((prev_ts, curr_ts, gap_minutes))
                
                if gaps:
                    print(f"\n  Gaps > 5 minutes: {len(gaps)}")
                    for prev_ts, curr_ts, gap_min in gaps[-3:]:
                        prev_chicago = prev_ts.astimezone(chicago_tz)
                        curr_chicago = curr_ts.astimezone(chicago_tz)
                        print(f"    {prev_chicago.strftime('%H:%M:%S')} -> {curr_chicago.strftime('%H:%M:%S')}: {gap_min:.1f} min gap")
    
    # Check for ES/MES specifically
    print("\n" + "="*80)
    print("ES/MES SPECIFIC ANALYSIS:")
    print("="*80)
    
    es_mes_events = [e for e in onbarupdate_events if isinstance(e.get('data'), dict) and 
                     e.get('data', {}).get('instrument') in ['ES', 'MES']]
    
    print(f"\n  ES/MES OnBarUpdate calls: {len(es_mes_events)}")
    
    if es_mes_events:
        last_es = max(es_mes_events, key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
        last_ts = parse_timestamp(last_es.get('ts_utc', ''))
        
        if last_ts:
            last_chicago = last_ts.astimezone(chicago_tz)
            age_minutes = (now_utc - last_ts).total_seconds() / 60
            
            print(f"  Last ES/MES OnBarUpdate:")
            print(f"    Time: {last_chicago.strftime('%H:%M:%S')} CT")
            print(f"    Age: {age_minutes:.1f} minutes ago")
            
            data = last_es.get('data', {})
            if isinstance(data, dict):
                print(f"    Instrument: {data.get('instrument', 'N/A')}")
                print(f"    Engine Ready: {data.get('engine_ready', 'N/A')}")
                print(f"    Current Bar: {data.get('current_bar', 'N/A')}")
    else:
        print(f"  [CRITICAL] No ES/MES OnBarUpdate events found!")
    
    # Check what happened after last OnBarUpdate
    print("\n" + "="*80)
    print("EVENTS AFTER LAST ONBARUPDATE:")
    print("="*80)
    
    if onbarupdate_events:
        last_onbarupdate = max(onbarupdate_events, key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
        last_ts = parse_timestamp(last_onbarupdate.get('ts_utc', ''))
        
        if last_ts:
            # Get events after last OnBarUpdate
            events_after = [e for e in events if parse_timestamp(e.get('ts_utc', '')) and parse_timestamp(e.get('ts_utc', '')) > last_ts]
            
            print(f"\n  Events after last OnBarUpdate ({len(events_after)} total):")
            
            # Group by event type
            by_type = defaultdict(int)
            for e in events_after:
                by_type[e.get('event', 'UNKNOWN')] += 1
            
            print(f"\n  Top event types:")
            for event_type, count in sorted(by_type.items(), key=lambda x: x[1], reverse=True)[:10]:
                print(f"    {event_type}: {count}")
            
            # Show recent events
            print(f"\n  Recent events after last OnBarUpdate:")
            for e in events_after[-10:]:
                ts = parse_timestamp(e.get('ts_utc', ''))
                if ts:
                    ts_chicago = ts.astimezone(chicago_tz)
                    event_type = e.get('event', 'N/A')
                    stream = e.get('stream', 'N/A')
                    stream_str = str(stream) if stream else 'N/A'
                    print(f"    {ts_chicago.strftime('%H:%M:%S')} CT | {stream_str:6} | {event_type}")
    
    # Check for connection/data feed issues
    print("\n" + "="*80)
    print("CONNECTION / DATA FEED CHECK:")
    print("="*80)
    
    connection_events = [e for e in events if any(x in e.get('event', '').upper() for x in ['CONNECTION', 'DISCONNECT', 'DATA_LOSS', 'STALL'])]
    
    print(f"\n  Connection/data feed events: {len(connection_events)}")
    if connection_events:
        print(f"    Recent connection events:")
        for e in connection_events[-10:]:
            ts = parse_timestamp(e.get('ts_utc', ''))
            if ts:
                ts_chicago = ts.astimezone(chicago_tz)
                event_type = e.get('event', 'N/A')
                print(f"      {ts_chicago.strftime('%H:%M:%S')} CT | {event_type}")
    
    # Summary
    print("\n" + "="*80)
    print("ROOT CAUSE:")
    print("="*80)
    
    if onbarupdate_events:
        last_ts = parse_timestamp(max(onbarupdate_events, key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc)).get('ts_utc', ''))
        if last_ts:
            age_minutes = (now_utc - last_ts).total_seconds() / 60
            
            print(f"\n  OnBarUpdate WAS being called but STOPPED {age_minutes:.1f} minutes ago")
            print(f"\n  Possible reasons:")
            print(f"    1. Market closed (but current time suggests market should be open)")
            print(f"    2. Bars stopped closing (NinjaTrader OnBarClose only fires when bars close)")
            print(f"    3. Data feed disconnected")
            print(f"    4. Strategy stopped/crashed")
            print(f"    5. NinjaTrader connection lost")
            
            print(f"\n  [ACTION REQUIRED]")
            print(f"    Check NinjaTrader:")
            print(f"      - Is ES1 strategy still running?")
            print(f"      - Is data feed connected?")
            print(f"      - Are bars closing? (check chart)")
            print(f"      - Any errors in Output window?")
            print(f"      - Strategy state? (should be Realtime)")

if __name__ == "__main__":
    main()
