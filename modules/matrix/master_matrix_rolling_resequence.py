"""
Rolling Resequence Implementation

This module implements the rolling resequence mode for master matrix updates.
"""

import pandas as pd
from pathlib import Path
from typing import Optional, Dict, Tuple
from datetime import datetime
import logging

from .checkpoint_manager import CheckpointManager
from .trading_days import find_trading_days_back
from .file_manager import load_existing_matrix, save_master_matrix
from .logging_config import setup_matrix_logger
from .utils import _enforce_trade_date_invariants

logger = setup_matrix_logger(__name__, console=True, level=logging.INFO)


def build_master_matrix_rolling_resequence(
    master_matrix_instance,
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
        master_matrix_instance: MasterMatrix instance (for access to methods)
        resequence_days: Number of trading days to resequence (default 40)
        output_dir: Directory containing matrix outputs
        stream_filters: Per-stream filter configuration
        analyzer_runs_dir: Override analyzer runs directory
        
    Returns:
        Tuple of (updated DataFrame, run_summary dict)
    """
    import time
    start_time = time.time()
    
    try:
        logger.info("=" * 80)
        logger.info("MASTER MATRIX: ROLLING RESEQUENCE")
        logger.info("=" * 80)
        
        # Import stream_manager
        from . import stream_manager
        
        # Override analyzer_runs_dir if provided
        if analyzer_runs_dir:
            master_matrix_instance.analyzer_runs_dir = Path(analyzer_runs_dir)
            master_matrix_instance.streams = stream_manager.discover_streams(
                master_matrix_instance.analyzer_runs_dir
            )
        
        # Update stream filters
        if stream_filters:
            master_matrix_instance._update_stream_filters(stream_filters, merge=True)
        
        logger.info(f"Resequencing last {resequence_days} trading days")
        
        # Step 1: Discover all analyzer output (full disk scan)
        logger.info("Discovering analyzer output from disk...")
        all_analyzer_data = master_matrix_instance._load_all_streams_with_sequencer_for_state()
        
        if all_analyzer_data.empty:
            error_msg = "No analyzer data found"
            logger.error(error_msg)
            return pd.DataFrame(), {"error": error_msg}
        
        # Get latest date from analyzer data
        # DATE OWNERSHIP: DataLoader owns date normalization
        # Validate that trade_date exists and has correct dtype (no parsing)
        from .data_loader import _validate_trade_date_dtype, _validate_trade_date_presence
        
        if 'trade_date' not in all_analyzer_data.columns:
            error_msg = "No trade_date column found in analyzer data - DataLoader should have normalized dates"
            logger.error(error_msg)
            return pd.DataFrame(), {"error": error_msg}
        
        # Validate trade_date dtype (should already be datetime from DataLoader)
        try:
            _validate_trade_date_dtype(all_analyzer_data, "rolling_resequence")
        except ValueError as e:
            logger.error(f"Rolling resequence: trade_date validation failed: {e}")
            return pd.DataFrame(), {"error": str(e)}
        
        latest_analyzer_date = all_analyzer_data['trade_date'].max()
        
        if pd.isna(latest_analyzer_date):
            error_msg = "Could not determine latest analyzer date"
            logger.error(error_msg)
            return pd.DataFrame(), {"error": error_msg}
        
        latest_analyzer_date_str = pd.to_datetime(latest_analyzer_date).strftime('%Y-%m-%d')
        logger.info(f"Latest analyzer date: {latest_analyzer_date_str}")
        
        # Step 2: Compute resequence_start_date = today - N trading days
        # Use latest analyzer date as "today" reference
        resequence_start_date = find_trading_days_back(
            all_analyzer_data, latest_analyzer_date_str, resequence_days
        )
        
        if not resequence_start_date:
            error_msg = f"Insufficient history: need {resequence_days} trading days back from {latest_analyzer_date_str}"
            logger.error(error_msg)
            return pd.DataFrame(), {"error": error_msg}
        
        logger.info(f"Resequence start date: {resequence_start_date}")
        
        # Step 3: Load existing master matrix
        logger.info("Loading existing master matrix...")
        existing_df = load_existing_matrix(output_dir)
        
        if existing_df.empty:
            error_msg = "No existing master matrix found. Please run a full rebuild first."
            logger.error(error_msg)
            return pd.DataFrame(), {"error": error_msg}
        
        rows_before_resequence = len(existing_df)
        logger.info(f"Existing matrix has {rows_before_resequence} rows")
        
        # Step 4: Remove all rows where trade_date >= resequence_start_date
        # DATE OWNERSHIP: Existing matrix should have trade_date already normalized
        # Validate dtype but don't parse
        if 'trade_date' not in existing_df.columns:
            error_msg = "No trade_date column found in existing matrix - matrix should have trade_date from DataLoader normalization"
            logger.error(error_msg)
            return pd.DataFrame(), {"error": error_msg}
        
        # Validate trade_date dtype (should already be datetime)
        try:
            _validate_trade_date_dtype(existing_df, "existing_matrix")
        except ValueError as e:
            logger.error(f"Rolling resequence: Existing matrix trade_date validation failed: {e}")
            return pd.DataFrame(), {"error": str(e)}
        resequence_start_dt = pd.to_datetime(resequence_start_date)
        
        # Preserve historical rows (before resequence_start_date)
        historical_df = existing_df[existing_df['trade_date'] < resequence_start_dt].copy()
        rows_preserved = len(historical_df)
        rows_removed = rows_before_resequence - rows_preserved
        
        logger.info(f"Preserved {rows_preserved} historical rows (removed {rows_removed} rows from window)")
        
        # Step 5: Restore sequencer state from checkpoint immediately before resequence_start_date
        checkpoint_mgr = CheckpointManager()
        
        # Find checkpoint before resequence_start_date
        # Load latest checkpoint and check if it's before resequence_start_date
        latest_checkpoint = checkpoint_mgr.load_latest_checkpoint()
        
        if not latest_checkpoint:
            error_msg = (
                "No checkpoint found. Rolling resequence requires a checkpoint to restore sequencer state. "
                "RECOVERY: Run a full rebuild first using build_master_matrix() to create the initial checkpoint. "
                "Checkpoints are created automatically after successful builds."
            )
            logger.error(error_msg)
            return pd.DataFrame(), {"error": error_msg}
        
        checkpoint_date_str = latest_checkpoint.get('checkpoint_date')
        if checkpoint_date_str:
            checkpoint_date = pd.to_datetime(checkpoint_date_str)
            if checkpoint_date >= resequence_start_dt:
                # Need to find an earlier checkpoint
                # For now, use the latest checkpoint and log a warning
                logger.warning(
                    f"Latest checkpoint ({checkpoint_date_str}) is after resequence_start_date ({resequence_start_date}). "
                    f"Using latest checkpoint state."
                )
        
        checkpoint_restore_id = latest_checkpoint.get('checkpoint_id')
        restored_states = latest_checkpoint.get('streams', {})
        
        logger.info(f"Restored sequencer state from checkpoint {checkpoint_restore_id}")
        
        # Step 6: Run sequencer forward using analyzer data for dates >= resequence_start_date
        logger.info(f"Running sequencer forward for dates >= {resequence_start_date}...")
        
        # Filter analyzer data to resequence window
        window_analyzer_data = all_analyzer_data[all_analyzer_data['trade_date'] >= resequence_start_dt].copy()
        
        if window_analyzer_data.empty:
            logger.warning("No analyzer data found for resequence window")
            # Still save the preserved historical matrix
            if not historical_df.empty:
                save_master_matrix(historical_df, output_dir, None, stream_filters=master_matrix_instance.stream_filters)
            return historical_df, {
                "message": "No analyzer data for resequence window",
                "rows_preserved": rows_preserved,
                "rows_resequenced": 0
            }
        
        # Create sequencer callback with restored state
        apply_sequencer = master_matrix_instance._create_sequencer_callback_with_restored_state(restored_states)
        
        # Apply sequencer logic to window data
        # Note: sequencer needs full history for state, so we need to load all data but filter output
        # Actually, we need to load all data up to latest_analyzer_date for sequencer context
        # But only output rows for the resequence window
        all_data_for_sequencer = master_matrix_instance._load_all_streams_with_sequencer_for_state()
        
        # Filter to dates >= resequence_start_date for sequencer processing
        # DATE OWNERSHIP: DataLoader should have normalized trade_date
        if 'trade_date' not in all_data_for_sequencer.columns:
            error_msg = "No trade_date column in sequencer data - DataLoader should have normalized dates"
            logger.error(error_msg)
            return pd.DataFrame(), {"error": error_msg}
        
        # Validate trade_date dtype (should already be datetime from DataLoader)
        try:
            _validate_trade_date_dtype(all_data_for_sequencer, "sequencer_data")
        except ValueError as e:
            logger.error(f"Rolling resequence: Sequencer data trade_date validation failed: {e}")
            return pd.DataFrame(), {"error": str(e)}
        window_data_for_sequencer = all_data_for_sequencer[all_data_for_sequencer['trade_date'] >= resequence_start_dt].copy()
        
        if window_data_for_sequencer.empty:
            logger.warning("No data available for sequencer processing")
            if not historical_df.empty:
                save_master_matrix(historical_df, output_dir, None, stream_filters=master_matrix_instance.stream_filters)
            return historical_df, {
                "message": "No data for sequencer processing",
                "rows_preserved": rows_preserved,
                "rows_resequenced": 0
            }
        
        # Apply sequencer logic with restored state
        # Sequencer returns tuple (result_df, final_states)
        sequencer_output = apply_sequencer(window_data_for_sequencer, display_year=None)
        if isinstance(sequencer_output, tuple):
            sequencer_result, _ = sequencer_output
        else:
            sequencer_result = sequencer_output
        
        if sequencer_result.empty:
            logger.warning("Sequencer returned no results")
            if not historical_df.empty:
                save_master_matrix(historical_df, output_dir, None, stream_filters=master_matrix_instance.stream_filters)
            return historical_df, {
                "message": "Sequencer returned no results",
                "rows_preserved": rows_preserved,
                "rows_resequenced": 0
            }
        
        # Normalize schema and add global columns to resequenced data
        resequenced_df = master_matrix_instance.normalize_schema(sequencer_result)
        
        # CONTRACT: ProfitDollars must exist before filter_engine
        # ProfitDollars is required by filter_engine for stream health gate calculations
        # DataLoader should have already created it, but sequencer output might not preserve it
        # Ensure it exists as defensive check (using statistics module helper - not synthesizing here)
        if 'ProfitDollars' not in resequenced_df.columns:
            from modules.matrix.statistics import _ensure_profit_dollars_column_inplace
            _ensure_profit_dollars_column_inplace(resequenced_df, contract_multiplier=1.0)
            logger.debug("ProfitDollars column created for resequenced data (required by filter_engine)")
        else:
            logger.debug("ProfitDollars column exists in resequenced data")
        
        resequenced_df = master_matrix_instance.add_global_columns(resequenced_df)
        
        rows_resequenced = len(resequenced_df)
        logger.info(f"Resequenced {rows_resequenced} rows")
        
        # Step 7: Append newly sequenced rows to preserved historical matrix rows
        if historical_df.empty:
            final_df = resequenced_df
        else:
            # INVARIANT: trade_date is canonical datetime column, Date is legacy-derived only
            # Ensure we're working with copies, not views
            historical_df = historical_df.copy()
            resequenced_df = resequenced_df.copy()
            
            # Pre-concat enforcement: enforce invariants on both DataFrames
            # trade_date canonical, Date derived for legacy compatibility only
            _enforce_trade_date_invariants(historical_df, "rolling_resequence_pre_concat_historical")
            _enforce_trade_date_invariants(resequenced_df, "rolling_resequence_pre_concat_resequenced")
            
            # Ensure both DataFrames have same columns
            all_columns = set(historical_df.columns) | set(resequenced_df.columns)
            for col in all_columns:
                if col not in historical_df.columns:
                    historical_df[col] = None
                if col not in resequenced_df.columns:
                    resequenced_df[col] = None
            
            # Append resequenced rows to historical rows
            final_df = pd.concat([historical_df, resequenced_df], ignore_index=True)
            
            # Post-concat enforcement: enforce invariants on final DataFrame
            # trade_date canonical, Date derived for legacy compatibility only
            _enforce_trade_date_invariants(final_df, "rolling_resequence_post_concat")
        
        # Invariants already enforced above via _enforce_trade_date_invariants
        # No need for additional validation here
        
        # Fill None values in string columns before sorting to avoid comparison errors
        # entry_time: use sentinel '23:59:59' so None sorts after valid times
        if 'entry_time' in final_df.columns and final_df['entry_time'].dtype == 'object':
            final_df['entry_time'] = final_df['entry_time'].fillna('23:59:59')
            final_df.loc[final_df['entry_time'] == '', 'entry_time'] = '23:59:59'
        
        # Instrument and Stream: use empty string for None
        for col in ['Instrument', 'Stream']:
            if col in final_df.columns and final_df[col].dtype == 'object':
                final_df[col] = final_df[col].fillna('')
        
        # Sort by trade_date, entry_time, Instrument, Stream
        final_df = final_df.sort_values(
            by=['trade_date', 'entry_time', 'Instrument', 'Stream'],
            ascending=[True, True, True, True],
            na_position='last'
        ).reset_index(drop=True)
        
        # Update global_trade_id
        final_df['global_trade_id'] = range(1, len(final_df) + 1)
        
        # Step 8: Save single new master matrix file
        save_master_matrix(final_df, output_dir, None, stream_filters=master_matrix_instance.stream_filters)
        
        total_rows = len(final_df)
        duration = time.time() - start_time
        
        logger.info("=" * 80)
        logger.info(f"ROLLING RESEQUENCE COMPLETE: {total_rows} total rows ({rows_preserved} preserved + {rows_resequenced} resequenced)")
        logger.info(f"Duration: {duration:.2f}s")
        logger.info("=" * 80)
        
        return final_df, {
            "message": "Rolling resequence complete",
            "rows_preserved": rows_preserved,
            "rows_resequenced": rows_resequenced,
            "total_rows": total_rows,
            "resequence_start_date": resequence_start_date,
            "latest_analyzer_date": latest_analyzer_date_str,
            "duration_seconds": duration
        }
        
    except Exception as e:
        logger.error(f"Error in rolling resequence: {e}", exc_info=True)
        return pd.DataFrame(), {"error": str(e)}
