import json
from pathlib import Path
from datetime import datetime

log_file = Path("logs/robot/robot_ENGINE.jsonl")

print("=" * 80)
print("CHECKING MOST RECENT BAR_DATE_MISMATCH (After Latest Restart)")
print("=" * 80)
print()

with open(log_file, 'r', encoding='utf-8', errors='ignore') as f:
    lines = f.readlines()

# Find the most recent TRADING_DATE_LOCKED event to know when restart happened
restart_time = None
trading_date_locked = None

for line in reversed(lines):  # Start from end
    try:
        entry = json.loads(line)
        if entry.get('event') == 'TRADING_DATE_LOCKED':
            restart_time = entry.get('ts_utc', '')
            trading_date_locked = entry.get('data', {}).get('payload', {}).get('trading_date')
            print(f"Most Recent Restart:")
            print(f"  Timestamp: {restart_time}")
            print(f"  Trading Date Locked: {trading_date_locked}")
            print(f"  Source: {entry.get('data', {}).get('payload', {}).get('source', 'N/A')}")
            print()
            break
    except:
        pass

if not restart_time:
    print("Could not find TRADING_DATE_LOCKED event")
    exit(1)

# Now check BAR_DATE_MISMATCH events AFTER the restart
print(f"Checking BAR_DATE_MISMATCH events after restart...")
print()

mismatches_after_restart = []
found_restart = False

for line in lines:
    try:
        entry = json.loads(line)
        ts = entry.get('ts_utc', '')
        
        # Mark when we've passed the restart
        if entry.get('event') == 'TRADING_DATE_LOCKED' and ts == restart_time:
            found_restart = True
            continue
        
        # Only process events after restart
        if found_restart and entry.get('event') == 'BAR_DATE_MISMATCH':
            payload = entry.get('data', {}).get('payload', {})
            mismatches_after_restart.append({
                'timestamp': ts,
                'locked_date': payload.get('locked_trading_date', ''),
                'bar_date': payload.get('bar_trading_date', ''),
                'bar_time_chicago': payload.get('bar_timestamp_chicago', ''),
                'bar_time_utc': payload.get('bar_timestamp_utc', '')
            })
    except:
        pass

print(f"Found {len(mismatches_after_restart)} BAR_DATE_MISMATCH events after restart")
print()

if mismatches_after_restart:
    # Group by bar date
    by_bar_date = {}
    for m in mismatches_after_restart:
        bar_date = m['bar_date']
        if bar_date not in by_bar_date:
            by_bar_date[bar_date] = []
        by_bar_date[bar_date].append(m)
    
    print("Mismatches by bar date:")
    for bar_date in sorted(by_bar_date.keys()):
        count = len(by_bar_date[bar_date])
        matches = "MATCHES" if bar_date == trading_date_locked else "WRONG DATE"
        print(f"  {bar_date}: {count} bars ({matches})")
        
        if bar_date == trading_date_locked:
            print(f"    *** BUG: {count} bars from locked trading date are being rejected! ***")
            print(f"    First mismatch:")
            first = by_bar_date[bar_date][0]
            print(f"      Timestamp: {first['timestamp']}")
            print(f"      Locked Date: {first['locked_date']}")
            print(f"      Bar Date: {first['bar_date']}")
            print(f"      Bar Time (Chicago): {first['bar_time_chicago']}")
    
    # Check if any bars from locked date are being rejected
    bars_from_locked = by_bar_date.get(trading_date_locked, [])
    if bars_from_locked:
        print(f"\n{'=' * 80}")
        print(f"BUG CONFIRMED: {len(bars_from_locked)} bars from {trading_date_locked} are being rejected!")
        print("=" * 80)
        print("\nThese bars should be processed, not rejected.")
        print("The date comparison logic in OnBar() is incorrectly rejecting matching dates.")
    else:
        print(f"\nOK: No bars from {trading_date_locked} are being rejected")
        print(f"All rejected bars are from other dates (expected behavior)")
else:
    print("No BAR_DATE_MISMATCH events after restart - all bars match trading date!")
