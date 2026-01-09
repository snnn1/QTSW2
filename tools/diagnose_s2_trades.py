#!/usr/bin/env python3
"""
Diagnostic tool for S2 trades not being taken by the analyzer

This script checks:
1. Data availability for today
2. S2 range calculation
3. Entry detection for S2 slots
4. Configuration issues
5. Recent analyzer output
"""

import sys
import pandas as pd
from pathlib import Path
from datetime import datetime, timedelta
from typing import Dict, List, Optional

# Add project root to path
qtsw2_root = Path(__file__).parent.parent
if str(qtsw2_root) not in sys.path:
    sys.path.insert(0, str(qtsw2_root))

from modules.analyzer.logic.config_logic import ConfigManager
from modules.analyzer.logic.range_logic import RangeDetector, RangeResult
from modules.analyzer.logic.entry_logic import EntryDetector
from modules.analyzer.logic.instrument_logic import InstrumentManager
from modules.analyzer.logic.utility_logic import UtilityManager

def print_section(title: str):
    print("\n" + "="*80)
    print(title)
    print("="*80)

def print_info(label: str, value: str):
    print(f"  {label}: {value}")

def check_data_availability(data_folder: Path, instrument: str = "ES") -> Optional[pd.DataFrame]:
    """Check if data is available for today"""
    print_section("DATA AVAILABILITY CHECK")
    
    if not data_folder.exists():
        print_info("Data folder", f"NOT FOUND: {data_folder}")
        return None
    
    print_info("Data folder", str(data_folder))
    
    # Find today's date
    today = datetime.now().date()
    print_info("Today's date", str(today))
    
    # Look for parquet files
    parquet_files = list(data_folder.rglob("*.parquet"))
    print_info("Total parquet files", str(len(parquet_files)))
    
    if not parquet_files:
        print_info("Status", "NO DATA FILES FOUND")
        return None
    
    # Try to load data for the instrument
    instrument_upper = instrument.upper()
    instrument_dir = data_folder / instrument_upper / "1m"
    
    if instrument_dir.exists():
        print_info("Instrument directory", f"Found: {instrument_dir}")
        # Find files for current year/month
        year = today.year
        month = today.month
        month_dir = instrument_dir / str(year) / f"{month:02d}"
        
        if month_dir.exists():
            files = list(month_dir.glob("*.parquet"))
            print_info(f"Files in {year}/{month:02d}", str(len(files)))
            
            # Try to load today's file
            today_file = month_dir / f"{instrument_upper}_1m_{today}.parquet"
            if today_file.exists():
                print_info("Today's file", f"Found: {today_file.name}")
                try:
                    df = pd.read_parquet(today_file)
                    print_info("Rows loaded", str(len(df)))
                    if len(df) > 0:
                        print_info("Date range", f"{df['timestamp'].min()} to {df['timestamp'].max()}")
                    return df
                except Exception as e:
                    print_info("Error loading file", str(e))
                    return None
            else:
                print_info("Today's file", "NOT FOUND")
                # Try to load most recent file
                if files:
                    latest_file = max(files, key=lambda p: p.stat().st_mtime)
                    print_info("Latest file", f"{latest_file.name}")
                    try:
                        df = pd.read_parquet(latest_file)
                        print_info("Rows loaded", str(len(df)))
                        if len(df) > 0:
                            print_info("Date range", f"{df['timestamp'].min()} to {df['timestamp'].max()}")
                        return df
                    except Exception as e:
                        print_info("Error loading file", str(e))
                        return None
        else:
            print_info(f"Month directory {year}/{month:02d}", "NOT FOUND")
    else:
        print_info("Instrument directory", f"NOT FOUND: {instrument_dir}")
    
    return None

def check_s2_configuration():
    """Check S2 configuration"""
    print_section("S2 CONFIGURATION CHECK")
    
    config_manager = ConfigManager()
    
    # Check session start times
    s1_start = config_manager.get_slot_start("S1")
    s2_start = config_manager.get_slot_start("S2")
    print_info("S1 start time", s1_start)
    print_info("S2 start time", s2_start)
    
    # Check slot end times
    slot_ends = config_manager.get_slot_ends("S2")
    print_info("S2 slot ends", ", ".join(slot_ends))
    
    # Check market close time
    market_close = config_manager.get_market_close_time()
    print_info("Market close time", market_close)
    
    slot_starts = {"S1": s1_start, "S2": s2_start}
    return slot_starts, slot_ends, market_close

def check_s2_ranges(df: pd.DataFrame, instrument: str = "ES", date: Optional[pd.Timestamp] = None):
    """Check if S2 ranges are being calculated correctly"""
    print_section("S2 RANGE CALCULATION CHECK")
    
    if df is None or len(df) == 0:
        print_info("Status", "NO DATA AVAILABLE")
        return None
    
    # Filter by instrument
    instrument_data = df[df['instrument'].str.upper() == instrument.upper()].copy()
    if len(instrument_data) == 0:
        print_info("Status", f"NO DATA FOR INSTRUMENT {instrument}")
        return None
    
    print_info("Instrument rows", str(len(instrument_data)))
    
    # Use today's date or most recent date
    if date is None:
        date = pd.Timestamp(instrument_data['timestamp'].max()).normalize()
    
    print_info("Checking date", str(date.date()))
    
    # Filter data for this date
    date_data = instrument_data[
        (instrument_data['timestamp'] >= date) & 
        (instrument_data['timestamp'] < date + pd.Timedelta(days=1))
    ].copy()
    
    print_info("Rows for date", str(len(date_data)))
    
    if len(date_data) == 0:
        print_info("Status", "NO DATA FOR THIS DATE")
        return None
    
    # Check time range
    print_info("Time range", f"{date_data['timestamp'].min()} to {date_data['timestamp'].max()}")
    
    # Check if we have data for S2 session (starts at 08:00)
    if date_data['timestamp'].dt.tz is not None:
        tz = date_data['timestamp'].iloc[0].tz
        s2_start_time = pd.Timestamp(f"{date.date()} 08:00:00", tz=tz)
    else:
        s2_start_time = pd.Timestamp(f"{date.date()} 08:00:00")
    
    s2_data = date_data[date_data['timestamp'] >= s2_start_time]
    print_info("S2 session data rows", str(len(s2_data)))
    if len(s2_data) > 0:
        print_info("S2 data time range", f"{s2_data['timestamp'].min()} to {s2_data['timestamp'].max()}")
    else:
        print_info("S2 data status", "NO DATA FOR S2 SESSION (starts at 08:00)")
        print_info("Latest data time", f"{date_data['timestamp'].max()}")
        print_info("S2 start time", f"{s2_start_time}")
        print_info("Issue", "Data file doesn't contain S2 session data yet - need to wait for more data or check data collection")
        print_info("Recommendation", "Wait for data collection to complete for today, or check if data collection is running")
    
    # Initialize range detector
    config_manager = ConfigManager()
    slot_config = {
        "SLOT_START": {
            "S1": config_manager.get_slot_start("S1"),
            "S2": config_manager.get_slot_start("S2")
        },
        "SLOT_ENDS": {
            "S1": config_manager.get_slot_ends("S1"),
            "S2": config_manager.get_slot_ends("S2")
        }
    }
    
    range_detector = RangeDetector(slot_config)
    
    # Check S2 slots
    s2_slots = config_manager.get_slot_ends("S2")
    print_info("S2 slots to check", ", ".join(s2_slots))
    
    ranges_found = []
    for slot_time in s2_slots:
        print(f"\n  Checking S2 slot: {slot_time}")
        try:
            range_result = range_detector.calculate_range(date_data, date, slot_time, "S2")
            if range_result:
                ranges_found.append((slot_time, range_result))
                print(f"    Range High: {range_result.range_high}")
                print(f"    Range Low: {range_result.range_low}")
                print(f"    Range Size: {range_result.range_size}")
                print(f"    Freeze Close: {range_result.freeze_close}")
                print(f"    Start Time: {range_result.start_time}")
                print(f"    End Time: {range_result.end_time}")
            else:
                print(f"    No range calculated")
        except Exception as e:
            print(f"    ERROR: {e}")
            import traceback
            traceback.print_exc()
    
    return ranges_found

def check_entry_detection(df: pd.DataFrame, ranges: List, instrument: str = "ES"):
    """Check entry detection for S2 ranges"""
    print_section("S2 ENTRY DETECTION CHECK")
    
    if not ranges:
        print_info("Status", "NO RANGES TO CHECK")
        return
    
    config_manager = ConfigManager()
    instrument_manager = InstrumentManager()
    utility_manager = UtilityManager()
    entry_detector = EntryDetector(config_manager=config_manager, instrument_manager=instrument_manager)
    
    tick_size = instrument_manager.get_tick_size(instrument)
    
    for slot_time, range_result in ranges:
        print(f"\n  Checking entry for S2 slot: {slot_time}")
        
        brk_long = utility_manager.round_to_tick(range_result.range_high + tick_size, tick_size)
        brk_short = utility_manager.round_to_tick(range_result.range_low - tick_size, tick_size)
        
        print(f"    Breakout Long: {brk_long}")
        print(f"    Breakout Short: {brk_short}")
        print(f"    Freeze Close: {range_result.freeze_close}")
        
        # Check immediate entry
        immediate_long = range_result.freeze_close >= brk_long
        immediate_short = range_result.freeze_close <= brk_short
        
        print(f"    Immediate Long: {immediate_long}")
        print(f"    Immediate Short: {immediate_short}")
        
        if immediate_long or immediate_short:
            print(f"    → IMMEDIATE ENTRY DETECTED")
            continue
        
        # Check post-range breakouts
        end_ts = range_result.end_time
        post_data = df[df['timestamp'] >= end_ts].copy()
        
        print(f"    Post-range bars: {len(post_data)}")
        
        if len(post_data) > 0:
            # Check for breakouts
            long_breakout = post_data[post_data['high'] >= brk_long]
            short_breakout = post_data[post_data['low'] <= brk_short]
            
            print(f"    Long breakout bars: {len(long_breakout)}")
            print(f"    Short breakout bars: {len(short_breakout)}")
            
            if len(long_breakout) > 0:
                long_time = long_breakout['timestamp'].min()
                print(f"    First long breakout: {long_time}")
            
            if len(short_breakout) > 0:
                short_time = short_breakout['timestamp'].min()
                print(f"    First short breakout: {short_time}")
            
            # Check market close cutoff
            market_close_time_str = config_manager.get_market_close_time()
            market_close_hour, market_close_minute = map(int, market_close_time_str.split(":"))
            
            if end_ts.tz is not None:
                date_str = end_ts.strftime('%Y-%m-%d')
                market_close = pd.Timestamp(f"{date_str} {market_close_hour:02d}:{market_close_minute:02d}:00", tz=end_ts.tz)
            else:
                market_close = end_ts.replace(hour=market_close_hour, minute=market_close_minute, second=0, microsecond=0)
            
            print(f"    Market close: {market_close}")
            
            if len(long_breakout) > 0:
                long_after_close = long_breakout['timestamp'].min() > market_close
                print(f"    Long breakout after close: {long_after_close}")
            
            if len(short_breakout) > 0:
                short_after_close = short_breakout['timestamp'].min() > market_close
                print(f"    Short breakout after close: {short_after_close}")
            
            # Try actual entry detection
            try:
                entry_result = entry_detector.detect_entry(
                    df, range_result, brk_long, brk_short, 
                    range_result.freeze_close, end_ts
                )
                print(f"    Entry Result: {entry_result.entry_direction}")
                if entry_result.entry_direction:
                    print(f"    Entry Price: {entry_result.entry_price}")
                    print(f"    Entry Time: {entry_result.entry_time}")
            except Exception as e:
                print(f"    ERROR in entry detection: {e}")
                import traceback
                traceback.print_exc()
        else:
            print(f"    → NO POST-RANGE DATA")

def check_recent_analyzer_output():
    """Check recent analyzer output for S2 trades"""
    print_section("RECENT ANALYZER OUTPUT CHECK")
    
    analyzer_temp = qtsw2_root / "data" / "analyzer_temp"
    if not analyzer_temp.exists():
        print_info("Status", "ANALYZER TEMP FOLDER NOT FOUND")
        return
    
    # Find today's folder
    today = datetime.now().date()
    today_folder = analyzer_temp / str(today)
    
    if today_folder.exists():
        print_info("Today's folder", f"Found: {today_folder}")
        files = list(today_folder.glob("*.parquet"))
        print_info("Output files", str(len(files)))
        
        for file in files:
            try:
                df = pd.read_parquet(file)
                print(f"\n  File: {file.name}")
                print(f"    Rows: {len(df)}")
                if len(df) > 0:
                    s2_trades = df[df['Session'] == 'S2']
                    print(f"    S2 trades: {len(s2_trades)}")
                    if len(s2_trades) > 0:
                        print(f"    S2 date range: {s2_trades['Date'].min()} to {s2_trades['Date'].max()}")
                        print(f"    S2 slots: {', '.join(s2_trades['Time'].unique())}")
                    else:
                        print(f"    → NO S2 TRADES FOUND")
            except Exception as e:
                print(f"    Error reading file: {e}")
    else:
        print_info("Today's folder", "NOT FOUND")
    
    # Check analyzed output
    analyzed = qtsw2_root / "data" / "analyzed"
    if analyzed.exists():
        print(f"\n  Checking analyzed output...")
        # Look for S2 files (e.g., ES2_an_2025_01.parquet)
        s2_files = list(analyzed.rglob("*2_an_*.parquet"))
        print(f"    S2 monthly files: {len(s2_files)}")
        if s2_files:
            latest_file = max(s2_files, key=lambda p: p.stat().st_mtime)
            print(f"    Latest S2 file: {latest_file.name}")
            try:
                df = pd.read_parquet(latest_file)
                print(f"    Rows: {len(df)}")
                if len(df) > 0:
                    recent_trades = df.tail(20)
                    print(f"    Recent trades (last 20):")
                    for _, row in recent_trades.iterrows():
                        print(f"      {row.get('Date', 'N/A')} {row.get('Time', 'N/A')} {row.get('Session', 'N/A')} {row.get('Result', 'N/A')}")
            except Exception as e:
                print(f"    Error reading file: {e}")

def main():
    print("="*80)
    print("S2 TRADES DIAGNOSTIC TOOL")
    print("="*80)
    print(f"Timestamp: {datetime.now().strftime('%Y-%m-%d %H:%M:%S')}")
    print(f"QTSW2 Root: {qtsw2_root}")
    
    # Check configuration
    slot_starts, slot_ends, market_close = check_s2_configuration()
    
    # Check data availability
    # Try multiple possible paths
    alt_paths = [
        qtsw2_root / "data" / "data_translated",
        qtsw2_root / "data" / "translated",
        qtsw2_root / "data" / "data_processed",
        qtsw2_root / "data" / "processed"
    ]
    data_folder = None
    for alt_path in alt_paths:
        if alt_path.exists():
            data_folder = alt_path
            break
    
    df = check_data_availability(data_folder, instrument="ES")
    
    # Check S2 ranges if we have data
    ranges = None
    if df is not None:
        ranges = check_s2_ranges(df, instrument="ES")
        
        # Check entry detection
        if ranges:
            check_entry_detection(df, ranges, instrument="ES")
    
    # Check recent analyzer output
    check_recent_analyzer_output()
    
    # Summary
    print_section("SUMMARY")
    if df is None:
        print("  [X] NO DATA AVAILABLE - Check data folder and file structure")
    elif not ranges:
        print("  [X] NO S2 RANGES CALCULATED - Check date and data availability")
    else:
        print(f"  [OK] Found {len(ranges)} S2 ranges")
        print("  -> Review entry detection results above for details")
    
    print("\n" + "="*80)

if __name__ == "__main__":
    main()
