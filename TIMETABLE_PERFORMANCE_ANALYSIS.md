# Timetable Performance Analysis

## Performance Bottlenecks Identified

### 1. **RS Calculation - Major Bottleneck** ⚠️

**Problem:**
- `calculate_rs_for_stream()` called **24 times** (12 streams × 2 sessions)
- Each call:
  - Does `rglob("*.parquet")` - recursive file search
  - Loads up to **10 parquet files**
  - Reads, parses dates, filters, concatenates
- **Total: 240 parquet file reads per timetable generation**

**Current Code:**
```python
for stream_id in self.streams:  # 12 streams
    for session in ["S1", "S2"]:  # 2 sessions
        selected_time, time_reason = self.select_best_time(stream_id, session)
        # select_best_time calls calculate_rs_for_stream()
        # which does rglob + reads 10 files
```

**Impact:** Each timetable generation reads ~240 parquet files

---

### 2. **SCF Calculation - Secondary Bottleneck** ⚠️

**Problem:**
- `get_scf_values()` called **24 times** (12 streams × 2 sessions)
- Each call:
  - Does `rglob("*.parquet")` if file not found
  - Reads entire parquet file just to get 2 SCF values
- **Total: 24 parquet file reads**

**Current Code:**
```python
for stream_id in self.streams:
    for session in ["S1", "S2"]:
        scf_s1, scf_s2 = self.get_scf_values(stream_id, trade_date_obj)
        # Reads entire file for one date's SCF values
```

**Impact:** Reads 24 files, but only extracts 2 values per file

---

### 3. **No Caching** ⚠️

**Problem:**
- RS values recalculated every time (even for same date)
- SCF values re-read every time
- File lists re-scanned every time
- No memoization or caching

**Impact:** Same calculations repeated unnecessarily

---

### 4. **Inefficient File Operations** ⚠️

**Problem:**
- `rglob()` searches recursively through all subdirectories
- Reads entire parquet files when only need small subset
- No optimization for file structure (files organized by year/month)

**Impact:** Slower file discovery and unnecessary data loading

---

## Performance Metrics

**Current Performance:**
- RS calculation: ~24 calls × ~10 files = 240 file reads
- SCF calculation: ~24 calls × 1 file = 24 file reads
- **Total: ~264 parquet file reads per timetable generation**

**Estimated Time:**
- rglob: ~0.002s per stream (negligible)
- Parquet read: ~0.01-0.05s per file
- **Total: ~2.6-13 seconds** just for file I/O

---

## Optimization Opportunities

### 1. **Cache RS Calculations** (High Impact)
- Cache RS values per stream/session
- Only recalculate if analyzer data changed
- Use date-based cache keys

### 2. **Batch File Reading** (High Impact)
- Load all needed files once, reuse across streams
- Pre-load file lists at initialization
- Cache file paths

### 3. **Optimize SCF Reading** (Medium Impact)
- Read SCF values in batch for all streams
- Use more targeted file paths (year/month structure)
- Cache SCF values per date

### 4. **Lazy Loading** (Medium Impact)
- Only calculate RS for streams that need it
- Skip SCF reading if filters don't require it
- Defer expensive operations

### 5. **Parallel Processing** (Low Impact)
- Calculate RS for multiple streams in parallel
- Use multiprocessing for file reads
- Thread pool for I/O operations

---

## Recommended Fixes (Priority Order)

1. **Add caching for RS calculations** - Biggest win
2. **Batch SCF value reading** - Medium win
3. **Cache file lists** - Small win
4. **Optimize file paths** - Small win
