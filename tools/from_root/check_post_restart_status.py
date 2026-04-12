#!/usr/bin/env python3
"""Check system status after restart"""
import json
from pathlib import Path
from datetime import datetime, timezone

restart_time = datetime.fromisoformat("2026-02-05T18:51:10+00:00")

log_dir = Path("logs/robot")
events_after_restart = []

for log_file in log_dir.glob("*.jsonl"):
    try:
        with open(log_file, 'r', encoding='utf-8-sig') as f:
            for line in f:
                if not line.strip():
                    continue
                try:
                    event = json.loads(line.strip())
                    ts_str = event.get('ts_utc', '')
                    if ts_str:
                        ts = datetime.fromisoformat(ts_str.replace('Z', '+00:00'))
                        if ts.tzinfo is None:
                            ts = ts.replace(tzinfo=timezone.utc)
                        if ts >= restart_time:
                            events_after_restart.append({
                                'ts': ts,
                                'event': event.get('event', ''),
                                'instrument': event.get('instrument', ''),
                                'level': event.get('level', ''),
                                'data': event.get('data', {})
                            })
                except:
                    pass
    except:
        pass

events_after_restart.sort(key=lambda x: x['ts'])

print("=" * 100)
print("SYSTEM STATUS AFTER RESTART (18:51:10)")
print("=" * 100)
print()

# Engine starts
engine_starts = [e for e in events_after_restart if 'ENGINE_START' in e['event'].upper()]
print(f"Engine Starts: {len(engine_starts)}")
if engine_starts:
    print(f"  Latest: {engine_starts[-1]['ts'].strftime('%H:%M:%S')}")
print()

# Protective order submissions
protective_subs = [e for e in events_after_restart 
                  if e['event'] == 'ORDER_SUBMIT_SUCCESS' and 
                  (e['data'].get('order_type') in ['PROTECTIVE_STOP', 'TARGET'] or 
                   '_orderMap' in str(e['data'].get('note', '')))]
print(f"Protective Order Submissions: {len(protective_subs)}")
if protective_subs:
    for e in protective_subs[:5]:
        note = e['data'].get('note', '')
        has_note = '_orderMap' in note
        print(f"  [{e['ts'].strftime('%H:%M:%S')}] {e['instrument']} - {e['data'].get('order_type', '')} {'[Added to _orderMap]' if has_note else ''}")
print()

# Exit fills
exit_fills = [e for e in events_after_restart if e['event'] == 'EXECUTION_EXIT_FILL']
print(f"Exit Fills (Protective Orders): {len(exit_fills)}")
if exit_fills:
    for e in exit_fills[:5]:
        print(f"  [{e['ts'].strftime('%H:%M:%S')}] {e['instrument']} - {e['data'].get('exit_order_type', '')} @ {e['data'].get('fill_price', '')}")
else:
    print("  None yet (waiting for protective orders to fill)")
print()

# Untracked fills (should NOT happen)
untracked = [e for e in events_after_restart 
            if 'UNTRACKED' in e['event'].upper() or 'UNKNOWN_ORDER_CRITICAL' in e['event'].upper()]
print(f"Untracked Fill Events: {len(untracked)}")
if untracked:
    print("  [ERROR] These should NOT occur for protective orders!")
    for e in untracked[:5]:
        tag = e['data'].get('tag', '') or e['data'].get('order_tag', '')
        order_type = 'UNKNOWN'
        if ':STOP' in tag:
            order_type = 'STOP'
        elif ':TARGET' in tag:
            order_type = 'TARGET'
        print(f"  [{e['ts'].strftime('%H:%M:%S')}] {e['instrument']} - {order_type} - {tag[:50]}")
else:
    print("  [OK] None found - Good!")
print()

# Errors
errors = [e for e in events_after_restart if e['level'] == 'ERROR']
print(f"Errors: {len(errors)}")
if errors:
    for e in errors[:5]:
        print(f"  [{e['ts'].strftime('%H:%M:%S')}] {e['event']} - {e['instrument']}")
else:
    print("  [OK] None found")
print()

# Summary
print("=" * 100)
print("SUMMARY")
print("=" * 100)
print(f"Total events after restart: {len(events_after_restart)}")
print(f"Engine starts: {len(engine_starts)}")
print(f"Protective orders submitted: {len(protective_subs)}")
print(f"Exit fills: {len(exit_fills)}")
print(f"Untracked fills: {len(untracked)}")
print(f"Errors: {len(errors)}")
print()

if len(untracked) == 0 and len(errors) == 0:
    print("[OK] SYSTEM HEALTH: GOOD")
    print("  - No untracked fill events")
    print("  - No errors")
    print("  - New DLL appears to be working")
elif len(untracked) > 0:
    print("[WARN] SYSTEM HEALTH: NEEDS ATTENTION")
    print("  - Untracked fill events detected")
    print("  - Protective orders may not be tracked properly")
else:
    print("[WARN] SYSTEM HEALTH: CHECK ERRORS")
    print("  - Errors detected in logs")
