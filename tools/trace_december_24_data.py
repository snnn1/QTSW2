"""
Trace December 24th Data - Follow the data through the pipeline
"""

import sys
import json
from pathlib import Path
from datetime import datetime
import pandas as pd

# Add project root to path
qtsw2_root = Path(__file__).parent.parent
if str(qtsw2_root) not in sys.path:
    sys.path.insert(0, str(qtsw2_root))

def main():
    today = "2025-12-24"
    print("="*80)
    print(f"TRACING DECEMBER 24TH DATA - {today}")
    print("="*80)
    
    # Step 1: Check raw files
    print(f"\n[1. RAW FILES]")
    raw_dir = qtsw2_root / "data" / "raw"
    raw_files = list(raw_dir.rglob(f'*{today}*.csv'))
    print(f"  Found {len(raw_files)} raw file(s) for {today}")
    if raw_files:
        for f in sorted(raw_files)[:3]:
            mtime = datetime.fromtimestamp(f.stat().st_mtime)
            size_kb = f.stat().st_size / 1024
            print(f"    {f.name}: {size_kb:.2f} KB, modified {mtime.strftime('%Y-%m-%d %H:%M:%S')}")
    
    # Step 2: Check translated files
    print(f"\n[2. TRANSLATED FILES]")
    translated_dir = qtsw2_root / "data" / "translated"
    translated_files = list(translated_dir.rglob(f'*{today}*.parquet'))
    print(f"  Found {len(translated_files)} translated file(s) for {today}")
    if translated_files:
        for f in sorted(translated_files)[:3]:
            mtime = datetime.fromtimestamp(f.stat().st_mtime)
            size_kb = f.stat().st_size / 1024
            print(f"    {f.name}: {size_kb:.2f} KB, modified {mtime.strftime('%Y-%m-%d %H:%M:%S')}")
            
            # Check if file has data
            try:
                df = pd.read_parquet(f)
                print(f"      Rows: {len(df)}")
                if 'Date' in df.columns:
                    dates = df['Date'].dt.date.unique()
                    print(f"      Dates in file: {sorted(dates)}")
            except Exception as e:
                print(f"      [ERROR] Could not read file: {e}")
    
    # Step 3: Check analyzer temp (should be empty if processed)
    print(f"\n[3. ANALYZER TEMP]")
    analyzer_temp = qtsw2_root / "data" / "analyzer_temp" / today
    if analyzer_temp.exists():
        temp_files = list(analyzer_temp.glob("*.parquet"))
        print(f"  Analyzer temp folder exists: {analyzer_temp}")
        print(f"  Files in temp: {len(temp_files)}")
        if temp_files:
            print(f"    [WARNING] Files still in temp - may not have been merged yet")
            for f in sorted(temp_files)[:3]:
                print(f"      {f.name}")
    else:
        print(f"  Analyzer temp folder does not exist (processed and cleaned up)")
    
    # Step 4: Check analyzed output
    print(f"\n[4. ANALYZED OUTPUT]")
    analyzed_dir = qtsw2_root / "data" / "analyzed"
    
    # Check ES1 (Session 1) December file
    es1_file = analyzed_dir / "ES1" / "2025" / "ES1_an_2025_12.parquet"
    if es1_file.exists():
        mtime = datetime.fromtimestamp(es1_file.stat().st_mtime)
        print(f"  ES1 December file: {es1_file.name}")
        print(f"    Last modified: {mtime.strftime('%Y-%m-%d %H:%M:%S')}")
        
        try:
            df = pd.read_parquet(es1_file)
            print(f"    Total rows: {len(df)}")
            if 'Date' in df.columns:
                dates = sorted(df['Date'].dt.date.unique())
                print(f"    Date range: {dates[0]} to {dates[-1]}")
                print(f"    Unique dates: {len(dates)}")
                
                if today in [str(d) for d in dates]:
                    print(f"    [OK] December 24th data IS in the file")
                    dec24_rows = df[df['Date'].dt.date == datetime.strptime(today, '%Y-%m-%d').date()]
                    print(f"    December 24th rows: {len(dec24_rows)}")
                    if len(dec24_rows) > 0:
                        print(f"    Sample December 24th data:")
                        print(dec24_rows[['Date', 'Time', 'Instrument', 'Session', 'Result', 'Profit']].head(5).to_string())
                else:
                    print(f"    [WARNING] December 24th data is NOT in the file")
                    print(f"    Latest date in file: {dates[-1]}")
        except Exception as e:
            print(f"    [ERROR] Could not read analyzed file: {e}")
    else:
        print(f"  [ERROR] ES1 December file not found: {es1_file}")
    
    # Step 5: Check merger processed log
    print(f"\n[5. MERGER PROCESSED LOG]")
    merger_log = qtsw2_root / "data" / "merger_processed.json"
    if merger_log.exists():
        with open(merger_log, 'r') as f:
            data = json.load(f)
        analyzer_processed = data.get("analyzer", [])
        today_folder = f"{qtsw2_root}\\data\\analyzer_temp\\{today}"
        today_folder_alt = f"{qtsw2_root}/data/analyzer_temp/{today}"
        
        if today_folder in analyzer_processed or today_folder_alt in analyzer_processed:
            print(f"  [OK] December 24th folder has been processed by merger")
        else:
            print(f"  [WARNING] December 24th folder NOT in processed log")
            print(f"  Looking for: {today_folder}")
            print(f"  Processed folders: {len(analyzer_processed)}")
            if analyzer_processed:
                print(f"  Latest processed: {analyzer_processed[-1] if analyzer_processed else 'N/A'}")
    else:
        print(f"  Merger log file not found")
    
    # Step 6: Check latest pipeline run
    print(f"\n[6. LATEST PIPELINE RUN]")
    events_dir = qtsw2_root / "automation" / "logs" / "events"
    pipeline_files = sorted(events_dir.glob("pipeline_*.jsonl"), key=lambda p: p.stat().st_mtime, reverse=True)
    if pipeline_files:
        latest_file = pipeline_files[0]
        mtime = datetime.fromtimestamp(latest_file.stat().st_mtime)
        print(f"  Latest run: {latest_file.name}")
        print(f"  Last modified: {mtime.strftime('%Y-%m-%d %H:%M:%S')}")
        
        # Check if today's date appears in events
        with open(latest_file, 'r') as f:
            events = [json.loads(l) for l in f if l.strip()]
        
        # Look for analyzer events mentioning today
        analyzer_events = [e for e in events if e.get('stage') == 'analyzer']
        print(f"  Analyzer events: {len(analyzer_events)}")
        
        # Check merger events
        merger_events = [e for e in events if e.get('stage') == 'merger']
        print(f"  Merger events: {len(merger_events)}")
        
        # Check if merger processed today
        merger_logs = [e.get('msg', '') for e in merger_events if 'log' in e.get('event', '')]
        if any(today in msg for msg in merger_logs):
            print(f"  [OK] Merger processed {today} data")
        else:
            print(f"  [INFO] No explicit mention of {today} in merger events")

if __name__ == "__main__":
    main()

