#!/usr/bin/env python3
"""
Check logs for zero quantity orders and STOP_BRACKETS events
"""
import json
import glob
from pathlib import Path
from datetime import datetime, timezone

# Find recent log files
log_files = sorted(glob.glob('logs/robot/**/*.jsonl', recursive=True), 
                   key=lambda x: Path(x).stat().st_mtime, reverse=True)[:10]

print(f"Checking {len(log_files)} most recent log files...\n")

events_found = []
for log_file in log_files:
    try:
        with open(log_file, 'r', encoding='utf-8') as f:
            for line in f:
                try:
                    event = json.loads(line.strip())
                    event_type = event.get('event', '')
                    data = event.get('data', {})
                    
                    # Look for STOP_BRACKETS events or orders with quantity issues
                    if ('STOP_BRACKETS' in event_type or 
                        'ORDER' in event_type.upper() or
                        'ENTRY_STOP' in event_type.upper()):
                        
                        quantity = data.get('quantity', data.get('order_quantity', 'N/A'))
                        if quantity == 0 or 'quantity' in str(data).lower():
                            events_found.append({
                                'ts': event.get('ts_utc', 'N/A')[:19],
                                'event': event_type,
                                'quantity': quantity,
                                'instrument': data.get('instrument', 'N/A'),
                                'stream': data.get('stream_id', 'N/A'),
                                'note': str(data.get('note', ''))[:100]
                            })
                except:
                    continue
    except:
        continue

print(f"Found {len(events_found)} relevant events\n")
print("=" * 120)
for e in events_found[-30:]:
    print(f"{e['ts']} | {e['event']:40} | Qty={e['quantity']:6} | Inst={e['instrument']:8} | Stream={e['stream']}")
    if e['note']:
        print(f"  Note: {e['note']}")
    print()
