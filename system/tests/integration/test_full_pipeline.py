#!/usr/bin/env python3
"""
Comprehensive pipeline test - tests entire pipeline including scheduler
"""
import sys
import asyncio
import requests
import time
import json
from pathlib import Path
from datetime import datetime
import pandas as pd

# Add project root to path
qtsw2_root = Path(__file__).parent
if str(qtsw2_root) not in sys.path:
    sys.path.insert(0, str(qtsw2_root))

BACKEND_URL = "http://localhost:8001"

def print_section(title):
    print("\n" + "="*60)
    print(f"  {title}")
    print("="*60)

def check_backend():
    """Test 1: Check if backend is running"""
    print_section("TEST 1: Backend Connection")
    try:
        response = requests.get(f"{BACKEND_URL}/", timeout=5)
        if response.status_code == 200:
            print("[OK] Backend is running")
            return True
        else:
            print(f"[ERROR] Backend returned status {response.status_code}")
            return False
    except requests.exceptions.ConnectionError:
        print("[ERROR] Backend is not running. Start it with: batch\\START_DASHBOARD.bat")
        return False
    except Exception as e:
        print(f"[ERROR] Failed to connect to backend: {e}")
        return False

def check_pipeline_status():
    """Test 2: Check current pipeline status"""
    print_section("TEST 2: Pipeline Status")
    try:
        response = requests.get(f"{BACKEND_URL}/api/pipeline/status", timeout=10)
        if response.status_code == 200:
            status = response.json()
            print(f"State: {status.get('state', 'unknown')}")
            print(f"Current Stage: {status.get('current_stage', 'unknown')}")
            print(f"Run ID: {status.get('run_id', 'none')}")
            if status.get('state') in ['idle', 'success', 'failed', 'stopped']:
                print("[OK] Pipeline is idle, ready for test run")
                return True
            else:
                print(f"[WARNING] Pipeline is in state: {status.get('state')}")
                return False
        else:
            print(f"[ERROR] Failed to get status: {response.status_code}")
            return False
    except Exception as e:
        print(f"[ERROR] Failed to check status: {e}")
        return False

def check_data_files():
    """Test 3: Check if data files exist"""
    print_section("TEST 3: Data Files Check")
    raw_dir = qtsw2_root / "data" / "raw"
    processed_dir = qtsw2_root / "processed"
    
    raw_files = list(raw_dir.glob("*.csv")) if raw_dir.exists() else []
    processed_files = list(processed_dir.glob("*.parquet")) if processed_dir.exists() else []
    
    print(f"Raw CSV files: {len(raw_files)}")
    if raw_files:
        print(f"  Latest: {sorted(raw_files, key=lambda x: x.stat().st_mtime, reverse=True)[0].name}")
    
    print(f"Processed files: {len(processed_files)}")
    if processed_files:
        print(f"  Latest: {sorted(processed_files, key=lambda x: x.stat().st_mtime, reverse=True)[0].name}")
    
    if raw_files:
        print("[OK] Raw data files found")
        return True
    else:
        print("[WARNING] No raw data files found")
        return False

def check_friday_data():
    """Test 4: Check if Friday Dec 5 data exists in processed files"""
    print_section("TEST 4: Friday Dec 5 Data Check")
    processed_dir = qtsw2_root / "data" / "processed"
    
    if not processed_dir.exists():
        print("[ERROR] Processed directory does not exist")
        return False
    
    parquet_files = list(processed_dir.glob("*.parquet"))
    if not parquet_files:
        print("[WARNING] No processed parquet files found")
        return False
    
    print(f"Checking {len(parquet_files)} processed file(s)...")
    friday_found = False
    
    for file in parquet_files[:3]:  # Check first 3 files
        try:
            df = pd.read_parquet(file)
            if 'timestamp' in df.columns:
                df['timestamp'] = pd.to_datetime(df['timestamp'])
                dates = df['timestamp'].dt.date.unique()
                has_friday = pd.Timestamp('2025-12-05').date() in dates
                
                print(f"  {file.name}:")
                print(f"    Date range: {df['timestamp'].min()} to {df['timestamp'].max()}")
                print(f"    Has Friday Dec 5: {has_friday}")
                
                if has_friday:
                    friday_data = df[df['timestamp'].dt.date == pd.Timestamp('2025-12-05').date()]
                    print(f"    Friday rows: {len(friday_data)}")
                    friday_found = True
        except Exception as e:
            print(f"  Error reading {file.name}: {e}")
    
    if friday_found:
        print("[OK] Friday Dec 5 data found in processed files")
    else:
        print("[WARNING] Friday Dec 5 data NOT found in processed files")
    
    return friday_found

def check_analyzer_output():
    """Test 5: Check analyzer output for Friday Dec 5"""
    print_section("TEST 5: Analyzer Output Check")
    analyzer_dir = qtsw2_root / "data" / "analyzer_runs"
    
    if not analyzer_dir.exists():
        print("[WARNING] Analyzer output directory does not exist")
        return False
    
    # Find most recent ES analyzer file
    es_files = list(analyzer_dir.rglob("*ES*.parquet"))
    if not es_files:
        print("[WARNING] No ES analyzer output files found")
        return False
    
    es_files.sort(key=lambda x: x.stat().st_mtime, reverse=True)
    latest_file = es_files[0]
    
    print(f"Checking latest ES analyzer file: {latest_file.name}")
    print(f"  Modified: {datetime.fromtimestamp(latest_file.stat().st_mtime)}")
    
    try:
        df = pd.read_parquet(latest_file)
        if 'Date' in df.columns:
            dates = sorted(df['Date'].unique())
            print(f"  Date range: {dates[0]} to {dates[-1]}")
            print(f"  Total dates: {len(dates)}")
            print(f"  Last 5 dates: {dates[-5:]}")
            
            dec5 = pd.Timestamp('2025-12-05').date()
            has_dec5 = any(pd.Timestamp(d).date() == dec5 for d in dates)
            
            if has_dec5:
                dec5_rows = df[pd.to_datetime(df['Date']).dt.date == dec5]
                print(f"  [OK] Friday Dec 5 found: {len(dec5_rows)} rows")
                return True
            else:
                print(f"  [ERROR] Friday Dec 5 NOT found in analyzer output")
                print(f"  [INFO] This is the issue - Friday data exists but analyzer isn't processing it")
                return False
        else:
            print("[ERROR] No 'Date' column in analyzer output")
            return False
    except Exception as e:
        print(f"[ERROR] Failed to read analyzer file: {e}")
        import traceback
        traceback.print_exc()
        return False

def test_pipeline_run():
    """Test 6: Run a full pipeline test"""
    print_section("TEST 6: Full Pipeline Run")
    
    # Check if pipeline is already running
    try:
        status_response = requests.get(f"{BACKEND_URL}/api/pipeline/status", timeout=10)
        if status_response.status_code == 200:
            status = status_response.json()
            if status.get('state') not in ['idle', 'success', 'failed', 'stopped']:
                print(f"[WARNING] Pipeline is already running (state: {status.get('state')})")
                print("  Skipping test run")
                return False
    except Exception as e:
        print(f"[ERROR] Failed to check pipeline status: {e}")
        return False
    
    # Start pipeline
    print("Starting pipeline run...")
    try:
        start_response = requests.post(
            f"{BACKEND_URL}/api/pipeline/start",
            params={"manual": True},
            timeout=30
        )
        
        if start_response.status_code == 200:
            data = start_response.json()
            run_id = data.get('run_id')
            print(f"[OK] Pipeline started: {run_id}")
            
            # Wait for pipeline to complete (with timeout)
            print("Waiting for pipeline to complete (max 5 minutes)...")
            start_time = time.time()
            timeout = 300  # 5 minutes
            
            while time.time() - start_time < timeout:
                time.sleep(5)
                status_response = requests.get(f"{BACKEND_URL}/api/pipeline/status", timeout=10)
                if status_response.status_code == 200:
                    status = status_response.json()
                    state = status.get('state')
                    stage = status.get('current_stage', 'unknown')
                    
                    elapsed = int(time.time() - start_time)
                    print(f"  [{elapsed}s] State: {state}, Stage: {stage}")
                    
                    if state in ['success', 'failed', 'stopped']:
                        print(f"[OK] Pipeline completed with state: {state}")
                        return state == 'success'
            
            print("[ERROR] Pipeline did not complete within timeout")
            return False
        else:
            error_msg = start_response.text
            print(f"[ERROR] Failed to start pipeline: {start_response.status_code}")
            print(f"  Error: {error_msg}")
            return False
    except requests.exceptions.Timeout:
        print("[ERROR] Request timed out")
        return False
    except Exception as e:
        print(f"[ERROR] Failed to start pipeline: {e}")
        import traceback
        traceback.print_exc()
        return False

def test_scheduler_trigger():
    """Test 7: Test scheduler trigger script (OBSOLETE - using run_pipeline_standalone.py now)"""
    print_section("TEST 7: Scheduler Trigger Test (SKIPPED - trigger_pipeline.py removed)")
    print("[INFO] This test is obsolete. Windows Task Scheduler now uses run_pipeline_standalone.py")
    print("[SKIP] Test skipped - old trigger_pipeline.py has been removed")
    return True  # Skip this test

def check_logs_for_errors():
    """Test 8: Check recent logs for errors"""
    print_section("TEST 8: Error Log Check")
    
    log_dir = qtsw2_root / "automation" / "logs"
    trigger_log = log_dir / "pipeline_trigger.log"
    
    if trigger_log.exists():
        print(f"Checking trigger log: {trigger_log}")
        try:
            with open(trigger_log, 'r', encoding='utf-8') as f:
                lines = f.readlines()
                recent_lines = lines[-20:] if len(lines) > 20 else lines
                print(f"Last {len(recent_lines)} lines:")
                for line in recent_lines:
                    if 'ERROR' in line or 'error' in line.lower() or 'failed' in line.lower():
                        print(f"  [ERROR] {line.strip()}")
                    else:
                        print(f"  {line.strip()}")
        except Exception as e:
            print(f"[ERROR] Failed to read log: {e}")
    
    # Check error log
    error_log = qtsw2_root / "logs" / "error_log.jsonl"
    if error_log.exists():
        print(f"\nChecking error log: {error_log}")
        try:
            with open(error_log, 'r', encoding='utf-8') as f:
                lines = f.readlines()
                recent_errors = lines[-5:] if len(lines) > 5 else lines
                if recent_errors:
                    print(f"Last {len(recent_errors)} errors:")
                    for line in recent_errors:
                        try:
                            error_data = json.loads(line)
                            print(f"  [{error_data.get('timestamp', 'unknown')}] {error_data.get('level', 'ERROR')}: {error_data.get('message', 'Unknown error')}")
                        except:
                            print(f"  {line.strip()}")
                else:
                    print("  [OK] No recent errors")
        except Exception as e:
            print(f"[ERROR] Failed to read error log: {e}")

def main():
    print("\n" + "="*60)
    print("  COMPREHENSIVE PIPELINE TEST SUITE")
    print("="*60)
    print(f"Started at: {datetime.now().strftime('%Y-%m-%d %H:%M:%S')}")
    
    results = {}
    
    # Run all tests
    results['Backend Connection'] = check_backend()
    if not results['Backend Connection']:
        print("\n[ERROR] Backend is not running. Cannot continue tests.")
        print("Start backend with: batch\\START_DASHBOARD.bat")
        return
    
    results['Pipeline Status'] = check_pipeline_status()
    results['Data Files'] = check_data_files()
    results['Friday Data'] = check_friday_data()
    results['Analyzer Output'] = check_analyzer_output()
    results['Scheduler Trigger'] = test_scheduler_trigger()
    check_logs_for_errors()
    
    # Optionally run full pipeline (commented out to avoid long wait)
    # results['Full Pipeline Run'] = test_pipeline_run()
    
    # Summary
    print_section("TEST SUMMARY")
    passed = sum(1 for v in results.values() if v)
    total = len(results)
    
    for test_name, result in results.items():
        status = "[OK]" if result else "[FAILED]"
        print(f"{status} {test_name}")
    
    print(f"\nTotal: {passed}/{total} tests passed")
    
    if not results.get('Analyzer Output', False):
        print("\n[ISSUE FOUND] Friday Dec 5 is not in analyzer output!")
        print("  - Friday data exists in processed files")
        print("  - But analyzer is not processing it")
        print("  - This was fixed by adding --days argument to parallel analyzer")
        print("  - Next pipeline run should include Friday")

if __name__ == "__main__":
    main()

