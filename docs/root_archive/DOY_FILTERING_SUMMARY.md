# DOY (Day of Year) Filtering Summary

## Overview
The DOY tab displays profit breakdown by day of year (1-365/366) for each stream, using **all available data** from the master matrix parquet file (not just worker-loaded data). The tab shows two views: **"After Filters"** and **"Before Filters"** to compare filtered vs unfiltered performance.

## Data Source
- **Source**: Full dataset from master matrix parquet file (backend API)
- **Calculation**: Backend calculates day of year using `trade_date.dt.dayofyear` (pandas)
- **Format**: `{1: {"ES1": profit, "ES2": profit, ...}, 2: {...}, ...}` where keys are day numbers (1-366)

## Filter Types Applied to DOY

### 1. Master Stream Filters (Applied to All Streams)
When filters are set on the "master" stream, they apply to **all streams** in the DOY breakdown:

#### a) Exclude Days of Week (DOW)
- **Filter**: `exclude_days_of_week`
- **Effect**: Removes trades from specified days of week (Monday, Tuesday, etc.)
- **Example**: Excluding "Wednesday" removes all Wednesday trades from DOY calculation
- **Impact**: Days of year that fall on excluded weekdays will show reduced or zero profit

#### b) Exclude Days of Month (DOM)
- **Filter**: `exclude_days_of_month`
- **Effect**: Removes trades from specified calendar days of month (1-31)
- **Example**: Excluding [4, 16, 30] removes trades from the 4th, 16th, and 30th of any month
- **Impact**: Multiple days of year will be affected (e.g., Jan 4, Feb 4, Mar 4, etc. all get filtered)

#### c) Exclude Times
- **Filter**: `exclude_times`
- **Effect**: Removes trades from specified time slots (e.g., "07:30", "08:00")
- **Example**: Excluding ["07:30", "08:00"] removes all trades at those times
- **Impact**: All days of year are affected equally (time-based filtering)

### 2. Per-Stream Filters
Individual streams can have their own filters:
- **Scope**: Only affects that specific stream
- **Types**: Same as master filters (DOW, DOM, Times)
- **Example**: ES1 can exclude Mondays while ES2 doesn't

### 3. Stream Inclusion Filter
- **Filter**: `include_streams` (on master stream)
- **Effect**: Limits which streams are included in the breakdown
- **Example**: `include_streams: ["ES1", "ES2"]` only shows ES1 and ES2 data
- **Impact**: Other streams are completely excluded from the breakdown

### 4. Final Allowed Filter
- **Filter**: `final_allowed` column (backend computed)
- **Effect**: Excludes trades marked as `final_allowed = False`
- **Purpose**: Removes trades that failed other filter criteria
- **Impact**: Only affects "After Filters" view

## Two Views: Before vs After Filters

### "Before Filters" (Blue Header)
- **Data**: All trades from the master matrix
- **Filters Applied**: None (except `final_allowed` may still exclude some)
- **Use Case**: See raw performance by day of year without any filtering
- **API Call**: `use_filtered=false`

### "After Filters" (Green Header)
- **Data**: Trades remaining after applying all active filters
- **Filters Applied**: 
  - Master stream filters (DOW, DOM, Times)
  - Per-stream filters
  - Stream inclusion filter
  - `final_allowed` filter
- **Use Case**: See how performance changes when filters are applied
- **API Call**: `use_filtered=true`

## Filter Application Flow

```
1. Load full dataset from parquet file
2. Apply stream inclusion filter (if specified)
3. Prepare data (ensure ProfitDollars, dates, etc.)
4. IF use_filtered=true:
   a. Apply master filters to all rows:
      - Exclude days of week
      - Exclude days of month
      - Exclude times
   b. Apply per-stream filters:
      - For each stream, apply its specific filters
   c. Apply final_allowed filter
5. Calculate DOY breakdown:
   - Group by day_of_year (1-366) and Stream
   - Sum ProfitDollars for each group
6. Return breakdown: {doy: {stream: profit}}
```

## Example: How Filters Affect DOY

### Scenario: Exclude Wednesdays
- **Before Filters**: Day 15 (Jan 15, 2024 = Monday) shows $1000 profit
- **After Filters**: Day 15 still shows $1000 (Monday not excluded)
- **Before Filters**: Day 17 (Jan 17, 2024 = Wednesday) shows $500 profit
- **After Filters**: Day 17 shows $0 (Wednesday excluded)

### Scenario: Exclude Days 4, 16, 30
- **Before Filters**: Day 4 (Jan 4) shows $800, Day 35 (Feb 4) shows $600, Day 66 (Mar 5) shows $700
- **After Filters**: Day 4 shows $0, Day 35 shows $0, Day 66 still shows $700 (not excluded)

### Scenario: Exclude Time "07:30"
- **Before Filters**: All days show full profit including 07:30 trades
- **After Filters**: All days show reduced profit (07:30 trades removed from all days)

## Contract Multiplier
- **Applied**: Contract multiplier affects profit calculations
- **Effect**: Multiplies ProfitDollars by the multiplier (e.g., 2.0 = trading 2 contracts)
- **Scope**: Applied to all profit calculations before grouping

## Key Differences from Other Tabs

| Feature | DOY | DOM | Date Tab |
|---------|-----|-----|----------|
| Data Source | Backend (full dataset) | Backend (full dataset) | Worker (loaded subset) |
| Filtering | Full backend filtering | Full backend filtering | Frontend filtering |
| Calculation | Day of year (1-366) | Day of month (1-31) | Calendar date (YYYY-MM-DD) |
| Performance | Fast (backend) | Fast (backend) | Slower (frontend) |

## Filter Configuration Location

Filters are configured in:
- **UI**: Filters panel (per stream or master)
- **Storage**: Browser localStorage
- **API**: Sent as `stream_filters` in breakdown request

## Debugging Filter Effects

Check browser console for:
- `[Breakdown] Fetching doy breakdown from backend (useFiltered=true/false, streamInclude=...)`
- `[Breakdown] Received response for doy: {breakdownKeys: X, ...}`
- Backend logs: `After filtering: X rows remaining (from Y total)`

## Summary Statistics

The DOY tab includes a quantitative summary showing:
- Total profit (sum of all days)
- Average profit per day
- Win rate (% of profitable days)
- Standard deviation
- Top 10 best days
- Top 10 worst days
- Top 5 streams by profit

All statistics respect the active filters when viewing "After Filters" view.
