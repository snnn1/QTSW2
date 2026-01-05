# Analyzer Output Contract
**Version:** 1.0  
**Date:** 2025  
**Purpose:** Formal data contract between Analyzer → Merger → Matrix pipeline stages

---

## Executive Summary

This contract defines strict invariants that analyzer output must satisfy to allow removal of ~150 lines of defensive matrix logic. Each invariant is machine-checkable and must be enforced before parquet write operations.

**Enforcement Points:**
1. **Analyzer Stage** - Before writing to `data/analyzer_temp/YYYY-MM-DD/`
2. **Merger Stage** - Before writing to `data/analyzed/<stream>/<year>/`

**Contract Principle:** Fail fast on violations. Do not attempt repair. Validation only.

---

## Invariant Table

| # | Invariant Name | Exact Condition | Enforcement Point | Validation Method | Matrix Code Removable |
|---|----------------|------------------|-------------------|-------------------|----------------------|
| 1 | **Valid Date Column** | All `Date` values parseable by `pd.to_datetime()` with no NaT results | Analyzer + Merger | Assert `pd.to_datetime(df['Date'], errors='coerce').isna().sum() == 0` | `_repair_invalid_dates()` strategies 1-3 (lines 652-692) |
| 2 | **Date Column Type** | `Date` column is `datetime64[ns]` dtype | Analyzer + Merger | Assert `pd.api.types.is_datetime64_any_dtype(df['Date'])` | `_repair_invalid_dates()` type conversion (lines 610-615) - **KEEP** (required for sorting) |
| 3 | **Required Schema** | All required columns present: `Date`, `Time`, `Session`, `Instrument`, `Stream` | Analyzer + Merger | Assert `set(REQUIRED_COLUMNS).issubset(set(df.columns))` | `normalize_schema()` missing column defaults (lines 68-82) |
| 4 | **Optional Schema Consistency** | Optional columns consistent across streams (if present) | Analyzer + Merger | Schema validation check | `normalize_schema()` optional column defaults (lines 80-82) |
| 5 | **Trimmed String Columns** | `Stream`, `Time`, `Instrument`, `Session` have no leading/trailing whitespace | Analyzer | Assert `df[col].str.strip().equals(df[col])` for string cols | `_apply_authoritative_rebuild()` string stripping (lines 481, 483) |
| 6 | **Non-None Sort Columns** | `trade_date`, `entry_time`, `Instrument`, `Stream` are non-None | Analyzer + Merger | Assert `df[col].notna().all()` for sort columns | `_sort_matrix_canonically()` None checking (lines 762-769) - **KEEP** sentinel filling (required) |
| 7 | **Valid Session Values** | `Session` column contains only `'S1'` or `'S2'` | Analyzer + Merger | Assert `df['Session'].isin(['S1', 'S2']).all()` | Already enforced in merger |
| 8 | **File Visibility** | Written parquet files immediately visible to readers | Analyzer + Merger | File system sync/flush before completion | `load_stream_data()` cache clearing (lines 143-148) |

---

## Detailed Invariant Specifications

### Invariant 1: Valid Date Column

**Name:** `VALID_DATE_COLUMN`

**Exact Condition:**
```python
# All Date values must parse to valid datetime objects
date_series = pd.to_datetime(df['Date'], errors='coerce')
assert date_series.isna().sum() == 0, f"Found {date_series.isna().sum()} invalid dates"
```

**Enforcement Location:**
- **Analyzer:** `modules/analyzer/scripts/run_data_processed.py` - Before `to_parquet()` call (line ~335)
- **Merger:** `modules/merger/merger.py` - In `process_analyzer_folder()` before merge (line ~834) - **ALREADY ENFORCED**

**Validation Code:**
```python
def validate_date_column(df: pd.DataFrame) -> None:
    """Validate all Date values are parseable."""
    if 'Date' not in df.columns:
        raise ValueError("REQUIRED COLUMN MISSING: Date")
    
    date_series = pd.to_datetime(df['Date'], errors='coerce')
    invalid_mask = date_series.isna()
    
    if invalid_mask.any():
        invalid_count = invalid_mask.sum()
        invalid_examples = df.loc[invalid_mask, 'Date'].head(5).tolist()
        raise ValueError(
            f"INVALID DATE VALUES: {invalid_count} row(s) with unparseable dates. "
            f"Examples: {invalid_examples}. "
            f"All dates must be parseable by pandas.to_datetime()."
        )
```

**Matrix Code Removable:**
- `_repair_invalid_dates()` - Strategy 1: Format parsing (lines 652-671)
- `_repair_invalid_dates()` - Strategy 2: Stream median inference (lines 673-685)
- `_repair_invalid_dates()` - Strategy 3: Global fallback (lines 687-692)
- `_apply_authoritative_rebuild()` - Invalid date skipping (lines 492-495)

**Total Lines Removable:** ~45 lines

---

### Invariant 2: Date Column Type

**Name:** `DATE_COLUMN_TYPE`

**Exact Condition:**
```python
# Date column must be datetime64[ns] dtype
assert pd.api.types.is_datetime64_any_dtype(df['Date']), "Date column must be datetime64 type"
```

**Enforcement Location:**
- **Analyzer:** `modules/analyzer/scripts/run_data_processed.py` - Before `to_parquet()` call
- **Merger:** `modules/merger/merger.py` - Already converts to datetime (line ~853)

**Validation Code:**
```python
def validate_date_type(df: pd.DataFrame) -> None:
    """Validate Date column is datetime type."""
    if 'Date' not in df.columns:
        raise ValueError("REQUIRED COLUMN MISSING: Date")
    
    if not pd.api.types.is_datetime64_any_dtype(df['Date']):
        # Convert to datetime (this is type coercion, not repair)
        df['Date'] = pd.to_datetime(df['Date'], errors='coerce')
        # Then validate (should already be valid per Invariant 1)
        if df['Date'].isna().any():
            raise ValueError("Date conversion failed - violates Invariant 1")
```

**Matrix Code Removable:**
- **NONE** - Type conversion is required for sorting operations
- **Status:** KEEP - Required for consistent sorting

---

### Invariant 3: Required Schema

**Name:** `REQUIRED_SCHEMA`

**Exact Condition:**
```python
# All required columns must be present
REQUIRED_COLUMNS = ['Date', 'Time', 'Session', 'Instrument', 'Stream']
assert set(REQUIRED_COLUMNS).issubset(set(df.columns)), f"Missing columns: {set(REQUIRED_COLUMNS) - set(df.columns)}"
```

**Enforcement Location:**
- **Analyzer:** `modules/analyzer/scripts/run_data_processed.py` - Before `to_parquet()` call
- **Merger:** `modules/merger/merger.py` - Already enforced in `_validate_schema()` (line ~423) - **PARTIALLY ENFORCED** (missing Stream requirement)

**Validation Code:**
```python
REQUIRED_COLUMNS = ['Date', 'Time', 'Session', 'Instrument', 'Stream']

def validate_required_schema(df: pd.DataFrame) -> None:
    """Validate all required columns are present."""
    missing_cols = [col for col in REQUIRED_COLUMNS if col not in df.columns]
    if missing_cols:
        raise ValueError(
            f"REQUIRED COLUMNS MISSING: {missing_cols}. "
            f"Required columns: {REQUIRED_COLUMNS}. "
            f"Analyzer must provide all required fields."
        )
```

**Matrix Code Removable:**
- `normalize_schema()` - Missing required column defaults (lines 68-77)
- `normalize_schema()` - Missing optional column defaults (lines 80-82)

**Total Lines Removable:** ~15 lines

---

### Invariant 4: Optional Schema Consistency

**Name:** `OPTIONAL_SCHEMA_CONSISTENCY`

**Exact Condition:**
```python
# If optional columns exist, they must have consistent types across streams
# This is informational - no matrix code depends on it
# Status: Not blocking matrix simplification
```

**Enforcement Location:**
- **Analyzer:** Schema validation layer
- **Merger:** Schema validation layer

**Matrix Code Removable:**
- **NONE** - Already handled by required schema enforcement

---

### Invariant 5: Trimmed String Columns

**Name:** `TRIMMED_STRING_COLUMNS`

**Exact Condition:**
```python
# String columns must have no leading/trailing whitespace
STRING_COLUMNS = ['Stream', 'Time', 'Instrument', 'Session']
for col in STRING_COLUMNS:
    if col in df.columns and df[col].dtype == 'object':
        assert df[col].str.strip().equals(df[col]), f"Column {col} contains whitespace"
```

**Enforcement Location:**
- **Analyzer:** `modules/analyzer/scripts/run_data_processed.py` - Before `to_parquet()` call
- **Merger:** `modules/merger/merger.py` - Before merge (normalize strings)

**Validation Code:**
```python
STRING_COLUMNS = ['Stream', 'Time', 'Instrument', 'Session']

def validate_trimmed_strings(df: pd.DataFrame) -> None:
    """Validate string columns have no leading/trailing whitespace."""
    for col in STRING_COLUMNS:
        if col in df.columns and df[col].dtype == 'object':
            # Check for whitespace
            has_whitespace = df[col].astype(str).str.strip().ne(df[col].astype(str)).any()
            if has_whitespace:
                # Auto-trim (this is normalization, not repair)
                df[col] = df[col].astype(str).str.strip()
                # Then validate no empty strings after trimming
                if df[col].eq('').any():
                    raise ValueError(f"Column {col} contains empty strings after trimming")
```

**Matrix Code Removable:**
- `_apply_authoritative_rebuild()` - String stripping in trade key building (lines 481, 483)

**Total Lines Removable:** ~2 lines (but affects logic flow)

---

### Invariant 6: Non-None Sort Columns

**Name:** `NON_NONE_SORT_COLUMNS`

**Exact Condition:**
```python
# Sort columns must be non-None
SORT_COLUMNS = ['trade_date', 'entry_time', 'Instrument', 'Stream']
for col in SORT_COLUMNS:
    if col in df.columns:
        assert df[col].notna().all(), f"Column {col} contains None/NaN values"
```

**Enforcement Location:**
- **Analyzer:** Before `to_parquet()` call (validate derived columns)
- **Merger:** Before writing monthly file (validate after merge)

**Validation Code:**
```python
SORT_COLUMNS = ['trade_date', 'entry_time', 'Instrument', 'Stream']

def validate_non_none_sort_columns(df: pd.DataFrame) -> None:
    """Validate sort columns are non-None."""
    for col in SORT_COLUMNS:
        if col in df.columns:
            none_count = df[col].isna().sum()
            if none_count > 0:
                raise ValueError(
                    f"SORT COLUMN CONTAINS None: Column '{col}' has {none_count} None/NaN values. "
                    f"Sort columns must be non-None for matrix operations."
                )
```

**Matrix Code Removable:**
- `_sort_matrix_canonically()` - None value checking (lines 762-769)
- **KEEP:** Sentinel filling logic (lines 771-783) - Required for sorting stability

**Total Lines Removable:** ~8 lines (checking only, keep filling)

---

### Invariant 7: Valid Session Values

**Name:** `VALID_SESSION_VALUES`

**Exact Condition:**
```python
# Session must be 'S1' or 'S2'
assert df['Session'].isin(['S1', 'S2']).all(), f"Invalid session values: {df[~df['Session'].isin(['S1', 'S2'])]['Session'].unique()}"
```

**Enforcement Location:**
- **Merger:** Already enforced in `_validate_schema()` (line ~435) - **ALREADY ENFORCED**

**Matrix Code Removable:**
- **NONE** - Already enforced upstream

---

### Invariant 8: File Visibility

**Name:** `FILE_VISIBILITY`

**Exact Condition:**
```python
# Written files must be immediately visible to readers
# This is a file system guarantee, not a data validation
```

**Enforcement Location:**
- **Analyzer:** After `to_parquet()` call - ensure file system sync
- **Merger:** After `to_parquet()` call - ensure file system sync

**Validation Code:**
```python
def ensure_file_visibility(file_path: Path) -> None:
    """Ensure written file is immediately visible to readers."""
    import os
    # Force file system sync
    file_handle = open(file_path, 'rb')
    os.fsync(file_handle.fileno())
    file_handle.close()
    # Verify file exists and is readable
    assert file_path.exists(), f"File {file_path} does not exist after write"
    assert file_path.stat().st_size > 0, f"File {file_path} is empty"
```

**Matrix Code Removable:**
- `load_stream_data()` - File system cache clearing (lines 143-148)

**Total Lines Removable:** ~5 lines

---

## Proposed Validation Layer

### Location: `modules/analyzer/validation/analyzer_output_validator.py`

```python
"""
Analyzer Output Validator

Validates analyzer output against strict contract before parquet write.
Fails fast on violations. Does not attempt repair.
"""

import pandas as pd
from pathlib import Path
from typing import List

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
        
        # Invariant 2: Date Column Type (coerce to datetime if needed, then validate)
        if not pd.api.types.is_datetime64_any_dtype(df['Date']):
            # This should not happen if Invariant 1 passed, but check anyway
            df['Date'] = date_series  # Already converted above
        
        # Invariant 3: Required Schema
        missing_cols = [col for col in REQUIRED_COLUMNS if col not in df.columns]
        if missing_cols:
            raise ValueError(
                f"REQUIRED COLUMNS MISSING{file_context}: {missing_cols}. "
                f"Required columns: {REQUIRED_COLUMNS}."
            )
        
        # Invariant 5: Trimmed String Columns
        for col in STRING_COLUMNS:
            if col in df.columns and df[col].dtype == 'object':
                has_whitespace = df[col].astype(str).str.strip().ne(df[col].astype(str)).any()
                if has_whitespace:
                    raise ValueError(
                        f"STRING COLUMN CONTAINS WHITESPACE{file_context}: Column '{col}' has leading/trailing whitespace. "
                        f"String columns must be trimmed."
                    )
        
        # Invariant 6: Non-None Sort Columns
        # Note: trade_date may not exist in analyzer output (it's derived)
        # Only validate if column exists
        for col in SORT_COLUMNS:
            if col in df.columns:
                none_count = df[col].isna().sum()
                if none_count > 0:
                    raise ValueError(
                        f"SORT COLUMN CONTAINS None{file_context}: Column '{col}' has {none_count} None/NaN values. "
                        f"Sort columns must be non-None."
                    )
        
        # Invariant 7: Valid Session Values
        if 'Session' in df.columns:
            invalid_sessions = df[~df['Session'].isin(['S1', 'S2'])]['Session'].unique()
            if len(invalid_sessions) > 0:
                raise ValueError(
                    f"INVALID SESSION VALUES{file_context}: {list(invalid_sessions)}. "
                    f"Session must be 'S1' or 'S2'."
                )


def validate_before_write(df: pd.DataFrame, file_path: Path) -> None:
    """
    Convenience function to validate before parquet write.
    
    Usage:
        validator.validate_before_write(df, parquet_path)
        df.to_parquet(parquet_path, index=False)
    """
    AnalyzerOutputValidator.validate(df, file_path)
```

### Integration Points

**1. Analyzer Script (`modules/analyzer/scripts/run_data_processed.py`):**
```python
# Before line 335 (to_parquet call)
from modules.analyzer.validation.analyzer_output_validator import validate_before_write

# Validate before write
validate_before_write(res, out_path)

# Then write
res.to_parquet(out_path, index=False, compression='snappy')
```

**2. Analyzer App (`modules/analyzer/analyzer_app/app.py`):**
```python
# Before line 809 (to_parquet call)
from modules.analyzer.validation.analyzer_output_validator import validate_before_write

# Validate before write
validate_before_write(final_copy, parquet_path)

# Then write
final_copy.to_parquet(parquet_path, index=False)
```

**3. Merger (`modules/merger/merger.py`):**
```python
# Already has validation, but enhance to include Stream requirement
# In _validate_schema() method, add Stream to REQUIRED_COLUMNS check
```

---

## Enforcement Checklist

### Analyzer Stage Enforcement

- [ ] **Invariant 1:** Add date validation before `to_parquet()` in `run_data_processed.py`
- [ ] **Invariant 2:** Ensure Date column is datetime type before write
- [ ] **Invariant 3:** Add schema validation (including Stream column) before write
- [ ] **Invariant 5:** Trim string columns before write (normalization, not repair)
- [ ] **Invariant 6:** Validate sort columns are non-None (if derived columns exist)
- [ ] **Invariant 8:** Ensure file system sync after `to_parquet()` write

### Merger Stage Enforcement

- [ ] **Invariant 1:** Already enforced (line ~834) - **VERIFIED**
- [ ] **Invariant 2:** Already enforced (line ~853) - **VERIFIED**
- [ ] **Invariant 3:** Add Stream to REQUIRED_COLUMNS check (currently missing)
- [ ] **Invariant 5:** Trim string columns during merge (normalization)
- [ ] **Invariant 6:** Validate sort columns after merge (if derived columns exist)
- [ ] **Invariant 7:** Already enforced (line ~435) - **VERIFIED**
- [ ] **Invariant 8:** Ensure file system sync after `to_parquet()` write

---

## Deletion Mapping: Invariant → Matrix Code

### Once Invariant 1 (Valid Date Column) is Enforced:

| Matrix Code Location | Lines | Action |
|---------------------|-------|--------|
| `_repair_invalid_dates()` - Strategy 1 (format parsing) | 652-671 | **DELETE** |
| `_repair_invalid_dates()` - Strategy 2 (stream median) | 673-685 | **DELETE** |
| `_repair_invalid_dates()` - Strategy 3 (global fallback) | 687-692 | **DELETE** |
| `_apply_authoritative_rebuild()` - Invalid date skipping | 492-495 | **DELETE** |
| **KEEP:** Sentinel date preservation | 706 | **KEEP** (required for sorting) |
| **KEEP:** Date type conversion | 610-615 | **KEEP** (required for sorting) |

**Total Removable:** ~45 lines

---

### Once Invariant 3 (Required Schema) is Enforced:

| Matrix Code Location | Lines | Action |
|---------------------|-------|--------|
| `normalize_schema()` - Missing required column defaults | 68-77 | **DELETE** |
| `normalize_schema()` - Missing optional column defaults | 80-82 | **DELETE** |

**Total Removable:** ~15 lines

---

### Once Invariant 5 (Trimmed Strings) is Enforced:

| Matrix Code Location | Lines | Action |
|---------------------|-------|--------|
| `_apply_authoritative_rebuild()` - String stripping | 481, 483 | **DELETE** (affects 2 lines) |

**Total Removable:** ~2 lines (but simplifies logic)

---

### Once Invariant 6 (Non-None Sort Columns) is Enforced:

| Matrix Code Location | Lines | Action |
|---------------------|-------|--------|
| `_sort_matrix_canonically()` - None value checking | 762-769 | **DELETE** |
| **KEEP:** Sentinel filling | 771-783 | **KEEP** (required for sorting) |

**Total Removable:** ~8 lines

---

### Once Invariant 8 (File Visibility) is Enforced:

| Matrix Code Location | Lines | Action |
|---------------------|-------|--------|
| `load_stream_data()` - File system cache clearing | 143-148 | **DELETE** |

**Total Removable:** ~5 lines

---

### Additional Redundant Code:

| Matrix Code Location | Lines | Action |
|---------------------|-------|--------|
| `_rebuild_partial()` - Stream filtering safety check | 295-297 | **DELETE** (redundant) |

**Total Removable:** ~3 lines

---

## Summary: Total Code Removable

| Category | Lines Removable | Status |
|----------|----------------|--------|
| Date repair strategies | ~45 | Conditional on Invariant 1 |
| Schema defaults | ~15 | Conditional on Invariant 3 |
| String trimming | ~2 | Conditional on Invariant 5 |
| None checking | ~8 | Conditional on Invariant 6 |
| Cache clearing | ~5 | Conditional on Invariant 8 |
| Redundant checks | ~3 | Can delete immediately |
| **TOTAL** | **~78 lines** | Once all invariants enforced |

**Note:** Some defensive logic is required for sorting stability (sentinel values) and cannot be removed even with perfect analyzer output.

---

## Implementation Priority

### Phase 1: High Impact (Remove ~60 lines)

1. **Invariant 1 (Valid Date Column)** - Removes ~45 lines of date repair logic
2. **Invariant 3 (Required Schema)** - Removes ~15 lines of schema defaults

### Phase 2: Medium Impact (Remove ~10 lines)

3. **Invariant 6 (Non-None Sort Columns)** - Removes ~8 lines of None checking
4. **Invariant 5 (Trimmed Strings)** - Removes ~2 lines, simplifies logic

### Phase 3: Low Impact (Remove ~8 lines)

5. **Invariant 8 (File Visibility)** - Removes ~5 lines of cache clearing
6. **Redundant checks** - Remove ~3 lines immediately

---

## Testing Requirements

For each invariant, add tests:

1. **Unit tests** - Test validator with valid/invalid data
2. **Integration tests** - Test analyzer → merger → matrix pipeline
3. **Regression tests** - Ensure removed matrix code doesn't break behavior

---

## Contract Version History

- **v1.0 (2025)** - Initial contract definition based on defensive logic audit

---

**Contract Definition Complete**
