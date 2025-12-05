#!/usr/bin/env python3
"""
Data Merger / Consolidator

Merges daily analyzer and sequencer files into monthly Parquet files, split by session.

Process:
1. Reads daily files from:
   - data/analyzer_temp/YYYY-MM-DD/
   - data/sequencer_temp/YYYY-MM-DD/
2. Merges each day's files into monthly files, split by session (S1/S2):
   - Analyzer → data/analyzer_runs/<instrument><session>/<year>/<instrument><session>_an_<year>_<month>.parquet
     Example: CL1_an_2025_11.parquet (S1 session), CL2_an_2025_11.parquet (S2 session)
   - Sequencer → data/sequencer_runs/<instrument><session>/<year>/<instrument><session>_seq_<year>_<month>.parquet
     Example: CL1_seq_2025_11.parquet (S1 session), CL2_seq_2025_11.parquet (S2 session)
3. Session Detection:
   - Uses Session column (S1/S2) if present
   - Falls back to Time column inference:
     * S1: 07:30, 08:00, 09:00
     * S2: 09:30, 10:00, 10:30, 11:00
4. Features:
   - Appends daily rows to monthly file
   - Removes duplicates (new data replaces old when duplicates found)
   - Sorts rows
   - Creates monthly file if it doesn't exist
   - Skips corrupted daily files
   - Deletes daily temp folder after merge
   - Idempotent (never double-writes data)
   - Splits data by session automatically (S1 → CL1, S2 → CL2)
   - Prefers new data: when duplicate records exist, keeps the new one and removes the old one

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
        
        # Create instrument-session folders in runs directories
        # Common instruments: ES, NQ, YM, CL, GC, NG
        # Sessions: S1 -> 1, S2 -> 2
        instruments = ["ES", "NQ", "YM", "CL", "GC", "NG"]
        sessions = ["S1", "S2"]
        
        # Create analyzer_runs/<instrument><session> folders (e.g., CL1, CL2, ES1, ES2)
        for instrument in instruments:
            for session in sessions:
                session_suffix = "1" if session == "S1" else "2"
                instrument_session_name = f"{instrument}{session_suffix}"
                analyzer_instrument_dir = ANALYZER_RUNS_DIR / instrument_session_name
                analyzer_instrument_dir.mkdir(parents=True, exist_ok=True)
                logger.info(f"Ensured analyzer instrument-session directory exists: {analyzer_instrument_dir}")
        
        # Create sequencer_runs/<instrument><session> folders (e.g., CL1, CL2, ES1, ES2)
        for instrument in instruments:
            for session in sessions:
                session_suffix = "1" if session == "S1" else "2"
                instrument_session_name = f"{instrument}{session_suffix}"
                sequencer_instrument_dir = SEQUENCER_RUNS_DIR / instrument_session_name
                sequencer_instrument_dir.mkdir(parents=True, exist_ok=True)
                logger.info(f"Ensured sequencer instrument-session directory exists: {sequencer_instrument_dir}")
        
        # Check for and warn about plain instrument folders (should not exist)
        self._check_for_plain_instrument_folders()
    
    def _check_for_plain_instrument_folders(self):
        """Check for plain instrument folders (CL, ES, etc.) that should not exist."""
        instruments = ["ES", "NQ", "YM", "CL", "GC", "NG"]
        runs_dirs = [ANALYZER_RUNS_DIR, SEQUENCER_RUNS_DIR]
        
        for runs_dir in runs_dirs:
            if not runs_dir.exists():
                continue
            
            for item in runs_dir.iterdir():
                if item.is_dir():
                    folder_name = item.name.upper()
                    # Check if this is a plain instrument folder (not instrument-session like CL1, ES2)
                    if folder_name in instruments:
                        logger.warning(f"WARNING: Found plain instrument folder '{item.name}' in {runs_dir.name}. "
                                     f"This folder should not exist - only instrument-session folders like '{item.name}1' and '{item.name}2' should be present.")
                        logger.warning(f"Please manually remove or rename '{item.name}' folder if it's empty or contains old data.")
    
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
            inst_lower = inst.lower()
            # Check for patterns like: GC1_seq_, GC2_seq_, GC1_an_, _GC1_, GC1_, etc.
            # Also check for: _gc_, gc_, _gc (original patterns)
            if (f"{inst_lower}1_" in filename or f"_{inst_lower}2_" in filename or
                filename.startswith(f"{inst_lower}1_") or filename.startswith(f"{inst_lower}2_") or
                f"_{inst_lower}_" in filename or filename.startswith(f"{inst_lower}_") or 
                filename.endswith(f"_{inst_lower}")):
                logger.info(f"Detected instrument '{inst}' from filename {file_path.name}")
                return inst
        
        logger.warning(f"Could not detect instrument from filename or content: {file_path.name}")
        return None
    
    def _get_monthly_file_path(self, instrument: str, session: str, year: int, month: int, file_type: str) -> Path:
        """Get the monthly file path for an instrument, session, year, and month."""
        # Validate session is S1 or S2
        if session not in ['S1', 'S2']:
            raise ValueError(f"Invalid session: {session}. Must be 'S1' or 'S2'")
        
        # Convert session to suffix: S1 -> 1, S2 -> 2
        session_suffix = "1" if session == "S1" else "2"
        instrument_name = f"{instrument}{session_suffix}"  # Always include session suffix
        
        if file_type == "analyzer":
            base_dir = ANALYZER_RUNS_DIR / instrument_name / str(year)
            filename = f"{instrument_name}_an_{year}_{month:02d}.parquet"
        elif file_type == "sequencer":
            base_dir = SEQUENCER_RUNS_DIR / instrument_name / str(year)
            filename = f"{instrument_name}_seq_{year}_{month:02d}.parquet"
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
        """Remove duplicates from analyzer DataFrame.
        
        When duplicates are found, keeps the LAST occurrence (new data replaces old data).
        """
        if df.empty:
            return df
        
        # Analyzer duplicate key: Date, Time, Target, Direction, Session, Instrument
        duplicate_key = ["Date", "Time", "Target", "Direction", "Session", "Instrument"]
        
        # Check which columns exist
        available_cols = [col for col in duplicate_key if col in df.columns]
        if not available_cols:
            logger.warning("No duplicate key columns found. Using all columns.")
            return df.drop_duplicates(keep='last')
        
        initial_count = len(df)
        
        # Remove duplicates, keeping last occurrence (new data replaces old)
        df_deduped = df.drop_duplicates(subset=available_cols, keep='last')
        duplicates_removed = initial_count - len(df_deduped)
        
        if duplicates_removed > 0:
            logger.info(f"Removed {duplicates_removed} duplicate rows from analyzer data (new data replaces old)")
        
        return df_deduped
    
    def _remove_duplicates_sequencer(self, df: pd.DataFrame) -> pd.DataFrame:
        """Remove duplicates from sequencer DataFrame.
        
        When duplicates are found, keeps the LAST occurrence (new data replaces old data).
        """
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
            return df.drop_duplicates(keep='last')
        
        initial_count = len(df)
        df_deduped = df.drop_duplicates(subset=available_cols, keep='last')
        duplicates_removed = initial_count - len(df_deduped)
        
        if duplicates_removed > 0:
            logger.info(f"Removed {duplicates_removed} duplicate rows from sequencer data (new data replaces old)")
        
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
    
    def _merge_daily_files(self, daily_files: List[Path], file_type: str) -> Dict[Tuple[str, str], pd.DataFrame]:
        """Merge daily files grouped by instrument and session.
        
        Returns:
            Dict with keys (instrument, session) and values as DataFrames
        """
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
        
        # Split by Instrument and Session columns
        result = {}
        if 'Instrument' in combined_df.columns:
            # Check if Session column exists
            if 'Session' in combined_df.columns:
                # Split by both Instrument and Session
                for instrument in combined_df['Instrument'].unique():
                    instrument_str = str(instrument).upper().strip()
                    if not instrument_str:
                        continue
                    
                    instrument_df = combined_df[combined_df['Instrument'] == instrument].copy()
                    
                    # Split by Session - first handle valid S1/S2 sessions
                    valid_sessions = {}
                    invalid_rows = pd.DataFrame()
                    
                    for session in instrument_df['Session'].unique():
                        session_str = str(session).strip()
                        if session_str in ['S1', 'S2']:
                            valid_sessions[session_str] = instrument_df[instrument_df['Session'] == session].copy()
                        else:
                            # Collect invalid session rows
                            invalid_rows = pd.concat([invalid_rows, instrument_df[instrument_df['Session'] == session]], ignore_index=True)
                    
                    # Process valid sessions
                    for session_str, session_df in valid_sessions.items():
                        # Remove duplicates
                        if file_type == "analyzer":
                            session_df = self._remove_duplicates_analyzer(session_df)
                            session_df = self._sort_analyzer_data(session_df)
                        elif file_type == "sequencer":
                            session_df = self._remove_duplicates_sequencer(session_df)
                            session_df = self._sort_sequencer_data(session_df)
                        
                        if not session_df.empty:
                            session_key = (instrument_str, session_str)
                            if session_key not in result:
                                result[session_key] = []
                            result[session_key].append(session_df)
                            logger.info(f"Grouped {len(session_df)} rows for {instrument_str} {session_str}")
                    
                    # Process invalid session rows by inferring from Time column
                    if not invalid_rows.empty and 'Time' in invalid_rows.columns:
                        time_slots_s1 = ['07:30', '08:00', '09:00']
                        time_slots_s2 = ['09:30', '10:00', '10:30', '11:00']
                        
                        s1_df = invalid_rows[invalid_rows['Time'].isin(time_slots_s1)].copy()
                        if not s1_df.empty:
                            s1_df['Session'] = 'S1'
                            if file_type == "analyzer":
                                s1_df = self._remove_duplicates_analyzer(s1_df)
                                s1_df = self._sort_analyzer_data(s1_df)
                            elif file_type == "sequencer":
                                s1_df = self._remove_duplicates_sequencer(s1_df)
                                s1_df = self._sort_sequencer_data(s1_df)
                            session_key = (instrument_str, 'S1')
                            if session_key not in result:
                                result[session_key] = []
                            result[session_key].append(s1_df)
                            logger.info(f"Grouped {len(s1_df)} rows for {instrument_str} S1 (inferred from Time)")
                        
                        s2_df = invalid_rows[invalid_rows['Time'].isin(time_slots_s2)].copy()
                        if not s2_df.empty:
                            s2_df['Session'] = 'S2'
                            if file_type == "analyzer":
                                s2_df = self._remove_duplicates_analyzer(s2_df)
                                s2_df = self._sort_analyzer_data(s2_df)
                            elif file_type == "sequencer":
                                s2_df = self._remove_duplicates_sequencer(s2_df)
                                s2_df = self._sort_sequencer_data(s2_df)
                            session_key = (instrument_str, 'S2')
                            if session_key not in result:
                                result[session_key] = []
                            result[session_key].append(s2_df)
                            logger.info(f"Grouped {len(s2_df)} rows for {instrument_str} S2 (inferred from Time)")
            else:
                # No Session column - try to infer from Time column
                logger.warning("No Session column found. Inferring from Time column.")
                time_slots_s1 = ['07:30', '08:00', '09:00']
                time_slots_s2 = ['09:30', '10:00', '10:30', '11:00']
                
                for instrument in combined_df['Instrument'].unique():
                    instrument_str = str(instrument).upper().strip()
                    if not instrument_str:
                        continue
                    
                    instrument_df = combined_df[combined_df['Instrument'] == instrument].copy()
                    
                    # Split by inferred session from Time
                    if 'Time' in instrument_df.columns:
                        s1_df = instrument_df[instrument_df['Time'].isin(time_slots_s1)].copy()
                        s2_df = instrument_df[instrument_df['Time'].isin(time_slots_s2)].copy()
                        
                        if not s1_df.empty:
                            s1_df['Session'] = 'S1'
                            if file_type == "analyzer":
                                s1_df = self._remove_duplicates_analyzer(s1_df)
                                s1_df = self._sort_analyzer_data(s1_df)
                            elif file_type == "sequencer":
                                s1_df = self._remove_duplicates_sequencer(s1_df)
                                s1_df = self._sort_sequencer_data(s1_df)
                            result[(instrument_str, 'S1')] = [s1_df]
                            logger.info(f"Grouped {len(s1_df)} rows for {instrument_str} S1 (inferred from Time)")
                        
                        if not s2_df.empty:
                            s2_df['Session'] = 'S2'
                            if file_type == "analyzer":
                                s2_df = self._remove_duplicates_analyzer(s2_df)
                                s2_df = self._sort_analyzer_data(s2_df)
                            elif file_type == "sequencer":
                                s2_df = self._remove_duplicates_sequencer(s2_df)
                                s2_df = self._sort_sequencer_data(s2_df)
                            result[(instrument_str, 'S2')] = [s2_df]
                            logger.info(f"Grouped {len(s2_df)} rows for {instrument_str} S2 (inferred from Time)")
                    else:
                        # No Time column either - can't infer session
                        logger.warning(f"Cannot infer session for {instrument_str} - no Session or Time column")
        else:
            # Fallback: try to detect from filename (shouldn't happen if Instrument column exists)
            logger.warning("No Instrument column found in combined data. Using filename detection.")
            for file_path in daily_files:
                instrument = self._detect_instrument_from_file(file_path)
                if instrument:
                    if (instrument, 'S1') not in result:
                        result[(instrument, 'S1')] = []
                    if (instrument, 'S2') not in result:
                        result[(instrument, 'S2')] = []
        
        # Combine DataFrames for each (instrument, session) key
        final_result = {}
        for (instrument, session), dfs in result.items():
            if dfs:
                combined = pd.concat(dfs, ignore_index=True)
                final_result[(instrument, session)] = combined
        
        return final_result
    
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
        
        # Write monthly files for each (instrument, session) combination, splitting by month if data spans multiple months
        success_count = 0
        for (instrument, session), df in merged_data.items():
            # Validate that the Instrument column matches what we expect (safety check)
            if 'Instrument' in df.columns:
                unique_instruments = df['Instrument'].dropna().unique()
                unique_instruments_str = [str(inst).upper().strip() for inst in unique_instruments]
                if instrument not in unique_instruments_str:
                    logger.error(f"CRITICAL: Instrument mismatch! Expected {instrument} but found {unique_instruments_str} in data. Skipping this group.")
                    logger.error(f"This indicates corrupted data - Instrument column doesn't match grouping. File would be: {instrument}{session[-1]}_an_*.parquet")
                    continue
                # Check for mixed instruments (shouldn't happen after grouping, but double-check)
                if len(unique_instruments_str) > 1:
                    logger.error(f"CRITICAL: Mixed instruments found in grouped data! Expected {instrument} but found {unique_instruments_str}. Skipping this group.")
                    continue
            
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
                            logger.warning(f"Skipping rows with invalid dates for {instrument} {session}")
                            continue
                        
                        year = int(year)
                        month = int(month)
                        
                        # Remove temporary columns
                        month_df_clean = month_df.drop(columns=['Year', 'Month'])
                        
                        monthly_file = self._get_monthly_file_path(instrument, session, year, month, "analyzer")
                        
                        # Merge with existing monthly file
                        combined_df = self._merge_with_existing_monthly_file(month_df_clean, monthly_file, "analyzer")
                        
                        # Validate combined data AFTER merging (to catch corrupted existing files)
                        if 'Instrument' in combined_df.columns:
                            combined_instruments = combined_df['Instrument'].dropna().unique()
                            combined_instruments_str = [str(inst).upper().strip() for inst in combined_instruments]
                            if instrument not in combined_instruments_str:
                                logger.error(f"CRITICAL: After merging, instrument mismatch detected! Expected {instrument} but found {combined_instruments_str} in combined data.")
                                logger.error(f"This indicates the existing file {monthly_file.name} is corrupted. Skipping write to prevent further corruption.")
                                logger.error(f"Please manually delete or fix the corrupted file: {monthly_file}")
                                continue
                            if len(combined_instruments_str) > 1:
                                logger.error(f"CRITICAL: After merging, mixed instruments detected! Expected {instrument} but found {combined_instruments_str} in combined data.")
                                logger.error(f"This indicates the existing file {monthly_file.name} contains mixed instruments. Skipping write to prevent further corruption.")
                                logger.error(f"Please manually delete or fix the corrupted file: {monthly_file}")
                                continue
                        
                        # Write monthly file
                        try:
                            self._write_monthly_file(combined_df, monthly_file)
                            success_count += 1
                            logger.info(f"Created monthly file for {instrument}{session[-1]} - {year}-{month:02d}: {len(combined_df)} rows")
                        except Exception as e:
                            logger.error(f"Error writing monthly file for {instrument}{session[-1]} {year}-{month:02d}: {e}")
                except Exception as e:
                    logger.error(f"Error processing dates for {instrument} {session}: {e}. Using folder date.")
                    # Fallback to folder date
                    monthly_file = self._get_monthly_file_path(instrument, session, folder_year, folder_month, "analyzer")
                    combined_df = self._merge_with_existing_monthly_file(df, monthly_file, "analyzer")
                    
                    # Validate combined data AFTER merging (to catch corrupted existing files)
                    if 'Instrument' in combined_df.columns:
                        combined_instruments = combined_df['Instrument'].dropna().unique()
                        combined_instruments_str = [str(inst).upper().strip() for inst in combined_instruments]
                        if instrument not in combined_instruments_str:
                            logger.error(f"CRITICAL: After merging (fallback), instrument mismatch detected! Expected {instrument} but found {combined_instruments_str}.")
                            logger.error(f"Corrupted file: {monthly_file}. Skipping write.")
                            continue
                        if len(combined_instruments_str) > 1:
                            logger.error(f"CRITICAL: After merging (fallback), mixed instruments detected! Expected {instrument} but found {combined_instruments_str}.")
                            logger.error(f"Corrupted file: {monthly_file}. Skipping write.")
                            continue
                    
                    try:
                        self._write_monthly_file(combined_df, monthly_file)
                        success_count += 1
                    except Exception as e:
                        logger.error(f"Error writing monthly file for {instrument}{session[-1]}: {e}")
            else:
                # No Date column - use folder date
                logger.warning(f"No Date column found for {instrument} {session}, using folder date {folder_year}-{folder_month:02d}")
                monthly_file = self._get_monthly_file_path(instrument, session, folder_year, folder_month, "analyzer")
                combined_df = self._merge_with_existing_monthly_file(df, monthly_file, "analyzer")
                
                # Validate combined data AFTER merging (to catch corrupted existing files)
                if 'Instrument' in combined_df.columns:
                    combined_instruments = combined_df['Instrument'].dropna().unique()
                    combined_instruments_str = [str(inst).upper().strip() for inst in combined_instruments]
                    if instrument not in combined_instruments_str:
                        logger.error(f"CRITICAL: After merging (no date), instrument mismatch detected! Expected {instrument} but found {combined_instruments_str}.")
                        logger.error(f"Corrupted file: {monthly_file}. Skipping write.")
                        continue
                    if len(combined_instruments_str) > 1:
                        logger.error(f"CRITICAL: After merging (no date), mixed instruments detected! Expected {instrument} but found {combined_instruments_str}.")
                        logger.error(f"Corrupted file: {monthly_file}. Skipping write.")
                        continue
                
                try:
                    self._write_monthly_file(combined_df, monthly_file)
                    success_count += 1
                except Exception as e:
                    logger.error(f"Error writing monthly file for {instrument}{session[-1]}: {e}")
        
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
        
        # Write monthly files for each (instrument, session) combination, splitting by month if data spans multiple months
        success_count = 0
        for (instrument, session), df in merged_data.items():
            # Validate that the Instrument column matches what we expect (safety check)
            if 'Instrument' in df.columns:
                unique_instruments = df['Instrument'].dropna().unique()
                unique_instruments_str = [str(inst).upper().strip() for inst in unique_instruments]
                if instrument not in unique_instruments_str:
                    logger.error(f"CRITICAL: Instrument mismatch! Expected {instrument} but found {unique_instruments_str} in data. Skipping this group.")
                    logger.error(f"This indicates corrupted data - Instrument column doesn't match grouping. File would be: {instrument}{session[-1]}_seq_*.parquet")
                    continue
                # Check for mixed instruments (shouldn't happen after grouping, but double-check)
                if len(unique_instruments_str) > 1:
                    logger.error(f"CRITICAL: Mixed instruments found in grouped data! Expected {instrument} but found {unique_instruments_str}. Skipping this group.")
                    continue
            
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
                            logger.warning(f"Skipping rows with invalid dates for {instrument} {session}")
                            continue
                        
                        year = int(year)
                        month = int(month)
                        
                        # Remove temporary columns
                        month_df_clean = month_df.drop(columns=['Year', 'Month'])
                        
                        monthly_file = self._get_monthly_file_path(instrument, session, year, month, "sequencer")
                        
                        # Merge with existing monthly file
                        combined_df = self._merge_with_existing_monthly_file(month_df_clean, monthly_file, "sequencer")
                        
                        # Validate combined data AFTER merging (to catch corrupted existing files)
                        if 'Instrument' in combined_df.columns:
                            combined_instruments = combined_df['Instrument'].dropna().unique()
                            combined_instruments_str = [str(inst).upper().strip() for inst in combined_instruments]
                            if instrument not in combined_instruments_str:
                                logger.error(f"CRITICAL: After merging, instrument mismatch detected! Expected {instrument} but found {combined_instruments_str} in combined data.")
                                logger.error(f"This indicates the existing file {monthly_file.name} is corrupted. Skipping write to prevent further corruption.")
                                logger.error(f"Please manually delete or fix the corrupted file: {monthly_file}")
                                continue
                            if len(combined_instruments_str) > 1:
                                logger.error(f"CRITICAL: After merging, mixed instruments detected! Expected {instrument} but found {combined_instruments_str} in combined data.")
                                logger.error(f"This indicates the existing file {monthly_file.name} contains mixed instruments. Skipping write to prevent further corruption.")
                                logger.error(f"Please manually delete or fix the corrupted file: {monthly_file}")
                                continue
                        
                        # Write monthly file
                        try:
                            self._write_monthly_file(combined_df, monthly_file)
                            success_count += 1
                            logger.info(f"Created monthly file for {instrument}{session[-1]} - {year}-{month:02d}: {len(combined_df)} rows")
                        except Exception as e:
                            logger.error(f"Error writing monthly file for {instrument}{session[-1]} {year}-{month:02d}: {e}")
                except Exception as e:
                    logger.error(f"Error processing dates for {instrument} {session}: {e}. Using folder date.")
                    # Fallback to folder date
                    monthly_file = self._get_monthly_file_path(instrument, session, folder_year, folder_month, "sequencer")
                    combined_df = self._merge_with_existing_monthly_file(df, monthly_file, "sequencer")
                    
                    # Validate combined data AFTER merging (to catch corrupted existing files)
                    if 'Instrument' in combined_df.columns:
                        combined_instruments = combined_df['Instrument'].dropna().unique()
                        combined_instruments_str = [str(inst).upper().strip() for inst in combined_instruments]
                        if instrument not in combined_instruments_str:
                            logger.error(f"CRITICAL: After merging (fallback), instrument mismatch detected! Expected {instrument} but found {combined_instruments_str}.")
                            logger.error(f"Corrupted file: {monthly_file}. Skipping write.")
                            continue
                        if len(combined_instruments_str) > 1:
                            logger.error(f"CRITICAL: After merging (fallback), mixed instruments detected! Expected {instrument} but found {combined_instruments_str}.")
                            logger.error(f"Corrupted file: {monthly_file}. Skipping write.")
                            continue
                    
                    try:
                        self._write_monthly_file(combined_df, monthly_file)
                        success_count += 1
                    except Exception as e:
                        logger.error(f"Error writing monthly file for {instrument}{session[-1]}: {e}")
            else:
                # No Date column - use folder date
                logger.warning(f"No Date column found for {instrument} {session}, using folder date {folder_year}-{folder_month:02d}")
                monthly_file = self._get_monthly_file_path(instrument, session, folder_year, folder_month, "sequencer")
                combined_df = self._merge_with_existing_monthly_file(df, monthly_file, "sequencer")
                
                # Validate combined data AFTER merging (to catch corrupted existing files)
                if 'Instrument' in combined_df.columns:
                    combined_instruments = combined_df['Instrument'].dropna().unique()
                    combined_instruments_str = [str(inst).upper().strip() for inst in combined_instruments]
                    if instrument not in combined_instruments_str:
                        logger.error(f"CRITICAL: After merging (no date), instrument mismatch detected! Expected {instrument} but found {combined_instruments_str}.")
                        logger.error(f"Corrupted file: {monthly_file}. Skipping write.")
                        continue
                    if len(combined_instruments_str) > 1:
                        logger.error(f"CRITICAL: After merging (no date), mixed instruments detected! Expected {instrument} but found {combined_instruments_str}.")
                        logger.error(f"Corrupted file: {monthly_file}. Skipping write.")
                        continue
                
                try:
                    self._write_monthly_file(combined_df, monthly_file)
                    success_count += 1
                except Exception as e:
                    logger.error(f"Error writing monthly file for {instrument}{session[-1]}: {e}")
        
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

