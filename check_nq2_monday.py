#!/usr/bin/env python3
"""
Check NQ2 data for Monday 2026-01-26 in master matrix.
"""

import sys
from pathlib import Path
import pandas as pd

# Add project root to path
project_root = Path(__file__).parent
sys.path.insert(0, str(project_root))

from modules.matrix.file_manager import load_existing_matrix

def main():
    """Check NQ2 Monday data."""
    master_df = load_existing_matrix("data/master_matrix")
    
    if master_df.empty:
        print("No master matrix found!")
        return
    
    # Filter for NQ2
    nq2_df = master_df[master_df['Stream'] == 'NQ2'].copy()
    
    # Filter for Monday 2026-01-26
    monday_date = pd.to_datetime('2026-01-26').date()
    monday_df = nq2_df[nq2_df['trade_date'].dt.date == monday_date].copy()
    
    print("=" * 80)
    print(f"NQ2 data for Monday {monday_date}")
    print("=" * 80)
    
    if monday_df.empty:
        print("No NQ2 data found for Monday!")
        print(f"\nAvailable dates for NQ2:")
        dates = sorted(nq2_df['trade_date'].dt.date.unique())
        for d in dates[-10:]:  # Show last 10 dates
            print(f"  {d}")
    else:
        print(f"\nFound {len(monday_df)} row(s) for NQ2 on Monday:")
        for idx, row in monday_df.iterrows():
            print(f"\nRow {idx}:")
            print(f"  Date: {row.get('Date', 'N/A')}")
            print(f"  trade_date: {row.get('trade_date', 'N/A')}")
            print(f"  Time: {row.get('Time', 'N/A')}")
            print(f"  Time Change: {row.get('Time Change', 'N/A')}")
            print(f"  Result: {row.get('Result', 'N/A')}")
            print(f"  Session: {row.get('Session', 'N/A')}")
            print(f"  EntryTime: {row.get('EntryTime', 'N/A')}")
    
    # Also check what the timetable logic would see
    print("\n" + "=" * 80)
    print("Timetable Logic Check:")
    print("=" * 80)
    
    # Get previous day (Sunday 2026-01-25)
    sunday_date = pd.to_datetime('2026-01-25').date()
    sunday_df = nq2_df[nq2_df['trade_date'].dt.date == sunday_date].copy()
    
    print(f"\nPrevious day (Sunday {sunday_date}):")
    if sunday_df.empty:
        print("  No data found for Sunday")
    else:
        for idx, row in sunday_df.iterrows():
            print(f"  Time: {row.get('Time', 'N/A')}")
            print(f"  Time Change: {row.get('Time Change', 'N/A')}")
            print(f"  Result: {row.get('Result', 'N/A')}")
    
    print(f"\nMonday {monday_date} (source for Tuesday's timetable):")
    if monday_df.empty:
        print("  No data found for Monday")
    else:
        for idx, row in monday_df.iterrows():
            print(f"  Time: {row.get('Time', 'N/A')}")
            print(f"  Time Change: {row.get('Time Change', 'N/A')}")
            print(f"  Result: {row.get('Result', 'N/A')}")
            print(f"\n  → For Tuesday, timetable should use:")
            time_change = row.get('Time Change', '')
            if time_change and str(time_change).strip():
                print(f"    Time Change: {time_change} (time changed due to loss)")
            else:
                print(f"    Time: {row.get('Time', 'N/A')} (time stays same - WIN/BE/NoTrade)")

if __name__ == "__main__":
    main()
