#!/usr/bin/env python3
"""
Fixed Data Translation Script
Processes raw NinjaTrader data exports into clean, timezone-corrected format
"""

import sys
import pandas as pd
import numpy as np
from pathlib import Path
from typing import Optional
import argparse
import re


def root_symbol(contract: str) -> str:
    """Extract root instrument from contract name."""
    # Handle different filename formats
    if "MinuteDataExport_ES" in contract:
        return "ES"
    elif "MinuteDataExport_NQ" in contract:
        return "NQ"
    elif "MinuteDataExport_YM" in contract:
        return "YM"
    elif "MinuteDataExport_CL" in contract:
        return "CL"
    elif "MinuteDataExport_NG" in contract:
        return "NG"
    elif "MinuteDataExport_GC" in contract:
        return "GC"
    else:
        # Fallback to original logic
        match = re.match(r"([A-Za-z]+)", contract)
        return match.group(1).upper() if match else contract.upper()


def infer_contract_from_filename(filepath: Path) -> str:
    """Extract contract name from filename."""
    filename = filepath.name
    # Remove extension
    name_without_ext = filename.rsplit('.', 1)[0]
    return name_without_ext


def detect_file_format(filepath: Path) -> dict:
    """Detect file format and separator."""
    with open(filepath, 'r') as f:
        first_line = f.readline().strip()
    
    has_header = first_line.startswith('Date') or first_line.startswith('Time')
    
    # Detect separator
    if ',' in first_line:
        sep = ','
    elif ';' in first_line:
        sep = ';'
    else:
        sep = ','
    
    return {
        'has_header': has_header,
        'separator': sep,
        'first_line': first_line
    }


def load_single_file(filepath: Path) -> pd.DataFrame:
    """Load a single data file with proper format detection."""
    print(f"Processing: {filepath.name}")
    
    # Detect file format
    format_info = detect_file_format(filepath)
    has_header = format_info['has_header']
    sep = format_info['separator']
    
    print(f"  Format: {'Header' if has_header else 'No Header'}, Separator: '{sep}'")
    
    try:
        if has_header:
            # CSV with header (Date,Time,Open,High,Low,Close,Volume,Instrument)
            df = pd.read_csv(filepath, sep=sep)
            
            # Combine Date and Time into timestamp
            df['timestamp'] = pd.to_datetime(df['Date'] + ' ' + df['Time'])
            
            # Clean up column names
            df = df.rename(columns={
                'Open': 'open',
                'High': 'high', 
                'Low': 'low',
                'Close': 'close',
                'Volume': 'volume',
                'Instrument': 'instrument'
            })
            
            # Drop original date/time columns
            df = df.drop(columns=['Date', 'Time'])
            
        else:
            # No header format
            df = pd.read_csv(
                filepath,
                sep=sep,
                header=None,
                names=["raw_dt", "open", "high", "low", "close", "volume"],
            )
            df["timestamp"] = pd.to_datetime(df["raw_dt"], format="%Y%m%d %H%M%S", errors="coerce")
            df.drop(columns=["raw_dt"], inplace=True)
            df["instrument"] = "ES"  # Default for no-header format
        
        # Ensure timestamp is timezone-aware (convert from UTC to Chicago time)
        if df["timestamp"].dt.tz is None:
            # Data is in UTC, convert to Chicago time
            df["timestamp"] = df["timestamp"].dt.tz_localize("UTC").dt.tz_convert("America/Chicago")
        
        # Convert numeric columns
        numeric_cols = ["open", "high", "low", "close", "volume"]
        for col in numeric_cols:
            df[col] = pd.to_numeric(df[col], errors="coerce")
        
        # Remove rows with invalid timestamps
        df = df.dropna(subset=["timestamp"])
        
        # Add contract and instrument info
        contract = infer_contract_from_filename(filepath)
        df["contract"] = contract
        df["instrument"] = root_symbol(contract)
        
        # Sort by timestamp
        df = df.sort_values("timestamp").reset_index(drop=True)
        
        print(f"  Loaded: {len(df):,} rows, {df['timestamp'].min()} to {df['timestamp'].max()}")
        return df
        
    except Exception as e:
        print(f"  ERROR loading {filepath.name}: {e}")
        raise


def load_folder(folder_path: str) -> pd.DataFrame:
    """Load all data files from a folder."""
    folder = Path(folder_path)
    if not folder.exists():
        raise FileNotFoundError(f"Folder not found: {folder_path}")
    
    print(f"Loading data from: {folder_path}")
    
    # Find all data files
    data_files = []
    for ext in ["*.csv", "*.txt", "*.dat"]:
        data_files.extend(sorted(folder.glob(ext)))
    
    if not data_files:
        raise RuntimeError(f"No data files found in {folder_path}")
    
    print(f"Found {len(data_files)} data files")
    
    # Load each file
    dfs = []
    for filepath in data_files:
        try:
            df = load_single_file(filepath)
            dfs.append(df)
        except Exception as e:
            print(f"Skipping {filepath.name}: {e}")
            continue
    
    if not dfs:
        raise RuntimeError("No files could be loaded successfully")
    
    # Combine all dataframes
    print("Combining data...")
    combined_df = pd.concat(dfs, ignore_index=True)
    print(f"Combined shape: {combined_df.shape}")
    
    # Sort by timestamp
    print("Sorting by timestamp...")
    combined_df = combined_df.sort_values("timestamp").reset_index(drop=True)
    
    # Remove duplicates (keep first occurrence)
    print("Removing duplicates...")
    initial_count = len(combined_df)
    combined_df = combined_df.drop_duplicates(
        subset=["timestamp", "instrument"], 
        keep="first"
    ).reset_index(drop=True)
    final_count = len(combined_df)
    
    if initial_count != final_count:
        print(f"Removed {initial_count - final_count:,} duplicate rows")
    
    print(f"Final data shape: {combined_df.shape}")
    print(f"Date range: {combined_df['timestamp'].min()} to {combined_df['timestamp'].max()}")
    
    return combined_df


def separate_by_year(df: pd.DataFrame, output_folder: str) -> None:
    """Separate data into yearly files."""
    output_path = Path(output_folder)
    output_path.mkdir(exist_ok=True)
    
    print("Separating data by year...")
    
    # Add year column
    df['year'] = df['timestamp'].dt.year
    years = sorted(df['year'].unique())
    
    print(f"Years found: {years}")
    
    for year in years:
        year_data = df[df['year'] == year].copy()
        year_data = year_data.drop(columns=['year'])
        
        # Save as both parquet and CSV
        parquet_file = output_path / f"merged_{year}.parquet"
        csv_file = output_path / f"merged_{year}.csv"
        
        print(f"  Saving {year}: {len(year_data):,} rows")
        
        year_data.to_parquet(parquet_file, index=False)
        year_data.to_csv(csv_file, index=False)
    
    # Save complete dataset
    complete_data = df.drop(columns=['year'])
    complete_parquet = output_path / "merged.parquet"
    complete_csv = output_path / "merged.csv"
    
    print(f"  Saving complete dataset: {len(complete_data):,} rows")
    complete_data.to_parquet(complete_parquet, index=False)
    complete_data.to_csv(complete_csv, index=False)


def main():
    parser = argparse.ArgumentParser(description="Fixed Data Translation Script")
    parser.add_argument("--input", "-i", required=True, help="Input folder path")
    parser.add_argument("--output", "-o", default="data_processed", help="Output folder path")
    parser.add_argument("--separate-years", action="store_true", help="Separate data into yearly files")
    parser.add_argument("--format", choices=["parquet", "csv", "both"], default="parquet", help="Output format")
    
    args = parser.parse_args()
    
    try:
        # Load data
        df = load_folder(args.input)
        
        # Create output folder
        output_path = Path(args.output)
        output_path.mkdir(exist_ok=True)
        
        # Save data
        if args.separate_years:
            separate_by_year(df, args.output)
        else:
            # Save complete dataset
            if args.format in ["parquet", "both"]:
                parquet_file = output_path / "merged.parquet"
                df.to_parquet(parquet_file, index=False)
                print(f"Saved: {parquet_file}")
            
            if args.format in ["csv", "both"]:
                csv_file = output_path / "merged.csv"
                df.to_csv(csv_file, index=False)
                print(f"Saved: {csv_file}")
        
        print("\n✅ Data translation completed successfully!")
        print(f"📊 Total rows: {len(df):,}")
        print(f"📅 Date range: {df['timestamp'].min()} to {df['timestamp'].max()}")
        print(f"🏷️  Instruments: {sorted(df['instrument'].unique())}")
        
    except Exception as e:
        print(f"❌ Error: {e}")
        sys.exit(1)


if __name__ == "__main__":
    main()

