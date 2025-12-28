"""
Inspect Analyzed File - Show contents of a specific analyzed Parquet file
"""

import sys
from pathlib import Path
import pandas as pd

# Add project root to path
qtsw2_root = Path(__file__).parent.parent
if str(qtsw2_root) not in sys.path:
    sys.path.insert(0, str(qtsw2_root))

def main():
    if len(sys.argv) < 2:
        print("Usage: python inspect_analyzed_file.py <filename>")
        print("Example: python inspect_analyzed_file.py ES1_an_2025_12.parquet")
        return
    
    filename = sys.argv[1]
    
    # Find the file
    analyzed_dir = qtsw2_root / "data" / "analyzed"
    file_path = None
    
    # Try to find the file
    for file in analyzed_dir.rglob(filename):
        file_path = file
        break
    
    if not file_path or not file_path.exists():
        print(f"[ERROR] File not found: {filename}")
        print(f"  Searched in: {analyzed_dir}")
        return
    
    print("="*80)
    print(f"INSPECTING FILE: {file_path.name}")
    print("="*80)
    print(f"Full path: {file_path}")
    print(f"File size: {file_path.stat().st_size / 1024:.2f} KB")
    print()
    
    try:
        # Read the Parquet file
        df = pd.read_parquet(file_path)
        
        print("[FILE STRUCTURE]")
        print(f"  Total rows: {len(df):,}")
        print(f"  Total columns: {len(df.columns)}")
        print()
        
        print("[COLUMNS]")
        for i, col in enumerate(df.columns, 1):
            dtype = str(df[col].dtype)
            non_null = df[col].notna().sum()
            null_count = df[col].isna().sum()
            print(f"  {i:2d}. {col:20s} ({dtype:15s}) - {non_null:,} non-null, {null_count:,} null")
        print()
        
        print("[DATA TYPES]")
        print(df.dtypes)
        print()
        
        print("[FIRST 10 ROWS]")
        print(df.head(10).to_string())
        print()
        
        print("[LAST 10 ROWS]")
        print(df.tail(10).to_string())
        print()
        
        # Check for date/time columns
        if 'Date' in df.columns:
            print("[DATE RANGE]")
            print(f"  Earliest date: {df['Date'].min()}")
            print(f"  Latest date: {df['Date'].max()}")
            print(f"  Unique dates: {df['Date'].nunique()}")
            print()
        
        if 'Time' in df.columns:
            print("[TIME RANGE]")
            print(f"  Earliest time: {df['Time'].min()}")
            print(f"  Latest time: {df['Time'].max()}")
            print(f"  Unique times: {df['Time'].nunique()}")
            print()
        
        if 'Session' in df.columns:
            print("[SESSIONS]")
            print(df['Session'].value_counts())
            print()
        
        if 'Instrument' in df.columns:
            print("[INSTRUMENTS]")
            print(df['Instrument'].value_counts())
            print()
        
        print("[STATISTICAL SUMMARY]")
        print(df.describe())
        print()
        
        print("[MEMORY USAGE]")
        print(f"  Memory usage: {df.memory_usage(deep=True).sum() / 1024:.2f} KB")
        print()
        
    except Exception as e:
        print(f"[ERROR] Failed to read file: {e}")
        import traceback
        traceback.print_exc()

if __name__ == "__main__":
    main()









