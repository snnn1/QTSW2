#!/usr/bin/env python3
"""
Diagnose why OnBarUpdate isn't being called.
Checks for early returns, guards, and NinjaTrader state issues.
"""
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
    cutoff = datetime.now(timezone.utc) - timedelta(hours=6)
    
    print("="*80)
    print("ONBARUPDATE NOT CALLING - DIAGNOSTIC ANALYSIS")
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
    
    print(f"\nLoaded {len(events):,} events from last 6 hours\n")
    
    # Check for ONBARUPDATE_CALLED events
    print("="*80)
    print("ONBARUPDATE_CALLED EVENTS:")
    print("="*80)
    
    onbarupdate_called = [e for e in events if e.get('event') == 'ONBARUPDATE_CALLED']
    onbarupdate_diag = [e for e in events if e.get('event') == 'ONBARUPDATE_DIAGNOSTIC']
    onbarupdate_exception = [e for e in events if e.get('event') == 'ONBARUPDATE_EXCEPTION']
    
    print(f"\n  ONBARUPDATE_CALLED: {len(onbarupdate_called)}")
    print(f"  ONBARUPDATE_DIAGNOSTIC: {len(onbarupdate_diag)}")
    print(f"  ONBARUPDATE_EXCEPTION: {len(onbarupdate_exception)}")
    
    if onbarupdate_called:
        latest = max(onbarupdate_called, key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
        latest_ts = parse_timestamp(latest.get('ts_utc', ''))
        now = datetime.now(timezone.utc)
        if latest_ts:
            age_minutes = (now - latest_ts).total_seconds() / 60
            print(f"\n  Latest ONBARUPDATE_CALLED:")
            print(f"    Time: {latest.get('ts_utc', '')[:19]}")
            print(f"    Age: {age_minutes:.1f} minutes ago")
            data = latest.get('data', {})
            if isinstance(data, dict):
                print(f"    Instrument: {data.get('instrument', 'N/A')}")
                print(f"    Engine Ready: {data.get('engine_ready', 'N/A')}")
                print(f"    Current Bar: {data.get('current_bar', 'N/A')}")
                print(f"    State: {data.get('state', 'N/A')}")
    else:
        print(f"\n  [CRITICAL] No ONBARUPDATE_CALLED events found!")
        print(f"    This means NinjaTrader is NOT calling OnBarUpdate() at all")
    
    if onbarupdate_exception:
        print(f"\n  [WARN] Found {len(onbarupdate_exception)} ONBARUPDATE_EXCEPTION events:")
        for e in onbarupdate_exception[-5:]:
            ts = e.get('ts_utc', '')[:19] if len(e.get('ts_utc', '')) > 19 else e.get('ts_utc', '')
            data = e.get('data', {})
            if isinstance(data, dict):
                print(f"    {ts}: {data.get('exception_type', 'N/A')} - {data.get('exception_message', 'N/A')[:80]}")
    
    # Check for early return conditions
    print("\n" + "="*80)
    print("EARLY RETURN CONDITIONS:")
    print("="*80)
    
    init_failed = [e for e in events if 'INIT_FAILED' in e.get('event', '') or 'INITIALIZATION_FAILED' in e.get('event', '')]
    engine_ready = [e for e in events if 'ENGINE_READY' in e.get('event', '') or 'DATALOADED_INITIALIZATION_COMPLETE' in e.get('event', '')]
    realtime_reached = [e for e in events if e.get('event') == 'REALTIME_STATE_REACHED']
    
    print(f"\n  INIT_FAILED events: {len(init_failed)}")
    if init_failed:
        print(f"    [WARN] Strategy initialization failed - OnBarUpdate will return early!")
        for e in init_failed[-3:]:
            ts = e.get('ts_utc', '')[:19] if len(e.get('ts_utc', '')) > 19 else e.get('ts_utc', '')
            print(f"      {ts}: {e.get('event')}")
    
    print(f"\n  ENGINE_READY / DATALOADED_INITIALIZATION_COMPLETE: {len(engine_ready)}")
    if engine_ready:
        latest = max(engine_ready, key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
        ts = latest.get('ts_utc', '')[:19] if len(latest.get('ts_utc', '')) > 19 else latest.get('ts_utc', '')
        print(f"    Latest: {ts}")
    else:
        print(f"    [WARN] No engine ready events - _engineReady may be false!")
    
    print(f"\n  REALTIME_STATE_REACHED: {len(realtime_reached)}")
    if realtime_reached:
        latest = max(realtime_reached, key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
        ts = latest.get('ts_utc', '')[:19] if len(latest.get('ts_utc', '')) > 19 else latest.get('ts_utc', '')
        print(f"    Latest: {ts}")
    else:
        print(f"    [WARN] Strategy may not have reached Realtime state!")
    
    # Check for bars period issues
    print("\n" + "="*80)
    print("BARS PERIOD VALIDATION:")
    print("="*80)
    
    bars_period_invalid = [e for e in events if e.get('event') == 'BARS_PERIOD_INVALID']
    
    print(f"\n  BARS_PERIOD_INVALID: {len(bars_period_invalid)}")
    if bars_period_invalid:
        print(f"    [CRITICAL] Invalid bars period - OnBarUpdate returns early!")
        for e in bars_period_invalid[-3:]:
            ts = e.get('ts_utc', '')[:19] if len(e.get('ts_utc', '')) > 19 else e.get('ts_utc', '')
            data = e.get('data', {})
            if isinstance(data, dict):
                print(f"      {ts}: {data.get('bars_period_type', 'N/A')}, Value: {data.get('bars_period_value', 'N/A')}")
    
    # Check for ES1 specifically
    print("\n" + "="*80)
    print("ES1 SPECIFIC ANALYSIS:")
    print("="*80)
    
    es1_events = [e for e in events if e.get('stream') == 'ES1' or (isinstance(e.get('data'), dict) and e.get('data', {}).get('instrument') == 'ES')]
    es1_onbarupdate = [e for e in es1_events if 'ONBARUPDATE' in e.get('event', '')]
    
    print(f"\n  ES1 ONBARUPDATE events: {len(es1_onbarupdate)}")
    
    if es1_onbarupdate:
        latest = max(es1_onbarupdate, key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
        latest_ts = parse_timestamp(latest.get('ts_utc', ''))
        now = datetime.now(timezone.utc)
        if latest_ts:
            age_minutes = (now - latest_ts).total_seconds() / 60
            print(f"    Latest: {latest.get('ts_utc', '')[:19]}")
            print(f"    Age: {age_minutes:.1f} minutes ago")
            data = latest.get('data', {})
            if isinstance(data, dict):
                print(f"    Engine Ready: {data.get('engine_ready', 'N/A')}")
                print(f"    Current Bar: {data.get('current_bar', 'N/A')}")
    else:
        print(f"    [WARN] No ES1 OnBarUpdate events found")
    
    # Check for strategy state
    print("\n" + "="*80)
    print("STRATEGY STATE CHECK:")
    print("="*80)
    
    # Check if strategy reached Realtime
    es1_realtime = [e for e in es1_events if e.get('event') == 'REALTIME_STATE_REACHED']
    es1_dataloaded = [e for e in es1_events if 'DATALOADED_INITIALIZATION_COMPLETE' in e.get('event', '')]
    
    print(f"\n  ES1 REALTIME_STATE_REACHED: {len(es1_realtime)}")
    if es1_realtime:
        latest = max(es1_realtime, key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
        ts = latest.get('ts_utc', '')[:19] if len(latest.get('ts_utc', '')) > 19 else latest.get('ts_utc', '')
        print(f"    Latest: {ts}")
    else:
        print(f"    [WARN] ES1 may not have reached Realtime state")
    
    print(f"\n  ES1 DATALOADED_INITIALIZATION_COMPLETE: {len(es1_dataloaded)}")
    
    # Check for market/data feed issues
    print("\n" + "="*80)
    print("MARKET / DATA FEED CHECK:")
    print("="*80)
    
    # Check current time vs market hours
    now_utc = datetime.now(timezone.utc)
    now_chicago = now_utc.astimezone(timezone(timedelta(hours=-6)))
    
    print(f"\n  Current Time:")
    print(f"    UTC: {now_utc.strftime('%Y-%m-%d %H:%M:%S')}")
    print(f"    Chicago: {now_chicago.strftime('%Y-%m-%d %H:%M:%S')}")
    
    # ES futures trading hours: 17:00-16:00 CT (next day)
    chicago_hour = now_chicago.hour
    chicago_minute = now_chicago.minute
    
    print(f"\n  Market Hours Check:")
    print(f"    Current Chicago Time: {chicago_hour:02d}:{chicago_minute:02d}")
    
    # ES trades 17:00-16:00 CT (overnight session)
    # During regular hours: 08:30-15:15 CT
    # After hours: 15:15-17:00 CT (closed)
    # Overnight: 17:00-08:30 CT (next day)
    
    is_market_hours = False
    if chicago_hour >= 17 or chicago_hour < 8:
        is_market_hours = True  # Overnight session
        print(f"    [INFO] Overnight session (17:00-08:00 CT) - market should be open")
    elif chicago_hour == 8 and chicago_minute >= 30:
        is_market_hours = True  # Regular session starting
        print(f"    [INFO] Regular session (08:30-15:15 CT) - market should be open")
    elif chicago_hour < 15 or (chicago_hour == 15 and chicago_minute < 15):
        is_market_hours = True  # Regular session
        print(f"    [INFO] Regular session (08:30-15:15 CT) - market should be open")
    else:
        is_market_hours = False  # After hours
        print(f"    [WARN] After hours (15:15-17:00 CT) - market is CLOSED")
        print(f"    OnBarUpdate won't be called if market is closed!")
    
    # Summary
    print("\n" + "="*80)
    print("DIAGNOSIS:")
    print("="*80)
    
    issues = []
    
    if not onbarupdate_called:
        issues.append("No ONBARUPDATE_CALLED events - NinjaTrader is NOT calling OnBarUpdate()")
    
    if init_failed:
        issues.append("Strategy initialization failed - OnBarUpdate returns early (line 1013)")
    
    if not engine_ready:
        issues.append("Engine not ready - OnBarUpdate returns early (line 1020)")
    
    if not realtime_reached:
        issues.append("Strategy not in Realtime state - OnBarUpdate may not be called")
    
    if bars_period_invalid:
        issues.append("Invalid bars period - OnBarUpdate returns early (line 1207)")
    
    if not is_market_hours:
        issues.append("Market is CLOSED - NinjaTrader won't call OnBarUpdate when market is closed")
    
    if onbarupdate_called:
        latest_ts = parse_timestamp(max(onbarupdate_called, key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc)).get('ts_utc', ''))
        if latest_ts:
            age_minutes = (datetime.now(timezone.utc) - latest_ts).total_seconds() / 60
            if age_minutes > 30:
                issues.append(f"OnBarUpdate stopped being called {age_minutes:.1f} minutes ago")
    
    if issues:
        print("\n  [CRITICAL] Issues preventing OnBarUpdate from being called:")
        for i, issue in enumerate(issues, 1):
            print(f"    {i}. {issue}")
        
        print("\n  [ROOT CAUSE ANALYSIS]")
        if not onbarupdate_called:
            print("    PRIMARY ISSUE: NinjaTrader is not calling OnBarUpdate()")
            print("    Possible reasons:")
            print("      1. Market is CLOSED (most likely)")
            print("      2. Strategy stopped/crashed")
            print("      3. Data feed disconnected")
            print("      4. Strategy not in Realtime state")
            print("      5. Bars aren't closing (OnBarClose requires bar close)")
        
        if init_failed:
            print("\n    SECONDARY ISSUE: Strategy initialization failed")
            print("    OnBarUpdate will return early at line 1013")
        
        if not engine_ready:
            print("\n    SECONDARY ISSUE: Engine not ready")
            print("    OnBarUpdate will return early at line 1020")
        
        if bars_period_invalid:
            print("\n    SECONDARY ISSUE: Invalid bars period")
            print("    OnBarUpdate will return early at line 1207")
    else:
        print("\n  [OK] No obvious issues - OnBarUpdate should be called")
        if onbarupdate_called:
            print("    OnBarUpdate IS being called (found events)")
            print("    Issue may be with bar processing logic, not OnBarUpdate itself")

if __name__ == "__main__":
    main()
