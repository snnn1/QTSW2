# Exact Flow When You Press "Update Matrix (Rolling 35-Day Window)" Button

## Visual Changes (What You See)

### 1. **Button State Changes**
   - Button text changes: `"Update Matrix (Rolling 35-Day Window)"` → `"Updating..."`
   - Button becomes disabled (grayed out, can't click again)
   - Loading spinner may appear (depending on UI implementation)

### 2. **During Processing**
   - Button stays disabled
   - Text stays as `"Updating..."`
   - No other UI changes until completion

### 3. **After Completion**
   - Button text returns: `"Updating..."` → `"Update Matrix (Rolling 35-Day Window)"`
   - Button becomes enabled again
   - Matrix data table refreshes automatically with updated data
   - Any error messages appear in red above the table

---

## Backend Processing (What Happens Behind the Scenes)

### **Step 1: Frontend Sends API Request**
```
POST http://localhost:8000/api/matrix/update
Body: {
  "mode": "window",
  "stream_filters": {...}  // Your current filter settings
}
```

### **Step 2: Backend Receives Request**
- Logs: `"UPDATE ENDPOINT HIT!"`
- Validates request (mode must be "window")
- Reloads matrix modules to ensure latest code

### **Step 3: Load Latest Checkpoint**
- Reads `data/matrix/state/checkpoints/` directory
- Finds the most recent checkpoint file
- Extracts:
  - `checkpoint_date` (e.g., "2025-01-15")
  - `stream_states` (sequencer state for each stream):
    ```json
    {
      "ES1": {
        "current_time": "07:30",
        "current_session": "S1",
        "time_slot_histories": {
          "07:30": [1, -1, 0, ...],
          "08:00": [0, 1, -1, ...],
          ...
        }
      },
      ...
    }
    ```

**If no checkpoint exists:**
- Returns error: `"No checkpoint found. Please run a full rebuild first."`
- Button re-enables, error message appears

### **Step 4: Calculate Reprocess Start Date**
- Loads ALL merged data from `data/analyzed/`
- Counts unique trading days (data-driven, not calendar)
- Calculates: `reprocess_start_date = max_processed_date - 35 trading days`
- Example: If checkpoint is "2025-01-15", reprocess_start_date might be "2024-11-20"

**Logs:**
```
Latest checkpoint date: 2025-01-15
Reprocess start date: 2024-11-20
Merged data max date: 2025-01-20
```

**If insufficient history:**
- Returns error: `"Insufficient history: need 35 trading days back"`
- Button re-enables, error message appears

### **Step 5: Restore Sequencer State**
- Loads checkpoint state for each stream
- Restores:
  - `current_time` (which time slot each stream is using)
  - `current_session` (S1 or S2)
  - `time_slot_histories` (rolling window of scores for each time slot)

**Logs:**
```
Restored sequencer state from checkpoint {checkpoint_id}
```

### **Step 6: Purge Matrix Outputs**
- Loads existing matrix from `data/master_matrix/master_matrix_*.parquet`
- Finds all rows where `trade_date >= reprocess_start_date`
- Deletes those rows
- Keeps all rows before `reprocess_start_date` (untouched)

**Example:**
- Before: 10,000 rows (dates 2020-01-01 to 2025-01-20)
- After purge: 9,500 rows (dates 2020-01-01 to 2024-11-19)
- Removed: 500 rows (dates 2024-11-20 to 2025-01-20)

**Logs:**
```
Purged matrix outputs: 10000 -> 9500 rows (removed 500 rows)
```

### **Step 7: Reprocess Window Period**
- Loads ALL merged data (needed for accurate sequencer histories)
- Applies sequencer logic with **restored initial state**
- Processes data day-by-day, starting from restored state
- Continues sequencer logic forward through the window period
- Filters output to only include dates >= `reprocess_start_date`

**What the sequencer does:**
- For each stream, for each day:
  - Uses restored `current_time` and `time_slot_histories`
  - Selects trade for that day based on current time slot
  - Updates rolling histories
  - Decides if time should change (only after losses)
  - Outputs chosen trade

**Logs:**
```
Loaded 50000 trades for sequencer processing
Processing 12 streams with sequencer logic (capturing state)...
```

### **Step 8: Merge and Save**
- Merges purged old data + newly processed window data
- Sorts by: `trade_date`, `entry_time`, `Instrument`, `Stream`
- Updates `global_trade_id` for all rows
- Saves to `data/master_matrix/master_matrix_{timestamp}.parquet`
- Also saves JSON version

**Logs:**
```
Window update complete: 10000 total rows (500 new)
```

### **Step 9: Create New Checkpoint**
- Captures final sequencer state after processing
- Creates new checkpoint file:
  ```
  data/matrix/state/checkpoints/checkpoint_{uuid}.json
  ```
- Checkpoint contains:
  - `checkpoint_date`: Latest date processed (e.g., "2025-01-20")
  - `stream_states`: Final sequencer state for each stream

**Logs:**
```
Checkpoint {checkpoint_id} created successfully for date 2025-01-20
```

### **Step 10: Record Run History**
- Writes run summary to `data/matrix/state/run_history.jsonl`:
  ```json
  {
    "run_id": "uuid",
    "mode": "window_update",
    "timestamp": "2025-01-20T10:30:00Z",
    "requested_days": 35,
    "reprocess_start_date": "2024-11-20",
    "merged_data_max_date": "2025-01-20",
    "checkpoint_restore_id": "previous-checkpoint-id",
    "rows_read": 50000,
    "rows_written": 10000,
    "duration_seconds": 45.2,
    "success": true
  }
  ```

**Logs:**
```
WINDOW UPDATE COMPLETE: 10000 total rows, duration 45.20s
```

### **Step 11: Return Response to Frontend**
```json
{
  "status": "success",
  "message": "Master matrix updated successfully",
  "trades": 10000,
  "date_range": {
    "start": "2020-01-01",
    "end": "2025-01-20"
  },
  "streams": ["ES1", "ES2", "GC1", ...],
  "instruments": ["ES", "GC", "CL", ...],
  "run_id": "uuid",
  "reprocess_start_date": "2024-11-20",
  "rows_read": 50000,
  "rows_written": 10000,
  "duration_seconds": 45.2
}
```

### **Step 12: Frontend Reloads Matrix Data**
- Makes GET request: `GET /api/matrix/data?limit=0`
- Receives updated matrix data
- Updates the table display
- Button re-enables

---

## What Gets Changed vs. What Stays the Same

### ✅ **Gets Reprocessed (Changed)**
- All matrix rows with `trade_date >= reprocess_start_date`
- Sequencer state for those dates
- Time slot selections for those dates
- Rolling histories for those dates

### ✅ **Stays the Same (Untouched)**
- All matrix rows with `trade_date < reprocess_start_date`
- These are bitwise identical (no changes)
- Historical data is preserved exactly

---

## Example Timeline

**Before Update:**
- Checkpoint date: `2025-01-15`
- Matrix has data: `2020-01-01` to `2025-01-15`
- Total rows: `9,500`

**After Update:**
- Reprocess start: `2024-11-20` (35 trading days before checkpoint)
- Purged rows: `2024-11-20` to `2025-01-15` (removed)
- Reprocessed: `2024-11-20` to `2025-01-20` (new data)
- New checkpoint date: `2025-01-20`
- Matrix has data: `2020-01-01` to `2025-01-20`
- Total rows: `10,000`

**What changed:**
- Rows from `2024-11-20` to `2025-01-15` were reprocessed (may have different sequencer decisions)
- Rows from `2025-01-16` to `2025-01-20` are new (added)
- Rows before `2024-11-20` are unchanged

---

## Error Scenarios

### Error 1: No Checkpoint
**What you see:** Error message in red
**What happened:** No checkpoint exists (need to run full rebuild first)
**Fix:** Click "Rebuild Matrix (Full)" first

### Error 2: Insufficient History
**What you see:** Error message in red
**What happened:** Not enough trading days in merged data
**Fix:** Ensure you have at least 35 trading days of data

### Error 3: Backend Not Running
**What you see:** Connection error
**What happened:** Backend server is down
**Fix:** Start backend: `cd modules/dashboard/backend && python main.py`

### Error 4: Processing Error
**What you see:** Error message with details
**What happened:** Something went wrong during processing
**Fix:** Check backend logs for details

---

## Success Indicators

✅ **Button works correctly if:**
1. Button text changes to "Updating..." then back
2. Matrix table refreshes with updated data
3. No error messages appear
4. Backend logs show "WINDOW UPDATE COMPLETE"
5. New checkpoint file is created
6. Run history entry is written

---

## Time Estimates

- **Small dataset** (< 10,000 trades): 10-30 seconds
- **Medium dataset** (10,000-50,000 trades): 30-90 seconds
- **Large dataset** (> 50,000 trades): 90+ seconds

The time depends on:
- Number of streams
- Amount of data to reprocess
- System performance

