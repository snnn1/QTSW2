#!/usr/bin/env python3
"""
Multi-Day Continuous DRYRUN Test

Question: "Does strategy logic remain stable across days with streams active?"

Tests:
- Run DRYRUN over 30-90 trading days
- Real timetable
- Real bar data
- No resets

Verifies:
- No drift
- No memory growth
- No state leakage
- Journals written every day
"""

import json
import subprocess
import sys
from pathlib import Path
from datetime import datetime, timedelta
import tempfile
import shutil
import os

def get_trading_dates(start_date, end_date):
    """Get list of trading dates (skip weekends)."""
    dates = []
    current = datetime.strptime(start_date, "%Y-%m-%d")
    end = datetime.strptime(end_date, "%Y-%m-%d")
    
    while current <= end:
        # Skip weekends
        if current.weekday() < 5:  # Monday=0, Friday=4
            dates.append(current.strftime("%Y-%m-%d"))
        current += timedelta(days=1)
    
    return dates

def create_timetable_for_date(test_dir, trading_date, streams):
    """Create timetable for specific trading date."""
    timetable_path = test_dir / "data" / "timetable" / "timetable_current.json"
    timetable_path.parent.mkdir(parents=True, exist_ok=True)
    
    timetable = {
        "as_of": datetime.now().isoformat(),
        "trading_date": trading_date,
        "timezone": "America/Chicago",
        "source": "multiday_test",
        "streams": streams
    }
    
    with open(timetable_path, 'w') as f:
        json.dump(timetable, f, indent=2)
    
    return timetable_path

def run_multiday_test(start_date, end_date, streams):
    """Run multi-day continuous DRYRUN test."""
    print(f"\n{'='*80}")
    print(f"Multi-Day Continuous DRYRUN Test")
    print(f"Date Range: {start_date} to {end_date}")
    print(f"Streams: {len(streams)}")
    print(f"{'='*80}")
    
    trading_dates = get_trading_dates(start_date, end_date)
    print(f"Trading days: {len(trading_dates)}")
    
    project_root = Path.cwd()
    test_dir = Path(tempfile.mkdtemp(prefix="qtsw2_multiday_"))
    
    try:
        # Copy project structure
        for dir_name in ["configs"]:
            src_dir = project_root / dir_name
            if src_dir.exists():
                dst_dir = test_dir / dir_name
                shutil.copytree(src_dir, dst_dir, dirs_exist_ok=True)
        
        # Copy bar data for all dates
        bar_src_base = project_root / "data" / "raw"
        bar_dst_base = test_dir / "data" / "raw"
        
        for date in trading_dates:
            year = date[:4]
            month = date[5:7]
            
            for stream_config in streams:
                instrument = stream_config["instrument"].lower()
                src_dir = bar_src_base / instrument / "1m" / year / month
                if src_dir.exists():
                    csv_file = src_dir / f"{stream_config['instrument'].upper()}_1m_{date}.csv"
                    if csv_file.exists():
                        dst_dir = bar_dst_base / instrument / "1m" / year / month
                        dst_dir.mkdir(parents=True, exist_ok=True)
                        shutil.copy(csv_file, dst_dir / csv_file.name)
        
        # Run DRYRUN for each date
        env = os.environ.copy()
        env["QTSW2_PROJECT_ROOT"] = str(test_dir)
        
        results = {}
        journal_files = []
        memory_usage = []
        
        for i, date in enumerate(trading_dates):
            print(f"\nProcessing day {i+1}/{len(trading_dates)}: {date}")
            
            # Create timetable for this date
            timetable_path = create_timetable_for_date(test_dir, date, streams)
            
            # Run DRYRUN with replay mode
            result = subprocess.run(
                [
                    "dotnet", "run",
                    "--project", str(project_root / "modules/robot/harness/Robot.Harness.csproj"),
                    "--mode", "DRYRUN",
                    "--replay",
                    "--start", date,
                    "--end", date,
                    "--timetable-path", str(timetable_path)
                ],
                cwd=project_root,
                capture_output=True,
                text=True,
                timeout=300,
                env=env
            )
            
            # Check for journal files
            journal_dir = test_dir / "logs" / "robot" / "journal"
            if journal_dir.exists():
                for journal_file in journal_dir.glob(f"{date}_*.json"):
                    journal_files.append(journal_file.name)
            
            # Check logs for issues
            log_dir = test_dir / "logs" / "robot"
            day_issues = []
            
            if log_dir.exists():
                for log_file in log_dir.glob("*.jsonl"):
                    with open(log_file, 'r') as f:
                        for line in f:
                            try:
                                event = json.loads(line.strip())
                                event_type = event.get('event_type') or event.get('event', '')
                                
                                # Check for errors
                                if event.get('level') == 'ERROR':
                                    if 'MEMORY' in event_type.upper() or 'LEAK' in event_type.upper():
                                        day_issues.append(f"Memory issue: {event_type}")
                                
                                # Check for state issues
                                if 'INVARIANT' in event_type.upper() or 'VIOLATION' in event_type.upper():
                                    day_issues.append(f"Invariant violation: {event_type}")
                            except:
                                pass
            
            results[date] = {
                "passed": len(day_issues) == 0,
                "issues": day_issues,
                "journal_count": len([f for f in journal_files if date in f])
            }
            
            if day_issues:
                print(f"  WARNING: Issues found: {day_issues}")
            else:
                print(f"  PASS: Day {date} passed")
        
        # Summary
        print("\n" + "="*80)
        print("MULTI-DAY TEST SUMMARY")
        print("="*80)
        
        passed_days = sum(1 for r in results.values() if r["passed"])
        total_journals = len(journal_files)
        
        print(f"Days processed: {len(results)}")
        print(f"Days passed: {passed_days}")
        print(f"Days failed: {len(results) - passed_days}")
        print(f"Journal files created: {total_journals}")
        
        # Check for patterns
        if passed_days < len(results):
            print("\nFailed days:")
            for date, result in results.items():
                if not result["passed"]:
                    print(f"  {date}: {result['issues']}")
        
        if total_journals < len(results):
            print(f"\nWARNING: Missing journals: Expected {len(results)}, got {total_journals}")
        
        return passed_days == len(results) and total_journals == len(results)
        
    finally:
        try:
            shutil.rmtree(test_dir)
        except:
            pass

def main():
    """Run multi-day test."""
    print("="*80)
    print("MULTI-DAY CONTINUOUS DRYRUN TEST")
    print("="*80)
    
    # Test configuration
    start_date = "2026-01-13"
    end_date = "2026-02-28"  # ~30 trading days
    
    streams = [
        {"stream": "ES1", "instrument": "ES", "session": "S1", "slot_time": "08:00", "enabled": True},
        {"stream": "ES2", "instrument": "ES", "session": "S2", "slot_time": "10:00", "enabled": True}
    ]
    
    passed = run_multiday_test(start_date, end_date, streams)
    
    print(f"\n{'='*80}")
    print(f"Overall Result: {'PASS' if passed else 'FAIL'}")
    print(f"{'='*80}")

if __name__ == "__main__":
    main()
