# Timetable Authority: Original vs New Directive Comparison

## CRITICAL DIFFERENCE: Silent Omission vs Explicit Blocking

### ORIGINAL BEHAVIOR (Current Code - BUGGY)

**Problem: Silent Omission**
- Streams that can't select a time are **completely omitted** from timetable file
- Line 328-329: `if selected_time is None: continue` → stream disappears
- Line 628-638: Only `enabled=True` streams written to file
- **Result**: Missing streams are **absent** from `timetable_current.json`

**Example (Original Bug):**
```json
{
  "streams": [
    {"stream": "ES1", "enabled": true},
    {"stream": "ES2", "enabled": true}
    // GC1 is MISSING - not present at all
  ]
}
```

**Why This Is Dangerous:**
- Robot can't distinguish "not tradable today" from "system error"
- No way to know if GC1 was filtered or if generation failed
- Execution system must infer/guesstimate what's missing

---

### NEW DIRECTIVE (Required Behavior)

**Solution: Explicit Blocking**
- **ALL 12 streams MUST always be present** in timetable file
- Streams that can't trade are **explicitly blocked** with `enabled=false`
- Must include `block_reason` explaining why
- Must include `decision_time` (sequencer intent, even if blocked)

**Example (Correct Behavior):**
```json
{
  "streams": [
    {"stream": "ES1", "enabled": true, "slot_time": "07:30"},
    {"stream": "ES2", "enabled": true, "slot_time": "11:00"},
    {"stream": "GC1", "enabled": false, "slot_time": "09:00", 
     "block_reason": "no_rs_data", "decision_time": "09:00"},
    {"stream": "GC2", "enabled": false, "slot_time": "09:30",
     "block_reason": "dom_blocked_5", "decision_time": "09:30"}
  ]
}
```

**Why This Is Safe:**
- Robot sees complete execution contract
- Can distinguish "blocked" from "error"
- No inference required - explicit state for every stream

---

## CODE CHANGES REQUIRED

### 1. `generate_timetable()` - Line 328-329 (CRITICAL FIX)

**OLD CODE:**
```python
selected_time, time_reason = self.select_best_time(stream_id, session)
if selected_time is None:
    continue  # ❌ SILENT OMISSION - FORBIDDEN
```

**NEW CODE:**
```python
selected_time, time_reason = self.select_best_time(stream_id, session)

# If time selection fails, use default time and mark as blocked
if selected_time is None:
    # Use first available time slot as default (sequencer intent)
    available_times = self.session_time_slots.get(session, [])
    selected_time = available_times[0] if available_times else None
    block_reason = "no_rs_data"
    allowed = False
else:
    block_reason = None
    # Check filters normally
    allowed, filter_reason = self.check_filters(...)
    if not allowed:
        block_reason = filter_reason

# ALWAYS append - never skip
timetable_rows.append({
    'stream_id': stream_id,
    'session': session,
    'selected_time': selected_time,
    'allowed': allowed,
    'block_reason': block_reason,
    'reason': time_reason or block_reason
})
```

### 2. `write_execution_timetable()` - Line 628-638 (CRITICAL FIX)

**OLD CODE:**
```python
# Only include streams that are enabled (enabled=true)
for stream_id, stream_data in enabled_streams.items():
    if stream_data['enabled']:  # ❌ FILTERS OUT BLOCKED STREAMS
        streams.append({...})
```

**NEW CODE:**
```python
# Include ALL streams - enabled and blocked
for stream_id, stream_data in enabled_streams.items():
    streams.append({
        'stream': stream_id,
        'instrument': instrument,
        'session': stream_data['session'],
        'slot_time': stream_data['slot_time'],
        'enabled': stream_data['enabled'],  # Can be False
        'block_reason': stream_data.get('block_reason')  # If blocked
    })
```

### 3. Timetable Contract Schema Update

**Add to `TimetableStream` model:**
```csharp
public string? BlockReason { get; set; }  // Why blocked (if enabled=false)
public string DecisionTime { get; set; }   // Sequencer intent time
```

---

## UI SEPARATION (Presentation Layer)

### UI Must Filter (Not Timetable)

**UI Code Should:**
```javascript
// Filter out disabled streams for display
const visibleStreams = timetable.streams.filter(s => s.enabled === true);

// Optional: Debug mode to show blocked streams
if (showBlockedStreams) {
  // Show all streams with block_reason displayed
}
```

**Timetable File:**
- Always contains all 12 streams
- Never filtered by UI logic
- Complete execution contract

---

## ACCEPTANCE TESTS

### Test 1: Monday After Friday
- ✅ Timetable JSON contains all 12 streams
- ✅ Some may have `enabled=false`
- ✅ UI shows only enabled streams

### Test 2: DOM/DOW Block Day
- ✅ Blocked streams appear in timetable with `block_reason`
- ✅ Blocked streams do NOT appear in UI (unless debug mode)

### Test 3: RS Calculation Failure
- ✅ Stream still appears in timetable
- ✅ Has `enabled=false`, `block_reason="no_rs_data"`
- ✅ Has `decision_time` (default time slot)
- ✅ UI hides it (unless debug mode)

---

## SUMMARY

| Aspect | Original (Buggy) | New (Correct) |
|--------|------------------|---------------|
| **Missing Streams** | Silent omission | Explicit blocking |
| **File Completeness** | Partial (only enabled) | Complete (all 12 streams) |
| **Blocked Streams** | Absent | Present with `enabled=false` |
| **Robot Inference** | Must guess | Explicit state |
| **UI Filtering** | Mixed with generation | Separate presentation layer |
| **Safety** | ❌ Dangerous | ✅ Safe |
