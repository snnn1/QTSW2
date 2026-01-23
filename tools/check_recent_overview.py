"""Quick overview of recent robot activity"""
import json
from pathlib import Path
from datetime import datetime
from collections import defaultdict

qtsw2_root = Path(__file__).parent.parent
log_file = qtsw2_root / "logs" / "robot" / "robot_ENGINE.jsonl"

def parse_timestamp(ts_str):
    try:
        return datetime.fromisoformat(ts_str.replace('Z', '+00:00'))
    except:
        return None

def main():
    print("="*80)
    print("ROBOT STATUS OVERVIEW - SINCE RESTART")
    print("="*80)
    
    events = []
    with open(log_file, 'r', encoding='utf-8-sig') as f:
        for line in f:
            line = line.strip()
            if line:
                try:
                    events.append(json.loads(line))
                except:
                    pass
    
    if not events:
        print("No events found")
        return
    
    # Get last 500 events
    recent = events[-500:]
    
    # Event type counts
    event_counts = defaultdict(int)
    for e in recent:
        event_name = e.get('event', 'N/A')
        event_counts[event_name] += 1
    
    print(f"\n[RECENT ACTIVITY - Last {len(recent)} events]")
    print(f"  Time range: {parse_timestamp(recent[0].get('ts_utc', '')).strftime('%H:%M:%S') if parse_timestamp(recent[0].get('ts_utc', '')) else 'N/A'} to {parse_timestamp(recent[-1].get('ts_utc', '')).strftime('%H:%M:%S') if parse_timestamp(recent[-1].get('ts_utc', '')) else 'N/A'}")
    
    print(f"\n[TOP EVENT TYPES]")
    for event_name, count in sorted(event_counts.items(), key=lambda x: -x[1])[:15]:
        print(f"  {event_name:45} {count:4}")
    
    # Run ID status
    run_ids = set(e.get('run_id') for e in recent if e.get('run_id'))
    events_with_run_id = sum(1 for e in recent if e.get('run_id'))
    print(f"\n[RUN ID STATUS]")
    print(f"  [OK] All events have run_id: {events_with_run_id}/{len(recent)} ({100*events_with_run_id/len(recent):.1f}%)")
    print(f"  Unique run_ids: {len(run_ids)}")
    if run_ids:
        latest_run_id = sorted(run_ids)[-1]
        print(f"  Latest run_id: {latest_run_id[:32]}...")
    
    # Health monitor check
    health_events = [e for e in recent if any(x in e.get('event', '').upper() for x in ['HEALTH', 'PUSHOVER', 'CRITICAL', 'NOTIFICATION'])]
    print(f"\n[HEALTH MONITOR STATUS]")
    if health_events:
        print(f"  Found {len(health_events)} health/notification events")
        for e in health_events[-5:]:
            print(f"    {e.get('event', 'N/A')} at {parse_timestamp(e.get('ts_utc', '')).strftime('%H:%M:%S') if parse_timestamp(e.get('ts_utc', '')) else 'N/A'}")
    else:
        print(f"  [WARN] No health monitor events found in recent logs")
        print(f"  This may mean:")
        print(f"    - Health monitor not initialized yet")
        print(f"    - Robot still starting up")
        print(f"    - Check for ENGINE_START events")
    
    # Errors/Warnings
    errors = [e for e in recent if e.get('level') == 'ERROR']
    warnings = [e for e in recent if e.get('level') == 'WARN']
    print(f"\n[ERRORS & WARNINGS]")
    print(f"  Errors: {len(errors)}")
    print(f"  Warnings: {len(warnings)}")
    
    if errors:
        print(f"\n  Recent Errors:")
        for e in errors[-3:]:
            ts = parse_timestamp(e.get('ts_utc', ''))
            ts_str = ts.strftime('%H:%M:%S') if ts else 'N/A'
            print(f"    [{ts_str}] {e.get('event', 'N/A')}: {str(e.get('message', ''))[:60]}")
    
    # Stream activity
    stream_events = [e for e in recent if 'STREAM' in e.get('event', '').upper() or 'RANGE' in e.get('event', '').upper()]
    print(f"\n[STREAM ACTIVITY]")
    print(f"  Stream-related events: {len(stream_events)}")
    if stream_events:
        stream_types = defaultdict(int)
        for e in stream_events:
            stream_types[e.get('event', 'N/A')] += 1
        for stype, count in sorted(stream_types.items(), key=lambda x: -x[1])[:5]:
            print(f"    {stype}: {count}")

if __name__ == '__main__':
    main()
