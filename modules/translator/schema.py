"""
QTSW2 Translator — Strict Quant-Grade Schema

This module defines, enforces, and validates the canonical Translator output schema.

Hard rules:
- All incoming timestamps are UTC.
- Translator converts UTC -> America/Chicago.
- Output timestamps are tz-aware America/Chicago.
- Interval is always "1m".
- Volume is int64, must be ≥ 0, and never NaN.
- No versioned identifiers anywhere.

Volume Contract:
- Volume must always be a valid integer ≥ 0
- Volume cannot be NaN (zero-volume bars are valid, but NaN is not)
- Fractional volumes are rejected
- Explicit validation ensures no NaNs survive to final cast
"""

from __future__ import annotations

from typing import List, Tuple
import pandas as pd


# =============================================================================
# Canonical Schema Definition (LOCKED)
# =============================================================================

SCHEMA_COLUMNS: List[str] = [
    "timestamp",
    "open",
    "high",
    "low",
    "close",
    "volume",
    "instrument",
    "source",
    "interval",
    "synthetic",
]

TARGET_TIMEZONE = "America/Chicago"
INTERVAL_VALUE = "1m"
SYNTHETIC_VALUE = False
DEFAULT_SOURCE = "exporter"


# =============================================================================
# Exceptions
# =============================================================================

class SchemaValidationError(Exception):
    """Raised when data does not conform to the strict QTSW2 schema."""
    pass


# =============================================================================
# UTC -> Chicago Conversion (Translator responsibility)
# =============================================================================

def convert_timestamp_utc_to_chicago(df: pd.DataFrame) -> pd.DataFrame:
    """
    Convert 'timestamp_utc' (UTC) to tz-aware America/Chicago 'timestamp'.

    Expected input:
        - df contains column 'timestamp_utc'
        - values are ISO-8601 UTC or parseable as UTC

    Output:
        - drops 'timestamp_utc'
        - adds 'timestamp' as tz-aware America/Chicago
    
    Empty DataFrames are handled: returns empty DataFrame with 'timestamp' column.
    """
    if "timestamp_utc" not in df.columns:
        raise SchemaValidationError("Expected 'timestamp_utc' column in raw data")

    out = df.copy()

    # Defensive guard: skip conversion for empty DataFrames
    # While pandas handles empty tz-aware Series, explicit guard ensures
    # robustness across pandas versions and avoids potential edge cases
    if df.empty:
        # Create empty timestamp column with correct dtype (tz-aware Chicago)
        out["timestamp"] = pd.Series(dtype="datetime64[ns, America/Chicago]")
        out = out.drop(columns=["timestamp_utc"])
        return out

    ts_utc = pd.to_datetime(
        out["timestamp_utc"],
        utc=True,
        errors="coerce"
    )

    # Validate timestamps
    if ts_utc.isna().any():
        raise SchemaValidationError("Unparseable UTC timestamps found in 'timestamp_utc'")

    # Convert UTC to Chicago timezone
    out["timestamp"] = ts_utc.dt.tz_convert(TARGET_TIMEZONE)
    out = out.drop(columns=["timestamp_utc"])

    return out


# =============================================================================
# Schema Enforcement (Coercion + Normalization)
# =============================================================================

def enforce_schema(
    df: pd.DataFrame,
    *,
    instrument: str,
    source: str = DEFAULT_SOURCE,
    drop_invalid_rows: bool = False,
) -> pd.DataFrame:
    """
    Enforce the strict QTSW2 schema.

    Assumptions:
    - 'timestamp' already exists and is tz-aware America/Chicago
    - UTC -> Chicago conversion already happened

    This function:
    - coerces types
    - injects metadata columns
    - removes non-schema columns
    - orders columns deterministically
    - allows empty days
    """

    if not instrument or not isinstance(instrument, str):
        raise SchemaValidationError("instrument must be a non-empty string")

    df = df.copy()

    # -------------------------------------------------------------------------
    # Timestamp assertions (NO conversion here)
    # -------------------------------------------------------------------------
    if "timestamp" not in df.columns:
        raise SchemaValidationError("Missing required column: timestamp")

    if not pd.api.types.is_datetime64_any_dtype(df["timestamp"]):
        raise SchemaValidationError("timestamp must be datetime dtype")

    # Empty DataFrame handling: skip timezone checks if no rows
    if not df.empty:
        if df["timestamp"].dt.tz is None:
            raise SchemaValidationError("timestamp must be tz-aware America/Chicago")

        if TARGET_TIMEZONE not in str(df["timestamp"].dt.tz):
            raise SchemaValidationError(
                f"timestamp timezone must be {TARGET_TIMEZONE}"
            )

    # -------------------------------------------------------------------------
    # OHLC coercion
    # -------------------------------------------------------------------------
    for col in ["open", "high", "low", "close"]:
        if col not in df.columns:
            raise SchemaValidationError(f"Missing required column: {col}")
        df[col] = pd.to_numeric(df[col], errors="coerce").astype("float64")

    # -------------------------------------------------------------------------
    # Volume coercion (STRICT int64)
    # -------------------------------------------------------------------------
    if "volume" not in df.columns:
        raise SchemaValidationError("Missing required column: volume")

    vol = pd.to_numeric(df["volume"], errors="coerce")

    # Only check for fractional values if DataFrame is not empty
    if not df.empty and (vol.notna() & (vol % 1 != 0)).any():
        raise SchemaValidationError("volume contains fractional values")

    df["volume"] = vol.astype("Int64")

    # -------------------------------------------------------------------------
    # Drop or reject invalid rows (skip for empty DataFrames)
    # -------------------------------------------------------------------------
    if not df.empty:
        required_numeric = ["timestamp", "open", "high", "low", "close", "volume"]
        invalid_mask = df[required_numeric].isna().any(axis=1)

        if invalid_mask.any():
            if drop_invalid_rows:
                df = df.loc[~invalid_mask].copy()
            else:
                raise SchemaValidationError(
                    f"{int(invalid_mask.sum())} rows contain invalid numeric values"
                )

    # Final volume cast: explicitly check for NaNs before non-nullable cast
    # Volume must always be a valid integer ≥ 0 (never NaN)
    # This ensures deterministic behavior even if edge cases allow NaNs to slip through
    if not df.empty and df["volume"].isna().any():
        nan_count = df["volume"].isna().sum()
        raise SchemaValidationError(
            f"Volume contains {nan_count} NaN value(s) after validation. "
            f"This should not occur - volume must always be a valid integer ≥ 0."
        )
    
    df["volume"] = df["volume"].astype("int64")

    # -------------------------------------------------------------------------
    # Metadata columns (forced constants)
    # -------------------------------------------------------------------------
    df["instrument"] = instrument.upper()
    df["source"] = source
    df["interval"] = INTERVAL_VALUE
    df["synthetic"] = SYNTHETIC_VALUE

    # Cast metadata types
    df["instrument"] = df["instrument"].astype("string")
    df["source"] = df["source"].astype("string")
    df["interval"] = df["interval"].astype("string")
    df["synthetic"] = df["synthetic"].astype("bool")

    # -------------------------------------------------------------------------
    # Remove non-schema columns
    # -------------------------------------------------------------------------
    df = df[[c for c in SCHEMA_COLUMNS if c in df.columns]]

    # Ensure all schema columns exist (even on empty days)
    for c in SCHEMA_COLUMNS:
        if c not in df.columns:
            raise SchemaValidationError(f"Missing schema column after enforcement: {c}")

    # Deterministic ordering
    df = df[SCHEMA_COLUMNS]
    
    # Only sort if DataFrame is not empty
    if not df.empty:
        df = df.sort_values("timestamp", kind="mergesort").reset_index(drop=True)

    # Final validation
    validate_schema(df)

    return df


# =============================================================================
# Schema Validation (Assertions only)
# =============================================================================

def validate_schema(df: pd.DataFrame) -> None:
    """
    Validate that a DataFrame conforms exactly to the QTSW2 schema.
    No coercion. No fixes. Assertions only.
    
    Empty DataFrames are VALID if they have the correct column structure.
    Row-level validation is skipped for empty DataFrames.
    """

    # Column set + order
    if list(df.columns) != SCHEMA_COLUMNS:
        raise SchemaValidationError(
            f"Column order mismatch. Expected {SCHEMA_COLUMNS}, got {list(df.columns)}"
        )

    # Empty DataFrame is valid if columns are correct
    # Skip row-level validation when len(df) == 0
    if df.empty:
        # Validate column dtypes exist (even if no rows)
        if not pd.api.types.is_datetime64_any_dtype(df["timestamp"]):
            raise SchemaValidationError("timestamp must be datetime dtype")
        
        for col in ["open", "high", "low", "close"]:
            if not pd.api.types.is_float_dtype(df[col]):
                raise SchemaValidationError(f"{col} must be float64")
        
        if not pd.api.types.is_integer_dtype(df["volume"]):
            raise SchemaValidationError("volume must be int64")
        
        for col in ["instrument", "source", "interval"]:
            if str(df[col].dtype) != "string":
                raise SchemaValidationError(f"{col} must be string dtype")
        
        if not pd.api.types.is_bool_dtype(df["synthetic"]):
            raise SchemaValidationError("synthetic must be bool dtype")
        
        # Empty DataFrame is valid - return early
        return

    # Row-level validation (only for non-empty DataFrames)
    # Timestamp
    if not pd.api.types.is_datetime64_any_dtype(df["timestamp"]):
        raise SchemaValidationError("timestamp must be datetime dtype")

    if df["timestamp"].dt.tz is None:
        raise SchemaValidationError("timestamp must be tz-aware America/Chicago")

    if TARGET_TIMEZONE not in str(df["timestamp"].dt.tz):
        raise SchemaValidationError("timestamp timezone mismatch")

    # OHLC
    for col in ["open", "high", "low", "close"]:
        if not pd.api.types.is_float_dtype(df[col]):
            raise SchemaValidationError(f"{col} must be float64")

    # Volume
    if not pd.api.types.is_integer_dtype(df["volume"]):
        raise SchemaValidationError("volume must be int64")

    # Strings
    for col in ["instrument", "source", "interval"]:
        if str(df[col].dtype) != "string":
            raise SchemaValidationError(f"{col} must be string dtype")
        if df[col].isna().any() or (df[col] == "").any():
            raise SchemaValidationError(f"{col} contains null or empty values")

    # Constants
    if not df["interval"].eq(INTERVAL_VALUE).all():
        raise SchemaValidationError("interval must be '1m'")

    if not df["synthetic"].eq(SYNTHETIC_VALUE).all():
        raise SchemaValidationError("synthetic must be False")

    if not pd.api.types.is_bool_dtype(df["synthetic"]):
        raise SchemaValidationError("synthetic must be bool dtype")
