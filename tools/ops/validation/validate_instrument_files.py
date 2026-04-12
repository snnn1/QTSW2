#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Validate Instrument Files

Scans all parquet files in analyzer_runs and sequencer_runs to detect
instrument mismatches (e.g., CL data in ES1 folder, ES data in CL1 folder).

Checks:
1. Folder name matches Instrument column in data
2. Filename pattern matches Instrument column
3. All rows in a file have the same instrument
"""

import os
import sys
# Set UTF-8 encoding for Windows console compatibility
if sys.platform == 'win32':
    try:
        sys.stdout.reconfigure(encoding='utf-8')
        sys.stderr.reconfigure(encoding='utf-8')
    except:
        pass  # Python < 3.7 or reconfigure not available

from pathlib import Path
import pandas as pd
from typing import List, Dict, Tuple

# Add project root to path
PROJECT_ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(PROJECT_ROOT))

ANALYZER_RUNS_DIR = PROJECT_ROOT / "data" / "analyzer_runs"
SEQUENCER_RUNS_DIR = PROJECT_ROOT / "data" / "sequencer_runs"


def extract_instrument_from_folder(folder_path: Path) -> str:
    """Extract expected instrument from folder name (e.g., ES1 -> ES, CL2 -> CL)."""
    folder_name = folder_path.name.upper()
    # Remove session number (1 or 2)
    if folder_name.endswith('1') or folder_name.endswith('2'):
        return folder_name[:-1]
    return folder_name


def extract_instrument_from_filename(filename: str) -> str:
    """Extract expected instrument from filename (e.g., ES1_an_2025_11.parquet -> ES)."""
    filename_upper = filename.upper()
    # Common patterns: ES1_an_*, CL1_seq_*, etc.
    for inst in ['ES', 'CL', 'NQ', 'YM', 'NG', 'GC']:
        if filename_upper.startswith(f"{inst}1_") or filename_upper.startswith(f"{inst}2_"):
            return inst
    return None


def detect_instrument_from_data(df: pd.DataFrame) -> List[str]:
    """Detect instruments from data columns."""
    instruments = set()
    
    if 'Instrument' in df.columns:
        unique_instruments = df['Instrument'].dropna().unique()
        instruments.update([str(inst).upper().strip() for inst in unique_instruments if pd.notna(inst)])
    
    if 'Stream' in df.columns:
        streams = df['Stream'].dropna().unique()
        for stream in streams:
            if pd.notna(stream) and isinstance(stream, str):
                stream_upper = stream.upper()
                if stream_upper.startswith('ES'):
                    instruments.add('ES')
                elif stream_upper.startswith('CL'):
                    instruments.add('CL')
                elif stream_upper.startswith('NQ'):
                    instruments.add('NQ')
                elif stream_upper.startswith('NG'):
                    instruments.add('NG')
                elif stream_upper.startswith('GC'):
                    instruments.add('GC')
                elif stream_upper.startswith('YM'):
                    instruments.add('YM')
    
    return sorted(list(instruments))


def validate_file(file_path: Path, expected_instrument: str) -> Dict:
    """Validate a single parquet file."""
    result = {
        'file': str(file_path),
        'expected': expected_instrument,
        'found': [],
        'status': 'OK',
        'error': None,
        'row_count': 0
    }
    
    try:
        # Read first 1000 rows for validation (faster than reading entire file)
        # Note: parquet doesn't support nrows, so read full file but limit processing
        df = pd.read_parquet(file_path)
        # Limit to first 1000 rows for faster processing
        if len(df) > 1000:
            df = df.head(1000)
        result['row_count'] = len(df)
        
        if df.empty:
            result['status'] = 'EMPTY'
            return result
        
        # Detect instruments from data
        found_instruments = detect_instrument_from_data(df)
        result['found'] = found_instruments
        
        if not found_instruments:
            result['status'] = 'NO_INSTRUMENT_DETECTED'
            result['error'] = 'Could not detect instrument from data'
            return result
        
        # Check if expected instrument matches
        if expected_instrument not in found_instruments:
            result['status'] = 'MISMATCH'
            result['error'] = f"Expected {expected_instrument} but found {found_instruments}"
        elif len(found_instruments) > 1:
            result['status'] = 'MIXED'
            result['error'] = f"Found multiple instruments: {found_instruments}"
        else:
            result['status'] = 'OK'
    
    except Exception as e:
        result['status'] = 'ERROR'
        result['error'] = str(e)
    
    return result


def scan_directory(directory: Path, file_type: str) -> List[Dict]:
    """Scan a directory (analyzer_runs or sequencer_runs) for instrument mismatches."""
    results = []
    
    if not directory.exists():
        print(f"Directory does not exist: {directory}")
        return results
    
    print(f"\nScanning {file_type} directory: {directory}")
    print("=" * 80)
    
    # Walk through instrument folders (ES1, CL1, etc.)
    for instrument_folder in sorted(directory.iterdir()):
        if not instrument_folder.is_dir():
            continue
        
        expected_instrument = extract_instrument_from_folder(instrument_folder)
        if not expected_instrument:
            continue
        
        print(f"\nChecking folder: {instrument_folder.name} (expected: {expected_instrument})")
        
        # Walk through year folders
        for year_folder in sorted(instrument_folder.iterdir()):
            if not year_folder.is_dir():
                continue
            
            # Find all parquet files
            parquet_files = list(year_folder.glob("*.parquet"))
            
            for file_path in sorted(parquet_files):
                # Also check filename pattern
                filename_inst = extract_instrument_from_filename(file_path.name)
                if filename_inst and filename_inst != expected_instrument:
                    print(f"  WARNING: Filename suggests {filename_inst} but folder is {expected_instrument}")
                
                # Validate file
                result = validate_file(file_path, expected_instrument)
                result['folder'] = str(instrument_folder.name)
                result['year'] = year_folder.name
                result['filename'] = file_path.name
                
                if result['status'] != 'OK':
                    print(f"  [X] {file_path.name}: {result['status']} - {result.get('error', '')}")
                    if result['found']:
                        print(f"     Found instruments: {result['found']}")
                else:
                    print(f"  [OK] {file_path.name}: OK ({result['row_count']} rows)")
                
                results.append(result)
    
    return results


def main():
    """Main validation function."""
    print("=" * 80)
    print("INSTRUMENT FILE VALIDATION")
    print("=" * 80)
    print("\nThis script checks all parquet files for instrument mismatches.")
    print("It validates that files in ES1 folder contain ES data, CL1 folder contains CL data, etc.\n")
    
    all_results = []
    
    # Scan analyzer_runs
    analyzer_results = scan_directory(ANALYZER_RUNS_DIR, "analyzer_runs")
    all_results.extend(analyzer_results)
    
    # Scan sequencer_runs
    sequencer_results = scan_directory(SEQUENCER_RUNS_DIR, "sequencer_runs")
    all_results.extend(sequencer_results)
    
    # Summary
    print("\n" + "=" * 80)
    print("SUMMARY")
    print("=" * 80)
    
    total_files = len(all_results)
    ok_files = len([r for r in all_results if r['status'] == 'OK'])
    mismatch_files = len([r for r in all_results if r['status'] == 'MISMATCH'])
    mixed_files = len([r for r in all_results if r['status'] == 'MIXED'])
    error_files = len([r for r in all_results if r['status'] == 'ERROR'])
    empty_files = len([r for r in all_results if r['status'] == 'EMPTY'])
    no_detect_files = len([r for r in all_results if r['status'] == 'NO_INSTRUMENT_DETECTED'])
    
    print(f"\nTotal files scanned: {total_files}")
    print(f"  [OK] OK: {ok_files}")
    print(f"  [X] Mismatch (wrong instrument): {mismatch_files}")
    print(f"  [!] Mixed (multiple instruments): {mixed_files}")
    print(f"  [!] Empty: {empty_files}")
    print(f"  [!] No instrument detected: {no_detect_files}")
    print(f"  [X] Errors: {error_files}")
    
    # List problematic files
    problematic = [r for r in all_results if r['status'] in ['MISMATCH', 'MIXED', 'ERROR']]
    
    if problematic:
        print("\n" + "=" * 80)
        print("PROBLEMATIC FILES:")
        print("=" * 80)
        for result in problematic:
            print(f"\n{result['file']}")
            print(f"  Folder: {result['folder']}")
            print(f"  Expected: {result['expected']}")
            print(f"  Found: {result['found']}")
            print(f"  Status: {result['status']}")
            if result['error']:
                print(f"  Error: {result['error']}")
            print(f"  Rows: {result['row_count']}")
    else:
        print("\n[OK] No problematic files found!")
    
    print("\n" + "=" * 80)
    print("Validation complete!")
    print("=" * 80)


if __name__ == "__main__":
    main()

