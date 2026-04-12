#!/usr/bin/env python3
"""
Check logs for order rejections and failures today (2026-01-30)
"""
import json
import glob
from pathlib import Path
from datetime import datetime, timezone

# Find log files from today
log_files = sorted(glob.glob('logs/robot/**/*.jsonl', recursive=True), 
                   key=lambda x: Path(x).stat().st_mtime, reverse=True)

print(f"Checking {len(log_files)} log files for today's orders...\n")

events_found = []
for log_file in log_files:
    try:
        with open(log_file, 'r', encoding='utf-8') as f:
            for line in f:
                try:
                    event = json.loads(line.strip())
                    ts = event.get('ts_utc', '')
                    
                    # Focus on today (2026-01-30)
                    if '2026-01-30' not in ts:
                        continue
                    
                    event_type = event.get('event', '')
                    data = event.get('data', {})
                    
                    # Look for order-related events
                    if any(keyword in event_type.upper() for keyword in 
                           ['ORDER', 'ENTRY', 'STOP', 'BRACKET', 'REJECT', 'FAIL', 'BLOCK']):
                        
                        instrument = data.get('instrument', data.get('execution_instrument', 'N/A'))
                        quantity = data.get('quantity', data.get('order_quantity', data.get('requested_quantity', 'N/A')))
                        reason = data.get('reason', data.get('error', data.get('note', '')))
                        
                        events_found.append({
                            'ts': ts[:19],
                            'event': event_type,
                            'quantity': quantity,
                            'instrument': instrument,
                            'reason': str(reason)[:150],
                            'full_data': data
                        })
                except:
                    continue
    except:
        continue

# Sort by timestamp
events_found.sort(key=lambda x: x['ts'])

print(f"Found {len(events_found)} order-related events from today\n")
print("=" * 150)
for e in events_found:
    print(f"{e['ts']} | {e['event']:45} | Inst={e['instrument']:8} | Qty={e['quantity']}")
    if e['reason']:
        print(f"  Reason: {e['reason']}")
    print()
