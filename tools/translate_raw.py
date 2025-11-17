#!/usr/bin/env python3
"""
Fixed Data Translation Script
Processes raw NinjaTrader data exports into clean, timezone-corrected format
"""

import sys
import os
# Set UTF-8 encoding for Windows console compatibility
if sys.platform == 'win32':
    try:
        sys.stdout.reconfigure(encoding='utf-8')
        sys.stderr.reconfigure(encoding='utf-8')
    except:
        pass  # Python < 3.7 or reconfigure not available

import pandas as pd
import numpy as np
from pathlib import Path
from typing import Optional
import argparse
import re


def root_symbol(contract: str) -> str:
    """Extract root instrument from contract name."""
    # Handle different filename formats
    # DataExport_CL_*, DataExport_ES_*, etc.
    if "DataExport_CL" in contract or "_CL_" in contract:
        return "CL"
    elif "DataExport_ES" in contract or "_ES_" in contract:
        return "ES"
    elif "DataExport_NQ" in contract or "_NQ_" in contract:
        return "NQ"
    elif "DataExport_YM" in contract or "_YM_" in contract:
        return "YM"
    elif "DataExport_NG" in contract or "_NG_" in contract:
        return "NG"
    elif "DataExport_GC" in contract or "_GC_" in contract:
        return "GC"
    # MinuteDataExport format
    elif "MinuteDataExport_ES" in contract:
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
        # Try to extract instrument from filename pattern: DataExport_INSTRUMENT_*
        match = re.search(r"DataExport_([A-Z]{2})_", contract)
        if match:
            return match.group(1)
        # Fallback: try to find 2-letter code
        match = re.search(r"_([A-Z]{2})_", contract)
        if match:
            return match.group(1)
        # Last resort: first 2 uppercase letters
        match = re.match(r"([A-Z]{2})", contract)
        return match.group(1) if match else contract.upper()


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
        
        # Determine timezone from filename
        # Check if filename indicates UTC (e.g., DataExport_ES_*_UTC.csv)
        filename_lower = filepath.name.lower()
        is_utc_data = "_utc" in filename_lower
        
        # Ensure timestamp is timezone-aware
        if df["timestamp"].dt.tz is None:
            if is_utc_data:
                # Data is in UTC, convert to Chicago time
                df["timestamp"] = df["timestamp"].dt.tz_localize("UTC").dt.tz_convert("America/Chicago")
                print(f"  Detected UTC data (from filename), converted to Chicago time")
            else:
                # Assume data is already in Chicago time (local trading timezone)
                df["timestamp"] = df["timestamp"].dt.tz_localize("America/Chicago")
                print(f"  Detected Chicago time data (no _UTC in filename), kept as Chicago time")
        
        # Convert numeric columns
        numeric_cols = ["open", "high", "low", "close", "volume"]
        for col in numeric_cols:
            df[col] = pd.to_numeric(df[col], errors="coerce")
        
        # Remove rows with invalid timestamps
        df = df.dropna(subset=["timestamp"])
        
        # Add contract and instrument info
        contract = infer_contract_from_filename(filepath)
        df["contract"] = contract
        instrument = root_symbol(contract)
        # ALWAYS override instrument column with filename-derived value (more reliable)
        # The CSV Instrument column often has "DATAEXPORT" or other incorrect values
        df["instrument"] = instrument
        print(f"  Set instrument to: {instrument} (from filename: {filepath.name})")
        
        # Sort by timestamp
        df = df.sort_values("timestamp").reset_index(drop=True)
        
        print(f"  Loaded: {len(df):,} rows, {df['timestamp'].min()} to {df['timestamp'].max()}")
        return df
        
    except Exception as e:
        print(f"  ERROR loading {filepath.name}: {e}")
        raise


def load_folder(folder_path: str) -> pd.DataFrame:
    """Load all data files from a folder."""
    folder = Path(folder_path).resolve()  # Resolve to absolute path
    if not folder.exists():
        raise FileNotFoundError(f"Folder not found: {folder_path} (resolved to: {folder})")
    
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


def process_files_separately(input_path: Path, output_path: Path, separate_years: bool, output_format: str):
    """Process each file separately without merging."""
    # Ensure output directory exists
    output_path.mkdir(parents=True, exist_ok=True)
    print(f"Output directory: {output_path}")
    print(f"Output directory exists: {output_path.exists()}")
    
    # Find all data files
    data_files = []
    for ext in ["*.csv", "*.txt", "*.dat"]:
        data_files.extend(sorted(input_path.glob(ext)))
    
    if not data_files:
        raise RuntimeError(f"No data files found in {input_path}")
    
    print(f"Processing {len(data_files)} files separately (no merging)...")
    print(f"Separate years: {separate_years}, Output format: {output_format}")
    
    for filepath in data_files:
        try:
            print(f"\nProcessing: {filepath.name}")
            df = load_single_file(filepath)
            
            if separate_years:
                # Separate by year for this file
                df['year'] = df['timestamp'].dt.year
                years = sorted(df['year'].unique())
                
                for year in years:
                    year_data = df[df['year'] == year].copy()
                    year_data = year_data.drop(columns=['year'])
                    
                    # Get instrument from filename (more reliable than data column)
                    instrument = root_symbol(infer_contract_from_filename(filepath))
                    # Verify instrument is valid
                    if instrument not in ['CL', 'ES', 'NQ', 'YM', 'NG', 'GC']:
                        # Fallback to data column
                        if len(year_data) > 0 and 'instrument' in year_data.columns:
                            instrument = year_data['instrument'].iloc[0]
                        else:
                            instrument = "UNKNOWN"
                    # Ensure instrument column in data matches filename-derived instrument
                    if 'instrument' in year_data.columns:
                        year_data['instrument'] = instrument
                    
                    if output_format in ["parquet", "both"]:
                        parquet_file = output_path / f"{instrument}_{year}_{filepath.stem}.parquet"
                        print(f"  Saving to: {parquet_file}")
                        year_data.to_parquet(parquet_file, index=False)
                        print(f"  Saved: {parquet_file.name} ({len(year_data):,} rows)")
                    
                    if output_format in ["csv", "both"]:
                        csv_file = output_path / f"{instrument}_{year}_{filepath.stem}.csv"
                        print(f"  Saving to: {csv_file}")
                        year_data.to_csv(csv_file, index=False)
                        print(f"  Saved: {csv_file.name} ({len(year_data):,} rows)")
            else:
                # Save file as-is
                # Get instrument from filename (more reliable than data column)
                instrument = root_symbol(infer_contract_from_filename(filepath))
                # Verify instrument is valid
                if instrument not in ['CL', 'ES', 'NQ', 'YM', 'NG', 'GC']:
                    # Fallback to data column
                    if len(df) > 0 and 'instrument' in df.columns:
                        instrument = df['instrument'].iloc[0]
                    else:
                        instrument = "UNKNOWN"
                
                if output_format in ["parquet", "both"]:
                    parquet_file = output_path / f"{instrument}_{filepath.stem}.parquet"
                    print(f"  Saving to: {parquet_file}")
                    df.to_parquet(parquet_file, index=False)
                    print(f"  Saved: {parquet_file.name} ({len(df):,} rows)")
                
                if output_format in ["csv", "both"]:
                    csv_file = output_path / f"{instrument}_{filepath.stem}.csv"
                    print(f"  Saving to: {csv_file}")
                    df.to_csv(csv_file, index=False)
                    print(f"  Saved: {csv_file.name} ({len(df):,} rows)")
                    
        except Exception as e:
            import traceback
            print(f"[ERROR] Failed to process {filepath.name}: {e}")
            print(f"[ERROR] Traceback: {traceback.format_exc()}")
            continue


def main():
    parser = argparse.ArgumentParser(description="Fixed Data Translation Script")
    parser.add_argument("--input", "-i", required=True, help="Input folder path")
    parser.add_argument("--output", "-o", default="data_processed", help="Output folder path")
    parser.add_argument("--separate-years", action="store_true", help="Separate data into yearly files")
    parser.add_argument("--no-merge", action="store_true", help="Process each file separately without merging")
    parser.add_argument("--format", choices=["parquet", "csv", "both"], default="parquet", help="Output format")
    
    args = parser.parse_args()
    
    try:
        # Resolve input path to absolute
        input_path = Path(args.input).resolve()
        if not input_path.exists():
            print(f"[ERROR] Input folder does not exist: {input_path}")
            print(f"   Checked path: {input_path}")
            sys.exit(1)
        
        # Create output folder (resolve to absolute)
        output_path = Path(args.output).resolve()
        output_path.mkdir(parents=True, exist_ok=True)
        
        # Process files separately if requested
        if args.no_merge:
            process_files_separately(input_path, output_path, args.separate_years, args.format)
            print("\n[SUCCESS] Data translation completed successfully!")
            print("[INFO] Files processed separately (no merging)")
        else:
            # Load and merge all data
            df = load_folder(str(input_path))
            
            # Save data
            if args.separate_years:
                separate_by_year(df, str(output_path))
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
            
            # Use ASCII-safe characters for Windows console compatibility
            print("\n[SUCCESS] Data translation completed successfully!")
            print(f"[INFO] Total rows: {len(df):,}")
            print(f"[INFO] Date range: {df['timestamp'].min()} to {df['timestamp'].max()}")
            print(f"[INFO] Instruments: {sorted(df['instrument'].unique())}")
        
    except Exception as e:
        print(f"[ERROR] Error: {e}")
        import traceback
        traceback.print_exc()
        sys.exit(1)


if __name__ == "__main__":
    main()

