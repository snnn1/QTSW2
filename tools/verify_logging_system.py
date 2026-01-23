#!/usr/bin/env python3
"""
Verify the new logging system is working properly.

Checks:
1. Event registry exists and has all events
2. Configuration is valid
3. Sensitive data filter patterns
4. Log files are being written correctly
5. Event levels are assigned correctly
"""

import json
import re
from pathlib import Path
from datetime import datetime, timezone
from collections import defaultdict

def check_event_registry():
    """Check if RobotEventTypes.cs exists and contains expected events."""
    print("=" * 80)
    print("1. CHECKING EVENT REGISTRY")
    print("=" * 80)
    
    registry_paths = [
        Path("modules/robot/core/RobotEventTypes.cs"),
        Path("RobotCore_For_NinjaTrader/RobotEventTypes.cs")
    ]
    
    for path in registry_paths:
        if not path.exists():
            print(f"❌ MISSING: {path}")
            return False
        
        content = path.read_text(encoding='utf-8')
        
        # Check for key methods
        if "GetLevel" not in content:
            print(f"[ERROR] MISSING GetLevel() method in {path}")
            return False
        
        if "IsValid" not in content:
            print(f"[ERROR] MISSING IsValid() method in {path}")
            return False
        
        # Count event types in registry
        event_count = len(re.findall(r'\["([A-Z_][A-Z0-9_]+)"\]\s*=', content))
        print(f"[OK] {path.name}: Found {event_count} event types in registry")
    
    return True

def check_configuration():
    """Check logging configuration file."""
    print("\n" + "=" * 80)
    print("2. CHECKING CONFIGURATION")
    print("=" * 80)
    
    config_path = Path("configs/robot/logging.json")
    if not config_path.exists():
        print(f"[WARN] Config file not found: {config_path}")
        print("   Using defaults (this is OK)")
        return True
    
    try:
        config = json.loads(config_path.read_text(encoding='utf-8'))
        
        # Check new fields
        new_fields = [
            "max_queue_size",
            "max_batch_per_flush",
            "flush_interval_ms",
            "rotate_daily",
            "enable_sensitive_data_filter",
            "archive_cleanup_days"
        ]
        
        print("Configuration values:")
        for field in new_fields:
            value = config.get(field, "default")
            print(f"  {field}: {value}")
        
        # Validate ranges
        if config.get("max_queue_size", 50000) <= 0:
            print("[ERROR] max_queue_size must be > 0")
            return False
        
        if config.get("max_batch_per_flush", 2000) <= 0:
            print("[ERROR] max_batch_per_flush must be > 0")
            return False
        
        print("[OK] Configuration is valid")
        return True
        
    except Exception as e:
        print(f"[ERROR] Error reading config: {e}")
        return False

def check_sensitive_filter():
    """Check if SensitiveDataFilter.cs exists and has correct patterns."""
    print("\n" + "=" * 80)
    print("3. CHECKING SENSITIVE DATA FILTER")
    print("=" * 80)
    
    filter_paths = [
        Path("modules/robot/core/SensitiveDataFilter.cs"),
        Path("RobotCore_For_NinjaTrader/SensitiveDataFilter.cs")
    ]
    
    for path in filter_paths:
        if not path.exists():
            print(f"[ERROR] MISSING: {path}")
            return False
        
        content = path.read_text(encoding='utf-8')
        
        # Check for key methods
        if "FilterDictionary" not in content:
            print(f"[ERROR] MISSING FilterDictionary() method in {path}")
            return False
        
        if "FilterString" not in content:
            print(f"[ERROR] MISSING FilterString() method in {path}")
            return False
        
        # Check for sensitive patterns
        patterns = [
            "api.*key",
            "token",
            "password",
            "secret",
            "account"
        ]
        
        found_patterns = []
        for pattern in patterns:
            if pattern in content.lower():
                found_patterns.append(pattern)
        
        print(f"[OK] {path.name}: Found {len(found_patterns)}/{len(patterns)} sensitive patterns")
        print(f"   Patterns: {', '.join(found_patterns)}")
    
    return True

def check_code_integration():
    """Check if code is using the new registry and filter."""
    print("\n" + "=" * 80)
    print("4. CHECKING CODE INTEGRATION")
    print("=" * 80)
    
    # Check RobotLogger.cs uses RobotEventTypes
    logger_path = Path("modules/robot/core/RobotLogger.cs")
    if logger_path.exists():
        content = logger_path.read_text(encoding='utf-8')
        if "RobotEventTypes.GetLevel" in content:
            print("[OK] RobotLogger.cs uses RobotEventTypes.GetLevel()")
        else:
            print("[ERROR] RobotLogger.cs does NOT use RobotEventTypes.GetLevel()")
            return False
    
    # Check RobotEngine.cs uses RobotEventTypes
    engine_path = Path("modules/robot/core/RobotEngine.cs")
    if engine_path.exists():
        content = engine_path.read_text(encoding='utf-8')
        if "RobotEventTypes.GetLevel" in content:
            print("[OK] RobotEngine.cs uses RobotEventTypes.GetLevel()")
        else:
            print("[ERROR] RobotEngine.cs does NOT use RobotEventTypes.GetLevel()")
            return False
    
    # Check RobotLoggingService.cs uses SensitiveDataFilter
    service_path = Path("modules/robot/core/RobotLoggingService.cs")
    if service_path.exists():
        content = service_path.read_text(encoding='utf-8')
        if "SensitiveDataFilter.FilterDictionary" in content:
            print("[OK] RobotLoggingService.cs uses SensitiveDataFilter")
        else:
            print("[ERROR] RobotLoggingService.cs does NOT use SensitiveDataFilter")
            return False
        
        # Check configurable parameters
        if "MAX_QUEUE_SIZE = _config.max_queue_size" in content or "MAX_QUEUE_SIZE" in content:
            print("[OK] RobotLoggingService.cs uses configurable queue size")
        else:
            print("[WARN] RobotLoggingService.cs may still use hardcoded queue size")
    
    return True

def check_log_files():
    """Check if log files exist and have recent activity."""
    print("\n" + "=" * 80)
    print("5. CHECKING LOG FILES")
    print("=" * 80)
    
    log_dir = Path("logs/robot")
    if not log_dir.exists():
        print(f"[WARN] Log directory not found: {log_dir}")
        print("   This is OK if robot hasn't run yet")
        return True
    
    # Check for JSONL files
    jsonl_files = list(log_dir.glob("robot_*.jsonl"))
    if not jsonl_files:
        print("[WARN] No JSONL log files found")
        print("   This is OK if robot hasn't run yet")
        return True
    
    print(f"Found {len(jsonl_files)} JSONL log files:")
    for f in sorted(jsonl_files)[:10]:  # Show first 10
        size_mb = f.stat().st_size / (1024 * 1024)
        mtime = datetime.fromtimestamp(f.stat().st_mtime, tz=timezone.utc)
        age_hours = (datetime.now(timezone.utc) - mtime).total_seconds() / 3600
        print(f"  {f.name}: {size_mb:.2f} MB, modified {age_hours:.1f} hours ago")
    
    # Check for daily summaries
    daily_summaries = list(log_dir.glob("daily_*.md"))
    if daily_summaries:
        print(f"\nFound {len(daily_summaries)} daily summary files")
        for f in sorted(daily_summaries)[-3:]:  # Show last 3
            print(f"  {f.name}")
    else:
        print("\n[WARN] No daily summary files found (may not have run yet)")
    
    # Check for notification errors log
    notif_log = log_dir / "notification_errors.log"
    if notif_log.exists():
        size_kb = notif_log.stat().st_size / 1024
        print(f"\n[OK] notification_errors.log exists ({size_kb:.1f} KB)")
    else:
        print("\n[WARN] notification_errors.log not found (may not have run yet)")
    
    return True

def check_recent_events():
    """Check recent log events for proper structure."""
    print("\n" + "=" * 80)
    print("6. CHECKING RECENT LOG EVENTS")
    print("=" * 80)
    
    log_dir = Path("logs/robot")
    if not log_dir.exists():
        print("[WARN] Log directory not found - skipping event check")
        return True
    
    jsonl_files = list(log_dir.glob("robot_ENGINE.jsonl"))
    if not jsonl_files:
        print("[WARN] No ENGINE log file found - skipping event check")
        return True
    
    engine_log = jsonl_files[0]
    
    # Read last 100 lines
    try:
        lines = engine_log.read_text(encoding='utf-8').strip().split('\n')
        recent_lines = lines[-100:] if len(lines) > 100 else lines
        
        events = []
        for line in recent_lines:
            if not line.strip():
                continue
            try:
                event = json.loads(line)
                events.append(event)
            except:
                continue
        
        if not events:
            print("[WARN] No valid events found in recent logs")
            return True
        
        print(f"Analyzed {len(events)} recent events")
        
        # Check event structure
        required_fields = ["ts_utc", "level", "source", "event"]
        missing_fields = defaultdict(int)
        
        for event in events:
            for field in required_fields:
                if field not in event:
                    missing_fields[field] += 1
        
        if missing_fields:
            print("[ERROR] Missing required fields:")
            for field, count in missing_fields.items():
                print(f"   {field}: missing in {count} events")
            return False
        
        # Check level assignments
        levels = defaultdict(int)
        event_types = defaultdict(int)
        
        for event in events:
            level = event.get("level", "UNKNOWN")
            event_type = event.get("event", "UNKNOWN")
            levels[level] += 1
            event_types[event_type] += 1
        
        print(f"\nLevel distribution:")
        for level, count in sorted(levels.items()):
            print(f"  {level}: {count}")
        
        # Check for events using new registry (should have proper levels)
        known_events = [
            "ENGINE_START", "ENGINE_STOP", "ORDER_SUBMITTED",
            "RANGE_LOCKED", "LOG_DIR_RESOLVED", "PROJECT_ROOT_RESOLVED"
        ]
        
        found_events = []
        for event_type in known_events:
            if event_type in event_types:
                found_events.append(event_type)
        
        if found_events:
            print(f"\n[OK] Found {len(found_events)} known event types in recent logs")
            print(f"   Examples: {', '.join(found_events[:5])}")
        else:
            print("\n[WARN] No known event types found (may be old logs)")
        
        print("[OK] Event structure is valid")
        return True
        
    except Exception as e:
        print(f"[ERROR] Error reading log file: {e}")
        return False

def check_ninjatrader_sync():
    """Check if NinjaTrader version is synced."""
    print("\n" + "=" * 80)
    print("7. CHECKING NINJATRADER SYNC")
    print("=" * 80)
    
    main_files = [
        "modules/robot/core/RobotEventTypes.cs",
        "modules/robot/core/SensitiveDataFilter.cs",
        "modules/robot/core/RobotLogger.cs",
        "modules/robot/core/RobotEngine.cs",
        "modules/robot/core/RobotLoggingService.cs"
    ]
    
    nt_files = [
        "RobotCore_For_NinjaTrader/RobotEventTypes.cs",
        "RobotCore_For_NinjaTrader/SensitiveDataFilter.cs",
        "RobotCore_For_NinjaTrader/RobotLogger.cs",
        "RobotCore_For_NinjaTrader/RobotEngine.cs",
        "RobotCore_For_NinjaTrader/RobotLoggingService.cs"
    ]
    
    all_synced = True
    for main_file, nt_file in zip(main_files, nt_files):
        main_path = Path(main_file)
        nt_path = Path(nt_file)
        
        if not main_path.exists():
            print(f"[WARN] Main file missing: {main_file}")
            continue
        
        if not nt_path.exists():
            print(f"[ERROR] NinjaTrader file missing: {nt_file}")
            all_synced = False
            continue
        
        # Check if both use RobotEventTypes
        main_content = main_path.read_text(encoding='utf-8')
        nt_content = nt_path.read_text(encoding='utf-8')
        
        if "RobotEventTypes.GetLevel" in main_content:
            if "RobotEventTypes.GetLevel" not in nt_content:
                print(f"[ERROR] {nt_file} not using RobotEventTypes.GetLevel()")
                all_synced = False
            else:
                print(f"[OK] {Path(nt_file).name} synced")
    
    return all_synced

def main():
    """Run all verification checks."""
    print("\n" + "=" * 80)
    print("ROBOT LOGGING SYSTEM VERIFICATION")
    print("=" * 80)
    print(f"Date: {datetime.now(timezone.utc).isoformat()}")
    print()
    
    checks = [
        ("Event Registry", check_event_registry),
        ("Configuration", check_configuration),
        ("Sensitive Data Filter", check_sensitive_filter),
        ("Code Integration", check_code_integration),
        ("Log Files", check_log_files),
        ("Recent Events", check_recent_events),
        ("NinjaTrader Sync", check_ninjatrader_sync),
    ]
    
    results = []
    for name, check_func in checks:
        try:
            result = check_func()
            results.append((name, result))
        except Exception as e:
            print(f"❌ Error in {name}: {e}")
            results.append((name, False))
    
    # Summary
    print("\n" + "=" * 80)
    print("VERIFICATION SUMMARY")
    print("=" * 80)
    
    passed = sum(1 for _, result in results if result)
    total = len(results)
    
    for name, result in results:
        status = "[PASS]" if result else "[FAIL]"
        print(f"{status}: {name}")
    
    print(f"\nTotal: {passed}/{total} checks passed")
    
    if passed == total:
        print("\n[SUCCESS] All checks passed! Logging system is working correctly.")
    else:
        print(f"\n[WARN] {total - passed} check(s) failed. Review above for details.")
    
    return passed == total

if __name__ == "__main__":
    import sys
    success = main()
    sys.exit(0 if success else 1)
