# How Matrix Update Replaces Data

## Summary
**Yes, the matrix update REPLACES data for the window period, but preserves all older data unchanged.**

## Detailed Process

### Step 1: Load Existing Matrix
- Loads the current matrix file (e.g., `master_matrix_20251229_021500.parquet`)
- Contains all historical data

### Step 2: Purge Window Period (DELETE)
- **Deletes** all rows where `trade_date >= reprocess_start_date`
- Example: If reprocess_start_date is `2025-11-06`, it deletes all rows from Nov 6 onward
- **Keeps** all rows before `reprocess_start_date` (untouched)

**Example:**
```
Before purge:
- Total rows: 24,398
- Dates: 2020-01-01 to 2025-12-26

After purge:
- Total rows: 24,142 (removed 256 rows)
- Dates: 2020-01-01 to 2025-11-05 (stopped before reprocess_start_date)
```

### Step 3: Reprocess Window Period
- Processes merged data for dates >= `reprocess_start_date`
- Applies sequencer logic with restored state
- Generates NEW rows for the window period

**Example:**
```
Reprocessed:
- Dates: 2025-11-06 to 2025-12-26
- New rows: 424 rows
```

### Step 4: Merge Old + New
- Combines: `existing_df` (old data before window) + `window_result_df` (newly processed window)
- Creates complete updated matrix

**Example:**
```
Final result:
- Old data (preserved): 24,142 rows (2020-01-01 to 2025-11-05)
- New data (replaced): 424 rows (2025-11-06 to 2025-12-26)
- Total: 24,566 rows
```

### Step 5: Save New File
- Creates a **NEW file** with timestamp: `master_matrix_20251229_022256.parquet`
- **Old files are NOT deleted** - they remain in the directory
- The new file becomes the "latest" file

## What Gets Replaced vs. Preserved

### ✅ **REPLACED** (Deleted and Reprocessed)
- All rows with `trade_date >= reprocess_start_date`
- These rows are:
  1. Deleted from the existing matrix
  2. Reprocessed from merged data
  3. Added back with potentially different sequencer decisions

### ✅ **PRESERVED** (Untouched)
- All rows with `trade_date < reprocess_start_date`
- These rows are:
  - Kept exactly as-is
  - Bitwise identical (no changes)
  - Not reprocessed

## File Behavior

### Old Files
- **NOT deleted** - they remain in `data/master_matrix/`
- You can see multiple files: `master_matrix_20251229_021500.parquet`, `master_matrix_20251229_022256.parquet`, etc.
- Each represents a snapshot at that time

### New File
- Created with current timestamp
- Contains the updated matrix
- Becomes the "latest" file (loaded by `load_existing_matrix()`)

## Why This Approach?

1. **Safety**: Old files preserved as backups
2. **Auditability**: Can compare old vs. new files
3. **Rollback**: Can revert to previous file if needed
4. **Determinism**: Older data never changes, only window period is reprocessed

## Example Timeline

**Before Update:**
```
File: master_matrix_20251229_021500.parquet
- 24,398 rows
- Dates: 2020-01-01 to 2025-12-26
- Checkpoint: 2025-12-26
```

**After Update:**
```
Old file: master_matrix_20251229_021500.parquet (still exists)
New file: master_matrix_20251229_022256.parquet (created)

New file contents:
- 24,142 rows from old file (2020-01-01 to 2025-11-05) - PRESERVED
- 424 rows newly processed (2025-11-06 to 2025-12-26) - REPLACED
- Total: 24,566 rows
```

## Key Points

1. **Data IS replaced** for the window period
2. **Old data is preserved** (before window)
3. **New file created** (old files not deleted)
4. **Deterministic**: Same inputs = same outputs for replaced period
5. **Idempotent**: Running update twice with no new data = no changes

