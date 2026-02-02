#!/usr/bin/env python3
"""
Audit Watchdog Event Processing

Checks:
- Cursor position vs latest events
- Events are being read
- Processing loop is running
- End-of-file reads work
"""
import json
import sys
from pathlib import Path
from datetime import datetime, timezone
from collections import defaultdict

# Add project root to path
project_root = Path(__file__).parent
sys.path.insert(0, str(project_root))

from modules.watchdog.config import FRONTEND_FEED_FILE, FRONTEND_CURSOR_FILE

print("="*80)
print("WATCHDOG EVENT PROCESSING AUDIT")
print("="*80)

# 1. Check cursor position vs latest events
print("\n1. CURSOR POSITION ANALYSIS")
print("-" * 80)

cursor_file = Path(FRONTEND_CURSOR_FILE)
if cursor_file.exists():
    with open(cursor_file, 'r') as f:
        cursor = json.load(f)
    print(f"Cursor file exists: {cursor_file}")
    print(f"Number of run_ids tracked: {len(cursor)}")
    
    # Show top 10 run_ids by seq
    sorted_runs = sorted(cursor.items(), key=lambda x: x[1], reverse=True)[:10]
    print("\nTop 10 run_ids by cursor position:")
    for run_id, seq in sorted_runs:
        print(f"  {run_id[:16]}...: seq={seq}")
else:
    print(f"Cursor file NOT FOUND: {cursor_file}")
    cursor = {}

# Check feed file
if not FRONTEND_FEED_FILE.exists():
    print(f"\nERROR: Feed file not found: {FRONTEND_FEED_FILE}")
    sys.exit(1)

print(f"\nFeed file exists: {FRONTEND_FEED_FILE}")
feed_size_mb = FRONTEND_FEED_FILE.stat().st_size / (1024 * 1024)
print(f"Feed file size: {feed_size_mb:.2f} MB")

# Read latest events
print("\n2. LATEST EVENTS IN FEED")
print("-" * 80)

with open(FRONTEND_FEED_FILE, 'r', encoding='utf-8') as f:
    lines = f.readlines()

total_events = len(lines)
print(f"Total events in feed: {total_events:,}")

if not lines:
    print("ERROR: Feed file is empty!")
    sys.exit(1)

# Analyze last 100 events
recent_events = []
for line in lines[-100:]:
    if line.strip():
        try:
            event = json.loads(line.strip())
            recent_events.append(event)
        except json.JSONDecodeError:
            continue

print(f"\nLast 100 events analysis:")
run_ids_in_recent = defaultdict(int)
event_types_in_recent = defaultdict(int)
for event in recent_events:
    run_id = event.get('run_id', 'NO_RUN_ID')
    event_type = event.get('event_type', 'UNKNOWN')
    run_ids_in_recent[run_id] += 1
    event_types_in_recent[event_type] += 1

print(f"\nRun IDs in last 100 events:")
for run_id, count in sorted(run_ids_in_recent.items(), key=lambda x: x[1], reverse=True)[:5]:
    latest_seq = max((e.get('event_seq', 0) for e in recent_events if e.get('run_id') == run_id), default=0)
    cursor_seq = cursor.get(run_id, 0)
    behind = latest_seq - cursor_seq
    print(f"  {run_id[:16]}...: {count} events, latest_seq={latest_seq}, cursor_seq={cursor_seq}, behind={behind}")

print(f"\nEvent types in last 100 events:")
for event_type, count in sorted(event_types_in_recent.items(), key=lambda x: x[1], reverse=True)[:10]:
    print(f"  {event_type}: {count}")

# Check for ticks and bars
print("\n3. TICK AND BAR EVENTS")
print("-" * 80)

ticks = [e for e in recent_events if e.get('event_type') == 'ENGINE_TICK_CALLSITE']
bars = [e for e in recent_events if 'BAR' in e.get('event_type', '')]

print(f"ENGINE_TICK_CALLSITE in last 100: {len(ticks)}")
if ticks:
    latest_tick = ticks[-1]
    tick_ts = datetime.fromisoformat(latest_tick.get('timestamp_utc', '').replace('Z', '+00:00'))
    age_seconds = (datetime.now(timezone.utc) - tick_ts).total_seconds()
    print(f"  Latest tick: {tick_ts.isoformat()[:19]} ({age_seconds:.1f}s ago)")
    print(f"  Run ID: {latest_tick.get('run_id', '')[:16]}...")
    print(f"  Seq: {latest_tick.get('event_seq', 0)}")

print(f"\nBar events in last 100: {len(bars)}")
if bars:
    latest_bar = bars[-1]
    bar_ts = datetime.fromisoformat(latest_bar.get('timestamp_utc', '').replace('Z', '+00:00'))
    age_seconds = (datetime.now(timezone.utc) - bar_ts).total_seconds()
    instrument = latest_bar.get('execution_instrument_full_name') or latest_bar.get('instrument') or 'UNKNOWN'
    print(f"  Latest bar: {bar_ts.isoformat()[:19]} ({age_seconds:.1f}s ago)")
    print(f"  Instrument: {instrument}")
    print(f"  Run ID: {latest_bar.get('run_id', '')[:16]}...")
    print(f"  Seq: {latest_bar.get('event_seq', 0)}")

# Test end-of-file reads
print("\n4. END-OF-FILE READ TEST")
print("-" * 80)

# Simulate _read_recent_ticks_from_end
def read_recent_ticks_from_end(max_events=1):
    ticks = []
    try:
        with open(FRONTEND_FEED_FILE, 'rb') as f:
            f.seek(0, 2)  # Seek to end
            file_size = f.tell()
            chunk_size = 8192
            buffer = b''
            position = file_size
            
            while position > 0 and len(ticks) < max_events:
                read_size = min(chunk_size, position)
                position -= read_size
                f.seek(position)
                chunk = f.read(read_size)
                buffer = chunk + buffer
                
                while b'\n' in buffer:
                    line, buffer = buffer.rsplit(b'\n', 1)
                    if line.strip():
                        try:
                            event = json.loads(line.decode('utf-8-sig', errors='ignore').strip())
                            if event.get('event_type') == 'ENGINE_TICK_CALLSITE':
                                ticks.append(event)
                                if len(ticks) >= max_events:
                                    break
                        except:
                            continue
                
                if position == 0:
                    break
    except Exception as e:
        print(f"  ERROR reading ticks: {e}")
    
    return ticks

test_ticks = read_recent_ticks_from_end(max_events=1)
if test_ticks:
    tick = test_ticks[0]
    tick_ts = datetime.fromisoformat(tick.get('timestamp_utc', '').replace('Z', '+00:00'))
    age_seconds = (datetime.now(timezone.utc) - tick_ts).total_seconds()
    print(f"[OK] End-of-file tick read SUCCESS")
    print(f"   Tick timestamp: {tick_ts.isoformat()[:19]} ({age_seconds:.1f}s ago)")
    print(f"   Run ID: {tick.get('run_id', '')[:16]}...")
    print(f"   Seq: {tick.get('event_seq', 0)}")
else:
    print(f"[ERROR] End-of-file tick read FAILED - no ticks found")

# Simulate _read_recent_bar_events_from_end
def read_recent_bars_from_end(max_events=10):
    bars = []
    try:
        with open(FRONTEND_FEED_FILE, 'rb') as f:
            f.seek(0, 2)
            file_size = f.tell()
            chunk_size = 8192
            buffer = b''
            position = file_size
            
            while position > 0 and len(bars) < max_events:
                read_size = min(chunk_size, position)
                position -= read_size
                f.seek(position)
                chunk = f.read(read_size)
                buffer = chunk + buffer
                
                while b'\n' in buffer:
                    line, buffer = buffer.rsplit(b'\n', 1)
                    if line.strip():
                        try:
                            event = json.loads(line.decode('utf-8-sig', errors='ignore').strip())
                            event_type = event.get('event_type', '')
                            if event_type in ('BAR_RECEIVED_NO_STREAMS', 'BAR_ACCEPTED', 'ONBARUPDATE_CALLED'):
                                bars.append(event)
                                if len(bars) >= max_events:
                                    break
                        except:
                            continue
                
                if position == 0:
                    break
    except Exception as e:
        print(f"  ERROR reading bars: {e}")
    
    return bars

test_bars = read_recent_bars_from_end(max_events=5)
if test_bars:
    print(f"\n[OK] End-of-file bar read SUCCESS ({len(test_bars)} bars found)")
    for bar in test_bars[:3]:
        bar_ts = datetime.fromisoformat(bar.get('timestamp_utc', '').replace('Z', '+00:00'))
        age_seconds = (datetime.now(timezone.utc) - bar_ts).total_seconds()
        instrument = bar.get('execution_instrument_full_name') or bar.get('instrument') or 'UNKNOWN'
        print(f"   Bar: {bar_ts.isoformat()[:19]} ({age_seconds:.1f}s ago), instrument={instrument}")
else:
    print(f"\n[ERROR] End-of-file bar read FAILED - no bars found")

# Check if events are behind cursor
print("\n5. CURSOR VS EVENTS ANALYSIS")
print("-" * 80)

# Find most recent run_id
run_id_seqs = defaultdict(int)
for event in recent_events:
    run_id = event.get('run_id')
    if run_id:
        seq = event.get('event_seq', 0)
        if seq > run_id_seqs[run_id]:
            run_id_seqs[run_id] = seq

if run_id_seqs:
    most_recent_run_id = max(run_id_seqs.items(), key=lambda x: x[1])[0]
    most_recent_seq = run_id_seqs[most_recent_run_id]
    cursor_seq = cursor.get(most_recent_run_id, 0)
    
    print(f"Most recent run_id: {most_recent_run_id[:16]}...")
    print(f"  Latest event seq: {most_recent_seq}")
    print(f"  Cursor seq: {cursor_seq}")
    print(f"  Events behind cursor: {max(0, most_recent_seq - cursor_seq)}")
    
    if cursor_seq == 0:
        print(f"  ⚠️  WARNING: Cursor is at 0 for most recent run_id!")
    elif most_recent_seq > cursor_seq:
        print(f"  ⚠️  WARNING: Events are behind cursor!")
    else:
        print(f"  [OK] Cursor is up to date")

print("\n" + "="*80)
print("AUDIT COMPLETE")
print("="*80)
