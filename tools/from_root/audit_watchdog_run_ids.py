#!/usr/bin/env python3
"""
Audit Watchdog Run IDs

Checks:
- List all run_ids in feed
- Check which run_ids cursor tracks
- Verify active run_id detection
- Check if new run_ids are processed
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
print("WATCHDOG RUN ID AUDIT")
print("="*80)

# 1. List all run_ids in feed
print("\n1. RUN_IDS IN FEED")
print("-" * 80)

if not FRONTEND_FEED_FILE.exists():
    print(f"[ERROR] Feed file not found: {FRONTEND_FEED_FILE}")
    sys.exit(1)

run_id_stats = defaultdict(lambda: {'count': 0, 'max_seq': 0, 'latest_ts': None, 'event_types': set()})

with open(FRONTEND_FEED_FILE, 'r', encoding='utf-8') as f:
    for line in f:
        if line.strip():
            try:
                event = json.loads(line.strip())
                run_id = event.get('run_id')
                if run_id:
                    stats = run_id_stats[run_id]
                    stats['count'] += 1
                    seq = event.get('event_seq', 0)
                    if seq > stats['max_seq']:
                        stats['max_seq'] = seq
                    
                    ts_str = event.get('timestamp_utc')
                    if ts_str:
                        try:
                            ts = datetime.fromisoformat(ts_str.replace('Z', '+00:00'))
                            if stats['latest_ts'] is None or ts > stats['latest_ts']:
                                stats['latest_ts'] = ts
                        except:
                            pass
                    
                    event_type = event.get('event_type')
                    if event_type:
                        stats['event_types'].add(event_type)
            except json.JSONDecodeError:
                continue

print(f"Total unique run_ids: {len(run_id_stats)}")

# Sort by latest timestamp
sorted_runs = sorted(run_id_stats.items(), key=lambda x: x[1]['latest_ts'] or datetime.min.replace(tzinfo=timezone.utc), reverse=True)

print(f"\nTop 10 most recent run_ids:")
for i, (run_id, stats) in enumerate(sorted_runs[:10], 1):
    latest_ts = stats['latest_ts']
    age_seconds = (datetime.now(timezone.utc) - latest_ts).total_seconds() if latest_ts else None
    age_str = f"{age_seconds:.1f}s ago" if age_seconds else "unknown"
    print(f"  {i}. {run_id[:16]}...")
    print(f"     Events: {stats['count']:,}, Max seq: {stats['max_seq']}, Latest: {age_str}")
    print(f"     Event types: {len(stats['event_types'])} unique")

# 2. Check cursor tracking
print("\n2. CURSOR TRACKING")
print("-" * 80)

cursor_file = Path(FRONTEND_CURSOR_FILE)
if cursor_file.exists():
    with open(cursor_file, 'r') as f:
        cursor = json.load(f)
    print(f"✅ Cursor file exists: {cursor_file}")
    print(f"   Run_ids tracked: {len(cursor)}")
else:
    print(f"[ERROR] Cursor file not found: {cursor_file}")
    cursor = {}

# Compare feed run_ids with cursor
print(f"\n3. FEED VS CURSOR COMPARISON")
print("-" * 80)

feed_run_ids = set(run_id_stats.keys())
cursor_run_ids = set(cursor.keys())

in_feed_not_cursor = feed_run_ids - cursor_run_ids
in_cursor_not_feed = cursor_run_ids - feed_run_ids
in_both = feed_run_ids & cursor_run_ids

print(f"Run_ids in feed: {len(feed_run_ids)}")
print(f"Run_ids in cursor: {len(cursor_run_ids)}")
print(f"Run_ids in both: {len(in_both)}")
print(f"Run_ids in feed but NOT cursor: {len(in_feed_not_cursor)}")
print(f"Run_ids in cursor but NOT feed: {len(in_cursor_not_feed)}")

if in_feed_not_cursor:
    print(f"\n⚠️  Run_ids in feed but not tracked in cursor:")
    for run_id in list(in_feed_not_cursor)[:5]:
        stats = run_id_stats[run_id]
        latest_ts = stats['latest_ts']
        age_seconds = (datetime.now(timezone.utc) - latest_ts).total_seconds() if latest_ts else None
        age_str = f"{age_seconds:.1f}s ago" if age_seconds else "unknown"
        print(f"  {run_id[:16]}...: {stats['count']} events, max_seq={stats['max_seq']}, latest={age_str}")

# 4. Check active run_id
print("\n4. ACTIVE RUN_ID DETECTION")
print("-" * 80)

# Most recent run_id (by timestamp)
if sorted_runs:
    most_recent_run_id, most_recent_stats = sorted_runs[0]
    latest_ts = most_recent_stats['latest_ts']
    age_seconds = (datetime.now(timezone.utc) - latest_ts).total_seconds() if latest_ts else None
    
    print(f"Most recent run_id: {most_recent_run_id[:16]}...")
    print(f"  Latest event: {latest_ts.isoformat()[:19] if latest_ts else 'unknown'} ({age_seconds:.1f}s ago if known)")
    print(f"  Max seq: {most_recent_stats['max_seq']}")
    print(f"  Cursor seq: {cursor.get(most_recent_run_id, 0)}")
    
    cursor_seq = cursor.get(most_recent_run_id, 0)
    if cursor_seq == 0:
        print(f"  [WARN] WARNING: Most recent run_id not in cursor or cursor at 0")
    elif most_recent_stats['max_seq'] > cursor_seq:
        print(f"  [WARN] WARNING: Events behind cursor ({most_recent_stats['max_seq']} > {cursor_seq})")
    else:
        print(f"  [OK] Cursor up to date")

# Check for recent ticks/bars in most recent run_id
print("\n5. RECENT EVENTS IN ACTIVE RUN_ID")
print("-" * 80)

if sorted_runs:
    active_run_id = sorted_runs[0][0]
    
    # Read recent events for this run_id
    recent_events_for_run = []
    with open(FRONTEND_FEED_FILE, 'r', encoding='utf-8') as f:
        lines = f.readlines()
        for line in lines[-1000:]:  # Check last 1000 lines
            if line.strip():
                try:
                    event = json.loads(line.strip())
                    if event.get('run_id') == active_run_id:
                        recent_events_for_run.append(event)
                except:
                    continue
    
    ticks = [e for e in recent_events_for_run if e.get('event_type') == 'ENGINE_TICK_CALLSITE']
    bars = [e for e in recent_events_for_run if 'BAR' in e.get('event_type', '')]
    
    print(f"Recent events for active run_id ({active_run_id[:16]}...):")
    print(f"  Total in last 1000 lines: {len(recent_events_for_run)}")
    print(f"  ENGINE_TICK_CALLSITE: {len(ticks)}")
    print(f"  Bar events: {len(bars)}")
    
    if ticks:
        latest_tick = ticks[-1]
        tick_ts = datetime.fromisoformat(latest_tick.get('timestamp_utc', '').replace('Z', '+00:00'))
        age_seconds = (datetime.now(timezone.utc) - tick_ts).total_seconds()
        print(f"\n  Latest tick: {tick_ts.isoformat()[:19]} ({age_seconds:.1f}s ago)")
        print(f"    Seq: {latest_tick.get('event_seq', 0)}")
    
    if bars:
        latest_bar = bars[-1]
        bar_ts = datetime.fromisoformat(latest_bar.get('timestamp_utc', '').replace('Z', '+00:00'))
        age_seconds = (datetime.now(timezone.utc) - bar_ts).total_seconds()
        instrument = latest_bar.get('execution_instrument_full_name') or latest_bar.get('instrument') or 'UNKNOWN'
        print(f"\n  Latest bar: {bar_ts.isoformat()[:19]} ({age_seconds:.1f}s ago)")
        print(f"    Instrument: {instrument}")
        print(f"    Seq: {latest_bar.get('event_seq', 0)}")

print("\n" + "="*80)
print("AUDIT COMPLETE")
print("="*80)
