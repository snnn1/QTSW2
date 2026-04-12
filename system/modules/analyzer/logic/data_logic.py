"""
Data Management Logic Module
Handles all data loading, cleaning, normalization, and preparation operations

This module consolidates data operations that were previously scattered across:
- run_data_processed.py (data loading)
- engine.py (data filtering, session cutting)
- validation_logic.py (data cleaning)
- range_logic.py (data slicing)
- entry_logic.py (data filtering)
- price_tracking_logic.py (data slicing)
"""

import pandas as pd
import numpy as np
from pathlib import Path
from typing import Optional, List, Tuple, Dict, Union
from dataclasses import dataclass
from datetime import datetime
import pytz

# Chicago timezone constant
CHICAGO_TZ = pytz.timezone("America/Chicago")


@dataclass
class DataLoadResult:
    """Result of data loading operation"""
    success: bool
    df: Optional[pd.DataFrame]
    errors: List[str]
    warnings: List[str]
    metadata: Dict


@dataclass
class DataFilterResult:
    """Result of data filtering operation"""
    df: pd.DataFrame
    rows_removed: int
    filters_applied: List[str]


class DataManager:
    """
    Centralized data management for the analyzer
    
    Handles:
    - Data loading (parquet, CSV)
    - Data cleaning (duplicates, invalid rows, OHLC fixes)
    - Timezone normalization (ensure Chicago timezone)
    - Missing bar reconstruction (optional)
    - Session cutting (filter by S1/S2 sessions)
    - Outlier detection and removal
    - Deterministic indexing (sorting, reset_index)
    """
    
    def __init__(self, auto_fix_ohlc: bool = True, enforce_timezone: bool = True):
        """
        Initialize DataManager
        
        Args:
            auto_fix_ohlc: Automatically fix invalid OHLC relationships
            enforce_timezone: Enforce that timestamps are timezone-aware and in Chicago
                             (Translator layer should handle conversion)
        """
        self.auto_fix_ohlc = auto_fix_ohlc
        self.enforce_timezone = enforce_timezone
        self.required_columns = {"timestamp", "open", "high", "low", "close", "instrument"}
        
    def load_parquet(self, file_path: Union[str, Path]) -> DataLoadResult:
        """
        Load data from parquet file(s)
        
        Args:
            file_path: Path to parquet file or directory containing parquet files
            
        Returns:
            DataLoadResult with loaded DataFrame and metadata
        """
        errors = []
        warnings = []
        metadata = {}
        
        try:
            path = Path(file_path)
            
            if path.is_file():
                # Single file
                files = [path]
            elif path.is_dir():
                # Directory - find all parquet files
                files = sorted(path.glob("*.parquet"))
                if not files:
                    errors.append(f"No parquet files found in {path}")
                    return DataLoadResult(False, None, errors, warnings, metadata)
            else:
                errors.append(f"Path not found: {file_path}")
                return DataLoadResult(False, None, errors, warnings, metadata)
            
            # Load all files
            parts = []
            total_size_mb = 0
            
            for f in files:
                try:
                    file_size_mb = f.stat().st_size / (1024 * 1024)
                    total_size_mb += file_size_mb
                    df_part = pd.read_parquet(f)
                    parts.append(df_part)
                except Exception as e:
                    errors.append(f"Error loading {f.name}: {e}")
                    continue
            
            if not parts:
                errors.append("No files could be loaded successfully")
                return DataLoadResult(False, None, errors, warnings, metadata)
            
            # Concatenate
            if len(parts) == 1:
                df = parts[0]
            else:
                try:
                    df = pd.concat(parts, ignore_index=True)
                except MemoryError as e:
                    errors.append(f"Out of memory while concatenating: {e}")
                    return DataLoadResult(False, None, errors, warnings, metadata)
            
            metadata = {
                "files_loaded": len(parts),
                "total_size_mb": total_size_mb,
                "rows": len(df),
                "columns": list(df.columns)
            }
            
            # Validate required columns
            missing = self.required_columns - set(df.columns)
            if missing:
                errors.append(f"Missing required columns: {sorted(missing)}")
                return DataLoadResult(False, None, errors, warnings, metadata)
            
            # Apply standard cleaning
            df, clean_warnings = self._clean_dataframe(df)
            warnings.extend(clean_warnings)
            
            return DataLoadResult(True, df, errors, warnings, metadata)
            
        except Exception as e:
            errors.append(f"Unexpected error loading data: {e}")
            return DataLoadResult(False, None, errors, warnings, metadata)
    
    def load_csv(self, file_path: Union[str, Path]) -> DataLoadResult:
        """
        Load data from CSV file
        
        Args:
            file_path: Path to CSV file
            
        Returns:
            DataLoadResult with loaded DataFrame and metadata
        """
        errors = []
        warnings = []
        metadata = {}
        
        try:
            path = Path(file_path)
            if not path.exists():
                errors.append(f"File not found: {file_path}")
                return DataLoadResult(False, None, errors, warnings, metadata)
            
            df = pd.read_csv(path)
            
            # Basic validation
            missing = self.required_columns - set(df.columns)
            if missing:
                errors.append(f"Missing required columns: {sorted(missing)}")
                return DataLoadResult(False, None, errors, warnings, metadata)
            
            # Apply standard cleaning
            df, clean_warnings = self._clean_dataframe(df)
            warnings.extend(clean_warnings)
            
            metadata = {
                "file": str(path),
                "rows": len(df),
                "columns": list(df.columns)
            }
            
            return DataLoadResult(True, df, errors, warnings, metadata)
            
        except Exception as e:
            errors.append(f"Error loading CSV: {e}")
            return DataLoadResult(False, None, errors, warnings, metadata)
    
    def _clean_dataframe(self, df: pd.DataFrame) -> Tuple[pd.DataFrame, List[str]]:
        """
        Clean DataFrame: remove duplicates, fix OHLC, normalize timezone
        
        Args:
            df: DataFrame to clean
            
        Returns:
            Tuple of (cleaned_df, warnings)
        """
        warnings = []
        df = df.copy()
        
        # Remove duplicates
        # POLICY: Keep last occurrence (most recent/corrected data)
        initial_count = len(df)
        df = df.drop_duplicates(
            subset=["timestamp", "instrument"],
            keep="last"  # Keep last occurrence - preserves most recent/corrected data
        ).reset_index(drop=True)
        duplicates_removed = initial_count - len(df)
        if duplicates_removed > 0:
            warnings.append(f"Removed {duplicates_removed} duplicate rows")
        
        # ARCHITECTURE: Translator layer handles timezone conversion
        # DataManager only ENFORCES timezone awareness (does not convert)
        # Verify timestamps are timezone-aware and in Chicago timezone
        if self.enforce_timezone:
            tz_errors, tz_warnings = self._enforce_timezone_awareness(df)
            if tz_errors:
                raise ValueError(f"Timezone validation failed: {tz_errors}")
            warnings.extend(tz_warnings)
        
        # Fix OHLC relationships
        if self.auto_fix_ohlc:
            df, ohlc_warnings = self._fix_ohlc_relationships(df)
            warnings.extend(ohlc_warnings)
        
        # Ensure deterministic indexing
        df = self._ensure_deterministic_index(df)
        
        return df, warnings
    
    def _enforce_timezone_awareness(self, df: pd.DataFrame) -> Tuple[List[str], List[str]]:
        """
        ========================================================================
        STRICT TIMEZONE ENFORCEMENT FOR NINJATRADER DATA
        ========================================================================
        
        NinjaTrader 8.1 bar timestamps are ALWAYS in the Trading Hours timezone.
        For all CME futures (CL, GC, ES, NQ, YM, NG), this is America/Chicago.
        This cannot be changed in NinjaTrader - the "General → Time zone" setting
        only affects the UI, NOT bar timestamps.
        
        ARCHITECTURE:
        - Translator layer (translator/file_loader.py) handles timezone conversion:
          * Interprets all naive timestamps as America/Chicago
          * Validates tz-aware timestamps are Chicago (warns if not)
          * Optionally normalizes to UTC or other timezone
        - DataManager ENFORCES strict rules:
          * No naive timestamps allowed
          * All timestamps must be America/Chicago (unless normalized to UTC)
          * No system timezone or OS timezone usage
          * Deterministic conversion path only
        
        RULES ENFORCED:
        1. Timestamps MUST be timezone-aware (no naive datetimes)
        2. Timestamps MUST be in America/Chicago (or normalized target timezone)
        3. No mixed timezones allowed
        4. No system/local timezone allowed
        5. Any non-Chicago tz-aware timestamp indicates upstream error
        
        Args:
            df: DataFrame with timestamp column
            
        Returns:
            Tuple of (errors, warnings)
        """
        errors = []
        warnings = []
        
        if 'timestamp' not in df.columns:
            return errors, warnings
        
        # Ensure timestamp column is datetime type
        if df['timestamp'].dtype == 'object':
            try:
                # NEVER use utc=True or assume UTC - let translator handle it
                df['timestamp'] = pd.to_datetime(df['timestamp'], utc=False)
            except (ValueError, TypeError) as e:
                errors.append(f"Cannot convert timestamp column to datetime: {e}")
                return errors, warnings
        
        # RULE 1: Check if timezone-aware (NO NAIVE TIMESTAMPS ALLOWED)
        if df['timestamp'].dt.tz is None:
            errors.append(
                "CRITICAL: Timestamps must be timezone-aware. "
                "All NinjaTrader timestamps are interpreted as America/Chicago by the Translator. "
                "Naive timestamps indicate a failure in the Translator layer."
            )
            return errors, warnings
        
        # RULE 2: Check if all timestamps are in the expected timezone
        # Translator should have normalized to Chicago (or configured target timezone)
        tz_values = df['timestamp'].dt.tz
        if isinstance(tz_values, pd.Series):
            unique_tzones = tz_values.unique()
            
            # Check for mixed timezones (RULE 3: No mixed timezones)
            if len(unique_tzones) > 1:
                errors.append(
                    f"CRITICAL: Mixed timezones detected: {unique_tzones}. "
                    "All timestamps must be in the same timezone (America/Chicago or normalized target)."
                )
                return errors, warnings
            
            tz = unique_tzones[0]
            
            # RULE 4: No system/local timezone allowed
            if tz == pytz.UTC or str(tz) == 'UTC':
                # UTC is acceptable if explicitly normalized (translator config)
                pass  # Valid normalization target
            elif tz != CHICAGO_TZ:
                # RULE 5: Non-Chicago tz-aware timestamp indicates upstream error
                warnings.append(
                    f"WARNING: Timestamps are in {tz} but expected America/Chicago. "
                    "NinjaTrader cannot produce non-Chicago timestamps - this indicates an upstream error. "
                    "Translator should have converted to Chicago or normalized to configured target."
                )
                # Don't error - allow it if it's a valid normalization target
                # But warn that it's unexpected for NinjaTrader data
        else:
            # Single timezone value
            if tz_values != CHICAGO_TZ and tz_values != pytz.UTC:
                warnings.append(
                    f"WARNING: Timestamps are in {tz_values} but expected America/Chicago. "
                    "This may be a valid normalization target, but is unexpected for raw NinjaTrader data."
                )
        
        # Verify timestamps are monotonic (enforced by DataManager)
        if not df['timestamp'].is_monotonic_increasing:
            warnings.append("Timestamps are not sorted - will be sorted by _ensure_deterministic_index")
        
        return errors, warnings
    
    def _normalize_timezone(self, df: pd.DataFrame) -> Tuple[pd.DataFrame, List[str]]:
        """
        DEPRECATED: This method should not be used.
        Translator layer handles timezone conversion.
        DataManager only enforces timezone awareness via _enforce_timezone_awareness().
        
        This method is kept for backward compatibility but will be removed.
        """
        """
        Normalize timestamps to Chicago timezone
        
        Args:
            df: DataFrame with timestamp column
            
        Returns:
            Tuple of (normalized_df, warnings)
        """
        warnings = []
        df = df.copy()
        
        if 'timestamp' not in df.columns:
            return df, warnings
        
        # Handle object dtype with mixed timezone-aware/naive timestamps
        # Process each timestamp individually to handle mixed timezones
        normalized_timestamps = []
        localized_count = 0
        converted_count = 0
        
        for ts in df['timestamp']:
            # Convert to pd.Timestamp if needed
            if not isinstance(ts, pd.Timestamp):
                if isinstance(ts, datetime):
                    ts = pd.Timestamp(ts)
                else:
                    ts = pd.to_datetime(ts)
            
            # Normalize to Chicago timezone
            if ts.tz is None:
                # Naive timestamp - localize to Chicago
                normalized_timestamps.append(ts.tz_localize(CHICAGO_TZ))
                localized_count += 1
            elif ts.tz != CHICAGO_TZ:
                # Different timezone - convert to Chicago
                normalized_timestamps.append(ts.tz_convert(CHICAGO_TZ))
                converted_count += 1
            else:
                # Already Chicago timezone
                normalized_timestamps.append(ts)
        
        # Create new Series with normalized timestamps
        # All timestamps are now Chicago timezone, so we can convert to datetime64
        df['timestamp'] = pd.Series(normalized_timestamps)
        
        # Try to convert to datetime64[ns, America/Chicago] if all are same timezone
        try:
            # All should be Chicago now, so we can convert
            df['timestamp'] = pd.to_datetime(df['timestamp'], utc=False)
        except (ValueError, TypeError):
            # If conversion fails, keep as object (shouldn't happen, but be safe)
            pass
        
        # Add warnings
        if localized_count > 0:
            warnings.append("Localized timestamps to Chicago timezone (assumed naive timestamps were Chicago)")
        if converted_count > 0:
            warnings.append("Converted timestamps to Chicago timezone")
        
        return df, warnings
    
    def _fix_ohlc_relationships(self, df: pd.DataFrame) -> Tuple[pd.DataFrame, List[str]]:
        """
        Fix invalid OHLC relationships
        
        Args:
            df: DataFrame with OHLC columns
            
        Returns:
            Tuple of (fixed_df, warnings)
        """
        warnings = []
        df = df.copy()
        
        required_cols = ['high', 'low', 'open', 'close']
        if not all(col in df.columns for col in required_cols):
            return df, warnings
        
        # Find invalid rows
        invalid_mask = (
            (df['high'] < df['low']) |
            (df['high'] < df['open']) |
            (df['high'] < df['close']) |
            (df['low'] > df['open']) |
            (df['low'] > df['close'])
        )
        
        invalid_count = invalid_mask.sum()
        if invalid_count == 0:
            return df, warnings
        
        # Fix each invalid row
        for idx in df[invalid_mask].index:
            o = df.loc[idx, 'open']
            h = df.loc[idx, 'high']
            l = df.loc[idx, 'low']
            c = df.loc[idx, 'close']
            
            # If high < low, swap them
            if h < l:
                df.loc[idx, 'high'] = l
                df.loc[idx, 'low'] = h
                h, l = l, h
            
            # Ensure high >= open and high >= close
            if h < o:
                df.loc[idx, 'high'] = o
                h = o
            if h < c:
                df.loc[idx, 'high'] = c
                h = c
            
            # Ensure low <= open and low <= close
            if l > o:
                df.loc[idx, 'low'] = o
                l = o
            if l > c:
                df.loc[idx, 'low'] = c
                l = c
            
            # Ensure open and close are within [low, high]
            df.loc[idx, 'open'] = np.clip(o, l, h)
            df.loc[idx, 'close'] = np.clip(c, l, h)
        
        warnings.append(f"Fixed {invalid_count} rows with invalid OHLC relationships")
        
        return df, warnings
    
    def _ensure_deterministic_index(self, df: pd.DataFrame) -> pd.DataFrame:
        """
        Ensure DataFrame has deterministic indexing (sorted, reset index)
        
        Args:
            df: DataFrame to process
            
        Returns:
            DataFrame with deterministic indexing
        """
        df = df.copy()
        
        # Sort by timestamp and instrument for deterministic order
        if 'timestamp' in df.columns:
            sort_cols = ['timestamp']
            if 'instrument' in df.columns:
                sort_cols.append('instrument')
            df = df.sort_values(sort_cols).reset_index(drop=True)
        
        return df
    
    def filter_by_instrument(self, df: pd.DataFrame, instrument: str) -> DataFilterResult:
        """
        Filter DataFrame by instrument
        
        Args:
            df: DataFrame to filter
            instrument: Instrument symbol (e.g., "ES", "CL")
            
        Returns:
            DataFilterResult with filtered DataFrame
        """
        initial_count = len(df)
        
        if 'instrument' not in df.columns:
            return DataFilterResult(df, 0, [])
        
        df_filtered = df[df['instrument'].str.upper() == instrument.upper()].copy()
        rows_removed = initial_count - len(df_filtered)
        
        return DataFilterResult(
            df_filtered,
            rows_removed,
            [f"instrument == {instrument.upper()}"]
        )
    
    def filter_by_date_range(self, df: pd.DataFrame, 
                            start_date: pd.Timestamp, 
                            end_date: pd.Timestamp) -> DataFilterResult:
        """
        Filter DataFrame by date range
        
        Args:
            df: DataFrame to filter
            start_date: Start timestamp (inclusive)
            end_date: End timestamp (exclusive)
            
        Returns:
            DataFilterResult with filtered DataFrame
        """
        initial_count = len(df)
        
        if 'timestamp' not in df.columns:
            return DataFilterResult(df, 0, [])
        
        # Ensure timezone-aware comparison
        if isinstance(start_date, pd.Timestamp):
            if start_date.tz is None:
                start_date = start_date.tz_localize(CHICAGO_TZ)
        elif isinstance(start_date, datetime):
            if start_date.tzinfo is None:
                start_date = CHICAGO_TZ.localize(start_date)
            start_date = pd.Timestamp(start_date)
        
        if isinstance(end_date, pd.Timestamp):
            if end_date.tz is None:
                end_date = end_date.tz_localize(CHICAGO_TZ)
        elif isinstance(end_date, datetime):
            if end_date.tzinfo is None:
                end_date = CHICAGO_TZ.localize(end_date)
            end_date = pd.Timestamp(end_date)
        
        mask = (df['timestamp'] >= start_date) & (df['timestamp'] < end_date)
        df_filtered = df[mask].copy()
        rows_removed = initial_count - len(df_filtered)
        
        return DataFilterResult(
            df_filtered,
            rows_removed,
            [f"timestamp >= {start_date} AND timestamp < {end_date}"]
        )
    
    def filter_by_session(self, df: pd.DataFrame, 
                         session: str,
                         session_start: pd.Timestamp,
                         session_end: pd.Timestamp) -> DataFilterResult:
        """
        Filter DataFrame by trading session (S1 or S2)
        
        Args:
            df: DataFrame to filter
            session: Session identifier ("S1" or "S2")
            session_start: Session start time (inclusive)
            session_end: Session end time (exclusive)
            
        Returns:
            DataFilterResult with filtered DataFrame
        """
        initial_count = len(df)
        
        if 'timestamp' not in df.columns:
            return DataFilterResult(df, 0, [])
        
        # Ensure timezone-aware comparison
        if isinstance(session_start, pd.Timestamp):
            if session_start.tz is None:
                session_start = session_start.tz_localize(CHICAGO_TZ)
        elif isinstance(session_start, datetime):
            if session_start.tzinfo is None:
                session_start = CHICAGO_TZ.localize(session_start)
            session_start = pd.Timestamp(session_start)
        
        if isinstance(session_end, pd.Timestamp):
            if session_end.tz is None:
                session_end = session_end.tz_localize(CHICAGO_TZ)
        elif isinstance(session_end, datetime):
            if session_end.tzinfo is None:
                session_end = CHICAGO_TZ.localize(session_end)
            session_end = pd.Timestamp(session_end)
        
        mask = (df['timestamp'] >= session_start) & (df['timestamp'] < session_end)
        df_filtered = df[mask].copy()
        rows_removed = initial_count - len(df_filtered)
        
        return DataFilterResult(
            df_filtered,
            rows_removed,
            [f"session {session}: timestamp >= {session_start} AND timestamp < {session_end}"]
        )
    
    def get_data_after_timestamp(self, df: pd.DataFrame, 
                                timestamp: pd.Timestamp,
                                inclusive: bool = True) -> pd.DataFrame:
        """
        Get data after (or at) a specific timestamp
        
        Args:
            df: DataFrame to filter
            timestamp: Cutoff timestamp
            inclusive: If True, include bars at exact timestamp
            
        Returns:
            Filtered DataFrame
        """
        if 'timestamp' not in df.columns:
            return df.copy()
        
        if inclusive:
            mask = df['timestamp'] >= timestamp
        else:
            mask = df['timestamp'] > timestamp
        
        return df[mask].copy()
    
    def get_data_before_timestamp(self, df: pd.DataFrame,
                                 timestamp: pd.Timestamp,
                                 inclusive: bool = True) -> pd.DataFrame:
        """
        Get data before (or at) a specific timestamp
        
        Args:
            df: DataFrame to filter
            timestamp: Cutoff timestamp
            inclusive: If True, include bars at exact timestamp
            
        Returns:
            Filtered DataFrame
        """
        if 'timestamp' not in df.columns:
            return df.copy()
        
        if inclusive:
            mask = df['timestamp'] <= timestamp
        else:
            mask = df['timestamp'] < timestamp
        
        return df[mask].copy()
    
    def detect_outliers(self, df: pd.DataFrame,
                       method: str = "iqr",
                       columns: Optional[List[str]] = None) -> pd.DataFrame:
        """
        Detect outliers in numeric columns
        
        Args:
            df: DataFrame to analyze
            method: Detection method ("iqr" for interquartile range)
            columns: Columns to check (default: OHLC columns)
            
        Returns:
            DataFrame with boolean mask (True = outlier)
        """
        if columns is None:
            columns = ['open', 'high', 'low', 'close']
        
        columns = [c for c in columns if c in df.columns]
        if not columns:
            return pd.DataFrame(index=df.index)
        
        outlier_mask = pd.Series(False, index=df.index)
        
        if method == "iqr":
            for col in columns:
                Q1 = df[col].quantile(0.25)
                Q3 = df[col].quantile(0.75)
                IQR = Q3 - Q1
                lower_bound = Q1 - 3 * IQR  # 3x IQR for extreme outliers
                upper_bound = Q3 + 3 * IQR
                
                col_outliers = (df[col] < lower_bound) | (df[col] > upper_bound)
                outlier_mask |= col_outliers
        
        return pd.DataFrame({'is_outlier': outlier_mask}, index=df.index)
    
    def reconstruct_missing_bars(self, df: pd.DataFrame,
                                expected_frequency: str = "1min",
                                fill_method: str = "forward",
                                flag_synthetic: bool = True) -> pd.DataFrame:
        """
        Reconstruct missing bars by creating regular time intervals
        
        Args:
            df: DataFrame with timestamp column
            expected_frequency: Expected bar frequency ("1min", "5min", etc.)
            fill_method: How to fill missing bars ("forward", "backward", "interpolate")
            
        Returns:
            DataFrame with missing bars reconstructed
        """
        if 'timestamp' not in df.columns or df.empty:
            return df.copy()
        
        # Create complete time range
        start_time = df['timestamp'].min()
        end_time = df['timestamp'].max()
        
        # Parse frequency
        if expected_frequency.endswith('min'):
            minutes = int(expected_frequency[:-3])
            freq = f"{minutes}min"
        else:
            freq = expected_frequency
        
        # Create complete timestamp range
        complete_range = pd.date_range(
            start=start_time,
            end=end_time,
            freq=freq,
            tz=df['timestamp'].dt.tz
        )
        
        # Reindex DataFrame
        df_reindexed = df.set_index('timestamp').reindex(complete_range)
        
        # Flag synthetic bars (bars not in original dataset)
        # POLICY: Explicitly flag synthetic bars for data lineage tracking
        if flag_synthetic:
            original_timestamps = set(df['timestamp'])
            df_reindexed['synthetic'] = ~df_reindexed.index.isin(original_timestamps)
        else:
            df_reindexed['synthetic'] = False
        
        # Fill missing values
        # POLICY: Forward fill (use previous bar's values)
        if fill_method == "forward":
            df_reindexed = df_reindexed.ffill()
        elif fill_method == "backward":
            df_reindexed = df_reindexed.bfill()
        elif fill_method == "interpolate":
            numeric_cols = df_reindexed.select_dtypes(include=[np.number]).columns
            df_reindexed[numeric_cols] = df_reindexed[numeric_cols].interpolate(method='linear')
        
        # Ensure synthetic flag is preserved (not filled by forward fill)
        if flag_synthetic:
            original_timestamps = set(df['timestamp'])
            df_reindexed['synthetic'] = ~df_reindexed.index.isin(original_timestamps)
        
        # Reset index
        df_reindexed = df_reindexed.reset_index()
        df_reindexed = df_reindexed.rename(columns={'index': 'timestamp'})
        
        return df_reindexed
    
    def validate_dataframe(self, df: pd.DataFrame) -> Tuple[bool, List[str], List[str]]:
        """
        Validate DataFrame structure and content
        
        Args:
            df: DataFrame to validate
            
        Returns:
            Tuple of (is_valid, errors, warnings)
        """
        errors = []
        warnings = []
        
        if df.empty:
            errors.append("DataFrame is empty")
            return False, errors, warnings
        
        # Check required columns
        missing = self.required_columns - set(df.columns)
        if missing:
            errors.append(f"Missing required columns: {sorted(missing)}")
        
        # Check data types
        if 'timestamp' in df.columns:
            if not pd.api.types.is_datetime64_any_dtype(df['timestamp']):
                errors.append("timestamp column must be datetime type")
            elif df['timestamp'].dt.tz is None:
                warnings.append("timestamp column should be timezone-aware")
        
        # Check numeric columns
        numeric_columns = ['open', 'high', 'low', 'close']
        for col in numeric_columns:
            if col in df.columns:
                if not pd.api.types.is_numeric_dtype(df[col]):
                    errors.append(f"{col} column must be numeric")
        
        is_valid = len(errors) == 0
        return is_valid, errors, warnings

