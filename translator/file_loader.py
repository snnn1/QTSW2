"""
File Loading and Detection
Handles file discovery, format detection, and loading
Supports both tick data and minute bar data
"""

from pathlib import Path
from typing import List, Optional, Dict, Any
import pandas as pd
import re
from .frequency_detector import detect_data_frequency, get_data_type_summary


def get_data_files(folder_path: str) -> List[Path]:
    """
    Get list of data files in folder
    
    Args:
        folder_path: Path to folder containing data files
        
    Returns:
        List of file paths
    """
    folder = Path(folder_path)
    if not folder.exists():
        return []
    
    data_files = []
    for ext in ["*.csv", "*.txt", "*.dat"]:
        data_files.extend(sorted(folder.glob(ext)))
    
    return data_files


def detect_file_format(filepath: Path) -> dict:
    """
    Detect file format and separator
    
    Args:
        filepath: Path to file
        
    Returns:
        Dictionary with format info:
        - has_header: bool
        - separator: str (',' or ';')
        - first_line: str
        - is_tick_format: bool (if filename suggests tick data)
    """
    with open(filepath, 'r') as f:
        first_line = f.readline().strip()
    
    has_header = first_line.startswith('Date') or first_line.startswith('Time')
    
    if ',' in first_line:
        sep = ','
    elif ';' in first_line:
        sep = ';'
    else:
        sep = ','
    
    # Check filename for tick data indicators
    filename_lower = filepath.name.lower()
    is_tick_format = any(indicator in filename_lower for indicator in ['tick', 'trade', 'tx'])\
                     and 'minute' not in filename_lower
    
    return {
        'has_header': has_header,
        'separator': sep,
        'first_line': first_line,
        'is_tick_format': is_tick_format
    }


def get_file_years(filepath: Path) -> List[int]:
    """
    Get years available in a file by sampling
    
    Args:
        filepath: Path to file
        
    Returns:
        List of years found in file
    """
    try:
        # Read first line to detect format
        with open(filepath, 'r') as f:
            first_line = f.readline().strip()
        
        has_header = first_line.startswith('Date') or first_line.startswith('Time')
        
        if ',' in first_line:
            sep = ','
        elif ';' in first_line:
            sep = ';'
        else:
            sep = ','
        
        all_years = set()
        
        if has_header:
            # Read Date and Time columns for efficiency
            try:
                df_dates = pd.read_csv(filepath, sep=sep, usecols=['Date', 'Time'])
                df_dates['timestamp'] = pd.to_datetime(
                    df_dates['Date'] + ' ' + df_dates['Time'], 
                    errors='coerce'
                )
                years = df_dates['timestamp'].dt.year.dropna().unique().astype(int)
                all_years.update(years)
            except:
                # Fallback: sample from beginning, middle, and end
                try:
                    file_size = filepath.stat().st_size
                    if file_size < 10 * 1024 * 1024:  # Less than 10MB
                        df_sample = pd.read_csv(filepath, sep=sep)
                        if 'Date' in df_sample.columns and 'Time' in df_sample.columns:
                            df_sample['timestamp'] = pd.to_datetime(
                                df_sample['Date'] + ' ' + df_sample['Time'], 
                                errors='coerce'
                            )
                            years = df_sample['timestamp'].dt.year.dropna().unique().astype(int)
                            all_years.update(years)
                    else:
                        # Sample from beginning
                        df_begin = pd.read_csv(filepath, sep=sep, nrows=1000)
                        if 'Date' in df_begin.columns and 'Time' in df_begin.columns:
                            df_begin['timestamp'] = pd.to_datetime(
                                df_begin['Date'] + ' ' + df_begin['Time'], 
                                errors='coerce'
                            )
                            years = df_begin['timestamp'].dt.year.dropna().unique().astype(int)
                            all_years.update(years)
                        
                        # Sample from middle
                        try:
                            df_middle = pd.read_csv(filepath, sep=sep, skiprows=range(1, 10000), nrows=1000)
                            if 'Date' in df_middle.columns and 'Time' in df_middle.columns:
                                df_middle['timestamp'] = pd.to_datetime(
                                    df_middle['Date'] + ' ' + df_middle['Time'], 
                                    errors='coerce'
                                )
                                years = df_middle['timestamp'].dt.year.dropna().unique().astype(int)
                                all_years.update(years)
                        except:
                            pass
                except Exception:
                    pass
        else:
            # No header format
            try:
                file_size = filepath.stat().st_size
                if file_size < 10 * 1024 * 1024:  # Less than 10MB
                    df_sample = pd.read_csv(filepath, sep=sep, header=None, usecols=[0])
                    df_sample['timestamp'] = pd.to_datetime(
                        df_sample.iloc[:, 0], 
                        format="%Y%m%d %H%M%S", 
                        errors='coerce'
                    )
                    years = df_sample['timestamp'].dt.year.dropna().unique().astype(int)
                    all_years.update(years)
                else:
                    # Sample from beginning
                    df_begin = pd.read_csv(filepath, sep=sep, header=None, nrows=1000, usecols=[0])
                    df_begin['timestamp'] = pd.to_datetime(
                        df_begin.iloc[:, 0], 
                        format="%Y%m%d %H%M%S", 
                        errors='coerce'
                    )
                    years = df_begin['timestamp'].dt.year.dropna().unique().astype(int)
                    all_years.update(years)
                    
                    # Sample from middle
                    try:
                        df_middle = pd.read_csv(filepath, sep=sep, header=None, skiprows=range(10000), nrows=1000, usecols=[0])
                        df_middle['timestamp'] = pd.to_datetime(
                            df_middle.iloc[:, 0], 
                            format="%Y%m%d %H%M%S", 
                            errors='coerce'
                        )
                        years = df_middle['timestamp'].dt.year.dropna().unique().astype(int)
                        all_years.update(years)
                    except:
                        pass
            except Exception:
                pass
        
        return sorted(list(all_years))
        
    except Exception:
        return []


def load_single_file(filepath: Path, auto_detect_frequency: bool = True) -> Optional[pd.DataFrame]:
    """
    Load a single data file with proper format detection
    Supports both tick data and minute bar data
    
    Args:
        filepath: Path to file
        auto_detect_frequency: If True, detects and stores data frequency
        
    Returns:
        DataFrame with columns: timestamp, open, high, low, close, volume, instrument, [frequency]
        Returns None if error occurs
    """
    format_info = detect_file_format(filepath)
    has_header = format_info['has_header']
    sep = format_info['separator']
    
    try:
        if has_header:
            # CSV with header (Date,Time,Open,High,Low,Close,Volume,Instrument)
            df = pd.read_csv(filepath, sep=sep)
            df['timestamp'] = pd.to_datetime(df['Date'] + ' ' + df['Time'])
            df = df.rename(columns={
                'Open': 'open',
                'High': 'high', 
                'Low': 'low',
                'Close': 'close',
                'Volume': 'volume',
                'Instrument': 'instrument'
            })
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
            df["instrument"] = "ES"  # Default
        
        # Determine timezone from filename
        # Check if filename indicates UTC (e.g., DataExport_ES_*_UTC.csv)
        filename_lower = filepath.name.lower()
        is_utc_data = "_utc" in filename_lower
        
        # Ensure timestamp is timezone-aware
        if df["timestamp"].dt.tz is None:
            if is_utc_data:
                # Data is labeled as UTC - convert to Chicago time
                # Note: Fixed DataExporter now properly exports UTC, but we keep detection
                # for old files that may have been exported incorrectly
                sample_timestamps = df["timestamp"].head(10)
                
                # Quick check: if timestamps are in early morning (0-6) and converting from UTC
                # would shift them to previous day, they might be mislabeled Central Time
                # This handles old exports before the DataExporter fix
                test_utc = pd.DatetimeIndex(sample_timestamps.iloc[:5]).tz_localize("UTC")
                test_chicago = test_utc.tz_convert("America/Chicago")
                original_dates = pd.DatetimeIndex(sample_timestamps.iloc[:5]).date
                converted_dates = pd.DatetimeIndex(test_chicago).date
                
                # If dates shift backwards, likely mislabeled Central Time (old export bug)
                if (original_dates > converted_dates).any():
                    print(f"  WARNING: Detected old export format - timestamps appear to be Central Time, not UTC")
                    print(f"  Treating as Central Time (compatibility with old exports)")
                    df["timestamp"] = df["timestamp"].dt.tz_localize("America/Chicago")
                else:
                    # Properly exported UTC data - convert to Chicago time
                    df["timestamp"] = df["timestamp"].dt.tz_localize("UTC").dt.tz_convert("America/Chicago")
            else:
                # Assume data is already in Chicago time (local trading timezone)
                df["timestamp"] = df["timestamp"].dt.tz_localize("America/Chicago")
        
        # Convert numeric columns
        numeric_cols = ["open", "high", "low", "close", "volume"]
        for col in numeric_cols:
            df[col] = pd.to_numeric(df[col], errors="coerce")
        
        df = df.dropna(subset=["timestamp"])
        
        # Add contract and instrument info
        from .core import infer_contract_from_filename, root_symbol
        contract = infer_contract_from_filename(filepath)
        df["contract"] = contract
        df["instrument"] = root_symbol(contract)
        
        df = df.sort_values("timestamp").reset_index(drop=True)
        
        # Auto-detect and add frequency metadata
        if auto_detect_frequency:
            frequency = detect_data_frequency(df)
            df.attrs['frequency'] = frequency  # Store as metadata attribute
            df.attrs['data_type'] = 'tick' if frequency == 'tick' else 'minute'
            # Also add as column for easy filtering
            df['frequency'] = frequency
        
        return df
        
    except Exception as e:
        print(f"Error loading {filepath.name}: {e}")
        return None

