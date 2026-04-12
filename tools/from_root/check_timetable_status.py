#!/usr/bin/env python3
"""Check timetable status and recent events"""
import json
from pathlib import Path

# Check timetable
tt_path = Path("data/timetable/timetable_current.json")
if tt_path.exists():
    tt = json.loads(tt_path.read_text())
    enabled = [s for s in tt.get('streams', []) if s.get('enabled')]
    disabled = [s for s in tt.get('streams', []) if not s.get('enabled')]
    
    print("="*80)
    print("TIMETABLE STATUS")
    print("="*80)
    print(f"Total streams: {len(tt.get('streams', []))}")
    print(f"Enabled: {len(enabled)}")
    print(f"Disabled: {len(disabled)}")
    print(f"\nEnabled streams:")
    for s in enabled:
        print(f"  {s.get('stream')} ({s.get('instrument')}) - block_reason: {s.get('block_reason', 'none')}")
    
    print(f"\nDisabled streams:")
    for s in disabled[:10]:
        print(f"  {s.get('stream')} ({s.get('instrument')}) - block_reason: {s.get('block_reason', 'none')}")
else:
    print(f"Timetable file not found: {tt_path}")

# Check latest STREAMS_CREATED
print(f"\n{'='*80}")
print("LATEST STREAMS_CREATED EVENT")
print(f"{'='*80}")

log_dir = Path("logs/robot")
events = []
for log_file in log_dir.glob("robot_*.jsonl"):
    try:
        with open(log_file, 'r', encoding='utf-8') as f:
            for line in f:
                line = line.strip()
                if line:
                    try:
                        events.append(json.loads(line))
                    except:
                        pass
    except:
        pass

streams_created = [e for e in events 
                  if e.get('ts_utc', '').startswith('2026-01-26') and
                  e.get('event') == 'STREAMS_CREATED']

if streams_created:
    latest = max(streams_created, key=lambda x: x.get('ts_utc', ''))
    print(f"Timestamp: {latest.get('ts_utc', '')[:19]}")
    data = latest.get('data', {})
    print(f"Stream count: {data.get('stream_count', 0)}")
    streams = data.get('streams', [])
    print(f"\nStreams created:")
    for s in streams[:20]:
        print(f"  {s.get('stream_id')} ({s.get('execution_instrument')} -> {s.get('canonical_instrument')})")
else:
    print("No STREAMS_CREATED events found for today")
    print("Robot may not have started yet after re-enabling")

print(f"\n{'='*80}")
