#!/usr/bin/env python3
"""
Check IDENTITY_INVARIANTS_STATUS events in detail to understand if they're violations or status checks.
"""
import json
from pathlib import Path
from datetime import datetime, timezone
import pytz

def parse_timestamp(ts_str: str):
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
    chicago_tz = pytz.timezone('America/Chicago')
    
    print("="*80)
    print("IDENTITY_INVARIANTS_STATUS EVENT ANALYSIS")
    print("="*80)
    
    # Load recent events
    events = []
    for log_file in sorted(log_dir.glob("robot_*.jsonl"), reverse=True)[:3]:
        try:
            with open(log_file, 'r', encoding='utf-8') as f:
                for line in f:
                    line = line.strip()
                    if line:
                        try:
                            e = json.loads(line)
                            if e.get('event') == 'IDENTITY_INVARIANTS_STATUS':
                                events.append(e)
                        except:
                            pass
        except:
            pass
    
    print(f"\nFound {len(events)} IDENTITY_INVARIANTS_STATUS events\n")
    
    if events:
        print("Recent IDENTITY_INVARIANTS_STATUS events:")
        print("-" * 80)
        
        for e in events[-10:]:
            ts = parse_timestamp(e.get('ts_utc', ''))
            if ts:
                ts_chicago = ts.astimezone(chicago_tz)
                level = e.get('level', 'N/A')
                data = e.get('data', {})
                
                print(f"\n  Time: {ts_chicago.strftime('%Y-%m-%d %H:%M:%S')} CT")
                print(f"  Level: {level}")
                print(f"  Data: {json.dumps(data, indent=4)}")
                
                # Check if this indicates a violation
                if data.get('violations') or data.get('violation_count', 0) > 0:
                    print(f"  [ERROR] VIOLATION DETECTED!")
                elif data.get('status') == 'OK' or data.get('all_ok'):
                    print(f"  [OK] Status check - all invariants OK")
                else:
                    print(f"  [INFO] Status check event")
    
    print("\n" + "="*80)

if __name__ == "__main__":
    main()
