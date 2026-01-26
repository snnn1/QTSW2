"""
Data loading for Master Matrix.

This module handles loading trade data from analyzer_runs directory,
including parallel loading, retry logic, and date filtering.

SINGLE OWNERSHIP: DataLoader is the sole owner of date normalization.
Canonical internal column name is 'trade_date'. Date normalization happens
once, immediately after reading analyzer output.
"""

import logging
import re
import time
import multiprocessing as mp
from pathlib import Path
from typing import List, Optional, Tuple, Callable
from concurrent.futures import ThreadPoolExecutor, as_completed
import pandas as pd

from .config import ALLOW_INVALID_DATES_SALVAGE

logger = logging.getLogger(__name__)


def _normalize_date_to_trade_date(df: pd.DataFrame, stream_id: str) -> pd.DataFrame:
    """
    Normalize Date column to trade_date column (canonical datetime dtype).
    
    SINGLE OWNERSHIP: This is the ONLY place where date normalization happens.
    All downstream modules MUST NOT re-parse dates, MAY only validate dtype/presence.
    
    Args:
        df: DataFrame with 'Date' column from analyzer output
        stream_id: Stream ID for error reporting
        
    Returns:
        DataFrame with 'trade_date' column (canonical datetime dtype)
        
    Raises:
        ValueError: If invalid dates found in strict mode (default)
    """
    if 'Date' not in df.columns:
        logger.error(f"Stream {stream_id}: Missing 'Date' column in analyzer output")
        raise ValueError(f"Stream {stream_id}: Missing 'Date' column")
    
    df = df.copy()  # Copy before modifying
    
    if ALLOW_INVALID_DATES_SALVAGE:
        # Salvage mode: Never propagate NaT downstream
        # Either drop invalid rows or fail stream with salvage report
        df['trade_date'] = pd.to_datetime(df['Date'], errors='coerce')
        invalid_mask = df['trade_date'].isna() & df['Date'].notna()
        
        if invalid_mask.any():
            invalid_count = invalid_mask.sum()
            invalid_samples = df.loc[invalid_mask, 'Date'].head(5).tolist()
            logger.error(
                f"Stream {stream_id}: Found {invalid_count} rows with invalid dates in salvage mode. "
                f"Sample invalid values: {invalid_samples}. "
                f"Dropping invalid rows (salvage mode never propagates NaT downstream)."
            )
            # Drop invalid rows (salvage mode never propagates NaT)
            df = df[~invalid_mask].copy()
            
            if df.empty:
                logger.error(f"Stream {stream_id}: All rows dropped due to invalid dates. Stream failed.")
                raise ValueError(f"Stream {stream_id}: All rows have invalid dates - stream failed")
    else:
        # Strict mode (default): Invalid dates are contract violations
        try:
            df['trade_date'] = pd.to_datetime(df['Date'], errors='raise')
        except (ValueError, TypeError) as e:
            invalid_samples = df['Date'].head(5).tolist() if len(df) > 0 else []
            logger.error(
                f"Stream {stream_id}: Contract violation - invalid dates found. "
                f"Sample invalid values: {invalid_samples}. "
                f"Error: {e}. "
                f"This violates analyzer output contract. Fix analyzer output before proceeding."
            )
            raise ValueError(
                f"Stream {stream_id}: Invalid dates found - contract violation. "
                f"Sample values: {invalid_samples}. "
                f"Set ALLOW_INVALID_DATES_SALVAGE=True for salvage mode (debugging only)."
            ) from e
    
    # Ensure trade_date is canonical datetime dtype
    if not pd.api.types.is_datetime64_any_dtype(df['trade_date']):
        logger.error(f"Stream {stream_id}: trade_date is not datetime dtype after normalization")
        raise ValueError(f"Stream {stream_id}: trade_date normalization failed")
    
    return df


def _validate_trade_date_dtype(df: pd.DataFrame, stream_id: str) -> None:
    """
    Validate that trade_date column has correct dtype (datetime).
    
    Downstream modules MAY validate dtype/presence but MUST NOT re-parse dates.
    
    Args:
        df: DataFrame to validate
        stream_id: Stream ID for error reporting
        
    Raises:
        ValueError: If trade_date dtype is incorrect
    """
    if 'trade_date' not in df.columns:
        logger.error(f"Stream {stream_id}: Missing 'trade_date' column")
        raise ValueError(f"Stream {stream_id}: Missing 'trade_date' column")
    
    if not pd.api.types.is_datetime64_any_dtype(df['trade_date']):
        logger.error(
            f"Stream {stream_id}: trade_date has incorrect dtype: {df['trade_date'].dtype}. "
            f"Expected datetime64. This indicates date normalization was not performed by DataLoader."
        )
        raise ValueError(
            f"Stream {stream_id}: trade_date has incorrect dtype: {df['trade_date'].dtype}. "
            f"DataLoader must normalize dates before downstream processing."
        )


def _validate_trade_date_presence(df: pd.DataFrame, stream_id: str) -> None:
    """
    Validate that trade_date column exists.
    
    Downstream modules MAY validate presence but MUST NOT re-parse dates.
    
    Args:
        df: DataFrame to validate
        stream_id: Stream ID for error reporting
        
    Raises:
        ValueError: If trade_date column is missing
    """
    if 'trade_date' not in df.columns:
        logger.error(f"Stream {stream_id}: Missing 'trade_date' column")
        raise ValueError(f"Stream {stream_id}: Missing 'trade_date' column")


def find_parquet_files(stream_dir: Path, stream_id: str) -> List[Path]:
    """
    Find all monthly consolidated parquet files for a stream.
    
    Pattern: <stream>_an_<year>_<month>.parquet in year subdirectories
    Example: ES1_an_2024_11.parquet in ES1/2024/
    Skips daily temp files in date folders (YYYY-MM-DD/)
    
    Args:
        stream_dir: Directory for the stream (e.g., analyzer_runs/ES1)
        stream_id: Stream ID (e.g., "ES1")
        
    Returns:
        List of parquet file paths
    """
    parquet_files = []
    
    # Look for year subdirectories (e.g., ES1/2024/, ES1/2025/)
    # CRITICAL: Load ALL years - no year filtering here
    years_found = []
    for year_dir in sorted(stream_dir.iterdir()):
        if not year_dir.is_dir():
            continue
        
        # Check if it's a year directory (4 digits) or skip date folders (YYYY-MM-DD)
        year_dir_name = year_dir.name
        if len(year_dir_name) == 4 and year_dir_name.isdigit():
            # This is a year directory - look for monthly consolidated files
            # Pattern: <stream>_an_<year>_<month>.parquet (analyzer output)
            monthly_files = sorted(year_dir.glob(f"{stream_id}_an_*.parquet"))
            if monthly_files:
                parquet_files.extend(monthly_files)
                years_found.append(year_dir_name)
        # Skip date folders (YYYY-MM-DD format) - these contain daily temp files
    
    # Log years found for debugging
    if years_found:
        logger.debug(f"Stream {stream_id}: Found {len(parquet_files)} files across {len(years_found)} years: {sorted(years_found)}")
    else:
        logger.warning(f"Stream {stream_id}: No parquet files found in any year directories")
    
    return parquet_files


def apply_date_filters(
    df: pd.DataFrame,
    start_date: Optional[str] = None,
    end_date: Optional[str] = None,
    specific_date: Optional[str] = None
) -> pd.DataFrame:
    """
    Apply date filters to a DataFrame using trade_date column.
    
    NOTE: This function expects trade_date to already be normalized by DataLoader.
    It does NOT parse dates - only filters using existing trade_date column.
    
    Args:
        df: Input DataFrame with 'trade_date' column (already normalized)
        start_date: Start date for filtering (YYYY-MM-DD) or None
        end_date: End date for filtering (YYYY-MM-DD) or None
        specific_date: Specific date to filter (YYYY-MM-DD) or None
        
    Returns:
        Filtered DataFrame
    """
    if 'trade_date' not in df.columns:
        # If trade_date doesn't exist, this is an error (should have been normalized)
        logger.warning("apply_date_filters called on DataFrame without trade_date column")
        return df
    
    # If no date filters provided, return DataFrame as-is
    if not specific_date and not start_date and not end_date:
        return df
    
    # Pre-compute date filters for efficiency
    specific_date_dt = pd.to_datetime(specific_date).date() if specific_date else None
    start_date_dt = pd.to_datetime(start_date) if start_date else None
    end_date_dt = pd.to_datetime(end_date) if end_date else None
    
    # Filter using trade_date (already datetime dtype from normalization)
    if specific_date_dt:
        df = df[df['trade_date'].dt.date == specific_date_dt]
    elif start_date_dt or end_date_dt:
        if start_date_dt:
            df = df[df['trade_date'] >= start_date_dt]
        if end_date_dt:
            df = df[df['trade_date'] <= end_date_dt]
    
    return df


def load_stream_data(
    stream_id: str,
    analyzer_runs_dir: Path,
    start_date: Optional[str] = None,
    end_date: Optional[str] = None,
    specific_date: Optional[str] = None
) -> Tuple[bool, Optional[List[pd.DataFrame]], str]:
    """
    Load a single stream's data from analyzer_runs directory.
    
    Args:
        stream_id: Stream ID (e.g., "ES1")
        analyzer_runs_dir: Base directory containing stream subdirectories
        start_date: Start date for filtering (YYYY-MM-DD) or None
        end_date: End date for filtering (YYYY-MM-DD) or None
        specific_date: Specific date to load (YYYY-MM-DD) or None
        
    Returns:
        Tuple of (success: bool, stream_trades_list: List[pd.DataFrame], stream_id: str)
    """
    stream_dir = analyzer_runs_dir / stream_id
    
    if not stream_dir.exists():
        error_msg = f"Stream directory not found: {stream_dir}"
        logger.warning(error_msg)
        # Check if analyzer_runs_dir exists
        if not analyzer_runs_dir.exists():
            error_msg += f" (analyzer_runs_dir also missing: {analyzer_runs_dir})"
        return (False, None, stream_id)
    
    # Find parquet files - force fresh discovery to see newly written files
    # Clear any file system caches by refreshing the directory
    try:
        # Force directory refresh by accessing it
        stream_dir.stat()
    except:
        pass
    
    parquet_files = find_parquet_files(stream_dir, stream_id)
    
    if not parquet_files:
        # Provide more diagnostic info
        year_dirs = [d for d in stream_dir.iterdir() if d.is_dir() and len(d.name) == 4 and d.name.isdigit()]
        error_msg = f"No monthly consolidated files found for stream: {stream_id} (checked {stream_dir})"
        if year_dirs:
            error_msg += f" (found {len(year_dirs)} year directories: {[d.name for d in year_dirs]})"
        else:
            error_msg += f" (no year subdirectories found)"
        logger.warning(error_msg)
        return (False, None, stream_id)
    
    logger.info(f"Loading stream: {stream_id} ({len(parquet_files)} monthly files from all available years)")
    
    stream_trades = []
    files_loaded = 0
    files_skipped = 0
    
    for file_path in parquet_files:
        try:
            # Force fresh read by checking file modification time and clearing pandas cache if needed
            # This ensures we read newly written files even if pandas cached the old version
            file_path.stat()  # Refresh file system metadata
            
            # Read parquet file fresh (pandas may cache, but stat() ensures we see new files)
            df = pd.read_parquet(file_path)
            
            if df.empty:
                continue
            
            # Extract stream info from filename if Stream column missing
            if 'Stream' not in df.columns or df['Stream'].isna().all():
                filename_match = re.match(r'^([A-Z]{2})([12])_', file_path.name)
                if filename_match:
                    instrument = filename_match.group(1).upper()
                    stream_num = filename_match.group(2)
                    df['Stream'] = f"{instrument}{stream_num}"
                    logger.debug(f"  Extracted stream '{df['Stream'].iloc[0]}' from filename: {file_path.name}")
            
            # Ensure Stream column matches expected stream_id
            if 'Stream' not in df.columns or (df['Stream'] != stream_id).any():
                df = df.copy()  # Only copy if we need to modify
                df['Stream'] = stream_id
            
            # SINGLE OWNERSHIP: Normalize Date to trade_date immediately after reading
            # This is the ONLY place where date normalization happens
            df = _normalize_date_to_trade_date(df, stream_id)
            
            # Apply date filters using trade_date (already normalized)
            df = apply_date_filters(df, start_date, end_date, specific_date)
            
            if not df.empty:
                stream_trades.append(df)
                files_loaded += 1
                logger.debug(f"  Loaded {len(df)} trades from {file_path.name}")
            else:
                files_skipped += 1
                logger.debug(f"  Skipped {file_path.name} (empty after date filtering)")
                
        except Exception as e:
            logger.error(f"Error loading {file_path}: {e}")
            import traceback
            logger.debug(f"Traceback for {file_path}: {traceback.format_exc()}")
            continue
    
    if not stream_trades:
        # All files were empty or failed to load after date filtering
        error_msg = f"Stream {stream_id}: No valid trades loaded"
        if parquet_files:
            error_msg += f" (checked {len(parquet_files)} files, all empty or filtered out)"
        logger.warning(error_msg)
        return (False, None, stream_id)
    
    total_trades = sum(len(df) for df in stream_trades)
    logger.info(
        f"[OK] Successfully loaded stream: {stream_id} "
        f"({total_trades} trades from {files_loaded} files, {files_skipped} skipped)"
    )
    return (True, stream_trades, stream_id)


def load_all_streams(
    streams: List[str],
    analyzer_runs_dir: Path,
    start_date: Optional[str] = None,
    end_date: Optional[str] = None,
    specific_date: Optional[str] = None,
    wait_for_streams: bool = True,
    max_retries: int = 3,
    retry_delay_seconds: int = 2,
    apply_sequencer_logic: Optional[Callable] = None
) -> pd.DataFrame:
    """
    Load all trades from analyzer_runs for multiple streams with parallel loading and retry logic.
    
    Args:
        streams: List of stream IDs to load
        analyzer_runs_dir: Base directory containing stream subdirectories
        start_date: Start date for backtest period (YYYY-MM-DD) or None for all
        end_date: End date for backtest period (YYYY-MM-DD) or None for all
        specific_date: Specific date to load (YYYY-MM-DD) for "today" mode, or None
        wait_for_streams: If True, retry loading streams that fail (default: True)
        max_retries: Maximum number of retry attempts for failed streams (default: 3)
        retry_delay_seconds: Seconds to wait between retries (default: 2)
        apply_sequencer_logic: Optional callback function to apply sequencer logic to the merged DataFrame
        
    Returns:
        Merged DataFrame with all trades (or chosen trades if sequencer logic is applied)
    """
    logger.info("=" * 80)
    logger.info("MASTER MATRIX - Loading from analyzer_runs")
    logger.info("=" * 80)
    
    if not streams:
        logger.warning("No streams to load!")
        return pd.DataFrame()
    
    all_trades = []
    streams_loaded = []
    streams_failed = {}  # Track failed streams and reasons
    
    # Load all streams with retry logic if enabled
    # OPTIMIZATION: Use parallel loading for I/O-bound operations
    streams_to_load = streams.copy()
    retry_count = 0
    max_workers = min(len(streams_to_load), mp.cpu_count() * 2)  # I/O bound, so 2x CPU cores
    
    while streams_to_load and retry_count <= max_retries:
        if retry_count > 0:
            logger.info(f"Retry attempt {retry_count}/{max_retries} for failed streams...")
            time.sleep(retry_delay_seconds)
        
        # Parallel stream loading (I/O bound, so ThreadPoolExecutor is better than ProcessPoolExecutor)
        remaining_streams = []
        with ThreadPoolExecutor(max_workers=max_workers) as executor:
            # Submit all stream loading tasks
            future_to_stream = {
                executor.submit(
                    load_stream_data,
                    stream_id,
                    analyzer_runs_dir,
                    start_date,
                    end_date,
                    specific_date
                ): stream_id 
                for stream_id in streams_to_load
            }
            
            # Collect results as they complete
            for future in as_completed(future_to_stream):
                stream_id = future_to_stream[future]
                try:
                    success, stream_trades_list, _ = future.result()
                    if success and stream_trades_list:
                        all_trades.extend(stream_trades_list)
                        streams_loaded.append(stream_id)
                    else:
                        remaining_streams.append(stream_id)
                        if stream_id not in streams_failed:
                            streams_failed[stream_id] = "Failed to load stream data"
                except Exception as e:
                    import traceback
                    error_detail = f"Exception: {str(e)}"
                    logger.error(f"Exception loading stream {stream_id}: {e}")
                    logger.debug(f"Traceback for {stream_id}: {traceback.format_exc()}")
                    remaining_streams.append(stream_id)
                    streams_failed[stream_id] = error_detail
        
        streams_to_load = remaining_streams
        retry_count += 1
        
        # If wait_for_streams is False, stop after first attempt
        if not wait_for_streams:
            break
    
    # Report results
    if streams_failed:
        logger.warning("=" * 80)
        logger.warning(f"FAILED TO LOAD {len(streams_failed)} STREAM(S):")
        for stream_id, reason in streams_failed.items():
            logger.warning(f"  - {stream_id}: {reason}")
        logger.warning("=" * 80)
        # Provide diagnostic suggestions
        missing_dirs = [s for s, r in streams_failed.items() if "directory not found" in r]
        missing_files = [s for s, r in streams_failed.items() if "No monthly consolidated files" in r]
        if missing_dirs:
            logger.warning(f"  Streams with missing directories: {missing_dirs}")
            logger.warning(f"  Check that analyzer_runs/{missing_dirs[0]}/ exists and contains year subdirectories")
        if missing_files:
            logger.warning(f"  Streams with missing files: {missing_files}")
            logger.warning(f"  Check that monthly parquet files exist (pattern: <stream>_an_YYYY_MM.parquet)")
    
    if not all_trades:
        logger.warning("No trade data found!")
        return pd.DataFrame()
    
    # Merge all DataFrames (optimized: use more efficient concat, defer sorting)
    logger.info(f"Merging {len(all_trades)} data files...")
    if len(all_trades) == 1:
        master_df = all_trades[0]
    else:
        # Use sort=False for faster concat - sorting will happen later in build_master_matrix
        # This avoids redundant sorting operations
        master_df = pd.concat(all_trades, ignore_index=True, sort=False)
    
    logger.info(f"Total trades loaded (before sequencer logic): {len(master_df)}")
    
    # Log date range if available (using trade_date, already normalized)
    if not master_df.empty and 'trade_date' in master_df.columns:
        try:
            valid_dates = master_df['trade_date'].notna()
            if valid_dates.any():
                min_date = master_df.loc[valid_dates, 'trade_date'].min()
                max_date = master_df.loc[valid_dates, 'trade_date'].max()
                logger.info(f"Date range in loaded data: {min_date.date()} to {max_date.date()}")
        except Exception as e:
            logger.debug(f"Could not determine date range: {e}")
    
    logger.info(f"Streams loaded successfully: {streams_loaded} ({len(streams_loaded)}/{len(streams)})")
    if streams_failed:
        # Validate empty streams based on criticality
        from .config import CRITICAL_STREAMS
        critical_failed = [s for s in streams_failed.keys() if s in CRITICAL_STREAMS]
        non_critical_failed = [s for s in streams_failed.keys() if s not in CRITICAL_STREAMS]
        
        if critical_failed:
            error_msg = (
                f"CRITICAL STREAMS FAILED TO LOAD: {sorted(critical_failed)}. "
                f"Critical streams must have data. Aborting load."
            )
            logger.error(error_msg)
            raise ValueError(error_msg)
        
        if non_critical_failed:
            logger.warning(
                f"Non-critical streams failed to load: {sorted(non_critical_failed)}. "
                f"Continuing with available streams."
            )
            logger.warning(f"Streams that failed to load: {list(streams_failed.keys())}")
    
    # Apply sequencer logic if provided
    if apply_sequencer_logic:
        logger.info("Applying sequencer time-change logic to select chosen trades...")
        logger.info("Processing ALL historical data to build accurate time slot histories (requires 13+ days for rolling window)")
        logger.info("Returning ALL years (histories built from all historical data)")
        master_df = apply_sequencer_logic(master_df, display_year=None)  # None = return ALL years
        logger.info(f"Total chosen trades (after sequencer logic): {len(master_df)}")
    
    return master_df



