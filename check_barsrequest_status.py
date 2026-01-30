#!/usr/bin/env python3
"""
Check BarsRequest status for streams waiting for hydration.
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
    cutoff = datetime.now(timezone.utc) - timedelta(hours=2)
    
    print("="*80)
    print("BARSREQUEST STATUS CHECK")
    print("="*80)
    
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
    
    # Filter BarsRequest events
    barsrequest_events = [e for e in events if 'BARSREQUEST' in e.get('event', '')]
    
    print(f"\nFound {len(barsrequest_events)} BarsRequest-related events\n")
    
    # Group by instrument
    by_instrument = defaultdict(list)
    for e in barsrequest_events:
        data = e.get('data', {})
        if isinstance(data, dict):
            instrument = data.get('instrument', '') or data.get('canonical_instrument', '')
            if instrument:
                by_instrument[instrument].append(e)
    
    # Check each instrument
    for instrument in sorted(by_instrument.keys()):
        inst_events = sorted(by_instrument[instrument], key=lambda x: parse_timestamp(x.get('ts_utc', '')) or datetime.min.replace(tzinfo=timezone.utc))
        
        print(f"{instrument}:")
        
        # Find latest events
        pending = [e for e in inst_events if e.get('event') == 'BARSREQUEST_PENDING_MARKED']
        completed = [e for e in inst_events if e.get('event') == 'BARSREQUEST_COMPLETED_MARKED']
        executed = [e for e in inst_events if e.get('event') == 'BARSREQUEST_EXECUTED']
        timeout = [e for e in inst_events if e.get('event') == 'BARSREQUEST_TIMEOUT']
        waiting = [e for e in events if 'PRE_HYDRATION_WAITING_FOR_BARSREQUEST' in e.get('event', '') and 
                  (e.get('data', {}).get('instrument') == instrument or 
                   e.get('data', {}).get('canonical_instrument') == instrument)]
        
        print(f"  Pending: {len(pending)}")
        print(f"  Completed: {len(completed)}")
        print(f"  Executed: {len(executed)}")
        print(f"  Timeouts: {len(timeout)}")
        print(f"  Waiting events: {len(waiting)}")
        
        # Check if currently waiting
        if waiting:
            latest_waiting = waiting[-1]
            wait_ts = parse_timestamp(latest_waiting.get('ts_utc', ''))
            now = datetime.now(timezone.utc)
            if wait_ts:
                if wait_ts.tzinfo is None:
                    wait_ts = wait_ts.replace(tzinfo=timezone.utc)
                wait_age = (now - wait_ts).total_seconds() / 60
                print(f"  Latest waiting: {wait_age:.1f} minutes ago")
        
        # Check timing
        if pending and completed:
            latest_pending = pending[-1]
            latest_completed = completed[-1]
            pending_ts = parse_timestamp(latest_pending.get('ts_utc', ''))
            completed_ts = parse_timestamp(latest_completed.get('ts_utc', ''))
            
            if pending_ts and completed_ts:
                duration = (completed_ts - pending_ts).total_seconds()
                print(f"  Latest duration: {duration:.1f} seconds")
        
        # Check if stuck
        if pending and not completed:
            latest_pending = pending[-1]
            pending_ts = parse_timestamp(latest_pending.get('ts_utc', ''))
            now = datetime.now(timezone.utc)
            if pending_ts:
                if pending_ts.tzinfo is None:
                    pending_ts = pending_ts.replace(tzinfo=timezone.utc)
                pending_age = (now - pending_ts).total_seconds() / 60
                if pending_age > 5:
                    print(f"  [WARN] BarsRequest pending for {pending_age:.1f} minutes - may be stuck")
        
        print()

if __name__ == "__main__":
    main()
