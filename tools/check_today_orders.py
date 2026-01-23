#!/usr/bin/env python3
"""Quick check for today's order submissions and range locks."""
import json
from pathlib import Path
from collections import Counter
from datetime import datetime, timezone

log_dir = Path("logs/robot")
today_str = "2026-01-22"

events = []
for f in log_dir.glob("robot_*.jsonl"):
    if not f.is_file():
        continue
    try:
        with open(f, 'r', encoding='utf-8') as handle:
            for line in handle:
                line = line.strip()
                if not line:
                    continue
                try:
                    e = json.loads(line)
                    if isinstance(e, dict):
                        ts = e.get('ts_utc', '')
                        if ts and ts.startswith(today_str):
                            events.append(e)
                except:
                    pass
    except:
        pass

print("=" * 80)
print(f"TODAY'S ROBOT LOG SUMMARY ({today_str})")
print("=" * 80)

# Event type breakdown
event_types = Counter([e.get('event') or e.get('event_type', 'UNKNOWN') for e in events])
print(f"\nTotal events today: {len(events)}")
print(f"\nTop 15 event types:")
for k, v in event_types.most_common(15):
    print(f"  {k}: {v}")

# Range locks
range_locked = [e for e in events if e.get('event') == 'RANGE_LOCKED']
print(f"\nRANGE_LOCKED events: {len(range_locked)}")
if range_locked:
    for r in range_locked[:10]:
        data = r.get('data', {})
        # Handle both 'data' dict and 'payload' nested structure
        payload = data.get('payload', {}) if isinstance(data.get('payload'), dict) else {}
        actual_data = payload if payload else data
        print(f"  {r.get('ts_utc', '')[:19]}Z | {r.get('instrument', '')} | stream={actual_data.get('stream', '')} | high={actual_data.get('range_high')} | low={actual_data.get('range_low')}")

# Order submissions (focus on ORDER_SUBMITTED/SUCCESS)
order_submitted = [e for e in events if e.get('event') in ('ORDER_SUBMITTED', 'ORDER_SUBMIT_SUCCESS')]
print(f"\nORDER_SUBMITTED/SUCCESS events: {len(order_submitted)}")
if order_submitted:
    for o in order_submitted[:20]:
        evt_name = o.get('event') or o.get('event_type', 'N/A')
        data = o.get('data', {})
        payload = data.get('payload', {}) if isinstance(data.get('payload'), dict) else {}
        actual_data = payload if payload else data
        print(f"  {o.get('ts_utc', '')[:19]}Z | {o.get('instrument', '')} | {evt_name} | type={actual_data.get('order_type', 'N/A')} | dir={actual_data.get('direction', 'N/A')} | price={actual_data.get('stop_price') or actual_data.get('entry_price', 'N/A')}")

# Executions
exec_events = [e for e in events if 'EXECUTION' in str(e.get('event', '')).upper() or 'FILL' in str(e.get('event', '')).upper()]
print(f"\nExecution/Fill events: {len(exec_events)}")
if exec_events:
    for ex in exec_events[:15]:
        print(f"  {ex.get('ts_utc', '')[:19]}Z | {ex.get('instrument', '')} | {ex.get('event') or ex.get('event_type', 'N/A')}")

# Errors summary
errors = [e for e in events if e.get('level') == 'ERROR' or 'ERROR' in str(e.get('event', '')).upper()]
print(f"\nERROR-level events: {len(errors)}")
error_types = Counter([e.get('event') or e.get('event_type', 'UNKNOWN') for e in errors])
print("Top error types:")
for k, v in error_types.most_common(10):
    print(f"  {k}: {v}")

print("\n" + "=" * 80)
