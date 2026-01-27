# Hydration Method Accuracy Analysis

## Overview

The hydration method loads historical bar data into the robot's buffer before trading begins. This analysis evaluates the accuracy and reliability of the hydration process.

## Hydration Methods

### 1. DRYRUN Mode: CSV File-Based Hydration
- **Source**: `data/raw/{instrument}/1m/{yyyy}/{MM}/{yyyy-MM-dd}.csv`
- **Method**: `PerformPreHydration()` in `StreamStateMachine.cs`
- **Process**:
  1. Reads CSV file line-by-line
  2. Parses timestamp (UTC) and OHLCV data
  3. Filters bars to hydration window: `[range_start, min(now, range_end))`
  4. Inserts bars into buffer with `BarSource.CSV`

### 2. SIM Mode: NinjaTrader BarsRequest API
- **Source**: NinjaTrader historical data via `BarsRequest`
- **Method**: `LoadPreHydrationBars()` in `RobotEngine.cs`
- **Process**:
  1. Receives bars from NinjaTrader strategy
  2. Filters future bars (`bar.TimestampUtc > utcNow`)
  3. Filters partial bars (`barAgeMinutes < 0.1 minutes`)
  4. Feeds bars to streams with `BarSource.BARSREQUEST`

## Accuracy Mechanisms

### ✅ Strengths

1. **Time Window Filtering**
   - Only loads bars within `[range_start, min(now, range_end))`
   - Prevents loading future or irrelevant bars
   - Location: `StreamStateMachine.cs:3132` (CSV), `RobotEngine.cs:1739` (BarsRequest)

2. **Future Bar Filtering**
   - Rejects bars with `timestamp > current_time`
   - Prevents duplicate bars when live feed arrives later
   - Location: `RobotEngine.cs:1739`

3. **Partial Bar Filtering**
   - SIM mode: Rejects bars < 0.1 minutes old
   - CSV mode: Rejects bars < 1.0 minutes old in `AddBarToBuffer()`
   - Ensures only fully closed bars are used
   - Location: `RobotEngine.cs:1748`, `StreamStateMachine.cs:4286`

4. **Deduplication with Precedence**
   - Centralized in `AddBarToBuffer()` method
   - Precedence: `LIVE > BARSREQUEST > CSV`
   - Prevents duplicate bars from multiple sources
   - Tracks OHLC differences when duplicates are replaced
   - Location: `StreamStateMachine.cs:4366-4464`

5. **Gap Detection**
   - Tracks gaps > 1 minute between bars
   - Classifies gaps as `DATA_FEED_FAILURE` or `LOW_LIQUIDITY`
   - Logs gap statistics for analysis
   - Location: `StreamStateMachine.cs:2296-2349`

6. **Comprehensive Logging**
   - `HYDRATION_SUMMARY`: Bar source breakdown at transition
   - `HYDRATION_SNAPSHOT`: Consolidated snapshot per stream
   - Tracks: historical_bar_count, live_bar_count, deduped_bar_count
   - Location: `StreamStateMachine.cs:1208-1357`

7. **Data Validation**
   - Validates bar data: `high >= low`, `close in [low, high]`
   - Rejects invalid bars with logging
   - Location: `StreamStateMachine.cs:2211-2248`

### ⚠️ Limitations

1. **No Expected Bar Count Validation**
   - Does not verify that all expected bars within the window were loaded
   - No comparison against expected count based on time window
   - Could miss data silently if CSV is incomplete

2. **Silent CSV Parsing Failures**
   - CSV parsing errors are silently skipped (`continue` statements)
   - No error aggregation or reporting for parsing failures
   - Location: `StreamStateMachine.cs:3120-3140`
   ```csharp
   if (parts.Length < 5) continue;  // Silent skip
   if (!DateTimeOffset.TryParse(...)) continue;  // Silent skip
   if (!decimal.TryParse(...)) continue;  // Silent skip
   ```

3. **No Data Integrity Checks**
   - No checksum or hash validation of loaded data
   - No comparison with source file to verify completeness
   - Cannot detect corruption or partial file reads

4. **Time Window Edge Cases**
   - If `now` is before `range_start`, hydration window is empty
   - No validation that hydration window is reasonable
   - Could result in zero bars loaded without clear error

5. **No Source Data Verification**
   - Does not verify CSV file exists before attempting to read
   - Does not check file size or modification time
   - No validation that CSV file matches expected trading date

6. **DST Transition Handling**
   - DST transitions may cause missing or duplicate hours
   - Gap detection may flag legitimate DST transitions as errors
   - Location: `StreamStateMachine.cs:3574-3587`

## Accuracy Metrics

### Current Tracking
- **Bar counts by source**: historical, live, deduped
- **Filtered bars**: future, partial
- **Gap statistics**: largest single gap, total gap minutes
- **Timing context**: now_chicago, range_start_chicago, slot_time_chicago

### Missing Metrics
- **Expected vs actual bar count**: No comparison
- **Completeness percentage**: No calculation
- **Parsing error count**: Not tracked
- **Source file validation**: Not performed

## Recommendations for Improved Accuracy

### 1. Add Expected Bar Count Validation
```csharp
var expectedBarCount = (int)(hydrationEnd - hydrationStart).TotalMinutes;
var completenessPercentage = (hydratedBars.Count / expectedBarCount) * 100.0;
if (completenessPercentage < 95.0)
{
    LogHealth("WARN", "PRE_HYDRATION_INCOMPLETE", 
        $"Only {completenessPercentage:F1}% of expected bars loaded");
}
```

### 2. Track CSV Parsing Errors
```csharp
var parsingErrors = new List<string>();
// ... parsing loop ...
if (parts.Length < 5) 
{
    parsingErrors.Add($"Line {lineNumber}: Insufficient columns");
    continue;
}
// Log aggregated parsing errors
```

### 3. Add Source File Validation
```csharp
if (!File.Exists(filePath))
{
    // Already handled, but could add more context
}
else
{
    var fileInfo = new FileInfo(filePath);
    var fileSize = fileInfo.Length;
    var lastModified = fileInfo.LastWriteTime;
    // Validate file size is reasonable (> 0, < max expected)
    // Validate modification time matches trading date
}
```

### 4. Add Data Integrity Checks
```csharp
// Calculate hash of loaded bars for verification
var barDataHash = CalculateBarDataHash(hydratedBars);
LogHealth("INFO", "PRE_HYDRATION_DATA_HASH", 
    new { hash = barDataHash, bar_count = hydratedBars.Count });
```

### 5. Improve Error Reporting
- Aggregate CSV parsing errors and report summary
- Log file read statistics (lines read, lines skipped, errors)
- Report completeness metrics in HYDRATION_SUMMARY

## Current Accuracy Assessment

### Overall Accuracy: **Good** (7/10)

**Strengths:**
- Robust deduplication prevents duplicate bars
- Time window filtering ensures relevant data only
- Comprehensive logging enables post-mortem analysis
- Gap detection identifies missing data

**Weaknesses:**
- No validation against expected completeness
- Silent parsing failures reduce visibility
- No data integrity verification
- Limited error reporting

### Accuracy by Mode

**DRYRUN Mode (CSV):** **Good** (6.5/10)
- File-based hydration is reliable when files exist
- Silent parsing failures reduce accuracy
- No completeness validation

**SIM Mode (BarsRequest):** **Very Good** (8/10)
- NinjaTrader API is authoritative source
- Future/partial bar filtering is robust
- Better error visibility than CSV mode

## Conclusion

The hydration method is **generally accurate** but has room for improvement:

1. **Immediate improvements**: Add expected bar count validation and parsing error tracking
2. **Medium-term**: Add data integrity checks and source file validation
3. **Long-term**: Implement completeness metrics and automated accuracy monitoring

The current implementation prioritizes **liveness** (ensuring system continues) over **completeness** (ensuring all data is loaded). This is acceptable for production but reduces accuracy visibility.
