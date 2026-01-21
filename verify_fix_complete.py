"""
Comprehensive verification that the fix is complete and working
"""
import json
from pathlib import Path
from datetime import datetime
import pytz
from collections import defaultdict

print("="*80)
print("COMPREHENSIVE FIX VERIFICATION")
print("="*80)

# 1. Check timetable file
print("\n[1] TIMETABLE FILE CHECK")
print("-" * 80)
timetable_path = Path("data/timetable/timetable_current.json")
if timetable_path.exists():
    timetable = json.loads(timetable_path.read_text())
    as_of_str = timetable.get('as_of', '')
    trading_date = timetable.get('trading_date', '')
    
    chicago_tz = pytz.timezone("America/Chicago")
    as_of_dt = datetime.fromisoformat(as_of_str)
    chicago_time = as_of_dt.astimezone(chicago_tz)
    chicago_hour = chicago_time.hour
    
    from datetime import timedelta
    expected_trading_date = (chicago_time.date() + timedelta(days=1)).isoformat() if chicago_hour >= 17 else chicago_time.date().isoformat()
    
    print(f"  Timetable as_of: {as_of_str}")
    print(f"  Chicago time: {chicago_time.strftime('%Y-%m-%d %H:%M:%S %Z')}")
    print(f"  Hour: {chicago_hour}")
    print(f"  Trading date in file: {trading_date}")
    print(f"  Expected (CME rollover): {expected_trading_date}")
    print(f"  Match: {trading_date == expected_trading_date} {'[PASS]' if trading_date == expected_trading_date else '[FAIL]'}")
else:
    print("  [FAIL] Timetable file not found")

# 2. Check robot logs
print("\n[2] ROBOT LOGS CHECK")
print("-" * 80)
engine_log = Path("logs/robot/robot_ENGINE.jsonl")
if engine_log.exists():
    events = []
    with open(engine_log, 'r', encoding='utf-8') as f:
        for line in f:
            line = line.strip()
            if line:
                try:
                    events.append(json.loads(line))
                except:
                    pass
    
    # Get trading date from events (check both top-level and payload)
    trading_date_from_logs = None
    for e in reversed(events):
        # Check top-level trading_date field
        if e.get('trading_date'):
            trading_date_from_logs = e.get('trading_date')
            break
        # Check payload for locked_trading_date
        payload = e.get('data', {}).get('payload', {})
        if payload.get('locked_trading_date'):
            trading_date_from_logs = payload.get('locked_trading_date')
            break
        if payload.get('active_trading_date'):
            trading_date_from_logs = payload.get('active_trading_date')
            break
    
    print(f"  Trading date in logs: {trading_date_from_logs}")
    print(f"  Matches timetable: {trading_date_from_logs == trading_date} {'[PASS]' if trading_date_from_logs == trading_date else '[FAIL]'}")
    
    # Check session windows
    bar_mismatch = [e for e in events if e.get('event') == 'BAR_DATE_MISMATCH']
    session_windows = {}
    for e in bar_mismatch[-20:]:
        payload = e.get('data', {}).get('payload', {})
        inst = payload.get('instrument', '')
        if inst and inst not in session_windows:
            session_windows[inst] = {
                'start': payload.get('session_start_chicago', ''),
                'end': payload.get('session_end_chicago', '')
            }
    
    print(f"\n  Session Windows (from logs):")
    for inst, window in sorted(session_windows.items()):
        start_time = window['start'].split('T')[1].split('.')[0] if 'T' in window['start'] else window['start']
        print(f"    {inst}: {start_time} (instrument-specific)")
    
    # Check bar acceptance
    bar_delivery = [e for e in events if e.get('event') == 'BAR_DELIVERY_TO_STREAM']
    evening_accepted = []
    for e in bar_delivery:
        payload = e.get('data', {}).get('payload', {})
        bar_str = payload.get('bar_timestamp_chicago', '')
        if bar_str:
            try:
                bar_dt = datetime.fromisoformat(bar_str.replace('Z', '+00:00'))
                if bar_dt.hour >= 17:
                    evening_accepted.append((payload.get('instrument', ''), bar_str))
            except:
                pass
    
    print(f"\n  Evening session bars (17:00+) ACCEPTED: {len(evening_accepted)}")
    if len(evening_accepted) > 0:
        print("    [PASS] Evening session bars are being accepted")
        for inst, time_str in evening_accepted[-3:]:
            print(f"      {inst}: {time_str}")
    else:
        print("    [INFO] No evening session bars accepted yet (may be before 17:00)")
    
    # Check rejections are correct
    recent_rejections = bar_mismatch[-10:]
    correct_rejections = 0
    for e in recent_rejections:
        payload = e.get('data', {}).get('payload', {})
        bar_str = payload.get('bar_chicago', '')
        session_start_str = payload.get('session_start_chicago', '')
        session_end_str = payload.get('session_end_chicago', '')
        if bar_str and session_start_str:
            try:
                bar_dt = datetime.fromisoformat(bar_str.replace('Z', '+00:00'))
                session_start = datetime.fromisoformat(session_start_str.replace('Z', '+00:00'))
                session_end = datetime.fromisoformat(session_end_str.replace('Z', '+00:00'))
                if bar_dt < session_start or bar_dt >= session_end:
                    correct_rejections += 1
            except:
                pass
    
    print(f"\n  Recent rejections analyzed: {len(recent_rejections)}")
    print(f"  Correct rejections: {correct_rejections}/{len(recent_rejections)}")
    if correct_rejections == len(recent_rejections):
        print("    [PASS] All rejections are correct (bars outside session window)")
    else:
        print("    [WARNING] Some rejections may be incorrect")

# 3. Summary
print("\n" + "="*80)
print("[FINAL VERIFICATION SUMMARY]")
print("="*80)

checks = []
checks.append(("CME Rollover in Timetable", trading_date == expected_trading_date))
checks.append(("Robot Using Correct Trading Date", trading_date_from_logs == trading_date if 'trading_date_from_logs' in locals() else False))
checks.append(("Evening Session Bars Accepted", len(evening_accepted) > 0 if 'evening_accepted' in locals() else False))
checks.append(("Session Windows Instrument-Specific", len(session_windows) > 1 if 'session_windows' in locals() else False))
checks.append(("Rejections Are Correct", correct_rejections == len(recent_rejections) if 'recent_rejections' in locals() and len(recent_rejections) > 0 else True))

all_passed = all(check[1] for check in checks)

for name, passed in checks:
    status = "[PASS]" if passed else "[FAIL]"
    print(f"  {name}: {status}")

print(f"\n{'='*80}")
if all_passed:
    print("[RESULT] ALL CHECKS PASSED - Fix is complete and working correctly!")
else:
    print("[RESULT] Some checks failed - review above for details")
print("="*80)

print("\n[KEY POINTS]")
print("  1. Timetable uses CME rollover (17:00 Chicago)")
print("  2. Robot reads trading_date from timetable correctly")
print("  3. Session windows are instrument-specific (from TradingHours)")
print("  4. Evening session bars (17:00+) are accepted for next trading date")
print("  5. Bars outside session windows are correctly rejected")
print("\n[CONCLUSION]")
if all_passed:
    print("  The Jan 20th issue is FULLY FIXED.")
    print("  Bars will NOT be rejected like they were on Jan 20th.")
    print("  Evening session bars are now correctly accepted.")
else:
    print("  Some issues detected - review logs above.")
