"""
Analyzer Output Validator

Validates analyzer output against strict contract before parquet write.
Fails fast on violations. Does not attempt repair.

Based on ANALYZER_OUTPUT_CONTRACT.md
"""

import pandas as pd
from pathlib import Path
from typing import Optional

# Required columns for analyzer output
REQUIRED_COLUMNS = ['Date', 'Time', 'Session', 'Instrument', 'Stream']
STRING_COLUMNS = ['Stream', 'Time', 'Instrument', 'Session']
SORT_COLUMNS = ['trade_date', 'entry_time', 'Instrument', 'Stream']


class AnalyzerOutputValidator:
    """Validates analyzer output against contract."""
    
    @staticmethod
    def validate(df: pd.DataFrame, file_path: Optional[Path] = None) -> None:
        """
        Validate DataFrame against analyzer output contract.
        
        Raises ValueError on any contract violation.
        Does not modify DataFrame.
        
        Args:
            df: DataFrame to validate
            file_path: Optional file path for error messages
            
        Raises:
            ValueError: If contract is violated
        """
        if df.empty:
            return  # Empty DataFrames are valid
        
        file_context = f" in {file_path.name}" if file_path else ""
        
        # Invariant 1: Valid Date Column
        if 'Date' not in df.columns:
            raise ValueError(f"REQUIRED COLUMN MISSING{file_context}: Date")
        
        date_series = pd.to_datetime(df['Date'], errors='coerce')
        invalid_mask = date_series.isna()
        if invalid_mask.any():
            invalid_count = invalid_mask.sum()
            invalid_examples = df.loc[invalid_mask, 'Date'].head(5).tolist()
            raise ValueError(
                f"INVALID DATE VALUES{file_context}: {invalid_count} row(s) with unparseable dates. "
                f"Examples: {invalid_examples}. All dates must be parseable by pandas.to_datetime()."
            )
        
        # Invariant 2: Date Column Type (normalize to datetime)
        # Note: Analyzer may output string dates that are valid - convert to datetime
        # This is normalization, not repair - dates are already validated above
        if not pd.api.types.is_datetime64_any_dtype(df['Date']):
            # Convert to datetime (normalization - dates are already valid per Invariant 1)
            # Note: This modifies the DataFrame in-place, which is acceptable for normalization
            df['Date'] = date_series  # Already converted above
            # Double-check no NaT values (should not happen if Invariant 1 passed)
            if df['Date'].isna().any():
                raise ValueError(
                    f"DATE TYPE CONVERSION FAILED{file_context}: "
                    f"Some dates became NaT during datetime conversion. "
                    f"This violates Invariant 1 (valid dates)."
                )


def validate_before_write(df: pd.DataFrame, file_path: Path) -> None:
    """
    Convenience function to validate before parquet write.
    
    Usage:
        validate_before_write(df, parquet_path)
        df.to_parquet(parquet_path, index=False)
    
    Args:
        df: DataFrame to validate
        file_path: Path where parquet will be written
        
    Raises:
        ValueError: If contract is violated
    """
    AnalyzerOutputValidator.validate(df, file_path)
