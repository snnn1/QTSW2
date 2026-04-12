"""Check for timer-related log messages"""
import json
from pathlib import Path

log_dir = Path('logs/robot')
log_file = log_dir / 'robot_ENGINE.jsonl'

print("=" * 80)
print("SEARCHING FOR TIMER-RELATED LOG MESSAGES")
print("=" * 80)

if not log_file.exists():
    print(f"Log file not found: {log_file}")
    exit(1)

# Read last 5000 lines
with open(log_file, 'r', encoding='utf-8') as f:
    lines = f.readlines()

print(f"\n[INFO] Checking last {min(5000, len(lines))} lines of {log_file.name}")

recent_lines = lines[-5000:] if len(lines) > 5000 else lines

timer_keywords = [
    'timer',
    'Tick timer',
    'tick timer',
    'StartTickTimer',
    'TickTimerCallback',
    'ENGINE_READY',
    '_engineReady',
    'engine ready',
    'Timer callback',
    'timer started',
    'timer stopped'
]

timer_matches = []

for i, line in enumerate(recent_lines):
    if line.strip():
        try:
            event = json.loads(line.strip())
            event_str = str(event).lower()
            event_type = event.get('event_type', '')
            data = event.get('data', {})
            data_str = str(data).lower()
            
            # Check if any keyword matches
            for keyword in timer_keywords:
                if keyword.lower() in event_str or keyword.lower() in event_type.lower() or keyword.lower() in data_str:
                    line_num = len(lines) - len(recent_lines) + i + 1
                    timer_matches.append((line_num, event))
                    break
        except:
            continue

print(f"\n[INFO] Found {len(timer_matches)} timer-related messages")

if timer_matches:
    print("\n  Recent timer-related messages:")
    for line_num, event in timer_matches[-20:]:  # Last 20
        ts = event.get('ts_chicago', event.get('timestamp_chicago', event.get('ts_utc', event.get('timestamp_utc', ''))))[:19]
        et = event.get('event_type', '')
        data = event.get('data', {})
        msg = str(data).replace('{', '').replace('}', '')[:100]
        print(f"    Line {line_num}: {ts} {et}")
        if msg:
            print(f"      {msg}")
else:
    print("\n  [WARNING] No timer-related messages found!")
    print("  This suggests:")
    print("    1. StartTickTimer() is not being called")
    print("    2. Log() calls in timer code are not reaching the log file")
    print("    3. Timer is not starting at all")

# Also check for any errors around the time of ENGINE_START
print("\n" + "=" * 80)
print("CHECKING FOR ERRORS AROUND ENGINE_START")
print("=" * 80)

start_events = []
for i, line in enumerate(recent_lines):
    if line.strip():
        try:
            event = json.loads(line.strip())
            if event.get('event_type') == 'ENGINE_START':
                line_num = len(lines) - len(recent_lines) + i + 1
                start_events.append((line_num, event))
        except:
            continue

if start_events:
    latest_start = start_events[-1]
    start_line = latest_start[0]
    print(f"\n[INFO] Most recent ENGINE_START at line {start_line}")
    
    # Check 50 lines after ENGINE_START for errors
    print("\n  Checking 50 lines after ENGINE_START for errors:")
    error_found = False
    for i in range(start_line, min(start_line + 50, len(lines))):
        if i < len(lines):
            line = lines[i-1]  # Line numbers are 1-indexed
            if line.strip():
                try:
                    event = json.loads(line.strip())
                    event_type = event.get('event_type', '')
                    if 'ERROR' in event_type or 'error' in str(event).lower() or 'exception' in str(event).lower():
                        ts = event.get('ts_chicago', event.get('timestamp_chicago', event.get('ts_utc', '')))[:19]
                        print(f"    Line {i}: {ts} {event_type}")
                        error_found = True
                except:
                    pass
    
    if not error_found:
        print("    No errors found immediately after ENGINE_START")
else:
    print("\n[WARNING] No ENGINE_START events found in recent lines")

print("\n" + "=" * 80)
