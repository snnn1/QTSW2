# DataExporter Integrity & Locking Improvements

## Overview

This document describes the critical improvements made to `DataExporter.cs` to address sequence-level integrity validation and deterministic locking mechanisms.

---

## 1. Sequence-Level Integrity Validation

### Problem
The original code only validated **individual bars** (NaN checks, OHLC relationships) but did not validate the **global sequence structure**. This is critical for quant work because:
- A single timestamp anomaly can invalidate volatility estimates
- Non-monotonic timestamps break compression logic
- Duplicate timestamps cause incorrect aggregation
- Negative durations indicate data corruption

### Solution: Added 4 Integrity Checks

#### 1.1 Duplicate Timestamp Detection
```csharp
private HashSet<DateTime> exportedTimestamps = new HashSet<DateTime>();
```

**Implementation:**
- Maintains a `HashSet` of all exported timestamps
- Before writing each bar, checks if timestamp already exists
- If duplicate found: **skips the bar** and increments `duplicateTimestampsDetected`
- Reports first 20 duplicates with full details

**Impact:**
- Prevents duplicate rows in output
- Critical for aggregation and analysis accuracy

#### 1.2 Non-Monotonic Timestamp Detection
```csharp
if (lastExportedTimestamp != DateTime.MinValue && exportTime < lastExportedTimestamp)
{
    nonMonotonicTimestampsDetected++;
    // Reports backward jump
}
```

**Implementation:**
- Tracks `lastExportedTimestamp` (the actual exported timestamp, not NinjaTrader bar time)
- Detects when current timestamp is **earlier** than previous
- Calculates backward jump duration
- **Continues processing** but flags the violation (may be legitimate session boundary)

**Impact:**
- Identifies data ordering issues
- Helps detect data source problems

#### 1.3 Negative Duration Detection
```csharp
TimeSpan duration = exportTime - lastExportedTimestamp;
if (duration.TotalMinutes < 0)
{
    negativeDurationsDetected++;
}
```

**Implementation:**
- Calculates duration between consecutive bars
- Flags negative durations (impossible for time series)
- Reports first 20 violations

**Impact:**
- Catches timestamp calculation errors
- Validates time series continuity

#### 1.4 Session Boundary Validation
```csharp
if (timeDiff.TotalHours < -4.0) // Large backward jump
{
    sessionBoundaryViolations++;
}
```

**Implementation:**
- Detects large backward time jumps (> 4 hours)
- Typically indicates session boundary (e.g., end of previous day's session)
- **Tracks but doesn't error** - this is expected behavior for futures data
- Reports first occurrence for visibility

**Impact:**
- Distinguishes legitimate session breaks from data errors
- Helps understand data structure

### Integrity Statistics

All integrity violations are tracked and reported:

**In Console:**
```
=== SEQUENCE INTEGRITY ===
Duplicate timestamps: 0
Non-monotonic timestamps: 0
Negative durations: 0
Session boundary violations: 2
```

**In Completion Signal JSON:**
```json
{
  "duplicateTimestamps": 0,
  "nonMonotonicTimestamps": 0,
  "negativeDurations": 0,
  "sessionBoundaryViolations": 2
}
```

**In Progress Signal JSON:**
- Same fields updated every 100,000 records

---

## 2. Deterministic Locking Mechanism

### Problem
The original code used **time-based duplicate prevention**:
- Checked if progress file updated < 300 seconds ago
- Checked if CSV file created < 120 seconds ago

**Issues:**
- **Not deterministic** - race conditions possible
- **Clock-dependent** - fails if system clock changes
- **No process tracking** - can't verify if process is actually running
- **No atomic operations** - multiple processes could start simultaneously

### Solution: File-Based Locking with PID Tracking

#### 2.1 Lock File Structure
```
DataExport_{INSTRUMENT}.lock
```

**Contents:** Process ID (PID) as plain text

**Location:** `C:\Users\jakej\QTSW2\data\raw\`

#### 2.2 Lock Acquisition (`AcquireLockFile()`)

**Process:**
1. **Check if lock exists:**
   - If exists, read PID from file
   - Check if process with that PID is still running
   - If process exists → **lock is valid, return false**
   - If process doesn't exist → **stale lock, delete it**

2. **Create lock file:**
   - Use `FileStream` with `FileShare.None` (exclusive access)
   - Write current process PID to file
   - Flush immediately
   - If creation fails → **another process has lock, return false**

**Key Features:**
- **Exclusive file access** prevents race conditions
- **PID tracking** allows stale lock detection
- **Atomic operation** via `FileMode.CreateNew`

#### 2.3 Lock Release (`ReleaseLockFile()`)

**Process:**
1. Close and dispose `FileStream`
2. Delete lock file
3. Handle errors gracefully (warnings only)

**Called:**
- On successful export completion
- On export errors/aborts
- In `State.Terminated` cleanup

#### 2.4 Stale Lock Detection

**Scenario:** Process crashes without releasing lock

**Solution:**
- Lock file contains PID
- Check if process with that PID exists
- If process doesn't exist → **stale lock, automatically removed**
- Allows recovery from crashes

**Example:**
```
Lock file exists with PID 12345
→ Check Process.GetProcessById(12345)
→ Process doesn't exist (crashed)
→ Delete stale lock file
→ Proceed with new export
```

---

## 3. Atomic "DONE" State

### Problem
No clear signal when export is **completely finished** and safe to process.

### Solution: Atomic File Rename

**Process:**
1. Export completes successfully
2. File stream closed and flushed
3. **Atomic rename:** `DataExport_ES_20250101_120000.csv` → `DataExport_ES_20250101_120000.csv.DONE`
4. Lock file released

**Benefits:**
- **Atomic operation** - either succeeds or fails, no partial state
- **Clear signal** - `.DONE` extension indicates completion
- **Safe for downstream processing** - translator can check for `.DONE` files
- **No race conditions** - rename is atomic on Windows

**Usage:**
```csharp
// Downstream code can check:
string doneFile = csvFile + ".DONE";
if (File.Exists(doneFile))
{
    // Export is complete, safe to process
}
```

---

## 4. Error Handling Improvements

### Lock File Release on Errors

**Added:**
- Lock file released if file creation fails
- Lock file released if stream becomes null
- Lock file released in `State.Terminated` cleanup

**Prevents:**
- Orphaned lock files blocking future exports
- Manual intervention required after errors

---

## 5. Code Changes Summary

### New Fields
```csharp
// Locking
private string lockFilePath;
private FileStream lockFileStream;

// Sequence integrity
private DateTime lastExportedTimestamp = DateTime.MinValue;
private HashSet<DateTime> exportedTimestamps = new HashSet<DateTime>();
private int duplicateTimestampsDetected = 0;
private int nonMonotonicTimestampsDetected = 0;
private int negativeDurationsDetected = 0;
private int sessionBoundaryViolations = 0;
private DateTime? sessionStartTime = null;
private DateTime? sessionEndTime = null;
```

### New Methods
- `AcquireLockFile()` - Deterministic lock acquisition
- `ReleaseLockFile()` - Lock cleanup

### Modified Methods
- `StartExport()` - Now uses lock file instead of time-based checks
- `OnBarUpdate()` - Added sequence integrity validation
- `OnStateChange()` - Added lock release and atomic rename

### Removed
- Time-based duplicate prevention (lines 267-303 in original)
- Progress file time checks
- Recent file creation time checks

---

## 6. Integration Impact

### Downstream Processing

**Translator/Conductor:**
- Can check for `.DONE` files to ensure export is complete
- Can verify lock file status before triggering export
- Can read integrity statistics from completion signals

**Example:**
```python
# Check if export is done
done_files = list(raw_path.glob("DataExport_ES_*.csv.DONE"))
if done_files:
    # Safe to process
    csv_file = done_files[0].stem  # Remove .DONE
    process_file(csv_file)
```

### Signal Files

**Completion Signal now includes:**
```json
{
  "duplicateTimestamps": 0,
  "nonMonotonicTimestamps": 0,
  "negativeDurations": 0,
  "sessionBoundaryViolations": 2
}
```

**Progress Signal now includes:**
- Same integrity fields (updated every 100k records)

---

## 7. Testing Recommendations

### Test Sequence Integrity
1. **Duplicate timestamps:** Inject duplicate bar in test data
2. **Non-monotonic:** Test with out-of-order bars
3. **Negative duration:** Test with timestamp calculation errors
4. **Session boundaries:** Test with multi-day data

### Test Locking
1. **Concurrent exports:** Try to start two exports simultaneously
2. **Stale lock:** Kill process mid-export, verify lock cleanup
3. **Lock persistence:** Verify lock file exists during export
4. **Lock release:** Verify lock removed on completion

### Test Atomic Rename
1. **Completion:** Verify `.DONE` file created
2. **Partial failure:** Test behavior if rename fails
3. **Downstream:** Verify translator can detect `.DONE` files

---

## 8. Migration Notes

### Breaking Changes
- **Lock files:** New `.lock` files will be created
- **DONE files:** Completed exports will have `.DONE` extension
- **Signal format:** Completion signals include new integrity fields

### Backward Compatibility
- Old CSV files (without `.DONE`) are still readable
- Progress signals still work (new fields are additive)
- No changes to CSV format itself

### Cleanup
- Old time-based checks removed (no longer needed)
- Stale lock files auto-cleaned (if process dead)
- Manual cleanup: Delete `.lock` files if needed

---

## 9. Performance Impact

### Memory
- `HashSet<DateTime>` for duplicate detection: ~24 bytes per timestamp
- For 1M bars: ~24 MB (acceptable)
- Can be optimized with bloom filter if needed

### CPU
- Hash set lookup: O(1) average case
- PID check: Minimal (single process lookup)
- Overall: Negligible impact

### Disk I/O
- Lock file: Single write at start, single delete at end
- Atomic rename: Single operation at completion
- Overall: Minimal overhead

---

## 10. Future Enhancements

### Potential Improvements
1. **Bloom filter** for duplicate detection (reduce memory for very large exports)
2. **Lock file timeout** (auto-release after N hours)
3. **Integrity report file** (detailed log of all violations)
4. **Session boundary detection** (automatic session start/end identification)
5. **Compression support** (gzip CSV files before `.DONE` rename)

---

## Summary

### What Changed
✅ **Sequence-level integrity validation** - 4 new checks  
✅ **Deterministic locking** - PID-based lock files  
✅ **Atomic DONE state** - File rename on completion  
✅ **Error handling** - Lock cleanup on all error paths  
✅ **Statistics tracking** - Integrity metrics in signals  

### Why It Matters
- **Quant work requires** perfect data integrity
- **Single timestamp anomaly** can invalidate entire analysis
- **Deterministic locking** prevents race conditions
- **Atomic operations** ensure consistent state
- **Comprehensive validation** catches data issues early

### Result
The DataExporter now provides **production-grade data integrity** suitable for quantitative trading systems.


