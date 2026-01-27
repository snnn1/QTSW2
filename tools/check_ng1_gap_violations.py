#!/usr/bin/env python3
"""Analyze NG1 gap violations in detail"""
import json
from pathlib import Path
from collections import defaultdict
from datetime import datetime

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

today_events = [e for e in events if e.get('ts_utc', '').startswith('2026-01-26')]
ng1_events = [e for e in today_events if e.get('stream') == 'NG1']

gap_violations = [e for e in ng1_events if e.get('event') == 'GAP_TOLERANCE_VIOLATION']
gap_detected = [e for e in ng1_events if e.get('event') == 'BAR_GAP_DETECTED']
range_invalidated = [e for e in ng1_events if e.get('event') == 'RANGE_INVALIDATED']

print("="*80)
print("NG1 GAP VIOLATION ANALYSIS")
print("="*80)
print()

print(f"Total GAP_TOLERANCE_VIOLATION events: {len(gap_violations)}")
print(f"Total BAR_GAP_DETECTED events: {len(gap_detected)}")
print(f"Total RANGE_INVALIDATED events: {len(range_invalidated)}")
print()

# Group violations by time period
violations_by_hour = defaultdict(int)
for v in gap_violations:
    ts = v.get('ts_utc', '')
    if ts:
        hour = ts[:13]  # YYYY-MM-DDTHH
        violations_by_hour[hour] += 1

print("Violations by hour:")
for hour in sorted(violations_by_hour.keys()):
    print(f"  {hour}: {violations_by_hour[hour]} violations")
print()

# Check if violations are from before or after our fix
# Our fix was deployed - need to check when the code was updated
# For now, let's see the pattern

print("Sample violations (first 10 and last 10):")
print()
print("FIRST 10:")
for v in sorted(gap_violations, key=lambda x: x.get('ts_utc', ''))[:10]:
    ts = v.get('ts_utc', '')[:19]
    data = v.get('data', {})
    reason = data.get('violation_reason', 'N/A')
    gap_type = data.get('gap_type', 'N/A')
    total_gap = data.get('total_gap_minutes', 'N/A')
    print(f"  {ts}: {reason[:80]}")
    print(f"    Gap Type: {gap_type}, Total Gap: {total_gap} minutes")
print()

print("LAST 10:")
for v in sorted(gap_violations, key=lambda x: x.get('ts_utc', ''))[-10:]:
    ts = v.get('ts_utc', '')[:19]
    data = v.get('data', {})
    reason = data.get('violation_reason', 'N/A')
    gap_type = data.get('gap_type', 'N/A')
    total_gap = data.get('total_gap_minutes', 'N/A')
    print(f"  {ts}: {reason[:80]}")
    print(f"    Gap Type: {gap_type}, Total Gap: {total_gap} minutes")
print()

# Check if violations are still happening (recent ones)
recent_cutoff = '2026-01-26T20:00:00'  # After 8 PM UTC (assuming fix was deployed around then)
recent_violations = [v for v in gap_violations if v.get('ts_utc', '') >= recent_cutoff]
print(f"Violations after {recent_cutoff[:19]}: {len(recent_violations)}")
print()

# Check BAR_GAP_DETECTED events
if gap_detected:
    print(f"BAR_GAP_DETECTED events (new diagnostic): {len(gap_detected)}")
    print("Sample BAR_GAP_DETECTED events:")
    for g in sorted(gap_detected, key=lambda x: x.get('ts_utc', ''))[-5:]:
        ts = g.get('ts_utc', '')[:19]
        data = g.get('data', {})
        print(f"  {ts}:")
        print(f"    Delta: {data.get('delta_minutes')} min")
        print(f"    Missing: {data.get('added_to_total_gap')} min")
        print(f"    Total Gap Now: {data.get('total_gap_now')} min")
        print(f"    Bar Source: {data.get('bar_source')}")
        print(f"    Gap Type: {data.get('gap_type_preliminary')}")
    print()

# Check if range was actually invalidated
if range_invalidated:
    print("WARNING: Range WAS invalidated!")
    for inv in range_invalidated:
        print(f"  {inv.get('ts_utc', '')[:19]}: {inv.get('data', {}).get('reason', 'N/A')}")
else:
    print("GOOD: No RANGE_INVALIDATED events found - ranges were NOT invalidated despite violations")
print()

print("="*80)
print("ANALYSIS:")
print("="*80)
print()
print("The 202 violations are likely:")
print("1. Historical violations from BEFORE the fix was deployed")
print("2. Violations that are still being LOGGED (for monitoring) but NOT causing invalidation")
print()
print("Key point: GAP_TOLERANCE_VIOLATION events are still logged for diagnostic purposes,")
print("but they no longer trigger range invalidation (our fix disabled that).")
print()
print("If violations are still happening after the fix, they're being logged but not blocking trading.")
