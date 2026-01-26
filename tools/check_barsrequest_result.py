"""Check BarsRequest result details"""
import json
from pathlib import Path
from datetime import datetime

def parse_timestamp(ts_str):
    try:
        return datetime.fromisoformat(ts_str.replace('Z', '+00:00'))
    except:
        return None

def main():
    log_file = Path("logs/robot/robot_ENGINE.jsonl")
    
    events = []
    with open(log_file, 'r', encoding='utf-8-sig') as f:
        for line in f:
            line = line.strip()
            if line:
                try:
                    events.append(json.loads(line))
                except:
                    pass
    
    # Get recent events
    recent = events[-200:] if len(events) > 200 else events
    
    # Find latest ENGINE_START
    starts = [e for e in recent if e.get('event') == 'ENGINE_START']
    if not starts:
        print("[ERROR] No ENGINE_START found")
        return
    
    latest_start = starts[-1]
    start_time = parse_timestamp(latest_start.get('ts_utc', ''))
    
    # Filter events since start
    events_since_start = [e for e in recent 
                          if parse_timestamp(e.get('ts_utc', '')) and 
                          parse_timestamp(e.get('ts_utc', '')) >= start_time]
    
    # Find BARSREQUEST_RAW_RESULT
    barsresult = [e for e in events_since_start if e.get('event') == 'BARSREQUEST_RAW_RESULT']
    
    print("="*80)
    print("BARSREQUEST RESULT ANALYSIS")
    print("="*80)
    print(f"\nLatest ENGINE_START: {start_time.strftime('%Y-%m-%d %H:%M:%S UTC') if start_time else 'N/A'}")
    print(f"BARSREQUEST_RAW_RESULT events: {len(barsresult)}\n")
    
    if barsresult:
        print("[BARSREQUEST RESULTS]")
        for e in barsresult:
            ts = parse_timestamp(e.get('ts_utc', ''))
            data = e.get('data', {})
            payload = str(data.get('payload', ''))
            
            print(f"\n  Time: {ts.strftime('%H:%M:%S UTC') if ts else 'N/A'}")
            print(f"  Payload: {payload}")
            
            # Extract key info
            if 'bars_returned_raw =' in payload:
                try:
                    bars_count = payload.split('bars_returned_raw =')[1].split(',')[0].strip()
                    print(f"  Bars Returned: {bars_count}")
                except:
                    pass
            
            if 'instrument =' in payload:
                try:
                    inst = payload.split('instrument =')[1].split(',')[0].strip()
                    print(f"  Instrument: {inst}")
                except:
                    pass
    else:
        print("[CRITICAL] No BARSREQUEST_RAW_RESULT events found!")
        print("  This means BarsRequest executed but no result was logged")
        print("  Possible causes:")
        print("    1. BarsRequest callback not being invoked")
        print("    2. BarsRequest returned 0 bars")
        print("    3. Error in bar processing preventing result logging")
    
    # Check for BARSREQUEST_EXECUTED
    executed = [e for e in events_since_start if e.get('event') == 'BARSREQUEST_EXECUTED']
    print(f"\n[BARSREQUEST_EXECUTED events: {len(executed)}]")
    
    if executed and not barsresult:
        print("  [ISSUE] BarsRequest executed but no RAW_RESULT logged!")
        print("    This suggests bars were requested but callback didn't fire or returned 0 bars")

if __name__ == '__main__':
    main()
