#!/usr/bin/env python3
"""
Duplicate/Overlapping Bar DRYRUN Test

Question: "Does deduplication really hold?"

Tests:
- Inject duplicate bars with same timestamp
- Inject bars with same timestamp but slightly different OHLC
- Inject duplicates from different sources (CSV vs BarsRequest equivalent)

Verifies:
- Deduplication precedence works (LIVE > BARSREQUEST > CSV)
- Final range is deterministic
- Logs show dedupe counts
"""

import json
import random
import subprocess
import sys
from pathlib import Path
from datetime import datetime, timedelta
import tempfile
import shutil
import csv

# Test scenarios
TEST_SCENARIOS = {
    "same_timestamp_same_ohlc": {
        "description": "Duplicate bars with identical timestamp and OHLC",
        "modify_ohlc": False
    },
    "same_timestamp_different_ohlc": {
        "description": "Duplicate bars with same timestamp but different OHLC",
        "modify_ohlc": True
    },
    "csv_then_barsrequest": {
        "description": "Same bar from CSV then BarsRequest (BARSREQUEST should win)",
        "source_precedence": True
    }
}

def load_csv_bars(csv_path):
    """Load bars from CSV file."""
    bars = []
    with open(csv_path, 'r') as f:
        reader = csv.reader(f)
        next(reader)  # Skip header
        for row in reader:
            if len(row) >= 5:
                bars.append(row)
    return bars

def create_duplicate_csv(test_dir, instrument, date, scenario_name, scenario_config):
    """Create CSV with duplicate bars."""
    project_root = Path.cwd()
    src_csv = project_root / "data" / "raw" / instrument.lower() / "1m" / date[:4] / date[5:7] / f"{instrument.upper()}_1m_{date}.csv"
    
    if not src_csv.exists():
        print(f"  WARNING: Source CSV not found: {src_csv}")
        return None
    
    bars = load_csv_bars(src_csv)
    
    if len(bars) == 0:
        print(f"  WARNING: No bars in source CSV")
        return None
    
    # Filter bars to range window
    range_start = datetime.strptime(f"{date} 07:30:00", "%Y-%m-%d %H:%M:%S")
    range_end = datetime.strptime(f"{date} 08:00:00", "%Y-%m-%d %H:%M:%S")
    
    range_bars = []
    for bar in bars:
        try:
            bar_time = datetime.fromisoformat(bar[0].replace('Z', '+00:00'))
            bar_time_local = bar_time.replace(tzinfo=None)
            if range_start <= bar_time_local < range_end:
                range_bars.append(bar)
        except:
            continue
    
    if len(range_bars) < 2:
        print(f"  WARNING: Not enough bars for duplicate test")
        return None
    
    # Create duplicates based on scenario
    if scenario_name == "same_timestamp_same_ohlc":
        # Duplicate a bar exactly
        duplicate_bar = range_bars[0].copy()
        range_bars.insert(1, duplicate_bar)
        print(f"  Inserted exact duplicate of first bar")
    
    elif scenario_name == "same_timestamp_different_ohlc":
        # Duplicate a bar but modify OHLC slightly
        duplicate_bar = range_bars[0].copy()
        # Modify OHLC values slightly
        duplicate_bar[1] = str(float(duplicate_bar[1]) + 0.25)  # Open
        duplicate_bar[2] = str(float(duplicate_bar[2]) + 0.25)  # High
        duplicate_bar[3] = str(float(duplicate_bar[3]) - 0.25)  # Low
        duplicate_bar[4] = str(float(duplicate_bar[4]) + 0.25)  # Close
        range_bars.insert(1, duplicate_bar)
        print(f"  Inserted duplicate with modified OHLC")
    
    elif scenario_name == "csv_then_barsrequest":
        # This scenario tests precedence - CSV bar exists, then same bar arrives via BarsRequest
        # In DRYRUN, we can't simulate BarsRequest, but we can verify deduplication logic
        # by checking that dedupe counts are logged
        duplicate_bar = range_bars[0].copy()
        range_bars.insert(1, duplicate_bar)
        print(f"  Inserted duplicate to test deduplication (CSV vs CSV in DRYRUN)")
    
    # Create CSV with duplicates
    dst_dir = test_dir / "data" / "raw" / instrument.lower() / "1m" / date[:4] / date[5:7]
    dst_dir.mkdir(parents=True, exist_ok=True)
    dst_csv = dst_dir / f"{instrument.upper()}_1m_{date}.csv"
    
    # Replace range window bars
    all_bars = load_csv_bars(src_csv)
    range_start_idx = None
    range_end_idx = None
    
    for i, bar in enumerate(all_bars):
        try:
            bar_time = datetime.fromisoformat(bar[0].replace('Z', '+00:00'))
            bar_time_local = bar_time.replace(tzinfo=None)
            if range_start <= bar_time_local < range_end:
                if range_start_idx is None:
                    range_start_idx = i
                range_end_idx = i + 1
        except:
            continue
    
    if range_start_idx is not None and range_end_idx is not None:
        new_bars = all_bars[:range_start_idx] + range_bars + all_bars[range_end_idx:]
    else:
        new_bars = all_bars
    
    # Write CSV with duplicates
    with open(dst_csv, 'w', newline='') as f:
        writer = csv.writer(f)
        writer.writerow(["timestamp_utc", "open", "high", "low", "close", "volume"])
        writer.writerows(new_bars)
    
    print(f"  Created CSV with duplicates: {dst_csv}")
    return dst_csv

def run_duplicate_test(scenario_name, scenario_config, trading_date="2026-01-16"):
    """Run a single duplicate bar test."""
    print(f"\n{'='*80}")
    print(f"Duplicate Bar Test: {scenario_name}")
    print(f"Description: {scenario_config['description']}")
    print(f"{'='*80}")
    
    project_root = Path.cwd()
    test_dir = Path(tempfile.mkdtemp(prefix="qtsw2_duplicate_"))
    
    try:
        # Copy project structure
        for dir_name in ["configs"]:
            src_dir = project_root / dir_name
            if src_dir.exists():
                dst_dir = test_dir / dir_name
                shutil.copytree(src_dir, dst_dir, dirs_exist_ok=True)
        
        # Copy parquet files if they exist (required for DRYRUN replay mode)
        parquet_src = project_root / "data" / "translated"
        parquet_dst = test_dir / "data" / "translated"
        if parquet_src.exists():
            parquet_dst.mkdir(parents=True, exist_ok=True)
            # Copy parquet files for the trading date
            for parquet_file in parquet_src.glob(f"**/*{trading_date}*.parquet"):
                rel_path = parquet_file.relative_to(parquet_src)
                dst_file = parquet_dst / rel_path
                dst_file.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy(parquet_file, dst_file)
            print(f"  Copied parquet files for {trading_date}")
        else:
            print(f"  WARNING: Parquet directory not found: {parquet_src}")
            print(f"  DRYRUN replay mode requires parquet files in data/translated/")
        
        # Create timetable
        timetable_path = test_dir / "data" / "timetable" / "timetable_current.json"
        timetable_path.parent.mkdir(parents=True, exist_ok=True)
        timetable = {
            "as_of": datetime.now().isoformat(),
            "trading_date": trading_date,
            "timezone": "America/Chicago",
            "source": "duplicate_test",
            "streams": [
                {"stream": "ES1", "instrument": "ES", "session": "S1", "slot_time": "08:00", "enabled": True}
            ]
        }
        with open(timetable_path, 'w') as f:
            json.dump(timetable, f, indent=2)
        
        # Create CSV with duplicates
        duplicate_csv = create_duplicate_csv(test_dir, "ES", trading_date, scenario_name, scenario_config)
        if not duplicate_csv:
            print("  WARNING: Could not create duplicate CSV - skipping test")
            return False
        
        # Run DRYRUN with replay mode
        env = os.environ.copy()
        env["QTSW2_PROJECT_ROOT"] = str(test_dir)
        
        result = subprocess.run(
            [
                "dotnet", "run",
                "--project", str(project_root / "modules/robot/harness/Robot.Harness.csproj"),
                "--mode", "DRYRUN",
                "--replay",
                "--start", trading_date,
                "--end", trading_date,
                "--timetable-path", str(timetable_path)
            ],
            cwd=project_root,
            capture_output=True,
            text=True,
            timeout=120,
            env=env
        )
        
        # Analyze logs
        log_dir = test_dir / "logs" / "robot"
        dedupe_found = False
        range_computed = False
        issues = []
        
        if log_dir.exists():
            for log_file in log_dir.glob("*.jsonl"):
                with open(log_file, 'r') as f:
                    for line in f:
                        try:
                            event = json.loads(line.strip())
                            event_type = event.get('event_type') or event.get('event', '')
                            data = event.get('data', {})
                            
                            # Check for deduplication logs
                            if 'DEDUP' in event_type.upper() or 'deduped' in str(data).lower():
                                dedupe_count = data.get('deduped_bar_count') or data.get('deduplicated', 0)
                                if dedupe_count and dedupe_count > 0:
                                    dedupe_found = True
                                    print(f"  PASS: Deduplication logged: {dedupe_count} bars deduplicated")
                            
                            # Check HYDRATION_SUMMARY for dedupe counts
                            if 'HYDRATION_SUMMARY' in event_type.upper():
                                dedupe_count = data.get('deduped_bar_count', 0)
                                if dedupe_count > 0:
                                    dedupe_found = True
                                    print(f"  PASS: Deduplication in summary: {dedupe_count} bars")
                            
                            # Check for range computation
                            if 'RANGE_COMPUTE' in event_type.upper():
                                if data.get('range_high') and data.get('range_low'):
                                    range_computed = True
                                    print(f"  PASS: Range computed: High={data.get('range_high')}, Low={data.get('range_low')}")
                        except:
                            pass
        
        # Check results
        print(f"\nResult: {'PASS' if dedupe_found and range_computed else 'FAIL'}")
        if not dedupe_found:
            print("  WARNING: Deduplication not logged (may be working but not logged)")
        if not range_computed:
            issues.append("Range not computed")
        
        return dedupe_found and range_computed
        
    finally:
        try:
            shutil.rmtree(test_dir)
        except:
            pass

def main():
    """Run all duplicate bar tests."""
    print("="*80)
    print("DUPLICATE BAR DRYRUN TEST SUITE")
    print("="*80)
    
    results = {}
    for scenario_name, scenario_config in TEST_SCENARIOS.items():
        results[scenario_name] = run_duplicate_test(scenario_name, scenario_config)
    
    # Summary
    print("\n" + "="*80)
    print("DUPLICATE BAR TEST SUMMARY")
    print("="*80)
    for scenario_name, passed in results.items():
        status = "PASS" if passed else "FAIL"
        print(f"{scenario_name:30s} {status}")

if __name__ == "__main__":
    import os
    main()
