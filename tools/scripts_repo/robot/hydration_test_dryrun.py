#!/usr/bin/env python3
"""
Hydration Test Dryrun

Specialized dryrun test focused on hydration scenarios:
- Zero-bar hydration (missing CSV, empty CSV, timeout)
- Normal hydration (CSV present with bars)
- Partial hydration (CSV with some bars)
- Hard timeout scenarios

This test manipulates CSV files to create specific hydration scenarios.
"""

import subprocess
import sys
import json
import shutil
from pathlib import Path
from datetime import datetime, timedelta
from typing import List, Dict, Optional
import argparse


class HydrationTestScenario:
    """Represents a single hydration test scenario."""
    
    def __init__(self, name: str, description: str, setup_func, expected_results: Dict):
        self.name = name
        self.description = description
        self.setup_func = setup_func  # Function that sets up the scenario (modifies CSV files)
        self.expected_results = expected_results  # Expected log patterns, terminal states, etc.


def setup_normal_hydration(project_root: Path, date: str, instrument: str) -> None:
    """Setup: Normal CSV file exists with bars."""
    # Ensure CSV file exists (don't modify if already present)
    csv_path = project_root / "data" / "raw" / instrument.lower() / "1m" / date[:4] / date[5:7] / f"{instrument.upper()}_1m_{date}.csv"
    if not csv_path.exists():
        # Create a minimal valid CSV with a few bars
        csv_path.parent.mkdir(parents=True, exist_ok=True)
        with open(csv_path, 'w') as f:
            f.write("timestamp_utc,open,high,low,close\n")
            # Add a few sample bars
            base_time = datetime.fromisoformat(f"{date}T00:00:00")
            for i in range(10):
                bar_time = base_time + timedelta(minutes=i)
                f.write(f"{bar_time.isoformat()}Z,5000.00,5000.25,4999.75,5000.10\n")
    print(f"  [OK] Normal CSV exists: {csv_path}")


def setup_missing_csv(project_root: Path, date: str, instrument: str) -> None:
    """Setup: CSV file is missing."""
    csv_path = project_root / "data" / "raw" / instrument.lower() / "1m" / date[:4] / date[5:7] / f"{instrument.upper()}_1m_{date}.csv"
    backup_path = csv_path.with_suffix('.csv.backup')
    
    # Backup if exists, then remove
    if csv_path.exists():
        shutil.move(str(csv_path), str(backup_path))
        print(f"  [OK] CSV backed up and removed: {csv_path}")
    else:
        print(f"  [OK] CSV already missing: {csv_path}")


def setup_empty_csv(project_root: Path, date: str, instrument: str) -> None:
    """Setup: CSV file exists but is empty (no bars)."""
    csv_path = project_root / "data" / "raw" / instrument.lower() / "1m" / date[:4] / date[5:7] / f"{instrument.upper()}_1m_{date}.csv"
    backup_path = csv_path.with_suffix('.csv.backup')
    
    # Backup if exists
    if csv_path.exists():
        shutil.copy2(str(csv_path), str(backup_path))
    
    # Create empty CSV (header only)
    csv_path.parent.mkdir(parents=True, exist_ok=True)
    with open(csv_path, 'w') as f:
        f.write("timestamp_utc,open,high,low,close\n")
    print(f"  [OK] Empty CSV created: {csv_path}")


def setup_partial_csv(project_root: Path, date: str, instrument: str) -> None:
    """Setup: CSV file exists but has very few bars (partial hydration)."""
    csv_path = project_root / "data" / "raw" / instrument.lower() / "1m" / date[:4] / date[5:7] / f"{instrument.upper()}_1m_{date}.csv"
    backup_path = csv_path.with_suffix('.csv.backup')
    
    # Backup if exists
    if csv_path.exists():
        shutil.copy2(str(csv_path), str(backup_path))
    
    # Create CSV with only 2 bars
    csv_path.parent.mkdir(parents=True, exist_ok=True)
    with open(csv_path, 'w') as f:
        f.write("timestamp_utc,open,high,low,close\n")
        base_time = datetime.fromisoformat(f"{date}T00:00:00")
        for i in range(2):
            bar_time = base_time + timedelta(minutes=i)
            f.write(f"{bar_time.isoformat()}Z,5000.00,5000.25,4999.75,5000.10\n")
    print(f"  [OK] Partial CSV created (2 bars): {csv_path}")


def restore_csv_backups(project_root: Path, date: str, instrument: str) -> None:
    """Restore CSV files from backups."""
    csv_path = project_root / "data" / "raw" / instrument.lower() / "1m" / date[:4] / date[5:7] / f"{instrument.upper()}_1m_{date}.csv"
    backup_path = csv_path.with_suffix('.csv.backup')
    
    if backup_path.exists():
        shutil.move(str(backup_path), str(csv_path))
        print(f"  [OK] Restored CSV from backup: {csv_path}")
    elif csv_path.exists():
        # No backup, but CSV exists - leave it
        pass
    else:
        # No CSV and no backup - that's fine
        pass


def run_dryrun_replay(project_root: Path, start_date: str, end_date: str, log_dir: Path, 
                      timetable_path: Optional[str] = None) -> subprocess.CompletedProcess:
    """Run DRYRUN replay and return result."""
    cmd = [
        "dotnet", "run", "--project",
        str(project_root / "modules" / "robot" / "harness" / "Robot.Harness.csproj"),
        "--",
        "--mode", "DRYRUN",
        "--replay",
        "--start", start_date,
        "--end", end_date,
    ]
    
    if log_dir:
        cmd.extend(["--log-dir", str(log_dir)])
    
    if timetable_path:
        cmd.extend(["--timetable-path", timetable_path])
    
    print(f"\n  Running: {' '.join(cmd)}")
    result = subprocess.run(cmd, cwd=project_root, capture_output=True, text=True)
    return result


def analyze_hydration_logs(log_dir: Path) -> Dict:
    """Analyze hydration-related log events."""
    results = {
        "zero_bar_hydration_count": 0,
        "pre_hydration_zero_bars": 0,
        "pre_hydration_timeout": 0,
        "pre_hydration_complete": 0,
        "terminal_states": {},
        "hydration_errors": []
    }
    
    # Find log files
    log_files = list(log_dir.glob("*.jsonl"))
    if not log_files:
        log_files = list(log_dir.glob("**/*.jsonl"))
    
    for log_file in log_files:
        try:
            with open(log_file, 'r') as f:
                for line in f:
                    if not line.strip():
                        continue
                    try:
                        event = json.loads(line)
                        event_type = event.get("event_type", "")
                        data = event.get("data", {})
                        
                        # Count hydration events
                        if "ZERO_BAR_HYDRATION" in str(event):
                            results["zero_bar_hydration_count"] += 1
                        
                        if event_type == "PRE_HYDRATION_ZERO_BARS":
                            results["pre_hydration_zero_bars"] += 1
                        
                        if event_type == "PRE_HYDRATION_TIMEOUT" or event_type == "PRE_HYDRATION_FORCED_TRANSITION":
                            results["pre_hydration_timeout"] += 1
                        
                        if event_type == "PRE_HYDRATION_COMPLETE":
                            results["pre_hydration_complete"] += 1
                        
                        # Track terminal states
                        if "terminal_state" in data:
                            terminal_state = data["terminal_state"]
                            results["terminal_states"][terminal_state] = results["terminal_states"].get(terminal_state, 0) + 1
                        
                        # Track errors
                        if "error" in data and "hydration" in data["error"].lower():
                            results["hydration_errors"].append({
                                "event_type": event_type,
                                "error": data["error"]
                            })
                    except json.JSONDecodeError:
                        continue
        except Exception as e:
            print(f"  [WARN] Could not read log file {log_file}: {e}")
    
    return results


def check_journal_files(journal_dir: Path) -> Dict:
    """Check journal files for terminal states."""
    results = {
        "total_journals": 0,
        "zero_bar_hydration": 0,
        "no_trade": 0,
        "trade_completed": 0,
        "other_states": {}
    }
    
    if not journal_dir.exists():
        return results
    
    for journal_file in journal_dir.glob("*.json"):
        try:
            with open(journal_file, 'r') as f:
                journal = json.load(f)
                results["total_journals"] += 1
                
                terminal_state = journal.get("TerminalState", "")
                if terminal_state == "ZERO_BAR_HYDRATION":
                    results["zero_bar_hydration"] += 1
                elif terminal_state == "NO_TRADE":
                    results["no_trade"] += 1
                elif terminal_state == "TRADE_COMPLETED":
                    results["trade_completed"] += 1
                else:
                    results["other_states"][terminal_state] = results["other_states"].get(terminal_state, 0) + 1
        except Exception as e:
            print(f"  [WARN] Could not read journal file {journal_file}: {e}")
    
    return results


def run_scenario(scenario: HydrationTestScenario, project_root: Path, start_date: str, 
                 end_date: str, instrument: str) -> Dict:
    """Run a single hydration test scenario."""
    print(f"\n{'='*60}")
    print(f"Scenario: {scenario.name}")
    print(f"Description: {scenario.description}")
    print(f"{'='*60}")
    
    # Setup scenario
    print("\n[Setup]")
    scenario.setup_func(project_root, start_date, instrument)
    
    # Run dryrun
    test_id = f"hydration_test_{scenario.name.lower().replace(' ', '_')}_{datetime.now().strftime('%Y%m%d_%H%M%S')}"
    log_dir = project_root / "logs" / "robot" / test_id
    log_dir.mkdir(parents=True, exist_ok=True)
    
    print(f"\n[Running DRYRUN]")
    result = run_dryrun_replay(project_root, start_date, end_date, log_dir)
    
    if result.returncode != 0:
        print(f"  [FAIL] DRYRUN failed with return code {result.returncode}")
        print(f"  stderr: {result.stderr[:500]}")
        return {
            "scenario": scenario.name,
            "status": "FAILED",
            "error": f"DRYRUN failed: {result.stderr[:200]}"
        }
    
    print(f"  [OK] DRYRUN completed successfully")
    
    # Analyze results
    print(f"\n[Analyzing Results]")
    log_results = analyze_hydration_logs(log_dir)
    journal_dir = log_dir / "journal"
    journal_results = check_journal_files(journal_dir)
    
    print(f"  Log Analysis:")
    print(f"    - Zero-bar hydration events: {log_results['zero_bar_hydration_count']}")
    print(f"    - PRE_HYDRATION_ZERO_BARS: {log_results['pre_hydration_zero_bars']}")
    print(f"    - PRE_HYDRATION_TIMEOUT: {log_results['pre_hydration_timeout']}")
    print(f"    - PRE_HYDRATION_COMPLETE: {log_results['pre_hydration_complete']}")
    print(f"    - Terminal states in logs: {log_results['terminal_states']}")
    
    print(f"  Journal Analysis:")
    print(f"    - Total journals: {journal_results['total_journals']}")
    print(f"    - ZERO_BAR_HYDRATION: {journal_results['zero_bar_hydration']}")
    print(f"    - NO_TRADE: {journal_results['no_trade']}")
    print(f"    - TRADE_COMPLETED: {journal_results['trade_completed']}")
    print(f"    - Other states: {journal_results['other_states']}")
    
    # Restore CSV files
    print(f"\n[Cleanup]")
    restore_csv_backups(project_root, start_date, instrument)
    
    # Check if results match expectations
    passed = True
    issues = []
    
    if "expected_zero_bar_count" in scenario.expected_results:
        expected = scenario.expected_results["expected_zero_bar_count"]
        actual = log_results["zero_bar_hydration_count"]
        if actual != expected:
            passed = False
            issues.append(f"Expected {expected} zero-bar hydration events, got {actual}")
    
    if "expected_terminal_state" in scenario.expected_results:
        expected_state = scenario.expected_results["expected_terminal_state"]
        actual_count = journal_results.get("zero_bar_hydration" if expected_state == "ZERO_BAR_HYDRATION" else 
                                          "no_trade" if expected_state == "NO_TRADE" else
                                          "trade_completed", 0)
        if actual_count == 0 and journal_results["total_journals"] > 0:
            passed = False
            issues.append(f"Expected terminal state {expected_state}, but not found in journals")
    
    return {
        "scenario": scenario.name,
        "status": "PASSED" if passed else "FAILED",
        "log_results": log_results,
        "journal_results": journal_results,
        "issues": issues,
        "log_dir": str(log_dir)
    }


def main():
    parser = argparse.ArgumentParser(
        description="Hydration Test Dryrun - Specialized test for hydration scenarios",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="""
Examples:
  # Run all hydration scenarios
  python scripts/robot/hydration_test_dryrun.py --start 2025-12-01 --end 2025-12-02
  
  # Run specific scenario
  python scripts/robot/hydration_test_dryrun.py --start 2025-12-01 --end 2025-12-02 --scenario missing_csv
  
  # Use specific instrument
  python scripts/robot/hydration_test_dryrun.py --start 2025-12-01 --end 2025-12-02 --instrument ES
        """
    )
    
    parser.add_argument("--start", type=str, required=True, help="Start date (YYYY-MM-DD)")
    parser.add_argument("--end", type=str, required=True, help="End date (YYYY-MM-DD)")
    parser.add_argument("--instrument", type=str, default="ES", help="Instrument to test (default: ES)")
    parser.add_argument("--scenario", type=str, default=None, 
                       choices=["all", "normal", "missing_csv", "empty_csv", "partial_csv"],
                       help="Specific scenario to run (default: all)")
    
    args = parser.parse_args()
    
    project_root = Path(__file__).parent.parent.parent
    
    # Define test scenarios
    scenarios = [
        HydrationTestScenario(
            name="Normal Hydration",
            description="CSV file exists with bars - normal operation",
            setup_func=setup_normal_hydration,
            expected_results={
                "expected_zero_bar_count": 0,
                "expected_terminal_state": "NO_TRADE"  # Normal no-trade, not zero-bar
            }
        ),
        HydrationTestScenario(
            name="Missing CSV",
            description="CSV file is missing - should trigger zero-bar hydration",
            setup_func=setup_missing_csv,
            expected_results={
                "expected_zero_bar_count": 1,  # Should detect zero-bar hydration
                "expected_terminal_state": "ZERO_BAR_HYDRATION"
            }
        ),
        HydrationTestScenario(
            name="Empty CSV",
            description="CSV file exists but is empty - should trigger zero-bar hydration",
            setup_func=setup_empty_csv,
            expected_results={
                "expected_zero_bar_count": 1,
                "expected_terminal_state": "ZERO_BAR_HYDRATION"
            }
        ),
        HydrationTestScenario(
            name="Partial CSV",
            description="CSV file has very few bars - partial hydration",
            setup_func=setup_partial_csv,
            expected_results={
                "expected_zero_bar_count": 0,  # Few bars is still hydration, not zero-bar
                "expected_terminal_state": "NO_TRADE"
            }
        ),
    ]
    
    # Filter scenarios if specific one requested
    if args.scenario and args.scenario != "all":
        scenario_map = {
            "normal": "Normal Hydration",
            "missing_csv": "Missing CSV",
            "empty_csv": "Empty CSV",
            "partial_csv": "Partial CSV"
        }
        scenario_name = scenario_map.get(args.scenario)
        scenarios = [s for s in scenarios if s.name == scenario_name]
    
    print("="*60)
    print("Hydration Test Dryrun")
    print("="*60)
    print(f"Date range: {args.start} to {args.end}")
    print(f"Instrument: {args.instrument}")
    print(f"Scenarios: {len(scenarios)}")
    print()
    
    results = []
    for scenario in scenarios:
        result = run_scenario(scenario, project_root, args.start, args.end, args.instrument)
        results.append(result)
    
    # Summary
    print("\n" + "="*60)
    print("Test Summary")
    print("="*60)
    
    passed = sum(1 for r in results if r["status"] == "PASSED")
    failed = sum(1 for r in results if r["status"] == "FAILED")
    
    print(f"\nTotal scenarios: {len(results)}")
    print(f"Passed: {passed}")
    print(f"Failed: {failed}")
    
    print("\nDetailed Results:")
    for result in results:
        status_icon = "[PASS]" if result["status"] == "PASSED" else "[FAIL]"
        print(f"\n{status_icon} {result['scenario']}: {result['status']}")
        if result.get("issues"):
            for issue in result["issues"]:
                print(f"      - {issue}")
        if "log_dir" in result:
            print(f"    Log directory: {result['log_dir']}")
    
    return 0 if failed == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
