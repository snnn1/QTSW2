#!/usr/bin/env python3
"""Check NG1 gap tolerance violation details"""
import json
from pathlib import Path

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
ng1_events.sort(key=lambda x: x.get('ts_utc', ''))

print("="*80)
print("NG1 GAP TOLERANCE VIOLATION DETAILS")
print("="*80)

# Gap tolerance thresholds
print("\nGap Tolerance Thresholds:")
print("  MAX_SINGLE_GAP_MINUTES: 3.0")
print("  MAX_TOTAL_GAP_MINUTES: 6.0")
print("  MAX_GAP_LAST_10_MINUTES: 2.0")
print("\nNote: Only DATA_FEED_FAILURE gaps count toward invalidation")
print("      LOW_LIQUIDITY gaps never invalidate the range")

# Find the first GAP_TOLERANCE_VIOLATION that caused invalidation
violations = [e for e in ng1_events if 'GAP_TOLERANCE_VIOLATION' in e.get('event', '')]
if violations:
    print(f"\nTotal GAP_TOLERANCE_VIOLATION events: {len(violations)}")
    
    # Find the one that triggered invalidation (around 16:42:29-30)
    critical_violations = [v for v in violations 
                          if v.get('ts_utc', '') >= '2026-01-26T16:42:25' and 
                          v.get('ts_utc', '') <= '2026-01-26T16:42:35']
    
    if critical_violations:
        print(f"\nCritical violations around commit time (16:42:30): {len(critical_violations)}")
        
        # Show first few to see the pattern
        for v in critical_violations[:5]:
            print(f"\n  {v.get('ts_utc', '')[:19]} - {v.get('event', 'N/A')}")
            data = v.get('data', {})
            if isinstance(data, dict):
                print(f"    Violation reason: {data.get('violation_reason', 'N/A')}")
                print(f"    Gap type: {data.get('gap_type', 'N/A')}")
                print(f"    Gap type note: {data.get('gap_type_note', 'N/A')}")
                print(f"    Gap minutes (missing): {data.get('gap_minutes', 'N/A')}")
                print(f"    Gap delta minutes: {data.get('gap_delta_minutes', 'N/A')}")
                print(f"    Largest single gap: {data.get('largest_single_gap_minutes', 'N/A')} minutes")
                print(f"    Total gap: {data.get('total_gap_minutes', 'N/A')} minutes")
                
                # Check which threshold was violated
                try:
                    largest = float(data.get('largest_single_gap_minutes', 0) or 0)
                    total = float(data.get('total_gap_minutes', 0) or 0)
                    
                    violations_found = []
                    if largest > 3.0:
                        violations_found.append(f"SINGLE_GAP ({largest} > 3.0)")
                    if total > 6.0:
                        violations_found.append(f"TOTAL_GAP ({total} > 6.0)")
                    
                    if violations_found:
                        print(f"    THRESHOLD VIOLATIONS: {', '.join(violations_found)}")
                except (ValueError, TypeError):
                    pass
                
                if violations_found:
                    print(f"    THRESHOLD VIOLATIONS: {', '.join(violations_found)}")

# Find SLOT_END_SUMMARY that shows the final outcome
summaries = [e for e in ng1_events if 'SLOT_END_SUMMARY' in e.get('event', '')]
if summaries:
    print(f"\nSLOT_END_SUMMARY events: {len(summaries)}")
    for ss in summaries[-3:]:
        print(f"\n  {ss.get('ts_utc', '')[:19]} - {ss.get('event', 'N/A')}")
        data = ss.get('data', {})
        if isinstance(data, dict):
            print(f"    Outcome: {data.get('outcome', 'N/A')}")
            print(f"    Reason: {data.get('reason', 'N/A')}")
            print(f"    Range valid: {data.get('range_valid', 'N/A')}")
            print(f"    Range locked: {data.get('range_locked', 'N/A')}")

print(f"\n{'='*80}")
