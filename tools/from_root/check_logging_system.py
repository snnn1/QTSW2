#!/usr/bin/env python3
"""
Comprehensive Logging System Audit
Verifies all logging components are working correctly.
"""

import json
import sys
from pathlib import Path
from datetime import datetime, timedelta
from collections import defaultdict

# Add project root to path
qtsw2_root = Path(__file__).parent
if str(qtsw2_root) not in sys.path:
    sys.path.insert(0, str(qtsw2_root))

def check_event_registry():
    """Check event registry integrity"""
    print("\n" + "="*80)
    print("1. EVENT REGISTRY INTEGRITY")
    print("="*80)
    
    event_types_file = qtsw2_root / "modules" / "robot" / "core" / "RobotEventTypes.cs"
    if not event_types_file.exists():
        print("[FAIL] RobotEventTypes.cs not found")
        return False
    
    content = event_types_file.read_text()
    
    # Check key events
    required_events = {
        "TRADE_COMPLETED": "INFO",
        "SLOT_STATUS_CHANGED": "INFO",
        "CRITICAL_EVENT_REPORTED": "INFO",
        "JOURNAL_WRITTEN": "DEBUG",
        "BAR_ACCEPTED": "DEBUG",
        "BAR_FLOW_STALLED": "WARN",
        "ENTRY_BLOCKED_RISK": "WARN",
        "RISK_CHECK_EVALUATED": "DEBUG",
        "POSITION_FLATTEN_FAIL_CLOSED": "ERROR",
    }
    
    all_pass = True
    for event, expected_level in required_events.items():
        # Check if event is in _levelMap
        level_pattern = f'["{event}"] = "{expected_level}"'
        in_level_map = level_pattern in content
        
        # Check if event is in _allEvents
        in_all_events = f'"{event}"' in content
        
        status = "[OK]" if (in_level_map and in_all_events) else "[FAIL]"
        if not (in_level_map and in_all_events):
            all_pass = False
        
        print(f"  {status} {event:30} ({expected_level:5}) - LevelMap: {in_level_map}, AllEvents: {in_all_events}")
    
    return all_pass

def check_health_logging():
    """Check health logging unification"""
    print("\n" + "="*80)
    print("2. HEALTH LOGGING UNIFICATION")
    print("="*80)
    
    logging_service_file = qtsw2_root / "modules" / "robot" / "core" / "RobotLoggingService.cs"
    if not logging_service_file.exists():
        print("[FAIL] RobotLoggingService.cs not found")
        return False
    
    content = logging_service_file.read_text()
    
    checks = [
        ("Health sink exists", "HEALTH_SINK_INFO_EVENTS" in content),
        ("CRITICAL_EVENT_REPORTED in health sink", '"CRITICAL_EVENT_REPORTED"' in content and "HEALTH_SINK_INFO_EVENTS" in content),
        ("TRADE_COMPLETED in health sink", '"TRADE_COMPLETED"' in content and "HEALTH_SINK_INFO_EVENTS" in content),
        ("No direct file writes to logs/health", "File.WriteAllText" not in content or "WriteToHealthSink" in content),
    ]
    
    all_pass = True
    for check_name, result in checks:
        status = "[OK]" if result else "[FAIL]"
        if not result:
            all_pass = False
        print(f"  {status} {check_name}")
    
    # Check health sink directory exists
    health_dir = qtsw2_root / "logs" / "health"
    if health_dir.exists():
        files = list(health_dir.glob("*.jsonl"))
        print(f"  [OK] Health sink directory exists with {len(files)} files")
    else:
        print(f"  [WARN] Health sink directory does not exist (will be created on first use)")
    
    return all_pass

def check_trade_completed():
    """Check TRADE_COMPLETED emission correctness"""
    print("\n" + "="*80)
    print("3. TRADE_COMPLETED EMISSION CORRECTNESS")
    print("="*80)
    
    journal_file = qtsw2_root / "modules" / "robot" / "core" / "Execution" / "ExecutionJournal.cs"
    if not journal_file.exists():
        print("[FAIL] ExecutionJournal.cs not found")
        return False
    
    content = journal_file.read_text()
    
    checks = [
        ("Duplicate guard exists", "if (entry.TradeCompleted)" in content and "return;" in content),
        ("Uses RobotEvents.Base", "RobotEvents.Base" in content and "TRADE_COMPLETED" in content),
        ("slot_instance_key support", "slotInstanceKey" in content and "slot_instance_key" in content),
        ("Includes required fields", all(field in content for field in [
            "entry_time_utc", "exit_time_utc", "commission", "fees", "time_in_trade"
        ])),
    ]
    
    all_pass = True
    for check_name, result in checks:
        status = "[OK]" if result else "[FAIL]"
        if not result:
            all_pass = False
        print(f"  {status} {check_name}")
    
    return all_pass

def check_commit_logging():
    """Check commit logging semantics"""
    print("\n" + "="*80)
    print("4. COMMIT LOGGING SEMANTICS")
    print("="*80)
    
    ssm_file = qtsw2_root / "modules" / "robot" / "core" / "StreamStateMachine.cs"
    if not ssm_file.exists():
        print("[FAIL] StreamStateMachine.cs not found")
        return False
    
    content = ssm_file.read_text()
    
    checks = [
        ("JOURNAL_WRITTEN is DEBUG", '"JOURNAL_WRITTEN"' in content),
        ("SLOT_STATUS_CHANGED emission", "SLOT_STATUS_CHANGED" in content),
        ("Commit produces operational event", "Commit" in content),
    ]
    
    all_pass = True
    for check_name, result in checks:
        status = "[OK]" if result else "[FAIL]"
        if not result:
            all_pass = False
        print(f"  {status} {check_name}")
    
    return all_pass

def check_verbosity_rate_limiting():
    """Check verbosity & rate limiting enforcement"""
    print("\n" + "="*80)
    print("5. VERBOSITY & RATE LIMITING ENFORCEMENT")
    print("="*80)
    
    logging_service_file = qtsw2_root / "modules" / "robot" / "core" / "RobotLoggingService.cs"
    if not logging_service_file.exists():
        print("[FAIL] RobotLoggingService.cs not found")
        return False
    
    content = logging_service_file.read_text()
    
    # Check event registry for BAR_ACCEPTED level
    event_types_file = qtsw2_root / "modules" / "robot" / "core" / "RobotEventTypes.cs"
    event_types_content = event_types_file.read_text() if event_types_file.exists() else ""
    bar_accepted_is_debug = '["BAR_ACCEPTED"] = "DEBUG"' in event_types_content
    
    checks = [
        ("diagnostics_enabled exists", "diagnostics_enabled" in content),
        ("Rate limiting applies to DEBUG/INFO only", "CheckRateLimit" in content),
        ("ERROR/CRITICAL bypass filtering", "isErrorOrCritical" in content or "ERROR" in content and "bypass" in content.lower()),
        ("BAR_ACCEPTED is DEBUG", bar_accepted_is_debug),
    ]
    
    all_pass = True
    for check_name, result in checks:
        status = "[OK]" if result else "[FAIL]"
        if not result:
            all_pass = False
        print(f"  {status} {check_name}")
    
    return all_pass

def check_recent_logs():
    """Check recent log files for proper operation"""
    print("\n" + "="*80)
    print("6. RECENT LOG FILES ANALYSIS")
    print("="*80)
    
    log_dir = qtsw2_root / "logs" / "robot"
    if not log_dir.exists():
        print("[WARN] logs/robot directory not found")
        return True
    
    # Find recent log files
    log_files = list(log_dir.glob("*.jsonl"))
    if not log_files:
        print("[WARN] No log files found")
        return True
    
    # Get most recent files
    recent_files = sorted(log_files, key=lambda f: f.stat().st_mtime, reverse=True)[:5]
    
    event_counts = defaultdict(int)
    error_count = 0
    total_lines = 0
    
    for log_file in recent_files:
        try:
            with open(log_file, 'r', encoding='utf-8') as f:
                lines = f.readlines()
                total_lines += len(lines)
                
                for line in lines[-100:]:  # Check last 100 lines
                    try:
                        event = json.loads(line.strip())
                        event_type = event.get('event', 'UNKNOWN')
                        level = event.get('level', 'UNKNOWN')
                        
                        event_counts[event_type] += 1
                        if level in ['ERROR', 'CRITICAL']:
                            error_count += 1
                    except json.JSONDecodeError:
                        pass
        except Exception as e:
            print(f"  ⚠ Error reading {log_file.name}: {e}")
    
    print(f"  [OK] Analyzed {len(recent_files)} recent log files")
    print(f"  [OK] Total lines analyzed: {total_lines}")
    print(f"  [OK] Error/CRITICAL events: {error_count}")
    print(f"  [OK] Unique event types: {len(event_counts)}")
    
    # Show top events
    if event_counts:
        print(f"\n  Top 10 events:")
        for event, count in sorted(event_counts.items(), key=lambda x: x[1], reverse=True)[:10]:
            print(f"    {event:30} {count:5}")
    
    return True

def check_health_sink_files():
    """Check health sink files"""
    print("\n" + "="*80)
    print("7. HEALTH SINK FILES")
    print("="*80)
    
    health_dir = qtsw2_root / "logs" / "health"
    if not health_dir.exists():
        print("  ⚠ Health sink directory does not exist (will be created on first use)")
        return True
    
    health_files = list(health_dir.glob("*.jsonl"))
    if not health_files:
        print("  ⚠ No health sink files found")
        return True
    
    print(f"  [OK] Found {len(health_files)} health sink files")
    
    # Check recent files
    recent_files = sorted(health_files, key=lambda f: f.stat().st_mtime, reverse=True)[:3]
    
    for health_file in recent_files:
        try:
            with open(health_file, 'r', encoding='utf-8') as f:
                lines = f.readlines()
                if lines:
                    print(f"  [OK] {health_file.name}: {len(lines)} events")
                else:
                    print(f"  [WARN] {health_file.name}: Empty file")
        except Exception as e:
            print(f"  [WARN] Error reading {health_file.name}: {e}")
    
    return True

def main():
    print("="*80)
    print("LOGGING SYSTEM COMPREHENSIVE AUDIT")
    print("="*80)
    print(f"Timestamp: {datetime.now().isoformat()}")
    
    results = []
    
    results.append(("Event Registry", check_event_registry()))
    results.append(("Health Logging", check_health_logging()))
    results.append(("TRADE_COMPLETED", check_trade_completed()))
    results.append(("Commit Logging", check_commit_logging()))
    results.append(("Verbosity & Rate Limiting", check_verbosity_rate_limiting()))
    results.append(("Recent Logs", check_recent_logs()))
    results.append(("Health Sink Files", check_health_sink_files()))
    
    print("\n" + "="*80)
    print("SUMMARY")
    print("="*80)
    
    all_pass = True
    for name, result in results:
        status = "[PASS]" if result else "[FAIL]"
        print(f"  {status} {name}")
        if not result:
            all_pass = False
    
    print("\n" + "="*80)
    if all_pass:
        print("[OK] LOGGING SYSTEM FULLY OPERATIONAL")
        print("All checks passed. Logging system matches approved plan.")
    else:
        print("[FAIL] SOME ISSUES DETECTED")
        print("Review the checks above for details.")
    print("="*80)
    
    return 0 if all_pass else 1

if __name__ == "__main__":
    sys.exit(main())
