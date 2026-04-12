#!/usr/bin/env python3
"""Comprehensive robot status assessment"""
import json
import glob
import re
from datetime import datetime, timezone
from collections import defaultdict

print("=" * 80)
print("ROBOT STATUS ASSESSMENT")
print("=" * 80)
print(f"Assessment Time: {datetime.now(timezone.utc).isoformat()}")
print()

# 1. CHECK ENGINE STATUS
print("=" * 80)
print("1. ENGINE STATUS")
print("=" * 80)

engine_log = "logs/robot/robot_ENGINE.jsonl"
with open(engine_log, 'r', encoding='utf-8-sig') as f:
    events = [json.loads(l) for l in f if l.strip()]

# Get most recent events (last hour)
recent_cutoff = datetime.now(timezone.utc).timestamp() - 3600
recent_events = [e for e in events if 
                 (ts_str := e.get('ts') or e.get('ts_utc', '')) and
                 (ts := datetime.fromisoformat(ts_str.replace('Z', '+00:00')).timestamp()) > recent_cutoff]

print(f"Total events in log: {len(events)}")
print(f"Events in last hour: {len(recent_events)}")

# Check for ENGINE_START
engine_starts = [e for e in events if 'ENGINE_START' in str(e.get('event', ''))]
if engine_starts:
    last_start = engine_starts[-1]
    start_ts = last_start.get('ts') or last_start.get('ts_utc', 'N/A')
    print(f"[OK] Last ENGINE_START: {start_ts}")
else:
    print("[WARN] No ENGINE_START events found")

# Check ENGINE_TICK (liveness)
engine_ticks = [e for e in recent_events if 'ENGINE_TICK' in str(e.get('event', ''))]
print(f"[OK] ENGINE_TICK events (last hour): {len(engine_ticks)}")

# 2. CHECK STREAM CREATION
print("\n" + "=" * 80)
print("2. STREAM CREATION STATUS")
print("=" * 80)

# Check TIMETABLE_PARSING_COMPLETE
parsing_complete = [e for e in recent_events if 'TIMETABLE_PARSING_COMPLETE' in str(e.get('event', ''))]
if parsing_complete:
    latest = parsing_complete[-1]
    payload_str = str(latest.get('payload') or latest.get('data', {}).get('payload', ''))
    ts = latest.get('ts') or latest.get('ts_utc', 'N/A')
    
    accepted_match = re.search(r'accepted\s*=\s*(\d+)', payload_str)
    skipped_match = re.search(r'skipped\s*=\s*(\d+)', payload_str)
    total_match = re.search(r'total_enabled\s*=\s*(\d+)', payload_str)
    
    print(f"Latest TIMETABLE_PARSING_COMPLETE: {ts}")
    if total_match:
        print(f"  Total Enabled: {total_match.group(1)}")
    if accepted_match:
        accepted = int(accepted_match.group(1))
        status = "[OK]" if accepted > 0 else "[WARN]"
        print(f"  {status} Accepted: {accepted}")
    if skipped_match:
        skipped = int(skipped_match.group(1))
        print(f"  Skipped: {skipped}")

# Check STREAMS_CREATED
streams_created = [e for e in recent_events if 'STREAMS_CREATED' in str(e.get('event', ''))]
if streams_created:
    latest = streams_created[-1]
    payload_str = str(latest.get('payload') or latest.get('data', {}).get('payload', ''))
    ts = latest.get('ts') or latest.get('ts_utc', 'N/A')
    
    count_match = re.search(r'stream_count\s*=\s*(\d+)', payload_str)
    print(f"\nLatest STREAMS_CREATED: {ts}")
    if count_match:
        count = int(count_match.group(1))
        status = "[OK]" if count > 0 else "[WARN]"
        print(f"  {status} Stream Count: {count}")
else:
    print("[WARN] No STREAMS_CREATED events in last hour")

# Check per-instrument stream status
print("\nPer-Instrument Stream Status:")
instrument_logs = glob.glob("logs/robot/robot_*.jsonl")
instrument_streams = defaultdict(lambda: {'created': 0, 'skipped': 0, 'recent_activity': False})

for log_file in instrument_logs:
    if 'ENGINE' in log_file:
        continue
    
    instrument = log_file.split('_')[-1].replace('.jsonl', '')
    with open(log_file, 'r', encoding='utf-8-sig') as f:
        for line in f:
            try:
                e = json.loads(line.strip())
                ts_str = e.get('ts') or e.get('ts_utc', '')
                if ts_str:
                    ts = datetime.fromisoformat(ts_str.replace('Z', '+00:00')).timestamp()
                    if ts > recent_cutoff:
                        instrument_streams[instrument]['recent_activity'] = True
                        
                        if 'STREAM_CREATED' in str(e.get('event', '')):
                            instrument_streams[instrument]['created'] += 1
                        if 'CANONICAL_MISMATCH' in str(e):
                            instrument_streams[instrument]['skipped'] += 1
            except:
                pass

for instrument in sorted(instrument_streams.keys()):
    status = instrument_streams[instrument]
    if status['recent_activity']:
        working = "[OK]" if status['created'] > 0 else "[SKIP]"
        print(f"  {instrument}: {working} Created={status['created']}, Skipped={status['skipped']}")

# 3. CHECK BARSREQUEST STATUS
print("\n" + "=" * 80)
print("3. BARSREQUEST STATUS")
print("=" * 80)

barsrequest_requested = [e for e in recent_events if 'BARSREQUEST_REQUESTED' in str(e.get('event', ''))]
barsrequest_executed = [e for e in recent_events if 'BARSREQUEST_EXECUTED' in str(e.get('event', ''))]
barsrequest_skipped = [e for e in recent_events if 'BARSREQUEST_SKIPPED' in str(e.get('event', ''))]
barsrequest_failed = [e for e in recent_events if 'BARSREQUEST_FAILED' in str(e.get('event', ''))]

print(f"[OK] BARSREQUEST_REQUESTED: {len(barsrequest_requested)}")
print(f"[OK] BARSREQUEST_EXECUTED: {len(barsrequest_executed)}")
print(f"[WARN] BARSREQUEST_SKIPPED: {len(barsrequest_skipped)}")
if barsrequest_failed:
    print(f"[ERROR] BARSREQUEST_FAILED: {len(barsrequest_failed)}")

if barsrequest_executed:
    print("\nRecent BARSREQUEST_EXECUTED events:")
    for e in barsrequest_executed[-5:]:
        payload_str = str(e.get('payload') or e.get('data', {}).get('payload', ''))
        ts = e.get('ts') or e.get('ts_utc', 'N/A')
        instrument_match = re.search(r'instrument\s*=\s*([^,}]+)', payload_str)
        bars_match = re.search(r'bars_returned\s*=\s*(\d+)', payload_str)
        
        instrument = instrument_match.group(1).strip() if instrument_match else 'N/A'
        bars = bars_match.group(1) if bars_match else 'N/A'
        status = "[OK]" if bars != 'N/A' and int(bars) > 0 else "[WARN]"
        print(f"  {status} [{ts}] Instrument: {instrument}, Bars: {bars}")

# 4. CHECK CONNECTION STATUS
print("\n" + "=" * 80)
print("4. CONNECTION STATUS")
print("=" * 80)

connection_events = [e for e in recent_events if 'CONNECTION' in str(e.get('event', '')).upper()]
if connection_events:
    print(f"Connection events (last hour): {len(connection_events)}")
    for e in connection_events[-5:]:
        ts = e.get('ts') or e.get('ts_utc', 'N/A')
        event_type = e.get('event') or e.get('event_type', 'N/A')
        print(f"  [{ts}] {event_type}")
else:
    print("[OK] No connection events in last hour (assuming stable connection)")

# 5. CHECK FOR ERRORS/WARNINGS
print("\n" + "=" * 80)
print("5. ERRORS AND WARNINGS")
print("=" * 80)

error_events = [e for e in recent_events if e.get('level') == 'ERROR' or 'ERROR' in str(e.get('event', '')).upper()]
warn_events = [e for e in recent_events if e.get('level') == 'WARN' or 'WARN' in str(e.get('event', '')).upper()]

print(f"[ERROR] Error events (last hour): {len(error_events)}")
if error_events:
    print("Recent errors:")
    for e in error_events[-5:]:
        ts = e.get('ts') or e.get('ts_utc', 'N/A')
        event_type = e.get('event') or e.get('event_type', 'N/A')
        message = e.get('message', 'N/A')
        print(f"  [{ts}] {event_type}: {message[:100]}")

print(f"\n[WARN] Warning events (last hour): {len(warn_events)}")
if warn_events:
    print("Recent warnings:")
    for e in warn_events[-10:]:
        ts = e.get('ts') or e.get('ts_utc', 'N/A')
        event_type = e.get('event') or e.get('event_type', 'N/A')
        print(f"  [{ts}] {event_type}")

# 6. CHECK STREAM STATES
print("\n" + "=" * 80)
print("6. STREAM STATES")
print("=" * 80)

# Check for stream state transitions
state_events = [e for e in recent_events if 'STREAM_' in str(e.get('event', '')) and 
                ('ARMED' in str(e.get('event', '')) or 'HYDRATION' in str(e.get('event', '')) or 
                 'RANGE_LOCKED' in str(e.get('event', '')) or 'DONE' in str(e.get('event', '')))]

if state_events:
    print(f"Stream state events (last hour): {len(state_events)}")
    states = defaultdict(int)
    for e in state_events:
        event_str = str(e.get('event') or e.get('event_type', ''))
        if 'ARMED' in event_str:
            states['ARMED'] += 1
        elif 'HYDRATION' in event_str:
            states['HYDRATION'] += 1
        elif 'RANGE_LOCKED' in event_str:
            states['RANGE_LOCKED'] += 1
        elif 'DONE' in event_str:
            states['DONE'] += 1
    
    for state, count in sorted(states.items()):
        print(f"  {state}: {count} events")
else:
    print("[INFO] No stream state transitions in last hour")

# 7. SUMMARY
print("\n" + "=" * 80)
print("7. OVERALL STATUS SUMMARY")
print("=" * 80)

issues = []
if not engine_starts:
    issues.append("No ENGINE_START found")
if len(engine_ticks) == 0:
    issues.append("No ENGINE_TICK in last hour (possible stall)")
if len(streams_created) == 0:
    issues.append("No streams created in last hour")
if len(barsrequest_executed) == 0:
    issues.append("No BarsRequest executed in last hour")
if len(error_events) > 0:
    issues.append(f"{len(error_events)} error events in last hour")

if issues:
    print("[WARN] Issues detected:")
    for issue in issues:
        print(f"  - {issue}")
else:
    print("[OK] No major issues detected")

# Check if streams are being created
if parsing_complete:
    latest = parsing_complete[-1]
    payload_str = str(latest.get('payload') or latest.get('data', {}).get('payload', ''))
    accepted_match = re.search(r'accepted\s*=\s*(\d+)', payload_str)
    if accepted_match and int(accepted_match.group(1)) > 0:
        print("[OK] Streams are being accepted")
    else:
        print("[WARN] No streams accepted in recent parsing")

print("\n" + "=" * 80)
