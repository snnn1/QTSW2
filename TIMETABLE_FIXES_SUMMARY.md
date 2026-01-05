# Timetable Authority Fixes - Summary

## Problem: Silent Omission vs Explicit Blocking

### Original Behavior (BUGGY)
- Streams that couldn't select a time were **silently omitted** from timetable
- Only `enabled=true` streams written to file
- Robot couldn't distinguish "blocked" from "error"
- **Result**: Missing streams (GC1, CL1, etc.) were absent from `timetable_current.json`

### New Behavior (CORRECT)
- **ALL 12 streams ALWAYS present** in timetable file
- Blocked streams have `enabled=false` with `block_reason`
- Complete execution contract - Robot sees full state
- **Result**: All streams present, explicit blocking state

---

## Code Changes Made

### 1. `generate_timetable()` - Fixed Silent Omission

**Before:**
```python
if selected_time is None:
    continue  # ❌ Stream disappears
```

**After:**
```python
if selected_time is None:
    # Use default time, mark as blocked
    selected_time = available_times[0] if available_times else ""
    block_reason = "no_rs_data"
    allowed = False
# ALWAYS append - never skip
```

### 2. `write_execution_timetable()` - Include All Streams

**Before:**
```python
if stream_data['enabled']:  # ❌ Only enabled streams
    streams.append({...})
```

**After:**
```python
# Include ALL streams - enabled and blocked
for stream_id, stream_data in all_streams.items():
    streams.append({
        'enabled': stream_data['enabled'],  # Can be False
        'block_reason': stream_data.get('block_reason')
    })
```

### 3. `write_execution_timetable_from_master_matrix()` - Ensure Completeness

**Before:**
- Only included streams from master matrix
- Missing streams were absent

**After:**
- First pass: Process streams from master matrix
- Second pass: **Ensure ALL 12 streams present**
- Missing streams added with `enabled=false`, `block_reason='not_in_master_matrix'`

### 4. Timetable Contract Schema

**Added Fields:**
- `block_reason` (string?): Why stream is blocked
- `decision_time` (string): Sequencer intent time (always present)

**C# Model Updated:**
```csharp
public string? BlockReason { get; set; }
public string DecisionTime { get; set; } = "";
```

---

## Key Differences from Original Rules

| Aspect | Original | New Directive |
|--------|----------|--------------|
| **Completeness** | Partial (only enabled) | Complete (all 12 streams) |
| **Missing Streams** | Silent omission | Explicit blocking |
| **Blocked Streams** | Absent from file | Present with `enabled=false` |
| **Robot Inference** | Must guess | Explicit state |
| **Safety** | ❌ Dangerous | ✅ Safe |

---

## Acceptance Tests

### ✅ Test 1: RS Calculation Failure
- Stream appears in timetable with `enabled=false`
- Has `block_reason="no_rs_data"`
- Has `decision_time` (default time slot)
- UI hides it (unless debug mode)

### ✅ Test 2: DOM/DOW Block Day
- Blocked streams appear in timetable with `block_reason`
- Blocked streams do NOT appear in UI (unless debug mode)

### ✅ Test 3: Monday After Friday
- Timetable JSON contains all 12 streams
- Some may have `enabled=false`
- UI shows only enabled streams

---

## UI Separation (Not Changed in This Fix)

**UI Must:**
- Filter out `enabled=false` streams for display
- Never modify timetable generation logic
- Never write back to timetable
- Optional: Show blocked streams in debug mode

**Timetable File:**
- Always contains all 12 streams
- Never filtered by UI logic
- Complete execution contract

---

## Files Modified

1. `modules/timetable/timetable_engine.py`
   - `generate_timetable()` - Never skip streams
   - `write_execution_timetable()` - Include all streams
   - `write_execution_timetable_from_master_matrix()` - Ensure completeness
   - `_write_execution_timetable_file()` - Add decision_time

2. `modules/robot/core/Models.TimetableContract.cs`
   - Added `BlockReason` field
   - Added `DecisionTime` field

---

## Impact

**Before Fix:**
- GC1, CL1, NQ1, NG1, NG2, YM2 missing from timetable
- Robot couldn't distinguish "blocked" from "error"
- Unsafe for execution systems

**After Fix:**
- All 12 streams always present
- Explicit blocking state for every stream
- Safe for execution systems
- Robot sees complete execution contract
