"""
Matrix integrity validation.

Validates matrix data on load to prevent silent corruption.
Checks: required columns, unique (stream, trade_date) for final rows, RS/SCF validity.
"""

import logging
from typing import List, Tuple, Optional
import pandas as pd

logger = logging.getLogger(__name__)

REQUIRED_COLUMNS = [
    "Stream",
    "trade_date",
    "Time",
    "Result",
    "Profit",
    "final_allowed",
]

# SCF columns: values should be in [0, 1] or NaN
SCF_COLUMNS = ["scf_s1", "scf_s2"]

# RS column: when present, should be numeric (NaN allowed for warmup rows)
RS_COLUMNS = ["rs_value"]


def validate_matrix_integrity(df: pd.DataFrame) -> Tuple[bool, List[str]]:
    """
    Validate matrix integrity. Returns (ok, list of issue messages).
    Non-blocking: logs issues but returns ok=True unless critical corruption.
    """
    issues: List[str] = []

    if df is None or df.empty:
        return True, []

    # 1. Required columns present
    missing = [c for c in REQUIRED_COLUMNS if c not in df.columns]
    if missing:
        # trade_date might be absent in legacy files; Date is fallback
        if "trade_date" in missing and "Date" in df.columns:
            missing = [c for c in missing if c != "trade_date"]
        if missing:
            issues.append(f"Missing required columns: {missing}")
            logger.warning(f"Matrix integrity: missing columns {missing}")

    # 2. Unique (stream, trade_date) for final rows
    if "Stream" in df.columns and "final_allowed" in df.columns:
        date_col = "trade_date" if "trade_date" in df.columns else "Date"
        if date_col in df.columns:
            final_df = df[df["final_allowed"] == True]
            if not final_df.empty:
                dupes = final_df.duplicated(subset=["Stream", date_col], keep=False)
                if dupes.any():
                    n_dupes = dupes.sum()
                    issues.append(f"Duplicate (stream, trade_date) in final rows: {n_dupes} rows")
                    logger.warning(f"Matrix integrity: {n_dupes} duplicate (stream, trade_date) in final_allowed rows")

    # 3. SCF values valid (0-1 or NaN)
    for col in SCF_COLUMNS:
        if col in df.columns:
            invalid = df[col].dropna()
            if len(invalid) > 0:
                out_of_range = invalid[(invalid < 0) | (invalid > 1)]
                if len(out_of_range) > 0:
                    issues.append(f"Invalid {col}: {len(out_of_range)} values outside [0, 1]")
                    logger.warning(f"Matrix integrity: {col} has {len(out_of_range)} values outside [0, 1]")

    # 4. RS not null where expected (optional - rs_value can be NaN for warmup)
    for col in RS_COLUMNS:
        if col in df.columns:
            # Only check rows that have Result (executed trades)
            executed = df[df["Result"].notna() & (df["Result"].astype(str).str.strip() != "")]
            if len(executed) > 0:
                null_rs = executed[executed[col].isna()]
                # Allow some NaN (e.g. first N rows per stream) - only warn if >10%
                pct_null = len(null_rs) / len(executed) * 100
                if pct_null > 50:
                    issues.append(f"High RS null rate: {pct_null:.0f}% of executed rows have null {col}")
                    logger.warning(f"Matrix integrity: {pct_null:.0f}% of executed rows have null {col}")

    ok = len([i for i in issues if "Missing required columns" in i]) == 0
    return ok, issues
