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
        
        if new_df.empty:
            logger.warning(f"[WARNING] No trades loaded for streams: {streams_to_load}")
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
        
        # Add global columns (applies filters)
        df = self.add_global_columns(df)
        
        # SL comes from analyzer output (schema_normalizer ensures it exists with NaN if missing)
        
        # Ensure Time Change column exists (should already be there from _apply_sequencer_logic)
        if 'Time Change' not in df.columns:
            logger.warning("Time Change column missing, adding with empty values")
            df['Time Change'] = ''
        
        # CANONICAL SORTING: MasterMatrix is the ONLY layer that sorts the output.
        # Output from build_master_matrix() is fully sorted by: trade_date, entry_time, Instrument, Stream
        # API and UI layers must NOT re-sort - they should assume data is already correctly sorted.
        # Ensure trade_date is datetime (not date objects) for consistent sorting
        if 'trade_date' in df.columns:
            if df['trade_date'].dtype == 'object' or not pd.api.types.is_datetime64_any_dtype(df['trade_date']):
                # Convert date objects to datetime for proper sorting
                from .utils import normalize_date
                df['trade_date'] = df['trade_date'].apply(normalize_date)
        
        # Initialize date_repaired and date_repair_quality columns (for backward compatibility)
        # Note: With analyzer contract enforcement, invalid dates should not occur
        # But we keep these columns for existing data compatibility
        if 'date_repaired' not in df.columns:
            df['date_repaired'] = False
        if 'date_repair_quality' not in df.columns:
            df['date_repair_quality'] = 1.0  # 1.0 = original date was valid, no repair needed
        
        # Handle rows with invalid trade_date - preserve with sentinel date only
        # CONTRACT ENFORCEMENT: AnalyzerOutputValidator ensures all dates are valid
        # This code path should rarely execute, but preserves rows for sorting stability
        valid_dates = df['trade_date'].notna()
        if not valid_dates.all():
            invalid_count = (~valid_dates).sum()
            invalid_df = df[~valid_dates].copy()
            invalid_percentage = invalid_count / len(df) * 100
            
            # Log warning - invalid dates should not occur with contract enforcement
            if 'Stream' in df.columns:
                invalid_by_stream = invalid_df.groupby('Stream').size()
                for stream_id, count in invalid_by_stream.items():
                    logger.error(
                        f"[CONTRACT VIOLATION] {stream_id} has {count} trades with invalid trade_date! "
                        f"This violates analyzer output contract (Invariant 1). "
                        f"Preserving rows with sentinel date for sorting stability."
                    )
            
            # Preserve rows with sentinel date (no repair attempts - analyzer should provide valid dates)
            for idx in invalid_df.index:
                df.loc[idx, 'date_repaired'] = False
                df.loc[idx, 'date_repair_quality'] = 0.0  # Failed - should not occur
                # Set to sentinel date (far future) so it sorts last but is preserved
                df.loc[idx, 'trade_date'] = pd.Timestamp('2099-12-31')
            
            logger.error(
                f"[CONTRACT VIOLATION] Found {invalid_count} rows with invalid trade_date ({invalid_percentage:.1f}%). "
                f"Analyzer output contract requires valid dates (Invariant 1). "
                f"Rows preserved with sentinel date for sorting stability."
            )
        
        # Sort with invalid dates (sentinel date 2099-12-31) at the end
        # entry_time None/empty values are now '23:59:59' so they sort after valid times
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
                            # Format: show only the new time (time changed to)
                            df.loc[prev_idx, 'Time Change'] = curr_time_normalized
                    
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
        # Execution timetable is automatically persisted by file_manager after save
        file_manager.save_master_matrix(df, output_dir, specific_date, stream_filters=self.stream_filters)
        
        # Create checkpoint after successful build (for window updates)
        if not df.empty and 'trade_date' in df.columns:
            try:
                max_date = df['trade_date'].max()
                if pd.notna(max_date):
                    max_date_str = pd.to_datetime(max_date).strftime('%Y-%m-%d')
                    self._create_checkpoint_after_build(df, max_date_str, output_dir)
            except Exception as e:
                logger.warning(f"Failed to create checkpoint after build: {e}")
        
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
        stream_values = [s for s in master_df['Stream'].unique() if s is not None and pd.notna(s)]
        instrument_values = [i for i in master_df['Instrument'].unique() if i is not None and pd.notna(i)]
        print(f"Streams: {sorted(stream_values)}")
        print(f"Instruments: {sorted(instrument_values)}")
        
        # Calculate and display stats
        stats = matrix._log_summary_stats(master_df)
        
        print(f"\nFirst 5 trades:")
        print(master_df[['global_trade_id', 'trade_date', 'entry_time', 'Instrument', 'Stream', 
                         'Session', 'Result', 'Profit', 'final_allowed']].head().to_string(index=False))


if __name__ == "__main__":
    main()

