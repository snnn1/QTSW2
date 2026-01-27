"""
Timetable Engine - "What trades to take today"

This module generates a timetable showing which trades should be taken today
based on RS/time selection rules and filters.

Author: Quantitative Trading System
Date: 2025
"""

import pandas as pd
import numpy as np
from pathlib import Path
from typing import List, Optional, Dict, Tuple
from datetime import datetime, date
import logging
import sys
import json
import pytz

# Import centralized config
# Handle both direct import and relative import
try:
    from modules.matrix.config import SLOT_ENDS, DOM_BLOCKED_DAYS, SCF_THRESHOLD
except ImportError:
    # Fallback: add parent directory to path
    sys.path.insert(0, str(Path(__file__).parent.parent.parent))
    from modules.matrix.config import SLOT_ENDS, DOM_BLOCKED_DAYS, SCF_THRESHOLD

# CONTRACT: Only import validation helpers, never normalization functions
# Timetable Engine validates, enforces, and fails — it never normalizes dates.
try:
    from modules.matrix.data_loader import (
        _validate_trade_date_dtype,
        _validate_trade_date_presence
    )
except ImportError:
    # Fallback: add parent directory to path
    sys.path.insert(0, str(Path(__file__).parent.parent.parent))
    from modules.matrix.data_loader import (
        _validate_trade_date_dtype,
        _validate_trade_date_presence
    )

# Configure logging
logging.basicConfig(level=logging.INFO, format='%(asctime)s - %(levelname)s - %(message)s')
logger = logging.getLogger(__name__)


class TimetableEngine:
    """
    Generates a timetable showing which trades to take today based on rules and filters.
    """
    
    def __init__(self, master_matrix_dir: str = "data/master_matrix",
                 analyzer_runs_dir: str = "data/analyzed"):
        """
        Initialize Timetable Engine.
        
        Args:
            master_matrix_dir: Directory containing master matrix files
            analyzer_runs_dir: Directory containing analyzer output files (for RS calculation)
        """
        self.master_matrix_dir = Path(master_matrix_dir)
        self.analyzer_runs_dir = Path(analyzer_runs_dir)
        
        # Streams to process
        self.streams = [
            "ES1", "ES2", "GC1", "GC2", "CL1", "CL2",
            "NQ1", "NQ2", "NG1", "NG2", "YM1", "YM2",
            "RTY1", "RTY2"
        ]
        
        # Day-of-month blocked days for "2" streams (from centralized config)
        self.dom_blocked_days = DOM_BLOCKED_DAYS
        
        # SCF threshold (from centralized config, but can be overridden)
        self.scf_threshold = SCF_THRESHOLD
        
        # Available time slots by session (from centralized config - SINGLE SOURCE OF TRUTH)
        self.session_time_slots = SLOT_ENDS
        
        # File list cache to avoid repeated rglob() calls
        self._file_list_cache = {}
    
    def _get_parquet_files(self, stream_dir: Path) -> List[Path]:
        """
        Get parquet files for a stream directory with caching.
        
        Args:
            stream_dir: Stream directory path
            
        Returns:
            Sorted list of parquet files (most recent first)
        """
        if stream_dir not in self._file_list_cache:
            self._file_list_cache[stream_dir] = sorted(
                stream_dir.rglob("*.parquet"), reverse=True
            )
        return self._file_list_cache[stream_dir]
    
    def calculate_rs_for_stream(self, stream_id: str, session: str, 
                               lookback_days: int = 13) -> Dict[str, float]:
        """
        Calculate RS (Rolling Sum) values for each time slot in a stream/session.
        
        This simulates the RS calculation from sequential processor logic:
        - Win = +1 point
        - Loss = -2 points
        - BE = 0 points
        - Rolling sum over last 13 trades
        
        Args:
            stream_id: Stream identifier (e.g., "ES1", "GC2")
            session: Session ("S1" or "S2")
            lookback_days: Number of days to look back (default 13)
            
        Returns:
            Dictionary mapping time slots to RS values
        """
        stream_dir = self.analyzer_runs_dir / stream_id
        
        if not stream_dir.exists():
            return {}
        
        # Find most recent parquet files (using cache)
        parquet_files = self._get_parquet_files(stream_dir)
        
        if not parquet_files:
            return {}
        
        # Load recent data (last N files or last N days)
        all_trades = []
        for file_path in parquet_files[:10]:  # Load last 10 files
            try:
                df = pd.read_parquet(file_path)
                if df.empty:
                    continue
                
                # Validate required columns
                if 'Result' not in df.columns:
                    logger.warning(f"File {file_path.name} missing 'Result' column, skipping")
                    continue
                
                # CONTRACT ENFORCEMENT: Require trade_date column
                if 'trade_date' not in df.columns:
                    raise ValueError(
                        f"File {file_path.name} missing trade_date column - "
                        f"analyzer output contract requires trade_date. "
                        f"Timetable Engine does not normalize dates. "
                        f"Fix analyzer output before proceeding."
                    )
                
                # Validate dtype/presence only (no normalization)
                _validate_trade_date_dtype(df, stream_id)
                _validate_trade_date_presence(df, stream_id)
                
                # CONTRACT ENFORCEMENT: Invalid trade_date values → ValueError
                invalid_dates = df['trade_date'].isna()
                if invalid_dates.any():
                    invalid_count = invalid_dates.sum()
                    raise ValueError(
                        f"File {file_path.name}: Found {invalid_count} rows with invalid trade_date. "
                        f"This violates analyzer output contract. Fix analyzer output before proceeding."
                    )
                
                all_trades.append(df)
            except Exception as e:
                # Log contract violations but continue (don't fail entire timetable generation)
                # This allows timetable to be generated even if some files have issues
                if isinstance(e, ValueError):
                    logger.warning(
                        f"Contract violation in {file_path.name}: {e}. "
                        f"Skipping this file (timetable generation will continue with available data)."
                    )
                    import traceback
                    logger.debug(f"Traceback: {traceback.format_exc()}")
                    continue
                # Log other errors but continue
                logger.warning(f"Error loading {file_path}: {e}")
                import traceback
                logger.debug(f"Traceback: {traceback.format_exc()}")
                continue
        
        # Handle missing data gracefully - return empty dict instead of raising error
        # This allows timetable generation to continue even if some streams have no data
        if not all_trades:
            logger.warning(
                f"Stream {stream_id} session {session}: No valid trade data found for RS calculation. "
                f"Returning empty RS values (will use default time slot)."
            )
            return {}
        
        # Merge and filter by session
        df = pd.concat(all_trades, ignore_index=True)
        df = df[df['Session'] == session].copy()
        
        if df.empty:
            return {}
        
        # Sort by trade_date (already datetime dtype)
        df = df.sort_values('trade_date').reset_index(drop=True)
        
        # Get last N trades per time slot
        time_slot_rs = {}
        
        for time_slot in self.session_time_slots.get(session, []):
            time_trades = df[df['Time'] == time_slot].tail(lookback_days)
            
            if time_trades.empty:
                time_slot_rs[time_slot] = 0.0
                continue
            
            # Calculate RS score for each trade
            scores = []
            for _, trade in time_trades.iterrows():
                result_value = trade.get('Result', '')
                # Handle NaN/None values
                if pd.isna(result_value) or result_value is None:
                    result = ''
                else:
                    result = str(result_value).strip().upper()
                
                if result == 'WIN':
                    scores.append(1)
                elif result == 'LOSS':
                    scores.append(-2)
                else:  # BE, TIME, NO TRADE, etc.
                    scores.append(0)
            
            # Rolling sum (limit to lookback_days)
            if len(scores) > lookback_days:
                scores = scores[-lookback_days:]  # Take last N scores
            rs_value = sum(scores)
            time_slot_rs[time_slot] = float(rs_value)
        
        return time_slot_rs
    
    def select_best_time(self, stream_id: str, session: str) -> Tuple[Optional[str], str]:
        """
        Select the best time slot for a stream/session based on RS values.
        
        Args:
            stream_id: Stream identifier
            session: Session ("S1" or "S2")
            
        Returns:
            Tuple of (selected_time, reason)
        """
        rs_values = self.calculate_rs_for_stream(stream_id, session)
        
        if not rs_values:
            return None, "no_data"
        
        # Find time slot with highest RS
        best_time = max(rs_values.items(), key=lambda x: x[1])
        
        if best_time[1] <= 0:
            # All RS values are 0 or negative, use first available time slot
            available_times = self.session_time_slots.get(session, [])
            if available_times:
                return available_times[0], "default_first_time"
        
        return best_time[0], "RS_best_time"
    
    def check_filters(self, trade_date: date, stream_id: str, session: str,
                     scf_s1: Optional[float] = None,
                     scf_s2: Optional[float] = None) -> Tuple[bool, str]:
        """
        Check if a trade should be allowed based on filters.
        
        Args:
            trade_date: Trading date
            stream_id: Stream identifier
            session: Session ("S1" or "S2")
            scf_s1: SCF value for S1 (if available)
            scf_s2: SCF value for S2 (if available)
            
        Returns:
            Tuple of (allowed, reason)
        """
        day_of_month = trade_date.day
        
        # 1. Day-of-month filter for "2" streams
        is_two_stream = stream_id.endswith('2')
        if is_two_stream and day_of_month in self.dom_blocked_days:
            return False, f"dom_blocked_{day_of_month}"
        
        # 2. SCF filter
        if session == "S1" and scf_s1 is not None:
            if scf_s1 >= self.scf_threshold:
                return False, "scf_blocked"
        
        if session == "S2" and scf_s2 is not None:
            if scf_s2 >= self.scf_threshold:
                return False, "scf_blocked"
        
        # 3. Other filters (Wednesday no-trade, etc.) would go here
        # For now, assuming all days are valid unless filtered above
        
        return True, "allowed"
    
    def get_scf_values(self, stream_id: str, trade_date: date) -> Tuple[Optional[float], Optional[float]]:
        """
        Get SCF values for a stream on a specific date.
        
        Args:
            stream_id: Stream identifier
            trade_date: Trading date
            
        Returns:
            Tuple of (scf_s1, scf_s2) or (None, None) if not found
        """
        stream_dir = self.analyzer_runs_dir / stream_id
        
        if not stream_dir.exists():
            return None, None
        
        # Find parquet files for this date
        year = trade_date.year
        month = trade_date.month
        
        # Try to find file for this month/year
        file_pattern = f"{stream_id}_an_{year}_{month:02d}.parquet"
        file_path = stream_dir / str(year) / file_pattern
        
        if not file_path.exists():
            # Try alternative patterns (using cache)
            parquet_files = self._get_parquet_files(stream_dir)
            for pf in parquet_files:
                try:
                    df = pd.read_parquet(pf)
                    if not df.empty:
                        # CONTRACT ENFORCEMENT: Require trade_date column
                        if 'trade_date' not in df.columns:
                            continue  # Skip files without trade_date
                        _validate_trade_date_dtype(df, stream_id)
                        # Ensure trade_date is datetime dtype before using .dt accessor
                        if not pd.api.types.is_datetime64_any_dtype(df['trade_date']):
                            continue  # Skip files with invalid dtype
                        if (df['trade_date'].dt.date == trade_date).any():
                            file_path = pf
                            break
                except:
                    continue
        
        if not file_path.exists():
            return None, None
        
        try:
            df = pd.read_parquet(file_path)
            
            # CONTRACT ENFORCEMENT: Require trade_date column
            if 'trade_date' not in df.columns:
                raise ValueError(
                    f"File {file_path.name} missing trade_date column - "
                    f"analyzer output contract requires trade_date. "
                    f"Timetable Engine does not normalize dates. "
                    f"Fix analyzer output before proceeding."
                )
            
            # Validate dtype only (no normalization, no re-parsing)
            _validate_trade_date_dtype(df, stream_id)
            # Ensure trade_date is datetime dtype before using .dt accessor
            if not pd.api.types.is_datetime64_any_dtype(df['trade_date']):
                raise ValueError(
                    f"File {file_path.name} trade_date column is not datetime dtype after validation: {df['trade_date'].dtype}. "
                    f"This is a contract violation. Analyzer output must have normalized trade_date column."
                )
            day_data = df[df['trade_date'].dt.date == trade_date]
            
            if day_data.empty:
                return None, None
            
            # Get SCF values (use first row if multiple)
            scf_s1 = day_data['scf_s1'].iloc[0] if 'scf_s1' in day_data.columns else None
            scf_s2 = day_data['scf_s2'].iloc[0] if 'scf_s2' in day_data.columns else None
            
            return scf_s1, scf_s2
        except Exception as e:
            logger.debug(f"Error reading SCF values from {file_path}: {e}")
            return None, None
    
    def generate_timetable(self, trade_date: Optional[str] = None) -> pd.DataFrame:
        """
        Generate timetable for a specific trading day.
        
        Args:
            trade_date: Trading date (YYYY-MM-DD) or None for today (Chicago time)
            
        Returns:
            DataFrame with timetable entries
        """
        if trade_date is None:
            # Use Chicago timezone to get today's date (consistent with robot engine)
            chicago_tz = pytz.timezone("America/Chicago")
            chicago_now = datetime.now(chicago_tz)
            trade_date = chicago_now.date().isoformat()
        
        trade_date_obj = pd.to_datetime(trade_date).date()
        
        logger.info("=" * 80)
        logger.info(f"GENERATING TIMETABLE FOR {trade_date}")
        logger.info("=" * 80)
        
        # OPTIMIZATION: Pre-load all SCF values once (batch loading)
        # SCF values are per-stream, not per-session, so we can load once and reuse
        scf_cache = {}
        for stream_id in self.streams:
            scf_cache[stream_id] = self.get_scf_values(stream_id, trade_date_obj)
        
        timetable_rows = []
        
        for stream_id in self.streams:
            # Extract instrument from stream_id
            instrument = stream_id[:-1]  # ES1 -> ES
            
            # Process both sessions
            for session in ["S1", "S2"]:
                # Get SCF values from cache (pre-loaded above)
                scf_s1, scf_s2 = scf_cache[stream_id]
                
                # Select best time based on RS
                try:
                    selected_time, time_reason = self.select_best_time(stream_id, session)
                except Exception as e:
                    logger.warning(f"Error selecting best time for {stream_id} {session}: {e}")
                    # Fallback to default time on error
                    available_times = self.session_time_slots.get(session, [])
                    selected_time = available_times[0] if available_times else ""
                    time_reason = f"error_fallback_{str(e)[:50]}"
                
                # CRITICAL: If time selection fails, use default time and mark as blocked
                # NEVER skip streams - all streams must be present in timetable
                if selected_time is None:
                    # Use first available time slot as default (sequencer intent)
                    available_times = self.session_time_slots.get(session, [])
                    selected_time = available_times[0] if available_times else ""
                    block_reason = "no_rs_data"
                    allowed = False
                    final_reason = f"{time_reason}_{block_reason}"
                else:
                    block_reason = None
                    # Check filters
                    allowed, filter_reason = self.check_filters(
                        trade_date_obj, stream_id, session, scf_s1, scf_s2
                    )
                    
                    # Combine reasons
                    if not allowed:
                        final_reason = filter_reason
                        block_reason = filter_reason
                    else:
                        final_reason = time_reason
                
                # ALWAYS append - never skip (timetable must contain all streams)
                timetable_rows.append({
                    'trade_date': trade_date,
                    'symbol': instrument,
                    'stream_id': stream_id,
                    'session': session,
                    'selected_time': selected_time,
                    'reason': final_reason,
                    'allowed': allowed,
                    'block_reason': block_reason,
                    'scf_s1': scf_s1,
                    'scf_s2': scf_s2,
                    'day_of_month': trade_date_obj.day,
                    'dow': trade_date_obj.strftime('%a')
                })
        
        timetable_df = pd.DataFrame(timetable_rows)
        
        logger.info(f"Timetable generated: {len(timetable_df)} entries")
        logger.info(f"Allowed trades: {timetable_df['allowed'].sum()} / {len(timetable_df)}")
        
        # Write canonical execution timetable file
        self.write_execution_timetable(timetable_df, trade_date)
        
        return timetable_df
    
    def save_timetable(self, timetable_df: pd.DataFrame, 
                       output_dir: str = "data/timetable") -> Tuple[Path, Path]:
        """
        Save timetable to file.
        
        Args:
            timetable_df: Timetable DataFrame
            output_dir: Output directory
            
        Returns:
            Tuple of (parquet_file, json_file) paths
        """
        output_path = Path(output_dir)
        output_path.mkdir(parents=True, exist_ok=True)
        
        if not timetable_df.empty:
            trade_date = timetable_df['trade_date'].iloc[0]
        else:
            # Use Chicago timezone to get today's date (consistent with robot engine)
            chicago_tz = pytz.timezone("America/Chicago")
            chicago_now = datetime.now(chicago_tz)
            trade_date = chicago_now.date().isoformat()
        timestamp = datetime.now().strftime('%Y%m%d_%H%M%S')
        
        parquet_file = output_path / f"timetable_{trade_date.replace('-', '')}_{timestamp}.parquet"
        json_file = output_path / f"timetable_{trade_date.replace('-', '')}_{timestamp}.json"
        
        # Save as Parquet
        timetable_df.to_parquet(parquet_file, index=False, compression='snappy')
        logger.info(f"Saved: {parquet_file}")
        
        # Save as JSON
        timetable_df.to_json(json_file, orient='records', date_format='iso', indent=2)
        logger.info(f"Saved: {json_file}")
        
        return parquet_file, json_file
    
    def write_execution_timetable_from_master_matrix(self, master_matrix_df: pd.DataFrame, 
                                                      trade_date: Optional[str] = None,
                                                      stream_filters: Optional[Dict] = None) -> None:
        """
        Write execution timetable from master matrix data.
        
        This is the authoritative persistence point - called when master matrix is finalized.
        Generates timetable from latest date in master matrix, applying filters.
        
        Args:
            master_matrix_df: Master matrix DataFrame
            trade_date: Optional trading date (YYYY-MM-DD). If None, uses latest date in matrix.
            stream_filters: Optional stream filters dict (for DOW/DOM filtering)
        """
        if master_matrix_df.empty:
            logger.warning("Master matrix is empty, cannot generate execution timetable")
            return
        
        # CONTRACT ENFORCEMENT: Require trade_date column
        if 'trade_date' not in master_matrix_df.columns:
            raise ValueError(
                "Master matrix missing trade_date column - DataLoader must normalize dates before timetable generation. "
                "This is a contract violation. Timetable Engine does not normalize dates."
            )
        
        # Validate dtype/presence only (no normalization, no re-parsing)
        _validate_trade_date_presence(master_matrix_df, "master_matrix")
        _validate_trade_date_dtype(master_matrix_df, "master_matrix")
        
        # Ensure trade_date is datetime dtype before using .dt accessor
        if not pd.api.types.is_datetime64_any_dtype(master_matrix_df['trade_date']):
            raise ValueError(
                f"Master matrix trade_date column is not datetime dtype after validation: {master_matrix_df['trade_date'].dtype}. "
                f"This is a contract violation. DataLoader must normalize dates before timetable generation."
            )
        
        # Get latest date from master matrix if not provided
        if trade_date is None:
            latest_date = master_matrix_df['trade_date'].max()
            trade_date = latest_date.strftime('%Y-%m-%d')
        
        trade_date_obj = pd.to_datetime(trade_date).date()
        
        # CRITICAL FIX: To determine the time slot for a given date, we need to look at the PREVIOUS day's row.
        # The Time Change column in the previous day's row tells us what time to use today.
        # If Time Change is empty, the time stays the same (use previous day's Time value).
        # This ensures that time slots only change on losses, not on wins.
        from datetime import timedelta
        previous_date_obj = trade_date_obj - timedelta(days=1)
        previous_df = master_matrix_df[master_matrix_df['trade_date'].dt.date == previous_date_obj].copy()
        
        # If no previous day data exists, fall back to using the current date's data
        # (this handles the case where we're generating the first day's timetable)
        if previous_df.empty:
            logger.info(f"No data for previous date {previous_date_obj}, using current date {trade_date_obj} data")
            source_df = master_matrix_df[master_matrix_df['trade_date'].dt.date == trade_date_obj].copy()
            use_previous_day_logic = False
        else:
            source_df = previous_df
            use_previous_day_logic = True
        
        if source_df.empty:
            logger.warning(f"No data for date {trade_date_obj}, cannot generate execution timetable")
            return
        
        # Extract day-of-week and day-of-month for filtering (use target date, not source date)
        target_dow = trade_date_obj.weekday()  # 0=Monday, 6=Sunday
        target_dom = trade_date_obj.day
        
        # Build streams array from master matrix data
        # CRITICAL: Must include ALL streams (complete execution contract)
        # Streams not in master matrix or filtered are included with enabled=false
        streams_dict = {}  # stream_id -> stream_entry
        seen_streams = set()
        
        # Day names for DOW filtering (0=Monday, 6=Sunday)
        day_names = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday']
        target_dow_name = day_names[target_dow]
        
        # Check if final_allowed column exists (authoritative filter indicator)
        # Use current date's data for final_allowed check (filters apply to target date, not source date)
        latest_df = master_matrix_df[master_matrix_df['trade_date'].dt.date == trade_date_obj].copy()
        has_final_allowed = 'final_allowed' in latest_df.columns if not latest_df.empty else False
        
        # First pass: Process streams that exist in master matrix
        # Use previous day's data to determine time slot, but current day's data for filters
        for _, row in source_df.iterrows():
            stream = row.get('Stream', '')
            if not stream or stream in seen_streams:
                continue
            
            seen_streams.add(stream)
            
            # Extract instrument and session from stream
            instrument = stream[:-1] if len(stream) > 1 else ''
            
            # Use Session column from master matrix if available
            if 'Session' in row and pd.notna(row.get('Session')):
                session = str(row['Session']).strip()
                if session not in ['S1', 'S2']:
                    logger.warning(f"Stream {stream}: Invalid session '{session}', using stream_id pattern")
                    session = 'S1' if stream.endswith('1') else 'S2'
            else:
                # Fall back to stream_id pattern
                session = 'S1' if stream.endswith('1') else 'S2'
            
            # CRITICAL FIX: Get time from PREVIOUS day's row
            # The sequencer sets Time Change only when there's a LOSS (decide_time_change returns non-None)
            # Time Change in previous day's row tells us what time to use TODAY
            # If Time Change is empty (WIN/BE/NoTrade), time stays the same (use previous day's Time value)
            # This ensures time slots only change on losses, not on wins
            # Example: Monday WIN at 11:00 → Time Change is empty → Tuesday uses 11:00 (same as Monday)
            # Example: Monday LOSS at 11:00 → Time Change is 09:30 → Tuesday uses 09:30 (changed)
            if use_previous_day_logic:
                # Use previous day's Time Change if it exists (this is the time for today)
                time = row.get('Time', '')
                time_change = row.get('Time Change', '')
                if time_change and str(time_change).strip():
                    time_change_str = str(time_change).strip()
                    if '->' in time_change_str:
                        # Backward compatibility: parse "old -> new" format
                        parts = time_change_str.split('->')
                        if len(parts) == 2:
                            time = parts[1].strip()
                    else:
                        # Current format: Time Change is just the new time
                        time = time_change_str
                # If Time Change is empty, time stays the same (use previous day's Time value)
                # This is already set above, so no change needed
            else:
                # Fallback: No previous day data, use current day's Time
                time = row.get('Time', '')
            
            # Validate that parsed time is in session_time_slots
            if time:
                from modules.matrix.utils import normalize_time
                normalized_time = normalize_time(str(time))
                available_times_normalized = [normalize_time(str(t)) for t in self.session_time_slots.get(session, [])]
                
                if normalized_time not in available_times_normalized:
                    logger.warning(
                        f"Stream {stream}: Parsed time '{time}' (normalized: '{normalized_time}') "
                        f"is not in available times for session {session}. "
                        f"Available: {self.session_time_slots.get(session, [])}"
                    )
                    # Use default time instead
                    available_times = self.session_time_slots.get(session, [])
                    time = available_times[0] if available_times else ""
            
            # If no time, use default for session
            if not time:
                available_times = self.session_time_slots.get(session, [])
                time = available_times[0] if available_times else ""
            
            # Initialize enabled status
            enabled = True
            block_reason = None
            
            # Check final_allowed column first (if exists, this is the authoritative filter)
            # CRITICAL: Use current date's row for final_allowed (filters apply to target date, not source date)
            if has_final_allowed and not latest_df.empty:
                # Find the current date's row for this stream
                current_day_row = latest_df[latest_df['Stream'] == stream]
                if not current_day_row.empty:
                    final_allowed = current_day_row.iloc[0].get('final_allowed')
                    # If final_allowed is False/NaN/None, mark as blocked
                    if final_allowed is not True:
                        enabled = False
                        block_reason = f"master_matrix_filtered_{final_allowed}"
            else:
                # No final_allowed column - check filters manually
                if stream_filters:
                    stream_filter = stream_filters.get(stream, {})
                    
                    # Check stream-specific DOW filter
                    if stream_filter.get('exclude_days_of_week'):
                        excluded_dow = stream_filter['exclude_days_of_week']
                        if any(d == target_dow_name or d == str(target_dow) for d in excluded_dow):
                            enabled = False
                            block_reason = f"dow_filter_{target_dow_name.lower()}"
                    
                    # Check stream-specific DOM filter
                    if enabled and stream_filter.get('exclude_days_of_month'):
                        excluded_dom = [int(d) for d in stream_filter['exclude_days_of_month']]
                        if target_dom in excluded_dom:
                            enabled = False
                            block_reason = f"dom_filter_{target_dom}"
                    
                    # Check master filter
                    master_filter = stream_filters.get('master', {})
                    if enabled and master_filter.get('exclude_days_of_week'):
                        excluded_dow = master_filter['exclude_days_of_week']
                        if any(d == target_dow_name or d == str(target_dow) for d in excluded_dow):
                            enabled = False
                            block_reason = f"master_dow_filter_{target_dow_name.lower()}"
                    
                    if enabled and master_filter.get('exclude_days_of_month'):
                        excluded_dom = [int(d) for d in master_filter['exclude_days_of_month']]
                        if target_dom in excluded_dom:
                            enabled = False
                            block_reason = f"master_dom_filter_{target_dom}"
            
            # CRITICAL: Check if the selected time is in exclude_times filter
            # If the time is filtered, block the stream
            if enabled and stream_filters and time:
                try:
                    from modules.matrix.utils import normalize_time
                    normalized_time = normalize_time(str(time))
                    
                    # Check stream-specific exclude_times
                    stream_filter = stream_filters.get(stream, {})
                    if stream_filter.get('exclude_times'):
                        exclude_times_normalized = [normalize_time(str(t)) for t in stream_filter['exclude_times']]
                        if normalized_time in exclude_times_normalized:
                            enabled = False
                            block_reason = f"time_filter({','.join(stream_filter['exclude_times'])})"
                    
                    # Check master exclude_times if stream filter didn't block it
                    if enabled:
                        master_filter = stream_filters.get('master', {})
                        if master_filter.get('exclude_times'):
                            exclude_times_normalized = [normalize_time(str(t)) for t in master_filter['exclude_times']]
                            if normalized_time in exclude_times_normalized:
                                enabled = False
                                block_reason = f"master_time_filter({','.join(master_filter['exclude_times'])})"
                except Exception as e:
                    # If normalization fails, log warning but don't fail
                    logger.warning(f"Failed to check exclude_times for stream {stream} time {time}: {e}")
            
            # Always include stream (enabled or blocked)
            stream_entry = {
                'stream': stream,
                'instrument': instrument,
                'session': session,
                'slot_time': time,
                'decision_time': time,  # Sequencer intent (same as slot_time)
                'enabled': enabled
            }
            if block_reason:
                stream_entry['block_reason'] = block_reason
            streams_dict[stream] = stream_entry
        
        # Second pass: Ensure ALL 14 streams are present (add missing ones as blocked)
        for stream_id in self.streams:
            if stream_id not in streams_dict:
                # Stream not in master matrix - add as blocked
                instrument = stream_id[:-1] if len(stream_id) > 1 else ''
                session = 'S1' if stream_id.endswith('1') else 'S2'
                available_times = self.session_time_slots.get(session, [])
                default_time = ""
                
                # CONTRACT ENFORCEMENT: Select first non-filtered time
                if stream_filters:
                    from modules.matrix.utils import normalize_time
                    exclude_times_list = []
                    
                    # Collect exclude_times from stream and master filters
                    stream_filter = stream_filters.get(stream_id, {})
                    if stream_filter.get('exclude_times'):
                        exclude_times_list.extend(stream_filter['exclude_times'])
                    
                    master_filter = stream_filters.get('master', {})
                    if master_filter.get('exclude_times'):
                        exclude_times_list.extend(master_filter['exclude_times'])
                    
                    # Select first NON-FILTERED time
                    if exclude_times_list:
                        exclude_times_normalized = [normalize_time(str(t)) for t in exclude_times_list]
                        for time_slot in available_times:
                            if normalize_time(time_slot) not in exclude_times_normalized:
                                default_time = time_slot
                                break
                        
                        # CONTRACT ENFORCEMENT: All times filtered → ValueError
                        if not default_time:
                            raise ValueError(
                                f"Stream {stream_id} (session {session}): All available times are filtered. "
                                f"Available times: {available_times}, Filtered times: {exclude_times_list}. "
                                f"This is a configuration error - cannot select default time for missing stream."
                            )
                    else:
                        default_time = available_times[0] if available_times else ""
                else:
                    default_time = available_times[0] if available_times else ""
                
                # CONTRACT ENFORCEMENT: No available time slots → ValueError
                if not default_time:
                    raise ValueError(
                        f"Stream {stream_id} (session {session}): No available time slots. "
                        f"This is a configuration error."
                    )
                
                streams_dict[stream_id] = {
                    'stream': stream_id,
                    'instrument': instrument,
                    'session': session,
                    'slot_time': default_time,
                    'decision_time': default_time,  # Sequencer intent
                    'enabled': False,
                    'block_reason': 'not_in_master_matrix'
                }
        
        # Convert dict to list (all 12 streams guaranteed)
        streams = list(streams_dict.values())
        
        # Write execution timetable file
        self._write_execution_timetable_file(streams, trade_date)
    
    def _write_execution_timetable_file(self, streams: List[Dict], trade_date: str) -> None:
        """
        Internal method to write execution timetable file.
        
        Args:
            streams: List of stream dicts with stream, instrument, session, slot_time, enabled, block_reason (optional)
            trade_date: Trading date (YYYY-MM-DD)
        """
        output_dir = Path("data/timetable")
        output_dir.mkdir(parents=True, exist_ok=True)
        
        # Clean up old files (keep only timetable_current.json)
        self._cleanup_old_timetable_files(output_dir)
        
        # Get current timestamp in America/Chicago timezone
        chicago_tz = pytz.timezone("America/Chicago")
        chicago_now = datetime.now(chicago_tz)
        as_of = chicago_now.isoformat()
        
        # Compute trading_date using CME rollover rule (17:00 Chicago)
        # If Chicago time < 17:00: trading_date = Chicago calendar date
        # If Chicago time >= 17:00: trading_date = Chicago calendar date + 1 day
        chicago_date = chicago_now.date()
        chicago_hour = chicago_now.hour
        
        # Rollover at 17:00 Chicago time
        if chicago_hour >= 17:
            # At or after 17:00, trading_date is next calendar day
            from datetime import timedelta
            trading_date = (chicago_date + timedelta(days=1)).isoformat()
            rollover_applied = True
        else:
            # Before 17:00, trading_date is current calendar day
            trading_date = chicago_date.isoformat()
            rollover_applied = False
        
        # Log CME trading date computation for verification
        logger.info(
            f"CME_TRADING_DATE_ROLLOVER: "
            f"Chicago time={chicago_now.strftime('%Y-%m-%d %H:%M:%S %Z')}, "
            f"hour={chicago_hour}, "
            f"calendar_date={chicago_date.isoformat()}, "
            f"computed_trading_date={trading_date}, "
            f"rollover_applied={rollover_applied}"
        )
        
        # CONTRACT ENFORCEMENT: CME rollover logic failure is a runtime contract violation
        if chicago_hour >= 17 and trading_date == chicago_date.isoformat():
            raise ValueError(
                f"CME_TRADING_DATE_ROLLOVER_CONTRACT_VIOLATION: "
                f"as_of={as_of} (>= 17:00) but trading_date={trading_date} "
                f"equals calendar date {chicago_date.isoformat()}. "
                f"This violates the CME rollover contract - rollover logic is broken."
            )
        
        # Optional: Log if trade_date parameter differs from computed trading_date
        if trade_date and trade_date != trading_date:
            logger.info(
                f"CME_TRADING_DATE_COMPUTED: "
                f"trade_date parameter={trade_date}, computed trading_date={trading_date} "
                f"(based on Chicago time {chicago_now.strftime('%Y-%m-%d %H:%M:%S %Z')})"
            )
        
        # Ensure all streams have decision_time (sequencer intent) - same as slot_time
        # This represents what the sequencer would use even if stream is blocked
        for stream in streams:
            if 'decision_time' not in stream:
                stream['decision_time'] = stream.get('slot_time', '')
        
        # Build execution timetable document
        execution_timetable = {
            'as_of': as_of,
            'trading_date': trading_date,  # Use computed value, not trade_date parameter
            'timezone': 'America/Chicago',
            'source': 'master_matrix',
            'streams': streams
        }
        
        # Atomic write: write to temp file, then rename
        temp_file = output_dir / "timetable_current.tmp"
        final_file = output_dir / "timetable_current.json"
        
        try:
            # Write to temporary file
            with open(temp_file, 'w', encoding='utf-8') as f:
                json.dump(execution_timetable, f, indent=2, ensure_ascii=False)
            
            # Atomic rename (works on Windows and Unix)
            temp_file.replace(final_file)
            
            logger.info(f"Execution timetable written: {final_file} ({len(streams)} streams)")
        except Exception as e:
            logger.error(f"Failed to write execution timetable: {e}")
            # Clean up temp file on error
            if temp_file.exists():
                try:
                    temp_file.unlink()
                except:
                    pass
            raise
    
    def write_execution_timetable(self, timetable_df: pd.DataFrame, trade_date: str) -> None:
        """
        Write canonical execution timetable file (timetable_current.json).
        
        This is the single source of truth for NinjaTrader execution.
        Uses atomic writes to prevent partial reads.
        
        Args:
            timetable_df: Timetable DataFrame
            trade_date: Trading date (YYYY-MM-DD)
        """
        output_dir = Path("data/timetable")
        output_dir.mkdir(parents=True, exist_ok=True)
        
        # Clean up old files (keep only timetable_current.json)
        self._cleanup_old_timetable_files(output_dir)
        
        # Build streams array - include ALL streams (enabled and blocked)
        # Each stream_id maps to one session: ES1->S1, ES2->S2, etc.
        # CRITICAL: Timetable must contain complete execution contract - all 14 streams
        streams = []
        
        # Create a lookup dict from timetable_df: stream_id -> (session, slot_time, enabled, block_reason)
        all_streams = {}
        for _, row in timetable_df.iterrows():
            stream_id = row['stream_id']
            session = row['session']
            # Only store if this stream_id matches its natural session
            # ES1 should only have S1 entries, ES2 should only have S2 entries
            expected_session = "S1" if stream_id.endswith("1") else "S2"
            if session == expected_session:
                all_streams[stream_id] = {
                    'session': session,
                    'slot_time': row['selected_time'],
                    'enabled': row['allowed'],
                    'block_reason': row.get('block_reason')  # May be None if enabled
                }
        
        # Include ALL streams - enabled and blocked (complete execution contract)
        for stream_id, stream_data in all_streams.items():
            instrument = stream_id[:-1]  # ES1 -> ES
            stream_entry = {
                'stream': stream_id,
                'instrument': instrument,
                'session': stream_data['session'],
                'slot_time': stream_data['slot_time'],
                'decision_time': stream_data['slot_time'],  # Sequencer intent (same as slot_time)
                'enabled': stream_data['enabled']  # Can be False
            }
            # Add block_reason if stream is blocked
            if stream_data.get('block_reason'):
                stream_entry['block_reason'] = stream_data['block_reason']
            streams.append(stream_entry)
        
        # Write execution timetable file using shared method
        self._write_execution_timetable_file(streams, trade_date)
    
    def _cleanup_old_timetable_files(self, output_dir: Path) -> None:
        """
        Remove all files in timetable directory except timetable_current.json.
        
        Args:
            output_dir: Timetable output directory
        """
        if not output_dir.exists():
            return
        
        current_file = output_dir / "timetable_current.json"
        temp_file = output_dir / "timetable_current.tmp"
        
        removed_count = 0
        for file_path in output_dir.iterdir():
            # Skip the current file and temp file
            if file_path.name == "timetable_current.json":
                continue
            if file_path.name == "timetable_current.tmp":
                continue
            
            try:
                file_path.unlink()
                removed_count += 1
            except Exception as e:
                logger.warning(f"Failed to remove old file {file_path}: {e}")
        
        if removed_count > 0:
            logger.info(f"Cleaned up {removed_count} old timetable files")


def main():
    """Main function for command-line usage"""
    import argparse
    
    parser = argparse.ArgumentParser(description='Generate Timetable for trading day')
    parser.add_argument('--date', type=str, help='Trading date (YYYY-MM-DD) or today if not specified')
    parser.add_argument('--master-matrix-dir', type=str, default='data/master_matrix',
                       help='Directory containing master matrix files')
    parser.add_argument('--analyzer-runs-dir', type=str, default='data/analyzed',
                       help='Directory containing analyzer output files')
    parser.add_argument('--output-dir', type=str, default='data/timetable',
                       help='Output directory for timetable files')
    parser.add_argument('--scf-threshold', type=float, default=0.5,
                       help='SCF threshold for blocking trades')
    
    args = parser.parse_args()
    
    engine = TimetableEngine(
        master_matrix_dir=args.master_matrix_dir,
        analyzer_runs_dir=args.analyzer_runs_dir
    )
    engine.scf_threshold = args.scf_threshold
    
    timetable_df = engine.generate_timetable(trade_date=args.date)
    
    if not timetable_df.empty:
        parquet_file, json_file = engine.save_timetable(timetable_df, args.output_dir)
        
        print("\n" + "=" * 80)
        print("TIMETABLE SUMMARY")
        print("=" * 80)
        print(f"Date: {timetable_df['trade_date'].iloc[0]}")
        print(f"Total entries: {len(timetable_df)}")
        print(f"Allowed trades: {timetable_df['allowed'].sum()}")
        print(f"\nTimetable:")
        print(timetable_df[['symbol', 'stream_id', 'session', 'selected_time', 
                           'allowed', 'reason']].to_string(index=False))
        print(f"\nFiles saved:")
        print(f"  - {parquet_file}")
        print(f"  - {json_file}")


if __name__ == "__main__":
    main()



