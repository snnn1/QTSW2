#!/usr/bin/env python3
"""
Helper script to read parquet bars and output as JSON for C# consumption.
Called by SnapshotParquetBarProvider when Parquet.Net API is problematic.
"""
import sys
import json
import pandas as pd
from pathlib import Path
from datetime import datetime
import pytz

def read_parquet_bars(file_path: str, instrument: str, start_utc: str, end_utc: str):
    """Read bars from parquet file and filter by instrument and time range."""
    try:
        df = pd.read_parquet(file_path)
        
        # Filter by instrument if column exists
        if 'instrument' in df.columns:
            df = df[df['instrument'].str.upper() == instrument.upper()]
        
        # Convert timestamp to UTC if needed
        if 'timestamp' in df.columns:
            if df['timestamp'].dtype == 'object':
                df['timestamp'] = pd.to_datetime(df['timestamp'])
            
            # Assume timestamps are in Chicago timezone (as per Translator output)
            chicago_tz = pytz.timezone('America/Chicago')
            if df['timestamp'].dt.tz is None:
                df['timestamp'] = df['timestamp'].dt.tz_localize(chicago_tz)
            else:
                df['timestamp'] = df['timestamp'].dt.tz_convert(chicago_tz)
            
            # Convert to UTC
            df['timestamp'] = df['timestamp'].dt.tz_convert('UTC')
            
            # Parse filter times
            start_dt = pd.to_datetime(start_utc, utc=True)
            end_dt = pd.to_datetime(end_utc, utc=True)
            
            # Filter by time range
            df = df[(df['timestamp'] >= start_dt) & (df['timestamp'] < end_dt)]
        
        # Convert to list of arrays (column-ordered per Translator schema)
        # Column order: timestamp, open, high, low, close, volume
        bars = []
        for _, row in df.iterrows():
            # Output as array matching Translator schema:
            # index 0 → timestamp (DateTimeOffset / UTC)
            # index 1 → open
            # index 2 → high
            # index 3 → low
            # index 4 → close
            # index 5 → volume (optional)
            timestamp_iso = row['timestamp'].isoformat()
            open_val = float(row['open'])
            high_val = float(row['high'])
            low_val = float(row['low'])
            close_val = float(row['close'])
            volume_val = float(row['volume']) if 'volume' in row and pd.notna(row.get('volume')) else None
            
            bar = [timestamp_iso, open_val, high_val, low_val, close_val, volume_val]
            bars.append(bar)
        
        return bars
    except Exception as e:
        return {'error': str(e)}

if __name__ == '__main__':
    if len(sys.argv) != 5:
        print(json.dumps({'error': 'Usage: read_parquet_bars.py <file_path> <instrument> <start_utc> <end_utc>'}))
        sys.exit(1)
    
    file_path = sys.argv[1]
    instrument = sys.argv[2]
    start_utc = sys.argv[3]
    end_utc = sys.argv[4]
    
    bars = read_parquet_bars(file_path, instrument, start_utc, end_utc)
    print(json.dumps(bars))
