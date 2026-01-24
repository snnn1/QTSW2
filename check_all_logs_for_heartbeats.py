"""Check ALL log files for heartbeat events"""
import json
from pathlib import Path
from datetime import datetime, timezone
import pytz

CHICAGO_TZ = pytz.timezone("America/Chicago")

log_dir = Path('logs/robot')
if not log_dir.exists():
    print("Log directory not found")
    exit(1)

print("=" * 80)
print("SEARCHING ALL LOG FILES FOR ENGINE_TICK_HEARTBEAT")
print("=" * 80)

# Find all JSONL files
jsonl_files = list(log_dir.glob('*.jsonl'))
print(f"\n[INFO] Found {len(jsonl_files)} JSONL files:")
for f in jsonl_files:
    print(f"  - {f.name}")

all_heartbeats = []

for log_file in jsonl_files:
    print(f"\n{'='*80}")
    print(f"Checking: {log_file.name}")
    print(f"{'='*80}")
    
    try:
        with open(log_file, 'r', encoding='utf-8') as f:
            lines = f.readlines()
        
        print(f"  Total lines: {len(lines)}")
        
        # Search for heartbeats
        heartbeats_in_file = []
        for i, line in enumerate(lines):
            if line.strip():
                try:
                    event = json.loads(line.strip())
                    event_type = event.get('event_type', '')
                    
                    if event_type == 'ENGINE_TICK_HEARTBEAT':
                        heartbeats_in_file.append((i+1, event))
                        all_heartbeats.append((log_file.name, i+1, event))
                except:
                    continue
        
        print(f"  Heartbeats found: {len(heartbeats_in_file)}")
        
        if heartbeats_in_file:
            print(f"\n  Last 5 heartbeats in this file:")
            for line_num, event in heartbeats_in_file[-5:]:
                ts_utc_str = event.get('timestamp_utc', event.get('ts_utc', ''))
                try:
                    ts_utc = datetime.fromisoformat(ts_utc_str.replace('Z', '+00:00'))
                    if ts_utc.tzinfo is None:
                        ts_utc = ts_utc.replace(tzinfo=timezone.utc)
                    ts_chicago = ts_utc.astimezone(CHICAGO_TZ)
                    now = datetime.now(timezone.utc)
                    elapsed = (now - ts_utc).total_seconds()
                    print(f"    Line {line_num}: {ts_chicago.strftime('%Y-%m-%d %H:%M:%S %Z')} (elapsed: {elapsed:.1f}s)")
                except Exception as e:
                    print(f"    Line {line_num}: [ERROR parsing timestamp: {e}]")
        
        # Also check for ENGINE_START
        start_events = []
        for i, line in enumerate(lines):
            if line.strip():
                try:
                    event = json.loads(line.strip())
                    if event.get('event_type') == 'ENGINE_START':
                        start_events.append((i+1, event))
                except:
                    continue
        
        print(f"  ENGINE_START events: {len(start_events)}")
        if start_events:
            latest_start = start_events[-1]
            ts_utc_str = latest_start[1].get('timestamp_utc', latest_start[1].get('ts_utc', ''))
            try:
                ts_utc = datetime.fromisoformat(ts_utc_str.replace('Z', '+00:00'))
                if ts_utc.tzinfo is None:
                    ts_utc = ts_utc.replace(tzinfo=timezone.utc)
                ts_chicago = ts_utc.astimezone(CHICAGO_TZ)
                print(f"    Most recent: Line {latest_start[0]}, {ts_chicago.strftime('%Y-%m-%d %H:%M:%S %Z')}")
            except:
                pass
        
        # Check for timer-related messages
        timer_msgs = []
        for i, line in enumerate(lines[-1000:]):  # Last 1000 lines
            if line.strip():
                try:
                    event = json.loads(line.strip())
                    event_str = str(event).lower()
                    if 'timer' in event_str or 'tick timer' in event_str:
                        timer_msgs.append((len(lines)-1000+i+1, event))
                except:
                    continue
        
        if timer_msgs:
            print(f"  Timer-related messages: {len(timer_msgs)}")
            for line_num, event in timer_msgs[-5:]:
                et = event.get('event_type', '')
                ts = event.get('timestamp_chicago', event.get('ts_chicago', event.get('timestamp_utc', '')))[:19]
                print(f"    Line {line_num}: {ts} {et}")
        
    except Exception as e:
        print(f"  [ERROR reading file: {e}]")

print(f"\n{'='*80}")
print("SUMMARY")
print(f"{'='*80}")
print(f"\nTotal heartbeats found across all files: {len(all_heartbeats)}")

if all_heartbeats:
    print("\n✅ HEARTBEATS FOUND!")
    print("\nAll heartbeat locations:")
    for filename, line_num, event in all_heartbeats[-10:]:  # Last 10
        ts_utc_str = event.get('timestamp_utc', event.get('ts_utc', ''))
        try:
            ts_utc = datetime.fromisoformat(ts_utc_str.replace('Z', '+00:00'))
            if ts_utc.tzinfo is None:
                ts_utc = ts_utc.replace(tzinfo=timezone.utc)
            ts_chicago = ts_utc.astimezone(CHICAGO_TZ)
            now = datetime.now(timezone.utc)
            elapsed = (now - ts_utc).total_seconds()
            print(f"  {filename}:{line_num} - {ts_chicago.strftime('%Y-%m-%d %H:%M:%S %Z')} (elapsed: {elapsed:.1f}s)")
        except:
            print(f"  {filename}:{line_num} - [ERROR parsing timestamp]")
    
    # Most recent heartbeat
    latest = all_heartbeats[-1]
    ts_utc_str = latest[2].get('timestamp_utc', latest[2].get('ts_utc', ''))
    try:
        ts_utc = datetime.fromisoformat(ts_utc_str.replace('Z', '+00:00'))
        if ts_utc.tzinfo is None:
            ts_utc = ts_utc.replace(tzinfo=timezone.utc)
        ts_chicago = ts_utc.astimezone(CHICAGO_TZ)
        now = datetime.now(timezone.utc)
        elapsed = (now - ts_utc).total_seconds()
        print(f"\n  Most recent heartbeat:")
        print(f"    File: {latest[0]}")
        print(f"    Line: {latest[1]}")
        print(f"    Time: {ts_chicago.strftime('%Y-%m-%d %H:%M:%S %Z')}")
        print(f"    Elapsed: {elapsed:.1f} seconds ({elapsed/60:.1f} minutes)")
    except Exception as e:
        print(f"\n  [ERROR parsing latest heartbeat timestamp: {e}]")
else:
    print("\n❌ NO HEARTBEATS FOUND IN ANY LOG FILE")
    print("\nThis confirms that Tick() is not being called or heartbeats are not being emitted.")

print("\n" + "=" * 80)
