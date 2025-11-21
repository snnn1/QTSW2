"""
Master Matrix - "All trades in order" across all streams

This module creates a unified master table that merges all trades from all streams
(ES1, ES2, GC1, GC2, CL1, CL2, NQ1, NQ2, NG1, NG2, etc.) into one sorted table.

Author: Quantitative Trading System
Date: 2025
"""

import pandas as pd
import numpy as np
from pathlib import Path
from typing import List, Optional, Dict, Tuple
from datetime import datetime, timedelta
import logging

# Configure logging
logging.basicConfig(level=logging.INFO, format='%(asctime)s - %(levelname)s - %(message)s')
logger = logging.getLogger(__name__)


class MasterMatrix:
    """
    Creates a master matrix by merging all trade files from all streams.
    """
    
    def __init__(self, sequencer_runs_dir: str = "data/sequencer_runs",
                 stream_filters: Optional[Dict[str, Dict]] = None):
        """
        Initialize Master Matrix builder.
        
        Args:
            sequencer_runs_dir: Directory containing sequential processor output files organized by stream
            stream_filters: Dictionary mapping stream_id to filter config:
                {
                    "ES1": {
                        "exclude_days_of_week": ["Wednesday"],
                        "exclude_days_of_month": [4, 16, 30],
                        "exclude_times": ["07:30", "08:00"]
                    },
                    ...
                }
        """
        self.sequencer_runs_dir = Path(sequencer_runs_dir)
        self.master_df: Optional[pd.DataFrame] = None
        
        # Streams to process (all "1" and "2" streams)
        self.streams = [
            "ES1", "ES2", "GC1", "GC2", "CL1", "CL2", 
            "NQ1", "NQ2", "NG1", "NG2", "YM1", "YM2"
        ]
        
        # Day-of-month blocked days for "2" streams (default)
        self.dom_blocked_days = {4, 16, 30}
        
        # Per-stream filters
        self.stream_filters = stream_filters or {}
        
        # Initialize default filters for each stream if not provided
        for stream in self.streams:
            if stream not in self.stream_filters:
                self.stream_filters[stream] = {
                    "exclude_days_of_week": [],
                    "exclude_days_of_month": [],
                    "exclude_times": []
                }
        
    def load_all_streams(self, start_date: Optional[str] = None, 
                         end_date: Optional[str] = None,
                         specific_date: Optional[str] = None) -> pd.DataFrame:
        """
        Load all trade files from all streams and merge into one DataFrame.
        
        Args:
            start_date: Start date for backtest period (YYYY-MM-DD) or None for all
            end_date: End date for backtest period (YYYY-MM-DD) or None for all
            specific_date: Specific date to load (YYYY-MM-DD) for "today" mode, or None
            
        Returns:
            Merged DataFrame with all trades from all streams
        """
        logger.info("=" * 80)
        logger.info("MASTER MATRIX - Loading all streams")
        logger.info("=" * 80)
        
        all_trades = []
        streams_loaded = []
        
        for stream_id in self.streams:
            stream_dir = self.sequencer_runs_dir / stream_id
            
            if not stream_dir.exists():
                logger.warning(f"Stream directory not found: {stream_dir}")
                continue
            
            # Only load monthly consolidated files (finished data from data merger)
            # Pattern: <stream>_seq_<year>_<month>.parquet in year subdirectories
            # Example: ES1_seq_2024_11.parquet in ES1/2024/
            # Skip daily temp files in date folders (YYYY-MM-DD/)
            parquet_files = []
            
            # Look for year subdirectories (e.g., ES1/2024/, ES1/2025/)
            for year_dir in sorted(stream_dir.iterdir()):
                if not year_dir.is_dir():
                    continue
                
                # Check if it's a year directory (4 digits) or skip date folders (YYYY-MM-DD)
                year_dir_name = year_dir.name
                if len(year_dir_name) == 4 and year_dir_name.isdigit():
                    # This is a year directory - look for monthly consolidated files
                    monthly_files = sorted(year_dir.glob(f"{stream_id}_seq_*.parquet"))
                    parquet_files.extend(monthly_files)
                # Skip date folders (YYYY-MM-DD format) - these contain daily temp files
            
            if not parquet_files:
                logger.warning(f"No monthly consolidated files found for stream: {stream_id}")
                logger.info(f"  Looked in: {stream_dir}")
                logger.info(f"  Expected pattern: {stream_id}/YYYY/{stream_id}_seq_YYYY_MM.parquet")
                continue
            
            logger.info(f"Loading stream: {stream_id} ({len(parquet_files)} monthly files)")
            
            for file_path in parquet_files:
                try:
                    df = pd.read_parquet(file_path)
                    
                    if df.empty:
                        continue
                    
                    # Ensure Stream column matches
                    if 'Stream' not in df.columns:
                        df['Stream'] = stream_id
                    
                    # Filter by date if specified
                    if specific_date:
                        df['Date'] = pd.to_datetime(df['Date'])
                        df = df[df['Date'].dt.date == pd.to_datetime(specific_date).date()]
                    elif start_date or end_date:
                        df['Date'] = pd.to_datetime(df['Date'])
                        if start_date:
                            df = df[df['Date'] >= pd.to_datetime(start_date)]
                        if end_date:
                            df = df[df['Date'] <= pd.to_datetime(end_date)]
                    
                    if not df.empty:
                        all_trades.append(df)
                        logger.debug(f"  Loaded {len(df)} trades from {file_path.name}")
                        
                except Exception as e:
                    logger.error(f"Error loading {file_path}: {e}")
                    continue
            
            # Check if we loaded any data for this stream
            stream_data_count = sum(len(df) for df in all_trades if not df.empty and 
                                   (df['Stream'].iloc[0] == stream_id if 'Stream' in df.columns and len(df) > 0 else False))
            if stream_data_count > 0:
                streams_loaded.append(stream_id)
        
        if not all_trades:
            logger.warning("No trade data found!")
            return pd.DataFrame()
        
        # Merge all DataFrames
        logger.info(f"Merging {len(all_trades)} data files...")
        master_df = pd.concat(all_trades, ignore_index=True)
        
        logger.info(f"Total trades loaded: {len(master_df)}")
        logger.info(f"Streams loaded: {streams_loaded}")
        
        return master_df
    
    def normalize_schema(self, df: pd.DataFrame) -> pd.DataFrame:
        """
        Normalize fields so every stream has the same schema.
        Adds missing columns with default values.
        
        Args:
            df: Input DataFrame
            
        Returns:
            Normalized DataFrame with consistent schema
        """
        logger.info("Normalizing schema...")
        
        # Required columns from analyzer output
        required_columns = {
            'Date': 'object',
            'Time': 'object',
            'Target': 'float64',
            'Peak': 'float64',
            'Direction': 'object',
            'Result': 'object',
            'Range': 'float64',
            'Stream': 'object',
            'Instrument': 'object',
            'Session': 'object',
            'Profit': 'float64',
        }
        
        # Optional columns (may not exist in all files)
        optional_columns = {
            'scf_s1': 'float64',
            'scf_s2': 'float64',
            'onr': 'float64',
            'onr_high': 'float64',
            'onr_low': 'float64',
        }
        
        # Ensure Date is datetime
        if 'Date' in df.columns:
            if df['Date'].dtype == 'object':
                df['Date'] = pd.to_datetime(df['Date'], errors='coerce')
            elif not pd.api.types.is_datetime64_any_dtype(df['Date']):
                df['Date'] = pd.to_datetime(df['Date'], errors='coerce')
        
        # Add missing required columns
        for col, dtype in required_columns.items():
            if col not in df.columns:
                if col == 'Date':
                    df[col] = pd.NaT
                elif dtype == 'float64':
                    df[col] = np.nan
                elif dtype == 'object':
                    df[col] = ''
                else:
                    df[col] = None
        
        # Add missing optional columns with NaN
        for col, dtype in optional_columns.items():
            if col not in df.columns:
                df[col] = np.nan
        
        # Create derived columns that may not exist
        # entry_time, exit_time (using Time as entry_time, exit_time would be calculated)
        if 'entry_time' not in df.columns:
            df['entry_time'] = df['Time']
        
        if 'exit_time' not in df.columns:
            # For now, set exit_time same as entry_time (would need actual exit logic)
            df['exit_time'] = df['Time']
        
        # entry_price, exit_price (not in analyzer output, create placeholder)
        if 'entry_price' not in df.columns:
            df['entry_price'] = np.nan
        
        if 'exit_price' not in df.columns:
            df['exit_price'] = np.nan
        
        # R (Risk-Reward ratio) - calculate from Profit/Target
        if 'R' not in df.columns:
            df['R'] = df.apply(
                lambda row: row['Profit'] / row['Target'] if pd.notna(row['Target']) and row['Target'] != 0 else np.nan,
                axis=1
            )
        
        # pnl (same as Profit for now)
        if 'pnl' not in df.columns:
            df['pnl'] = df['Profit']
        
        # rs_value (Rolling Sum value - would need to calculate from sequential processor)
        if 'rs_value' not in df.columns:
            df['rs_value'] = np.nan
        
        # selected_time (same as Time for now)
        if 'selected_time' not in df.columns:
            df['selected_time'] = df['Time']
        
        # time_bucket (same as Time for now)
        if 'time_bucket' not in df.columns:
            df['time_bucket'] = df['Time']
        
        # trade_date (same as Date)
        if 'trade_date' not in df.columns:
            if 'Date' in df.columns:
                df['trade_date'] = pd.to_datetime(df['Date']).dt.date
            else:
                df['trade_date'] = None
        
        logger.info(f"Schema normalized. Columns: {list(df.columns)}")
        
        return df
    
    def add_global_columns(self, df: pd.DataFrame) -> pd.DataFrame:
        """
        Add global columns for each trade:
        - global_trade_id (unique id)
        - day_of_month (1-31)
        - dow (Mon-Fri)
        - session_index (1 or 2)
        - is_two_stream (true for *2 streams)
        - dom_blocked (true if day is 4/16/30 and stream is a "2")
        - filter_reasons (list of reasons why trade was filtered)
        - final_allowed (boolean after all filters)
        
        Args:
            df: Input DataFrame
            
        Returns:
            DataFrame with global columns added
        """
        logger.info("Adding global columns...")
        
        # global_trade_id
        df['global_trade_id'] = range(1, len(df) + 1)
        
        # day_of_month
        df['day_of_month'] = df['Date'].dt.day
        
        # dow (day of week) - full name for filtering
        df['dow'] = df['Date'].dt.strftime('%a')
        df['dow_full'] = df['Date'].dt.strftime('%A')  # Full name: Monday, Tuesday, etc.
        
        # month (1-12)
        df['month'] = df['Date'].dt.month
        
        # session_index (1 for S1, 2 for S2)
        df['session_index'] = df['Session'].apply(
            lambda x: 1 if str(x).upper() == 'S1' else 2 if str(x).upper() == 'S2' else None
        )
        
        # is_two_stream (true for *2 streams)
        df['is_two_stream'] = df['Stream'].str.endswith('2')
        
        # dom_blocked (true if day is 4/16/30 and stream is a "2")
        df['dom_blocked'] = (
            df['is_two_stream'] & 
            df['day_of_month'].isin(self.dom_blocked_days)
        )
        
        # Initialize filter reasons and final_allowed
        df['filter_reasons'] = ''
        df['final_allowed'] = True
        
        # Apply per-stream filters
        for stream_id, filters in self.stream_filters.items():
            stream_mask = df['Stream'] == stream_id
            
            # Day of week filter
            if filters.get('exclude_days_of_week'):
                exclude_dows = [d.lower() for d in filters['exclude_days_of_week']]
                dow_mask = stream_mask & df['dow_full'].str.lower().isin(exclude_dows)
                df.loc[dow_mask, 'final_allowed'] = False
                df.loc[dow_mask, 'filter_reasons'] = df.loc[dow_mask, 'filter_reasons'].apply(
                    lambda x: f"{x}, " if x else ""
                ) + f"dow_filter({','.join(exclude_dows)})"
            
            # Day of month filter
            if filters.get('exclude_days_of_month'):
                exclude_doms = filters['exclude_days_of_month']
                dom_mask = stream_mask & df['day_of_month'].isin(exclude_doms)
                df.loc[dom_mask, 'final_allowed'] = False
                df.loc[dom_mask, 'filter_reasons'] = df.loc[dom_mask, 'filter_reasons'].apply(
                    lambda x: f"{x}, " if x else ""
                ) + f"dom_filter({','.join(map(str, exclude_doms))})"
            
            # Time filter
            if filters.get('exclude_times'):
                exclude_times = [str(t) for t in filters['exclude_times']]
                time_mask = stream_mask & df['Time'].isin(exclude_times)
                df.loc[time_mask, 'final_allowed'] = False
                df.loc[time_mask, 'filter_reasons'] = df.loc[time_mask, 'filter_reasons'].apply(
                    lambda x: f"{x}, " if x else ""
                ) + f"time_filter({','.join(exclude_times)})"
        
        # Apply default filters to final_allowed
        # 1. dom_blocked filter (for *2 streams)
        df.loc[df['dom_blocked'], 'final_allowed'] = False
        df.loc[df['dom_blocked'], 'filter_reasons'] = df.loc[df['dom_blocked'], 'filter_reasons'].apply(
            lambda x: f"{x}, " if x else ""
        ) + "dom_blocked_2stream"
        
        # 2. SCF filters (if scf_s1 >= threshold for S1, or scf_s2 >= threshold for S2)
        # Note: Threshold would need to be configured - using 0.5 as default
        scf_threshold = 0.5  # TODO: Make this configurable
        
        s1_blocked = (df['Session'] == 'S1') & (df['scf_s1'] >= scf_threshold)
        s2_blocked = (df['Session'] == 'S2') & (df['scf_s2'] >= scf_threshold)
        df.loc[s1_blocked | s2_blocked, 'final_allowed'] = False
        df.loc[s1_blocked, 'filter_reasons'] = df.loc[s1_blocked, 'filter_reasons'].apply(
            lambda x: f"{x}, " if x else ""
        ) + f"scf_s1_blocked(>={scf_threshold})"
        df.loc[s2_blocked, 'filter_reasons'] = df.loc[s2_blocked, 'filter_reasons'].apply(
            lambda x: f"{x}, " if x else ""
        ) + f"scf_s2_blocked(>={scf_threshold})"
        
        # Clean up filter_reasons (remove leading comma/space)
        df['filter_reasons'] = df['filter_reasons'].str.strip().str.rstrip(',')
        
        logger.info(f"Global columns added. Final allowed trades: {df['final_allowed'].sum()} / {len(df)}")
        
        return df
    
    def build_master_matrix(self, start_date: Optional[str] = None,
                           end_date: Optional[str] = None,
                           specific_date: Optional[str] = None,
                           output_dir: str = "data/master_matrix",
                           stream_filters: Optional[Dict[str, Dict]] = None,
                           sequencer_runs_dir: Optional[str] = None) -> pd.DataFrame:
        """
        Build the master matrix by loading, normalizing, and merging all streams.
        
        Args:
            start_date: Start date for backtest period (YYYY-MM-DD) or None for all
            end_date: End date for backtest period (YYYY-MM-DD) or None for all
            specific_date: Specific date to load (YYYY-MM-DD) for "today" mode, or None
            output_dir: Directory to save master matrix files
            stream_filters: Per-stream filter configuration
            sequencer_runs_dir: Override sequencer runs directory (optional)
            
        Returns:
            Master matrix DataFrame sorted by trade_date, entry_time, symbol, stream_id
        """
        logger.info("=" * 80)
        logger.info("BUILDING MASTER MATRIX")
        logger.info("=" * 80)
        
        # Override sequencer_runs_dir if provided
        if sequencer_runs_dir:
            self.sequencer_runs_dir = Path(sequencer_runs_dir)
        
        # Load all streams
        df = self.load_all_streams(start_date, end_date, specific_date)
        
        if df.empty:
            logger.warning("No data loaded!")
            return pd.DataFrame()
        
        # Update stream filters if provided
        if stream_filters:
            self.stream_filters = stream_filters
            # Initialize defaults for streams not in provided filters
            for stream in self.streams:
                if stream not in self.stream_filters:
                    self.stream_filters[stream] = {
                        "exclude_days_of_week": [],
                        "exclude_days_of_month": [],
                        "exclude_times": []
                    }
        
        # Normalize schema
        df = self.normalize_schema(df)
        
        # Add global columns (applies filters)
        df = self.add_global_columns(df)
        
        # Sort by: trade_date, then entry_time, then symbol, then stream_id
        df = df.sort_values(
            by=['trade_date', 'entry_time', 'Instrument', 'Stream'],
            ascending=[True, True, True, True]
        ).reset_index(drop=True)
        
        # Update global_trade_id after sorting
        df['global_trade_id'] = range(1, len(df) + 1)
        
        self.master_df = df
        
        logger.info(f"Master matrix built: {len(df)} trades")
        logger.info(f"Date range: {df['trade_date'].min()} to {df['trade_date'].max()}")
        logger.info(f"Streams: {sorted(df['Stream'].unique())}")
        logger.info(f"Instruments: {sorted(df['Instrument'].unique())}")
        
        # Save master matrix
        output_path = Path(output_dir)
        output_path.mkdir(parents=True, exist_ok=True)
        
        timestamp = datetime.now().strftime('%Y%m%d_%H%M%S')
        
        if specific_date:
            # Save as "today" file
            parquet_file = output_path / f"master_matrix_today_{specific_date.replace('-', '')}.parquet"
            json_file = output_path / f"master_matrix_today_{specific_date.replace('-', '')}.json"
        else:
            # Save as full backtest file
            parquet_file = output_path / f"master_matrix_{timestamp}.parquet"
            json_file = output_path / f"master_matrix_{timestamp}.json"
        
        # Save as Parquet
        df.to_parquet(parquet_file, index=False, compression='snappy')
        logger.info(f"Saved: {parquet_file}")
        
        # Save as JSON (for easy inspection)
        df.to_json(json_file, orient='records', date_format='iso', indent=2)
        logger.info(f"Saved: {json_file}")
        
        return df
    
    def get_master_matrix(self) -> pd.DataFrame:
        """
        Get the current master matrix DataFrame.
        
        Returns:
            Master matrix DataFrame or empty DataFrame if not built yet
        """
        if self.master_df is None:
            logger.warning("Master matrix not built yet. Call build_master_matrix() first.")
            return pd.DataFrame()
        
        return self.master_df.copy()


def main():
    """Main function for command-line usage"""
    import argparse
    
    parser = argparse.ArgumentParser(description='Build Master Matrix from all streams')
    parser.add_argument('--start-date', type=str, help='Start date (YYYY-MM-DD)')
    parser.add_argument('--end-date', type=str, help='End date (YYYY-MM-DD)')
    parser.add_argument('--today', type=str, help='Specific date for today mode (YYYY-MM-DD)')
    parser.add_argument('--sequencer-runs-dir', type=str, default='data/sequencer_runs',
                       help='Directory containing sequential processor output files')
    parser.add_argument('--output-dir', type=str, default='data/master_matrix',
                       help='Output directory for master matrix files')
    
    args = parser.parse_args()
    
    matrix = MasterMatrix(sequencer_runs_dir=args.sequencer_runs_dir)
    master_df = matrix.build_master_matrix(
        start_date=args.start_date,
        end_date=args.end_date,
        specific_date=args.today,
        output_dir=args.output_dir,
        sequencer_runs_dir=args.sequencer_runs_dir
    )
    
    if not master_df.empty:
        print("\n" + "=" * 80)
        print("MASTER MATRIX SUMMARY")
        print("=" * 80)
        print(f"Total trades: {len(master_df)}")
        print(f"Date range: {master_df['trade_date'].min()} to {master_df['trade_date'].max()}")
        print(f"Streams: {sorted(master_df['Stream'].unique())}")
        print(f"Instruments: {sorted(master_df['Instrument'].unique())}")
        print(f"\nFirst 5 trades:")
        print(master_df[['global_trade_id', 'trade_date', 'entry_time', 'Instrument', 'Stream', 
                         'Session', 'Result', 'Profit', 'final_allowed']].head().to_string(index=False))


if __name__ == "__main__":
    main()

