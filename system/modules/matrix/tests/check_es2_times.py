#!/usr/bin/env python3
"""
Check what times are available in ES2 data
"""

import sys
import pandas as pd
from pathlib import Path

# Add project root to path
QTSW2_ROOT = Path(__file__).parent.parent.parent.parent
sys.path.insert(0, str(QTSW2_ROOT))

from modules.matrix import data_loader

# Check ES2 data
analyzer_runs_dir = QTSW2_ROOT / "data" / "analyzed"

print("="*70)
print("ES2 TIME AVAILABILITY CHECK")
print("="*70)
print()

success, data_list, _ = data_loader.load_stream_data("ES2", analyzer_runs_dir)

if success and data_list:
    # Combine all dataframes
    df = pd.concat(data_list, ignore_index=True)
    
    print(f"Total ES2 trades: {len(df)}")
    print()
    
    # Check times
    print("Time distribution:")
    time_counts = df['Time'].value_counts().sort_index()
    for time, count in time_counts.items():
        pct = (count / len(df)) * 100
        print(f"  {time}: {count:,} trades ({pct:.1f}%)")
    
    print()
    print("Session distribution:")
    if 'Session' in df.columns:
        session_counts = df['Session'].value_counts()
        for session, count in session_counts.items():
            pct = (count / len(df)) * 100
            print(f"  {session}: {count:,} trades ({pct:.1f}%)")
    
    print()
    print("S2 times breakdown:")
    s2_times = ['09:30', '10:00', '10:30', '11:00']
    s2_df = df[df['Session'] == 'S2'] if 'Session' in df.columns else df
    
    for time in s2_times:
        time_trades = s2_df[s2_df['Time'] == time]
        if len(time_trades) > 0:
            print(f"  {time}: {len(time_trades):,} trades")
            # Show date range
            if 'Date' in time_trades.columns:
                dates = pd.to_datetime(time_trades['Date'])
                print(f"    Date range: {dates.min().date()} to {dates.max().date()}")
        else:
            print(f"  {time}: NO TRADES")
    
    print()
    print("Sample dates with multiple S2 times:")
    # Group by date and see which dates have multiple S2 times
    if 'Date' in df.columns and 'Session' in df.columns:
        s2_by_date = s2_df.groupby('Date')['Time'].apply(lambda x: sorted(x.unique().tolist()))
        multi_time_dates = s2_by_date[s2_by_date.apply(len) > 1]
        if len(multi_time_dates) > 0:
            print(f"  Found {len(multi_time_dates)} dates with multiple S2 times:")
            for date, times in list(multi_time_dates.head(10).items()):
                print(f"    {date}: {times}")
        else:
            print("  NO DATES with multiple S2 times - each date only has one time!")
            print("  This explains why it's always 11:00 - there's only one time per day.")
    
else:
    print("Failed to load ES2 data")


