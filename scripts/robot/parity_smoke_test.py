#!/usr/bin/env python3
"""
Parity Smoke Test

Lightweight test that runs a short replay, extracts intents, and verifies HARD_MISMATCH = 0.

This is a safety net to catch parity regressions early.
"""

import subprocess
import sys
from pathlib import Path
import json

def main():
    project_root = Path(__file__).parent.parent.parent
    
    # Use a small date window (1-2 days) for quick smoke test
    start_date = "2025-12-01"
    end_date = "2025-12-02"
    run_id = f"smoke_test_{start_date.replace('-', '')}_{end_date.replace('-', '')}"
    
    log_dir = project_root / "docs" / "robot" / "parity_runs" / run_id / "robot_logs"
    log_dir.mkdir(parents=True, exist_ok=True)
    
    print(f"Running parity smoke test: {start_date} to {end_date}")
    print(f"Run ID: {run_id}")
    
    # Step 1: Run DRYRUN replay
    print("\n[1/4] Running DRYRUN replay...")
    replay_cmd = [
        "dotnet", "run", "--project", 
        str(project_root / "modules" / "robot" / "harness" / "Robot.Harness.csproj"),
        "--",
        "--mode", "DRYRUN",
        "--replay",
        "--start", start_date,
        "--end", end_date,
        "--timetable-path", "data/timetable/timetable_replay.json",
        "--log-dir", str(log_dir)
    ]
    
    result = subprocess.run(replay_cmd, cwd=project_root, capture_output=True, text=True)
    if result.returncode != 0:
        print(f"ERROR: Replay failed with return code {result.returncode}")
        print(result.stderr)
        return 1
    
    # Step 2: Extract Robot intents
    print("\n[2/4] Extracting Robot intents...")
    robot_extract_cmd = [
        sys.executable,
        str(project_root / "modules" / "robot" / "parity" / "robot_intent_extractor.py"),
        run_id
    ]
    
    result = subprocess.run(robot_extract_cmd, cwd=project_root, capture_output=True, text=True)
    if result.returncode != 0:
        print(f"ERROR: Robot intent extraction failed")
        print(result.stderr)
        return 1
    
    robot_intents_path = project_root / "docs" / "robot" / "parity_runs" / run_id / "robot_intents.csv"
    if not robot_intents_path.exists():
        print(f"ERROR: Robot intents file not found: {robot_intents_path}")
        return 1
    
    # Step 3: Extract Analyzer intents
    print("\n[3/4] Extracting Analyzer intents...")
    analyzer_extract_cmd = [
        sys.executable,
        str(project_root / "modules" / "analyzer" / "parity" / "analyzer_intent_extractor.py"),
        "--input", "data/analyzed",
        "--run-id", run_id,
        "--out", str(project_root / "docs" / "robot" / "parity_runs" / run_id / "analyzer_intents.csv")
    ]
    
    result = subprocess.run(analyzer_extract_cmd, cwd=project_root, capture_output=True, text=True)
    if result.returncode != 0:
        print(f"ERROR: Analyzer intent extraction failed")
        print(result.stderr)
        return 1
    
    # Filter Analyzer intents to date range
    import pandas as pd
    analyzer_df = pd.read_csv(project_root / "docs" / "robot" / "parity_runs" / run_id / "analyzer_intents.csv")
    analyzer_filtered = analyzer_df[(analyzer_df['trading_date'] >= start_date) & (analyzer_df['trading_date'] <= end_date)]
    analyzer_filtered.to_csv(project_root / "docs" / "robot" / "parity_runs" / run_id / "analyzer_intents.csv", index=False)
    
    # Step 4: Run parity diff
    print("\n[4/4] Running parity diff...")
    parity_diff_cmd = [
        sys.executable,
        str(project_root / "modules" / "robot" / "parity" / "parity_diff.py"),
        "--run-id", run_id
    ]
    
    result = subprocess.run(parity_diff_cmd, cwd=project_root, capture_output=True, text=True)
    if result.returncode != 0:
        print(f"ERROR: Parity diff failed")
        print(result.stderr)
        return 1
    
    # Step 5: Verify HARD_MISMATCH = 0
    summary_path = project_root / "docs" / "robot" / "parity_runs" / run_id / "parity_summary.md"
    if not summary_path.exists():
        print(f"ERROR: Parity summary not found: {summary_path}")
        return 1
    
    summary_text = summary_path.read_text()
    if "HARD_MISMATCH**: 0" not in summary_text:
        print("\n" + "="*80)
        print("PARITY SMOKE TEST FAILED")
        print("="*80)
        print(summary_text)
        return 1
    
    print("\n" + "="*80)
    print("PARITY SMOKE TEST PASSED")
    print("="*80)
    print(summary_text)
    return 0

if __name__ == "__main__":
    sys.exit(main())
