"""
Strict Quant-Grade Schema Enforcement
Defines and enforces the exact output schema for exported data
"""

from typing import List, Tuple, Optional
import pandas as pd

# Strict schema definition - exact column order
SCHEMA_COLUMNS = [
    "timestamp",
    "open",
    "high",
    "low",
    "close",
    "volume",
    "instrument",
    "source",
    "interval",
    "synthetic"
]

# Required column types
SCHEMA_TYPES = {
    "timestamp": "datetime64[ns]",
    "open": "float64",
    "high": "float64",
    "low": "float64",
    "close": "float64",
    "volume": "float64",
    "instrument": "string",
    "source": "string",
    "interval": "string",
    "synthetic": "bool"
}


class SchemaValidationError(Exception):
    """Raised when data does not conform to the strict schema"""
    pass


def validate_schema(df: pd.DataFrame) -> Tuple[bool, List[str]]:
    """
    Validate that DataFrame conforms to strict schema requirements.
    
    Args:
        df: DataFrame to validate
        
    Returns:
        Tuple of (is_valid: bool, errors: List[str])
    """
    errors = []
    
    if df.empty:
        errors.append("DataFrame is empty")
        return False, errors
    
    # Check required columns exist
    missing_cols = set(SCHEMA_COLUMNS) - set(df.columns)
    if missing_cols:
        errors.append(f"Missing required columns: {sorted(missing_cols)}")
    
    # Check for extra columns
    extra_cols = set(df.columns) - set(SCHEMA_COLUMNS)
    if extra_cols:
        errors.append(f"Extraneous columns found: {sorted(extra_cols)}")
    
    # Check column order
    if list(df.columns) != SCHEMA_COLUMNS:
        errors.append(f"Column order incorrect. Expected: {SCHEMA_COLUMNS}, Got: {list(df.columns)}")
    
    # Validate timestamp column - should be timezone-aware (Chicago) for DataExport files
    if "timestamp" in df.columns:
        if df["timestamp"].dtype == "object":
            # Check if it's string format
            if not all(isinstance(ts, str) for ts in df["timestamp"].head(10)):
                errors.append("timestamp column must be string (ISO8601) format")
        else:
            # Verify it's a datetime type
            if not pd.api.types.is_datetime64_any_dtype(df["timestamp"]):
                errors.append("timestamp must be datetime type")
            # Note: Timezone-aware timestamps (Chicago) are expected after UTC->Chicago conversion
    
    # Validate numeric columns
    for col in ["open", "high", "low", "close", "volume"]:
        if col in df.columns:
            if not pd.api.types.is_numeric_dtype(df[col]):
                errors.append(f"{col} must be numeric, got {df[col].dtype}")
    
    # Validate instrument column
    if "instrument" in df.columns:
        if df["instrument"].isna().any():
            errors.append("instrument column contains null values")
        if (df["instrument"] == "").any():
            errors.append("instrument column contains empty strings")
        if not df["instrument"].dtype == "string":
            # Convert to string if not already
            pass  # Will be handled in enforcement
    
    # Validate source column
    if "source" in df.columns:
        if df["source"].isna().any():
            errors.append("source column contains null values")
    
    # Validate interval column
    if "interval" in df.columns:
        if not (df["interval"] == "1min").all():
            errors.append(f"interval must always be '1min', found: {df['interval'].unique()}")
    
    # Validate synthetic column
    if "synthetic" in df.columns:
        if not pd.api.types.is_bool_dtype(df["synthetic"]):
            errors.append(f"synthetic must be boolean, got {df['synthetic'].dtype}")
        if df["synthetic"].isna().any():
            errors.append("synthetic column contains null values")
    
    return len(errors) == 0, errors


def enforce_schema(
    df: pd.DataFrame,
    source: str = "translator",
    instrument: Optional[str] = None
) -> pd.DataFrame:
    """
    Enforce strict schema on DataFrame - removes non-schema fields, adds missing fields,
    enforces types, and reorders columns.
    
    Args:
        df: DataFrame to enforce schema on
        source: Source pipeline identifier string
        instrument: Explicit instrument symbol (required if not in dataframe)
        
    Returns:
        DataFrame with strict schema enforced
        
    Raises:
        SchemaValidationError: If schema cannot be enforced or data is invalid
    """
    df = df.copy()
    
    # Ensure instrument is explicitly set
    if instrument is None:
        if "instrument" not in df.columns or df["instrument"].isna().any() or (df["instrument"] == "").any():
            raise SchemaValidationError("instrument must be explicitly provided and non-empty")
    else:
        df["instrument"] = instrument
    
    # Ensure instrument is string type
    df["instrument"] = df["instrument"].astype("string")
    
    # Keep timestamp exactly as-is - no timezone conversion
    if "timestamp" in df.columns:
        if df["timestamp"].dtype == "object":
            # Convert string to datetime if needed, but preserve timezone
            df["timestamp"] = pd.to_datetime(df["timestamp"], utc=False)
        # If already datetime, keep it as-is (no conversion)
    
    # Ensure numeric columns are float64
    for col in ["open", "high", "low", "close", "volume"]:
        if col in df.columns:
            df[col] = pd.to_numeric(df[col], errors="coerce").astype("float64")
    
    # Add source column if missing
    if "source" not in df.columns:
        df["source"] = source
    else:
        df["source"] = df["source"].astype("string")
    
    # Add interval column if missing (always "1min")
    if "interval" not in df.columns:
        df["interval"] = "1min"
    else:
        df["interval"] = "1min"  # Override to ensure it's always "1min"
        df["interval"] = df["interval"].astype("string")
    
    # Add synthetic column if missing (default False)
    if "synthetic" not in df.columns:
        df["synthetic"] = False
    else:
        df["synthetic"] = df["synthetic"].astype("bool")
        # Fill any nulls with False
        df["synthetic"] = df["synthetic"].fillna(False)
    
    # Remove all non-schema columns
    schema_cols = [col for col in SCHEMA_COLUMNS if col in df.columns]
    df = df[schema_cols]
    
    # Ensure exact column order
    df = df[SCHEMA_COLUMNS]
    
    # Ensure deterministic row order (sorted by timestamp)
    df = df.sort_values("timestamp").reset_index(drop=True)
    
    # Final validation
    is_valid, errors = validate_schema(df)
    if not is_valid:
        raise SchemaValidationError(f"Schema validation failed after enforcement: {', '.join(errors)}")
    
    return df


def prepare_for_export(
    df: pd.DataFrame,
    source: str = "translator",
    instrument: Optional[str] = None,
    validate: bool = True
) -> pd.DataFrame:
    """
    Prepare DataFrame for export by enforcing schema and validating.
    
    Args:
        df: DataFrame to prepare
        source: Source pipeline identifier
        instrument: Explicit instrument symbol
        validate: If True, validate before returning
        
    Returns:
        DataFrame ready for export with strict schema
        
    Raises:
        SchemaValidationError: If validation fails
    """
    # Enforce schema
    df_export = enforce_schema(df, source=source, instrument=instrument)
    
    # Validate before export
    if validate:
        is_valid, errors = validate_schema(df_export)
        if not is_valid:
            raise SchemaValidationError(f"Validation failed: {', '.join(errors)}")
    
    return df_export

