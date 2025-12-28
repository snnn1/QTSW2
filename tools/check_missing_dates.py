"""
Check Missing Dates - See which trading days are missing from analyzed data
"""

import sys
from pathlib import Path
import pandas as pd
from datetime import date, timedelta

# Add project root to path
qtsw2_root = Path(__file__).parent.parent
if str(qtsw2_root) not in sys.path:
    sys.path.insert(0, str(qtsw2_root))

def main():
    if len(sys.argv) < 2:
        filename = "ES1_an_2025_12.parquet"
        print(f"No filename provided, using default: {filename}")
    else:
        filename = sys.argv[1]
    
    # Find the file
    analyzed_dir = qtsw2_root / "data" / "analyzed"
    file_path = None
    
    for file in analyzed_dir.rglob(filename):
        file_path = file
        break
    
    if not file_path or not file_path.exists():
        print(f"[ERROR] File not found: {filename}")
        return
    
    print("="*80)
    print(f"CHECKING MISSING DATES: {file_path.name}")
    print("="*80)
    
    # Read the file
    df = pd.read_parquet(file_path)
    
    # Get unique dates in file
    dates_present = sorted(df['Date'].dt.date.unique())
    
    print(f"\n[PRESENT DATES]")
    print(f"  Total unique dates: {len(dates_present)}")
    print(f"  Date range: {dates_present[0]} to {dates_present[-1]}")
    print(f"\n  Dates in file:")
    for d in dates_present:
        weekday = d.strftime("%A")
        print(f"    {d} ({weekday})")
    
    # Calculate expected trading days
    start_date = dates_present[0]
    end_date = dates_present[-1]
    
    all_dates = [start_date + timedelta(days=x) for x in range((end_date - start_date).days + 1)]
    trading_days = [d for d in all_dates if d.weekday() < 5]  # Monday=0, Friday=4
    
    print(f"\n[EXPECTED TRADING DAYS]")
    print(f"  Date range: {start_date} to {end_date}")
    print(f"  Total calendar days: {len(all_dates)}")
    print(f"  Expected trading days (Mon-Fri): {len(trading_days)}")
    
    # Find missing dates
    missing = [d for d in trading_days if d not in dates_present]
    
    print(f"\n[MISSING DATES]")
    if missing:
        print(f"  Missing {len(missing)} trading day(s):")
        for d in missing:
            weekday = d.strftime("%A")
            print(f"    {d} ({weekday})")
    else:
        print(f"  No missing dates - all trading days are present!")
    
    # Check if there's data for today or future dates
    today = date.today()
    future_dates = [d for d in dates_present if d > today]
    
    print(f"\n[FUTURE DATES]")
    if future_dates:
        print(f"  Found {len(future_dates)} future date(s) (data beyond today):")
        for d in future_dates:
            print(f"    {d}")
    else:
        print(f"  No future dates - data is up to today or earlier")
    
    # Check latest date vs today
    latest_date = dates_present[-1]
    print(f"\n[LATEST DATE STATUS]")
    print(f"  Latest date in file: {latest_date}")
    print(f"  Today's date: {today}")
    if latest_date < today:
        days_behind = (today - latest_date).days
        print(f"  File is {days_behind} day(s) behind today")
        if days_behind <= 3:
            print(f"  [INFO] This is normal - data may not be available for recent days yet")
    elif latest_date == today:
        print(f"  [OK] File includes today's data")
    else:
        print(f"  [WARNING] File has future dates (data beyond today)")

if __name__ == "__main__":
    main()








