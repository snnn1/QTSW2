#!/usr/bin/env python3
"""
Check ES1 strategy initialization and why it's not reaching Realtime state.
"""
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
    cutoff = datetime.now(timezone.utc) - timedelta(hours=6)
    
    print("="*80)
    print("ES1 STRATEGY INITIALIZATION ANALYSIS")
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
    
    # Filter ES1 or ES-related events
    es1_events = []
    es_engine_events = []
    
    for e in events:
        stream = e.get('stream', '')
        data = e.get('data', {})
        if isinstance(data, dict):
            instrument = data.get('instrument', '')
            execution_instrument = data.get('execution_instrument', '')
        else:
            instrument = execution_instrument = ''
        
        # ES1 stream events
        if stream == 'ES1':
            es1_events.append(e)
        
        # Engine-level ES events (strategy initialization)
        if (not stream or stream == '') and (
            instrument == 'ES' or 
            execution_instrument == 'MES' or
            'ES' in str(e.get('data', {}))
        ):
            es_engine_events.append(e)
    
    print(f"\nFound {len(es1_events)} ES1 stream events")
    print(f"Found {len(es_engine_events)} ES engine-level events\n")
    
    # Check engine-level ES events (strategy initialization)
    print("="*80)
    print("ES STRATEGY INITIALIZATION EVENTS:")
    print("="*80)
    
    if es_engine_events:
        print(f"\n  Found {len(es_engine_events)} ES engine-level events:")
        
        # Key initialization events
        init_events = [
            'ENGINE_START',
            'DATALOADED_INITIALIZATION_COMPLETE',
            'REALTIME_STATE_REACHED',
            'SIM_ACCOUNT_VERIFIED',
            'NT_CONTEXT_WIRED',
            'BARSREQUEST_REQUESTED',
            'BARSREQUEST_EXECUTED'
        ]
        
        for event_type in init_events:
            matching = [e for e in es_engine_events if e.get('event') == event_type]
            if matching:
                latest = max(matching, key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
                ts = latest.get('ts_utc', '')[:19] if len(latest.get('ts_utc', '')) > 19 else latest.get('ts_utc', '')
                data = latest.get('data', {})
                instrument = data.get('instrument', 'N/A') if isinstance(data, dict) else 'N/A'
                print(f"    {event_type}: {ts} | Instrument: {instrument}")
            else:
                print(f"    {event_type}: NOT FOUND")
        
        # Show recent ES engine events
        print(f"\n  Recent ES engine-level events (last 20):")
        for e in es_engine_events[-20:]:
            ts = e.get('ts_utc', '')[:19] if len(e.get('ts_utc', '')) > 19 else e.get('ts_utc', '')
            event_type = e.get('event', 'N/A')
            data = e.get('data', {})
            instrument = data.get('instrument', 'N/A') if isinstance(data, dict) else 'N/A'
            print(f"    {ts} | {event_type} | Instrument: {instrument}")
    else:
        print("\n  [CRITICAL] No ES engine-level events found!")
        print("    This means ES strategy may not have started or initialized")
    
    # Check for MES (execution instrument) events
    print("\n" + "="*80)
    print("MES (EXECUTION INSTRUMENT) EVENTS:")
    print("="*80)
    
    mes_events = [e for e in events if isinstance(e.get('data'), dict) and 
                  (e.get('data', {}).get('execution_instrument') == 'MES' or
                   e.get('data', {}).get('instrument') == 'MES')]
    
    print(f"\n  Found {len(mes_events)} MES-related events")
    
    if mes_events:
        print(f"\n  Recent MES events (last 10):")
        for e in mes_events[-10:]:
            ts = e.get('ts_utc', '')[:19] if len(e.get('ts_utc', '')) > 19 else e.get('ts_utc', '')
            event_type = e.get('event', 'N/A')
            stream = e.get('stream', 'N/A')
            print(f"    {ts} | {stream} | {event_type}")
    
    # Check for all instruments that reached Realtime
    print("\n" + "="*80)
    print("INSTRUMENTS THAT REACHED REALTIME:")
    print("="*80)
    
    realtime_events = [e for e in events if e.get('event') == 'REALTIME_STATE_REACHED']
    
    instruments_realtime = {}
    for e in realtime_events:
        data = e.get('data', {})
        if isinstance(data, dict):
            instrument = data.get('instrument', 'N/A')
            if instrument not in instruments_realtime:
                instruments_realtime[instrument] = []
            instruments_realtime[instrument].append(e)
    
    print(f"\n  Instruments that reached Realtime: {len(instruments_realtime)}")
    for instrument, inst_events in sorted(instruments_realtime.items()):
        latest = max(inst_events, key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
        ts = latest.get('ts_utc', '')[:19] if len(latest.get('ts_utc', '')) > 19 else latest.get('ts_utc', '')
        print(f"    {instrument}: {ts} ({len(inst_events)} events)")
    
    if 'ES' not in instruments_realtime and 'MES' not in [e.get('data', {}).get('execution_instrument') for e in realtime_events if isinstance(e.get('data'), dict)]:
        print(f"\n  [CRITICAL] ES/MES did NOT reach Realtime state!")
        print(f"    This explains why OnBarUpdate isn't being called for ES1")
    
    # Check for strategy errors
    print("\n" + "="*80)
    print("STRATEGY ERRORS / FAILURES:")
    print("="*80)
    
    error_events = [e for e in es_engine_events if any(x in e.get('event', '').upper() for x in ['ERROR', 'FAIL', 'EXCEPTION', 'ABORT'])]
    
    print(f"\n  ES strategy error events: {len(error_events)}")
    if error_events:
        print(f"    [WARN] Found errors during ES strategy initialization:")
        for e in error_events[-10:]:
            ts = e.get('ts_utc', '')[:19] if len(e.get('ts_utc', '')) > 19 else e.get('ts_utc', '')
            event_type = e.get('event', 'N/A')
            msg = e.get('msg', '')[:80] if e.get('msg') else ''
            print(f"      {ts} | {event_type} | {msg}")
    
    # Summary
    print("\n" + "="*80)
    print("DIAGNOSIS:")
    print("="*80)
    
    print("\n  [ROOT CAUSE]")
    
    if not es_engine_events:
        print("    ES strategy NEVER STARTED or initialized")
        print("    Possible reasons:")
        print("      1. ES1 strategy not added to chart in NinjaTrader")
        print("      2. Strategy failed to start (check NinjaTrader Output window)")
        print("      3. Strategy crashed during initialization")
        print("      4. Strategy disabled or stopped")
    elif 'ES' not in instruments_realtime:
        print("    ES strategy started but NEVER REACHED REALTIME state")
        print("    Possible reasons:")
        print("      1. Strategy stuck in DataLoaded state")
        print("      2. BarsRequest blocking Realtime transition")
        print("      3. Strategy initialization failed silently")
        print("      4. NinjaTrader connection issue")
        print("\n    Check NinjaTrader:")
        print("      - Is ES1 strategy running?")
        print("      - What state is it in? (DataLoaded, Realtime, etc.)")
        print("      - Any errors in Output window?")
    else:
        print("    ES strategy reached Realtime")
        print("    Issue may be with bar processing, not initialization")

if __name__ == "__main__":
    main()
