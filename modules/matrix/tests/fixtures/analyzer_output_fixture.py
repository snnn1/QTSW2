"""
Analyzer Output Fixture Generator

Creates deterministic analyzer output fixture data for golden tests.
This fixture contains contract-valid data (all invariants satisfied).
"""

import pandas as pd
import numpy as np
from pathlib import Path
from datetime import datetime, timedelta
from typing import List

# Fixture directory
FIXTURE_DIR = Path(__file__).parent / "data" / "analyzed"


def create_fixture_data():
    """
    Create deterministic analyzer output fixture.
    
    Returns:
        Dictionary mapping stream_id to DataFrame
    """
    # Create 2-3 streams with 5-10 trading days each
    streams = ['ES1', 'GC1']
    
    # Start date: 2025-01-02 (a trading day)
    start_date = datetime(2025, 1, 2)
    
    # Generate 7 trading days (skip weekends)
    trading_dates = []
    current_date = start_date
    while len(trading_dates) < 7:
        # Skip weekends (Saturday=5, Sunday=6)
        if current_date.weekday() < 5:
            trading_dates.append(current_date)
        current_date += timedelta(days=1)
    
    fixture_data = {}
    
    for stream_id in streams:
        trades = []
        
        for i, trade_date in enumerate(trading_dates):
            # Create deterministic trade data
            trade = {
                'Date': trade_date.strftime('%Y-%m-%d'),
                'Time': f'09:{30 + i % 2:02d}:00',  # Alternate between 09:30 and 09:31
                'Instrument': stream_id.replace('1', ''),
                'Session': 'S1' if i % 2 == 0 else 'S2',
                'Direction': 'Long' if i % 2 == 0 else 'Short',
                'Result': 'WIN' if i % 3 != 0 else 'LOSS',  # 2 wins, 1 loss pattern
                'Entry': 100.0 + i * 0.5,
                'Exit': 100.5 + i * 0.5,
                'StopLoss': 99.0 + i * 0.5,
                'scf_s1': 1.0,
                'scf_s2': 1.0,
            }
            trades.append(trade)
        
        df = pd.DataFrame(trades)
        
        # Ensure trade_date column exists (canonical)
        df['trade_date'] = pd.to_datetime(df['Date'])
        
        # Ensure entry_time column exists (canonical)
        df['entry_time'] = df['Time']
        
        fixture_data[stream_id] = df
    
    return fixture_data


def create_test_analyzer_output(
    streams: List[str],
    dates: List[str],
    times: List[str]
) -> pd.DataFrame:
    """
    Create test analyzer output DataFrame for testing.
    
    Args:
        streams: List of stream IDs (e.g., ['ES1', 'ES2'])
        dates: List of date strings in YYYY-MM-DD format
        times: List of time strings (e.g., ['07:30', '08:00'])
        
    Returns:
        DataFrame with analyzer output format (Date, Time, Stream, Result, etc.)
    """
    all_trades = []
    
    for stream_id in streams:
        instrument = stream_id[:-1]  # Remove trailing digit
        session = 'S1' if times[0] in ['07:30', '08:00', '09:00'] else 'S2'
        
        for date_str in dates:
            for time_str in times:
                # Create deterministic trade
                trade = {
                    'Date': date_str,
                    'Time': time_str,
                    'Stream': stream_id,
                    'Instrument': instrument,
                    'Session': session,
                    'Direction': 'Long' if len(all_trades) % 2 == 0 else 'Short',
                    'Result': 'WIN' if len(all_trades) % 3 != 0 else 'LOSS',
                    'Target': 10.0,
                    'Range': 20.0,
                    'StopLoss': 5.0,
                    'Peak': 12.0,
                    'Profit': 10.0 if len(all_trades) % 3 != 0 else -5.0,
                }
                all_trades.append(trade)
    
    df = pd.DataFrame(all_trades)
    
    # Ensure trade_date column exists (canonical) - DataLoader will normalize this
    # For test purposes, create it from Date
    df['trade_date'] = pd.to_datetime(df['Date'], errors='raise')
    
    return df


def save_fixture_parquet():
    """Save fixture data as parquet files matching analyzer output structure."""
    fixture_data = create_fixture_data()
    
    for stream_id, df in fixture_data.items():
        # Create stream directory
        stream_dir = FIXTURE_DIR / stream_id / "2025"
        stream_dir.mkdir(parents=True, exist_ok=True)
        
        # Save as monthly parquet file (matching analyzer output format)
        parquet_file = stream_dir / f"{stream_id}_an_2025_01.parquet"
        df.to_parquet(parquet_file, index=False)
        print(f"Created fixture: {parquet_file}")


if __name__ == "__main__":
    save_fixture_parquet()
    print("Fixture data created successfully!")
