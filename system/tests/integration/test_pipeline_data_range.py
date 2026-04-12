#!/usr/bin/env python3
"""
Test pipeline run and analyze data ranges
"""
import sys
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

# Try both ports (8000 is standard, 8001 was in test files)
BACKEND_URLS = ["http://localhost:8000", "http://localhost:8001"]

def find_backend():
    """Find which backend port is running"""
    for url in BACKEND_URLS:
        try:
            response = requests.get(f"{url}/", timeout=5)
            if response.status_code == 200:
                return url
        except:
            continue
    return None

def print_section(title):
    print("\n" + "="*70)
    print(f"  {title}")
    print("="*70)

def check_backend():
    """Check if backend is running"""
    print_section("Checking Backend Connection")
    backend_url = find_backend()
    if backend_url:
        print(f"[OK] Backend is running at {backend_url}")
        return backend_url
    else:
        print("[ERROR] Backend is not running!")
        print("Start it with: batch\\START_DASHBOARD.bat")
        return None

def check_pipeline_status(backend_url):
    """Check current pipeline status"""
    try:
        response = requests.get(f"{backend_url}/api/pipeline/status", timeout=10)
        if response.status_code == 200:
            status = response.json()
            print(f"State: {status.get('state', 'unknown')}")
            print(f"Current Stage: {status.get('current_stage', 'unknown')}")
            print(f"Run ID: {status.get('run_id', 'none')}")
            return status
        else:
            print(f"[ERROR] Failed to get status: {response.status_code}")
            return None
    except Exception as e:
        print(f"[ERROR] Failed to check status: {e}")
        return None

def start_pipeline_standalone():
    """Start a pipeline run using standalone runner"""
    print_section("Starting Pipeline Run (Standalone)")
    try:
        import subprocess
        import asyncio
        
        # Use the standalone pipeline runner
        standalone_script = qtsw2_root / "automation" / "run_pipeline_standalone.py"
        
        if not standalone_script.exists():
            print(f"[ERROR] Standalone script not found: {standalone_script}")
            return None
        
        print(f"Running: {standalone_script}")
        print("This will run the full pipeline (translator -> analyzer -> merger)...")
        
        # Run the standalone pipeline
        process = subprocess.Popen(
            [sys.executable, str(standalone_script)],
            cwd=str(qtsw2_root),
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            text=True,
            bufsize=1
        )
        
        # Read output in real-time
        print("\nPipeline output:")
        print("-" * 70)
        for line in process.stdout:
            print(f"  {line.rstrip()}")
        
        process.wait()
        
        if process.returncode == 0:
            print("\n[OK] Pipeline completed successfully")
            return "standalone_run"
        else:
            print(f"\n[WARNING] Pipeline completed with exit code: {process.returncode}")
            return "standalone_run"
            
    except Exception as e:
        print(f"[ERROR] Failed to run pipeline: {e}")
        import traceback
        traceback.print_exc()
        return None

def start_pipeline(backend_url):
    """Start a pipeline run via API (fallback)"""
    print_section("Starting Pipeline Run")
    try:
        # Check if pipeline is already running
        status = check_pipeline_status(backend_url)
        if status and status.get('state') not in ['idle', 'success', 'failed', 'stopped', 'unavailable']:
            print(f"[WARNING] Pipeline is already running (state: {status.get('state')})")
            print("Waiting for it to complete...")
            return status.get('run_id')
        
        # Start pipeline
        print("Sending start request...")
        start_response = requests.post(
            f"{backend_url}/api/pipeline/start",
            json={"manual": True},
            timeout=30
        )
        
        if start_response.status_code == 200:
            data = start_response.json()
            run_id = data.get('run_id')
            print(f"[OK] Pipeline started: {run_id}")
            return run_id
        else:
            error_msg = start_response.text
            print(f"[ERROR] Failed to start pipeline: {start_response.status_code}")
            print(f"  Error: {error_msg}")
            return None
    except Exception as e:
        print(f"[ERROR] Failed to start pipeline: {e}")
        import traceback
        traceback.print_exc()
        return None

def wait_for_completion(backend_url, run_id, timeout=600):
    """Wait for pipeline to complete"""
    print_section("Waiting for Pipeline Completion")
    print(f"Run ID: {run_id}")
    print(f"Timeout: {timeout} seconds ({timeout//60} minutes)")
    print("Monitoring progress...\n")
    
    start_time = time.time()
    last_stage = None
    
    while time.time() - start_time < timeout:
        try:
            status_response = requests.get(f"{backend_url}/api/pipeline/status", timeout=10)
            if status_response.status_code == 200:
                status = status_response.json()
                state = status.get('state', 'unknown')
                stage = status.get('current_stage', 'unknown')
                run_id_current = status.get('run_id', 'none')
                
                # Only print when stage changes
                if stage != last_stage:
                    elapsed = int(time.time() - start_time)
                    print(f"  [{elapsed:4d}s] Stage: {stage:15s} | State: {state}")
                    last_stage = stage
                
                if state in ['success', 'failed', 'stopped']:
                    elapsed = int(time.time() - start_time)
                    print(f"\n[OK] Pipeline completed in {elapsed} seconds")
                    print(f"Final state: {state}")
                    return state == 'success'
            
            time.sleep(5)  # Check every 5 seconds
        except Exception as e:
            print(f"[WARNING] Error checking status: {e}")
            time.sleep(5)
    
    print(f"\n[ERROR] Pipeline did not complete within {timeout} seconds")
    return False

def analyze_data_ranges():
    """Analyze data ranges in all pipeline outputs"""
    print_section("Analyzing Data Ranges")
    
    results = {}
    
    # 1. Raw data files
    print("\n1. RAW DATA FILES (data/raw/)")
    raw_dir = qtsw2_root / "data" / "raw"
    if raw_dir.exists():
        csv_files = list(raw_dir.glob("*.csv"))
        txt_files = list(raw_dir.glob("*.txt"))
        all_files = csv_files + txt_files
        
        if all_files:
            print(f"   Found {len(all_files)} file(s)")
            
            # Get file sizes
            total_size = sum(f.stat().st_size for f in all_files)
            print(f"   Total size: {total_size / (1024*1024):.2f} MB")
            
            # Try to read first few files to get date ranges
            date_ranges = []
            for file in sorted(all_files, key=lambda x: x.stat().st_mtime, reverse=True)[:5]:
                try:
                    # Try to read as CSV
                    df = pd.read_csv(file, nrows=1000)  # Just sample
                    if 'Date' in df.columns or 'date' in df.columns:
                        date_col = 'Date' if 'Date' in df.columns else 'date'
                        dates = pd.to_datetime(df[date_col], errors='coerce').dropna()
                        if len(dates) > 0:
                            date_ranges.append({
                                'file': file.name,
                                'min': dates.min(),
                                'max': dates.max(),
                                'rows_sampled': len(df)
                            })
                except:
                    pass
            
            if date_ranges:
                all_dates = []
                for dr in date_ranges:
                    all_dates.extend([dr['min'], dr['max']])
                if all_dates:
                    print(f"   Date range (sampled): {min(all_dates)} to {max(all_dates)}")
            
            results['raw'] = {
                'file_count': len(all_files),
                'total_size_mb': total_size / (1024*1024),
                'latest_file': sorted(all_files, key=lambda x: x.stat().st_mtime, reverse=True)[0].name
            }
        else:
            print("   [WARNING] No raw data files found")
            results['raw'] = {'file_count': 0}
    else:
        print("   [WARNING] Raw data directory does not exist")
        results['raw'] = {'file_count': 0}
    
    # 2. Processed data files
    print("\n2. PROCESSED DATA FILES (data/processed/)")
    processed_dir = qtsw2_root / "data" / "processed"
    if processed_dir.exists():
        parquet_files = list(processed_dir.glob("*.parquet"))
        
        if parquet_files:
            print(f"   Found {len(parquet_files)} file(s)")
            
            # Get file sizes
            total_size = sum(f.stat().st_size for f in parquet_files)
            print(f"   Total size: {total_size / (1024*1024):.2f} MB")
            
            # Read all files to get date ranges
            all_timestamps = []
            total_rows = 0
            instruments = set()
            
            for file in parquet_files:
                try:
                    df = pd.read_parquet(file)
                    total_rows += len(df)
                    
                    if 'timestamp' in df.columns:
                        df['timestamp'] = pd.to_datetime(df['timestamp'], errors='coerce')
                        timestamps = df['timestamp'].dropna()
                        if len(timestamps) > 0:
                            all_timestamps.extend([timestamps.min(), timestamps.max()])
                    
                    if 'instrument' in df.columns:
                        instruments.update(df['instrument'].unique())
                    
                except Exception as e:
                    print(f"   [ERROR] Failed to read {file.name}: {e}")
            
            if all_timestamps:
                min_ts = min(all_timestamps)
                max_ts = max(all_timestamps)
                print(f"   Date range: {min_ts} to {max_ts}")
                print(f"   Total rows: {total_rows:,}")
                print(f"   Instruments: {sorted(instruments)}")
                
                results['processed'] = {
                    'file_count': len(parquet_files),
                    'total_size_mb': total_size / (1024*1024),
                    'total_rows': total_rows,
                    'date_min': str(min_ts),
                    'date_max': str(max_ts),
                    'instruments': sorted(list(instruments))
                }
            else:
                results['processed'] = {
                    'file_count': len(parquet_files),
                    'total_size_mb': total_size / (1024*1024),
                    'total_rows': total_rows
                }
        else:
            print("   [WARNING] No processed parquet files found")
            results['processed'] = {'file_count': 0}
    else:
        print("   [WARNING] Processed directory does not exist")
        results['processed'] = {'file_count': 0}
    
    # 3. Analyzer output
    print("\n3. ANALYZER OUTPUT (data/analyzer_runs/)")
    analyzer_dir = qtsw2_root / "data" / "analyzer_runs"
    if analyzer_dir.exists():
        # Find all analyzer output files
        analyzer_files = list(analyzer_dir.rglob("*.parquet"))
        
        if analyzer_files:
            print(f"   Found {len(analyzer_files)} file(s)")
            
            # Group by instrument
            by_instrument = {}
            for file in analyzer_files:
                # Try to extract instrument from filename
                name = file.name.upper()
                if 'ES' in name:
                    inst = 'ES'
                elif 'NQ' in name:
                    inst = 'NQ'
                elif 'YM' in name:
                    inst = 'YM'
                else:
                    inst = 'UNKNOWN'
                
                if inst not in by_instrument:
                    by_instrument[inst] = []
                by_instrument[inst].append(file)
            
            # Analyze each instrument
            for inst, files in by_instrument.items():
                # Get latest file for this instrument
                latest_file = max(files, key=lambda x: x.stat().st_mtime)
                print(f"\n   {inst} (latest: {latest_file.name})")
                
                try:
                    df = pd.read_parquet(latest_file)
                    
                    if 'Date' in df.columns:
                        df['Date'] = pd.to_datetime(df['Date'], errors='coerce')
                        dates = df['Date'].dropna()
                        if len(dates) > 0:
                            print(f"      Date range: {dates.min()} to {dates.max()}")
                            print(f"      Total rows: {len(df):,}")
                            print(f"      Unique dates: {dates.nunique()}")
                            
                            if inst not in results:
                                results[inst] = {}
                            results[inst] = {
                                'file': latest_file.name,
                                'date_min': str(dates.min()),
                                'date_max': str(dates.max()),
                                'total_rows': len(df),
                                'unique_dates': dates.nunique()
                            }
                    else:
                        print(f"      [WARNING] No 'Date' column found")
                        print(f"      Columns: {list(df.columns)}")
                        print(f"      Total rows: {len(df):,}")
                        
                except Exception as e:
                    print(f"      [ERROR] Failed to read {latest_file.name}: {e}")
                    import traceback
                    traceback.print_exc()
        else:
            print("   [WARNING] No analyzer output files found")
            results['analyzer'] = {'file_count': 0}
    else:
        print("   [WARNING] Analyzer directory does not exist")
        results['analyzer'] = {'file_count': 0}
    
    # Summary
    print_section("DATA RANGE SUMMARY")
    print(json.dumps(results, indent=2, default=str))
    
    return results

def main():
    print("\n" + "="*70)
    print("  PIPELINE TEST RUN - DATA RANGE ANALYSIS")
    print("="*70)
    print(f"Started at: {datetime.now().strftime('%Y-%m-%d %H:%M:%S')}\n")
    
    # Step 1: Check backend
    backend_url = check_backend()
    if not backend_url:
        return
    
    # Step 2: Check current status
    print_section("Current Pipeline Status")
    status = check_pipeline_status(backend_url)
    
    # Step 3: Start pipeline (try standalone first, then API)
    print("\nAttempting to start pipeline via standalone runner...")
    run_id = start_pipeline_standalone()
    
    if not run_id:
        print("\n[WARNING] Standalone runner failed, trying API...")
        run_id = start_pipeline(backend_url)
        if not run_id:
            print("\n[ERROR] Could not start pipeline. Analyzing existing data...")
            analyze_data_ranges()
            return
        
        # Step 4: Wait for completion (only if using API)
        success = wait_for_completion(backend_url, run_id, timeout=600)  # 10 minute timeout
    else:
        # Standalone runner already completed
        success = True
    
    # Step 5: Analyze data ranges
    results = analyze_data_ranges()
    
    # Final summary
    print_section("TEST COMPLETE")
    if success:
        print("[OK] Pipeline completed successfully")
    else:
        print("[WARNING] Pipeline may not have completed successfully")
    
    print(f"\nCompleted at: {datetime.now().strftime('%Y-%m-%d %H:%M:%S')}")
    print("\nData Range Summary:")
    print(json.dumps(results, indent=2, default=str))

if __name__ == "__main__":
    main()

