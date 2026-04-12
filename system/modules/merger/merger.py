#!/usr/bin/env python3
"""
Data Merger - Canonical Role in QTSW2

Purpose:
The Merger is a deterministic, idempotent consolidation layer that transforms daily 
analyzer outputs into stable, partitioned monthly fact tables, without introducing 
or inferring strategy logic.

What the Merger IS allowed to do:
- Concatenate daily analyzer outputs
- Enforce schema consistency
- Enforce session and instrument identity
- Deduplicate deterministically
- Partition by (instrument, session, year, month)
- Write atomically
- Track processed inputs

What the Merger is NOT allowed to do:
- Infer missing strategy attributes (session, stream, direction)
- Guess instrument identity
- Modify trade outcomes
- Apply filters
- Apply slot switching
- Perform selection logic

Principle: If a required field is missing, fail loudly, not infer.

Required Schema:
- Date: Date of the data (required for partitioning)
- Time: Time slot (required for sorting)
- Session: S1 or S2 (required for partitioning, MUST be present)
- Instrument: Instrument code (required for partitioning, MUST be present)
- Stream: Stream identifier (optional, but if present, included in dedup key)

Deduplication:
- If Stream column is present: Dedup key is [Date, Time, Session, Instrument, Stream]
  - This preserves parallel streams/variants from analyzer
- If Stream column is missing: Asserts upstream invariant that analyzer emits only one row per (Date, Time, Session, Instrument)
  - Fails loudly if multiple rows per slot are found (indicates analyzer bug or missing Stream)

Process:
1. Reads daily files from: data/analyzer_temp/YYYY-MM-DD/
2. Validates required columns (Date, Time, Session, Instrument)
3. Merges each day's files into monthly files, split by session (S1/S2):
   - Analyzer → data/analyzed/<instrument><session>/<year>/<instrument><session>_an_<year>_<month>.parquet
     Example: CL1_an_2025_11.parquet (S1 session), CL2_an_2025_11.parquet (S2 session)
4. Features:
   - Appends daily rows to monthly file
   - Removes duplicates (new data replaces old when duplicates found)
   - Sorts rows by Date, Time
   - Creates monthly file if it doesn't exist
   - Error handling:
     * IO corruption (cannot read parquet): skip file with error log, continue processing
     * Schema violations (missing required columns, invalid dates): fail loudly and abort folder
   - Deletes daily temp folder after merge
   - Idempotent (never double-writes data)
   - Splits data by session automatically (S1 → CL1, S2 → CL2)
   - Prefers new data: when duplicate records exist, keeps the new one and removes the old one
   
5. Idempotency semantics:
   - Folder processing is "best effort per instrument/session group"
   - If any group fails with schema error, folder is not marked processed and remains for remediation
   - Partial writes are safe to reprocess (atomic writes + deterministic dedup prevent double-writes)

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

# Base paths (must be defined before logging setup)
BASE_DIR = Path(__file__).resolve().parent.parent.parent
LOGS_DIR = BASE_DIR / 'logs'

# Ensure logs directory exists before setting up logging
try:
    LOGS_DIR.mkdir(parents=True, exist_ok=True)
except (OSError, PermissionError) as e:
    # If we can't create logs directory, fall back to console-only logging
    print(f"WARNING: Could not create logs directory {LOGS_DIR}: {e}. Using console-only logging.")

# Setup logging
handlers = [logging.StreamHandler()]
if LOGS_DIR.exists():
    try:
        handlers.append(logging.FileHandler(LOGS_DIR / 'data_merger.log'))
    except (OSError, PermissionError) as e:
        print(f"WARNING: Could not create log file: {e}. Using console-only logging.")

logging.basicConfig(
    level=logging.INFO,
    format='%(asctime)s - %(levelname)s - %(message)s',
    handlers=handlers
)
logger = logging.getLogger(__name__)
DATA_DIR = BASE_DIR / "data"
ANALYZER_TEMP_DIR = DATA_DIR / "analyzer_temp"
MANUAL_ANALYZER_RUNS_DIR = DATA_DIR / "manual_analyzer_runs"
ANALYZER_RUNS_DIR = DATA_DIR / "analyzed"
PROCESSED_LOG_FILE = DATA_DIR / "merger_processed.json"


class DataMerger:
    """
    Merges daily analyzer files into monthly Parquet files.
    
    Canonical Role: Deterministic consolidation layer that transforms daily analyzer 
    outputs into stable, partitioned monthly fact tables without inferring strategy logic.
    """
    
    # Required columns for analyzer output schema
    REQUIRED_COLUMNS = ["Date", "Time", "Session", "Instrument"]
    
    def __init__(self):
        """Initialize the data merger."""
        self.processed_log = self._load_processed_log()
        self._ensure_directories()
    
    def _ensure_directories(self):
        """
        Create base directories only.
        
        Per canonical specification: Directories are created lazily when data arrives.
        The merger does not pre-declare the trading universe - it discovers instruments
        and sessions from the data itself.
        """
        # Base directories only - instrument/session directories created lazily
        directories = [
            ANALYZER_TEMP_DIR,
            MANUAL_ANALYZER_RUNS_DIR,
            ANALYZER_RUNS_DIR,
        ]
        for directory in directories:
            directory.mkdir(parents=True, exist_ok=True)
            logger.info(f"Ensured base directory exists: {directory}")
        
        # Check for and warn about plain instrument folders (should not exist)
        self._check_for_plain_instrument_folders()
    
    def _check_for_plain_instrument_folders(self):
        """
        Check for plain instrument folders (CL, ES, etc.) that should not exist.
        
        Per canonical specification: Uses pattern detection, not hard-coded instrument lists.
        A plain instrument folder is one that:
        - Matches instrument-like pattern (1-4 uppercase letters)
        - Doesn't end with '1' or '2' (session suffix)
        
        Ignores service folders (e.g., _tmp, _staging, README, logs) and year folders (numeric).
        """
        runs_dirs = [ANALYZER_RUNS_DIR]
        
        for runs_dir in runs_dirs:
            if not runs_dir.exists():
                continue
            
            for item in runs_dir.iterdir():
                if item.is_dir():
                    folder_name = item.name.upper()
                    
                    # Pattern: instrument-like folders are 1-6 alphanumeric uppercase characters
                    # Examples: CL, ES, NQ, YM, GC, NG, 6E, M6E, RTY (valid instruments)
                    # Not: _tmp, _staging, README, logs, 2025 (service/year folders)
                    # Supports both letter-only (CL, ES) and alphanumeric (6E, M6E) instruments
                    is_instrument_like = (
                        len(folder_name) >= 1 and 
                        len(folder_name) <= 6 and 
                        folder_name.isalnum() and 
                        folder_name.isupper()
                    )
                    
                    # Check if this is a plain instrument folder (not instrument-session like CL1, ES2)
                    # Only warn if it matches instrument pattern AND doesn't end with session suffix
                    if is_instrument_like and not folder_name.endswith('1') and not folder_name.endswith('2'):
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
                return {"analyzer": []}
        return {"analyzer": []}
    
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
    
    def process_manual_analyzer_folder(self, daily_folder: Path) -> bool:
        """
        Process a single manual analyzer daily folder.
        
        This is the same as process_analyzer_folder but tracks it separately
        in the processed log under 'manual_analyzer' key.
        """
        folder_path_str = str(daily_folder)
        
        # Get Parquet files first to check if folder has content
        parquet_files = self._get_parquet_files(daily_folder)
        
        # If folder is marked as processed but has files, allow reprocessing
        if self._is_folder_processed("manual_analyzer", folder_path_str):
            if parquet_files:
                logger.info(f"Folder {daily_folder.name} marked as processed but contains {len(parquet_files)} file(s). Removing from processed log to allow reprocessing.")
                if folder_path_str in self.processed_log.get("manual_analyzer", []):
                    self.processed_log["manual_analyzer"].remove(folder_path_str)
                    self._save_processed_log()
            else:
                logger.info(f"Skipping already processed manual analyzer folder (empty): {daily_folder}")
                return True
        
        # Validate folder name format (must be YYYY-MM-DD)
        try:
            datetime.strptime(daily_folder.name, "%Y-%m-%d")
        except ValueError:
            logger.error(f"Invalid date format in folder name: {daily_folder.name}. Expected YYYY-MM-DD format.")
            return False
        
        # Get Parquet files
        parquet_files = self._get_parquet_files(daily_folder)
        if not parquet_files:
            logger.warning(f"No Parquet files found in {daily_folder}")
            return False
        
        logger.info(f"Processing manual analyzer folder {daily_folder.name}: {len(parquet_files)} files")
        
        # Merge daily files by instrument (same logic as regular analyzer)
        try:
            merged_data = self._merge_daily_files(parquet_files, "analyzer")
        except ValueError as e:
            error_msg = (
                f"SCHEMA VALIDATION FAILED for manual analyzer folder {daily_folder.name}: {e}. "
                f"Merger does not infer missing strategy attributes - analyzer must provide all required fields. "
                f"Required columns: {self.REQUIRED_COLUMNS}"
            )
            logger.error(error_msg)
            raise ValueError(error_msg) from e
        except Exception as e:
            logger.error(f"Unexpected error merging files in {daily_folder.name}: {e}")
            return False
        
        if not merged_data:
            logger.warning(f"No valid data found in manual analyzer folder {daily_folder.name}")
            return False
        
        # Write monthly files for each (instrument, session) combination
        success_count = 0
        for (instrument, session), df in merged_data.items():
            # Validate Instrument column
            if 'Instrument' in df.columns:
                unique_instruments = df['Instrument'].dropna().unique()
                unique_instruments_str = [str(inst).upper().strip() for inst in unique_instruments]
                if instrument not in unique_instruments_str:
                    logger.error(f"CRITICAL: Instrument mismatch! Expected {instrument} but found {unique_instruments_str} in data. Skipping this group.")
                    continue
                if len(unique_instruments_str) > 1:
                    logger.error(f"CRITICAL: Mixed instruments found in grouped data! Expected {instrument} but found {unique_instruments_str}. Skipping this group.")
                    continue
            
            if 'Date' not in df.columns:
                error_msg = (
                    f"REQUIRED COLUMN MISSING: Date column not found for {instrument} {session}. "
                    f"Required columns: {self.REQUIRED_COLUMNS}."
                )
                logger.error(error_msg)
                raise ValueError(error_msg)
            
            try:
                # Validate and parse dates
                date_series = pd.to_datetime(df['Date'], errors='coerce')
                invalid_mask = date_series.isna()
                if invalid_mask.any():
                    invalid_count = invalid_mask.sum()
                    invalid_examples = df.loc[invalid_mask, 'Date'].head(5).tolist()
                    error_msg = (
                        f"INVALID DATE VALUES found for {instrument} {session}: "
                        f"{invalid_count} row(s) with unparseable dates. Examples: {invalid_examples}."
                    )
                    logger.error(error_msg)
                    raise ValueError(error_msg)
                
                df['Date'] = date_series
                df['Year'] = df['Date'].dt.year
                df['Month'] = df['Date'].dt.month
                
                # Group by year and month
                for (year, month), month_df in df.groupby(['Year', 'Month']):
                    year = int(year)
                    month = int(month)
                    
                    month_df_clean = month_df.drop(columns=['Year', 'Month'])
                    monthly_file = self._get_monthly_file_path(instrument, session, year, month, "analyzer")
                    
                    # Merge with existing monthly file
                    combined_df = self._merge_with_existing_monthly_file(month_df_clean, monthly_file, "analyzer")
                    
                    # Validate combined data
                    if 'Instrument' in combined_df.columns:
                        combined_instruments = combined_df['Instrument'].dropna().unique()
                        combined_instruments_str = [str(inst).upper().strip() for inst in combined_instruments]
                        if instrument not in combined_instruments_str or len(combined_instruments_str) > 1:
                            logger.error(f"CRITICAL: After merging, instrument mismatch detected! Expected {instrument} but found {combined_instruments_str}. Skipping write.")
                            continue
                    
                    # Write monthly file
                    try:
                        self._write_monthly_file(combined_df, monthly_file)
                        success_count += 1
                        logger.info(f"Created monthly file for {instrument}{session[-1]} - {year}-{month:02d}: {len(combined_df)} rows (from manual run)")
                    except Exception as e:
                        logger.error(f"Error writing monthly file for {instrument}{session[-1]} {year}-{month:02d}: {e}")
            except ValueError:
                raise
            except Exception as e:
                error_msg = f"Error processing dates for {instrument} {session}: {e}."
                logger.error(error_msg)
                raise ValueError(error_msg) from e
        
        if success_count > 0:
            # Mark folder as processed (under 'manual_analyzer' key)
            self._mark_folder_processed("manual_analyzer", folder_path_str)
            
            # Note: We don't delete manual analyzer folders (they're kept for reference)
            logger.info(f"Processed manual analyzer folder: {daily_folder} (kept for reference)")
            
            return True
        
        return False
    
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
        else:
            raise ValueError(f"Unknown file_type: {file_type}. Only 'analyzer' is supported.")
        
        base_dir.mkdir(parents=True, exist_ok=True)
        return base_dir / filename
    
    def _validate_schema(self, df: pd.DataFrame, file_path: Path) -> bool:
        """
        Validate that DataFrame has all required columns.
        
        Fails loudly if required columns are missing (per canonical specification).
        
        Args:
            df: DataFrame to validate
            file_path: Path to file (for error messages)
        
        Returns:
            True if valid, False otherwise
        
        Raises:
            ValueError: If required columns are missing
        """
        if df.empty:
            logger.warning(f"Empty file: {file_path}")
            return False
        
        missing_cols = [col for col in self.REQUIRED_COLUMNS if col not in df.columns]
        if missing_cols:
            error_msg = (
                f"REQUIRED COLUMNS MISSING in {file_path.name}: {missing_cols}. "
                f"Required columns: {self.REQUIRED_COLUMNS}. "
                f"Merger does not infer missing strategy attributes - analyzer must provide all required fields."
            )
            logger.error(error_msg)
            raise ValueError(error_msg)
        
        # Validate Session values are S1 or S2
        if 'Session' in df.columns:
            invalid_sessions = df[~df['Session'].isin(['S1', 'S2'])]['Session'].unique()
            if len(invalid_sessions) > 0:
                error_msg = (
                    f"INVALID SESSION VALUES in {file_path.name}: {list(invalid_sessions)}. "
                    f"Session must be 'S1' or 'S2'. Found invalid values."
                )
                logger.error(error_msg)
                raise ValueError(error_msg)
        
        return True
    
    def _read_daily_file(self, file_path: Path) -> Optional[pd.DataFrame]:
        """
        Read a daily Parquet file, validating schema.
        
        Fails loudly if required columns are missing (per canonical specification).
        Normalizes column names: renames legacy "SL" to "StopLoss" for consistency.
        """
        try:
            df = pd.read_parquet(file_path)
            if df.empty:
                logger.warning(f"Empty file: {file_path}")
                return None
            
            # Normalize column names: rename legacy "SL" to "StopLoss" if it exists
            # This ensures consistent output schema regardless of source file age
            if 'SL' in df.columns and 'StopLoss' not in df.columns:
                df = df.rename(columns={'SL': 'StopLoss'})
            elif 'SL' in df.columns and 'StopLoss' in df.columns:
                # Both exist - drop SL, keep StopLoss
                df = df.drop(columns=['SL'])
            
            # Validate schema - fails loudly if required columns missing
            self._validate_schema(df, file_path)
            
            return df
        except ValueError:
            # Re-raise validation errors (fail loudly)
            raise
        except Exception as e:
            logger.error(f"Error reading file {file_path}: {e}. Skipping corrupted file.")
            return None
    
    def _remove_duplicates_analyzer(self, df: pd.DataFrame) -> pd.DataFrame:
        """
        Remove duplicates from analyzer DataFrame.
        
        Per canonical specification:
        - Duplicate key includes Stream if present (to prevent silent overwrites of parallel streams)
        - If Stream is missing, asserts upstream invariant: one row per (Date, Time, Session, Instrument)
        - When duplicates are found, keeps the LAST occurrence (new data replaces old data)
        - Additionally checks if profit/ExitTime/ExitPrice differ - if they do, it's an update, not a duplicate
        
        Duplicate key: Date, Time, Session, Instrument, Stream (if Stream present)
        Update check: Profit, ExitTime, ExitPrice (if these differ, it's an update even if dedup key matches)
        """
        if df.empty:
            return df
        
        # Base duplicate key (always required)
        base_key = ["Date", "Time", "Session", "Instrument"]
        
        # Include Stream in dedup key if present (prevents silent overwrites of parallel streams)
        # Per canonical spec: If analyzer emits multiple streams/variants, they must be preserved
        duplicate_key = base_key.copy()
        if 'Stream' in df.columns:
            duplicate_key.append('Stream')
            logger.debug("Including Stream in duplicate key to preserve parallel streams/variants")
        else:
            # Stream not present - assert upstream invariant: one row per slot
            # This documents the coupling and fails loudly if violated
            slot_key = base_key
            available_slot_cols = [col for col in slot_key if col in df.columns]
            if available_slot_cols:
                slot_counts = df[available_slot_cols].value_counts()
                if (slot_counts > 1).any():
                    duplicate_slots = slot_counts[slot_counts > 1]
                    error_msg = (
                        f"UPSTREAM INVARIANT VIOLATION: Multiple rows per (Date, Time, Session, Instrument) found. "
                        f"This indicates analyzer emitted multiple streams/variants without Stream column. "
                        f"Duplicate slots: {duplicate_slots.head(10).to_dict()}. "
                        f"Merger cannot safely deduplicate without Stream column - analyzer must provide Stream or collapse to one row per slot."
                    )
                    logger.error(error_msg)
                    raise ValueError(error_msg)
        
        # Check which columns exist
        available_cols = [col for col in duplicate_key if col in df.columns]
        if not available_cols:
            logger.warning("No duplicate key columns found. Using all columns.")
            return df.drop_duplicates(keep='last')
        
        initial_count = len(df)
        
        # Check for rows with same dedup key but different profit/ExitTime/ExitPrice (these are updates, not duplicates)
        update_check_cols = ['Profit', 'ExitTime', 'ExitPrice']
        available_update_cols = [col for col in update_check_cols if col in df.columns]
        
        true_duplicates = 0
        updates = 0
        
        if available_update_cols:
            # Group by dedup key and check if update fields differ
            grouped = df.groupby(available_cols)
            for key, group in grouped:
                if len(group) > 1:
                    # Check if update fields differ within this group
                    is_update = False
                    for update_col in available_update_cols:
                        unique_values = group[update_col].dropna().unique()
                        if len(unique_values) > 1:
                            is_update = True
                            updates += len(group) - 1
                            logger.debug(f"Found update for {key}: {update_col} differs ({unique_values})")
                            break
                    
                    if not is_update:
                        # All update fields are the same - it's a true duplicate
                        true_duplicates += len(group) - 1
            
            if updates > 0:
                logger.info(f"Found {updates} rows with same dedup key but different profit/ExitTime/ExitPrice - treating as updates (will replace old data)")
            if true_duplicates > 0:
                logger.info(f"Found {true_duplicates} true duplicate rows (same dedup key AND same profit/ExitTime/ExitPrice)")
        
        # Remove duplicates, keeping last occurrence (new data replaces old)
        df_deduped = df.drop_duplicates(subset=available_cols, keep='last')
        duplicates_removed = initial_count - len(df_deduped)
        
        if duplicates_removed > 0:
            if updates > 0:
                logger.info(f"Removed {duplicates_removed} rows from analyzer data ({updates} updates, {true_duplicates} true duplicates) - new data replaces old")
            else:
                logger.info(f"Removed {duplicates_removed} duplicate rows from analyzer data (new data replaces old)")
        
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
    
    def _merge_daily_files(self, daily_files: List[Path], file_type: str) -> Dict[Tuple[str, str], pd.DataFrame]:
        """
        Merge daily files grouped by instrument and session.
        
        Per canonical specification:
        - Requires Instrument and Session columns (validated in _read_daily_file)
        - Does NOT infer Session from Time column
        - Does NOT infer Instrument from filename
        - Fails loudly if required columns are missing
        
        Returns:
            Dict with keys (instrument, session) and values as DataFrames
        
        Raises:
            ValueError: If required columns are missing (fail loudly per canonical spec)
        """
        # First, collect all dataframes (schema validation happens in _read_daily_file)
        all_dfs = []
        
        for file_path in daily_files:
            try:
                df = self._read_daily_file(file_path)
                if df is None:
                    continue
                
                # Schema validation in _read_daily_file ensures Instrument and Session columns exist
                # If Instrument is missing, _read_daily_file will raise ValueError (fail loudly)
                # No fallback - per canonical spec, merger does not infer instrument identity
                
                all_dfs.append(df)
            except ValueError:
                # Re-raise validation errors (fail loudly)
                raise
            except Exception as e:
                logger.error(f"Error processing file {file_path}: {e}. Skipping.")
                continue
        
        if not all_dfs:
            return {}
        
        # Combine all dataframes
        # Note: Column normalization happens in _read_daily_file, so all dataframes already have consistent schema
        combined_df = pd.concat(all_dfs, ignore_index=True)
        
        # Validate required columns exist in combined data
        missing_cols = [col for col in self.REQUIRED_COLUMNS if col not in combined_df.columns]
        if missing_cols:
            error_msg = (
                f"REQUIRED COLUMNS MISSING in combined data: {missing_cols}. "
                f"Required columns: {self.REQUIRED_COLUMNS}. "
                f"Merger does not infer missing strategy attributes - analyzer must provide all required fields."
            )
            logger.error(error_msg)
            raise ValueError(error_msg)
        
        # Split by Instrument and Session columns (both required per canonical spec)
        result = {}
        
        for instrument in combined_df['Instrument'].unique():
            instrument_str = str(instrument).upper().strip()
            if not instrument_str:
                logger.warning(f"Skipping empty instrument value")
                continue
            
            instrument_df = combined_df[combined_df['Instrument'] == instrument].copy()
            
            # Split by Session (required column, validated above)
            # Per canonical spec: Session MUST be S1 or S2 (validated in _validate_schema)
            for session in instrument_df['Session'].unique():
                session_str = str(session).strip()
                
                # Additional validation (should already be validated, but double-check)
                if session_str not in ['S1', 'S2']:
                    error_msg = (
                        f"INVALID SESSION VALUE: '{session_str}' for instrument {instrument_str}. "
                        f"Session must be 'S1' or 'S2'. Merger does not infer session from Time."
                    )
                    logger.error(error_msg)
                    raise ValueError(error_msg)
                
                session_df = instrument_df[instrument_df['Session'] == session].copy()
                
                # Remove duplicates and sort
                if file_type == "analyzer":
                    session_df = self._remove_duplicates_analyzer(session_df)
                    session_df = self._sort_analyzer_data(session_df)
                else:
                    raise ValueError(f"Unknown file_type: {file_type}. Only 'analyzer' is supported.")
                
                if not session_df.empty:
                    session_key = (instrument_str, session_str)
                    if session_key not in result:
                        result[session_key] = []
                    result[session_key].append(session_df)
                    logger.info(f"Grouped {len(session_df)} rows for {instrument_str} {session_str}")
        
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
                
                # Normalize column names in existing file: rename legacy "SL" to "StopLoss"
                # This ensures consistent output schema regardless of source file age
                if 'SL' in existing_df.columns and 'StopLoss' not in existing_df.columns:
                    existing_df = existing_df.rename(columns={'SL': 'StopLoss'})
                elif 'SL' in existing_df.columns and 'StopLoss' in existing_df.columns:
                    existing_df = existing_df.drop(columns=['SL'])
                
                # Combine with new data (both now have consistent schema)
                combined = pd.concat([existing_df, new_data], ignore_index=True)
                
                # Remove duplicates
                if file_type == "analyzer":
                    combined = self._remove_duplicates_analyzer(combined)
                    combined = self._sort_analyzer_data(combined)
                else:
                    raise ValueError(f"Unknown file_type: {file_type}. Only 'analyzer' is supported.")
                
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
        
        # Validate folder name format (must be YYYY-MM-DD)
        # Note: Date column is required in data, so folder date is only used for validation
        try:
            datetime.strptime(daily_folder.name, "%Y-%m-%d")
        except ValueError:
            logger.error(f"Invalid date format in folder name: {daily_folder.name}. Expected YYYY-MM-DD format.")
            return False
        
        # Get Parquet files
        parquet_files = self._get_parquet_files(daily_folder)
        if not parquet_files:
            logger.warning(f"No Parquet files found in {daily_folder}")
            return False
        
        logger.info(f"Processing analyzer folder {daily_folder.name}: {len(parquet_files)} files")
        
        # Merge daily files by instrument
        # Per canonical spec: Fails loudly if required columns are missing
        try:
            merged_data = self._merge_daily_files(parquet_files, "analyzer")
        except ValueError as e:
            # Schema validation failed - fail loudly per canonical specification
            error_msg = (
                f"SCHEMA VALIDATION FAILED for folder {daily_folder.name}: {e}. "
                f"Merger does not infer missing strategy attributes - analyzer must provide all required fields. "
                f"Required columns: {self.REQUIRED_COLUMNS}"
            )
            logger.error(error_msg)
            raise ValueError(error_msg) from e
        except Exception as e:
            logger.error(f"Unexpected error merging files in {daily_folder.name}: {e}")
            return False
        
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
            
            # Date column is required (validated in schema validation)
            # Per canonical spec: Fail loudly if Date column is missing
            if 'Date' not in df.columns:
                error_msg = (
                    f"REQUIRED COLUMN MISSING: Date column not found for {instrument} {session}. "
                    f"Required columns: {self.REQUIRED_COLUMNS}. "
                    f"Merger does not infer missing strategy attributes - analyzer must provide Date column."
                )
                logger.error(error_msg)
                raise ValueError(error_msg)
            
            try:
                # Validate all dates are parseable - single pass with fail-loudly check
                # Per canonical spec: Fail loudly if any Date cannot be parsed
                date_series = pd.to_datetime(df['Date'], errors='coerce')
                
                # Check for any invalid dates (NaT values)
                invalid_mask = date_series.isna()
                if invalid_mask.any():
                    invalid_count = invalid_mask.sum()
                    invalid_examples = df.loc[invalid_mask, 'Date'].head(5).tolist()
                    
                    error_msg = (
                        f"INVALID DATE VALUES found for {instrument} {session}: "
                        f"{invalid_count} row(s) with unparseable dates. "
                        f"Examples: {invalid_examples}. "
                        f"Merger does not skip invalid data - analyzer must provide valid dates. "
                        f"All dates must be parseable by pandas.to_datetime()."
                    )
                    logger.error(error_msg)
                    raise ValueError(error_msg)
                
                # All dates are valid - proceed with conversion
                df['Date'] = date_series
                df['Year'] = df['Date'].dt.year
                df['Month'] = df['Date'].dt.month
                
                # Group by year and month
                for (year, month), month_df in df.groupby(['Year', 'Month']):
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
            except ValueError:
                # Re-raise validation errors (fail loudly)
                raise
            except Exception as e:
                # Per canonical spec: Fail loudly on date processing errors
                error_msg = (
                    f"Error processing dates for {instrument} {session}: {e}. "
                    f"Date column is required and must be parseable. Merger does not use fallback dates."
                )
                logger.error(error_msg)
                raise ValueError(error_msg) from e
        
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
    
    def run(self):
        """Run the data merger for all daily folders."""
        logger.info("=" * 60)
        logger.info("Starting Data Merger / Consolidator")
        logger.info("=" * 60)
        
        # Process analyzer folders (from pipeline runs)
        analyzer_folders = self._get_daily_folders(ANALYZER_TEMP_DIR)
        logger.info(f"Found {len(analyzer_folders)} analyzer daily folders")
        
        analyzer_success = 0
        for folder in analyzer_folders:
            if self.process_analyzer_folder(folder):
                analyzer_success += 1
        
        logger.info(f"Processed {analyzer_success}/{len(analyzer_folders)} analyzer folders")
        
        logger.info("=" * 60)
        logger.info("Data Merger / Consolidator completed")
        logger.info("=" * 60)


def main():
    """Main entry point."""
    merger = DataMerger()
    merger.run()


if __name__ == "__main__":
    main()

