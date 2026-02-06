#!/usr/bin/env python3
"""Check system health after restart - verify new DLL is working"""
import json
from pathlib import Path
from datetime import datetime, timezone, timedelta

# Find log files
log_dirs = [
    Path("logs/robot"),
    Path("modules/robot/core/logs"),
]

events = []
cutoff = datetime.now(timezone.utc) - timedelta(minutes=30)

for log_dir in log_dirs:
    if not log_dir.exists():
        continue
    
    for log_file in log_dir.glob("*.jsonl"):
        try:
            with open(log_file, 'r', encoding='utf-8-sig') as f:
                for line in f:
                    if not line.strip():
                        continue
                    try:
                        event = json.loads(line.strip())
                        ts_str = event.get('ts_utc', '') or event.get('timestamp_utc', '')
                        if ts_str:
                            ts = datetime.fromisoformat(ts_str.replace('Z', '+00:00'))
                            if ts.tzinfo is None:
                                ts = ts.replace(tzinfo=timezone.utc)
                            if ts >= cutoff:
                                events.append({
                                    'ts': ts,
                                    'event': event.get('event', '') or event.get('event_type', ''),
                                    'instrument': event.get('instrument', '') or event.get('data', {}).get('instrument', ''),
                                    'level': event.get('level', ''),
                                    'data': event.get('data', {}),
                                    'file': str(log_file)
                                })
                    except:
                        pass
        except:
            pass

events.sort(key=lambda x: x['ts'], reverse=True)

print("=" * 100)
print("SYSTEM HEALTH CHECK - POST RESTART")
print("=" * 100)
print()

# Check for engine start
engine_starts = [e for e in events if 'ENGINE_START' in e['event'].upper()]
if engine_starts:
    print(f"[OK] ENGINE STARTED: {len(engine_starts)} event(s)")
    latest_start = engine_starts[0]
    print(f"   Time: {latest_start['ts'].strftime('%H:%M:%S')}")
    print()

# Check for protective order tracking
protective_tracked = [e for e in events if 'PROTECTIVE_ORDER_FILL_TRACKED_FROM_TAG' in e['event']]
if protective_tracked:
    print(f"[WARN] PROTECTIVE ORDER FALLBACK USED: {len(protective_tracked)} event(s)")
    print("   (This indicates protective orders weren't in _orderMap - should be rare)")
    for e in protective_tracked[:3]:
        print(f"   [{e['ts'].strftime('%H:%M:%S')}] {e['data'].get('order_type', '')} - {e['data'].get('intent_id', '')[:8]}")
    print()

# Check for untracked fill events (should NOT happen for protective orders)
untracked_critical = [e for e in events if 'UNTRACKED_FILL_CRITICAL' in e['event'] or 'UNKNOWN_ORDER_CRITICAL' in e['event']]
if untracked_critical:
    print(f"[ERROR] UNTRACKED FILL EVENTS: {len(untracked_critical)} event(s)")
    print("   (These should NOT occur for protective orders with new DLL)")
    for e in untracked_critical[:5]:
        tag = e['data'].get('tag', '') or e['data'].get('order_tag', '')
        order_type = 'UNKNOWN'
        if ':STOP' in tag:
            order_type = 'STOP'
        elif ':TARGET' in tag:
            order_type = 'TARGET'
        print(f"   [{e['ts'].strftime('%H:%M:%S')}] {e['instrument']} - {order_type} - Tag: {tag[:50]}")
    print()
else:
    print("[OK] NO UNTRACKED FILL EVENTS (Good!)")
    print()

# Check for protective order submissions
protective_submitted = [e for e in events if 'ORDER_SUBMIT_SUCCESS' in e['event'] and 
                       ('PROTECTIVE_STOP' in str(e['data']) or 'TARGET' in str(e['data']))]
if protective_submitted:
    print(f"[OK] PROTECTIVE ORDERS SUBMITTED: {len(protective_submitted)} event(s)")
    for e in protective_submitted[:5]:
        order_type = e['data'].get('order_type', '')
        note = e['data'].get('note', '')
        if '_orderMap' in note:
            print(f"   [{e['ts'].strftime('%H:%M:%S')}] {e['instrument']} - {order_type} - Added to _orderMap [OK]")
        else:
            print(f"   [{e['ts'].strftime('%H:%M:%S')}] {e['instrument']} - {order_type}")
    print()

# Check for exit fills (protective orders filling)
exit_fills = [e for e in events if 'EXECUTION_EXIT_FILL' in e['event']]
if exit_fills:
    print(f"[OK] EXIT FILLS (Protective Orders): {len(exit_fills)} event(s)")
    for e in exit_fills[:5]:
        order_type = e['data'].get('exit_order_type', '')
        print(f"   [{e['ts'].strftime('%H:%M:%S')}] {e['instrument']} - {order_type} @ {e['data'].get('fill_price', '')}")
    print()

# Check for errors
errors = [e for e in events if e['level'] == 'ERROR']
if errors:
    print(f"[ERROR] ERRORS: {len(errors)} event(s)")
    for e in errors[:5]:
        print(f"   [{e['ts'].strftime('%H:%M:%S')}] {e['event']} - {e['instrument']}")
        if 'error' in e['data']:
            print(f"      Error: {e['data']['error'][:100]}")
    print()
else:
    print("[OK] NO ERRORS")
    print()

# Check for opposite entry cancellations
opposite_cancelled = [e for e in events if 'OPPOSITE_ENTRY_CANCELLED' in e['event']]
if opposite_cancelled:
    print(f"[OK] OPPOSITE ENTRY CANCELLATIONS: {len(opposite_cancelled)} event(s)")
    print("   (Re-entry prevention working)")
    for e in opposite_cancelled[:3]:
        print(f"   [{e['ts'].strftime('%H:%M:%S')}] {e['instrument']} - {e['data'].get('stream', '')}")
    print()

# Summary
print("=" * 100)
print("SUMMARY")
print("=" * 100)
print(f"Total events (last 30 min): {len(events)}")
print(f"Engine starts: {len(engine_starts)}")
print(f"Protective orders submitted: {len(protective_submitted)}")
print(f"Exit fills: {len(exit_fills)}")
print(f"Untracked fill events: {len(untracked_critical)}")
print(f"Errors: {len(errors)}")
print()

if len(untracked_critical) == 0 and len(errors) == 0:
    print("[OK] SYSTEM HEALTH: GOOD")
    print("   - No untracked fill events")
    print("   - No errors")
    print("   - New DLL appears to be working")
elif len(untracked_critical) > 0:
    print("[WARN] SYSTEM HEALTH: NEEDS ATTENTION")
    print("   - Untracked fill events detected (protective orders may not be tracked)")
else:
    print("[WARN] SYSTEM HEALTH: CHECK ERRORS")
    print("   - Errors detected in logs")
