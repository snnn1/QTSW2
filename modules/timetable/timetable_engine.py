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
            "NQ1", "NQ2", "NG1", "NG2", "YM1", "YM2"
        ]
        
        # Day-of-month blocked days for "2" streams (from centralized config)
        self.dom_blocked_days = DOM_BLOCKED_DAYS
        
        # SCF threshold (from centralized config, but can be overridden)
        self.scf_threshold = SCF_THRESHOLD
        
        # Available time slots by session (from centralized config - SINGLE SOURCE OF TRUTH)
        self.session_time_slots = SLOT_ENDS
    
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
        
        # Find most recent parquet files
        parquet_files = sorted(stream_dir.rglob("*.parquet"), reverse=True)
        
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
                if 'Date' not in df.columns:
                    logger.warning(f"File {file_path.name} missing 'Date' column, skipping")
                    continue
                if 'Result' not in df.columns:
                    logger.warning(f"File {file_path.name} missing 'Result' column, skipping")
                    continue
                
                df['Date'] = pd.to_datetime(df['Date'], errors='coerce')
                # Skip rows with invalid dates
                valid_dates = df['Date'].notna()
                if not valid_dates.any():
                    logger.warning(f"File {file_path.name} has no valid dates, skipping")
                    continue
                
                df = df[valid_dates].copy()
                all_trades.append(df)
            except Exception as e:
                logger.warning(f"Error loading {file_path}: {e}")
                import traceback
                logger.debug(f"Traceback: {traceback.format_exc()}")
                continue
        
        if not all_trades:
            return {}
        
        # Merge and filter by session
        df = pd.concat(all_trades, ignore_index=True)
        df = df[df['Session'] == session].copy()
        
        if df.empty:
            return {}
        
        # Sort by date
        df = df.sort_values('Date').reset_index(drop=True)
        
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
            # Try alternative patterns
            parquet_files = list(stream_dir.rglob("*.parquet"))
            for pf in parquet_files:
                try:
                    df = pd.read_parquet(pf)
                    if not df.empty:
                        df['Date'] = pd.to_datetime(df['Date'])
                        if (df['Date'].dt.date == trade_date).any():
                            file_path = pf
                            break
                except:
                    continue
        
        if not file_path.exists():
            return None, None
        
        try:
            df = pd.read_parquet(file_path)
            df['Date'] = pd.to_datetime(df['Date'])
            day_data = df[df['Date'].dt.date == trade_date]
            
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
            trade_date: Trading date (YYYY-MM-DD) or None for today
            
        Returns:
            DataFrame with timetable entries
        """
        if trade_date is None:
            trade_date = date.today().isoformat()
        
        trade_date_obj = pd.to_datetime(trade_date).date()
        
        logger.info("=" * 80)
        logger.info(f"GENERATING TIMETABLE FOR {trade_date}")
        logger.info("=" * 80)
        
        timetable_rows = []
        
        for stream_id in self.streams:
            # Extract instrument from stream_id
            instrument = stream_id[:-1]  # ES1 -> ES
            
            # Process both sessions
            for session in ["S1", "S2"]:
                # Select best time based on RS
                selected_time, time_reason = self.select_best_time(stream_id, session)
                
                if selected_time is None:
                    continue
                
                # Get SCF values
                scf_s1, scf_s2 = self.get_scf_values(stream_id, trade_date_obj)
                
                # Check filters
                allowed, filter_reason = self.check_filters(
                    trade_date_obj, stream_id, session, scf_s1, scf_s2
                )
                
                # Combine reasons
                if not allowed:
                    reason = filter_reason
                else:
                    reason = time_reason
                
                timetable_rows.append({
                    'trade_date': trade_date,
                    'symbol': instrument,
                    'stream_id': stream_id,
                    'session': session,
                    'selected_time': selected_time,
                    'reason': reason,
                    'allowed': allowed,
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
        
        trade_date = timetable_df['trade_date'].iloc[0] if not timetable_df.empty else date.today().isoformat()
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
        
        # Get latest date from master matrix if not provided
        if trade_date is None:
            if 'trade_date' in master_matrix_df.columns:
                latest_date = pd.to_datetime(master_matrix_df['trade_date']).max()
                trade_date = latest_date.strftime('%Y-%m-%d')
            elif 'Date' in master_matrix_df.columns:
                latest_date = pd.to_datetime(master_matrix_df['Date']).max()
                trade_date = latest_date.strftime('%Y-%m-%d')
            else:
                logger.error("Master matrix missing date columns, cannot generate execution timetable")
                return
        
        trade_date_obj = pd.to_datetime(trade_date).date()
        
        # Filter to latest date
        if 'trade_date' in master_matrix_df.columns:
            latest_df = master_matrix_df[pd.to_datetime(master_matrix_df['trade_date']).dt.date == trade_date_obj].copy()
        elif 'Date' in master_matrix_df.columns:
            latest_df = master_matrix_df[pd.to_datetime(master_matrix_df['Date']).dt.date == trade_date_obj].copy()
        else:
            latest_df = master_matrix_df.copy()
        
        if latest_df.empty:
            logger.warning(f"No data for date {trade_date}, cannot generate execution timetable")
            return
        
        # Extract day-of-week and day-of-month for filtering
        target_dow = trade_date_obj.weekday()  # 0=Monday, 6=Sunday
        target_dom = trade_date_obj.day
        
        # Build streams array from master matrix data
        # Only include streams that pass filters (matching UI timetable behavior)
        streams = []
        seen_streams = set()
        
        # Day names for DOW filtering (0=Monday, 6=Sunday)
        day_names = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday']
        target_dow_name = day_names[target_dow]
        
        # Check if final_allowed column exists (authoritative filter indicator)
        has_final_allowed = 'final_allowed' in latest_df.columns
        
        for _, row in latest_df.iterrows():
            stream = row.get('Stream', '')
            if not stream or stream in seen_streams:
                continue
            
            # Check final_allowed column first (if exists, this is the authoritative filter)
            # This column indicates which streams pass all filters in the master matrix
            # Only include streams where final_allowed is explicitly True
            if has_final_allowed:
                final_allowed = row.get('final_allowed')
                # Skip if final_allowed is False, None, NaN, or any falsy value
                if final_allowed is False:
                    continue  # Skip filtered-out streams (matching UI timetable behavior)
                if pd.isna(final_allowed):
                    continue  # Skip NaN values
                if final_allowed is not True:
                    continue  # Only include if explicitly True
            
            # Extract instrument and session from stream
            instrument = stream[:-1] if len(stream) > 1 else ''
            session = 'S1' if stream.endswith('1') else 'S2'
            
            # Get time (use Time Change if available, otherwise Time)
            time = row.get('Time', '')
            time_change = row.get('Time Change', '')
            if time_change and '->' in str(time_change):
                parts = str(time_change).split('->')
                if len(parts) == 2:
                    time = parts[1].strip()
            
            if not time:
                continue
            
            # Apply stream filters (same logic as UI worker)
            # Default to enabled, but check filters
            enabled = True
            
            if stream_filters:
                stream_filter = stream_filters.get(stream, {})
                
                # Check stream-specific DOW filter
                if stream_filter.get('exclude_days_of_week'):
                    excluded_dow = stream_filter['exclude_days_of_week']
                    if any(d == target_dow_name or d == str(target_dow) for d in excluded_dow):
                        enabled = False
                        continue  # Skip this stream entirely
                
                # Check stream-specific DOM filter
                if enabled and stream_filter.get('exclude_days_of_month'):
                    excluded_dom = [int(d) for d in stream_filter['exclude_days_of_month']]
                    if target_dom in excluded_dom:
                        enabled = False
                        continue  # Skip this stream entirely
                
                # Check master filter
                master_filter = stream_filters.get('master', {})
                if enabled and master_filter.get('exclude_days_of_week'):
                    excluded_dow = master_filter['exclude_days_of_week']
                    if any(d == target_dow_name or d == str(target_dow) for d in excluded_dow):
                        enabled = False
                        continue  # Skip this stream entirely
                
                if enabled and master_filter.get('exclude_days_of_month'):
                    excluded_dom = [int(d) for d in master_filter['exclude_days_of_month']]
                    if target_dom in excluded_dom:
                        enabled = False
                        continue  # Skip this stream entirely
            
            # Only include enabled streams (matching UI timetable)
            if enabled:
                streams.append({
                    'stream': stream,
                    'instrument': instrument,
                    'session': session,
                    'slot_time': time,
                    'enabled': True
                })
                seen_streams.add(stream)
        
        # Write execution timetable file
        self._write_execution_timetable_file(streams, trade_date)
    
    def _write_execution_timetable_file(self, streams: List[Dict], trade_date: str) -> None:
        """
        Internal method to write execution timetable file.
        
        Args:
            streams: List of stream dicts with stream, instrument, session, slot_time, enabled
            trade_date: Trading date (YYYY-MM-DD)
        """
        output_dir = Path("data/timetable")
        output_dir.mkdir(parents=True, exist_ok=True)
        
        # Clean up old files (keep only timetable_current.json)
        self._cleanup_old_timetable_files(output_dir)
        
        # Get current timestamp in America/Chicago timezone
        chicago_tz = pytz.timezone("America/Chicago")
        as_of = datetime.now(chicago_tz).isoformat()
        
        # Build execution timetable document
        execution_timetable = {
            'as_of': as_of,
            'trading_date': trade_date,
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
        
        # Build streams array - only include enabled streams from the timetable
        # Each stream_id maps to one session: ES1->S1, ES2->S2, etc.
        streams = []
        
        # Create a lookup dict from timetable_df: stream_id -> (session, slot_time, enabled)
        enabled_streams = {}
        for _, row in timetable_df.iterrows():
            stream_id = row['stream_id']
            session = row['session']
            # Only store if this stream_id matches its natural session
            # ES1 should only have S1 entries, ES2 should only have S2 entries
            expected_session = "S1" if stream_id.endswith("1") else "S2"
            if session == expected_session:
                enabled_streams[stream_id] = {
                    'session': session,
                    'slot_time': row['selected_time'],
                    'enabled': row['allowed']
                }
        
        # Only include streams that are enabled (enabled=true)
        for stream_id, stream_data in enabled_streams.items():
            if stream_data['enabled']:
                instrument = stream_id[:-1]  # ES1 -> ES
                streams.append({
                    'stream': stream_id,
                    'instrument': instrument,
                    'session': stream_data['session'],
                    'slot_time': stream_data['slot_time'],
                    'enabled': True
                })
        
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



