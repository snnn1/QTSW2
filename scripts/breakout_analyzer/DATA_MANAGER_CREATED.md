# DataManager Module Created ✅

## Overview

Created a new `DataManager` module (`logic/data_logic.py`) that consolidates all data-related operations that were previously scattered across multiple modules.

## What It Does

The `DataManager` class provides a centralized interface for:

### 1. **Data Loading** ✅
- `load_parquet()` - Load from single file or directory of parquet files
- `load_csv()` - Load from CSV files
- Handles concatenation, memory management, and error handling

### 2. **Data Cleaning** ✅
- `_clean_dataframe()` - Remove duplicates, fix OHLC, normalize timezone
- Automatic duplicate removal (by timestamp + instrument)
- Deterministic indexing (sorted, reset index)

### 3. **Timezone Normalization** ✅
- `_normalize_timezone()` - Ensure all timestamps are in Chicago timezone
- Handles naive timestamps (assumes Chicago)
- Converts from other timezones to Chicago
- Critical for accurate session calculations

### 4. **OHLC Relationship Fixing** ✅
- `_fix_ohlc_relationships()` - Automatically fix invalid OHLC data
- Swaps high/low if reversed
- Ensures high >= open/close
- Ensures low <= open/close
- Clips open/close to [low, high] range

### 5. **Session Cutting** ✅
- `filter_by_session()` - Filter data by trading session (S1/S2)
- `filter_by_date_range()` - Filter by date range
- `filter_by_instrument()` - Filter by instrument symbol
- Timezone-aware filtering

### 6. **Data Slicing** ✅
- `get_data_after_timestamp()` - Get data after (or at) timestamp
- `get_data_before_timestamp()` - Get data before (or at) timestamp
- Used for range detection, entry detection, and trade execution

### 7. **Outlier Detection** ✅
- `detect_outliers()` - Detect outliers using IQR method
- Configurable detection method
- Returns boolean mask for outlier rows

### 8. **Missing Bar Reconstruction** ✅
- `reconstruct_missing_bars()` - Create regular time intervals
- Fill missing bars with forward/backward fill or interpolation
- Useful for ensuring complete data coverage

### 9. **Data Validation** ✅
- `validate_dataframe()` - Validate DataFrame structure
- Checks required columns, data types, timezone awareness
- Returns errors and warnings

## Module Structure

```python
class DataManager:
    def __init__(self, auto_fix_ohlc=True, auto_normalize_timezone=True)
    
    # Loading
    def load_parquet(file_path) -> DataLoadResult
    def load_csv(file_path) -> DataLoadResult
    
    # Filtering
    def filter_by_instrument(df, instrument) -> DataFilterResult
    def filter_by_date_range(df, start_date, end_date) -> DataFilterResult
    def filter_by_session(df, session, start, end) -> DataFilterResult
    
    # Slicing
    def get_data_after_timestamp(df, timestamp, inclusive=True) -> pd.DataFrame
    def get_data_before_timestamp(df, timestamp, inclusive=True) -> pd.DataFrame
    
    # Analysis
    def detect_outliers(df, method="iqr", columns=None) -> pd.DataFrame
    def reconstruct_missing_bars(df, frequency="1min", fill_method="forward") -> pd.DataFrame
    def validate_dataframe(df) -> Tuple[bool, List[str], List[str]]
    
    # Internal helpers
    def _clean_dataframe(df) -> Tuple[pd.DataFrame, List[str]]
    def _normalize_timezone(df) -> Tuple[pd.DataFrame, List[str]]
    def _fix_ohlc_relationships(df) -> Tuple[pd.DataFrame, List[str]]
    def _ensure_deterministic_index(df) -> pd.DataFrame
```

## Data Classes

### `DataLoadResult`
```python
@dataclass
class DataLoadResult:
    success: bool
    df: Optional[pd.DataFrame]
    errors: List[str]
    warnings: List[str]
    metadata: Dict
```

### `DataFilterResult`
```python
@dataclass
class DataFilterResult:
    df: pd.DataFrame
    rows_removed: int
    filters_applied: List[str]
```

## Where Data Operations Were Scattered (Before)

1. **`run_data_processed.py`**
   - Data loading from parquet files
   - Concatenation logic
   - Basic column validation

2. **`breakout_core/engine.py`**
   - Data filtering by instrument
   - Data slicing for trade execution (`day_df`, `mfe_df`)
   - Session-based filtering

3. **`logic/validation_logic.py`**
   - OHLC relationship fixing
   - Data validation
   - Column checking

4. **`logic/range_logic.py`**
   - Data slicing for range periods
   - Session-based data extraction

5. **`logic/entry_logic.py`**
   - Data filtering after range end (`post = df[df["timestamp"] >= end_ts]`)

6. **`logic/price_tracking_logic.py`**
   - Data slicing for MFE calculation
   - Data filtering for trade execution

## Next Steps (Integration)

To fully integrate `DataManager`, update:

1. **`run_data_processed.py`**
   ```python
   from logic.data_logic import DataManager
   data_manager = DataManager()
   result = data_manager.load_parquet(data_folder)
   if result.success:
       df = result.df
   ```

2. **`breakout_core/engine.py`**
   ```python
   from logic.data_logic import DataManager
   data_manager = DataManager()
   
   # Replace: df = df[df["instrument"] == inst].copy()
   filter_result = data_manager.filter_by_instrument(df, inst)
   df = filter_result.df
   
   # Replace: day_df = df[(df["timestamp"] >= R.end_ts) & ...].copy()
   day_df = data_manager.filter_by_date_range(
       df, R.end_ts, R.end_ts + pd.Timedelta(hours=24)
   ).df
   ```

3. **`logic/entry_logic.py`**
   ```python
   # Replace: post = df[df["timestamp"] >= end_ts].copy()
   post = data_manager.get_data_after_timestamp(df, end_ts, inclusive=True)
   ```

4. **`logic/range_logic.py`**
   - Use `DataManager` for session-based filtering
   - Use `DataManager` for date range filtering

## Benefits

1. **Centralized Logic** - All data operations in one place
2. **Consistent Behavior** - Same timezone handling, same cleaning logic everywhere
3. **Easier Testing** - Single module to test data operations
4. **Better Error Handling** - Structured error/warning reporting
5. **Deterministic Results** - Consistent sorting and indexing
6. **Maintainability** - Changes to data logic only need to happen in one place

## Usage Example

```python
from logic.data_logic import DataManager

# Initialize
data_manager = DataManager(
    auto_fix_ohlc=True,
    auto_normalize_timezone=True
)

# Load data
result = data_manager.load_parquet("data/processed/ES_2024.parquet")
if not result.success:
    print(f"Errors: {result.errors}")
    return

df = result.df
print(f"Warnings: {result.warnings}")
print(f"Loaded {result.metadata['rows']} rows")

# Filter by instrument
filter_result = data_manager.filter_by_instrument(df, "ES")
df = filter_result.df
print(f"Removed {filter_result.rows_removed} rows")

# Get data after timestamp
post_data = data_manager.get_data_after_timestamp(df, some_timestamp)

# Validate
is_valid, errors, warnings = data_manager.validate_dataframe(df)
```

---

**Created:** 2025-01-XX  
**Status:** ✅ Module created, ready for integration









