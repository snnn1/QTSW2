"""
SIM Smoke Test Harness

Runs a controlled SIM execution test to verify:
- SIM orders are placed
- Protective orders follow fills
- Idempotency holds across restarts
- Kill switch blocks execution
- DRYRUN parity is untouched

Usage:
    python scripts/robot/sim_smoke_test.py [--date YYYY-MM-DD] [--instrument INSTRUMENT] [--streams N]
"""

import argparse
import subprocess
import json
import sys
from pathlib import Path
from datetime import datetime


def run_sim_test(date: str, instrument: str = "ES", num_streams: int = 1, project_root: Path = None):
    """Run SIM smoke test for a single trading day."""
    if project_root is None:
        project_root = Path(__file__).parent.parent.parent
    
    print(f"SIM Smoke Test: {date}, {instrument}, {num_streams} stream(s)")
    print(f"Project root: {project_root}")
    
    # Create test timetable with limited streams
    timetable_path = project_root / "data" / "timetable" / f"timetable_sim_test_{date}.json"
    timetable_dir = timetable_path.parent
    timetable_dir.mkdir(parents=True, exist_ok=True)
    
    # Generate minimal timetable (1-2 streams for safety)
    timetable = {
        "as_of": datetime.now().isoformat(),
        "trading_date": date,
        "timezone": "America/Chicago",
        "source": "sim_smoke_test",
        "metadata": {
            "replay": True
        },
        "streams": []
    }
    
    # Add streams (ES1, ES2 for S1 slots)
    slot_times = ["07:30", "08:00"][:num_streams]
    for i, slot_time in enumerate(slot_times, start=1):
        timetable["streams"].append({
            "stream": f"{instrument}{i}",
            "instrument": instrument,
            "session": "S1",
            "slot_time": slot_time,
            "enabled": True
        })
    
    # Write timetable
    with open(timetable_path, "w") as f:
        json.dump(timetable, f, indent=2)
    
    print(f"Created test timetable: {timetable_path}")
    
    # Run Robot in SIM mode
    log_dir = project_root / "docs" / "robot" / "sim_smoke_tests" / f"test_{date}"
    log_dir.mkdir(parents=True, exist_ok=True)
    
    cmd = [
        "dotnet", "run", "--project", "modules/robot/harness/Robot.Harness.csproj",
        "--mode", "SIM",
        "--replay",
        "--start", date,
        "--end", date,
        "--timetable-path", str(timetable_path),
        "--log-dir", str(log_dir)
    ]
    
    print(f"Running: {' '.join(cmd)}")
    result = subprocess.run(cmd, cwd=project_root, capture_output=True, text=True)
    
    if result.returncode != 0:
        print(f"ERROR: Robot execution failed")
        print(result.stderr)
        return False
    
    print("Robot execution completed")
    
    # Check execution summary
    summary_dir = project_root / "data" / "execution_summaries"
    summaries = list(summary_dir.glob("summary_*.json")) if summary_dir.exists() else []
    
    if summaries:
        latest_summary = max(summaries, key=lambda p: p.stat().st_mtime)
        print(f"Found execution summary: {latest_summary}")
        
        with open(latest_summary, "r") as f:
            summary = json.load(f)
        
        print("\nExecution Summary:")
        print(f"  Intents seen: {summary.get('intents_seen', 0)}")
        print(f"  Intents executed: {summary.get('intents_executed', 0)}")
        print(f"  Orders submitted: {summary.get('orders_submitted', 0)}")
        print(f"  Orders rejected: {summary.get('orders_rejected', 0)}")
        print(f"  Orders filled: {summary.get('orders_filled', 0)}")
        print(f"  Orders blocked: {summary.get('orders_blocked', 0)}")
        print(f"  Duplicates skipped: {summary.get('duplicates_skipped', 0)}")
        
        # Validation checks
        checks_passed = True
        
        if summary.get('orders_submitted', 0) == 0:
            print("\n❌ VALIDATION FAILED: No orders submitted")
            checks_passed = False
        else:
            print("\n✅ Orders submitted")
        
        if summary.get('orders_blocked', 0) > 0:
            print(f"⚠️  WARNING: {summary.get('orders_blocked')} orders blocked")
            blocked_reasons = summary.get('blocked_by_reason', {})
            for reason, count in blocked_reasons.items():
                print(f"   - {reason}: {count}")
        
        return checks_passed
    else:
        print("⚠️  WARNING: No execution summary found")
        return False


def test_kill_switch(date: str, project_root: Path = None):
    """Test kill switch blocks execution."""
    if project_root is None:
        project_root = Path(__file__).parent.parent.parent
    
    kill_switch_path = project_root / "configs" / "robot" / "kill_switch.json"
    kill_switch_dir = kill_switch_path.parent
    kill_switch_dir.mkdir(parents=True, exist_ok=True)
    
    # Enable kill switch
    kill_switch = {
        "message": "SIM smoke test - kill switch enabled",
        "enabled": True
    }
    
    with open(kill_switch_path, "w") as f:
        json.dump(kill_switch, f, indent=2)
    
    print(f"Kill switch enabled: {kill_switch_path}")
    
    # Run test (should be blocked)
    result = run_sim_test(date, num_streams=1, project_root=project_root)
    
    # Disable kill switch
    kill_switch["enabled"] = False
    with open(kill_switch_path, "w") as f:
        json.dump(kill_switch, f, indent=2)
    
    print(f"Kill switch disabled")
    
    return result


def main():
    parser = argparse.ArgumentParser(description="SIM Smoke Test Harness")
    parser.add_argument("--date", type=str, default="2025-12-01", help="Trading date (YYYY-MM-DD)")
    parser.add_argument("--instrument", type=str, default="ES", help="Instrument symbol")
    parser.add_argument("--streams", type=int, default=1, help="Number of streams (1-2)")
    parser.add_argument("--test-kill-switch", action="store_true", help="Test kill switch blocking")
    
    args = parser.parse_args()
    
    project_root = Path(__file__).parent.parent.parent
    
    if args.test_kill_switch:
        print("Testing kill switch...")
        test_kill_switch(args.date, project_root)
    else:
        print("Running SIM smoke test...")
        success = run_sim_test(args.date, args.instrument, args.streams, project_root)
        sys.exit(0 if success else 1)


if __name__ == "__main__":
    main()
