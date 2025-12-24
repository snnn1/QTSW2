# None Value Comparison Error - Debugging Added

## Error Message
```
Failed to load matrix data: '<' not supported between instances of 'NoneType' and 'str'
```

## Root Cause
This error occurs when pandas tries to sort or compare values where some are `None` and others are strings. Python cannot compare `None` with strings using `<`, `>`, etc.

## Debugging Added

### 1. **Master Matrix Build Sorting** (`master_matrix.py`)
**Location**: Lines 391-412

**Added**:
- Pre-sort validation: Checks each sort column for None/NaN values before sorting
- Logging: Warns about None values and logs sample problematic rows
- Fix: Replaces None with empty string for object/string columns before sorting
- Post-sort logging: Safe sorting for Stream/Instrument logging (handles None values)

**Columns checked**: `trade_date`, `entry_time`, `Instrument`, `Stream`

### 2. **Update Master Matrix Sorting** (`master_matrix.py`)
**Location**: Lines 651-667

**Added**:
- Same pre-sort validation and None handling
- Applied to update_master_matrix() function

### 3. **Sequencer Logic Sorting** (`sequencer_logic.py`)
**Location**: Lines 404-420

**Added**:
- Pre-sort validation for `Stream` and `Date` columns
- Logs sample rows with None values
- Fix: Fills None values in `Stream` column with empty string before sorting

### 4. **Schema Normalizer** (`schema_normalizer.py`)
**Location**: Lines 99-112

**Added**:
- Ensures `entry_time` and `exit_time` don't contain None values
- Fills None with empty string when creating from `Time` column

### 5. **API Endpoint** (`api.py`)
**Location**: Lines 350-387

**Added**:
- Filters None values before sorting streams/instruments/years
- Try-catch around sorting operations with detailed error logging
- Returns unsorted lists if sorting fails (graceful degradation)
- Enhanced exception handling with full traceback logging

## Debugging Output

When the error occurs, you'll now see:

1. **Warnings** about columns with None values:
   ```
   Column 'entry_time' has 15 None/NaN values before sorting
   ```

2. **Sample rows** with None values (first 3-5 rows):
   ```
   Sample rows with None in 'entry_time': [{'Stream': 'ES2', 'Date': '2025-12-02', ...}, ...]
   ```

3. **Fixes applied**:
   ```
   Filled None values in 'entry_time' with empty string for sorting
   ```

4. **Full traceback** in API errors:
   ```
   Traceback: [full stack trace]
   ```

## Where to Check Logs

- **Master Matrix logs**: Check `logs/master_matrix.log` or console output
- **API logs**: Check backend console output or API error responses
- **Debug level**: Set logging level to DEBUG to see more detailed information

## Expected Behavior After Fix

1. None values in sort columns are detected and logged
2. None values are replaced with empty strings for object columns before sorting
3. Sorting operations complete successfully
4. If sorting still fails, detailed error information is logged
5. The system continues to work even if some sorting operations fail (graceful degradation)

## Next Steps

If you still see the error:

1. Check the logs for which column has None values
2. Check the sample rows to see what data is causing the issue
3. Investigate why that data has None values (missing in source? schema issue?)
4. The error message will now include the full traceback to pinpoint the exact location

