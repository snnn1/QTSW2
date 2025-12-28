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
from typing import List, Optional, Dict, Tuple, Callable
from datetime import datetime, timedelta
import logging
import sys

# Import refactored modules
from . import stream_manager
from . import data_loader
from . import sequencer_logic
from . import schema_normalizer
from . import filter_engine
from . import statistics
from . import file_manager
from .config import DOM_BLOCKED_DAYS

# Configure logging using centralized configuration
from .logging_config import setup_matrix_logger

# Set up logger - simple and clean
logger = setup_matrix_logger(__name__, console=True, level=logging.INFO)

# Log module load
logger.info("Master Matrix module loaded")


class MasterMatrix:
    """
    Creates a master matrix by merging all trade files from all streams.
    
    IMPORTANT: This class ORCHESTRATES but does NOT decide time slots.
    Time decisions are OWNED by sequencer_logic.py.
    
    This class:
    - Orchestrates data loading
    - Calls sequencer_logic to get chosen trades
    - Writes output
    - NEVER mutates the Time column (it's set by sequencer_logic.py)
    
    The Time column means: "The sequencer's intended trading slot for that day."
    """
    
    def __init__(self, analyzer_runs_dir: str = "data/analyzed",
                 stream_filters: Optional[Dict[str, Dict]] = None,
                 sequencer_runs_dir: Optional[str] = None):
        """
        Initialize Master Matrix builder.
        Works like the sequencer: reads from analyzer_runs and applies time change logic
        to select chosen trades (one per day per stream).
        
        Args:
            analyzer_runs_dir: Directory containing analyzer output files (all trades)
            stream_filters: Dictionary mapping stream_id to filter config:
                {
                    "ES1": {
                        "exclude_days_of_week": ["Wednesday"],
                        "exclude_days_of_month": [4, 16, 30],
                        "exclude_times": ["07:30", "08:00"]
                    },
                    ...
                }
            sequencer_runs_dir: Deprecated alias for analyzer_runs_dir (for backward compatibility)
        """
        # Handle deprecated sequencer_runs_dir parameter
        if sequencer_runs_dir is not None:
            analyzer_runs_dir = sequencer_runs_dir
        
        self.analyzer_runs_dir = Path(analyzer_runs_dir)
        self.master_df: Optional[pd.DataFrame] = None
        
        # Auto-discover streams by scanning analyzer_runs directory
        self.streams = stream_manager.discover_streams(self.analyzer_runs_dir)
        
        # Day-of-month blocked days for "2" streams (from centralized config)
        self.dom_blocked_days = DOM_BLOCKED_DAYS
        
        # Per-stream filters - initialize with defaults
        self.stream_filters = stream_filters or {}
        self.stream_filters = stream_manager.ensure_default_filters(self.streams, self.stream_filters)
    
    def _update_stream_filters(self, stream_filters: Optional[Dict[str, Dict]], merge: bool = False):
        """
        Update stream filters.
        
        Args:
            stream_filters: New filters to apply
            merge: If True, merge with existing filters. If False, replace all filters.
        """
        self.stream_filters = stream_manager.update_stream_filters(
            self.stream_filters,
            stream_filters,
            self.streams,
            merge
        )
    
    def _rebuild_full(self) -> pd.DataFrame:
        """Rebuild everything from scratch."""
        import time
        start_time = time.time()
        logger.info("=" * 80)
        logger.info("Rebuilding all streams from scratch")
        logger.info("=" * 80)
        # IMPORTANT: Always load ALL historical data (ignore date filters) to build accurate time slot histories
        result = self._load_all_streams_with_sequencer(start_date=None, end_date=None, specific_date=None)
        elapsed = time.time() - start_time
        logger.info(f"Rebuild completed in {elapsed:.2f} seconds ({elapsed/60:.2f} minutes)")
        logger.info(f"Loaded {len(result)} trades")
        return result
    
    def _rebuild_partial(self, streams: List[str], output_dir: str) -> pd.DataFrame:
        """Rebuild specific streams and merge with existing data."""
        logger.info(f"Rebuilding only streams: {streams}")
        
        # Load existing master matrix if it exists
        existing_df = file_manager.load_existing_matrix(output_dir)
        if not existing_df.empty:
            # Remove old data for streams being rebuilt
            existing_df = existing_df[~existing_df['Stream'].isin(streams)].copy()
            logger.info(f"After removing rebuilt streams: {len(existing_df)} trades")
        
        # Only process requested streams
        original_streams = self.streams.copy()
        streams_to_load = [s for s in self.streams if s in streams]
        self.streams = streams_to_load
        logger.info(f"Limiting load to streams: {streams_to_load}")
        
        # Load only the requested streams
        new_df = self._load_all_streams_with_sequencer(start_date=None, end_date=None, specific_date=None)
        
        # Double-check: filter to only the streams we wanted (safety check)
        if not new_df.empty:
            new_df = new_df[new_df['Stream'].isin(streams_to_load)].copy()
            logger.info(f"Loaded {len(new_df)} trades from requested streams: {streams_to_load}")
        else:
            logger.warning(f"[WARNING] No trades loaded for streams: {streams_to_load}")
            for stream_id in streams_to_load:
                stream_filters = self.stream_filters.get(stream_id, {})
                exclude_times = stream_filters.get('exclude_times', [])
                if exclude_times:
                    logger.warning(f"[WARNING] Stream {stream_id} has excluded times: {exclude_times}")
        
        # Restore original streams list
        self.streams = original_streams
        
        # Merge with existing data
        if not existing_df.empty:
            if not new_df.empty:
                df = pd.concat([existing_df, new_df], ignore_index=True)
                logger.info(f"Merged with existing data: {len(df)} total trades")
            else:
                df = existing_df
                logger.info(f"No new trades to merge (streams skipped), keeping existing data: {len(df)} trades")
        else:
            df = new_df
            if df.empty:
                logger.warning(f"[WARNING] Final result is empty - no trades for streams: {streams_to_load}")
        
        return df
        
    def _create_sequencer_callback(self) -> Callable:
        """
        Create sequencer callback function that uses current stream_filters.
        
        Returns:
            Callable function that applies sequencer logic to a DataFrame
        """
        def apply_sequencer(df: pd.DataFrame, display_year: Optional[int] = None) -> pd.DataFrame:
            # DIAGNOSTIC: Log function being called and module file path (DEBUG level for performance)
            logger.debug("=" * 80)
            logger.debug("CALLING SEQUENCER")
            logger.debug(f"Function: sequencer_logic.apply_sequencer_logic")
            logger.debug(f"Module file: {sequencer_logic.__file__}")
            logger.debug("=" * 80)
            return sequencer_logic.apply_sequencer_logic(df, self.stream_filters, display_year)
        
        return apply_sequencer
    
    def _load_all_streams_with_sequencer(
        self,
        start_date: Optional[str] = None,
        end_date: Optional[str] = None,
        specific_date: Optional[str] = None,
        wait_for_streams: bool = True,
        max_retries: int = 3,
        retry_delay_seconds: int = 2
    ) -> pd.DataFrame:
        """
        Load all streams and apply sequencer logic.
        Internal method that wraps data_loader with sequencer logic callback.
        """
        # Re-discover streams if needed
        if not self.streams or len(self.streams) == 0:
            self.streams = stream_manager.discover_streams(self.analyzer_runs_dir)
        
        if not self.streams:
            logger.warning("No streams discovered! Check analyzer_runs directory.")
            return pd.DataFrame()
        
        # Create sequencer callback that uses current stream_filters
        apply_sequencer = self._create_sequencer_callback()
        
        # Load all streams with sequencer logic applied
        return data_loader.load_all_streams(
            streams=self.streams,
            analyzer_runs_dir=self.analyzer_runs_dir,
            start_date=start_date,
            end_date=end_date,
            specific_date=specific_date,
            wait_for_streams=wait_for_streams,
            max_retries=max_retries,
            retry_delay_seconds=retry_delay_seconds,
            apply_sequencer_logic=apply_sequencer
        )
    
    # Utility methods moved to utils module - kept for backward compatibility
    def _normalize_time(self, time_str: str) -> str:
        """Normalize time format - delegates to utils module."""
        from .utils import normalize_time
        return normalize_time(time_str)
    
    def _get_session_for_time(self, time: str, slot_ends: Dict) -> str:
        """Get session for time - delegates to utils module."""
        from .utils import get_session_for_time
        return get_session_for_time(time, slot_ends)
    
    def _calculate_time_score(self, result: str) -> int:
        """Calculate time score - delegates to utils module."""
        from .utils import calculate_time_score
        return calculate_time_score(result)
    
    def normalize_schema(self, df: pd.DataFrame) -> pd.DataFrame:
        """Normalize schema using schema_normalizer module."""
        return schema_normalizer.normalize_schema(df)
    
    def add_global_columns(self, df: pd.DataFrame) -> pd.DataFrame:
        """Add global columns using filter_engine module."""
        return filter_engine.add_global_columns(df, self.stream_filters, self.dom_blocked_days)
    
    def _log_summary_stats(self, df: pd.DataFrame, include_filtered_executed: bool = True) -> Dict:
        """Calculate and log summary statistics using statistics module."""
        return statistics.calculate_summary_stats(df, include_filtered_executed=include_filtered_executed)
    
    def build_master_matrix(self, start_date: Optional[str] = None,
                           end_date: Optional[str] = None,
                           specific_date: Optional[str] = None,
                           output_dir: str = "data/master_matrix",
                           stream_filters: Optional[Dict[str, Dict]] = None,
                           analyzer_runs_dir: Optional[str] = None,
                           streams: Optional[List[str]] = None) -> pd.DataFrame:
        """
        Build the master matrix by loading, normalizing, and merging all streams.
        Works like sequencer: reads from analyzer_runs and applies time change logic
        to select chosen trades (one per day per stream).
        
        Args:
            start_date: Start date for backtest period (YYYY-MM-DD) or None for all
            end_date: End date for backtest period (YYYY-MM-DD) or None for all
            specific_date: Specific date to load (YYYY-MM-DD) for "today" mode, or None
            output_dir: Directory to save master matrix files
            stream_filters: Per-stream filter configuration
            analyzer_runs_dir: Override analyzer runs directory (optional)
            
        Returns:
            Master matrix DataFrame sorted by trade_date, entry_time, Instrument, Stream
            (ascending order). This is the CANONICAL sort order - API and UI layers must
            NOT re-sort this data, they should assume it is already correctly sorted.
        """
        # Force output to stderr immediately AND log file
        import sys
        debug_start = "=" * 80 + "\nMASTER MATRIX: build_master_matrix() called\n"
        debug_start += f"Streams: {streams}\n"
        debug_start += f"Output dir: {output_dir}\n"
        debug_start += f"Stream filters: {stream_filters}\n"
        debug_start += "=" * 80 + "\n"
        
        # High-signal INFO logs only (build start with key parameters)
        logger.info("=" * 80)
        logger.info("MASTER MATRIX: build_master_matrix() called")
        logger.info(f"Streams: {streams}")
        logger.info(f"Output dir: {output_dir}")
        logger.info("BUILDING MASTER MATRIX (Applying Sequencer Logic)")
        logger.info("=" * 80)
        # Stream filters details moved to DEBUG (diagnostic, not high-signal)
        logger.debug(f"Stream filters: {stream_filters}")
        
        # Override analyzer_runs_dir if provided
        if analyzer_runs_dir:
            self.analyzer_runs_dir = Path(analyzer_runs_dir)
            # Re-discover streams after changing directory
            self.streams = stream_manager.discover_streams(self.analyzer_runs_dir)
        
        # CRITICAL: Update stream filters BEFORE loading data
        # load_all_streams calls _apply_sequencer_logic which needs these filters
        # For full rebuild, replace filters. For partial rebuild, merge filters.
        is_partial_rebuild = streams and len(streams) > 0
        self._update_stream_filters(stream_filters, merge=is_partial_rebuild)
        
        # Log filter state (DEBUG level - diagnostic info)
        logger.debug(f"Stream filters set BEFORE load_all_streams: {list(self.stream_filters.keys())}")
        for stream_id, filters in self.stream_filters.items():
            exclude_times = filters.get('exclude_times', [])
            if exclude_times:
                logger.debug(f"  {stream_id}: exclude_times = {exclude_times}")
        
        # If rebuilding specific streams, merge with existing. Otherwise rebuild everything.
        if streams and len(streams) > 0:
            df = self._rebuild_partial(streams, output_dir)
        else:
            df = self._rebuild_full()
        
        if df.empty:
            logger.warning("No data loaded!")
            return pd.DataFrame()
        
        # NOTE: stream_filters are already set above (before loading)
        # They were used in sequencer_logic during loading
        
        # DIAGNOSTIC: Check Time column values before post-processing (DEBUG level - diagnostic only)
        if 'Time' in df.columns and 'Stream' in df.columns:
            s2_streams = ['ES2', 'NQ2', 'GC2', 'NG2', 'YM2', 'CL2']
            logger.debug("=" * 80)
            logger.debug("BEFORE normalize_schema/add_global_columns - Time column check:")
            for stream in s2_streams:
                stream_df = df[df['Stream'] == stream]
                if not stream_df.empty:
                    time_counts = stream_df['Time'].value_counts().head(5)
                    logger.debug(f"  {stream}: {dict(time_counts)}")
            logger.debug("=" * 80)
        
        # Normalize schema
        df = self.normalize_schema(df)
        
        # DIAGNOSTIC: Check Time column values after normalize_schema (DEBUG level - diagnostic only)
        if 'Time' in df.columns and 'Stream' in df.columns:
            logger.debug("AFTER normalize_schema - Time column check:")
            for stream in s2_streams:
                stream_df = df[df['Stream'] == stream]
                if not stream_df.empty:
                    time_counts = stream_df['Time'].value_counts().head(5)
                    logger.debug(f"  {stream}: {dict(time_counts)}")
        
        # Add global columns (applies filters)
        df = self.add_global_columns(df)
        
        # DIAGNOSTIC: Check Time column values after add_global_columns (DEBUG level - diagnostic only)
        if 'Time' in df.columns and 'Stream' in df.columns:
            logger.debug("AFTER add_global_columns - Time column check:")
            for stream in s2_streams:
                stream_df = df[df['Stream'] == stream]
                if not stream_df.empty:
                    time_counts = stream_df['Time'].value_counts().head(5)
                    logger.debug(f"  {stream}: {dict(time_counts)}")
            logger.debug("=" * 80)
        
        # SL comes from analyzer output (schema_normalizer ensures it exists with NaN if missing)
        
        # Ensure Time Change column exists (will be calculated after sorting)
        if 'Time Change' not in df.columns:
            df['Time Change'] = ''
        
        # CANONICAL SORTING: MasterMatrix is the ONLY layer that sorts the output.
        # Output from build_master_matrix() is fully sorted by: trade_date, entry_time, Instrument, Stream
        # API and UI layers must NOT re-sort - they should assume data is already correctly sorted.
        # Ensure trade_date is datetime (not date objects) for consistent sorting
        if 'trade_date' in df.columns:
            if df['trade_date'].dtype == 'object':
                # Convert date objects to datetime for proper sorting
                df['trade_date'] = pd.to_datetime(df['trade_date'], errors='coerce')
            elif not pd.api.types.is_datetime64_any_dtype(df['trade_date']):
                df['trade_date'] = pd.to_datetime(df['trade_date'], errors='coerce')
        
        # Initialize date_repaired and date_repair_quality columns (will be set for invalid dates)
        if 'date_repaired' not in df.columns:
            df['date_repaired'] = False
        if 'date_repair_quality' not in df.columns:
            df['date_repair_quality'] = 1.0  # 1.0 = original date was valid, no repair needed
        
        # Handle rows with invalid trade_date - preserve them instead of removing
        valid_dates = df['trade_date'].notna()
        if not valid_dates.all():
            invalid_count = (~valid_dates).sum()
            invalid_df = df[~valid_dates].copy()
            invalid_percentage = invalid_count / len(df) * 100
            
            # Report per-stream breakdown for better diagnostics
            if 'Stream' in df.columns:
                invalid_by_stream = invalid_df.groupby('Stream').size()
                for stream_id, count in invalid_by_stream.items():
                    logger.warning(f"[WARNING] {stream_id} has {count} trades with invalid trade_date! Attempting to repair...")
                    # Log sample of invalid dates for this stream for debugging
                    stream_invalid = invalid_df[invalid_df['Stream'] == stream_id]
                    if 'Date' in stream_invalid.columns:
                        sample_dates = stream_invalid['Date'].head(5).tolist()
                        logger.debug(f"  Sample invalid dates for {stream_id}: {sample_dates}")
            
            # Attempt to repair invalid dates with quality scoring
            repaired_count = 0
            repair_quality_scores = []  # Track repair confidence (0.0-1.0)
            
            for idx in invalid_df.index:
                original_date = invalid_df.loc[idx, 'Date'] if 'Date' in invalid_df.columns else None
                
                # Try to repair: attempt multiple strategies with quality scoring
                repaired_date = None
                repair_quality = 0.0  # 0.0 = low confidence, 1.0 = high confidence
                
                # Strategy 1: Try parsing Date column again with different formats (HIGH CONFIDENCE)
                if original_date is not None and pd.notna(original_date):
                    try:
                        # Try common date formats
                        for fmt in ['%Y-%m-%d', '%m/%d/%Y', '%d/%m/%Y', '%Y%m%d']:
                            try:
                                repaired_date = pd.to_datetime(str(original_date), format=fmt, errors='coerce')
                                if pd.notna(repaired_date):
                                    repair_quality = 0.9  # High confidence - exact format match
                                    break
                            except:
                                continue
                        # If format parsing failed, try general parsing (MEDIUM CONFIDENCE)
                        if pd.isna(repaired_date):
                            repaired_date = pd.to_datetime(str(original_date), errors='coerce')
                            if pd.notna(repaired_date):
                                repair_quality = 0.7  # Medium confidence - general parsing
                    except:
                        pass
                
                # Strategy 2: Infer from adjacent valid dates (MEDIUM CONFIDENCE)
                if pd.isna(repaired_date) and 'Stream' in invalid_df.columns:
                    stream_id = invalid_df.loc[idx, 'Stream']
                    if 'Stream' in df.columns:
                        stream_valid = df[valid_dates & (df['Stream'] == stream_id)]
                    else:
                        stream_valid = df[valid_dates]
                    if not stream_valid.empty and 'trade_date' in stream_valid.columns:
                        # Use median date from same stream as fallback
                        median_date = stream_valid['trade_date'].median()
                        if pd.notna(median_date):
                            repaired_date = median_date
                            repair_quality = 0.5  # Medium-low confidence - inferred from stream
                
                # Strategy 3: Use a default date (first valid date in dataset) as last resort (LOW CONFIDENCE)
                if pd.isna(repaired_date):
                    valid_dates_in_df = df[valid_dates]['trade_date'] if valid_dates.any() else pd.Series()
                    if not valid_dates_in_df.empty:
                        repaired_date = valid_dates_in_df.min()
                        repair_quality = 0.2  # Low confidence - global fallback
                
                # Update if repair succeeded
                if pd.notna(repaired_date):
                    df.loc[idx, 'trade_date'] = repaired_date
                    df.loc[idx, 'date_repaired'] = True
                    df.loc[idx, 'date_repair_quality'] = repair_quality  # Store quality score
                    repair_quality_scores.append(repair_quality)
                    repaired_count += 1
                else:
                    # Mark as unrepaired but keep the row
                    df.loc[idx, 'date_repaired'] = False
                    df.loc[idx, 'date_repair_quality'] = 0.0  # Failed repair
                    # Set to a sentinel date (far future) so it sorts last but is preserved
                    df.loc[idx, 'trade_date'] = pd.Timestamp('2099-12-31')
            
            # Calculate average repair quality
            avg_repair_quality = sum(repair_quality_scores) / len(repair_quality_scores) if repair_quality_scores else 0.0
            
            # Preserve original Date column for debugging
            if 'Date' in invalid_df.columns and 'original_date' not in df.columns:
                df['original_date'] = df['Date']
            
            # Log results with quality scoring
            unrepaired_count = invalid_count - repaired_count
            logger.warning(
                f"Found {invalid_count} rows with invalid trade_date out of {len(df)} total rows ({invalid_percentage:.1f}%). "
                f"Repaired {repaired_count} (avg quality: {avg_repair_quality:.2f}), {unrepaired_count} could not be repaired but were preserved."
            )
            
            # Alert user if significant data quality issues
            if invalid_percentage > 1.0:
                logger.error(
                    f"⚠️ CRITICAL: {invalid_percentage:.1f}% of data had invalid dates! "
                    f"All rows have been preserved (repaired or marked). "
                    f"Please investigate the source data quality."
                )
            
            # Warn if repair quality is low (many dates inferred rather than parsed)
            if repaired_count > 0 and avg_repair_quality < 0.5:
                logger.warning(
                    f"⚠️ WARNING: Low date repair quality ({avg_repair_quality:.2f}). "
                    f"Many dates were inferred rather than parsed. "
                    f"Please verify date column format in source data."
                )
            
            # All rows are now preserved (either repaired or marked with sentinel date)
        
        # Debug: Check for None values in sort columns before sorting
        sort_columns = ['trade_date', 'entry_time', 'Instrument', 'Stream']
        for col in sort_columns:
            if col in df.columns:
                none_count = df[col].isna().sum() if hasattr(df[col], 'isna') else (df[col] == None).sum()
                if none_count > 0:
                    logger.warning(f"Column '{col}' has {none_count} None/NaN values before sorting")
                    # Log sample of problematic rows
                    problematic = df[df[col].isna()] if hasattr(df[col], 'isna') else df[df[col] == None]
                    if len(problematic) > 0:
                        sample_cols = ['Stream', 'Date', 'Time', 'Result'] if all(c in df.columns for c in ['Stream', 'Date', 'Time', 'Result']) else list(df.columns[:5])
                        logger.debug(f"Sample rows with None in '{col}': {problematic[sample_cols].head(3).to_dict('records')}")
                # Replace None with empty string for string columns to avoid comparison errors
                if df[col].dtype == 'object':
                    df[col] = df[col].fillna('')
                    logger.debug(f"Filled None values in '{col}' with empty string for sorting")
        
        # Sort with invalid dates (sentinel date 2099-12-31) at the end
        df = df.sort_values(
            by=['trade_date', 'entry_time', 'Instrument', 'Stream'],
            ascending=[True, True, True, True],
            na_position='last'
        ).reset_index(drop=True)
        
        # Update global_trade_id after sorting
        df['global_trade_id'] = range(1, len(df) + 1)
        
        # Calculate Time Change column from sequencer output (Time column)
        # This must be done AFTER sorting to ensure correct chronological order per stream
        from .utils import normalize_time
        
        if 'Time Change' not in df.columns:
            df['Time Change'] = ''
        
        # Calculate Time Change per stream (each stream has its own time sequence)
        # According to sequencer logic:
        # - Time column shows the time used FOR that day
        # - Time changes happen at the END of a day (after trade selection)
        # - The change affects the NEXT day's Time column
        # - So Time Change should show on the day when Time actually changed
        if 'Time' in df.columns and 'trade_date' in df.columns and 'Stream' in df.columns:
            df['Time Change'] = ''  # Reset to empty
            
            # Process each stream separately (each has independent time sequence)
            for stream_id in df['Stream'].unique():
                stream_mask = df['Stream'] == stream_id
                stream_subset = df[stream_mask].copy()
                
                if len(stream_subset) < 2:
                    continue  # Need at least 2 days to detect changes
                
                # Ensure chronological order within this stream
                stream_subset = stream_subset.sort_values('trade_date')
                
                # Compare consecutive days: Time Change should only show when:
                # 1. Time changed from previous day to current day
                # 2. Previous day had Result = 'LOSS' (time changes only happen after losses)
                prev_idx = None
                prev_time_normalized = None
                for idx in stream_subset.index:
                    curr_time_str = str(df.loc[idx, 'Time'])
                    curr_time_normalized = normalize_time(curr_time_str)
                    
                    if prev_time_normalized is not None and prev_idx is not None and prev_time_normalized != curr_time_normalized:
                        # Check if previous day had a LOSS (time changes only happen after losses)
                        prev_result = str(df.loc[prev_idx, 'Result']).upper().strip()
                        if prev_result == 'LOSS':
                            # Time changed from previous day after a loss - show on previous day (when change occurred)
                            # Format: remove trailing whitespace for clean canonical data
                            df.loc[prev_idx, 'Time Change'] = f"{prev_time_normalized} -> {curr_time_normalized}"
                    
                    prev_idx = idx
                    prev_time_normalized = curr_time_normalized
        
        self.master_df = df
        
        logger.info(f"Master matrix built: {len(df)} trades")
        logger.info(f"Date range: {df['trade_date'].min()} to {df['trade_date'].max()}")
        
        # Safe sorting with None handling
        stream_values = [s for s in df['Stream'].unique() if s is not None and pd.notna(s)] if 'Stream' in df.columns else []
        instrument_values = [i for i in df['Instrument'].unique() if i is not None and pd.notna(i)] if 'Instrument' in df.columns else []
        try:
            logger.info(f"Streams: {sorted(stream_values) if stream_values else 'N/A'}")
            logger.info(f"Instruments: {sorted(instrument_values) if instrument_values else 'N/A'}")
        except Exception as e:
            logger.warning(f"Error sorting streams/instruments for logging: {e}")
            logger.info(f"Streams (unsorted): {stream_values[:10] if stream_values else 'N/A'}")
            logger.info(f"Instruments (unsorted): {instrument_values[:10] if instrument_values else 'N/A'}")
        
        # Calculate and log summary statistics
        self._log_summary_stats(df)
        
        # Save master matrix using file_manager
        file_manager.save_master_matrix(df, output_dir, specific_date)
        
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
    
    def update_master_matrix(self, output_dir: str = "data/master_matrix",
                            stream_filters: Optional[Dict[str, Dict]] = None,
                            analyzer_runs_dir: Optional[str] = None) -> Tuple[pd.DataFrame, Dict]:
        """
        Update master matrix by adding only new dates (after latest date per stream).
        For sequencer accuracy, reprocesses the last full year of data per stream.
        
        Args:
            output_dir: Directory containing existing master matrix and where to save updated one
            stream_filters: Per-stream filter configuration
            analyzer_runs_dir: Override analyzer runs directory (optional)
            
        Returns:
            Tuple of (updated DataFrame, update_stats dict with counts of new trades per stream)
        """
        # High-signal INFO logs only (update start)
        logger.info("=" * 80)
        logger.info("UPDATING MASTER MATRIX - Adding new dates only")
        logger.info("=" * 80)
        
        # Override analyzer_runs_dir if provided
        if analyzer_runs_dir:
            self.analyzer_runs_dir = Path(analyzer_runs_dir)
            self.streams = stream_manager.discover_streams(self.analyzer_runs_dir)
        
        # Update stream filters
        if stream_filters:
            self._update_stream_filters(stream_filters, merge=True)
        
        # Load existing master matrix using file_manager
        existing_df = file_manager.load_existing_matrix(output_dir)
        
        if existing_df.empty:
            logger.warning("No existing master matrix found. Use build_master_matrix() instead.")
            return pd.DataFrame(), {}
        
        # Ensure trade_date is datetime
        if 'trade_date' in existing_df.columns:
            if not pd.api.types.is_datetime64_any_dtype(existing_df['trade_date']):
                existing_df['trade_date'] = pd.to_datetime(existing_df['trade_date'])
        elif 'Date' in existing_df.columns:
            existing_df['trade_date'] = pd.to_datetime(existing_df['Date'])
        else:
            logger.error("No date column found in existing matrix!")
            return existing_df, {}
        
        # Find latest date per stream in existing matrix
        latest_dates = {}
        for stream_id in existing_df['Stream'].unique():
            stream_data = existing_df[existing_df['Stream'] == stream_id]
            latest_date = stream_data['trade_date'].max()
            latest_dates[stream_id] = latest_date
            logger.info(f"Stream {stream_id}: Latest date = {latest_date.date()}")
        
        # Find streams with new data and determine date ranges to process
        streams_to_update = []
        update_ranges = {}  # stream_id -> (start_date, end_date)
        update_stats = {}
        
        for stream_id in self.streams:
            stream_dir = self.analyzer_runs_dir / stream_id
            if not stream_dir.exists():
                continue
            
            # Find all parquet files for this stream
            parquet_files = []
            for year_dir in sorted(stream_dir.iterdir()):
                if not year_dir.is_dir():
                    continue
                year_dir_name = year_dir.name
                if len(year_dir_name) == 4 and year_dir_name.isdigit():
                    monthly_files = sorted(year_dir.glob(f"{stream_id}_an_*.parquet"))
                    parquet_files.extend(monthly_files)
            
            if not parquet_files:
                continue
            
            # Check for new dates in files
            latest_existing_date = latest_dates.get(stream_id, None)
            if latest_existing_date is None:
                # Stream not in existing matrix - need to add it (but this is more like rebuild)
                logger.info(f"Stream {stream_id} not in existing matrix - will add all data")
                streams_to_update.append(stream_id)
                update_ranges[stream_id] = (None, None)  # Process all data
                continue
            
            # Check files for dates after latest_existing_date
            has_new_data = False
            earliest_new_date = None
            latest_new_date = None
            
            for file_path in parquet_files:
                try:
                    # Quick check: prefer trade_date (canonical), fallback to Date for backward compatibility
                    df_check = pd.read_parquet(file_path)
                    if df_check.empty:
                        continue
                    
                    # Prefer trade_date (canonical), fallback to Date
                    date_col = 'trade_date' if 'trade_date' in df_check.columns else ('Date' if 'Date' in df_check.columns else None)
                    if date_col is None:
                        continue
                    
                    df_check[date_col] = pd.to_datetime(df_check[date_col])
                    new_dates = df_check[df_check[date_col] > latest_existing_date]
                    
                    if not new_dates.empty:
                        has_new_data = True
                        file_min = new_dates['Date'].min()
                        file_max = new_dates['Date'].max()
                        if earliest_new_date is None or file_min < earliest_new_date:
                            earliest_new_date = file_min
                        if latest_new_date is None or file_max > latest_new_date:
                            latest_new_date = file_max
                except Exception as e:
                    logger.debug(f"Error checking {file_path}: {e}")
                    continue
            
            if has_new_data:
                # For sequencer accuracy: reprocess last full year + new dates
                # Calculate start of last full year from latest_existing_date
                year_start = pd.Timestamp(latest_existing_date.year - 1, 1, 1)
                # But we only want to add dates after latest_existing_date
                process_start = year_start  # Start from last full year for sequencer accuracy
                process_end = latest_new_date  # End at latest new date
                
                streams_to_update.append(stream_id)
                update_ranges[stream_id] = (process_start.strftime('%Y-%m-%d'), process_end.strftime('%Y-%m-%d'))
                logger.info(f"Stream {stream_id}: New dates found from {earliest_new_date.date()} to {latest_new_date.date()}")
                logger.info(f"  Will reprocess from {process_start.date()} (last full year) to {process_end.date()} for sequencer accuracy")
        
        if not streams_to_update:
            logger.info("No new data found. Matrix is up to date.")
            return existing_df, {"message": "No new data found", "streams_updated": 0}
        
        logger.info(f"Found {len(streams_to_update)} stream(s) with new data: {streams_to_update}")
        
        # Process each stream: load ALL historical data for sequencer accuracy, then filter to only new dates
        # REFACTORED: Use shared sequencer callback instead of duplicating logic
        all_new_trades = []
        apply_sequencer = self._create_sequencer_callback()
        
        for stream_id in streams_to_update:
            logger.info(f"Processing stream {stream_id}...")
            
            # Load ALL historical data for this stream (needed for accurate sequencer logic)
            # Use data_loader for consistency with _load_all_streams_with_sequencer
            stream_trades_list = []
            stream_dir = self.analyzer_runs_dir / stream_id
            parquet_files = data_loader.find_parquet_files(stream_dir, stream_id)
            
            if not parquet_files:
                logger.warning(f"Stream {stream_id}: No parquet files found")
                continue
            
            # Load all historical data for sequencer accuracy
            for file_path in parquet_files:
                try:
                    df = pd.read_parquet(file_path)
                    if df.empty:
                        continue
                    if 'Stream' not in df.columns:
                        df['Stream'] = stream_id
                    else:
                        df['Stream'] = stream_id
                    stream_trades_list.append(df)
                except Exception as e:
                    logger.error(f"Error loading {file_path}: {e}")
                    continue
            
            if not stream_trades_list:
                continue
            
            # Merge all historical data for sequencer accuracy
            all_history_df = pd.concat(stream_trades_list, ignore_index=True) if len(stream_trades_list) > 1 else stream_trades_list[0]
            
            # Apply sequencer logic to all historical data using shared callback
            sequencer_result = apply_sequencer(all_history_df, display_year=None)
            
            # Filter to only new dates (after latest_existing_date)
            latest_existing = latest_dates.get(stream_id)
            if latest_existing:
                # trade_date is canonical; Date is fallback only
                if 'trade_date' not in sequencer_result.columns:
                    if 'Date' in sequencer_result.columns:
                        # Fallback: derive trade_date from Date (for backward compatibility)
                        sequencer_result['trade_date'] = pd.to_datetime(sequencer_result['Date'])
                    else:
                        logger.warning(f"Stream {stream_id}: No date column found in sequencer result")
                        continue
                else:
                    sequencer_result['trade_date'] = pd.to_datetime(sequencer_result['trade_date'])
                
                new_trades = sequencer_result[sequencer_result['trade_date'] > latest_existing]
            else:
                # Stream not in existing matrix - add all
                new_trades = sequencer_result
            
            if not new_trades.empty:
                all_new_trades.append(new_trades)
                update_stats[stream_id] = len(new_trades)
                logger.info(f"  Added {len(new_trades)} new trades for stream {stream_id}")
            else:
                logger.info(f"  No new trades after sequencer logic for stream {stream_id}")
        
        if not all_new_trades:
            logger.info("No new trades to add after processing.")
            return existing_df, {"message": "No new trades found", "streams_updated": 0, "trades_added": 0}
        
        # Merge new trades
        new_df = pd.concat(all_new_trades, ignore_index=True) if len(all_new_trades) > 1 else all_new_trades[0]
        
        # Normalize schema and add global columns
        new_df = self.normalize_schema(new_df)
        new_df = self.add_global_columns(new_df)
        
        # Merge with existing data
        updated_df = pd.concat([existing_df, new_df], ignore_index=True)
        
        # CANONICAL SORTING: MasterMatrix is the ONLY layer that sorts the output.
        # Output from update_master_matrix() is fully sorted by: trade_date, entry_time, Instrument, Stream
        # API and UI layers must NOT re-sort - they should assume data is already correctly sorted.
        if 'trade_date' in updated_df.columns:
            # Debug: Check for None values in sort columns before sorting
            sort_columns = ['trade_date', 'entry_time', 'Instrument', 'Stream']
            for col in sort_columns:
                if col in updated_df.columns:
                    none_count = updated_df[col].isna().sum() if hasattr(updated_df[col], 'isna') else (updated_df[col] == None).sum()
                    if none_count > 0:
                        logger.warning(f"[update_master_matrix] Column '{col}' has {none_count} None/NaN values before sorting")
                    # Replace None with empty string for string columns to avoid comparison errors
                    if updated_df[col].dtype == 'object':
                        updated_df[col] = updated_df[col].fillna('')
                        logger.debug(f"[update_master_matrix] Filled None values in '{col}' with empty string for sorting")
            
            updated_df = updated_df.sort_values(
                by=['trade_date', 'entry_time', 'Instrument', 'Stream'],
                ascending=[True, True, True, True],
                na_position='last'
            ).reset_index(drop=True)
        
        # Update global_trade_id
        updated_df['global_trade_id'] = range(1, len(updated_df) + 1)
        
        # Save updated matrix using file_manager
        file_manager.save_master_matrix(updated_df, output_dir, specific_date=None)
        
        total_new = len(new_df)
        update_stats['total_trades_added'] = total_new
        update_stats['streams_updated'] = len(streams_to_update)
        update_stats['message'] = f"Added {total_new} new trades from {len(streams_to_update)} stream(s)"
        
        logger.info("=" * 80)
        logger.info(f"UPDATE COMPLETE: Added {total_new} new trades from {len(streams_to_update)} stream(s)")
        logger.info("=" * 80)
        
        return updated_df, update_stats


def main():
    """Main function for command-line usage"""
    import argparse
    
    parser = argparse.ArgumentParser(description='Build Master Matrix from all streams')
    parser.add_argument('--start-date', type=str, help='Start date (YYYY-MM-DD)')
    parser.add_argument('--end-date', type=str, help='End date (YYYY-MM-DD)')
    parser.add_argument('--today', type=str, help='Specific date for today mode (YYYY-MM-DD)')
    parser.add_argument('--analyzer-runs-dir', type=str, default='data/analyzed',
                       help='Directory containing analyzer output files (all trades)')
    parser.add_argument('--output-dir', type=str, default='data/master_matrix',
                       help='Output directory for master matrix files')
    
    args = parser.parse_args()
    
    matrix = MasterMatrix(analyzer_runs_dir=args.analyzer_runs_dir)
    master_df = matrix.build_master_matrix(
        start_date=args.start_date,
        end_date=args.end_date,
        specific_date=args.today,
        output_dir=args.output_dir,
        analyzer_runs_dir=args.analyzer_runs_dir
    )
    
    if not master_df.empty:
        # Summary stats are already logged by build_master_matrix
        print("\n" + "=" * 80)
        print("MASTER MATRIX SUMMARY")
        print("=" * 80)
        print(f"Total trades: {len(master_df)}")
        print(f"Date range: {master_df['trade_date'].min()} to {master_df['trade_date'].max()}")
        print(f"Streams: {sorted(master_df['Stream'].unique())}")
        print(f"Instruments: {sorted(master_df['Instrument'].unique())}")
        
        # Calculate and display stats
        stats = matrix._log_summary_stats(master_df)
        
        print(f"\nFirst 5 trades:")
        print(master_df[['global_trade_id', 'trade_date', 'entry_time', 'Instrument', 'Stream', 
                         'Session', 'Result', 'Profit', 'final_allowed']].head().to_string(index=False))


if __name__ == "__main__":
    main()

