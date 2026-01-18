#!/usr/bin/env python3
"""
Stress test suite for pre-hydration functionality.

Tests various edge cases and failure scenarios:
1. Missing data files
2. Empty files
3. Corrupted files (invalid format, missing columns)
4. Wrong trading dates
5. Out-of-order bars
6. Large data volumes
7. Gaps in data
8. Future bars
9. Invalid timestamps
10. Invalid OHLC values
11. DST edge cases
12. Multiple streams simultaneously
"""

import os
import json
import shutil
import tempfile
from pathlib import Path
from datetime import datetime, timedelta
import subprocess
import sys

# Test scenarios
SCENARIOS = {
    "missing_file": {
        "description": "Test behavior when CSV file is missing",
        "setup": lambda test_dir, date: None,  # Don't create file
        "expected": ["PRE_HYDRATION_COMPLETE", "HYDRATION_SUMMARY"]  # Completes with zero bars
    },
    "empty_file": {
        "description": "Test behavior with empty CSV file (only header)",
        "setup": lambda test_dir, date: _create_empty_csv(test_dir, date),
        "expected": ["PRE_HYDRATION_COMPLETE", "HYDRATION_SUMMARY"]  # Completes with zero bars
    },
    "corrupted_format": {
        "description": "Test behavior with corrupted CSV (invalid format)",
        "setup": lambda test_dir, date: _create_corrupted_csv(test_dir, date),
        "expected": ["PRE_HYDRATION_COMPLETE"]  # Should skip invalid lines
    },
    "wrong_date": {
        "description": "Test behavior with CSV from wrong trading date",
        "setup": lambda test_dir, date: _create_wrong_date_csv(test_dir, date),
        "expected": ["PRE_HYDRATION_COMPLETE"]  # Should filter out wrong date bars
    },
    "out_of_order": {
        "description": "Test behavior with out-of-order bars",
        "setup": lambda test_dir, date: _create_out_of_order_csv(test_dir, date),
        "expected": ["PRE_HYDRATION_COMPLETE"]  # Should sort correctly
    },
    "large_volume": {
        "description": "Test behavior with very large CSV file (10k+ bars)",
        "setup": lambda test_dir, date: _create_large_csv(test_dir, date, 15000),
        "expected": ["PRE_HYDRATION_COMPLETE"]
    },
    "data_gaps": {
        "description": "Test behavior with gaps in data",
        "setup": lambda test_dir, date: _create_gapped_csv(test_dir, date),
        "expected": ["PRE_HYDRATION_COMPLETE"]
    },
    "future_bars": {
        "description": "Test behavior with future bars in CSV",
        "setup": lambda test_dir, date: _create_future_bars_csv(test_dir, date),
        "expected": ["PRE_HYDRATION_COMPLETE"]  # Should filter future bars
    },
    "invalid_timestamps": {
        "description": "Test behavior with invalid timestamps",
        "setup": lambda test_dir, date: _create_invalid_timestamps_csv(test_dir, date),
        "expected": ["PRE_HYDRATION_COMPLETE"]  # Should skip invalid lines
    },
    "invalid_ohlc": {
        "description": "Test behavior with invalid OHLC values",
        "setup": lambda test_dir, date: _create_invalid_ohlc_csv(test_dir, date),
        "expected": ["PRE_HYDRATION_COMPLETE"]  # Should skip invalid lines
    },
    "dst_transition": {
        "description": "Test behavior around DST transition",
        "setup": lambda test_dir, date: _create_dst_csv(test_dir, date),
        "expected": ["PRE_HYDRATION_COMPLETE"]
    },
    "multiple_streams": {
        "description": "Test multiple streams loading simultaneously",
        "setup": lambda test_dir, date: _create_multi_stream_csvs(test_dir, date),
        "expected": ["PRE_HYDRATION_COMPLETE"]
    }
}

def _create_empty_csv(test_dir, date):
    """Create empty CSV with only header."""
    file_path = _get_csv_path(test_dir, "ES", date)
    file_path.parent.mkdir(parents=True, exist_ok=True)
    with open(file_path, 'w') as f:
        f.write("timestamp_utc,open,high,low,close,volume\n")

def _create_corrupted_csv(test_dir, date):
    """Create corrupted CSV with invalid format."""
    file_path = _get_csv_path(test_dir, "ES", date)
    file_path.parent.mkdir(parents=True, exist_ok=True)
    with open(file_path, 'w') as f:
        f.write("timestamp_utc,open,high,low,close,volume\n")
        f.write("invalid,line,format\n")
        f.write("2026-01-16T08:00:00Z,7000,7005,6995,7002\n")  # Missing volume OK
        f.write("not,a,valid,line\n")

def _create_wrong_date_csv(test_dir, date):
    """Create CSV with bars from wrong trading date."""
    file_path = _get_csv_path(test_dir, "ES", date)
    file_path.parent.mkdir(parents=True, exist_ok=True)
    wrong_date = (datetime.strptime(date, "%Y-%m-%d") - timedelta(days=1)).strftime("%Y-%m-%d")
    with open(file_path, 'w') as f:
        f.write("timestamp_utc,open,high,low,close,volume\n")
        # Add bars from wrong date (should be filtered)
        base_time = datetime.strptime(f"{wrong_date}T08:00:00", "%Y-%m-%dT%H:%M:%S")
        for i in range(10):
            ts = (base_time + timedelta(minutes=i)).strftime("%Y-%m-%dT%H:%M:%SZ")
            f.write(f"{ts},7000,7005,6995,7002,1000\n")

def _create_out_of_order_csv(test_dir, date):
    """Create CSV with out-of-order bars."""
    file_path = _get_csv_path(test_dir, "ES", date)
    file_path.parent.mkdir(parents=True, exist_ok=True)
    with open(file_path, 'w') as f:
        f.write("timestamp_utc,open,high,low,close,volume\n")
        base_time = datetime.strptime(f"{date}T08:00:00", "%Y-%m-%dT%H:%M:%S")
        # Write bars out of order (should be sorted)
        for i in [5, 2, 8, 1, 9, 3, 7, 4, 6, 0]:
            ts = (base_time + timedelta(minutes=i)).strftime("%Y-%m-%dT%H:%M:%SZ")
            f.write(f"{ts},7000,7005,6995,7002,1000\n")

def _create_large_csv(test_dir, date, bar_count):
    """Create large CSV file."""
    file_path = _get_csv_path(test_dir, "ES", date)
    file_path.parent.mkdir(parents=True, exist_ok=True)
    with open(file_path, 'w') as f:
        f.write("timestamp_utc,open,high,low,close,volume\n")
        base_time = datetime.strptime(f"{date}T08:00:00", "%Y-%m-%dT%H:%M:%S")
        for i in range(bar_count):
            ts = (base_time + timedelta(minutes=i)).strftime("%Y-%m-%dT%H:%M:%SZ")
            price = 7000 + (i % 100) * 0.25
            f.write(f"{ts},{price},{price+5},{price-5},{price+2},1000\n")

def _create_gapped_csv(test_dir, date):
    """Create CSV with gaps in data."""
    file_path = _get_csv_path(test_dir, "ES", date)
    file_path.parent.mkdir(parents=True, exist_ok=True)
    with open(file_path, 'w') as f:
        f.write("timestamp_utc,open,high,low,close,volume\n")
        base_time = datetime.strptime(f"{date}T08:00:00", "%Y-%m-%dT%H:%M:%S")
        # Create gaps: 08:00-08:05, then gap to 08:30-08:35
        for i in range(5):
            ts = (base_time + timedelta(minutes=i)).strftime("%Y-%m-%dT%H:%M:%SZ")
            f.write(f"{ts},7000,7005,6995,7002,1000\n")
        # Gap of 25 minutes
        for i in range(30, 35):
            ts = (base_time + timedelta(minutes=i)).strftime("%Y-%m-%dT%H:%M:%SZ")
            f.write(f"{ts},7000,7005,6995,7002,1000\n")

def _create_future_bars_csv(test_dir, date):
    """Create CSV with future bars."""
    file_path = _get_csv_path(test_dir, "ES", date)
    file_path.parent.mkdir(parents=True, exist_ok=True)
    with open(file_path, 'w') as f:
        f.write("timestamp_utc,open,high,low,close,volume\n")
        base_time = datetime.strptime(f"{date}T08:00:00", "%Y-%m-%dT%H:%M:%S")
        # Add past bars (should be accepted)
        for i in range(10):
            ts = (base_time + timedelta(minutes=i)).strftime("%Y-%m-%dT%H:%M:%SZ")
            f.write(f"{ts},7000,7005,6995,7002,1000\n")
        # Add future bars (should be filtered)
        future_time = datetime.utcnow() + timedelta(hours=2)
        for i in range(5):
            ts = (future_time + timedelta(minutes=i)).strftime("%Y-%m-%dT%H:%M:%SZ")
            f.write(f"{ts},7000,7005,6995,7002,1000\n")

def _create_invalid_timestamps_csv(test_dir, date):
    """Create CSV with invalid timestamps."""
    file_path = _get_csv_path(test_dir, "ES", date)
    file_path.parent.mkdir(parents=True, exist_ok=True)
    with open(file_path, 'w') as f:
        f.write("timestamp_utc,open,high,low,close,volume\n")
        base_time = datetime.strptime(f"{date}T08:00:00", "%Y-%m-%dT%H:%M:%S")
        # Valid bars
        for i in range(5):
            ts = (base_time + timedelta(minutes=i)).strftime("%Y-%m-%dT%H:%M:%SZ")
            f.write(f"{ts},7000,7005,6995,7002,1000\n")
        # Invalid timestamps (should be skipped)
        f.write("invalid-timestamp,7000,7005,6995,7002,1000\n")
        f.write("2026-13-45T25:99:99Z,7000,7005,6995,7002,1000\n")
        f.write(",7000,7005,6995,7002,1000\n")  # Empty timestamp

def _create_invalid_ohlc_csv(test_dir, date):
    """Create CSV with invalid OHLC values."""
    file_path = _get_csv_path(test_dir, "ES", date)
    file_path.parent.mkdir(parents=True, exist_ok=True)
    with open(file_path, 'w') as f:
        f.write("timestamp_utc,open,high,low,close,volume\n")
        base_time = datetime.strptime(f"{date}T08:00:00", "%Y-%m-%dT%H:%M:%S")
        # Valid bars
        for i in range(5):
            ts = (base_time + timedelta(minutes=i)).strftime("%Y-%m-%dT%H:%M:%SZ")
            f.write(f"{ts},7000,7005,6995,7002,1000\n")
        # Invalid OHLC (should be skipped)
        ts = (base_time + timedelta(minutes=10)).strftime("%Y-%m-%dT%H:%M:%SZ")
        f.write(f"{ts},invalid,7005,6995,7002,1000\n")  # Invalid open
        f.write(f"{ts},7000,invalid,6995,7002,1000\n")  # Invalid high
        f.write(f"{ts},7000,7005,invalid,7002,1000\n")  # Invalid low
        f.write(f"{ts},7000,7005,6995,invalid,1000\n")  # Invalid close

def _create_dst_csv(test_dir, date):
    """Create CSV around DST transition (March/April or October/November)."""
    file_path = _get_csv_path(test_dir, "ES", date)
    file_path.parent.mkdir(parents=True, exist_ok=True)
    with open(file_path, 'w') as f:
        f.write("timestamp_utc,open,high,low,close,volume\n")
        # Use a date around DST transition
        dst_date = "2026-03-09" if date < "2026-03-09" else "2026-11-02"
        base_time = datetime.strptime(f"{dst_date}T08:00:00", "%Y-%m-%dT%H:%M:%S")
        for i in range(10):
            ts = (base_time + timedelta(minutes=i)).strftime("%Y-%m-%dT%H:%M:%SZ")
            f.write(f"{ts},7000,7005,6995,7002,1000\n")

def _create_multi_stream_csvs(test_dir, date):
    """Create CSV files for multiple streams."""
    instruments = ["ES", "NQ", "GC"]
    for inst in instruments:
        file_path = _get_csv_path(test_dir, inst, date)
        file_path.parent.mkdir(parents=True, exist_ok=True)
        with open(file_path, 'w') as f:
            f.write("timestamp_utc,open,high,low,close,volume\n")
            base_time = datetime.strptime(f"{date}T08:00:00", "%Y-%m-%dT%H:%M:%S")
            for i in range(10):
                ts = (base_time + timedelta(minutes=i)).strftime("%Y-%m-%dT%H:%M:%SZ")
                price = 7000 if inst == "ES" else (26000 if inst == "NQ" else 4600)
                f.write(f"{ts},{price},{price+5},{price-5},{price+2},1000\n")

def _get_csv_path(test_dir, instrument, date):
    """Get CSV file path for given instrument and date."""
    date_parts = date.split("-")
    year, month = date_parts[0], date_parts[1]
    inst_lower = instrument.lower()
    inst_upper = instrument.upper()
    file_dir = Path(test_dir) / "data" / "raw" / inst_lower / "1m" / year / month
    file_name = f"{inst_upper}_1m_{date}.csv"
    return file_dir / file_name

def run_stress_test(scenario_name, test_date="2026-01-16"):
    """Run a single stress test scenario."""
    print(f"\n{'='*80}")
    print(f"Testing: {scenario_name}")
    print(f"Description: {SCENARIOS[scenario_name]['description']}")
    print(f"{'='*80}")
    
    project_root = Path.cwd()
    
    # Create temporary test directory
    test_dir = Path(tempfile.mkdtemp(prefix="qtsw2_stress_"))
    try:
        # Setup test data
        SCENARIOS[scenario_name]["setup"](test_dir, test_date)
        
        # Create minimal timetable for test
        timetable_path = Path(test_dir) / "data" / "timetable" / "timetable_current.json"
        timetable_path.parent.mkdir(parents=True, exist_ok=True)
        timetable = {
            "as_of": datetime.now().isoformat(),
            "trading_date": test_date,
            "timezone": "America/Chicago",
            "source": "stress_test",
            "streams": [
                {"stream": "ES1", "instrument": "ES", "session": "S1", "slot_time": "08:00", "enabled": True}
            ]
        }
        with open(timetable_path, 'w') as f:
            json.dump(timetable, f, indent=2)
        
        # Copy project structure to test directory
        for dir_name in ["configs"]:
            src_dir = project_root / dir_name
            if src_dir.exists():
                dst_dir = test_dir / dir_name
                shutil.copytree(src_dir, dst_dir, dirs_exist_ok=True)
        
        # Run DRYRUN with QTSW2_PROJECT_ROOT pointing to test directory
        env = os.environ.copy()
        env["QTSW2_PROJECT_ROOT"] = str(test_dir)
        
        try:
            result = subprocess.run(
                [
                    "dotnet", "run",
                    "--project", str(project_root / "modules/robot/harness/Robot.Harness.csproj"),
                    "--mode", "DRYRUN",
                    "--timetable-path", str(timetable_path)
                ],
                cwd=project_root,
                capture_output=True,
                text=True,
                timeout=60,
                env=env
            )
            
            # Check logs for expected events (check all log files)
            log_dir = test_dir / "logs" / "robot"
            events_found = []
            all_events = []
            prehydration_events = []
            
            # Check all log files in the directory
            if log_dir.exists():
                for log_file in log_dir.glob("*.jsonl"):
                    with open(log_file, 'r') as f:
                        for line in f:
                            try:
                                event = json.loads(line.strip())
                                event_type = event.get('event_type') or event.get('event', '')
                                all_events.append(event_type)
                                
                                # Check for pre-hydration related events
                                message = event.get('message', '')
                                level = event.get('level', '')
                                
                                # Check event type
                                if any(keyword in event_type.upper() for keyword in ['PRE_HYDRATION', 'HYDRATION', 'PREHYDRATION']):
                                    prehydration_events.append(f"{event_type} (level: {level})")
                                    events_found.append(event_type)
                                
                                # Check message field (LogHealth uses message field)
                                if any(keyword in message.upper() for keyword in ['PRE_HYDRATION', 'HYDRATION', 'PREHYDRATION', 'ZERO_BARS']):
                                    if event_type not in events_found:
                                        prehydration_events.append(f"{event_type}:{message} (level: {level})")
                                        events_found.append(event_type)
                                
                                # Also check data.payload.message for nested format
                                data = event.get('data', {})
                                if isinstance(data, dict):
                                    payload = data.get('payload', {}) if 'payload' in data else {}
                                    payload_msg = payload.get('message', '') if isinstance(payload, dict) else ''
                                    if payload_msg and any(keyword in payload_msg.upper() for keyword in ['PRE_HYDRATION', 'HYDRATION', 'PREHYDRATION', 'ZERO_BARS']):
                                        if event_type not in events_found:
                                            prehydration_events.append(f"{event_type}:{payload_msg} (level: {level})")
                                            events_found.append(event_type)
                            except:
                                pass
            
            # Print debugging info
            print(f"\nLog directory: {log_dir}")
            print(f"Log files found: {list(log_dir.glob('*.jsonl')) if log_dir.exists() else 'N/A'}")
            if prehydration_events:
                print(f"Pre-hydration events found: {prehydration_events}")
            elif not events_found:
                print(f"\nAll event types found (first 30): {all_events[:30]}")
                print(f"Total events: {len(all_events)}")
            
            # Print subprocess output for debugging
            if result.returncode != 0:
                print(f"\nSubprocess failed with return code {result.returncode}")
                print("STDOUT:", result.stdout[:500] if result.stdout else "(empty)")
                print("STDERR:", result.stderr[:500] if result.stderr else "(empty)")
            
            # Check if any expected event is found (more flexible matching)
            expected_found = any(
                any(exp.lower() in found.lower() for found in events_found)
                for exp in SCENARIOS[scenario_name]['expected']
            )
            
            print(f"\nResult: {'PASS' if expected_found else 'FAIL'}")
            print(f"Events found: {events_found}")
            print(f"Expected (any match): {SCENARIOS[scenario_name]['expected']}")
            
            # Save log directory for manual inspection if test fails
            if not expected_found:
                print(f"\nLog directory saved for inspection: {log_dir}")
                print(f"To inspect: python check_log_events.py \"{log_dir / 'robot_ES.jsonl'}\"")
            
            return events_found
            
        except subprocess.TimeoutExpired:
            print("FAIL: Test timed out")
            return []
        except Exception as e:
            print(f"FAIL: {e}")
            import traceback
            traceback.print_exc()
            return []
    finally:
        # Cleanup test directory
        try:
            shutil.rmtree(test_dir)
        except:
            pass

def main():
    """Run all stress tests."""
    print("="*80)
    print("PRE-HYDRATION STRESS TEST SUITE")
    print("="*80)
    
    if len(sys.argv) > 1:
        # Run specific scenario
        scenario = sys.argv[1]
        if scenario not in SCENARIOS:
            print(f"Error: Unknown scenario '{scenario}'")
            print(f"Available scenarios: {', '.join(SCENARIOS.keys())}")
            return
        run_stress_test(scenario)
    else:
        # Run all scenarios
        results = {}
        for scenario_name in SCENARIOS.keys():
            results[scenario_name] = run_stress_test(scenario_name)
        
        # Summary
        print("\n" + "="*80)
        print("STRESS TEST SUMMARY")
        print("="*80)
        for scenario_name, events in results.items():
            status = "PASS" if events else "FAIL"
            print(f"{scenario_name:30s} {status:10s} {len(events)} events")

if __name__ == "__main__":
    main()
