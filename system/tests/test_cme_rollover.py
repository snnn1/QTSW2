"""
Test CME Trading Date Rollover Logic
"""
import json
import pytz
from datetime import datetime, timedelta
from pathlib import Path

# Test the rollover logic
chicago_tz = pytz.timezone("America/Chicago")

# Test case 1: Before 17:00
test_time_before = datetime(2026, 1, 20, 16, 59, 0)
chicago_before = chicago_tz.localize(test_time_before)
chicago_date_before = chicago_before.date()
chicago_hour_before = chicago_before.hour

if chicago_hour_before >= 17:
    trading_date_before = (chicago_date_before + timedelta(days=1)).isoformat()
else:
    trading_date_before = chicago_date_before.isoformat()

print("Test 1: Before 17:00")
print(f"  Chicago time: {chicago_before.strftime('%Y-%m-%d %H:%M:%S %Z')}")
print(f"  Hour: {chicago_hour_before}")
print(f"  Expected trading_date: {trading_date_before}")
print(f"  Should be: 2026-01-20")
print(f"  Match: {trading_date_before == '2026-01-20'}")

# Test case 2: At 17:00
test_time_at = datetime(2026, 1, 20, 17, 0, 0)
chicago_at = chicago_tz.localize(test_time_at)
chicago_date_at = chicago_at.date()
chicago_hour_at = chicago_at.hour

if chicago_hour_at >= 17:
    trading_date_at = (chicago_date_at + timedelta(days=1)).isoformat()
else:
    trading_date_at = chicago_date_at.isoformat()

print("\nTest 2: At 17:00")
print(f"  Chicago time: {chicago_at.strftime('%Y-%m-%d %H:%M:%S %Z')}")
print(f"  Hour: {chicago_hour_at}")
print(f"  Expected trading_date: {trading_date_at}")
print(f"  Should be: 2026-01-21")
print(f"  Match: {trading_date_at == '2026-01-21'}")

# Test case 3: After 17:00 (like the actual timetable)
test_time_after = datetime(2026, 1, 20, 17, 12, 5)
chicago_after = chicago_tz.localize(test_time_after)
chicago_date_after = chicago_after.date()
chicago_hour_after = chicago_after.hour

if chicago_hour_after >= 17:
    trading_date_after = (chicago_date_after + timedelta(days=1)).isoformat()
else:
    trading_date_after = chicago_date_after.isoformat()

print("\nTest 3: After 17:00 (17:12)")
print(f"  Chicago time: {chicago_after.strftime('%Y-%m-%d %H:%M:%S %Z')}")
print(f"  Hour: {chicago_hour_after}")
print(f"  Expected trading_date: {trading_date_after}")
print(f"  Should be: 2026-01-21")
print(f"  Match: {trading_date_after == '2026-01-21'}")

# Check current timetable
print("\n" + "="*60)
print("Current Timetable Analysis")
print("="*60)
timetable_path = Path("data/timetable/timetable_current.json")
if timetable_path.exists():
    timetable = json.loads(timetable_path.read_text())
    as_of_str = timetable["as_of"]
    trading_date_actual = timetable["trading_date"]
    
    # Parse as_of timestamp
    as_of_dt = datetime.fromisoformat(as_of_str)
    chicago_time = as_of_dt.astimezone(chicago_tz)
    chicago_date = chicago_time.date()
    chicago_hour = chicago_time.hour
    
    # Compute expected trading_date
    if chicago_hour >= 17:
        expected_trading_date = (chicago_date + timedelta(days=1)).isoformat()
    else:
        expected_trading_date = chicago_date.isoformat()
    
    print(f"Timetable as_of: {as_of_str}")
    print(f"Chicago time: {chicago_time.strftime('%Y-%m-%d %H:%M:%S %Z')}")
    print(f"Hour: {chicago_hour}")
    print(f"Actual trading_date: {trading_date_actual}")
    print(f"Expected trading_date: {expected_trading_date}")
    print(f"Match: {trading_date_actual == expected_trading_date}")
    
    if trading_date_actual != expected_trading_date:
        print(f"\n[ISSUE] Trading date mismatch!")
        print(f"  Timetable was written at {chicago_time.strftime('%H:%M:%S %Z')} (hour {chicago_hour})")
        print(f"  Should have trading_date={expected_trading_date}")
        print(f"  But has trading_date={trading_date_actual}")
        print(f"  This suggests the timetable was written BEFORE the fix was deployed.")
else:
    print("Timetable file not found")
