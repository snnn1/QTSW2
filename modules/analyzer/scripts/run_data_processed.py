
#!/usr/bin/env python

import argparse
import pandas as pd
import pathlib
import sys
import os
import datetime

# Add the parent directory to the path so we can import breakout_core
sys.path.append(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

# Add QTSW2 root to path so we can import modules.analyzer.validation
# Script is at modules/analyzer/scripts/run_data_processed.py, so go up 3 levels to QTSW2 root
# Level 1: scripts -> analyzer, Level 2: analyzer -> modules, Level 3: modules -> QTSW2
script_dir = os.path.dirname(os.path.abspath(__file__))  # modules/analyzer/scripts
analyzer_dir = os.path.dirname(script_dir)  # modules/analyzer
modules_dir = os.path.dirname(analyzer_dir)  # modules
qtsw2_root = os.path.dirname(modules_dir)  # QTSW2 root
if qtsw2_root not in sys.path:
    sys.path.insert(0, qtsw2_root)

from logic.config_logic import RunParams, ConfigManager
from breakout_core.engine import run_strategy

def parse_slots(args_slots, sessions):
    # args_slots like ["S1:07:30","S1:08:00","S2:09:30"]
    # Use ConfigManager for slot ends instead of breakout_core.config
    config_manager = ConfigManager()
    enabled = {s: [] for s in ["S1","S2"]}
    for s in sessions:
        enabled.setdefault(s, [])
    if not args_slots:
        # If no slots specified, use all slots from config
        for sess in sessions:
            enabled[sess] = config_manager.get_slot_ends(sess)
        return enabled
    for token in args_slots:
        if ":" not in token:
            continue
        sess, end = token.split(":",1)
        sess = sess.strip().upper()
        if sess not in ["S1","S2"]:
            continue
        enabled.setdefault(sess, [])
        enabled[sess].append(end.strip())
    # ensure validity vs ConfigManager slot ends
    for sess in list(enabled.keys()):
        valid_slots = config_manager.get_slot_ends(sess)
        enabled[sess] = [e for e in enabled[sess] if e in valid_slots]
    return enabled

def load_folder(folder: str, instrument: str = None) -> pd.DataFrame:
    p = pathlib.Path(folder)
    if not p.exists() or not p.is_dir():
        raise SystemExit(f"Folder not found: {folder}")
    parts = []
    # Recursively search for parquet files in subdirectories
    # Translator outputs files in structure like: {folder}/{instrument}/1m/YYYY/MM/{instrument}_1m_{date}.parquet
    print(f"[LOAD] Starting file discovery for instrument: {instrument}", file=sys.stderr, flush=True)
    if instrument:
        # Filter by instrument BEFORE loading - much faster and uses less memory
        # Look for files in {folder}/{instrument}/1m/... or files matching {instrument}_*.parquet
        instrument_upper = instrument.upper()
        parquet_files = []
        # First try instrument subdirectory structure
        instrument_dir = p / instrument_upper / "1m"
        print(f"[LOAD] Checking directory: {instrument_dir}", file=sys.stderr, flush=True)
        if instrument_dir.exists():
            print(f"[LOAD] Directory exists, scanning for parquet files...", file=sys.stderr, flush=True)
            # OPTIMIZED: Iterate year/month structure instead of rglob (much faster)
            # Structure: {instrument}/1m/{year}/{month}/{file}.parquet
            parquet_files = []
            year_dirs = sorted([d for d in instrument_dir.iterdir() if d.is_dir() and d.name.isdigit() and len(d.name) == 4])
            print(f"[LOAD] Found {len(year_dirs)} year directories", file=sys.stderr, flush=True)
            for year_dir in year_dirs:
                month_dirs = sorted([d for d in year_dir.iterdir() if d.is_dir()])
                for month_dir in month_dirs:
                    files = sorted(month_dir.glob("*.parquet"))
                    parquet_files.extend(files)
                    if len(parquet_files) % 500 == 0:
                        print(f"[LOAD] Found {len(parquet_files)} files so far...", file=sys.stderr, flush=True)
            print(f"[LOAD] Found {len(parquet_files)} files total in {instrument_dir}", file=sys.stderr, flush=True)
        # Also check for files matching instrument pattern in root or subdirs
        if not parquet_files:
            print(f"[LOAD] No files in subdirectory, searching for {instrument_upper}_*.parquet pattern...", file=sys.stderr, flush=True)
            all_files = list(p.rglob(f"{instrument_upper}_*.parquet"))
            all_files.sort()
            parquet_files = [f for f in all_files if instrument_upper in f.name]
            print(f"[LOAD] Found {len(parquet_files)} files matching pattern", file=sys.stderr, flush=True)
    else:
        # Load all files if no instrument specified (backward compatibility)
        print(f"[LOAD] No instrument specified, loading all parquet files...", file=sys.stderr, flush=True)
        parquet_files = list(p.rglob("*.parquet"))
        parquet_files.sort()
    
    if not parquet_files:
        raise SystemExit(f"No CSV/Parquet files found under {folder}")
    
    print(f"Found {len(parquet_files)} parquet file(s) to load", flush=True)
    
    total_size_mb = 0
    # Show progress every 100 files to avoid slowing down with thousands of files
    progress_interval = max(1, len(parquet_files) // 100) if len(parquet_files) > 100 else 1
    
    for i, f in enumerate(parquet_files, 1):
        file_size_mb = f.stat().st_size / (1024 * 1024)
        total_size_mb += file_size_mb
        
        # Only print progress every N files to avoid slowdown
        if i % progress_interval == 0 or i == 1 or i == len(parquet_files):
            print(f"Loading file {i}/{len(parquet_files)} ({i*100//len(parquet_files)}%): {f.name} ({file_size_mb:.1f} MB)", flush=True)
        
        try:
            df = pd.read_parquet(f)
            if i % progress_interval == 0 or i == 1 or i == len(parquet_files):
                print(f"  Loaded {len(df):,} rows from {f.name}", flush=True)
            parts.append(df)
        except Exception as e:
            print(f"  ERROR loading {f.name}: {e}", flush=True)
            raise
    
    print(f"Total data size: {total_size_mb:.1f} MB across {len(parquet_files)} file(s)")
    print(f"Concatenating {len(parts)} dataframes...")
    
    try:
        df = pd.concat(parts, ignore_index=True)
        print(f"Concatenated dataframe: {len(df):,} rows, {len(df.columns)} columns")
        print(f"Memory usage: {df.memory_usage(deep=True).sum() / (1024**2):.1f} MB")
    except MemoryError as e:
        print(f"ERROR: Out of memory while concatenating dataframes")
        print(f"  Total rows across files: {sum(len(p) for p in parts):,}")
        print(f"  Total size: {total_size_mb:.1f} MB")
        raise SystemExit(f"Memory error: {e}. Consider processing files in smaller batches.")
    except Exception as e:
        print(f"ERROR concatenating dataframes: {e}")
        raise
    # Ensure required columns
    req = {"timestamp","open","high","low","close","instrument"}
    missing = req - set(df.columns)
    if missing:
        raise SystemExit(f"Missing required columns: {sorted(missing)}")
    # Data is already in correct timezone from NinjaTrader export with bar time fix
    
    return df

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--folder", required=True, help="Path to data_processed folder")
    ap.add_argument("--instrument", required=True, choices=["ES","NQ","YM","CL","NG","GC","RTY","MES","MNQ","MYM","MCL","MNG","MGC","M2K","MINUTEDATAEXPORT"])
    ap.add_argument("--sessions", nargs="*", default=["S1","S2"], choices=["S1","S2"])
    ap.add_argument("--slots", nargs="*", default=[], help="Tokens like S1:07:30 S1:08:00 S2:09:30 ...")
    ap.add_argument("--days", nargs="*", default=["Mon","Tue","Wed","Thu","Fri"], choices=["Mon","Tue","Wed","Thu","Fri"])
    ap.add_argument("--priority", default="STOP_FIRST", choices=["STOP_FIRST","TP_FIRST"])
    ap.add_argument("--write-setup", action="store_true")
    ap.add_argument("--no-write-notrade", action="store_true", help="Disable NoTrade entries (default: enabled)")
    ap.add_argument("--out", default=None, help="Output parquet path (default data/analyzer_runs/breakout_<instrument>_<details>.parquet)")
    ap.add_argument("--debug", action="store_true", help="Enable debug output")
    args = ap.parse_args()

    df = load_folder(args.folder, instrument=args.instrument)
    
    # Debug output
    if args.debug:
        print(f"Loaded {len(df)} rows")
        print(f"Date range: {df['timestamp'].min()} to {df['timestamp'].max()}")
        print(f"Price range: {df['low'].min()} to {df['high'].max()}")
    
    # Always show instrument availability for diagnostics
    available_instruments = df['instrument'].str.upper().unique() if 'instrument' in df.columns else []
    print(f"Available instruments in data: {', '.join(sorted(available_instruments))}")
    print(f"Requested instrument: {args.instrument.upper()}")
    
    # Check if requested instrument exists in data
    instrument_data = df[df['instrument'].str.upper() == args.instrument.upper()]
    print(f"Rows matching {args.instrument}: {len(instrument_data)}")
    if len(instrument_data) > 0:
        print(f"  Date range: {instrument_data['timestamp'].min()} to {instrument_data['timestamp'].max()}")

    day_map = {"Mon":0,"Tue":1,"Wed":2,"Thu":3,"Fri":4}
    rp = RunParams(
        instrument=args.instrument,
        enabled_sessions=args.sessions,
        enabled_slots=parse_slots(args.slots, args.sessions),
        trade_days=[day_map[d] for d in args.days],
        same_bar_priority=args.priority,
        write_setup_rows=args.write_setup,
        write_no_trade_rows=not args.no_write_notrade  # Default to True, disable with --no-write-notrade
    )


    if args.debug:
        print(f"Running strategy with params: {rp}")
        print(f"Data shape: {df.shape}")
        print(f"ES data shape: {df[df['instrument'].str.upper() == 'ES'].shape}")
    
    # Additional diagnostics before running strategy
    if len(instrument_data) == 0:
        print(f"ERROR: No data found for instrument {args.instrument.upper()}")
        print(f"  Available instruments: {', '.join(sorted(available_instruments))}")
        print(f"  Total rows loaded: {len(df)}")
        if 'instrument' in df.columns:
            print(f"  Instrument column sample values: {df['instrument'].str.upper().unique()[:10]}")
        sys.exit(1)  # Exit with error code when no data found
    
    # Check date range and trading days
    if len(instrument_data) > 0:
        date_range_start = instrument_data['timestamp'].min()
        date_range_end = instrument_data['timestamp'].max()
        print(f"Data details for {args.instrument}:")
        print(f"  Total rows: {len(instrument_data)}")
        print(f"  Date range: {date_range_start} to {date_range_end}")
        
        # Check what days of week are in the data
        if 'timestamp' in instrument_data.columns:
            instrument_data_copy = instrument_data.copy()
            instrument_data_copy['day_of_week'] = pd.to_datetime(instrument_data_copy['timestamp']).dt.dayofweek
            days_in_data = instrument_data_copy['day_of_week'].unique()
            day_names = {0: 'Mon', 1: 'Tue', 2: 'Wed', 3: 'Thu', 4: 'Fri', 5: 'Sat', 6: 'Sun'}
            days_found = [day_names.get(d, 'Unknown') for d in sorted(days_in_data)]
            print(f"  Days of week in data: {', '.join(days_found)}")
            print(f"  Requested trade days: {', '.join(args.days)}")
            
            # Check if requested days match data
            requested_day_nums = [day_map[d] for d in args.days]
            matching_days = set(days_in_data) & set(requested_day_nums)
            if not matching_days:
                print(f"  WARNING: No data for requested trade days!")
    
    print(f"\n{'='*60}")
    print(f"Starting strategy execution for {args.instrument}")
    print(f"  Data rows: {len(df):,}")
    print(f"  Instrument rows: {len(instrument_data):,}")
    print(f"  Sessions: {', '.join(args.sessions)}")
    print(f"  Time slots: {len([s for slots in rp.enabled_slots.values() for s in slots])} total")
    print(f"{'='*60}\n")
    
    import time
    strategy_start_time = time.time()
    
    try:
        res = run_strategy(df, rp, debug=args.debug)
        strategy_elapsed = time.time() - strategy_start_time
        
        print(f"\n{'='*60}")
        print(f"Strategy execution completed in {strategy_elapsed:.1f} seconds ({strategy_elapsed/60:.1f} minutes)")
        print(f"  Results generated: {len(res)}")
        print(f"{'='*60}\n")
        
        if args.debug:
            print(f"Strategy returned {len(res)} results")
    except MemoryError as e:
        print(f"\n{'='*60}")
        print(f"ERROR: Out of memory during strategy execution")
        print(f"  Data rows: {len(df):,}")
        print(f"  Memory usage: {df.memory_usage(deep=True).sum() / (1024**2):.1f} MB")
        print(f"  Error: {e}")
        print(f"{'='*60}\n")
        raise SystemExit(f"Memory error during strategy execution: {e}")
    except Exception as e:
        print(f"\n{'='*60}")
        print(f"ERROR during strategy execution: {e}")
        import traceback
        traceback.print_exc()
        print(f"{'='*60}\n")
        raise

    # Handle empty results
    if len(res) == 0:
        print(f"\n{'='*60}")
        print(f"Warning: No results generated for {args.instrument}")
        print(f"{'='*60}")
        print(f"Data Availability:")
        print(f"  Available instruments in data: {', '.join(sorted(available_instruments))}")
        print(f"  Rows matching {args.instrument}: {len(instrument_data)}")
        if len(instrument_data) > 0:
            print(f"  Data date range: {instrument_data['timestamp'].min()} to {instrument_data['timestamp'].max()}")
        print(f"\nStrategy Parameters:")
        print(f"  Sessions: {', '.join(args.sessions)}")
        print(f"  Time Slots: {', '.join(args.slots) if args.slots else 'Default (all slots)'}")
        print(f"  Target: Base level only")
        print(f"  Trade Days: {', '.join(args.days)}")
        print(f"\nPossible Causes:")
        print("  - No data matching the instrument filter")
        print("  - No valid ranges found for the specified sessions/time slots")
        print("  - Data date range doesn't contain valid trading days")
        print("  - Strategy parameters don't match available data")
        print("  - No breakouts detected within the specified parameters")
        print(f"{'='*60}\n")
        
        # Still create an empty output file for consistency
        if args.out:
            out_path = args.out
        else:
            # Determine output folder: manual runs go to manual_analyzer_runs, automatic runs go to analyzer_temp
            today = datetime.datetime.now().strftime("%Y-%m-%d")
            is_pipeline_run = os.getenv("PIPELINE_RUN", "0") == "1"
            if is_pipeline_run:
                # Automatic pipeline run - use analyzer_temp (for data merger)
                analyzer_temp_dir = pathlib.Path(f"data/analyzer_temp/{today}")
            else:
                # Manual run - use manual_analyzer_runs folder
                analyzer_temp_dir = pathlib.Path(f"data/manual_analyzer_runs/{today}")
            analyzer_temp_dir.mkdir(parents=True, exist_ok=True)
            
            timestamp = datetime.datetime.now().strftime("%Y%m%d_%H%M%S")
            out_path = analyzer_temp_dir / f"breakout_{args.instrument}_no_results_{timestamp}.parquet"
        
        pathlib.Path(out_path).parent.mkdir(parents=True, exist_ok=True)
        
        # Validate empty DataFrame (validation allows empty DataFrames)
        from modules.analyzer.validation import validate_before_write
        try:
            validate_before_write(res, out_path)
        except ValueError as e:
            raise ValueError(f"Analyzer output validation failed: {e}") from e
        
        # Save empty DataFrame with correct schema
        res.to_parquet(out_path, index=False, compression='snappy')
        print(f"Empty results saved to {out_path}")
        return

    # Calculate statistics
    date_range = f"{res['Date'].min()}_to_{res['Date'].max()}"
    total_trades = len(res)
    win_rate = (len(res[res['Result'] == 'Win']) / len(res[res['Result'].isin(['Win', 'Loss', 'BE'])]) * 100) if len(res[res['Result'].isin(['Win', 'Loss', 'BE'])]) > 0 else 0
    total_profit = res['Profit'].sum()
    
    # Create descriptive output filename
    if args.out:
        out_path = args.out
    else:
        # Determine output folder: manual runs go to manual_analyzer_runs, automatic runs go to analyzer_temp
        today = datetime.datetime.now().strftime("%Y-%m-%d")
        is_pipeline_run = os.getenv("PIPELINE_RUN", "0") == "1"
        if is_pipeline_run:
            # Automatic pipeline run - use analyzer_temp (for data merger)
            analyzer_temp_dir = pathlib.Path(f"data/analyzer_temp/{today}")
        else:
            # Manual run - use manual_analyzer_runs folder
            analyzer_temp_dir = pathlib.Path(f"data/manual_analyzer_runs/{today}")
        analyzer_temp_dir.mkdir(parents=True, exist_ok=True)
        
        # Create descriptive filename like the GUI app
        desc_filename = f"breakout_{args.instrument}_{date_range}_{total_trades}trades_{win_rate:.0f}winrate_{total_profit:.0f}profit"
        out_path = analyzer_temp_dir / f"{desc_filename}.parquet"
    
    pathlib.Path(out_path).parent.mkdir(parents=True, exist_ok=True)
    
    # Validate analyzer output before write (Invariant 1 & 2: Valid dates, datetime dtype)
    from modules.analyzer.validation import validate_before_write
    try:
        validate_before_write(res, out_path)
    except ValueError as e:
        raise ValueError(f"Analyzer output validation failed: {e}") from e
    
    # Save as parquet file (for sequential processor compatibility)
    res.to_parquet(out_path, index=False, compression='snappy')
    
    print(f"Wrote {len(res)} rows to {out_path}")
    

if __name__ == "__main__":
    main()
