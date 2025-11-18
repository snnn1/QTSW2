#!/usr/bin/env python3
"""
Data Merger / Consolidator

Merges daily analyzer and sequencer files into monthly Parquet files.

Process:
1. Reads daily files from:
   - data/analyzer_temp/YYYY-MM-DD/
   - data/sequencer_temp/YYYY-MM-DD/
2. Merges each day's files into monthly files:
   - Analyzer → data/analyzer_runs/<instrument>/<year>/<instrument>_an_<year>_<month>.parquet
   - Sequencer → data/sequencer_runs/<instrument>/<year>/<instrument>_seq_<year>_<month>.parquet
3. Features:
   - Appends daily rows to monthly file
   - Removes duplicates
   - Sorts rows
   - Creates monthly file if it doesn't exist
   - Skips corrupted daily files
   - Deletes daily temp folder after merge
   - Idempotent (never double-writes data)

Author: Quant Development Environment
Date: 2025
"""

import os
import sys
import logging
import shutil
from pathlib import Path
from datetime import datetime
from typing import List, Dict, Optional, Tuple
import pandas as pd
import json

# Setup logging
logging.basicConfig(
    level=logging.INFO,
    format='%(asctime)s - %(levelname)s - %(message)s',
    handlers=[
        logging.FileHandler('data_merger.log'),
        logging.StreamHandler()
    ]
)
logger = logging.getLogger(__name__)

# Base paths
BASE_DIR = Path(__file__).resolve().parent.parent
DATA_DIR = BASE_DIR / "data"
ANALYZER_TEMP_DIR = DATA_DIR / "analyzer_temp"
SEQUENCER_TEMP_DIR = DATA_DIR / "sequencer_temp"
ANALYZER_RUNS_DIR = DATA_DIR / "analyzer_runs"
SEQUENCER_RUNS_DIR = DATA_DIR / "sequencer_runs"
PROCESSED_LOG_FILE = DATA_DIR / "merger_processed.json"


class DataMerger:
    """Merges daily analyzer and sequencer files into monthly Parquet files."""
    
    def __init__(self):
        """Initialize the data merger."""
        self.processed_log = self._load_processed_log()
        self._ensure_directories()
    
    def _ensure_directories(self):
        """Create all necessary directories."""
        # Base directories
        directories = [
            ANALYZER_TEMP_DIR,
            SEQUENCER_TEMP_DIR,
            ANALYZER_RUNS_DIR,
            SEQUENCER_RUNS_DIR,
        ]
        for directory in directories:
            directory.mkdir(parents=True, exist_ok=True)
            logger.info(f"Ensured directory exists: {directory}")
        
        # Create instrument folders in runs directories
        # Common instruments: ES, NQ, YM, CL, GC, NG
        instruments = ["ES", "NQ", "YM", "CL", "GC", "NG"]
        
        # Create analyzer_runs/<instrument> folders
        for instrument in instruments:
            analyzer_instrument_dir = ANALYZER_RUNS_DIR / instrument
            analyzer_instrument_dir.mkdir(parents=True, exist_ok=True)
            logger.info(f"Ensured analyzer instrument directory exists: {analyzer_instrument_dir}")
        
        # Create sequencer_runs/<instrument> folders
        for instrument in instruments:
            sequencer_instrument_dir = SEQUENCER_RUNS_DIR / instrument
            sequencer_instrument_dir.mkdir(parents=True, exist_ok=True)
            logger.info(f"Ensured sequencer instrument directory exists: {sequencer_instrument_dir}")
    
    def _load_processed_log(self) -> Dict:
        """Load the log of processed daily folders."""
        if PROCESSED_LOG_FILE.exists():
            try:
                with open(PROCESSED_LOG_FILE, 'r') as f:
                    return json.load(f)
            except Exception as e:
                logger.warning(f"Error loading processed log: {e}. Starting fresh.")
                return {"analyzer": [], "sequencer": []}
        return {"analyzer": [], "sequencer": []}
    
    def _save_processed_log(self):
        """Save the log of processed daily folders."""
        try:
            with open(PROCESSED_LOG_FILE, 'w') as f:
                json.dump(self.processed_log, f, indent=2)
        except Exception as e:
            logger.error(f"Error saving processed log: {e}")
    
    def _mark_folder_processed(self, folder_type: str, folder_path: str):
        """Mark a daily folder as processed."""
        if folder_type not in self.processed_log:
            self.processed_log[folder_type] = []
        if folder_path not in self.processed_log[folder_type]:
            self.processed_log[folder_type].append(folder_path)
            self._save_processed_log()
    
    def _is_folder_processed(self, folder_type: str, folder_path: str) -> bool:
        """Check if a daily folder has already been processed."""
        return folder_path in self.processed_log.get(folder_type, [])
    
    def _get_daily_folders(self, temp_dir: Path) -> List[Path]:
        """Get all daily folders (YYYY-MM-DD format) from temp directory."""
        if not temp_dir.exists():
            return []
        
        folders = []
        for item in temp_dir.iterdir():
            if item.is_dir():
                # Check if folder name matches YYYY-MM-DD format
                try:
                    datetime.strptime(item.name, "%Y-%m-%d")
                    folders.append(item)
                except ValueError:
                    logger.warning(f"Skipping non-date folder: {item.name}")
        
        return sorted(folders)
    
    def _get_parquet_files(self, folder: Path) -> List[Path]:
        """Get all Parquet files from a folder."""
        if not folder.exists():
            return []
        return sorted(folder.glob("*.parquet"))
    
    def _detect_instrument_from_file(self, file_path: Path) -> Optional[str]:
        """Detect instrument from file content (Instrument column) first, then filename."""
        # Always prefer Instrument column from data over filename
        try:
            df = pd.read_parquet(file_path, nrows=100)  # Read first 100 rows for better detection
            if 'Instrument' in df.columns:
                unique_instruments = df['Instrument'].dropna().unique()
                if len(unique_instruments) == 1:
                    instrument = str(unique_instruments[0]).upper().strip()
                    logger.info(f"Detected instrument '{instrument}' from Instrument column in {file_path.name}")
                    return instrument
                elif len(unique_instruments) > 1:
                    # Multiple instruments - return None, let the merge function handle splitting
                    logger.info(f"Found multiple instruments {list(unique_instruments)} in {file_path.name}, will split by Instrument column")
                    return None
        except Exception as e:
            logger.warning(f"Could not read file to detect instrument {file_path}: {e}")
        
        # Fallback: Try to extract from filename
        filename = file_path.stem.lower()
        
        # Common instrument patterns in filenames (check in order of specificity)
        # Check for longer/more specific patterns first
        instruments = ["CL", "GC", "NG", "ES", "NQ", "YM"]  # Reordered to check CL first
        for inst in instruments:
            # Use word boundaries to avoid false matches (e.g., "CL" in "CLOSE")
            if f"_{inst.lower()}_" in filename or filename.startswith(f"{inst.lower()}_") or filename.endswith(f"_{inst.lower()}"):
                logger.info(f"Detected instrument '{inst}' from filename {file_path.name}")
                return inst
        
        logger.warning(f"Could not detect instrument from filename or content: {file_path.name}")
        return None
    
    def _get_monthly_file_path(self, instrument: str, year: int, month: int, file_type: str) -> Path:
        """Get the monthly file path for an instrument, year, and month."""
        if file_type == "analyzer":
            base_dir = ANALYZER_RUNS_DIR / instrument / str(year)
            filename = f"{instrument}_an_{year}_{month:02d}.parquet"
        elif file_type == "sequencer":
            base_dir = SEQUENCER_RUNS_DIR / instrument / str(year)
            filename = f"{instrument}_seq_{year}_{month:02d}.parquet"
        else:
            raise ValueError(f"Unknown file_type: {file_type}")
        
        base_dir.mkdir(parents=True, exist_ok=True)
        return base_dir / filename
    
    def _read_daily_file(self, file_path: Path) -> Optional[pd.DataFrame]:
        """Read a daily Parquet file, handling errors gracefully."""
        try:
            df = pd.read_parquet(file_path)
            if df.empty:
                logger.warning(f"Empty file: {file_path}")
                return None
            return df
        except Exception as e:
            logger.error(f"Error reading file {file_path}: {e}. Skipping corrupted file.")
            return None
    
    def _remove_duplicates_analyzer(self, df: pd.DataFrame) -> pd.DataFrame:
        """Remove duplicates from analyzer DataFrame."""
        if df.empty:
            return df
        
        # Analyzer duplicate key: Date, Time, Target, Direction, Session, Instrument
        duplicate_key = ["Date", "Time", "Target", "Direction", "Session", "Instrument"]
        
        # Check which columns exist
        available_cols = [col for col in duplicate_key if col in df.columns]
        if not available_cols:
            logger.warning("No duplicate key columns found. Using all columns.")
            return df.drop_duplicates(keep='first')
        
        initial_count = len(df)
        df_deduped = df.drop_duplicates(subset=available_cols, keep='first')
        duplicates_removed = initial_count - len(df_deduped)
        
        if duplicates_removed > 0:
            logger.info(f"Removed {duplicates_removed} duplicate rows from analyzer data")
        
        return df_deduped
    
    def _remove_duplicates_sequencer(self, df: pd.DataFrame) -> pd.DataFrame:
        """Remove duplicates from sequencer DataFrame."""
        if df.empty:
            return df
        
        # Sequencer duplicate key: Date, Time, Target (or Day, Date, Time, Target if Day exists)
        if 'Day' in df.columns:
            duplicate_key = ["Day", "Date", "Time", "Target"]
        else:
            duplicate_key = ["Date", "Time", "Target"]
        
        # Check which columns exist
        available_cols = [col for col in duplicate_key if col in df.columns]
        if not available_cols:
            logger.warning("No duplicate key columns found. Using all columns.")
            return df.drop_duplicates(keep='first')
        
        initial_count = len(df)
        df_deduped = df.drop_duplicates(subset=available_cols, keep='first')
        duplicates_removed = initial_count - len(df_deduped)
        
        if duplicates_removed > 0:
            logger.info(f"Removed {duplicates_removed} duplicate rows from sequencer data")
        
        return df_deduped
    
    def _sort_analyzer_data(self, df: pd.DataFrame) -> pd.DataFrame:
        """Sort analyzer DataFrame."""
        if df.empty:
            return df
        
        sort_cols = ["Date", "Time"]
        available_cols = [col for col in sort_cols if col in df.columns]
        
        if not available_cols:
            logger.warning("No sort columns found. Returning unsorted data.")
            return df
        
        return df.sort_values(by=available_cols, ascending=[True, True]).reset_index(drop=True)
    
    def _sort_sequencer_data(self, df: pd.DataFrame) -> pd.DataFrame:
        """Sort sequencer DataFrame."""
        if df.empty:
            return df
        
        # Try Day, Date, Time if Day exists, otherwise Date, Time
        if 'Day' in df.columns:
            sort_cols = ["Day", "Date", "Time"]
        else:
            sort_cols = ["Date", "Time"]
        
        available_cols = [col for col in sort_cols if col in df.columns]
        
        if not available_cols:
            logger.warning("No sort columns found. Returning unsorted data.")
            return df
        
        return df.sort_values(by=available_cols, ascending=[True, True, True] if len(available_cols) == 3 else [True, True]).reset_index(drop=True)
    
    def _merge_daily_files(self, daily_files: List[Path], file_type: str) -> Dict[str, pd.DataFrame]:
        """Merge daily files grouped by instrument."""
        # First, collect all dataframes
        all_dfs = []
        
        for file_path in daily_files:
            df = self._read_daily_file(file_path)
            if df is None:
                continue
            
            # If DataFrame has Instrument column, use it to split by instrument
            # Otherwise, try to detect from filename
            if 'Instrument' in df.columns:
                all_dfs.append(df)
            else:
                # No Instrument column - try to detect from filename
                instrument = self._detect_instrument_from_file(file_path)
                if instrument is None:
                    logger.warning(f"Could not detect instrument from {file_path} and no Instrument column. Skipping.")
                    continue
                # Add instrument column if missing
                df['Instrument'] = instrument
                all_dfs.append(df)
        
        if not all_dfs:
            return {}
        
        # Combine all dataframes
        combined_df = pd.concat(all_dfs, ignore_index=True)
        
        # Split by Instrument column if it exists
        result = {}
        if 'Instrument' in combined_df.columns:
            for instrument in combined_df['Instrument'].unique():
                instrument_str = str(instrument).upper().strip()
                if not instrument_str:
                    continue
                
                instrument_df = combined_df[combined_df['Instrument'] == instrument].copy()
                
                # Remove duplicates
                if file_type == "analyzer":
                    instrument_df = self._remove_duplicates_analyzer(instrument_df)
                    instrument_df = self._sort_analyzer_data(instrument_df)
                elif file_type == "sequencer":
                    instrument_df = self._remove_duplicates_sequencer(instrument_df)
                    instrument_df = self._sort_sequencer_data(instrument_df)
                
                if not instrument_df.empty:
                    result[instrument_str] = instrument_df
                    logger.info(f"Grouped {len(instrument_df)} rows for instrument {instrument_str}")
        else:
            # Fallback: try to detect from filename (shouldn't happen if Instrument column exists)
            logger.warning("No Instrument column found in combined data. Using filename detection.")
            # This shouldn't happen, but handle it gracefully
            for file_path in daily_files:
                instrument = self._detect_instrument_from_file(file_path)
                if instrument:
                    if instrument not in result:
                        result[instrument] = pd.DataFrame()
        
        return result
    
    def _merge_with_existing_monthly_file(self, new_data: pd.DataFrame, monthly_file: Path, file_type: str) -> pd.DataFrame:
        """Merge new data with existing monthly file."""
        if monthly_file.exists():
            try:
                existing_df = pd.read_parquet(monthly_file)
                logger.info(f"Loaded existing monthly file: {len(existing_df)} rows")
                
                # Combine with new data
                combined = pd.concat([existing_df, new_data], ignore_index=True)
                
                # Remove duplicates
                if file_type == "analyzer":
                    combined = self._remove_duplicates_analyzer(combined)
                    combined = self._sort_analyzer_data(combined)
                elif file_type == "sequencer":
                    combined = self._remove_duplicates_sequencer(combined)
                    combined = self._sort_sequencer_data(combined)
                
                logger.info(f"After merge and deduplication: {len(combined)} rows")
                return combined
            except Exception as e:
                logger.error(f"Error reading existing monthly file {monthly_file}: {e}. Using new data only.")
                return new_data
        else:
            logger.info(f"Monthly file doesn't exist. Creating new file.")
            return new_data
    
    def _write_monthly_file(self, df: pd.DataFrame, monthly_file: Path):
        """Write DataFrame to monthly Parquet file atomically."""
        if df.empty:
            logger.warning(f"Empty DataFrame. Not writing {monthly_file}")
            return
        
        # Write to temporary file first
        temp_file = monthly_file.with_suffix('.tmp.parquet')
        try:
            df.to_parquet(temp_file, index=False, compression='snappy')
            
            # Atomic rename
            if monthly_file.exists():
                monthly_file.unlink()
            temp_file.rename(monthly_file)
            
            logger.info(f"Wrote {len(df)} rows to {monthly_file}")
        except Exception as e:
            logger.error(f"Error writing monthly file {monthly_file}: {e}")
            if temp_file.exists():
                temp_file.unlink()
            raise
    
    def process_analyzer_folder(self, daily_folder: Path) -> bool:
        """Process a single analyzer daily folder."""
        folder_path_str = str(daily_folder)
        
        # Get Parquet files first to check if folder has content
        parquet_files = self._get_parquet_files(daily_folder)
        
        # If folder is marked as processed but has files, allow reprocessing
        # (files might have been added after processing, or processing might have failed)
        if self._is_folder_processed("analyzer", folder_path_str):
            if parquet_files:
                logger.info(f"Folder {daily_folder.name} marked as processed but contains {len(parquet_files)} file(s). Removing from processed log to allow reprocessing.")
                # Remove from processed log to allow reprocessing
                if folder_path_str in self.processed_log.get("analyzer", []):
                    self.processed_log["analyzer"].remove(folder_path_str)
                    self._save_processed_log()
            else:
                logger.info(f"Skipping already processed analyzer folder (empty): {daily_folder}")
                return True
        
        # Parse date from folder name (used as fallback if Date column not available)
        try:
            folder_date = datetime.strptime(daily_folder.name, "%Y-%m-%d")
            folder_year = folder_date.year
            folder_month = folder_date.month
        except ValueError:
            logger.error(f"Invalid date format in folder name: {daily_folder.name}")
            return False
        
        # Get Parquet files
        parquet_files = self._get_parquet_files(daily_folder)
        if not parquet_files:
            logger.warning(f"No Parquet files found in {daily_folder}")
            return False
        
        logger.info(f"Processing analyzer folder {daily_folder.name}: {len(parquet_files)} files")
        
        # Merge daily files by instrument
        merged_data = self._merge_daily_files(parquet_files, "analyzer")
        
        if not merged_data:
            logger.warning(f"No valid data found in analyzer folder {daily_folder.name}")
            return False
        
        # Write monthly files for each instrument, splitting by month if data spans multiple months
        success_count = 0
        for instrument, df in merged_data.items():
            # Check if Date column exists and split by month
            if 'Date' in df.columns:
                try:
                    # Convert Date to datetime if it's not already
                    df['Date'] = pd.to_datetime(df['Date'], errors='coerce')
                    df['Year'] = df['Date'].dt.year
                    df['Month'] = df['Date'].dt.month
                    
                    # Group by year and month
                    for (year, month), month_df in df.groupby(['Year', 'Month']):
                        if pd.isna(year) or pd.isna(month):
                            logger.warning(f"Skipping rows with invalid dates for {instrument}")
                            continue
                        
                        year = int(year)
                        month = int(month)
                        
                        # Remove temporary columns
                        month_df_clean = month_df.drop(columns=['Year', 'Month'])
                        
                        monthly_file = self._get_monthly_file_path(instrument, year, month, "analyzer")
                        
                        # Merge with existing monthly file
                        combined_df = self._merge_with_existing_monthly_file(month_df_clean, monthly_file, "analyzer")
                        
                        # Write monthly file
                        try:
                            self._write_monthly_file(combined_df, monthly_file)
                            success_count += 1
                            logger.info(f"Created monthly file for {instrument} - {year}-{month:02d}: {len(combined_df)} rows")
                        except Exception as e:
                            logger.error(f"Error writing monthly file for {instrument} {year}-{month:02d}: {e}")
                except Exception as e:
                    logger.error(f"Error processing dates for {instrument}: {e}. Using folder date.")
                    # Fallback to folder date
                    monthly_file = self._get_monthly_file_path(instrument, folder_year, folder_month, "analyzer")
                    combined_df = self._merge_with_existing_monthly_file(df, monthly_file, "analyzer")
                    try:
                        self._write_monthly_file(combined_df, monthly_file)
                        success_count += 1
                    except Exception as e:
                        logger.error(f"Error writing monthly file for {instrument}: {e}")
            else:
                # No Date column - use folder date
                logger.warning(f"No Date column found for {instrument}, using folder date {folder_year}-{folder_month:02d}")
                monthly_file = self._get_monthly_file_path(instrument, folder_year, folder_month, "analyzer")
                combined_df = self._merge_with_existing_monthly_file(df, monthly_file, "analyzer")
                try:
                    self._write_monthly_file(combined_df, monthly_file)
                    success_count += 1
                except Exception as e:
                    logger.error(f"Error writing monthly file for {instrument}: {e}")
        
        if success_count > 0:
            # Mark folder as processed
            self._mark_folder_processed("analyzer", folder_path_str)
            
            # Delete daily folder after successful merge
            try:
                shutil.rmtree(daily_folder)
                logger.info(f"Deleted processed analyzer folder: {daily_folder}")
            except Exception as e:
                logger.error(f"Error deleting folder {daily_folder}: {e}")
            
            return True
        
        return False
    
    def process_sequencer_folder(self, daily_folder: Path) -> bool:
        """Process a single sequencer daily folder."""
        folder_path_str = str(daily_folder)
        
        # Get Parquet files first to check if folder has content
        parquet_files = self._get_parquet_files(daily_folder)
        
        # If folder is marked as processed but has files, allow reprocessing
        # (files might have been added after processing, or processing might have failed)
        if self._is_folder_processed("sequencer", folder_path_str):
            if parquet_files:
                logger.info(f"Folder {daily_folder.name} marked as processed but contains {len(parquet_files)} file(s). Removing from processed log to allow reprocessing.")
                # Remove from processed log to allow reprocessing
                if folder_path_str in self.processed_log.get("sequencer", []):
                    self.processed_log["sequencer"].remove(folder_path_str)
                    self._save_processed_log()
            else:
                logger.info(f"Skipping already processed sequencer folder (empty): {daily_folder}")
                return True
        
        # Parse date from folder name (used as fallback if Date column not available)
        try:
            folder_date = datetime.strptime(daily_folder.name, "%Y-%m-%d")
            folder_year = folder_date.year
            folder_month = folder_date.month
        except ValueError:
            logger.error(f"Invalid date format in folder name: {daily_folder.name}")
            return False
        
        # Get Parquet files
        parquet_files = self._get_parquet_files(daily_folder)
        if not parquet_files:
            logger.warning(f"No Parquet files found in {daily_folder}")
            return False
        
        logger.info(f"Processing sequencer folder {daily_folder.name}: {len(parquet_files)} files")
        
        # Merge daily files by instrument
        merged_data = self._merge_daily_files(parquet_files, "sequencer")
        
        if not merged_data:
            logger.warning(f"No valid data found in sequencer folder {daily_folder.name}")
            return False
        
        # Write monthly files for each instrument, splitting by month if data spans multiple months
        success_count = 0
        for instrument, df in merged_data.items():
            # Check if Date column exists and split by month
            if 'Date' in df.columns:
                try:
                    # Convert Date to datetime if it's not already
                    df['Date'] = pd.to_datetime(df['Date'], errors='coerce')
                    df['Year'] = df['Date'].dt.year
                    df['Month'] = df['Date'].dt.month
                    
                    # Group by year and month
                    for (year, month), month_df in df.groupby(['Year', 'Month']):
                        if pd.isna(year) or pd.isna(month):
                            logger.warning(f"Skipping rows with invalid dates for {instrument}")
                            continue
                        
                        year = int(year)
                        month = int(month)
                        
                        # Remove temporary columns
                        month_df_clean = month_df.drop(columns=['Year', 'Month'])
                        
                        monthly_file = self._get_monthly_file_path(instrument, year, month, "sequencer")
                        
                        # Merge with existing monthly file
                        combined_df = self._merge_with_existing_monthly_file(month_df_clean, monthly_file, "sequencer")
                        
                        # Write monthly file
                        try:
                            self._write_monthly_file(combined_df, monthly_file)
                            success_count += 1
                            logger.info(f"Created monthly file for {instrument} - {year}-{month:02d}: {len(combined_df)} rows")
                        except Exception as e:
                            logger.error(f"Error writing monthly file for {instrument} {year}-{month:02d}: {e}")
                except Exception as e:
                    logger.error(f"Error processing dates for {instrument}: {e}. Using folder date.")
                    # Fallback to folder date
                    monthly_file = self._get_monthly_file_path(instrument, folder_year, folder_month, "sequencer")
                    combined_df = self._merge_with_existing_monthly_file(df, monthly_file, "sequencer")
                    try:
                        self._write_monthly_file(combined_df, monthly_file)
                        success_count += 1
                    except Exception as e:
                        logger.error(f"Error writing monthly file for {instrument}: {e}")
            else:
                # No Date column - use folder date
                logger.warning(f"No Date column found for {instrument}, using folder date {folder_year}-{folder_month:02d}")
                monthly_file = self._get_monthly_file_path(instrument, folder_year, folder_month, "sequencer")
                combined_df = self._merge_with_existing_monthly_file(df, monthly_file, "sequencer")
                try:
                    self._write_monthly_file(combined_df, monthly_file)
                    success_count += 1
                except Exception as e:
                    logger.error(f"Error writing monthly file for {instrument}: {e}")
        
        if success_count > 0:
            # Mark folder as processed
            self._mark_folder_processed("sequencer", folder_path_str)
            
            # Delete daily folder after successful merge
            try:
                shutil.rmtree(daily_folder)
                logger.info(f"Deleted processed sequencer folder: {daily_folder}")
            except Exception as e:
                logger.error(f"Error deleting folder {daily_folder}: {e}")
            
            return True
        
        return False
    
    def run(self):
        """Run the data merger for all daily folders."""
        logger.info("=" * 60)
        logger.info("Starting Data Merger / Consolidator")
        logger.info("=" * 60)
        
        # Process analyzer folders
        analyzer_folders = self._get_daily_folders(ANALYZER_TEMP_DIR)
        logger.info(f"Found {len(analyzer_folders)} analyzer daily folders")
        
        analyzer_success = 0
        for folder in analyzer_folders:
            if self.process_analyzer_folder(folder):
                analyzer_success += 1
        
        logger.info(f"Processed {analyzer_success}/{len(analyzer_folders)} analyzer folders")
        
        # Process sequencer folders
        sequencer_folders = self._get_daily_folders(SEQUENCER_TEMP_DIR)
        logger.info(f"Found {len(sequencer_folders)} sequencer daily folders")
        
        sequencer_success = 0
        for folder in sequencer_folders:
            if self.process_sequencer_folder(folder):
                sequencer_success += 1
        
        logger.info(f"Processed {sequencer_success}/{len(sequencer_folders)} sequencer folders")
        
        logger.info("=" * 60)
        logger.info("Data Merger / Consolidator completed")
        logger.info("=" * 60)


def main():
    """Main entry point."""
    merger = DataMerger()
    merger.run()


if __name__ == "__main__":
    main()

