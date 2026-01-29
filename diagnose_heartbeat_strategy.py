#!/usr/bin/env python3
"""
Diagnose HeartbeatStrategy issues - comprehensive check

This script checks all potential issues that could prevent ENGINE_HEARTBEAT
events from appearing in the logs.
"""

import json
import os
from pathlib import Path
from datetime import datetime, timezone
import pytz

CHICAGO_TZ = pytz.timezone("America/Chicago")
ROBOT_LOGS_DIR = Path("logs/robot")
ENGINE_LOG_FILE = ROBOT_LOGS_DIR / "robot_ENGINE.jsonl"

def check_file_exists():
    """Check if robot_ENGINE.jsonl exists."""
    print("=" * 80)
    print("1. CHECKING LOG FILE EXISTS")
    print("=" * 80)
    
    if ENGINE_LOG_FILE.exists():
        mtime = datetime.fromtimestamp(ENGINE_LOG_FILE.stat().st_mtime, tz=timezone.utc)
        now = datetime.now(timezone.utc)
        age = (now - mtime).total_seconds()
        size = ENGINE_LOG_FILE.stat().st_size
        
        print(f"[OK] File exists: {ENGINE_LOG_FILE}")
        print(f"     Size: {size:,} bytes")
        print(f"     Last modified: {mtime.isoformat()} ({age:.1f}s ago)")
        
        if age > 300:
            print(f"     [!] WARNING: File hasn't been modified in {age:.0f} seconds")
            print(f"         Robot may not be running")
        return True
    else:
        print(f"[X] File not found: {ENGINE_LOG_FILE}")
        print(f"    Possible issues:")
        print(f"    - RobotLogger not initialized")
        print(f"    - Log directory doesn't exist")
        print(f"    - Project root resolution failed")
        return False

def check_log_directory():
    """Check if log directory exists and is writable."""
    print()
    print("=" * 80)
    print("2. CHECKING LOG DIRECTORY")
    print("=" * 80)
    
    if not ROBOT_LOGS_DIR.exists():
        print(f"[X] Log directory doesn't exist: {ROBOT_LOGS_DIR}")
        print(f"    RobotLogger will create it, but this suggests project root may be wrong")
        return False
    
    print(f"[OK] Log directory exists: {ROBOT_LOGS_DIR}")
    
    # Check if writable
    try:
        test_file = ROBOT_LOGS_DIR / ".test_write"
        test_file.write_text("test")
        test_file.unlink()
        print(f"[OK] Directory is writable")
    except Exception as e:
        print(f"[X] Directory is NOT writable: {e}")
        return False
    
    # List log files
    log_files = list(ROBOT_LOGS_DIR.glob("robot_*.jsonl"))
    print(f"[INFO] Found {len(log_files)} robot log files:")
    for log_file in sorted(log_files)[:10]:  # Show first 10
        size = log_file.stat().st_size
        mtime = datetime.fromtimestamp(log_file.stat().st_mtime, tz=timezone.utc)
        age = (datetime.now(timezone.utc) - mtime).total_seconds()
        print(f"       {log_file.name} ({size:,} bytes, {age:.0f}s ago)")
    
    return True

def check_project_root():
    """Check project root resolution."""
    print()
    print("=" * 80)
    print("3. CHECKING PROJECT ROOT RESOLUTION")
    print("=" * 80)
    
    # Check environment variable
    env_root = os.environ.get("QTSW2_PROJECT_ROOT")
    if env_root:
        print(f"[INFO] QTSW2_PROJECT_ROOT environment variable is set:")
        print(f"       {env_root}")
        if Path(env_root).exists():
            print(f"[OK] Path exists")
            config_file = Path(env_root) / "configs" / "analyzer_robot_parity.json"
            if config_file.exists():
                print(f"[OK] Config file found: {config_file}")
            else:
                print(f"[X] Config file NOT found: {config_file}")
                print(f"    ProjectRootResolver.ResolveProjectRoot() will fail")
                return False
        else:
            print(f"[X] Path does NOT exist")
            print(f"    ProjectRootResolver.ResolveProjectRoot() will fail")
            return False
    else:
        print(f"[INFO] QTSW2_PROJECT_ROOT not set - will use directory walk")
        print(f"       ProjectRootResolver will walk up from current directory")
        
        # Check if we can find config file
        current_dir = Path.cwd()
        config_file = current_dir / "configs" / "analyzer_robot_parity.json"
        if config_file.exists():
            print(f"[OK] Config file found in current directory: {config_file}")
        else:
            print(f"[!] Config file not in current directory")
            print(f"    ProjectRootResolver may fail if NinjaTrader's working directory is wrong")
    
    return True

def check_robot_logging_service():
    """Check if RobotLoggingService is working."""
    print()
    print("=" * 80)
    print("4. CHECKING ROBOT LOGGING SERVICE")
    print("=" * 80)
    
    # Check for robot_ENGINE.jsonl (created by RobotLoggingService)
    engine_logs = list(ROBOT_LOGS_DIR.glob("robot_ENGINE*.jsonl"))
    
    if engine_logs:
        print(f"[OK] Found {len(engine_logs)} ENGINE log file(s)")
        for log_file in engine_logs:
            size = log_file.stat().st_size
            mtime = datetime.fromtimestamp(log_file.stat().st_mtime, tz=timezone.utc)
            age = (datetime.now(timezone.utc) - mtime).total_seconds()
            print(f"     {log_file.name} ({size:,} bytes, {age:.0f}s ago)")
        
        # Check if file has content
        if ENGINE_LOG_FILE.exists():
            with open(ENGINE_LOG_FILE, 'r', encoding='utf-8-sig') as f:
                lines = [l for l in f if l.strip()]
                print(f"     Contains {len(lines)} log lines")
                if len(lines) > 0:
                    print(f"[OK] RobotLoggingService is writing to ENGINE log")
                    return True
                else:
                    print(f"[!] File exists but is empty")
                    return False
    else:
        print(f"[!] No robot_ENGINE*.jsonl files found")
        print(f"    RobotLoggingService may not be initialized")
        print(f"    Or HeartbeatStrategy is using fallback logger (per-instance file)")
        return False
    
    return True

def check_recent_engine_events():
    """Check what engine events are being logged."""
    print()
    print("=" * 80)
    print("5. CHECKING RECENT ENGINE EVENTS")
    print("=" * 80)
    
    if not ENGINE_LOG_FILE.exists():
        print(f"[X] Cannot check - file doesn't exist")
        return
    
    events = []
    try:
        with open(ENGINE_LOG_FILE, 'r', encoding='utf-8-sig') as f:
            for line in f:
                line = line.strip()
                if not line:
                    continue
                try:
                    event = json.loads(line)
                    event_type = event.get("event_type") or event.get("event", "")
                    stream = event.get("stream", "")
                    ts_str = event.get("ts_utc") or event.get("timestamp_utc") or event.get("timestamp")
                    
                    if stream == "__engine__":
                        ts = None
                        if ts_str:
                            try:
                                ts_str = ts_str.replace('Z', '+00:00')
                                ts = datetime.fromisoformat(ts_str)
                                if ts.tzinfo is None:
                                    ts = ts.replace(tzinfo=timezone.utc)
                            except:
                                pass
                        
                        events.append({
                            "event_type": event_type,
                            "timestamp": ts,
                            "timestamp_str": ts_str
                        })
                except:
                    continue
    except Exception as e:
        print(f"[X] Error reading file: {e}")
        return
    
    if not events:
        print(f"[!] No engine events found in file")
        print(f"    RobotLogger may not be routing ENGINE events correctly")
        return
    
    print(f"[INFO] Found {len(events)} engine events")
    
    # Group by event type
    event_types = {}
    for evt in events:
        et = evt["event_type"]
        event_types[et] = event_types.get(et, 0) + 1
    
    print(f"     Event types:")
    for et, count in sorted(event_types.items(), key=lambda x: -x[1]):
        print(f"       {et}: {count}")
    
    # Check for ENGINE_HEARTBEAT
    heartbeat_events = [e for e in events if e["event_type"] == "ENGINE_HEARTBEAT"]
    if heartbeat_events:
        print(f"[OK] Found {len(heartbeat_events)} ENGINE_HEARTBEAT events!")
        latest = heartbeat_events[-1]
        if latest["timestamp"]:
            elapsed = (datetime.now(timezone.utc) - latest["timestamp"]).total_seconds()
            print(f"     Latest: {elapsed:.1f}s ago")
    else:
        print(f"[X] No ENGINE_HEARTBEAT events found")
        print(f"     HeartbeatStrategy is not emitting events")
    
    # Show most recent events
    print()
    print(f"     Most recent events (last 10):")
    recent = sorted(events, key=lambda x: x["timestamp"] if x["timestamp"] else datetime.min.replace(tzinfo=timezone.utc))[-10:]
    for evt in reversed(recent):
        if evt["timestamp"]:
            elapsed = (datetime.now(timezone.utc) - evt["timestamp"]).total_seconds()
            chicago_time = evt["timestamp"].astimezone(CHICAGO_TZ)
            print(f"       {chicago_time.strftime('%H:%M:%S')} CT ({elapsed:.0f}s ago) - {evt['event_type']}")
        else:
            print(f"       {evt['event_type']} (timestamp unknown)")

def check_ninjatrader_strategy_file():
    """Check if strategy file exists in NinjaTrader location."""
    print()
    print("=" * 80)
    print("6. CHECKING NINJATRADER STRATEGY FILE")
    print("=" * 80)
    
    # Common NinjaTrader locations
    username = os.getenv("USERNAME", "jakej")
    possible_locations = [
        Path.home() / "Documents" / "NinjaTrader 8" / "bin" / "Custom" / "Strategies" / "HeartbeatStrategy.cs",
        Path("C:/Users") / username / "Documents" / "NinjaTrader 8" / "bin" / "Custom" / "Strategies" / "HeartbeatStrategy.cs",
        Path("C:/Users") / username / "OneDrive" / "Documents" / "NinjaTrader 8" / "bin" / "Custom" / "Strategies" / "HeartbeatStrategy.cs",
    ]
    
    found = False
    for location in possible_locations:
        if location.exists():
            print(f"[OK] Found strategy file: {location}")
            size = location.stat().st_size
            mtime = datetime.fromtimestamp(location.stat().st_mtime, tz=timezone.utc)
            print(f"     Size: {size:,} bytes")
            print(f"     Modified: {mtime.isoformat()}")
            found = True
            break
    
    if not found:
        print(f"[X] Strategy file not found in NinjaTrader Custom/Strategies folder")
        print(f"    Checked locations:")
        for loc in possible_locations:
            print(f"      {loc}")
        print()
        print(f"    ACTION REQUIRED:")
        print(f"    1. Copy modules/robot/ninjatrader/HeartbeatStrategy.cs")
        print(f"    2. To: Documents\\NinjaTrader 8\\bin\\Custom\\Strategies\\HeartbeatStrategy.cs")
    
    return found

def check_dll_reference():
    """Check if Robot.Core.dll exists."""
    print()
    print("=" * 80)
    print("7. CHECKING ROBOT.CORE.DLL")
    print("=" * 80)
    
    # Check in RobotCore_For_NinjaTrader
    dll_path = Path("RobotCore_For_NinjaTrader/bin/Release/net48/Robot.Core.dll")
    if dll_path.exists():
        size = dll_path.stat().st_size
        mtime = datetime.fromtimestamp(dll_path.stat().st_mtime, tz=timezone.utc)
        print(f"[OK] DLL found: {dll_path}")
        print(f"     Size: {size:,} bytes")
        print(f"     Modified: {mtime.isoformat()}")
        return True
    else:
        print(f"[X] DLL not found at: {dll_path}")
        print(f"    ACTION REQUIRED:")
        print(f"    1. Build Robot.Core project")
        print(f"    2. Or copy DLL to NinjaTrader Custom folder")
        return False

def main():
    """Run all diagnostic checks."""
    print("\n" + "=" * 80)
    print("HEARTBEAT STRATEGY DIAGNOSTIC")
    print("=" * 80)
    print(f"Current time: {datetime.now(timezone.utc).isoformat()}")
    print()
    
    results = {
        "log_file_exists": check_file_exists(),
        "log_directory_ok": check_log_directory(),
        "project_root_ok": check_project_root(),
        "logging_service_ok": check_robot_logging_service(),
        "strategy_file_exists": check_ninjatrader_strategy_file(),
        "dll_exists": check_dll_reference(),
    }
    
    check_recent_engine_events()
    
    print()
    print("=" * 80)
    print("DIAGNOSIS SUMMARY")
    print("=" * 80)
    
    all_ok = all(results.values())
    
    if all_ok:
        print("[OK] All basic checks passed")
        print()
        print("If ENGINE_HEARTBEAT events are still not appearing:")
        print("  1. Verify HeartbeatStrategy is enabled in NinjaTrader")
        print("  2. Check NinjaTrader Output window for errors")
        print("  3. Verify strategy is in Realtime state (not Historical)")
        print("  4. Check that Robot.Core.dll reference is added in NinjaTrader")
    else:
        print("[!] Issues found:")
        for check, passed in results.items():
            status = "[OK]" if passed else "[X]"
            print(f"  {status} {check.replace('_', ' ').title()}")
        
        print()
        print("COMMON ISSUES AND FIXES:")
        print()
        
        if not results["strategy_file_exists"]:
            print("1. STRATEGY FILE NOT IN NINJATRADER:")
            print("   -> Copy HeartbeatStrategy.cs to NinjaTrader Custom/Strategies folder")
            print()
        
        if not results["dll_exists"]:
            print("2. DLL NOT FOUND:")
            print("   -> Build Robot.Core project or copy DLL to NinjaTrader")
            print()
        
        if not results["project_root_ok"]:
            print("3. PROJECT ROOT RESOLUTION:")
            print("   -> Set QTSW2_PROJECT_ROOT environment variable")
            print("   -> Or ensure NinjaTrader's working directory is correct")
            print()
        
        if not results["logging_service_ok"]:
            print("4. LOGGING SERVICE:")
            print("   -> Check RobotLoggingService initialization")
            print("   -> Verify log directory permissions")
            print()

if __name__ == "__main__":
    main()
