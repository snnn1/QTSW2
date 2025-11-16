
#!/usr/bin/env python

import argparse
import pandas as pd
import pathlib
import sys
import os
import datetime

# Add the parent directory to the path so we can import breakout_core
sys.path.append(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from logic.config_logic import RunParams
from breakout_core.config import SLOT_ENDS
from breakout_core.engine import run_strategy

def parse_slots(args_slots, sessions):
    # args_slots like ["S1:07:30","S1:08:00","S2:09:30"]
    enabled = {s: [] for s in ["S1","S2"]}
    for s in sessions:
        enabled.setdefault(s, [])
    if not args_slots:
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
    # ensure validity vs SLOT_ENDS
    for sess in list(enabled.keys()):
        enabled[sess] = [e for e in enabled[sess] if e in SLOT_ENDS[sess]]
    return enabled

def load_folder(folder: str) -> pd.DataFrame:
    p = pathlib.Path(folder)
    if not p.exists() or not p.is_dir():
        raise SystemExit(f"Folder not found: {folder}")
    parts = []
    for f in sorted(p.rglob("*")):
        if f.suffix.lower() == ".parquet":
            df = pd.read_parquet(f)
            parts.append(df)
    if not parts:
        raise SystemExit(f"No CSV/Parquet files found under {folder}")
    df = pd.concat(parts, ignore_index=True)
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
    ap.add_argument("--instrument", required=True, choices=["ES","NQ","YM","CL","NG","GC","MES","MNQ","MYM","MCL","MNG","MGC","MINUTEDATAEXPORT"])
    ap.add_argument("--sessions", nargs="*", default=["S1","S2"], choices=["S1","S2"])
    ap.add_argument("--slots", nargs="*", default=[], help="Tokens like S1:07:30 S1:08:00 S2:09:30 ...")
    ap.add_argument("--levels", nargs="*", type=int, default=[1], help="Levels 1..7 (L1..L7)")
    ap.add_argument("--days", nargs="*", default=["Mon","Tue","Wed","Thu","Fri"], choices=["Mon","Tue","Wed","Thu","Fri"])
    ap.add_argument("--priority", default="STOP_FIRST", choices=["STOP_FIRST","TP_FIRST"])
    ap.add_argument("--write-setup", action="store_true")
    ap.add_argument("--no-write-notrade", action="store_true", help="Disable NoTrade entries (default: enabled)")
    ap.add_argument("--out", default=None, help="Output parquet path (default data/analyzer_runs/breakout_<instrument>_<details>.parquet)")
    ap.add_argument("--debug", action="store_true", help="Enable debug output")
    args = ap.parse_args()

    df = load_folder(args.folder)
    
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
    
    res = run_strategy(df, rp, debug=args.debug)
    
    if args.debug:
        print(f"Strategy returned {len(res)} results")

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
            timestamp = datetime.datetime.now().strftime("%Y%m%d_%H%M%S")
            out_path = f"data/analyzer_runs/breakout_{args.instrument}_no_results_{timestamp}.parquet"
        
        pathlib.Path(out_path).parent.mkdir(parents=True, exist_ok=True)
        
        # Save empty DataFrame with correct schema
        res.to_parquet(out_path, index=False, compression='snappy')
        print(f"Empty results saved to {out_path}")
        
        # Create summary file in summaries subfolder
        summaries_dir = pathlib.Path("data/analyzer_runs/summaries")
        summaries_dir.mkdir(parents=True, exist_ok=True)
        summary_filename = pathlib.Path(out_path).stem + "_SUMMARY.txt"
        summary_path = summaries_dir / summary_filename
        with open(summary_path, 'w') as f:
            f.write("BREAKOUT ANALYZER RESULTS SUMMARY\n")
            f.write("=" * 50 + "\n\n")
            f.write(f"Run Information:\n")
            f.write(f"  Generated: {datetime.datetime.now().strftime('%Y-%m-%d %H:%M:%S')}\n")
            f.write(f"  Instrument: {args.instrument}\n")
            f.write(f"  Sessions: {', '.join(args.sessions)}\n")
            f.write(f"  Time Slots: {', '.join(args.slots) if args.slots else 'Default'}\n")
            f.write(f"  Target: Base level only\n")
            f.write(f"  Trade Days: {', '.join(args.days)}\n")
            f.write(f"\nPerformance Summary:\n")
            f.write(f"  Total Trades: 0\n")
            f.write(f"  Status: No results generated\n")
            f.write(f"  Note: Check data availability and strategy parameters\n")
        
        print(f"Summary saved to {summary_path}")
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
        # Create descriptive filename like the GUI app
        desc_filename = f"breakout_{args.instrument}_{date_range}_{total_trades}trades_{win_rate:.0f}winrate_{total_profit:.0f}profit"
        out_path = f"data/analyzer_runs/{desc_filename}.parquet"
    
    pathlib.Path(out_path).parent.mkdir(parents=True, exist_ok=True)
    
    # Save as parquet file (for sequential processor compatibility)
    res.to_parquet(out_path, index=False, compression='snappy')
    
    print(f"Wrote {len(res)} rows to {out_path}")
    
    # Also create a summary file in summaries subfolder
    summaries_dir = pathlib.Path("data/analyzer_runs/summaries")
    summaries_dir.mkdir(parents=True, exist_ok=True)
    summary_filename = pathlib.Path(out_path).stem + "_SUMMARY.txt"
    summary_path = summaries_dir / summary_filename
    with open(summary_path, 'w') as f:
        f.write("BREAKOUT ANALYZER RESULTS SUMMARY\n")
        f.write("=" * 50 + "\n\n")
        f.write(f"Run Information:\n")
        f.write(f"  Generated: {datetime.datetime.now().strftime('%Y-%m-%d %H:%M:%S')}\n")
        f.write(f"  Instrument: {args.instrument}\n")
        f.write(f"  Date Range: {res['Date'].min()} to {res['Date'].max()}\n")
        f.write(f"  Sessions: {', '.join(args.sessions)}\n")
        f.write(f"  Time Slots: {', '.join(args.slots) if args.slots else 'Default'}\n")
        f.write(f"  Target: Base level only\n")
        f.write(f"  Trade Days: {', '.join(args.days)}\n")
        
        f.write(f"Performance Summary:\n")
        f.write(f"  Total Trades: {len(res):,}\n")
        f.write(f"  Wins: {len(res[res['Result'] == 'Win']):,}\n")
        f.write(f"  Losses: {len(res[res['Result'] == 'Loss']):,}\n")
        f.write(f"  Break-Even: {len(res[res['Result'] == 'BE']):,}\n")
        f.write(f"  No Trades: {len(res[res['Result'] == 'NoTrade']):,}\n")
        f.write(f"  Win Rate: {win_rate:.1f}%\n")
        f.write(f"  Total Profit: ${total_profit:,.2f}\n")
        if total_trades > 0:
            f.write(f"  Average Profit per Trade: ${total_profit/total_trades:,.2f}\n")
        else:
            f.write(f"  Average Profit per Trade: N/A (no trades)\n")
    
    print(f"Summary saved to {summary_path}")
    

if __name__ == "__main__":
    main()
