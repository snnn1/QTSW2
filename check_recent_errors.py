"""Check recent ERROR messages in robot_ENGINE.jsonl"""
import json
from pathlib import Path
from datetime import datetime, timezone
import pytz

CHICAGO_TZ = pytz.timezone("America/Chicago")
log_file = Path('logs/robot/robot_ENGINE.jsonl')

print("=" * 80)
print("RECENT ERROR MESSAGES IN robot_ENGINE.jsonl")
print("=" * 80)

if not log_file.exists():
    print("Log file not found")
    exit(1)

with open(log_file, 'r', encoding='utf-8') as f:
    lines = f.readlines()

print(f"\n[INFO] Checking last 100 lines of {log_file.name}")

recent_lines = lines[-100:] if len(lines) > 100 else lines

errors = []
for i, line in enumerate(recent_lines):
    if line.strip():
        try:
            event = json.loads(line.strip())
            level = event.get('level', '')
            if level == 'ERROR':
                line_num = len(lines) - len(recent_lines) + i + 1
                errors.append((line_num, event))
        except:
            continue

print(f"\n[INFO] Found {len(errors)} ERROR messages in last 100 lines")

if errors:
    print("\n  Recent errors:")
    for line_num, event in errors[-20:]:  # Last 20 errors
        ts_utc_str = event.get('ts_utc', '')
        try:
            ts_utc = datetime.fromisoformat(ts_utc_str.replace('Z', '+00:00'))
            if ts_utc.tzinfo is None:
                ts_utc = ts_utc.replace(tzinfo=timezone.utc)
            ts_chicago = ts_utc.astimezone(CHICAGO_TZ)
        except:
            ts_chicago = ts_utc_str[:19]
        
        event_type = event.get('event_type', '')
        message = event.get('message', '')
        data = event.get('data', {})
        
        print(f"\n    Line {line_num}: {ts_chicago.strftime('%Y-%m-%d %H:%M:%S %Z')}")
        print(f"      Event Type: {event_type}")
        if message:
            print(f"      Message: {message[:200]}")
        if data:
            data_str = str(data)[:200]
            print(f"      Data: {data_str}")
else:
    print("\n  No ERROR messages found in recent lines")

# Also check for WARN messages
warnings = []
for i, line in enumerate(recent_lines):
    if line.strip():
        try:
            event = json.loads(line.strip())
            level = event.get('level', '')
            if level == 'WARN':
                line_num = len(lines) - len(recent_lines) + i + 1
                warnings.append((line_num, event))
        except:
            continue

print(f"\n[INFO] Found {len(warnings)} WARN messages in last 100 lines")
if warnings:
    print("\n  Recent warnings:")
    for line_num, event in warnings[-10:]:  # Last 10 warnings
        ts_utc_str = event.get('ts_utc', '')
        try:
            ts_utc = datetime.fromisoformat(ts_utc_str.replace('Z', '+00:00'))
            if ts_utc.tzinfo is None:
                ts_utc = ts_utc.replace(tzinfo=timezone.utc)
            ts_chicago = ts_utc.astimezone(CHICAGO_TZ)
        except:
            ts_chicago = ts_utc_str[:19]
        
        event_type = event.get('event_type', '')
        message = event.get('message', '')
        print(f"    Line {line_num}: {ts_chicago.strftime('%H:%M:%S')} {event_type}")
        if message:
            print(f"      {message[:150]}")

print("\n" + "=" * 80)
