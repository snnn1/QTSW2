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
from .config import DOM_BLOCKED_DAYS, MATRIX_REPROCESS_TRADING_DAYS, MATRIX_CHECKPOINT_FREQUENCY
from .checkpoint_manager import CheckpointManager
from .trading_days import find_trading_days_back, get_merged_data_date_range
from .run_history import RunHistory
from .utils import _enforce_trade_date_invariants

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
        # Note: Sequencer logic already filters by stream list, so no additional filtering needed
        new_df = self._load_all_streams_with_sequencer(start_date=None, end_date=None, specific_date=None)
        
        # Validate empty streams based on criticality
        if new_df.empty:
            self._validate_empty_streams(streams_to_load, "partial rebuild")
        else:
            logger.info(f"Loaded {len(new_df)} trades from requested streams: {streams_to_load}")
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
                # INVARIANT: trade_date is canonical datetime column, Date is legacy-derived only
                # Ensure both DataFrames are safe to mutate (copy if they might be slices)
                existing_df = existing_df.copy()
                new_df = new_df.copy()
                
                # Pre-concat enforcement: enforce invariants on both DataFrames
                # trade_date canonical, Date derived for legacy compatibility only
                _enforce_trade_date_invariants(existing_df, "partial_rebuild_pre_concat_existing")
                _enforce_trade_date_invariants(new_df, "partial_rebuild_pre_concat_new")
                
                # Concatenate
                df = pd.concat([existing_df, new_df], ignore_index=True)
                
                # Post-concat enforcement: enforce invariants on combined DataFrame
                # trade_date canonical, Date derived for legacy compatibility only
                _enforce_trade_date_invariants(df, "partial_rebuild_post_concat")
                
                logger.info(f"Merged with existing data: {len(df)} total trades")
            else:
                df = existing_df
                logger.info(f"No new trades to merge (streams skipped), keeping existing data: {len(df)} trades")
        else:
            df = new_df
            if df.empty:
                self._validate_empty_streams(streams_to_load, "final result")
        
        return df
    
    def _is_critical_stream(self, stream_id: str) -> bool:
        """
        Check if a stream is marked as critical.
        
        Args:
            stream_id: Stream ID to check
            
        Returns:
            True if stream is critical, False otherwise
        """
        from .config import CRITICAL_STREAMS
        return stream_id in CRITICAL_STREAMS
    
    def _validate_streams(self, streams: List[str], context: str = "") -> None:
        """
        Validate that streams exist and are discoverable.
        
        Args:
            streams: List of stream IDs to validate
            context: Context string for error messages
            
        Raises:
            ValueError: If critical streams are missing
        """
        discovered_streams = set(stream_manager.discover_streams(self.analyzer_runs_dir))
        missing_streams = set(streams) - discovered_streams
        
        if missing_streams:
            critical_missing = [s for s in missing_streams if self._is_critical_stream(s)]
            non_critical_missing = [s for s in missing_streams if not self._is_critical_stream(s)]
            
            if critical_missing:
                error_msg = (
                    f"{context}: Critical streams missing: {sorted(critical_missing)}. "
                    f"Critical streams must exist and have data."
                )
                logger.error(error_msg)
                raise ValueError(error_msg)
            
            if non_critical_missing:
                logger.warning(
                    f"{context}: Non-critical streams missing: {sorted(non_critical_missing)}. "
                    f"Continuing with available streams."
                )
    
    def _validate_empty_streams(self, stream_ids: List[str], context: str = "") -> None:
        """
        Validate empty streams based on criticality.
        
        Critical streams cause ERROR (fail-closed).
        Non-critical streams cause WARN (continue processing).
        
        Args:
            stream_ids: List of stream IDs that are empty
            context: Context string for error messages
            
        Raises:
            ValueError: If critical streams are empty
        """
        critical_empty = [s for s in stream_ids if self._is_critical_stream(s)]
        non_critical_empty = [s for s in stream_ids if not self._is_critical_stream(s)]
        
        if critical_empty:
            error_msg = (
                f"{context}: Critical streams are empty: {sorted(critical_empty)}. "
                f"Critical streams must have data. Aborting build."
            )
            logger.error(error_msg)
            raise ValueError(error_msg)
        
        if non_critical_empty:
            logger.warning(
                f"{context}: Non-critical streams are empty: {sorted(non_critical_empty)}. "
                f"Continuing with available streams."
            )
        
    def _create_sequencer_callback(self) -> Callable:
        """
        Create sequencer callback function that uses current stream_filters.
        
        Returns:
            Callable function that applies sequencer logic to a DataFrame
        """
        def apply_sequencer(df: pd.DataFrame, display_year: Optional[int] = None) -> pd.DataFrame:
            # Sequencer call (debug logging removed - already verified working)
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
    
    def _log_summary_stats(self, df: pd.DataFrame) -> Dict:
        """Calculate and log summary statistics using statistics module."""
        return statistics.calculate_summary_stats(df)
    
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
        
        # Normalize schema
        df = self.normalize_schema(df)
        
        # TIME COLUMN PROTECTION: Capture Time column snapshot immediately post-sequencer
        # This is the first checkpoint - Time column is owned by sequencer at this point
        # Also capture key columns for row matching after sorting (sorting reorders rows)
        if 'Time' in df.columns:
            time_snapshot_post_sequencer = df['Time'].copy()
            # Capture key columns for row matching (needed because sorting will reorder rows)
            if all(col in df.columns for col in ['Stream', 'trade_date', 'entry_time']):
                time_snapshot_keys = df[['Stream', 'trade_date', 'entry_time']].copy()
            else:
                time_snapshot_keys = None
                logger.warning("Missing key columns for Time snapshot matching - using positional comparison")
            logger.debug("Time column snapshot captured post-sequencer (first checkpoint)")
        else:
            time_snapshot_post_sequencer = None
            time_snapshot_keys = None
            logger.warning("Time column missing post-sequencer - sequencer should have set Time column")
        
        # CONTRACT: ProfitDollars must exist before filter_engine
        # ProfitDollars is required by filter_engine for stream health gate calculations
        # DataLoader should have already created it, but ensure it exists as defensive check
        # (using statistics module helper - not synthesizing here)
        if 'ProfitDollars' not in df.columns:
            from .statistics import _ensure_profit_dollars_column_inplace
            _ensure_profit_dollars_column_inplace(df, contract_multiplier=1.0)
            logger.warning("ProfitDollars was missing - created by master_matrix (should have been created by data_loader)")
        else:
            logger.debug("ProfitDollars column exists (created by data_loader)")
        
        # Add global columns (applies filters)
        df = self.add_global_columns(df)
        
        # SL comes from analyzer output (schema_normalizer ensures it exists with NaN if missing)
        
        # Ensure Time Change column exists (should already be there from _apply_sequencer_logic)
        if 'Time Change' not in df.columns:
            logger.warning("Time Change column missing, adding with empty values")
            df['Time Change'] = ''
        
        # DATE OWNERSHIP: DataLoader owns date normalization
        # Validate that trade_date exists and has correct dtype (no parsing)
        from .data_loader import _validate_trade_date_dtype, _validate_trade_date_presence
        
        if 'trade_date' not in df.columns:
            logger.error("build_master_matrix: Missing trade_date column - DataLoader should have normalized dates")
            raise ValueError("Missing trade_date column - DataLoader must normalize dates before master matrix build")
        
        # PERFORMANCE OPTIMIZATION: Validate trade_date dtype once for entire DataFrame (contract-first)
        # Global validation enforces the contract (fail-fast on any violation)
        # Per-stream validation is only for diagnostics when the global check fails
        try:
            _validate_trade_date_dtype(df, "all_streams")
        except ValueError as e:
            # If validation fails, validate per-stream for detailed diagnostics
            logger.error(f"build_master_matrix: trade_date validation failed globally: {e}")
            for stream_id in df['Stream'].unique() if 'Stream' in df.columns else []:
                stream_mask = df['Stream'] == stream_id
                stream_df = df[stream_mask]
                try:
                    _validate_trade_date_dtype(stream_df, stream_id)
                except ValueError:
                    pass  # Already logged in global validation
            raise  # Re-raise original error
        
        # FAIL-CLOSED: Invalid dates are Tier-0 risk (hard contract violations)
        # Check for invalid dates and fail fast
        valid_dates = df['trade_date'].notna()
        if not valid_dates.all():
            invalid_count = (~valid_dates).sum()
            invalid_df = df[~valid_dates].copy()
            invalid_percentage = invalid_count / len(df) * 100
            
            # Collect stream-level details for error reporting
            invalid_by_stream = {}
            invalid_samples_by_stream = {}
            if 'Stream' in df.columns:
                invalid_by_stream = invalid_df.groupby('Stream').size().to_dict()
                for stream_id in invalid_by_stream.keys():
                    stream_invalid = invalid_df[invalid_df['Stream'] == stream_id]
                    invalid_samples_by_stream[stream_id] = stream_invalid['trade_date'].head(5).tolist()
            
            # Build comprehensive error message
            error_msg = (
                f"[CONTRACT VIOLATION] Found {invalid_count} rows with invalid trade_date ({invalid_percentage:.1f}%). "
                f"Analyzer output contract requires valid dates (Invariant 1). "
                f"This is a Tier-0 risk - invalid dates cause hard failures."
            )
            
            if invalid_by_stream:
                error_msg += "\nInvalid dates by stream:"
                for stream_id, count in invalid_by_stream.items():
                    samples = invalid_samples_by_stream.get(stream_id, [])
                    error_msg += f"\n  - {stream_id}: {count} rows. Sample values: {samples}"
            
            logger.error(error_msg)
            
            # FAIL-CLOSED: Abort build or fail affected streams
            from .config import ALLOW_INVALID_DATES_SALVAGE
            if not ALLOW_INVALID_DATES_SALVAGE:
                # Default: Fail fast
                raise ValueError(
                    f"Master Matrix build aborted: {invalid_count} rows with invalid trade_date. "
                    f"Fix analyzer output before proceeding. "
                    f"Set ALLOW_INVALID_DATES_SALVAGE=True for salvage mode (debugging only)."
                )
            else:
                # Salvage mode: Drop invalid rows (never propagate NaT downstream)
                logger.error(
                    f"Salvage mode enabled: Dropping {invalid_count} rows with invalid trade_date. "
                    f"Salvage mode never propagates NaT downstream."
                )
                df = df[valid_dates].copy()
                
                # Ensure trade_date remains datetime dtype after copy operation
                if 'trade_date' in df.columns and not pd.api.types.is_datetime64_any_dtype(df['trade_date']):
                    logger.warning(
                        f"trade_date column lost datetime dtype after copy operation ({df['trade_date'].dtype}), "
                        f"converting to datetime64."
                    )
                    df['trade_date'] = pd.to_datetime(df['trade_date'], errors='raise')
                
                if df.empty:
                    raise ValueError(
                        f"Master Matrix build aborted: All rows dropped due to invalid dates. "
                        f"Stream failed."
                    )
        
        # CANONICAL SORTING: MasterMatrix is the ONLY layer that sorts the output.
        # Output from build_master_matrix() is fully sorted by: Stream, trade_date, entry_time (grouping-first order)
        # This grouping-first order enables Time Change loop optimization (no per-stream copy/sort needed)
        # API and UI layers must NOT re-sort - they should assume data is already correctly sorted.
        # Ensure trade_date is datetime dtype before sorting (operations can sometimes change dtype)
        if 'trade_date' in df.columns:
            if not pd.api.types.is_datetime64_any_dtype(df['trade_date']):
                logger.warning(
                    f"trade_date column is {df['trade_date'].dtype} before sorting, "
                    f"converting to datetime64. This should not happen - check data processing pipeline."
                )
                df['trade_date'] = pd.to_datetime(df['trade_date'], errors='raise')
        
        df = df.sort_values(
            by=['Stream', 'trade_date', 'entry_time'],
            ascending=[True, True, True],
            na_position='last'
        ).reset_index(drop=True)
        
        # Update global_trade_id after sorting
        df['global_trade_id'] = range(1, len(df) + 1)
        
        # Time Change column is already set correctly by sequencer_logic.py
        # sequencer_logic sets TimeChange to show FUTURE changes (what time it WILL change to)
        # We should NOT recalculate it here - that would overwrite the correct values
        # Just ensure the column exists (sequencer should have already set it)
        if 'Time Change' not in df.columns:
            df['Time Change'] = ''
            logger.warning("Time Change column missing from sequencer output - sequencer should have set it")
        
        # PERFORMANCE OPTIMIZATION: Cache unique streams for reuse (used in logging)
        unique_streams_cached = df['Stream'].unique() if 'Stream' in df.columns else []
        
        self.master_df = df
        
        logger.info(f"Master matrix built: {len(df)} trades")
        # Ensure trade_date is datetime before using .min()/.max() (safeguard)
        if 'trade_date' in df.columns and not pd.api.types.is_datetime64_any_dtype(df['trade_date']):
            logger.warning(
                f"trade_date column is {df['trade_date'].dtype} before logging date range, "
                f"converting to datetime64."
            )
            df['trade_date'] = pd.to_datetime(df['trade_date'], errors='raise')
        logger.info(f"Date range: {df['trade_date'].min()} to {df['trade_date'].max()}")
        
        # PERFORMANCE OPTIMIZATION: Reuse cached unique_streams_cached (already computed above)
        # Cache unique_instruments for logging
        unique_instruments_cached = df['Instrument'].unique() if 'Instrument' in df.columns else []
        
        # Safe sorting with None handling
        stream_values = [s for s in unique_streams_cached if s is not None and pd.notna(s)]
        instrument_values = [i for i in unique_instruments_cached if i is not None and pd.notna(i)]
        try:
            logger.info(f"Streams: {sorted(stream_values) if stream_values else 'N/A'}")
            logger.info(f"Instruments: {sorted(instrument_values) if instrument_values else 'N/A'}")
        except Exception as e:
            logger.warning(f"Error sorting streams/instruments for logging: {e}")
            logger.info(f"Streams (unsorted): {stream_values[:10] if stream_values else 'N/A'}")
            logger.info(f"Instruments (unsorted): {instrument_values[:10] if instrument_values else 'N/A'}")
        
        # TIME COLUMN PROTECTION: Verify Time column just before final write (second checkpoint)
        # Check that Time column hasn't been mutated downstream
        if time_snapshot_post_sequencer is not None and 'Time' in df.columns:
            self._verify_time_column_postcondition(df, time_snapshot_post_sequencer, time_snapshot_keys)
        
        # Calculate and log summary statistics
        self._log_summary_stats(df)
        
        # Save master matrix using file_manager
        # Execution timetable is automatically persisted by file_manager after save
        file_manager.save_master_matrix(df, output_dir, specific_date, stream_filters=self.stream_filters)
        
        # Create checkpoint after successful build (for window updates)
        if not df.empty and 'trade_date' in df.columns:
            try:
                # Ensure trade_date is datetime before using .max() (safeguard)
                if not pd.api.types.is_datetime64_any_dtype(df['trade_date']):
                    logger.warning(
                        f"trade_date column is {df['trade_date'].dtype} before checkpoint creation, "
                        f"converting to datetime64."
                    )
                    df['trade_date'] = pd.to_datetime(df['trade_date'], errors='raise')
                max_date = df['trade_date'].max()
                if pd.notna(max_date):
                    max_date_str = pd.to_datetime(max_date).strftime('%Y-%m-%d')
                    self._create_checkpoint_after_build(df, max_date_str, output_dir)
            except Exception as e:
                logger.warning(f"Failed to create checkpoint after build: {e}")
        
        return df
    
    def _verify_time_column_postcondition(
        self, 
        df: pd.DataFrame, 
        time_snapshot: pd.Series, 
        time_snapshot_keys: Optional[pd.DataFrame] = None
    ) -> None:
        """
        Verify Time column post-condition: Time column should not be mutated downstream.
        
        After sequencer, Time column is treated as owned. Downstream mutation is detected
        via equality/invariant checks. Solution is pragmatic and pandas-safe.
        
        CRITICAL: After sorting, rows are reordered, so we must match rows by key columns
        (Stream + trade_date + entry_time) rather than positionally.
        
        Args:
            df: Current DataFrame (after downstream processing, may be sorted)
            time_snapshot: Snapshot of Time column immediately post-sequencer (before sorting)
            time_snapshot_keys: Optional DataFrame with key columns (Stream, trade_date, entry_time)
                              from when snapshot was taken. If None, uses positional comparison
                              (which will fail if rows were sorted).
            
        Raises:
            AssertionError: If Time column has been mutated
        """
        if 'Time' not in df.columns:
            logger.error("Time column missing - sequencer should have set Time column")
            raise AssertionError("Time column missing - sequencer contract violation")
        
        if len(df) != len(time_snapshot):
            logger.warning(
                f"Time snapshot length mismatch: snapshot={len(time_snapshot)}, current={len(df)}. "
                f"This may indicate rows were added/removed downstream."
            )
            # Still check what we can
            min_len = min(len(df), len(time_snapshot))
            df_time_subset = df['Time'].iloc[:min_len]
            snapshot_subset = time_snapshot.iloc[:min_len]
        else:
            df_time_subset = df['Time']
            snapshot_subset = time_snapshot
        
        # Match rows by key columns if available (needed because sorting reorders rows)
        if time_snapshot_keys is not None and all(col in df.columns for col in ['Stream', 'trade_date', 'entry_time']):
            # Create composite key for matching rows
            # Normalize trade_date to string for comparison (handles datetime objects and NaT)
            def create_key(row):
                stream = str(row.get('Stream', '')) if pd.notna(row.get('Stream')) else ''
                trade_date_val = row.get('trade_date')
                if pd.isna(trade_date_val):
                    trade_date = ''
                elif isinstance(trade_date_val, pd.Timestamp):
                    trade_date = trade_date_val.strftime('%Y-%m-%d %H:%M:%S')
                else:
                    trade_date = str(trade_date_val)
                entry_time = str(row.get('entry_time', '')) if pd.notna(row.get('entry_time')) else ''
                return f"{stream}|{trade_date}|{entry_time}"
            
            # Create keys for current DataFrame
            df_keys = df[['Stream', 'trade_date', 'entry_time']].apply(create_key, axis=1)
            # Create keys for snapshot DataFrame (using original index from snapshot)
            snapshot_keys = time_snapshot_keys.apply(create_key, axis=1)
            
            # Create a mapping from snapshot key to snapshot index for efficient lookup
            snapshot_key_to_idx = {}
            for snapshot_idx in snapshot_keys.index:
                key = snapshot_keys.loc[snapshot_idx]
                if key not in snapshot_key_to_idx:
                    snapshot_key_to_idx[key] = snapshot_idx
                else:
                    # Duplicate key - log warning but use first match
                    logger.warning(f"Duplicate snapshot key {key} - multiple rows match")
            
            # Match rows by key and compare Time values
            from .utils import normalize_time
            
            mismatches = []
            matched_count = 0
            for idx in df.index:
                df_key = df_keys.loc[idx]
                # Find matching row in snapshot
                if df_key in snapshot_key_to_idx:
                    snapshot_idx = snapshot_key_to_idx[df_key]
                    df_time_val = normalize_time(str(df_time_subset.loc[idx]))
                    snapshot_time_val = normalize_time(str(snapshot_subset.loc[snapshot_idx]))
                    
                    if df_time_val != snapshot_time_val:
                        mismatches.append((idx, snapshot_time_val, df_time_val))
                    matched_count += 1
                else:
                    # Row not found in snapshot (shouldn't happen if no rows added/removed)
                    logger.warning(f"Row {idx} with key {df_key} not found in snapshot - may have been added/removed")
            
            if matched_count == 0:
                logger.error("No rows matched between snapshot and current DataFrame - key matching failed")
                # This is a serious error - cannot verify Time column without matching rows
                raise AssertionError(
                    "Time column verification failed: No rows matched between snapshot and current DataFrame. "
                    "This may indicate rows were added/removed or key columns changed."
                )
        else:
            # Fallback to positional comparison (will fail if rows were sorted)
            # Pandas-safe comparison: handle NaN, None, string normalization
            from .utils import normalize_time
            
            # Normalize both for comparison
            df_time_normalized = df_time_subset.astype(str).str.strip().apply(normalize_time)
            snapshot_normalized = snapshot_subset.astype(str).str.strip().apply(normalize_time)
            
            # Check for differences
            mismatches_mask = df_time_normalized != snapshot_normalized
            if mismatches_mask.any():
                mismatch_indices = df_time_subset[mismatches_mask].index.tolist()
                mismatches = [
                    (idx, snapshot_normalized.iloc[i], df_time_normalized.iloc[i])
                    for i, idx in enumerate(mismatch_indices)
                ]
            else:
                mismatches = []
        
        if mismatches:
            mismatch_count = len(mismatches)
            mismatch_pairs = mismatches[:10]  # First 10 mismatches
            
            logger.error(
                f"TIME COLUMN MUTATION DETECTED: {mismatch_count} rows have mutated Time column. "
                f"Time column is owned by sequencer and must not be mutated downstream. "
                f"Sample mismatches: {mismatch_pairs[:5]}"
            )
            raise AssertionError(
                f"Time column mutation detected: {mismatch_count} rows mutated. "
                f"Time column is owned by sequencer_logic.py and must not be mutated downstream."
            )
        
        logger.debug("Time column post-condition verified: No mutations detected")
    
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
    
    
    def _create_checkpoint_after_build(self, df: pd.DataFrame, checkpoint_date: str, output_dir: str):
        """
        Create a checkpoint after successful build by capturing sequencer state.
        
        Args:
            df: Master matrix DataFrame
            checkpoint_date: Date to checkpoint (YYYY-MM-DD)
            output_dir: Output directory (used to determine checkpoint location)
        """
        try:
            # Use apply_sequencer_logic_with_state to capture final states
            # We need to reload data and process it to get state, but we can use the existing df
            # Actually, we need to reprocess to get state. Let's do a lightweight approach:
            # Load all streams again and process with state capture
            
            logger.info(f"Creating checkpoint for date {checkpoint_date}...")
            
            # Load all streams and apply sequencer with state capture
            all_data = self._load_all_streams_with_sequencer_for_state()
            
            if all_data.empty:
                logger.warning("No data available for checkpoint creation")
                return
            
            # Apply sequencer logic with state capture
            apply_sequencer = self._create_sequencer_callback_with_state()
            result_df, final_states = apply_sequencer(all_data)
            
            if not final_states:
                logger.warning("No sequencer states captured for checkpoint")
                return
            
            # Create checkpoint
            checkpoint_mgr = CheckpointManager()
            checkpoint_id = checkpoint_mgr.create_checkpoint(
                checkpoint_date=checkpoint_date,
                stream_states=final_states
            )
            
            logger.info(f"Checkpoint {checkpoint_id} created successfully for date {checkpoint_date}")
        except Exception as e:
            logger.error(f"Failed to create checkpoint: {e}")
            import traceback
            logger.debug(f"Checkpoint creation traceback: {traceback.format_exc()}")
    
    def _load_all_streams_with_sequencer_for_state(self) -> pd.DataFrame:
        """Load all streams without sequencer (for state capture)."""
        if not self.streams or len(self.streams) == 0:
            self.streams = stream_manager.discover_streams(self.analyzer_runs_dir)
        
        if not self.streams:
            return pd.DataFrame()
        
        # Load all streams WITHOUT sequencer logic (we'll apply it with state capture)
        return data_loader.load_all_streams(
            streams=self.streams,
            analyzer_runs_dir=self.analyzer_runs_dir,
            start_date=None,
            end_date=None,
            specific_date=None,
            wait_for_streams=True,
            max_retries=3,
            retry_delay_seconds=2,
            apply_sequencer_logic=None  # Don't apply sequencer here
        )
    
    def _create_sequencer_callback_with_state(self) -> Callable:
        """Create sequencer callback that captures state."""
        def apply_sequencer_with_state(df: pd.DataFrame, display_year: Optional[int] = None):
            return sequencer_logic.apply_sequencer_logic_with_state(
                df, self.stream_filters, display_year, parallel=False
            )
        return apply_sequencer_with_state
    
    
    def _create_sequencer_callback_with_restored_state(self, restored_states: Dict[str, Dict]) -> Callable:
        """Create sequencer callback that uses restored initial states."""
        def apply_sequencer_with_state(df: pd.DataFrame, display_year: Optional[int] = None):
            return sequencer_logic.apply_sequencer_logic_with_state(
                df, self.stream_filters, display_year, parallel=False, initial_states=restored_states
            )
        return apply_sequencer_with_state
    
    def build_master_matrix_rolling_resequence(
        self,
        resequence_days: int = 40,
        output_dir: str = "data/master_matrix",
        stream_filters: Optional[Dict[str, Dict]] = None,
        analyzer_runs_dir: Optional[str] = None
    ) -> Tuple[pd.DataFrame, Dict]:
        """
        Perform rolling resequence: remove window rows and resequence from checkpoint state.
        
        Behavior:
        1. Discover all analyzer output from data/analyzed/ (full disk scan)
        2. Compute resequence_start_date = today - N trading days
        3. Load existing master matrix
        4. Remove all rows where trade_date >= resequence_start_date
        5. Restore sequencer state from checkpoint immediately before resequence_start_date
        6. Run sequencer forward using analyzer data for dates >= resequence_start_date
        7. Append newly sequenced rows to preserved historical matrix rows
        8. Save single new master matrix file
        
        Args:
            resequence_days: Number of trading days to resequence (default 40)
            output_dir: Directory containing matrix outputs
            stream_filters: Per-stream filter configuration
            analyzer_runs_dir: Override analyzer runs directory
            
        Returns:
            Tuple of (updated DataFrame, run_summary dict)
        """
        from .master_matrix_rolling_resequence import build_master_matrix_rolling_resequence
        return build_master_matrix_rolling_resequence(
            self, resequence_days, output_dir, stream_filters, analyzer_runs_dir
        )


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
        # PERFORMANCE OPTIMIZATION: Cache unique values
        unique_streams_main = master_df['Stream'].unique()
        unique_instruments_main = master_df['Instrument'].unique()
        stream_values = [s for s in unique_streams_main if s is not None and pd.notna(s)]
        instrument_values = [i for i in unique_instruments_main if i is not None and pd.notna(i)]
        print(f"Streams: {sorted(stream_values)}")
        print(f"Instruments: {sorted(instrument_values)}")
        
        # Calculate and display stats
        stats = matrix._log_summary_stats(master_df)
        
        print(f"\nFirst 5 trades:")
        print(master_df[['global_trade_id', 'trade_date', 'entry_time', 'Instrument', 'Stream', 
                         'Session', 'Result', 'Profit', 'final_allowed']].head().to_string(index=False))


if __name__ == "__main__":
    main()

