# Master Matrix - Interactive Walkthrough Guide

This guide walks through the Master Matrix system step-by-step, connecting the concepts to actual code.

---

## 🎯 Part 1: Understanding the Big Picture

### What You're Looking At

When you open `matrix_timetable_app/frontend/src/App.jsx`, you're seeing the **frontend interface** that:
1. **Loads** master matrix data from the backend API
2. **Displays** trades in a table with filtering
3. **Calculates** statistics (profit by time, day of week, etc.)
4. **Allows** rebuilding the matrix with filters

### The Complete Flow

```
┌─────────────────────────────────────────────────────────────┐
│ 1. ANALYZER OUTPUT (data/analyzer_runs/)                     │
│    ES1/2024/ES1_an_2024_11.parquet                          │
│    ES2/2024/ES2_an_2024_11.parquet                          │
│    ... (all streams)                                         │
└──────────────────────┬──────────────────────────────────────┘
                       │
                       ▼
┌─────────────────────────────────────────────────────────────┐
│ 2. MASTER MATRIX BUILDER (master_matrix/master_matrix.py)   │
│    - Loads all streams                                       │
│    - Applies sequencer logic (one trade per day per stream) │
│    - Normalizes schema                                       │
│    - Adds global columns                                     │
│    - Applies filters                                         │
└──────────────────────┬──────────────────────────────────────┘
                       │
                       ▼
┌─────────────────────────────────────────────────────────────┐
│ 3. OUTPUT FILES (data/master_matrix/)                        │
│    master_matrix_20241127_143022.parquet                     │
│    master_matrix_20241127_143022.json                        │
└──────────────────────┬──────────────────────────────────────┘
                       │
                       ▼
┌─────────────────────────────────────────────────────────────┐
│ 4. BACKEND API (dashboard/backend/main.py)                   │
│    GET /api/matrix/data - Load existing matrix               │
│    POST /api/matrix/build - Build new matrix                 │
└──────────────────────┬──────────────────────────────────────┘
                       │
                       ▼
┌─────────────────────────────────────────────────────────────┐
│ 5. FRONTEND (matrix_timetable_app/frontend/src/App.jsx)      │
│    - Calls API to load/build matrix                          │
│    - Displays data in tables                                 │
│    - Applies filters in Web Worker                           │
│    - Calculates statistics                                  │
└─────────────────────────────────────────────────────────────┘
```

---

## 🔍 Part 2: Frontend Code Walkthrough

### Step 1: App Initialization

**Location**: `App.jsx` lines 10-24

```javascript
function App() {
  // Error boundary - catch any initialization errors
  try {
    return <AppContent />
  } catch (error) {
    console.error('App initialization error:', error)
    return (
      <div className="min-h-screen bg-black text-white p-8">
        <h1 className="text-2xl font-bold mb-4 text-red-400">Error Loading App</h1>
        <p className="text-gray-300 mb-2">Error: {error.message}</p>
        <pre className="bg-gray-900 p-4 rounded text-sm overflow-auto">{error.stack}</pre>
      </div>
    )
  }
}
```

**What it does**: Catches any errors during app initialization and displays them.

**Related Error**: See "App initialization error" in the overview document.

---

### Step 2: Loading Master Matrix on Mount

**Location**: `App.jsx` lines 132-139

```javascript
// Load master matrix on mount (don't rebuild, just load existing)
useEffect(() => {
  // Wait a bit for backend to be ready, then try loading
  const timer = setTimeout(() => {
    loadMasterMatrix(false)
  }, 1000)
  
  return () => clearTimeout(timer)
}, [])
```

**What it does**: 
- Waits 1 second for backend to be ready
- Calls `loadMasterMatrix(false)` to load existing matrix (not rebuild)

**Why the delay?**: Backend might not be ready immediately when frontend loads.

---

### Step 3: The `loadMasterMatrix` Function

**Location**: `App.jsx` lines 1056-1250

This is the **core function** that handles loading and building the matrix. Let's break it down:

#### 3a. Health Check (lines 1061-1079)

```javascript
// Check if backend is reachable
try {
  const controller = new AbortController()
  const timeoutId = setTimeout(() => controller.abort(), 3000)
  const healthCheck = await fetch(`${API_BASE.replace('/api', '')}/`, { 
    method: 'GET', 
    signal: controller.signal 
  })
  clearTimeout(timeoutId)
} catch (e) {
  if (e.name === 'AbortError') {
    setMasterError('Backend connection timeout. Make sure the dashboard backend is running on http://localhost:8000')
  } else {
    setMasterError('Backend not running. Please start the dashboard backend on port 8000. Error: ' + e.message)
  }
  setMasterData([])
  setMasterLoading(false)
  return
}
```

**What it does**: 
- Checks if backend is running (3 second timeout)
- Sets error state if backend is unreachable
- Returns early if backend is down

**Related Error**: "Backend not ready" - see overview document section 6.

---

#### 3b. Building Matrix (if rebuild requested) (lines 1082-1151)

```javascript
if (rebuild) {
  // Build stream filters for API
  const streamFiltersApi = {}
  Object.keys(streamFilters).forEach(streamId => {
    const filters = streamFilters[streamId]
    if (filters) {
      streamFiltersApi[streamId] = {
        exclude_days_of_week: filters.exclude_days_of_week || [],
        exclude_days_of_month: filters.exclude_days_of_month || [],
        exclude_times: filters.exclude_times || []
      }
    }
  })
  
  // ... build request body ...
  
  const buildResponse = await fetch(`${API_BASE}/matrix/build`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(buildBody)
  })
  
  if (!buildResponse.ok) {
    const errorData = await buildResponse.json()
    setMasterError(errorData.detail || 'Failed to build master matrix')
    setMasterLoading(false)
    return
  }
}
```

**What it does**:
1. Converts frontend filter format to API format
2. Sends POST request to `/api/matrix/build`
3. Handles errors if build fails

**Related Errors**: 
- "Failed to build master matrix" - see overview section 5
- "Master matrix built but is empty" - see overview section 5

**Backend Endpoint**: `dashboard/backend/main.py` line 1043-1275

---

#### 3c. Loading Matrix Data (lines 1154-1163)

```javascript
// Load the matrix data
const dataResponse = await fetch(`${API_BASE}/matrix/data?limit=50000`)

if (!dataResponse.ok) {
  const errorData = await dataResponse.json()
  setMasterError(errorData.detail || 'Failed to load matrix data')
  setMasterData([])
  setMasterLoading(false)
  return
}

const data = await dataResponse.json()
const trades = data.data || []
```

**What it does**:
- Fetches matrix data from backend (up to 50,000 rows)
- Handles errors if load fails
- Extracts trades array from response

**Related Errors**:
- "No master matrix files found" - see overview section 5
- "Failed to load matrix data" - see overview section 5

**Backend Endpoint**: `dashboard/backend/main.py` line 1358-1549

---

#### 3d. Initializing Web Worker (lines 1173-1179)

```javascript
if (trades.length > 0) {
  setMasterData(trades)
  
  // Initialize worker with data (converts to columnar format)
  if (trades.length > 0) {
    workerInitData(trades)
  }
}
```

**What it does**:
- Stores trades in React state
- Sends data to Web Worker for efficient filtering/processing

**Why Web Worker?**: 
- Keeps UI responsive during heavy filtering operations
- Processes data in background thread

**Related Error**: "Worker error" - see overview section 6

---

### Step 4: Filter Application

**Location**: `App.jsx` lines 147-163

```javascript
// Apply filters in worker when filters or active tab changes
useEffect(() => {
  try {
    if (workerReady && masterData.length > 0 && workerFilter) {
      const streamId = activeTab === 'timetable' ? 'master' : activeTab
      // Request initial rows for table rendering (first 100 rows, sorted)
      const returnRows = activeTab !== 'timetable' // Return rows for data table tabs
      workerFilter(streamFilters, streamId, returnRows, true) // sortIndices = true
      
      // Calculate stats in worker
      if (activeTab !== 'timetable' && workerCalculateStats) {
        workerCalculateStats(streamFilters, streamId, masterContractMultiplier)
      }
    }
  } catch (error) {
    console.error('Error in filter useEffect:', error)
  }
}, [streamFilters, activeTab, masterContractMultiplier, workerReady, masterData.length, workerFilter, workerCalculateStats])
```

**What it does**:
- Automatically applies filters when:
  - Filters change
  - Active tab changes
  - Contract multiplier changes
- Calculates statistics in Web Worker
- Handles errors gracefully

**Related Errors**: Filter-related errors in sequencer logic - see overview section 2

---

## 🏗️ Part 3: Backend Code Walkthrough

### Step 1: Building Master Matrix (Backend)

**Location**: `dashboard/backend/main.py` lines 1043-1275

```python
@app.post("/api/matrix/build")
async def build_master_matrix(request: MatrixBuildRequest):
    """Build master matrix from all streams."""
    
    # ... module reloading logic ...
    
    # Initialize MasterMatrix
    matrix = MasterMatrix(**init_kwargs)
    
    # Build master matrix
    master_df = matrix.build_master_matrix(
        start_date=request.start_date,
        end_date=request.end_date,
        specific_date=request.specific_date,
        output_dir="data/master_matrix",
        stream_filters=stream_filters_dict,
        analyzer_runs_dir="data/analyzer_runs",
        streams=request.streams
    )
    
    if master_df.empty:
        return {
            "status": "warning",
            "message": "Master matrix built but is empty",
            ...
        }
    
    return {
        "status": "success",
        "message": "Master matrix built successfully",
        ...
    }
```

**What it does**:
1. Reloads MasterMatrix module (to get latest code)
2. Creates MasterMatrix instance with filters
3. Calls `build_master_matrix()` method
4. Returns success/warning/error response

**Related Errors**: See overview section 5 (API/Backend Errors)

---

### Step 2: Master Matrix Builder Core Logic

**Location**: `master_matrix/master_matrix.py`

#### 2a. Discovering Streams (lines 95-124)

```python
def _discover_streams(self) -> List[str]:
    """
    Auto-discover streams by scanning analyzer_runs directory.
    Looks for subdirectories matching stream patterns (ES1, ES2, etc.)
    """
    streams = []
    if not self.analyzer_runs_dir.exists():
        logger.warning(f"Analyzer runs directory not found: {self.analyzer_runs_dir}")
        return streams
    
    import re
    # Pattern: ES1, GC2, CL1, etc. (2 letters + 1 or 2)
    stream_pattern = re.compile(r'^([A-Z]{2})([12])$')
    
    for item in self.analyzer_runs_dir.iterdir():
        if not item.is_dir():
            continue
        
        # Check if directory name matches stream pattern
        match = stream_pattern.match(item.name)
        if match:
            stream_id = item.name  # e.g., "ES1", "GC2"
            streams.append(stream_id)
    
    streams.sort()  # Sort alphabetically for consistency
    logger.info(f"Discovered {len(streams)} streams: {streams}")
    return streams
```

**What it does**:
- Scans `data/analyzer_runs/` directory
- Finds subdirectories matching pattern `[A-Z]{2}[12]` (ES1, GC2, etc.)
- Returns list of discovered streams

**Related Error**: "No streams discovered" - see overview section 1

---

#### 2b. Loading Streams (lines 242-428)

```python
def load_all_streams(self, start_date: Optional[str] = None, 
                     end_date: Optional[str] = None,
                     specific_date: Optional[str] = None,
                     wait_for_streams: bool = True,
                     max_retries: int = 3,
                     retry_delay_seconds: int = 2) -> pd.DataFrame:
    """
    Load all trades from analyzer_runs and apply sequencer logic.
    """
    # ... discovery logic ...
    
    # Load all streams with retry logic
    streams_to_load = self.streams.copy()
    retry_count = 0
    
    while streams_to_load and retry_count <= max_retries:
        if retry_count > 0:
            logger.info(f"Retry attempt {retry_count}/{max_retries} for failed streams...")
            time.sleep(retry_delay_seconds)
        
        remaining_streams = []
        for stream_id in streams_to_load:
            success = load_stream(stream_id)
            if not success:
                remaining_streams.append(stream_id)
        
        streams_to_load = remaining_streams
        retry_count += 1
        
        if not wait_for_streams:
            break
    
    # ... merge and apply sequencer logic ...
```

**What it does**:
- Loads each stream with retry logic (up to 3 retries)
- Merges all stream data
- Applies sequencer logic to select one trade per day per stream

**Related Errors**: 
- "Stream directory not found" - see overview section 1
- "No monthly consolidated files found" - see overview section 1
- "Error loading Parquet file" - see overview section 1

---

#### 2c. Sequencer Logic (lines 430-861)

This is the **most complex part** - it selects one trade per day per stream using time-change logic.

**Key Concepts**:
1. **Rolling Sum**: Last 13 trades per time slot (Win=+1, Loss=-2, BE=0)
2. **Time Change**: Only happens on Loss, compares rolling sums
3. **Session Times**: 
   - S1: ["07:30", "08:00", "09:00"]
   - S2: ["09:30", "10:00", "10:30", "11:00"]

**Example Flow**:
```
Day 1: ES1 starts at 09:00 (first available time)
Day 2: ES1 still at 09:00 (no loss yet)
Day 3: ES1 at 09:00, gets Loss
       → Compare 09:00 rolling sum vs other S1 times (07:30, 08:00)
       → If 08:00 has higher rolling sum, switch to 08:00 for Day 4
Day 4: ES1 now at 08:00 (time change took effect)
```

**Related Errors**: See overview section 2 (Sequencer Logic Errors)

---

## 🐛 Part 4: Common Issues & How to Debug

### Issue 1: "No data in master matrix"

**Symptoms**: Empty table, no trades displayed

**Debug Steps**:
1. Check `logs/master_matrix.log` for errors
2. Verify `data/analyzer_runs/` exists and has stream folders
3. Check if monthly Parquet files exist: `ES1/2024/ES1_an_2024_11.parquet`
4. Try rebuilding matrix from frontend (click "Rebuild Matrix" button)

**Code to Check**:
- `master_matrix.py` line 104-106: `_discover_streams()` method
- `master_matrix.py` line 313-316: File discovery logic

---

### Issue 2: "Backend not running"

**Symptoms**: Frontend shows "Backend not running" error

**Debug Steps**:
1. Check if backend is running: `http://localhost:8000/api/matrix/test`
2. Check backend logs: `logs/backend_debug.log`
3. Verify backend started correctly (no Python errors)

**Code to Check**:
- `App.jsx` line 1062-1079: Health check logic
- `dashboard/backend/main.py` line 175-201: Test endpoint

---

### Issue 3: "All trades filtered out"

**Symptoms**: Matrix built but shows 0 allowed trades

**Debug Steps**:
1. Check `final_allowed` column values in data
2. Review `filter_reasons` column for why trades are blocked
3. Check filter settings in frontend (may have excluded all times)
4. Verify `exclude_times` filters aren't too restrictive

**Code to Check**:
- `master_matrix.py` line 1066-1094: Filter application logic
- `App.jsx` line 1084-1096: Filter building for API

---

### Issue 4: "Time slot selection incorrect"

**Symptoms**: Wrong time slots selected, unexpected time changes

**Debug Steps**:
1. Check `Time Change` column to see when changes occur
2. Verify rolling sum calculations (check `{time} Rolling` columns)
3. Ensure all historical data is loaded (not filtered too early)
4. Check session configuration matches expected times

**Code to Check**:
- `master_matrix.py` line 488-492: Session configuration
- `master_matrix.py` line 595-662: Time change logic

---

## 📊 Part 5: Understanding the Data Structure

### Master Matrix Columns

**Required Columns** (from analyzer output):
- `Date`, `Time`, `Target`, `Peak`, `Direction`, `Result`, `Range`, `Stream`, `Instrument`, `Session`, `Profit`, `SL`

**Global Columns** (added by matrix):
- `global_trade_id`: Unique ID (1, 2, 3, ...)
- `day_of_month`: 1-31
- `dow`: Mon-Fri
- `month`: 1-12
- `session_index`: 1 (S1) or 2 (S2)
- `is_two_stream`: True for *2 streams
- `dom_blocked`: True if day is 4/16/30 and stream is "2"
- `filter_reasons`: Why trade was filtered (if any)
- `final_allowed`: True if trade passes all filters

**Time Slot Columns** (added by sequencer):
- `{time} Rolling`: Rolling sum for time slot (e.g., "09:00 Rolling")
- `{time} Points`: Points for last trade at time slot (e.g., "09:00 Points")
- `Time Change`: Shows time changes (e.g., "09:00→10:00")

**Example Row**:
```json
{
  "global_trade_id": 1234,
  "Date": "2024-11-27",
  "Time": "09:00",
  "Stream": "ES1",
  "Instrument": "ES",
  "Session": "S1",
  "Result": "Win",
  "Profit": 2.5,
  "Target": 1.0,
  "SL": 3.0,
  "day_of_month": 27,
  "dow": "Wed",
  "final_allowed": true,
  "09:00 Rolling": 5.0,
  "09:00 Points": 1,
  "Time Change": ""
}
```

---

## 🔧 Part 6: Key Functions Reference

### Frontend Functions

| Function | Location | Purpose |
|----------|----------|---------|
| `loadMasterMatrix()` | `App.jsx:1056` | Load/build matrix from API |
| `calculateTimeProfit()` | `App.jsx:222` | Calculate profit by time slot |
| `calculateDOMProfit()` | `App.jsx:277` | Calculate profit by day of month |
| `calculateDailyProfit()` | `App.jsx:314` | Calculate profit by day of week |

### Backend Functions

| Function | Location | Purpose |
|----------|----------|---------|
| `build_master_matrix()` | `main.py:1043` | API endpoint to build matrix |
| `get_matrix_data()` | `main.py:1358` | API endpoint to load matrix data |

### Master Matrix Functions

| Function | Location | Purpose |
|----------|----------|---------|
| `_discover_streams()` | `master_matrix.py:95` | Find all stream directories |
| `load_all_streams()` | `master_matrix.py:242` | Load all stream data |
| `_apply_sequencer_logic()` | `master_matrix.py:430` | Select one trade per day per stream |
| `normalize_schema()` | `master_matrix.py:901` | Ensure all streams have same columns |
| `add_global_columns()` | `master_matrix.py:1014` | Add metadata columns |
| `build_master_matrix()` | `master_matrix.py:1213` | Main build function |

---

## 🎓 Part 7: Learning Path

### Beginner: Understanding the Flow
1. Read "What is the Master Matrix?" section
2. Follow data flow diagram
3. Try loading matrix in frontend
4. Check what data appears

### Intermediate: Understanding the Logic
1. Read "How It Works" section
2. Study sequencer logic explanation
3. Look at filter application
4. Try rebuilding with different filters

### Advanced: Debugging Issues
1. Read "Possible Errors & Issues" section
2. Study error handling code
3. Practice debugging common issues
4. Check logs when errors occur

### Expert: Modifying the System
1. Understand all code sections
2. Know where to add new features
3. Understand data structures
4. Can modify sequencer logic if needed

---

## 💡 Tips for Working with the Matrix

1. **Always check logs first**: `logs/master_matrix.log` and `logs/backend_debug.log`
2. **Use date filters**: Limit data when testing to speed up processing
3. **Test with one stream**: Use `rebuildStream` parameter to test with ES1 only
4. **Verify data exists**: Check `data/analyzer_runs/` before building
5. **Understand filters**: Know what `exclude_times` does before applying
6. **Check browser console**: Frontend errors appear there
7. **Use API test endpoint**: `http://localhost:8000/api/matrix/test` to verify backend

---

## 🚀 Next Steps

1. **Try it yourself**: 
   - Open the frontend
   - Load the matrix
   - Try rebuilding with filters

2. **Read the code**:
   - Start with `loadMasterMatrix()` in App.jsx
   - Then look at backend endpoint
   - Finally study `master_matrix.py`

3. **Experiment**:
   - Change filter settings
   - Rebuild matrix
   - See how results change

4. **Debug issues**:
   - When something goes wrong, check logs
   - Use this guide to find relevant code
   - Fix the issue

---

This walkthrough connects the overview document to the actual code. Use it as a reference when working with the Master Matrix system!



