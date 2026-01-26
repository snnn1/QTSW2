# Timetable Engine: Complete Summary with Examples and Timeline

## Overview

The **Timetable Engine** is the bridge between historical analysis (Master Matrix) and live execution (Robot). It generates `timetable_current.json` - the single source of truth that tells NinjaTrader which trades to execute today.

**Core Purpose**: Transform sequencer decisions from the Master Matrix into an execution contract that the Robot can read and execute.

---

## What It Does

### Primary Function

The Timetable Engine takes the latest date's data from the Master Matrix and creates a complete execution plan for all 14 streams:

1. **Reads Master Matrix** - Gets sequencer decisions (which time slot each stream should use)
2. **Applies Filters** - Checks DOW/DOM/exclude_times/SCF filters to determine if trades are enabled
3. **Ensures Completeness** - Guarantees all 14 streams are present (enabled or blocked)
4. **Writes Execution Contract** - Creates `timetable_current.json` with complete execution plan
5. **Robot Reads It** - NinjaTrader Robot polls this file to know what to trade

### Key Characteristics

- **Fail-Closed Design**: Raises `ValueError` on contract violations (no silent failures)
- **Strict Consumer**: Validates `trade_date` but never normalizes dates (DataLoader owns normalization)
- **Complete Contract**: Always includes all 14 streams, even if blocked
- **Atomic Writes**: Uses temp file + rename to prevent partial reads

---

## Architecture

### Two Generation Paths

#### 1. Backend Path (Authoritative - Used by Robot)

**File**: `modules/timetable/timetable_engine.py`

**When Called**:
- Automatically when master matrix is saved (`file_manager.py` line 66)
- Via API endpoint: `POST /api/timetable/generate`
- Via command line: `python -m modules.timetable.timetable_engine --date 2024-01-15`

**Method**: `write_execution_timetable_from_master_matrix()`

**Source**: Master Matrix DataFrame (latest date)

**Output**: `data/timetable/timetable_current.json`

**Used By**: Robot engine (NinjaTrader) reads this file every few seconds

#### 2. Frontend Path (Display Only - Not Used by Robot)

**File**: `modules/matrix_timetable_app/frontend/src/matrixWorker.js`

**When Called**: Calculated on-demand in browser when user views timetable tab

**Method**: `CALCULATE_TIMETABLE` message handler

**Source**: Columnar matrix data loaded in browser

**Output**: Returns timetable array to UI for display

**Used By**: Matrix/Timetable UI app (for visualization only)

**⚠️ Important**: Only the backend path matters for Robot execution!

---

## How It Works: Step-by-Step

### Phase 1: Input Validation

```
1. Master Matrix DataFrame arrives
   ↓
2. CONTRACT CHECK: Require trade_date column
   - If missing → raise ValueError (fail-closed)
   ↓
3. Validate trade_date dtype and presence
   - Uses _validate_trade_date_dtype()
   - Uses _validate_trade_date_presence()
   ↓
4. Extract latest date (or use provided trade_date)
   - latest_date = master_matrix_df['trade_date'].max()
   ↓
5. Filter to latest date rows
   - latest_df = master_matrix_df[master_matrix_df['trade_date'].dt.date == trade_date_obj]
```

**Example**:
```python
# Input: Master Matrix DataFrame
master_matrix_df = pd.DataFrame({
    'Stream': ['ES1', 'ES2', 'GC1'],
    'trade_date': pd.to_datetime(['2024-01-15', '2024-01-15', '2024-01-15']),
    'Time': ['08:00', '09:30', '07:30'],
    'Time Change': ['08:00', '', '08:00'],
    'final_allowed': [True, False, True]
})

# After validation and filtering:
latest_df = master_matrix_df[master_matrix_df['trade_date'].dt.date == date(2024, 1, 15)]
# Result: All 3 rows (for 2024-01-15)
```

### Phase 2: Stream Processing (First Pass)

```
For each row in latest_df:
  1. Extract stream_id (e.g., "ES1")
  2. Extract session (use Session column if available, else infer from stream_id)
  3. Extract time:
     - If Time Change exists → use Time Change (parse if "old -> new" format)
     - Else → use Time column
     - Validate time is in session_time_slots
  4. Check final_allowed column (if exists):
     - If False → mark as blocked
  5. Apply filters (if no final_allowed):
     - DOW filter (day-of-week)
     - DOM filter (day-of-month)
     - exclude_times filter
  6. Add to streams_dict
```

**Example**:
```python
# Row 1: ES1
stream = 'ES1'
session = 'S1'  # From Session column or inferred from stream_id
time = '08:00'  # From Time Change column
enabled = True  # From final_allowed column
block_reason = None

# Row 2: ES2
stream = 'ES2'
session = 'S2'
time = '09:30'  # From Time column (Time Change empty)
enabled = False  # From final_allowed column
block_reason = 'master_matrix_filtered_False'

# Result after first pass:
streams_dict = {
    'ES1': {'stream': 'ES1', 'session': 'S1', 'slot_time': '08:00', 'enabled': True},
    'ES2': {'stream': 'ES2', 'session': 'S2', 'slot_time': '09:30', 'enabled': False, 'block_reason': '...'}
}
```

### Phase 3: Missing Stream Handling (Second Pass)

```
For each stream_id in self.streams (all 14 streams):
  If stream_id NOT in streams_dict:
    1. Stream missing from master matrix
    2. Select default time:
       - Collect exclude_times from filters
       - Find first NON-FILTERED time slot
       - If all times filtered → raise ValueError
    3. Add to streams_dict with enabled=False
```

**Example**:
```python
# All 14 streams: ES1, ES2, GC1, GC2, CL1, CL2, NQ1, NQ2, NG1, NG2, YM1, YM2, RTY1, RTY2

# After first pass, only ES1, ES2, GC1 found
# Second pass adds missing streams:

# CL1 missing → add with default time
stream_id = 'CL1'
session = 'S1'
available_times = ['07:30', '08:00', '09:00']  # S1 time slots
exclude_times = ['07:30']  # From filters
default_time = '08:00'  # First non-filtered time

streams_dict['CL1'] = {
    'stream': 'CL1',
    'session': 'S1',
    'slot_time': '08:00',
    'enabled': False,
    'block_reason': 'not_in_master_matrix'
}
```

### Phase 4: CME Trading Date Rollover

```
1. Get current Chicago time
2. Calculate trading_date:
   - If Chicago hour >= 17:00 → trading_date = next calendar day
   - Else → trading_date = current calendar day
3. Validate rollover logic:
   - If hour >= 17:00 but trading_date == calendar_date → raise ValueError
```

**Example**:
```python
# Scenario 1: Before 17:00 Chicago time
chicago_now = datetime(2024, 1, 15, 14, 30, tzinfo=chicago_tz)  # 2:30 PM
chicago_hour = 14
trading_date = '2024-01-15'  # Same day

# Scenario 2: After 17:00 Chicago time
chicago_now = datetime(2024, 1, 15, 17, 30, tzinfo=chicago_tz)  # 5:30 PM
chicago_hour = 17
trading_date = '2024-01-16'  # Next day (rollover applied)

# Scenario 3: Contract violation (should never happen)
chicago_hour = 18
trading_date = '2024-01-15'  # Still same day
# → raise ValueError("CME_TRADING_DATE_ROLLOVER_CONTRACT_VIOLATION")
```

### Phase 5: Write Execution Timetable

```
1. Build execution_timetable document:
   {
     'as_of': '2024-01-15T17:30:00-06:00',
     'trading_date': '2024-01-16',
     'timezone': 'America/Chicago',
     'source': 'master_matrix',
     'streams': [
       {'stream': 'ES1', 'instrument': 'ES', 'session': 'S1', 'slot_time': '08:00', 'enabled': True},
       {'stream': 'ES2', 'instrument': 'ES', 'session': 'S2', 'slot_time': '09:30', 'enabled': False, 'block_reason': '...'},
       ...
     ]
   }

2. Atomic write:
   - Write to temp file: timetable_current.tmp
   - Rename to: timetable_current.json
   - Robot reads this file
```

**Example Output** (`timetable_current.json`):
```json
{
  "as_of": "2024-01-15T17:30:00-06:00",
  "trading_date": "2024-01-16",
  "timezone": "America/Chicago",
  "source": "master_matrix",
  "streams": [
    {
      "stream": "ES1",
      "instrument": "ES",
      "session": "S1",
      "slot_time": "08:00",
      "decision_time": "08:00",
      "enabled": true
    },
    {
      "stream": "ES2",
      "instrument": "ES",
      "session": "S2",
      "slot_time": "09:30",
      "decision_time": "09:30",
      "enabled": false,
      "block_reason": "master_matrix_filtered_False"
    },
    {
      "stream": "CL1",
      "instrument": "CL",
      "session": "S1",
      "slot_time": "08:00",
      "decision_time": "08:00",
      "enabled": false,
      "block_reason": "not_in_master_matrix"
    }
    // ... all 14 streams
  ]
}
```

---

## Complete Timeline

### Daily Workflow

```
┌─────────────────────────────────────────────────────────────────┐
│ DAY 1: Data Collection & Analysis                              │
└─────────────────────────────────────────────────────────────────┘

00:00 - 16:59 Chicago Time
├─ Analyzer processes historical trades
├─ Analyzer writes output files to data/analyzed/
└─ DataLoader normalizes dates → trade_date column

17:00 Chicago Time (CME Rollover)
├─ Trading date rolls over to next calendar day
└─ Example: 2024-01-15 17:00 → trading_date = 2024-01-16

Evening (After Market Close)
├─ Master Matrix builds historical analysis
├─ Sequencer selects optimal time slots for each stream
├─ Master Matrix saves → triggers Timetable Engine
└─ Timetable Engine writes timetable_current.json

┌─────────────────────────────────────────────────────────────────┐
│ DAY 2: Execution Day                                            │
└─────────────────────────────────────────────────────────────────┘

Pre-Market (Before 07:30 Chicago Time)
├─ Robot starts up
├─ Robot polls timetable_current.json every 5 seconds
├─ Robot reads execution plan:
│  ├─ ES1: enabled=true, slot_time=08:00
│  ├─ ES2: enabled=false, block_reason=...
│  └─ ... (all 14 streams)
└─ Robot prepares for execution

07:30 Chicago Time (S1 Session Start)
├─ Robot checks timetable for S1 streams
├─ ES1: enabled=true → Robot executes at 08:00
├─ GC1: enabled=false → Robot skips
└─ CL1: enabled=false → Robot skips

08:00 Chicago Time (ES1 Execution)
├─ Robot executes ES1 trade
├─ Robot logs execution to journal
└─ Robot continues monitoring for next time slot

09:30 Chicago Time (S2 Session Start)
├─ Robot checks timetable for S2 streams
├─ ES2: enabled=false → Robot skips
└─ GC2: enabled=true → Robot executes at 10:00

10:00 Chicago Time (GC2 Execution)
├─ Robot executes GC2 trade
└─ Robot logs execution

Throughout Day
├─ Robot continues monitoring timetable
├─ Robot executes enabled trades at their slot_time
└─ Robot skips blocked trades

17:00 Chicago Time (CME Rollover)
├─ Trading date rolls over again
└─ Process repeats for next day
```

### Detailed Execution Timeline Example

**Date**: January 15, 2024

```
┌─────────────────────────────────────────────────────────────────┐
│ January 14, 2024 (Evening)                                      │
└─────────────────────────────────────────────────────────────────┘

20:00 CT - Master Matrix Build
├─ Analyzer output files loaded
├─ Sequencer analyzes last 13 trades per stream
├─ Sequencer selects best time slots:
│  ├─ ES1: 08:00 (RS=+5, best performing)
│  ├─ ES2: 09:30 (RS=-2, but default for S2)
│  ├─ GC1: 07:30 (RS=+3)
│  └─ GC2: 10:00 (RS=+1)
├─ Filters applied:
│  ├─ ES2: DOM filter blocks (day 14 in blocked days)
│  └─ GC1: exclude_times filter blocks 07:30
└─ Master Matrix saved

20:01 CT - Timetable Engine Triggered
├─ write_execution_timetable_from_master_matrix() called
├─ Reads Master Matrix latest date (2024-01-15)
├─ Processes streams:
│  ├─ ES1: Time=08:00, enabled=True
│  ├─ ES2: Time=09:30, enabled=False (DOM filter)
│  ├─ GC1: Time Change=08:00, enabled=True (07:30 filtered)
│  └─ GC2: Time=10:00, enabled=True
├─ Adds missing streams (CL1, CL2, NQ1, etc.) with defaults
├─ Calculates trading_date: 2024-01-15 (before 17:00)
└─ Writes timetable_current.json

┌─────────────────────────────────────────────────────────────────┐
│ January 15, 2024 (Execution Day)                               │
└─────────────────────────────────────────────────────────────────┘

06:00 CT - Robot Startup
├─ Robot initializes
├─ Robot starts polling timetable_current.json (every 5 seconds)
└─ Robot reads execution plan

07:30 CT - S1 Session Start
├─ Robot checks timetable for S1 streams:
│  ├─ ES1: enabled=True, slot_time=08:00 → Will execute
│  ├─ GC1: enabled=True, slot_time=08:00 → Will execute
│  ├─ CL1: enabled=False, block_reason=not_in_master_matrix → Skip
│  └─ NQ1: enabled=False → Skip
└─ Robot prepares for 08:00 executions

08:00 CT - ES1 & GC1 Execution
├─ Robot executes ES1 trade
│  ├─ Entry: 08:00:00 CT
│  ├─ Instrument: ES
│  └─ Stream: ES1
├─ Robot executes GC1 trade
│  ├─ Entry: 08:00:00 CT
│  ├─ Instrument: GC
│  └─ Stream: GC1
└─ Robot logs both executions

09:30 CT - S2 Session Start
├─ Robot checks timetable for S2 streams:
│  ├─ ES2: enabled=False, block_reason=dom_blocked_14 → Skip
│  ├─ GC2: enabled=True, slot_time=10:00 → Will execute
│  └─ CL2: enabled=False → Skip
└─ Robot prepares for 10:00 execution

10:00 CT - GC2 Execution
├─ Robot executes GC2 trade
│  ├─ Entry: 10:00:00 CT
│  ├─ Instrument: GC
│  └─ Stream: GC2
└─ Robot logs execution

17:00 CT - CME Rollover
├─ Trading date rolls over to 2024-01-16
├─ Master Matrix builds for next day
└─ Timetable Engine generates new timetable_current.json
```

---

## Key Components

### 1. RS (Rolling Sum) Calculation

**Purpose**: Calculate performance scores for each time slot to select the best one.

**Method**: `calculate_rs_for_stream(stream_id, session, lookback_days=13)`

**Scoring**:
- Win = +1 point
- Loss = -2 points
- BE/Time/No Trade = 0 points
- Rolling sum over last 13 trades per time slot

**Example**:
```python
# ES1 S1 time slot "08:00" last 13 trades:
trades = [
    {'Result': 'WIN'},   # +1
    {'Result': 'WIN'},   # +1
    {'Result': 'LOSS'},  # -2
    {'Result': 'BE'},    # 0
    {'Result': 'WIN'},   # +1
    # ... 8 more trades
]

# RS calculation:
rs_value = sum([+1, +1, -2, 0, +1, ...]) = +5

# Result: RS value for "08:00" = +5
```

### 2. Time Selection

**Purpose**: Select the best time slot based on RS values.

**Method**: `select_best_time(stream_id, session)`

**Logic**:
- Find time slot with highest RS value
- If all RS values <= 0 → use first available time slot
- Returns: `(selected_time, reason)`

**Example**:
```python
# RS values for ES1 S1:
rs_values = {
    '07:30': -3,  # Poor performance
    '08:00': +5,  # Best performance
    '09:00': +2   # Good performance
}

# Result: selected_time = '08:00', reason = 'RS_best_time'
```

### 3. Filter Checking

**Purpose**: Determine if a trade should be allowed based on filters.

**Method**: `check_filters(trade_date, stream_id, session, scf_s1, scf_s2)`

**Filters Applied**:
1. **DOM Filter**: Blocks "2" streams on specific days of month
2. **SCF Filter**: Blocks if SCF value >= threshold (default 0.5)
3. **DOW Filter**: Blocks specific days of week (if configured)
4. **exclude_times Filter**: Blocks specific time slots (if configured)

**Example**:
```python
# ES2 on January 14, 2024
trade_date = date(2024, 1, 14)
stream_id = 'ES2'  # Ends with '2' → subject to DOM filter
day_of_month = 14

# DOM filter check:
if stream_id.endswith('2') and day_of_month in dom_blocked_days:
    return False, 'dom_blocked_14'

# Result: enabled = False, block_reason = 'dom_blocked_14'
```

### 4. SCF (Stream Configuration Factor) Lookup

**Purpose**: Get SCF values for a stream on a specific date.

**Method**: `get_scf_values(stream_id, trade_date)`

**Returns**: `(scf_s1, scf_s2)` or `(None, None)` if not found

**Example**:
```python
# Get SCF values for ES1 on 2024-01-15
scf_s1, scf_s2 = get_scf_values('ES1', date(2024, 1, 15))

# Result: scf_s1 = 0.3, scf_s2 = 0.7

# Filter check:
if scf_s1 >= 0.5:  # 0.3 < 0.5 → allowed
    return False, 'scf_blocked'
else:
    return True, 'allowed'
```

---

## Examples

### Example 1: Normal Execution Day

**Input**: Master Matrix DataFrame for 2024-01-15

```python
master_matrix_df = pd.DataFrame({
    'Stream': ['ES1', 'ES2', 'GC1', 'GC2'],
    'trade_date': pd.to_datetime(['2024-01-15'] * 4),
    'Time': ['08:00', '09:30', '07:30', '10:00'],
    'Time Change': ['08:00', '', '08:00', ''],
    'final_allowed': [True, True, True, True],
    'Session': ['S1', 'S2', 'S1', 'S2']
})
```

**Processing**:
1. Extract latest date: 2024-01-15
2. Process streams:
   - ES1: Time=08:00, enabled=True
   - ES2: Time=09:30, enabled=True
   - GC1: Time Change=08:00 (overrides Time=07:30), enabled=True
   - GC2: Time=10:00, enabled=True
3. Add missing streams (CL1, CL2, NQ1, etc.) with defaults
4. Write timetable_current.json

**Output**: `timetable_current.json`
```json
{
  "as_of": "2024-01-15T20:01:00-06:00",
  "trading_date": "2024-01-15",
  "streams": [
    {"stream": "ES1", "slot_time": "08:00", "enabled": true},
    {"stream": "ES2", "slot_time": "09:30", "enabled": true},
    {"stream": "GC1", "slot_time": "08:00", "enabled": true},
    {"stream": "GC2", "slot_time": "10:00", "enabled": true},
    {"stream": "CL1", "slot_time": "08:00", "enabled": false, "block_reason": "not_in_master_matrix"},
    // ... all 14 streams
  ]
}
```

### Example 2: Filtered Execution Day

**Input**: Master Matrix DataFrame for 2024-01-14 (day 14, ES2 blocked by DOM filter)

```python
master_matrix_df = pd.DataFrame({
    'Stream': ['ES1', 'ES2', 'GC1'],
    'trade_date': pd.to_datetime(['2024-01-14'] * 3),
    'Time': ['08:00', '09:30', '07:30'],
    'final_allowed': [True, False, True]  # ES2 blocked by DOM filter
})
```

**Processing**:
1. Extract latest date: 2024-01-14
2. Process streams:
   - ES1: Time=08:00, enabled=True
   - ES2: Time=09:30, enabled=False (final_allowed=False)
   - GC1: Time=07:30, enabled=True
3. Check filters:
   - ES2: DOM filter blocks (day 14 in blocked days)
4. Add missing streams

**Output**: `timetable_current.json`
```json
{
  "streams": [
    {"stream": "ES1", "slot_time": "08:00", "enabled": true},
    {"stream": "ES2", "slot_time": "09:30", "enabled": false, "block_reason": "dom_blocked_14"},
    {"stream": "GC1", "slot_time": "07:30", "enabled": true},
    // ... all 14 streams
  ]
}
```

### Example 3: Missing Stream Handling

**Input**: Master Matrix DataFrame with only ES1 and ES2

```python
master_matrix_df = pd.DataFrame({
    'Stream': ['ES1', 'ES2'],
    'trade_date': pd.to_datetime(['2024-01-15'] * 2),
    'Time': ['08:00', '09:30'],
    'final_allowed': [True, True]
})
```

**Processing**:
1. First pass: Process ES1 and ES2
2. Second pass: Add missing streams (GC1, GC2, CL1, CL2, etc.)
   - For each missing stream:
     - Select first non-filtered time slot
     - Mark as enabled=False
     - Set block_reason='not_in_master_matrix'

**Output**: `timetable_current.json`
```json
{
  "streams": [
    {"stream": "ES1", "slot_time": "08:00", "enabled": true},
    {"stream": "ES2", "slot_time": "09:30", "enabled": true},
    {"stream": "GC1", "slot_time": "08:00", "enabled": false, "block_reason": "not_in_master_matrix"},
    {"stream": "GC2", "slot_time": "09:30", "enabled": false, "block_reason": "not_in_master_matrix"},
    // ... all 14 streams
  ]
}
```

### Example 4: Contract Violation (Fail-Closed)

**Input**: Master Matrix DataFrame missing `trade_date` column

```python
master_matrix_df = pd.DataFrame({
    'Stream': ['ES1'],
    'Date': ['2024-01-15'],  # Wrong column name
    'Time': ['08:00']
})
```

**Processing**:
1. Contract check: `trade_date` column missing
2. **Raises ValueError**: "Master matrix missing trade_date column - DataLoader must normalize dates before timetable generation. This is a contract violation. Timetable Engine does not normalize dates."

**Result**: Timetable generation fails immediately (fail-closed design)

---

## Integration Points

### 1. Master Matrix Integration

**Trigger**: Master Matrix saves → calls `write_execution_timetable_from_master_matrix()`

**Location**: `modules/matrix/file_manager.py` line 66

**Flow**:
```
Master Matrix.save() 
  → file_manager.py writes parquet/json
  → file_manager.py calls timetable_engine.write_execution_timetable_from_master_matrix()
  → Timetable Engine generates timetable_current.json
```

### 2. Robot Integration

**Trigger**: Robot polls `timetable_current.json` every 5 seconds

**Location**: `modules/robot/core/RobotEngine.cs`

**Flow**:
```
Robot.Start()
  → Robot polls timetable_current.json
  → Robot reads execution plan
  → Robot executes enabled trades at slot_time
  → Robot skips blocked trades
```

### 3. API Integration

**Endpoint**: `POST /api/timetable/generate`

**Location**: `modules/dashboard/backend/main.py` line 838

**Flow**:
```
API Request → generate_timetable()
  → Timetable Engine generates timetable DataFrame
  → Returns timetable data to API
  → API can display or save timetable
```

---

## Key Design Principles

### 1. Fail-Closed Design

- **No Silent Failures**: All contract violations raise `ValueError`
- **No Fallbacks**: Missing `trade_date` → fail immediately
- **No Coercion**: Invalid dates → fail immediately

### 2. Strict Consumer Contract

- **Never Normalizes**: Timetable Engine validates but never normalizes dates
- **DataLoader Owns Normalization**: All dates must be normalized upstream
- **Contract Enforcement**: Validates `trade_date` dtype and presence only

### 3. Complete Execution Contract

- **All 14 Streams**: Timetable always includes all streams (enabled or blocked)
- **Never Skip**: Missing streams are added with defaults
- **Complete Information**: Every stream has slot_time, enabled status, and block_reason (if blocked)

### 4. Atomic Writes

- **Temp File First**: Write to `timetable_current.tmp`
- **Atomic Rename**: Rename to `timetable_current.json`
- **Prevents Partial Reads**: Robot never reads incomplete file

---

## Summary

The Timetable Engine is the critical bridge between analysis and execution:

1. **Reads** sequencer decisions from Master Matrix
2. **Applies** filters to determine enabled/blocked status
3. **Ensures** all 14 streams are present
4. **Writes** complete execution contract to `timetable_current.json`
5. **Robot** reads and executes the plan

**Key Characteristics**:
- Fail-closed design (no silent failures)
- Strict consumer (validates but never normalizes)
- Complete contract (all streams always present)
- Atomic writes (prevents partial reads)

**Timeline**:
- Evening: Master Matrix builds → Timetable Engine generates plan
- Next Day: Robot reads plan → Robot executes enabled trades

**Integration**:
- Master Matrix → Timetable Engine (automatic trigger)
- Timetable Engine → Robot (file polling)
- API → Timetable Engine (on-demand generation)
