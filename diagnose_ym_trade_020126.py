"""
Diagnostic script to analyze YM trade on 02/01/2026 at 09:00
This will help identify why it exited with TIME instead of BE
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

def analyze_trade():
    """Analyze the specific YM trade"""
    
    # Trade details from matrix
    date = pd.Timestamp("2026-02-01", tz="America/Chicago")
    time_label = "09:00"
    entry_time_str = "02/01/26 13:24"  # This is likely 13:24 Chicago time
    instrument = "YM"
    direction = "Long"
    entry_price = 100.00
    stop_loss = 477.00  # Original stop loss
    target = 300.00  # Target points
    exit_price = 82.00
    exit_reason = "TIME"
    
    print(f"Analyzing YM trade:")
    print(f"  Date: {date}")
    print(f"  Time slot: {time_label}")
    print(f"  Entry time: {entry_time_str}")
    print(f"  Entry price: {entry_price}")
    print(f"  Direction: {direction}")
    print(f"  Original stop loss: {stop_loss}")
    print(f"  Target: {target}")
    print(f"  Exit price: {exit_price}")
    print(f"  Exit reason: {exit_reason}")
    print()
    
    # Load data
    data_folder = Path("data/data_processed")
    if not data_folder.exists():
        print(f"ERROR: Data folder not found: {data_folder}")
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
    
    # Parse entry time
    # Entry time format: "02/01/26 13:24" - this is likely 13:24 Chicago time
    entry_time = pd.Timestamp("2026-02-01 13:24:00", tz="America/Chicago")
    
    # Calculate expiry time (next day same slot + 1 minute)
    # Friday trades expire Monday, but this is a Friday trade
    expiry_time = pd.Timestamp("2026-02-02 09:01:00", tz="America/Chicago")  # Next day 09:01
    
    # Calculate T1 threshold (65% of target)
    t1_threshold = target * 0.65  # 195 points
    
    print(f"Trade parameters:")
    print(f"  Entry time: {entry_time}")
    print(f"  Expiry time: {expiry_time}")
    print(f"  T1 threshold: {t1_threshold} points")
    print(f"  Break-even stop loss (if T1 triggered): {entry_price - 1.0} (1 tick below entry for YM)")
    print()
    
    # Get bars after entry
    after_entry = df[df['timestamp'] >= entry_time].copy()
    print(f"Bars after entry: {len(after_entry)}")
    
    if len(after_entry) == 0:
        print("ERROR: No bars after entry time")
        return
    
    # Initialize trackers
    instrument_manager = InstrumentManager()
    config_manager = ConfigManager()
    debug_manager = DebugManager(True)  # Enable debug
    price_tracker = PriceTracker(debug_manager, instrument_manager, config_manager)
    
    # Execute trade with debug enabled
    print("=" * 80)
    print("EXECUTING TRADE WITH DEBUG ENABLED")
    print("=" * 80)
    print()
    
    try:
        result = price_tracker.execute_trade(
            df=df,
            entry_time=entry_time,
            entry_price=entry_price,
            direction=direction,
            target_level=entry_price + target,
            stop_loss=stop_loss,
            expiry_time=expiry_time,
            target_pts=target,
            instrument=instrument,
            time_label=time_label,
            date=date,
            debug=True
        )
        
        print()
        print("=" * 80)
        print("TRADE EXECUTION RESULT")
        print("=" * 80)
        print(f"Exit price: {result.exit_price}")
        print(f"Exit time: {result.exit_time}")
        print(f"Exit reason: {result.exit_reason}")
        print(f"Result classification: {result.result_classification}")
        print(f"T1 triggered: {result.t1_triggered}")
        print(f"Stop loss adjusted: {result.stop_loss_adjusted}")
        print(f"Final stop loss: {result.final_stop_loss}")
        print(f"Target hit: {result.target_hit}")
        print(f"Stop hit: {result.stop_hit}")
        print(f"Time expired: {result.time_expired}")
        print(f"Profit: {result.profit}")
        print(f"Peak: {result.peak}")
        print()
        
        # Analyze what happened
        print("=" * 80)
        print("ANALYSIS")
        print("=" * 80)
        
        if result.t1_triggered:
            be_stop_loss = entry_price - instrument_manager.get_tick_size(instrument)
            print(f"✓ T1 WAS TRIGGERED")
            print(f"  Break-even stop loss was set to: {be_stop_loss}")
            print(f"  Original stop loss: {stop_loss}")
            
            if result.exit_reason == "TIME":
                print()
                print("⚠ ISSUE DETECTED:")
                print("  Trade exited with TIME even though T1 was triggered")
                print("  This means price should have hit break-even stop loss before time expired")
                print()
                
                # Check if break-even stop loss was hit
                bars_after_entry = df[df['timestamp'] >= entry_time].copy()
                bars_before_expiry = bars_after_entry[bars_after_entry['timestamp'] < expiry_time].copy()
                
                print(f"Checking {len(bars_before_expiry)} bars before expiry time:")
                be_hit = False
                for idx, bar in bars_before_expiry.iterrows():
                    low = float(bar['low'])
                    if low <= be_stop_loss:
                        be_hit = True
                        print(f"  ✓ Break-even stop loss HIT at {bar['timestamp']}")
                        print(f"    Bar low: {low}, BE stop: {be_stop_loss}")
                        break
                
                if not be_hit:
                    print(f"  ✗ Break-even stop loss NOT HIT before expiry")
                    print(f"    Checking if price got close...")
                    min_low = bars_before_expiry['low'].min()
                    print(f"    Minimum low before expiry: {min_low}")
                    print(f"    Break-even stop loss: {be_stop_loss}")
                    print(f"    Difference: {min_low - be_stop_loss}")
        else:
            print("✗ T1 WAS NOT TRIGGERED")
            print("  This explains why it exited with TIME instead of BE")
        
    except Exception as e:
        print(f"ERROR executing trade: {e}")
        import traceback
        traceback.print_exc()

if __name__ == "__main__":
    analyze_trade()
