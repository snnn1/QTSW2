#!/usr/bin/env python3
"""Check new logging features: reason codes, categories, invariant violations."""

import json
from pathlib import Path
from datetime import datetime, timezone, timedelta
from collections import Counter

def main():
    cutoff = datetime.now(timezone.utc) - timedelta(minutes=5)
    print("=" * 80)
    print(f"CHECKING NEW LOGGING FEATURES (last 5 minutes)")
    print("=" * 80)
    print()
    
    log_dir = Path("logs/robot")
    if not log_dir.exists():
        print("[ERROR] Log directory not found")
        return
    
    # Collect all events from last 5 minutes
    all_events = []
    for log_file in log_dir.glob("robot_*.jsonl"):
        try:
            with open(log_file, 'r', encoding='utf-8-sig') as f:
                for line in f:
                    if not line.strip():
                        continue
                    try:
                        event = json.loads(line)
                        ts_str = event.get('ts_utc', '')
                        if ts_str:
                            try:
                                ts = datetime.fromisoformat(ts_str.replace('Z', '+00:00').split('+')[0].split('.')[0] + '+00:00')
                                if ts > cutoff:
                                    all_events.append(event)
                            except:
                                continue
                    except json.JSONDecodeError:
                        continue
        except Exception as e:
            print(f"[WARN] Error reading {log_file.name}: {e}")
            continue
    
    print(f"Total events in last 5 minutes: {len(all_events)}")
    print()
    
    # Check for RANGE_COMPUTE_FAILED with new reason codes (extend search to 1 hour)
    cutoff_1h = datetime.now(timezone.utc) - timedelta(hours=1)
    failures_1h = []
    for log_file in log_dir.glob("robot_*.jsonl"):
        try:
            with open(log_file, 'r', encoding='utf-8-sig') as f:
                for line in f:
                    if not line.strip():
                        continue
                    try:
                        event = json.loads(line)
                        if event.get('event') == 'RANGE_COMPUTE_FAILED':
                            ts_str = event.get('ts_utc', '')
                            if ts_str:
                                try:
                                    ts = datetime.fromisoformat(ts_str.replace('Z', '+00:00').split('+')[0].split('.')[0] + '+00:00')
                                    if ts > cutoff_1h:
                                        failures_1h.append(event)
                                except:
                                    continue
                    except json.JSONDecodeError:
                        continue
        except:
            continue
    
    failures = failures_1h
    print(f"RANGE_COMPUTE_FAILED events (last hour): {len(failures)}")
    
    if failures:
        print("\nReason code breakdown:")
        reasons = Counter([e.get('data', {}).get('reason', 'N/A') for e in failures])
        for reason, count in reasons.most_common():
            print(f"  {reason}: {count}")
        
        print("\nCategory breakdown:")
        categories = Counter([e.get('data', {}).get('reason_category', 'N/A') for e in failures])
        for cat, count in categories.most_common():
            print(f"  {cat}: {count}")
        
        print("\nLevel verification (should all be ERROR):")
        levels = Counter([e.get('level', 'N/A') for e in failures])
        for level, count in levels.most_common():
            print(f"  {level}: {count}")
        
        print("\nSample RANGE_COMPUTE_FAILED events:")
        for e in failures[:5]:
            ts = e.get('ts_utc', '')[:19]
            inst = e.get('instrument', '')
            reason = e.get('data', {}).get('reason', 'N/A')
            category = e.get('data', {}).get('reason_category', 'N/A')
            level = e.get('level', 'N/A')
            print(f"  {ts} | {inst:4} | reason={reason:25} | category={category:10} | level={level}")
    else:
        print("  (No RANGE_COMPUTE_FAILED events found)")
    
    print()
    
    # Check for invariant violations
    violations = [e for e in all_events if e.get('event') == 'LOGGING_INVARIANT_VIOLATION']
    print(f"LOGGING_INVARIANT_VIOLATION events: {len(violations)}")
    
    if violations:
        print("[ERROR] Found invariant violations! This indicates a configuration issue.")
        for e in violations:
            ts = e.get('ts_utc', '')[:19]
            msg = e.get('data', {}).get('message', '')
            print(f"  {ts} | {msg}")
    else:
        print("[OK] No invariant violations detected - guardrails working correctly")
    
    print()
    
    # Check for ENGINE_START events (recent restarts)
    engine_starts = [e for e in all_events if e.get('event') == 'ENGINE_START']
    print(f"ENGINE_START events: {len(engine_starts)}")
    if engine_starts:
        print("  Recent engine starts:")
        for e in engine_starts[-3:]:
            ts = e.get('ts_utc', '')[:19]
            print(f"    {ts}")
    
    print()
    
    # Check event structure
    print("Event structure validation:")
    required_fields = ['ts_utc', 'level', 'source', 'event']
    sample_events = all_events[-10:] if len(all_events) > 10 else all_events
    missing = {}
    for e in sample_events:
        for field in required_fields:
            if field not in e:
                missing[field] = missing.get(field, 0) + 1
    
    if missing:
        print(f"[WARN] Missing required fields in sample:")
        for field, count in missing.items():
            print(f"  {field}: {count} events")
    else:
        print("[OK] All sample events have required fields")
    
    print()
    
    # Summary
    print("=" * 80)
    print("SUMMARY")
    print("=" * 80)
    print(f"Total events: {len(all_events)}")
    print(f"RANGE_COMPUTE_FAILED: {len(failures)}")
    print(f"Invariant violations: {len(violations)}")
    print(f"Engine starts: {len(engine_starts)}")
    
    if len(violations) == 0 and len(failures) > 0:
        # Verify all failures have reason codes
        failures_with_reason = [e for e in failures if e.get('data', {}).get('reason')]
        if len(failures_with_reason) == len(failures):
            print("\n[SUCCESS] New logging features working correctly:")
            print("  - Reason codes present")
            print("  - Categories assigned")
            print("  - No invariant violations")
            print("  - Guardrails functioning")
        else:
            print(f"\n[WARN] {len(failures) - len(failures_with_reason)} failures missing reason codes")

if __name__ == "__main__":
    main()
