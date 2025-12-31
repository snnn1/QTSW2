"""
Validation Logic Module
Handles data validation and input verification
"""

import pandas as pd
from typing import List, Dict, Optional, Tuple
from dataclasses import dataclass

@dataclass
class ValidationResult:
    """Result of validation operation"""
    is_valid: bool
    errors: List[str]
    warnings: List[str]

class ValidationManager:
    """Handles data validation and input verification"""
    
    def __init__(self, filter_invalid_rows: bool = False, deduplicate_timestamps: bool = False):
        """
        Initialize validation manager
        
        Args:
            filter_invalid_rows: If True, filter out invalid OHLC rows instead of just reporting errors
            deduplicate_timestamps: If True, deduplicate timestamps (keep first) instead of just warning
        """
        self.required_columns = {"timestamp", "open", "high", "low", "close", "instrument"}
        self.valid_instruments = {"ES", "NQ", "YM", "CL", "NG", "GC", "RTY", "MES", "MNQ", "MYM", "MCL", "MNG", "MGC", "MINUTEDATAEXPORT"}
        self.valid_sessions = {"S1", "S2"}
        self.filter_invalid_rows = filter_invalid_rows
        self.deduplicate_timestamps = deduplicate_timestamps
    
    def validate_dataframe(self, df: pd.DataFrame) -> ValidationResult:
        """
        Validate DataFrame structure and content
        
        Args:
            df: DataFrame to validate
            
        Returns:
            ValidationResult with validation status and messages
        """
        errors = []
        warnings = []
        
        # Check if DataFrame is empty
        if df.empty:
            errors.append("DataFrame is empty")
            return ValidationResult(False, errors, warnings)
        
        # Check required columns
        missing_columns = self.required_columns - set(df.columns)
        if missing_columns:
            errors.append(f"Missing required columns: {sorted(missing_columns)}")
        
        # Check data types
        if 'timestamp' in df.columns:
            if not pd.api.types.is_datetime64_any_dtype(df['timestamp']):
                errors.append("timestamp column must be datetime type")
            elif df['timestamp'].dt.tz is None:
                warnings.append("timestamp column should be timezone-aware (data from NinjaTrader export is already correct)")
        
        # Check for required numeric columns
        numeric_columns = ['open', 'high', 'low', 'close']
        for col in numeric_columns:
            if col in df.columns:
                if not pd.api.types.is_numeric_dtype(df[col]):
                    errors.append(f"{col} column must be numeric")
        
        # Check for negative prices
        if all(col in df.columns for col in ['open', 'high', 'low', 'close']):
            negative_prices = df[
                (df['open'] < 0) |
                (df['high'] < 0) |
                (df['low'] < 0) |
                (df['close'] < 0)
            ]
            if not negative_prices.empty:
                errors.append(f"Found {len(negative_prices)} rows with negative prices")
        
        # Check OHLC relationships
        if all(col in df.columns for col in ['high', 'low', 'open', 'close']):
            invalid_ohlc = df[
                (df['high'] < df['low']) |
                (df['high'] < df['open']) |
                (df['high'] < df['close']) |
                (df['low'] > df['open']) |
                (df['low'] > df['close'])
            ]
            if not invalid_ohlc.empty:
                if self.filter_invalid_rows:
                    warnings.append(f"Found {len(invalid_ohlc)} rows with invalid OHLC relationships (will be filtered if get_cleaned_dataframe() is used)")
                else:
                    errors.append(f"Found {len(invalid_ohlc)} rows with invalid OHLC relationships")
        
        # Check instrument values (case-insensitive comparison, strip whitespace)
        if 'instrument' in df.columns:
            df_instruments_upper = {str(inst).strip().upper() if isinstance(inst, str) else str(inst).strip().upper() for inst in df['instrument'].unique()}
            valid_instruments_upper = {inst.upper() for inst in self.valid_instruments}
            invalid_instruments = df_instruments_upper - valid_instruments_upper
            if invalid_instruments:
                errors.append(f"Invalid instruments found: {sorted(invalid_instruments)}")
        
        # Check for duplicate timestamps
        if 'timestamp' in df.columns:
            duplicates = df['timestamp'].duplicated().sum()
            if duplicates > 0:
                if self.deduplicate_timestamps:
                    warnings.append(f"Found {duplicates} duplicate timestamps (will be deduplicated if get_cleaned_dataframe() is used)")
                else:
                    warnings.append(f"Found {duplicates} duplicate timestamps")
        
        # Check for timestamp gaps (large gaps may indicate missing data)
        if 'timestamp' in df.columns and len(df) > 1:
            df_sorted = df.sort_values('timestamp')
            time_diffs = df_sorted['timestamp'].diff()
            if pd.api.types.is_datetime64_any_dtype(time_diffs):
                # Expected interval is 1 minute for minute data
                expected_interval = pd.Timedelta(minutes=1)
                large_gaps = time_diffs[time_diffs > expected_interval * 10]  # Gaps > 10 minutes
                if len(large_gaps) > 0:
                    warnings.append(f"Found {len(large_gaps)} timestamp gaps > 10 minutes (may indicate missing data)")
        
        # Check for missing values
        missing_values = df.isnull().sum()
        if missing_values.any():
            warnings.append(f"Found missing values in columns: {missing_values[missing_values > 0].to_dict()}")
        
        is_valid = len(errors) == 0
        return ValidationResult(is_valid, errors, warnings)
    
    def get_cleaned_dataframe(self, df: pd.DataFrame) -> pd.DataFrame:
        """
        Get cleaned DataFrame with invalid rows filtered and duplicates removed
        
        Args:
            df: Original DataFrame
            
        Returns:
            Cleaned DataFrame
        """
        # Create a copy to avoid modifying original
        cleaned_df = df.copy()
        
        # Filter invalid OHLC rows if enabled
        if self.filter_invalid_rows and all(col in cleaned_df.columns for col in ['high', 'low', 'open', 'close']):
            invalid_ohlc = cleaned_df[
                (cleaned_df['high'] < cleaned_df['low']) |
                (cleaned_df['high'] < cleaned_df['open']) |
                (cleaned_df['high'] < cleaned_df['close']) |
                (cleaned_df['low'] > cleaned_df['open']) |
                (cleaned_df['low'] > cleaned_df['close'])
            ]
            cleaned_df = cleaned_df[~cleaned_df.index.isin(invalid_ohlc.index)]
        
        # Deduplicate timestamps if enabled
        if self.deduplicate_timestamps and 'timestamp' in cleaned_df.columns:
            cleaned_df = cleaned_df.drop_duplicates(subset=['timestamp'], keep='first')
        
        return cleaned_df
    
    def validate_run_params(self, params) -> ValidationResult:
        """
        Validate run parameters
        
        Args:
            params: RunParams object to validate
            
        Returns:
            ValidationResult with validation status and messages
        """
        errors = []
        warnings = []
        
        # Check instrument (case-insensitive comparison)
        if not hasattr(params, 'instrument'):
            errors.append("Missing instrument parameter")
        else:
            instrument_upper = params.instrument.upper() if isinstance(params.instrument, str) else str(params.instrument).upper()
            valid_instruments_upper = {inst.upper() for inst in self.valid_instruments}
            if instrument_upper not in valid_instruments_upper:
                errors.append(f"Invalid instrument: {params.instrument}")
        
        # Check sessions
        if hasattr(params, 'enabled_sessions'):
            invalid_sessions = set(params.enabled_sessions) - self.valid_sessions
            if invalid_sessions:
                errors.append(f"Invalid sessions: {sorted(invalid_sessions)}")
        
        # Check trade days
        if hasattr(params, 'trade_days'):
            if any(day < 0 or day > 4 for day in params.trade_days):
                errors.append("Trade days must be between 0 (Monday) and 4 (Friday)")
        
        is_valid = len(errors) == 0
        return ValidationResult(is_valid, errors, warnings)
    
    def validate_entry_conditions(self, entry_price: float, direction: str, 
                                breakout_level: float, freeze_close: float) -> ValidationResult:
        """
        Validate trade entry conditions
        
        Args:
            entry_price: Proposed entry price
            direction: Trade direction
            breakout_level: Breakout level
            freeze_close: Freeze close price
            
        Returns:
            ValidationResult with validation status and messages
        """
        errors = []
        warnings = []
        
        # Check if entry price is reasonable
        if entry_price <= 0:
            errors.append("Entry price must be positive")
        
        # Check if breakout level is reasonable
        if breakout_level <= 0:
            errors.append("Breakout level must be positive")
        
        # Check entry logic consistency
        if direction == "Long":
            if entry_price < breakout_level:
                warnings.append("Long entry price below breakout level")
        elif direction == "Short":
            if entry_price > breakout_level:
                warnings.append("Short entry price above breakout level")
        else:
            errors.append(f"Invalid direction: {direction}")
        
        is_valid = len(errors) == 0
        return ValidationResult(is_valid, errors, warnings)
    
    def validate_range_data(self, range_high: float, range_low: float, 
                           range_size: float) -> ValidationResult:
        """
        Validate range calculation results
        
        Args:
            range_high: Range high price
            range_low: Range low price
            range_size: Calculated range size
            
        Returns:
            ValidationResult with validation status and messages
        """
        errors = []
        warnings = []
        
        # Check if range high is greater than range low
        if range_high <= range_low:
            errors.append("Range high must be greater than range low")
        
        # Check if range size matches calculation
        calculated_size = range_high - range_low
        if abs(range_size - calculated_size) > 0.001:  # Allow for small floating point differences
            errors.append(f"Range size mismatch: provided {range_size}, calculated {calculated_size}")
        
        # Check if range size is reasonable
        if range_size < 0:
            errors.append("Range size cannot be negative")
        elif range_size == 0:
            warnings.append("Range size is zero (high == low) - may cause issues in calculations")
        elif range_size > 1000:  # Arbitrary large value
            warnings.append("Range size seems unusually large")
        
        is_valid = len(errors) == 0
        return ValidationResult(is_valid, errors, warnings)
    
    def validate_mfe_calculation(self, peak: float, entry_price: float, 
                               direction: str, lowest_price: float = None, 
                               highest_price: float = None) -> ValidationResult:
        """
        Validate MFE calculation results
        
        Args:
            peak: Calculated peak value
            entry_price: Entry price
            direction: Trade direction
            lowest_price: Lowest price reached (optional)
            highest_price: Highest price reached (optional)
            
        Returns:
            ValidationResult with validation status and messages
        """
        errors = []
        warnings = []
        
        # Check if peak is non-negative
        if peak < 0:
            errors.append("Peak value cannot be negative")
        
        # Validate against actual price movements if provided
        if lowest_price is not None and direction == "Short":
            expected_peak = entry_price - lowest_price
            if abs(peak - expected_peak) > 0.001:
                warnings.append(f"Peak mismatch for short: expected {expected_peak:.2f}, got {peak:.2f}")
        
        if highest_price is not None and direction == "Long":
            expected_peak = highest_price - entry_price
            if abs(peak - expected_peak) > 0.001:
                warnings.append(f"Peak mismatch for long: expected {expected_peak:.2f}, got {peak:.2f}")
        
        is_valid = len(errors) == 0
        return ValidationResult(is_valid, errors, warnings)
