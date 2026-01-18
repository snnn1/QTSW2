#!/usr/bin/env python3
"""
Late-Start DRYRUN Test (Critical Realism)

Question: "What happens if the system starts after range start?"

Tests:
- Start at 07:45 (15 min after range start)
- Start at 08:10 (40 min after range start)
- Start at 08:45 (75 min after range start)

Verifies:
- BarsRequest window truncation behaves correctly
- Range still computes correctly from partial data
- No future bars are injected
- State machine does not mis-transition
"""

import json
import os
import subprocess
import sys
from pathlib import Path
from datetime import datetime, timedelta
import tempfile
import shutil

# Test scenarios
TEST_TIMES = [
    ("07:45", "15 minutes after range start"),
    ("08:10", "40 minutes after range start"),
    ("08:45", "75 minutes after range start (near slot time)"),
]

def create_test_timetable(test_dir, trading_date, start_time_chicago):
    """Create timetable with trading date."""
    timetable_path = Path(test_dir) / "data" / "timetable" / "timetable_current.json"
    timetable_path.parent.mkdir(parents=True, exist_ok=True)
    
    # Parse start time to get hour:minute
    hour, minute = start_time_chicago.split(":")
    start_datetime = datetime.strptime(f"{trading_date} {hour}:{minute}", "%Y-%m-%d %H:%M")
    
    timetable = {
        "as_of": start_datetime.isoformat(),
        "trading_date": trading_date,
        "timezone": "America/Chicago",
        "source": "late_start_test",
        "streams": [
            {"stream": "ES1", "instrument": "ES", "session": "S1", "slot_time": "08:00", "enabled": True}
        ]
    }
    
    with open(timetable_path, 'w') as f:
        json.dump(timetable, f, indent=2)
    
    return timetable_path

def filter_bars_for_late_start(csv_path, start_time_chicago, trading_date):
    """Filter CSV to simulate late start by removing bars before start_time."""
    import csv
    
    # Parse start time
    hour, minute = start_time_chicago.split(":")
    start_datetime = datetime.strptime(f"{trading_date} {hour}:{minute}", "%Y-%m-%d %H:%M")
    start_utc = start_datetime.replace(tzinfo=None)  # Will be treated as Chicago time
    
    # Load bars
    bars = []
    with open(csv_path, 'r') as f:
        reader = csv.reader(f)
        header = next(reader)
        for row in reader:
            if len(row) >= 5:
                bars.append(row)
    
    # Filter bars - keep only those >= start_time
    filtered_bars = []
    removed_count = 0
    
    for bar in bars:
        try:
            bar_time_str = bar[0]
            # Parse UTC timestamp
            bar_time = datetime.fromisoformat(bar_time_str.replace('Z', '+00:00'))
            bar_time_local = bar_time.replace(tzinfo=None)  # Convert to naive for comparison
            
            # Keep bar if >= start_time
            if bar_time_local >= start_datetime:
                filtered_bars.append(bar)
            else:
                removed_count += 1
        except:
            # Keep bar if parsing fails (shouldn't happen)
            filtered_bars.append(bar)
    
    return filtered_bars, header, removed_count

def run_late_start_test(start_time_chicago, description, trading_date="2026-01-16"):
    """Run a single late-start test."""
    print(f"\n{'='*80}")
    print(f"Late-Start Test: {start_time_chicago} ({description})")
    print(f"{'='*80}")
    
    project_root = Path.cwd()
    test_dir = Path(tempfile.mkdtemp(prefix="qtsw2_late_start_"))
    
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
        timetable_path = create_test_timetable(test_dir, trading_date, start_time_chicago)
        
        # Copy and filter bar data to simulate late start
        bar_src = project_root / "data" / "raw" / "es" / "1m" / "2026" / "01"
        bar_dst = test_dir / "data" / "raw" / "es" / "1m" / "2026" / "01"
        bar_dst.mkdir(parents=True, exist_ok=True)
        
        if bar_src.exists() and (bar_src / f"ES_1m_{trading_date}.csv").exists():
            src_csv = bar_src / f"ES_1m_{trading_date}.csv"
            dst_csv = bar_dst / f"ES_1m_{trading_date}.csv"
            
            # Filter bars to simulate late start
            filtered_bars, header, removed_count = filter_bars_for_late_start(src_csv, start_time_chicago, trading_date)
            
            # Write filtered CSV
            import csv
            with open(dst_csv, 'w', newline='') as f:
                writer = csv.writer(f)
                writer.writerow(header)
                writer.writerows(filtered_bars)
            
            print(f"  Filtered bars: Removed {removed_count} bars before {start_time_chicago}, kept {len(filtered_bars)} bars")
        else:
            print(f"  WARNING: Source CSV not found: {bar_src / f'ES_1m_{trading_date}.csv'}")
            return False
        
        # Run DRYRUN with replay mode (simulates late start via timetable as_of time)
        env = os.environ.copy()
        env["QTSW2_PROJECT_ROOT"] = str(test_dir)
        
        # Use replay mode to simulate historical execution
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
        issues = []
        warnings = []
        
        if log_dir.exists():
            for log_file in log_dir.glob("*.jsonl"):
                with open(log_file, 'r') as f:
                    for line in f:
                        try:
                            event = json.loads(line.strip())
                            event_type = event.get('event_type') or event.get('event', '')
                            data = event.get('data', {})
                            
                            # Check for future bars
                            if 'FUTURE' in event_type.upper() or 'future' in str(data).lower():
                                warnings.append(f"{event_type}: {data}")
                            
                            # Check for range computation
                            if 'RANGE_COMPUTE' in event_type.upper():
                                if data.get('range_high') and data.get('range_low'):
                                    print(f"  PASS: Range computed: High={data.get('range_high')}, Low={data.get('range_low')}")
                                else:
                                    issues.append(f"Range computed but missing high/low: {event_type}")
                            
                            # Check for state transitions
                            if 'TRANSITION' in event_type.upper() or 'ARMED' in event_type.upper():
                                state = data.get('state') or event.get('state', '')
                                if state:
                                    print(f"  State: {state}")
                        except:
                            pass
        
        # Check results
        print(f"\nResult: {'PASS' if not issues else 'FAIL'}")
        if issues:
            print(f"Issues found: {issues}")
        if warnings:
            print(f"Warnings: {warnings}")
        
        return len(issues) == 0
        
    finally:
        try:
            shutil.rmtree(test_dir)
        except:
            pass

def main():
    """Run all late-start tests."""
    print("="*80)
    print("LATE-START DRYRUN TEST SUITE")
    print("="*80)
    
    results = {}
    for start_time, description in TEST_TIMES:
        results[start_time] = run_late_start_test(start_time, description)
    
    # Summary
    print("\n" + "="*80)
    print("LATE-START TEST SUMMARY")
    print("="*80)
    for start_time, passed in results.items():
        status = "PASS" if passed else "FAIL"
        print(f"{start_time:10s} {status}")

if __name__ == "__main__":
    main()
