#!/usr/bin/env python3
"""
Migration Script: Split existing analyzer/sequencer data by session

This script migrates existing monthly files from CL/ES/etc. folders
to CL1/CL2/ES1/ES2/etc. folders based on session (S1/S2).

Usage:
    python tools/migrate_to_session_folders.py
"""

import sys
import logging
from pathlib import Path
from typing import List, Tuple
import pandas as pd

# Setup logging
logging.basicConfig(
    level=logging.INFO,
    format='%(asctime)s - %(levelname)s - %(message)s',
    handlers=[
        logging.FileHandler('migrate_to_session_folders.log'),
        logging.StreamHandler()
    ]
)
logger = logging.getLogger(__name__)

# Paths
QTSW2_ROOT = Path(__file__).parent.parent
ANALYZER_RUNS_DIR = QTSW2_ROOT / "data" / "analyzer_runs"
SEQUENCER_RUNS_DIR = QTSW2_ROOT / "data" / "sequencer_runs"

# Time slots for session inference
TIME_SLOTS_S1 = ['07:30', '08:00', '09:00']
TIME_SLOTS_S2 = ['09:30', '10:00', '10:30', '11:00']


def get_monthly_file_path(instrument: str, session: str, year: int, month: int, file_type: str) -> Path:
    """Get the monthly file path for an instrument, session, year, and month."""
    # Convert session to suffix: S1 -> 1, S2 -> 2
    session_suffix = "1" if session == "S1" else "2" if session == "S2" else ""
    instrument_name = f"{instrument}{session_suffix}" if session_suffix else instrument
    
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


def split_by_session(df: pd.DataFrame, file_type: str) -> Tuple[pd.DataFrame, pd.DataFrame]:
    """Split DataFrame by session (S1/S2)."""
    s1_df = pd.DataFrame()
    s2_df = pd.DataFrame()
    
    if 'Session' in df.columns:
        # Use Session column
        s1_rows = df[df['Session'] == 'S1'].copy()
        s2_rows = df[df['Session'] == 'S2'].copy()
        
        # Handle invalid/missing sessions by inferring from Time
        invalid_rows = df[~df['Session'].isin(['S1', 'S2'])].copy()
        if not invalid_rows.empty and 'Time' in invalid_rows.columns:
            s1_time_rows = invalid_rows[invalid_rows['Time'].isin(TIME_SLOTS_S1)].copy()
            s2_time_rows = invalid_rows[invalid_rows['Time'].isin(TIME_SLOTS_S2)].copy()
            s1_rows = pd.concat([s1_rows, s1_time_rows], ignore_index=True)
            s2_rows = pd.concat([s2_rows, s2_time_rows], ignore_index=True)
        
        s1_df = s1_rows
        s2_df = s2_rows
    elif 'Time' in df.columns:
        # Infer from Time column
        s1_df = df[df['Time'].isin(TIME_SLOTS_S1)].copy()
        s2_df = df[df['Time'].isin(TIME_SLOTS_S2)].copy()
        
        # Add Session column
        if not s1_df.empty:
            s1_df['Session'] = 'S1'
        if not s2_df.empty:
            s2_df['Session'] = 'S2'
    else:
        logger.warning("No Session or Time column found. Cannot split by session.")
        return pd.DataFrame(), pd.DataFrame()
    
    return s1_df, s2_df


def migrate_instrument_folder(instrument_dir: Path, file_type: str) -> int:
    """Migrate all monthly files for an instrument."""
    migrated_count = 0
    
    # Get instrument name from folder (e.g., "CL" from "CL/")
    instrument = instrument_dir.name
    
    # Skip if already migrated (ends with 1 or 2)
    if instrument.endswith('1') or instrument.endswith('2'):
        logger.info(f"Skipping {instrument} - already migrated")
        return 0
    
    # Find all year folders
    year_folders = [d for d in instrument_dir.iterdir() if d.is_dir() and d.name.isdigit()]
    
    if not year_folders:
        logger.info(f"No year folders found in {instrument_dir}")
        return 0
    
    logger.info(f"Migrating {instrument} ({file_type}) - {len(year_folders)} year folders")
    
    for year_dir in sorted(year_folders):
        year = int(year_dir.name)
        
        # Find all monthly parquet files
        pattern = f"{instrument}_an_{year}_*.parquet" if file_type == "analyzer" else f"{instrument}_seq_{year}_*.parquet"
        monthly_files = list(year_dir.glob(pattern))
        
        if not monthly_files:
            continue
        
        logger.info(f"  Processing {year}: {len(monthly_files)} files")
        
        for monthly_file in sorted(monthly_files):
            try:
                # Parse month from filename (e.g., CL_an_2024_01.parquet -> month 1)
                month_str = monthly_file.stem.split('_')[-1]
                month = int(month_str)
                
                # Read the file
                df = pd.read_parquet(monthly_file)
                
                if df.empty:
                    logger.warning(f"  Empty file: {monthly_file.name}")
                    continue
                
                # Split by session
                s1_df, s2_df = split_by_session(df, file_type)
                
                # Write S1 file
                if not s1_df.empty:
                    s1_file = get_monthly_file_path(instrument, 'S1', year, month, file_type)
                    s1_df.to_parquet(s1_file, index=False, compression='snappy')
                    logger.info(f"    Created {s1_file.name}: {len(s1_df)} rows (S1)")
                    migrated_count += 1
                
                # Write S2 file
                if not s2_df.empty:
                    s2_file = get_monthly_file_path(instrument, 'S2', year, month, file_type)
                    s2_df.to_parquet(s2_file, index=False, compression='snappy')
                    logger.info(f"    Created {s2_file.name}: {len(s2_df)} rows (S2)")
                    migrated_count += 1
                
                if s1_df.empty and s2_df.empty:
                    logger.warning(f"    No session data found in {monthly_file.name}")
                
            except Exception as e:
                logger.error(f"  Error processing {monthly_file.name}: {e}")
                continue
    
    return migrated_count


def main():
    """Main migration function."""
    logger.info("Starting migration to session-based folders...")
    
    total_migrated = 0
    
    # Migrate analyzer files
    if ANALYZER_RUNS_DIR.exists():
        logger.info(f"\n=== Migrating Analyzer Files ===")
        analyzer_instruments = [d for d in ANALYZER_RUNS_DIR.iterdir() 
                               if d.is_dir() and not d.name.startswith('.') 
                               and d.name not in ['archived', 'summaries']]
        
        for instrument_dir in sorted(analyzer_instruments):
            count = migrate_instrument_folder(instrument_dir, "analyzer")
            total_migrated += count
    
    # Migrate sequencer files
    if SEQUENCER_RUNS_DIR.exists():
        logger.info(f"\n=== Migrating Sequencer Files ===")
        sequencer_instruments = [d for d in SEQUENCER_RUNS_DIR.iterdir() 
                                 if d.is_dir() and not d.name.startswith('.') 
                                 and d.name != 'archived']
        
        for instrument_dir in sorted(sequencer_instruments):
            count = migrate_instrument_folder(instrument_dir, "sequencer")
            total_migrated += count
    
    logger.info(f"\n=== Migration Complete ===")
    logger.info(f"Total files migrated: {total_migrated}")
    logger.info("\nNote: Old CL/ES/etc. folders still exist.")
    logger.info("You can delete them manually after verifying the migration.")


if __name__ == "__main__":
    main()



