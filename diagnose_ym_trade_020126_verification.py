"""
Diagnostic script to verify YM trade on 02/01/2026 at 09:00
Checks if break-even stop loss detection fix is working correctly
"""

import pandas as pd
import sys
from pathlib import Path

# Add modules to path
sys.path.insert(0, str(Path(__file__).parent))

from modules.analyzer.logic.price_tracking_logic import PriceTracker
from modules.analyzer.logic.debug_logic import DebugManager
from modules.analyzer.logic.instrument_logic import InstrumentManager
from modules.analyzer.logic.config_logic import ConfigManager

def verify_trade():
    """Verify the specific YM trade"""
    
    # Trade details from matrix
    date = pd.Timestamp("2026-02-01", tz="America/Chicago")
    time_label = "09:00"
    entry_time_str = "02/01/26 13:24"  # 13:24 Chicago time
    instrument = "YM"
    direction = "Long"
    
    print(f"Verifying YM trade:")
    print(f"  Date: {date}")
    print(f"  Time slot: {time_label}")
    print(f"  Entry time: {entry_time_str}")
    print(f"  Instrument: {instrument}")
    print(f"  Direction: {direction}")
    print()
    
    # Load data
    data_folder = Path("data/data_processed")
    if not data_folder.exists():
        print(f"ERROR: Data folder not found: {data_folder}")
        print(f"Looking for: {data_folder.absolute()}")
        return
    
    # Find YM parquet files
    ym_files = list(data_folder.glob("YM*.parquet"))
    if not ym_files:
        print(f"ERROR: No YM parquet files found in {data_folder}")
        return
    
    print(f"Found {len(ym_files)} YM parquet files")
    
    # Load data
    dfs = []
    for file in ym_files:
        try:
            df = pd.read_parquet(file)
            if 'instrument' in df.columns:
                df = df[df['instrument'].str.upper() == 'YM']
            if not df.empty:
                dfs.append(df)
        except Exception as e:
            print(f"Warning: Could not load {file}: {e}")
    
    if not dfs:
        print("ERROR: No YM data loaded")
        return
    
    df = pd.concat(dfs, ignore_index=True)
    print(f"Loaded {len(df)} rows of YM data")
    
    # Filter to date range around 02/01/2026
    df['timestamp'] = pd.to_datetime(df['timestamp'])
    if df['timestamp'].dt.tz is None:
        df['timestamp'] = df['timestamp'].dt.tz_localize('America/Chicago')
    else:
        df['timestamp'] = df['timestamp'].dt.tz_convert('America/Chicago')
    
    # Filter to around the trade date
    start_date = pd.Timestamp("2026-01-31", tz="America/Chicago")
    end_date = pd.Timestamp("2026-02-03", tz="America/Chicago")
    df = df[(df['timestamp'] >= start_date) & (df['timestamp'] <= end_date)]
    
    print(f"Filtered to {len(df)} rows around trade date")
    print(f"Date range: {df['timestamp'].min()} to {df['timestamp'].max()}")
    print()
    
    # Parse entry time (13:24 Chicago time on 02/01/2026)
    entry_time = pd.Timestamp("2026-02-01 13:24:00", tz="America/Chicago")
    
    # Calculate expiry time (next day same slot + 1 minute)
    # Friday trades expire Monday
    expiry_time = pd.Timestamp("2026-02-02 09:01:00", tz="America/Chicago")
    
    # We need to find the actual trade parameters from analyzer output
    # For now, let's check if we can find analyzer output for this trade
    analyzer_runs = Path("data/analyzed/YM1/2026")
    if analyzer_runs.exists():
        print(f"Checking analyzer output in: {analyzer_runs}")
        parquet_files = list(analyzer_runs.glob("*.parquet"))
        print(f"Found {len(parquet_files)} analyzer output files")
        
        # Try to find the trade in analyzer output
        for file in parquet_files:
            try:
                analyzer_df = pd.read_parquet(file)
                # Filter for the specific trade
                trade_match = analyzer_df[
                    (analyzer_df['Date'] == '2026-02-01') & 
                    (analyzer_df['Time'] == '09:00')
                ]
                if not trade_match.empty:
                    print(f"\nFound trade in analyzer output:")
                    print(trade_match[['Date', 'Time', 'EntryTime', 'ExitTime', 'Result', 'Profit', 'Entry', 'Exit', 'StopLoss']].to_string())
                    print()
                    
                    # Get actual trade parameters
                    entry_price = float(trade_match.iloc[0]['Entry'])
                    stop_loss = float(trade_match.iloc[0]['StopLoss'])
                    target_pts = abs(float(trade_match.iloc[0]['Target']) - entry_price) if 'Target' in trade_match.columns else 10.0
                    exit_price = float(trade_match.iloc[0]['Exit'])
                    result = str(trade_match.iloc[0]['Result'])
                    
                    print(f"Trade parameters from analyzer:")
                    print(f"  Entry price: {entry_price}")
                    print(f"  Stop loss: {stop_loss}")
                    print(f"  Target points: {target_pts}")
                    print(f"  Exit price: {exit_price}")
                    print(f"  Result: {result}")
                    print()
                    
                    # Calculate T1 threshold
                    t1_threshold = target_pts * 0.65
                    print(f"T1 threshold: {t1_threshold} points")
                    
                    # Calculate break-even stop loss (if T1 triggered)
                    instrument_manager = InstrumentManager()
                    tick_size = instrument_manager.get_tick_size("YM")
                    be_stop_loss = entry_price - tick_size
                    print(f"Break-even stop loss (if T1 triggered): {be_stop_loss}")
                    print()
                    
                    # Check if break-even stop was hit in the data
                    bars_after_entry = df[df['timestamp'] >= entry_time].copy()
                    bars_before_expiry = bars_after_entry[bars_after_entry['timestamp'] <= expiry_time].copy()
                    
                    print(f"Checking {len(bars_before_expiry)} bars between entry and expiry:")
                    be_hit = False
                    be_hit_time = None
                    
                    for idx, bar in bars_before_expiry.iterrows():
                        low = float(bar['low'])
                        if low <= be_stop_loss:
                            be_hit = True
                            be_hit_time = bar['timestamp']
                            print(f"  ✓ Break-even stop loss HIT at {be_hit_time}")
                            print(f"    Bar low: {low}, BE stop: {be_stop_loss}")
                            break
                    
                    if not be_hit:
                        print(f"  ✗ Break-even stop loss NOT HIT before expiry")
                        min_low = bars_before_expiry['low'].min()
                        print(f"    Minimum low before expiry: {min_low}")
                        print(f"    Break-even stop loss: {be_stop_loss}")
                        print(f"    Difference: {min_low - be_stop_loss}")
                    
                    # Now re-run the trade execution with debug enabled
                    print("\n" + "="*80)
                    print("RE-RUNNING TRADE EXECUTION WITH FIX")
                    print("="*80)
                    print()
                    
                    debug_manager = DebugManager(True)
                    config_manager = ConfigManager()
                    price_tracker = PriceTracker(debug_manager, instrument_manager, config_manager)
                    
                    target_level = entry_price + target_pts
                    
                    result_new = price_tracker.execute_trade(
                        df=df,
                        entry_time=entry_time,
                        entry_price=entry_price,
                        direction=direction,
                        target_level=target_level,
                        stop_loss=stop_loss,
                        expiry_time=expiry_time,
                        target_pts=target_pts,
                        instrument=instrument,
                        time_label=time_label,
                        date=date,
                        debug=True
                    )
                    
                    print("\n" + "="*80)
                    print("NEW TRADE EXECUTION RESULT")
                    print("="*80)
                    print(f"Exit price: {result_new.exit_price}")
                    print(f"Exit time: {result_new.exit_time}")
                    print(f"Exit reason: {result_new.exit_reason}")
                    print(f"Result classification: {result_new.result_classification}")
                    print(f"T1 triggered: {result_new.t1_triggered}")
                    print(f"Stop loss adjusted: {result_new.stop_loss_adjusted}")
                    print(f"Final stop loss: {result_new.final_stop_loss}")
                    print(f"Profit: {result_new.profit}")
                    print()
                    
                    if result_new.exit_reason == "BE" and result == "TIME":
                        print("✓ FIX WORKING: Trade now exits with BE instead of TIME")
                    elif result_new.exit_reason == "TIME" and be_hit:
                        print("⚠ ISSUE: Break-even stop was hit but trade still exits with TIME")
                        print("  This suggests the fix may not be working correctly")
                    elif result_new.exit_reason == "TIME" and not be_hit:
                        print("✓ CORRECT: Break-even stop was NOT hit, TIME exit is correct")
                    
                    break
            except Exception as e:
                print(f"Error reading {file}: {e}")
                import traceback
                traceback.print_exc()
                continue
    
    else:
        print(f"Analyzer output directory not found: {analyzer_runs}")
        print("Cannot verify trade without analyzer output")

if __name__ == "__main__":
    verify_trade()
