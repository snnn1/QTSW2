#!/usr/bin/env python3
"""Check recent activity after restart"""
import json
from pathlib import Path
from datetime import datetime, timezone

restart = datetime.fromisoformat("2026-02-05T18:51:10+00:00")
events = []

for log_file in Path("logs/robot").glob("*.jsonl"):
    try:
        with open(log_file, 'r', encoding='utf-8-sig') as f:
            for line in f:
                if not line.strip():
                    continue
                try:
                    e = json.loads(line.strip())
                    ts_str = e.get('ts_utc', '')
                    if ts_str:
                        ts = datetime.fromisoformat(ts_str.replace('Z', '+00:00'))
                        if ts.tzinfo is None:
                            ts = ts.replace(tzinfo=timezone.utc)
                        if ts >= restart:
                            events.append(e)
                except:
                    pass
    except:
        pass

events.sort(key=lambda x: x.get('ts_utc', ''))

print("=" * 100)
print("RECENT ACTIVITY AFTER RESTART")
print("=" * 100)
print()

# Entry fills
entry_fills = [e for e in events if 'ENTRY' in e.get('event', '').upper() and 'FILL' in e.get('event', '').upper()]
print(f"Entry Fills: {len(entry_fills)}")
for e in entry_fills[:5]:
    print(f"  {e.get('ts_utc', '')[:19]} {e.get('instrument', '')} @ {e.get('data', {}).get('fill_price', '')}")
print()

# Order submissions
order_subs = [e for e in events if 'ORDER_SUBMIT' in e.get('event', '').upper()]
print(f"Order Submissions: {len(order_subs)}")
for e in order_subs[:10]:
    ot = e.get('data', {}).get('order_type', 'N/A')
    note = e.get('data', {}).get('note', '')
    print(f"  {e.get('ts_utc', '')[:19]} {e.get('instrument', '')} - {e.get('event', '')} - {ot}")
    if '_orderMap' in note:
        print(f"    -> Added to _orderMap")
print()

# Protective order submissions specifically
protective = [e for e in events if e.get('event') == 'ORDER_SUBMIT_SUCCESS' and 
             (e.get('data', {}).get('order_type') in ['PROTECTIVE_STOP', 'TARGET'] or 
              '_orderMap' in str(e.get('data', {}).get('note', '')))]
print(f"Protective Order Submissions: {len(protective)}")
for e in protective[:5]:
    note = e.get('data', {}).get('note', '')
    print(f"  {e.get('ts_utc', '')[:19]} {e.get('instrument', '')} - {e.get('data', {}).get('order_type', '')}")
    if '_orderMap' in note:
        print(f"    -> Added to _orderMap [OK]")
print()
