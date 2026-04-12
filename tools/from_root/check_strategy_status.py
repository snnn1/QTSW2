#!/usr/bin/env python3
"""
Quick check of strategy status after reset
"""
import json
import glob
from datetime import datetime, timezone
from collections import defaultdict

def main():
    print("=" * 80)
    print("STRATEGY STATUS CHECK (After Reset)")
    print("=" * 80)
    print()
    
    # Load recent events
    events = []
    cutoff = datetime.now(timezone.utc).timestamp() - 3600  # Last hour
    
    for log_file in glob.glob('logs/robot/robot_*.jsonl'):
        try:
            with open(log_file, 'r', encoding='utf-8') as f:
                for line in f:
                    line = line.strip()
                    if not line:
                        continue
                    try:
                        event = json.loads(line)
                        ts_utc = event.get('ts_utc', '')
                        if ts_utc:
                            try:
                                event_time = datetime.fromisoformat(ts_utc.replace('Z', '+00:00'))
                                if event_time.timestamp() >= cutoff:
                                    events.append(event)
                            except:
                                pass
                    except:
                        continue
        except Exception as e:
            print(f"  Error reading {log_file}: {e}")
    
    print(f"Total events in last hour: {len(events)}")
    print()
    
    # Check key indicators
    print("[KEY INDICATORS]")
    
    # ENGINE_TICK_CALLSITE
    tick_callsite = [e for e in events if e.get('event') == 'ENGINE_TICK_CALLSITE']
    print(f"ENGINE_TICK_CALLSITE: {len(tick_callsite)} events")
    if tick_callsite:
        print(f"  Latest: {tick_callsite[-1].get('ts_utc', '')[:19]}")
    else:
        print("  [WARN] Missing - DLL may need rebuild/deploy")
    
    # TICK_CALLED_FROM_ONMARKETDATA
    tick_onmarket = [e for e in events if e.get('event') == 'TICK_CALLED_FROM_ONMARKETDATA']
    print(f"TICK_CALLED_FROM_ONMARKETDATA: {len(tick_onmarket)} events")
    if tick_onmarket:
        print(f"  Latest: {tick_onmarket[-1].get('ts_utc', '')[:19]}")
    
    # ONBARUPDATE_CALLED
    onbarupdate = [e for e in events if e.get('event') == 'ONBARUPDATE_CALLED']
    print(f"ONBARUPDATE_CALLED: {len(onbarupdate)} events")
    
    # BAR events
    bar_received = [e for e in events if 'BAR' in e.get('event', '')]
    print(f"BAR events: {len(bar_received)} events")
    
    # Stream states
    stream_events = [e for e in events if e.get('stream') and e.get('stream') != '__engine__']
    print(f"Stream-level events: {len(stream_events)} events")
    
    # Errors
    errors = [e for e in events if any(x in e.get('event', '').upper() for x in ['ERROR', 'FAIL', 'REJECT', 'EXCEPTION'])]
    print(f"Error events: {len(errors)} events")
    if errors:
        print("  Recent errors:")
        for e in errors[-5:]:
            print(f"    {e.get('ts_utc', '')[:19]} | {e.get('event', '')} | {e.get('message', '')[:60]}")
    
    print()
    print("[STREAM STATUS]")
    streams = defaultdict(lambda: {'state': 'UNKNOWN', 'last_event': None})
    for e in stream_events:
        stream = e.get('stream', '')
        if stream:
            streams[stream]['last_event'] = e.get('ts_utc', '')
            if 'state' in e.get('data', {}):
                streams[stream]['state'] = e.get('data', {}).get('state', 'UNKNOWN')
    
    for stream, info in sorted(streams.items())[:10]:
        print(f"  {stream}: {info['state']} (last: {info['last_event'][:19] if info['last_event'] else 'N/A'})")
    
    print()
    print("[SUMMARY]")
    if tick_callsite:
        print("[OK] ENGINE_TICK_CALLSITE is logging")
    else:
        print("[ISSUE] ENGINE_TICK_CALLSITE missing - check DLL rebuild/deploy")
    
    if tick_onmarket:
        print("[OK] Tick() is being called from OnMarketData")
    else:
        print("[WARN] Tick() not being called from OnMarketData")
    
    if onbarupdate:
        print("[OK] OnBarUpdate is being called")
    else:
        print("[WARN] OnBarUpdate not being called")

if __name__ == '__main__':
    main()
