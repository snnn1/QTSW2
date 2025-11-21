# Merge Flow Verification

## How Merging Works

### Scenario: Analyze CL at 07:30, then 08:00

**Step 1: First Analysis (07:30)**
1. Analyzer runs → Saves to `data/analyzer_temp/2025-11-18/CL_*.parquet` (with 07:30 data)
2. Merger runs:
   - Reads CL file from temp folder
   - Detects instrument = CL (from Instrument column)
   - Groups by month from Date column (November 2025)
   - Creates `CL/2025/CL_an_2025_11.parquet` with 07:30 data
   - Deletes temp folder

**Step 2: Second Analysis (08:00)**
1. Analyzer runs → Saves to `data/analyzer_temp/2025-11-18/CL_*.parquet` (with 08:00 data)
2. Merger runs:
   - Reads CL file from temp folder
   - Detects instrument = CL
   - Groups by month (November 2025)
   - **Loads existing** `CL/2025/CL_an_2025_11.parquet`
   - **Concatenates** existing (07:30) + new (08:00) data
   - **Removes duplicates** using key: [Date, Time, Target, Direction, Session, Instrument]
   - **Sorts** by Date, Time
   - **Writes** merged file back
   - Deletes temp folder

## Duplicate Detection

Rows are considered duplicates if they match ALL of:
- `Date` (same date)
- `Time` (same time slot)
- `Target` (same target level)
- `Direction` (same direction)
- `Session` (same session)
- `Instrument` (same instrument)

**Example:**
- `2024-01-15, 07:30, Target=10, Long, S1, CL` + `2024-01-15, 08:00, Target=10, Long, S1, CL` = **NOT duplicates** (different Time)
- `2024-01-15, 07:30, Target=10, Long, S1, CL` + `2024-01-15, 07:30, Target=10, Long, S1, CL` = **DUPLICATES** (same everything)

## Verification Points

✅ **Test Confirmed:**
- Duplicate removal works (6 rows → 5 rows, removed 1 duplicate)
- Sorting works (sorted by Date, then Time)
- Merge logic concatenates correctly

✅ **Code Flow:**
1. `_merge_with_existing_monthly_file()` loads existing file
2. `pd.concat()` combines old + new data
3. `_remove_duplicates_analyzer()` removes duplicates
4. `_sort_analyzer_data()` sorts chronologically
5. `_write_monthly_file()` writes merged file atomically

## Result

**Yes, merging works correctly!**

When you analyze:
- CL at 07:30 → Creates `CL_an_2025_11.parquet` with 07:30 data
- CL at 08:00 → Merges with existing file, adds 08:00 data
- Both time slots will be in the same monthly file
- Duplicates are automatically removed
- Data is sorted chronologically




