"""
Check Latest Pipeline Run Data Dates - Show date ranges of data processed in latest pipeline run
"""

import sys
from pathlib import Path
import json
from datetime import datetime
import pandas as pd

# Add project root to path
qtsw2_root = Path(__file__).parent.parent
if str(qtsw2_root) not in sys.path:
    sys.path.insert(0, str(qtsw2_root))

def get_date_range_from_file(file_path):
    """Extract date range from a parquet file"""
    try:
        df = pd.read_parquet(file_path)
        
        # Try different date column names
        date_col = None
        for col_name in ['Date', 'date', 'timestamp', 'Timestamp']:
            if col_name in df.columns:
                date_col = col_name
                break
        
        if date_col is None:
            return None, None
        
        if date_col in ['Date', 'date']:
            # Date column is likely datetime or date
            min_date = pd.to_datetime(df[date_col]).min()
            max_date = pd.to_datetime(df[date_col]).max()
        else:  # timestamp
            min_date = pd.to_datetime(df[date_col]).min()
            max_date = pd.to_datetime(df[date_col]).max()
        
        return min_date, max_date
    except Exception as e:
        return None, None

def get_latest_files_by_instrument(data_dir, instrument_pattern="*", file_pattern="*.parquet", max_files=10):
    """Get latest files by instrument from a directory"""
    files_by_instrument = {}
    
    if not data_dir.exists():
        return files_by_instrument
    
    for file_path in data_dir.rglob(file_pattern):
        # Extract instrument from path (e.g., CL, ES, etc.)
        parts = file_path.parts
        instrument = None
        for part in parts:
            if part in ['CL', 'ES', 'GC', 'NG', 'NQ', 'YM']:
                instrument = part
                break
        
        if instrument is None:
            # Try to extract from filename
            name_parts = file_path.stem.split('_')
            if len(name_parts) > 0:
                instrument = name_parts[0]
        
        if instrument:
            if instrument not in files_by_instrument:
                files_by_instrument[instrument] = []
            files_by_instrument[instrument].append(file_path)
    
    # Sort by modification time and take latest
    for instrument in files_by_instrument:
        files_by_instrument[instrument].sort(key=lambda p: p.stat().st_mtime, reverse=True)
        files_by_instrument[instrument] = files_by_instrument[instrument][:max_files]
    
    return files_by_instrument

def main():
    print("="*80)
    print("LATEST PIPELINE RUN - DATA DATE ANALYSIS")
    print("="*80)
    
    events_dir = qtsw2_root / "automation" / "logs" / "events"
    
    if not events_dir.exists():
        print(f"[ERROR] Events directory not found: {events_dir}")
        return
    
    # Find most recent pipeline log
    pipeline_files = sorted(events_dir.glob("pipeline_*.jsonl"), key=lambda p: p.stat().st_mtime, reverse=True)
    
    if not pipeline_files:
        print("[ERROR] No pipeline log files found")
        return
    
    latest_file = pipeline_files[0]
    mtime = datetime.fromtimestamp(latest_file.stat().st_mtime)
    
    print(f"\n[PIPELINE RUN INFO]")
    print(f"  Log File: {latest_file.name}")
    print(f"  Last Modified: {mtime.strftime('%Y-%m-%d %H:%M:%S')}")
    
    # Read pipeline events to get instruments processed
    events = []
    try:
        with open(latest_file, 'r', encoding='utf-8') as f:
            for line in f:
                line = line.strip()
                if line:
                    try:
                        event = json.loads(line)
                        events.append(event)
                    except:
                        pass
    except Exception as e:
        print(f"[ERROR] Failed to read log file: {e}")
        return
    
    # Extract instruments from analyzer events
    instruments_processed = set()
    analyzer_events = [e for e in events if e.get('stage') == 'analyzer']
    for event in analyzer_events:
        data = event.get('data', {})
        if 'instrument' in data:
            instruments_processed.add(data['instrument'])
        if 'instruments' in data:
            for inst in data['instruments']:
                instruments_processed.add(inst)
    
    if not instruments_processed:
        # Try to infer from file_start events
        for event in analyzer_events:
            if event.get('event') == 'file_start':
                inst = event.get('data', {}).get('instrument', '')
                if inst:
                    instruments_processed.add(inst)
    
    print(f"  Instruments Processed: {', '.join(sorted(instruments_processed)) if instruments_processed else 'Unknown'}")
    
    # Track latest dates across all instruments
    all_latest_dates = []
    
    # Check translated data (input to analyzer)
    print(f"\n[TRANSLATED DATA (Input to Analyzer)]")
    translated_dir = qtsw2_root / "data" / "translated"
    
    if translated_dir.exists():
        # Get latest translated files
        latest_translated_files = {}
        for inst in instruments_processed if instruments_processed else ['CL', 'ES', 'GC', 'NG', 'NQ', 'YM']:
            inst_dir = translated_dir / inst / "1m"
            if inst_dir.exists():
                # Find latest files
                latest_files = sorted(inst_dir.rglob("*.parquet"), key=lambda p: p.stat().st_mtime, reverse=True)
                if latest_files:
                    latest_translated_files[inst] = latest_files[:5]  # Top 5 latest
        
        if latest_translated_files:
            for inst, files in sorted(latest_translated_files.items()):
                print(f"\n  {inst}:")
                dates_found = []
                for file_path in files:
                    mtime = datetime.fromtimestamp(file_path.stat().st_mtime)
                    # Try to extract date from filename (format: INST_1m_YYYY-MM-DD.parquet)
                    file_date = None
                    name_parts = file_path.stem.split('_')
                    if len(name_parts) >= 3:
                        try:
                            file_date = datetime.strptime(name_parts[-1], '%Y-%m-%d').date()
                        except:
                            pass
                    
                    if file_date:
                        dates_found.append(file_date)
                        print(f"    {file_path.name}: {file_date} (file modified: {mtime.strftime('%Y-%m-%d %H:%M:%S')})")
                    else:
                        # Try reading the file
                        min_date, max_date = get_date_range_from_file(file_path)
                        if min_date and max_date:
                            dates_found.append(min_date.date() if hasattr(min_date, 'date') else min_date)
                            dates_found.append(max_date.date() if hasattr(max_date, 'date') else max_date)
                            print(f"    {file_path.name}: {min_date.date()} to {max_date.date()} (file modified: {mtime.strftime('%Y-%m-%d %H:%M:%S')})")
                        else:
                            print(f"    {file_path.name}: (file modified: {mtime.strftime('%Y-%m-%d %H:%M:%S')})")
                
                if dates_found:
                    latest_date = max(dates_found)
                    earliest_date = min(dates_found)
                    all_latest_dates.append(latest_date)
                    print(f"    Latest data date: {latest_date}")
                    if len(dates_found) > 1:
                        print(f"    Date range in sample: {earliest_date} to {latest_date}")
        else:
            print("  No recent translated files found")
    else:
        print("  Translated directory not found")
    
    # Check analyzed data (output from analyzer)
    print(f"\n[ANALYZED DATA (Output from Analyzer)]")
    analyzed_dir = qtsw2_root / "data" / "analyzed"
    
    if analyzed_dir.exists():
        latest_analyzed_files = {}
        for inst in instruments_processed if instruments_processed else ['CL', 'ES', 'GC', 'NG', 'NQ', 'YM']:
            # Check both INST1 and INST2 variants
            for variant in [f"{inst}1", f"{inst}2"]:
                variant_dir = analyzed_dir / variant / "2025"
                if variant_dir.exists():
                    # Find latest month files (format: INST_an_YYYY_MM.parquet)
                    latest_files = sorted(variant_dir.glob("*.parquet"), key=lambda p: p.stat().st_mtime, reverse=True)
                    if latest_files:
                        if inst not in latest_analyzed_files:
                            latest_analyzed_files[inst] = []
                        latest_analyzed_files[inst].extend(latest_files[:3])  # Latest 3 months
        
        if latest_analyzed_files:
            for inst, files in sorted(latest_analyzed_files.items()):
                print(f"\n  {inst}:")
                # Sort files by modification time
                files.sort(key=lambda p: p.stat().st_mtime, reverse=True)
                
                all_min_dates = []
                all_max_dates = []
                
                for file_path in files[:3]:  # Show latest 3
                    mtime = datetime.fromtimestamp(file_path.stat().st_mtime)
                    
                    # Extract month from filename
                    month_str = None
                    name_parts = file_path.stem.split('_')
                    if len(name_parts) >= 4:
                        month_str = name_parts[-1]  # e.g., "12" for December
                    
                    min_date, max_date = get_date_range_from_file(file_path)
                    
                    if min_date and max_date:
                        min_date_obj = min_date.date() if hasattr(min_date, 'date') else min_date
                        max_date_obj = max_date.date() if hasattr(max_date, 'date') else max_date
                        all_min_dates.append(min_date_obj)
                        all_max_dates.append(max_date_obj)
                        
                        month_info = f" (Month: {month_str})" if month_str else ""
                        print(f"    {file_path.name}{month_info}:")
                        print(f"      Date range: {min_date_obj} to {max_date_obj}")
                        print(f"      File modified: {mtime.strftime('%Y-%m-%d %H:%M:%S')}")
                    else:
                        month_info = f" (Month: {month_str})" if month_str else ""
                        print(f"    {file_path.name}{month_info}: (file modified: {mtime.strftime('%Y-%m-%d %H:%M:%S')})")
                
                if all_min_dates and all_max_dates:
                    overall_min = min(all_min_dates)
                    overall_max = max(all_max_dates)
                    all_latest_dates.append(overall_max)
                    print(f"    Overall date range: {overall_min} to {overall_max}")
                    print(f"    Latest data date: {overall_max}")
        else:
            print("  No recent analyzed files found")
    else:
        print("  Analyzed directory not found")
    
    # Summary
    if all_latest_dates:
        overall_latest = max(all_latest_dates)
        print(f"\n[SUMMARY]")
        print(f"  Latest data date processed: {overall_latest}")
        print(f"  Pipeline run date: {mtime.strftime('%Y-%m-%d')}")
        days_behind = (mtime.date() - overall_latest).days if hasattr(mtime, 'date') else 0
        if days_behind >= 0:
            print(f"  Data is {days_behind} day(s) behind pipeline run date")
        else:
            print(f"  Data appears to be from the future (check timezone settings)")
    
    print("\n" + "="*80)

if __name__ == "__main__":
    main()

