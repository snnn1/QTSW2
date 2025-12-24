"""
QTSW2 Translator — Core

Responsibilities:
- Translate ONE raw exporter CSV into ONE canonical Parquet file
- Convert UTC timestamps to America/Chicago
- Enforce strict quant-grade schema
- Write deterministic daily output

Non-responsibilities:
- No merging
- No rollover
- No frequency detection
- No batching
- No UI logic

Folder Structure Contract:
- Raw CSV input: raw/{instrument}/1m/YYYY/MM/{instrument}_1m_{YYYY-MM-DD}.csv
- Translated output: {translated_root}/{instrument}/1m/YYYY/MM/{instrument}_1m_{YYYY-MM-DD}.parquet

Filename Format:
- Input: {instrument}_1m_{YYYY-MM-DD}.csv
- Output: {instrument}_1m_{YYYY-MM-DD}.parquet

The instrument and date are extracted from the filename (not path traversal),
ensuring robustness against folder structure changes.

Date Filtering:
- Trading date is derived EXCLUSIVELY from filename (authoritative source)
- Timestamps that don't match the filename date after UTC->Chicago conversion are filtered out
- This handles cases where UTC timestamps span midnight and convert to different dates
- Warnings are logged when filtering occurs (expected in trading data)

Empty File Contract:
- If a raw CSV exists but contains no data rows (header only), the Translator SHALL write
  an empty Parquet file for that day.
- Empty trading days (holidays, partial sessions, data outages) are valid and expected.
- Empty Parquet files maintain the correct schema structure with zero rows.
- This ensures day-based processing logic (e.g., Analyzer) can rely on file existence
  as an indicator of processed days, regardless of whether data exists for that day.
"""

from pathlib import Path
from datetime import date as Date
import pandas as pd
import logging

from .schema import (
    convert_timestamp_utc_to_chicago,
    enforce_schema,
    SchemaValidationError,
)

logger = logging.getLogger(__name__)

# ======================================================================
# FILE-LEVEL TRANSLATOR (primitive)
# ======================================================================

def translate_file(
    raw_csv_path: Path,
    translated_root: Path,
) -> Path:
    """
    Translate a single raw exporter CSV into canonical Parquet.
    
    Contract:
    - Trading date is derived EXCLUSIVELY from filename, not data contents
    - Timestamps (after UTC->Chicago conversion) that don't match the filename date are filtered out
    - Only data belonging to the trading day specified in the filename is kept
    - This ensures determinism and prevents data from being written to wrong folders
    - Warnings are logged when timestamps are filtered (common in trading data due to timezone boundaries)
    """

    raw_csv_path = Path(raw_csv_path)
    translated_root = Path(translated_root)

    if not raw_csv_path.exists():
        raise ValueError(f"Raw CSV does not exist: {raw_csv_path}")

    # Extract instrument from filename (more robust than path traversal)
    # Filename format: {instrument}_1m_{YYYY-MM-DD}.csv
    instrument = _infer_instrument_from_filename(raw_csv_path)
    
    # Validate expected path structure: raw/{instrument}/1m/YYYY/MM/file.csv
    _validate_raw_path_structure(raw_csv_path, instrument)

    # ALWAYS derive date from filename (authoritative source)
    # Never infer from data rows (UTC->Chicago conversion can change dates)
    trade_date = _infer_date_from_filename(raw_csv_path)

    # Read CSV file with error handling
    try:
        df_raw = pd.read_csv(raw_csv_path)
    except Exception as e:
        raise IOError(
            f"Failed to read CSV file {raw_csv_path}: {e}. "
            f"This may indicate corrupted file, encoding issue, or file lock."
        ) from e

    # Validate CSV has expected columns
    if "timestamp_utc" not in df_raw.columns:
        raise ValueError(
            f"CSV file {raw_csv_path} missing required column 'timestamp_utc'. "
            f"Found columns: {list(df_raw.columns)}. "
            f"Expected columns: timestamp_utc, open, high, low, close, volume"
        )

    df = convert_timestamp_utc_to_chicago(df_raw)

    df = enforce_schema(
        df,
        instrument=instrument,
        source="exporter",
        drop_invalid_rows=False,
    )

    # Filter timestamps to match the filename date (trading day)
    # UTC timestamps can span midnight and convert to different dates after UTC->Chicago conversion
    # We keep only data that belongs to the trading day specified in the filename
    if not df.empty:
        timestamp_dates = df["timestamp"].dt.date
        mismatched = timestamp_dates != trade_date
        if mismatched.any():
            mismatched_count = mismatched.sum()
            total_count = len(df)
            mismatched_dates = timestamp_dates[mismatched].unique()
            
            # Filter out mismatched rows (keep only data for the correct trading day)
            df = df[~mismatched].copy()
            
            # Log warning for observability (but don't fail - this is expected in trading data)
            logger.warning(
                f"Date filter: Filename indicates {trade_date}, but {mismatched_count}/{total_count} "
                f"timestamp(s) after UTC->Chicago conversion had date(s) {sorted(mismatched_dates)}. "
                f"Filtered out {mismatched_count} row(s) to keep only data for trading day {trade_date}."
            )

    yyyy = f"{trade_date.year:04d}"
    mm = f"{trade_date.month:02d}"
    date_str = trade_date.isoformat()

    out_dir = translated_root / instrument / "1m" / yyyy / mm
    
    # Create output directory with error handling
    try:
        out_dir.mkdir(parents=True, exist_ok=True)
    except Exception as e:
        raise IOError(
            f"Failed to create output directory {out_dir}: {e}. "
            f"This may indicate permission error or disk full."
        ) from e

    out_path = out_dir / f"{instrument}_1m_{date_str}.parquet"

    # Write Parquet file with explicit error handling
    # Translator SHALL fail loudly on write failure, never silently skip
    # Empty DataFrames are intentionally written (see Empty File Contract in module docstring)
    try:
        df.to_parquet(out_path, index=False)
    except Exception as e:
        raise IOError(
            f"Failed to write Parquet file {out_path}: {e}. "
            f"This may indicate disk full, permission error, or file lock."
        ) from e

    return out_path


# ======================================================================
# DOMAIN-LEVEL TRANSLATOR (CLI ENTRY POINT)
# ======================================================================

def translate_day(
    instrument: str,
    day: Date,
    raw_root: Path,
    output_root: Path,
    overwrite: bool = False,
) -> bool:
    """
    Translate exactly ONE instrument-day if raw data exists.

    This is the ONLY function the CLI should call.
    """

    instrument = instrument.upper()
    raw_root = Path(raw_root)
    output_root = Path(output_root)

    yyyy = f"{day.year:04d}"
    mm = f"{day.month:02d}"
    date_str = day.isoformat()

    raw_csv = (
        raw_root
        / instrument
        / "1m"
        / yyyy
        / mm
        / f"{instrument}_1m_{date_str}.csv"
    )

    if not raw_csv.exists():
        return False

    out_dir = output_root / instrument / "1m" / yyyy / mm
    out_file = out_dir / f"{instrument}_1m_{date_str}.parquet"

    if out_file.exists() and not overwrite:
        return True

    translate_file(
        raw_csv_path=raw_csv,
        translated_root=output_root,
    )

    return True


# ======================================================================
# INTERNAL HELPERS
# ======================================================================

def _infer_instrument_from_filename(path: Path) -> str:
    """
    Extract instrument symbol from filename.
    
    Filename format: {instrument}_1m_{YYYY-MM-DD}.csv
    Returns: Instrument symbol (e.g., "ES", "NQ", "CL")
    
    This is more robust than path traversal and works regardless of folder depth.
    """
    stem = path.stem
    parts = stem.split("_")
    if len(parts) < 3:
        raise ValueError(
            f"Cannot infer instrument from filename: {path.name}. "
            f"Expected format: {{instrument}}_1m_{{YYYY-MM-DD}}.csv"
        )
    
    instrument = parts[0].upper()
    if not instrument:
        raise ValueError(f"Empty instrument in filename: {path.name}")
    
    return instrument


def _validate_raw_path_structure(path: Path, expected_instrument: str) -> None:
    """
    Validate that the raw CSV path follows the expected structure.
    
    Expected: raw/{instrument}/1m/YYYY/MM/{instrument}_1m_{date}.csv
    
    This provides clear error messages if the folder structure changes.
    """
    parts = path.parts
    
    # Find the raw root (wherever "raw" folder is)
    try:
        raw_idx = next(i for i, part in enumerate(parts) if part.lower() == "raw")
    except StopIteration:
        raise ValueError(
            f"Path does not contain 'raw' folder: {path}. "
            f"Expected structure: .../raw/{{instrument}}/1m/YYYY/MM/file.csv"
        )
    
    # Validate structure after 'raw' folder
    # raw/{instrument}/1m/YYYY/MM/file.csv
    expected_parts_after_raw = 4  # instrument, 1m, YYYY, MM
    if len(parts) < raw_idx + 1 + expected_parts_after_raw + 1:  # +1 for filename
        raise ValueError(
            f"Path too short after 'raw' folder: {path}. "
            f"Expected: .../raw/{{instrument}}/1m/YYYY/MM/file.csv"
        )
    
    instrument_in_path = parts[raw_idx + 1]
    interval_in_path = parts[raw_idx + 2]
    year_in_path = parts[raw_idx + 3]
    month_in_path = parts[raw_idx + 4]
    
    # Validate instrument matches
    if instrument_in_path.upper() != expected_instrument.upper():
        raise ValueError(
            f"Instrument mismatch: Path has '{instrument_in_path}' but filename indicates '{expected_instrument}'. "
            f"Path: {path}"
        )
    
    # Validate interval
    if interval_in_path != "1m":
        raise ValueError(
            f"Unexpected interval in path: '{interval_in_path}' (expected '1m'). "
            f"Path: {path}"
        )
    
    # Validate year format (4 digits)
    if not (year_in_path.isdigit() and len(year_in_path) == 4):
        raise ValueError(
            f"Invalid year format in path: '{year_in_path}' (expected YYYY). "
            f"Path: {path}"
        )
    
    # Validate month format (2 digits, 01-12)
    if not (month_in_path.isdigit() and len(month_in_path) == 2):
        raise ValueError(
            f"Invalid month format in path: '{month_in_path}' (expected MM). "
            f"Path: {path}"
        )
    
    month_num = int(month_in_path)
    if month_num < 1 or month_num > 12:
        raise ValueError(
            f"Invalid month number in path: '{month_in_path}' (expected 01-12). "
            f"Path: {path}"
        )


def _infer_date_from_filename(path: Path) -> Date:
    """
    Extract trading date from filename.
    
    Filename format: {instrument}_1m_{YYYY-MM-DD}.csv
    Returns: Date object from filename (authoritative source)
    
    This is the ONLY source of truth for trading date.
    Never derive date from data timestamps (UTC->Chicago conversion can change dates).
    """
    stem = path.stem
    parts = stem.split("_")
    if len(parts) < 3:
        raise ValueError(
            f"Cannot infer date from filename: {path.name}. "
            f"Expected format: {{instrument}}_1m_{{YYYY-MM-DD}}.csv"
        )

    date_str = parts[-1]
    try:
        return pd.to_datetime(date_str).date()
    except Exception as e:
        raise ValueError(
            f"Cannot parse date from filename: {path.name}. "
            f"Expected format: {{instrument}}_1m_{{YYYY-MM-DD}}.csv. "
            f"Date part '{date_str}' is invalid: {e}"
        ) from e
