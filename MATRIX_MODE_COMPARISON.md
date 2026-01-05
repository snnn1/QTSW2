# Matrix Mode Comparison Assessment
**Date:** 2025-01-05  
**Status:** Evidence Gathering - No Code Changes

---

## Executive Summary

This document compares authoritative rebuild and window update modes to assess whether both are necessary. The goal is to determine if window update can be demoted or deprecated without loss of correctness.

---

## Mode Definitions

### Authoritative Rebuild (`build_master_matrix` with `authoritative=True`)

**Purpose:** Rebuild matrix to exactly match analyzer output, removing any rows not present in analyzer.

**Behavior:**
- Reads analyzer output from disk
- Builds trade keys from analyzer output
- Removes matrix rows whose keys are NOT in analyzer output
- Rebuilds entire matrix for specified date range
- Does NOT use checkpoints or sequencer state restoration
- Does NOT preserve historical data outside date range

**Use Cases:**
- Ensuring matrix exactly matches analyzer output
- Removing stale trades that no longer exist in analyzer
- Full rebuild after analyzer changes

**Code Path:** `modules/matrix/master_matrix.py` → `build_master_matrix(authoritative=True)`

---

### Window Update (`build_master_matrix_window_update`)

**Purpose:** Incremental update preserving historical data, using sequencer state restoration.

**Behavior:**
- Uses checkpoints to restore sequencer state
- Purges matrix outputs for dates >= reprocess_start_date
- Re-runs matrix build only for reprocess window (last N trading days)
- Preserves historical data outside the window
- Maintains sequencer state continuity across updates

**Use Cases:**
- Daily incremental updates
- Preserving historical data
- Maintaining sequencer state continuity
- Performance optimization (only reprocess recent days)

**Code Path:** `modules/matrix/master_matrix.py` → `build_master_matrix_window_update()`

---

## Key Differences

### 1. **Data Scope**

| Aspect | Authoritative Rebuild | Window Update |
|--------|----------------------|--------------|
| Date Range | Specified start/end date (or all) | Last N trading days (configurable) |
| Historical Data | Rebuilds entire range | Preserves data outside window |
| Stale Data Handling | Removes rows not in analyzer | May preserve stale rows outside window |

### 2. **Sequencer State**

| Aspect | Authoritative Rebuild | Window Update |
|--------|----------------------|--------------|
| State Restoration | No - starts fresh | Yes - restores from checkpoint |
| State Continuity | Breaks continuity | Maintains continuity |
| Checkpoint Dependency | None | Requires existing checkpoint |

### 3. **Performance**

| Aspect | Authoritative Rebuild | Window Update |
|--------|----------------------|--------------|
| Processing Scope | Full date range | Last N days only |
| Execution Time | Longer (full rebuild) | Shorter (incremental) |
| Resource Usage | Higher | Lower |

### 4. **Correctness Guarantees**

| Aspect | Authoritative Rebuild | Window Update |
|--------|----------------------|--------------|
| Analyzer Alignment | Exact match (removes stale rows) | May preserve stale rows outside window |
| Sequencer Accuracy | May differ if state matters | More accurate (state continuity) |
| Data Freshness | Always fresh | Fresh within window only |

---

## Expected Differences

### 1. **Row Count Differences**

**Expected:**
- Authoritative rebuild may have fewer rows (removes stale trades)
- Window update may have more rows (preserves historical data)

**Impact:** Low - Both are correct for their use cases

### 2. **Sequencer State Differences**

**Expected:**
- Authoritative rebuild starts sequencer state fresh
- Window update restores sequencer state from checkpoint

**Impact:** Medium - May affect time slot selection if sequencer state matters

### 3. **Date Range Differences**

**Expected:**
- Authoritative rebuild covers specified date range
- Window update covers last N trading days only

**Impact:** Low - Different use cases

---

## Historical Artifacts

### 1. **Stale Trade Preservation**

**Window Update:**
- Preserves trades outside reprocess window even if they no longer exist in analyzer
- This is a historical artifact - trades deleted from analyzer remain in matrix

**Authoritative Rebuild:**
- Removes stale trades immediately
- Matrix always matches analyzer output

**Assessment:** Window update preserves historical artifacts; authoritative rebuild removes them.

### 2. **Sequencer State Continuity**

**Window Update:**
- Maintains sequencer state across updates
- Time slot selection depends on historical state

**Authoritative Rebuild:**
- Breaks sequencer state continuity
- Time slot selection starts fresh

**Assessment:** Window update maintains historical continuity; authoritative rebuild breaks it.

---

## True Logic Differences

### 1. **Stale Data Removal**

**Authoritative Rebuild:**
- Explicitly removes rows not in analyzer output
- Guarantees matrix matches analyzer exactly

**Window Update:**
- Does NOT remove stale rows outside reprocess window
- May preserve rows that no longer exist in analyzer

**Assessment:** This is a true logic difference - authoritative rebuild is more correct for data freshness.

### 2. **Sequencer State Handling**

**Authoritative Rebuild:**
- Starts sequencer state fresh
- May produce different time slot selections if state matters

**Window Update:**
- Restores sequencer state from checkpoint
- Maintains historical continuity

**Assessment:** This is a true logic difference - window update is more correct for sequencer accuracy.

---

## Necessity Assessment

### Question: Can Authoritative Rebuild Fully Dominate Correctness?

**Answer: Partially - depends on use case**

**For Data Freshness:**
- ✅ **YES** - Authoritative rebuild is more correct
- Removes stale trades immediately
- Guarantees matrix matches analyzer exactly

**For Sequencer Accuracy:**
- ❌ **NO** - Window update is more correct
- Maintains sequencer state continuity
- Preserves historical context for time slot selection

**For Performance:**
- ❌ **NO** - Window update is more efficient
- Only reprocesses recent days
- Faster execution

---

## Recommendations

### Option 1: Make Authoritative Rebuild Default (Recommended)

**Rationale:**
- Analyzer contract enforcement ensures data quality
- Stale data removal is more important than sequencer state continuity
- Can run full authoritative rebuild periodically to reset state

**Implementation:**
- Make authoritative rebuild the default for UI "Update Matrix" button
- Keep window update as an advanced option
- Document when window update is required (performance optimization)

**Trade-offs:**
- May break sequencer state continuity
- Slower execution for large date ranges
- More correct for data freshness

### Option 2: Keep Both Modes (Current State)

**Rationale:**
- Different use cases require different modes
- Window update is faster for daily updates
- Authoritative rebuild is more correct for data freshness

**Implementation:**
- Keep both modes available
- Document when to use each mode
- Make authoritative rebuild default for "Refresh Data" button

**Trade-offs:**
- More complexity
- Users must choose correct mode
- Both modes must be maintained

### Option 3: Deprecate Window Update

**Rationale:**
- Authoritative rebuild is more correct
- Analyzer contract enforcement makes stale data less likely
- Simpler codebase

**Implementation:**
- Remove window update mode
- Always use authoritative rebuild
- Optimize authoritative rebuild for performance

**Trade-offs:**
- Loses sequencer state continuity
- Slower execution
- Simpler codebase

---

## Evidence Needed

To make a final recommendation, we need:

1. **Performance Comparison:**
   - Measure execution time for both modes on same date range
   - Compare resource usage

2. **Output Comparison:**
   - Run both modes on same date range
   - Compare row counts, hashes, key fields
   - Identify specific differences

3. **Sequencer State Impact:**
   - Determine if sequencer state continuity affects time slot selection
   - Measure impact on trade selection accuracy

4. **Usage Patterns:**
   - Analyze how often each mode is used
   - Identify primary use cases

---

## Implementation Status

### Authoritative Rebuild

**Status:** ⚠️ **PARTIALLY IMPLEMENTED**

**Current Implementation:**
- Parameter `authoritative=True` exists in `build_master_matrix()`
- However, the actual authoritative rebuild logic (removing rows not in analyzer output) appears to be missing
- The code path does not currently remove stale trades

**Required Implementation:**
- Add logic to build trade keys from analyzer output
- Compare with existing matrix trade keys
- Remove matrix rows whose keys are NOT in analyzer output
- This was planned but may not be fully implemented

**Recommendation:**
- Complete authoritative rebuild implementation before making it default
- Ensure it actually removes stale trades as intended

### Window Update

**Status:** ✅ **FULLY IMPLEMENTED**

**Current Implementation:**
- Fully functional with checkpoint restoration
- Preserves historical data outside reprocess window
- Maintains sequencer state continuity

---

## Next Steps

1. **Complete Authoritative Rebuild:**
   - Implement stale trade removal logic
   - Verify it removes rows not in analyzer output
   - Test with real data

2. **Run Comparison Script:**
   - Execute `compare_matrix_modes.py` on real data
   - Compare outputs for same date range
   - Document specific differences

3. **Performance Benchmarking:**
   - Measure execution time for both modes
   - Compare resource usage

4. **Usage Analysis:**
   - Review usage logs (if available)
   - Identify primary use cases

5. **Final Recommendation:**
   - Based on evidence, recommend default mode
   - Document when alternative mode is required

---

## Conclusion

**Current Assessment:**
- Authoritative rebuild is more correct for data freshness
- Window update is more correct for sequencer accuracy
- Both modes serve different use cases

**Recommendation:**
- Make authoritative rebuild the default for UI "Update Matrix" button
- Keep window update as advanced option for performance optimization
- Document when each mode should be used

**Status:** Evidence gathering complete - awaiting performance and output comparison results

---

**Last Updated:** 2025-01-05
