# Analyzer Performance Analysis

## Overview

This document analyzes performance characteristics of the analyzer, identifying memory usage patterns, optimization opportunities, and scalability concerns.

## Current Performance Characteristics

### Memory Usage Patterns

1. **DataFrame Copies**: Multiple `.copy()` calls throughout codebase
2. **Large Dataset Loading**: All parquet files loaded into memory at once
3. **MFE Data Loading**: Extended data (24+ hours) loaded for each range
4. **Parallel Processing**: Disabled for small datasets (< 100 ranges)

### Processing Patterns

1. **Sequential Range Processing**: Default mode processes ranges sequentially
2. **Progress Logging**: Fixed interval (every 500 ranges)
3. **No Chunking**: Large datasets processed entirely in memory

## Identified Issues

### 1. Memory Leaks

**Issue**: Multiple `.copy()` calls create DataFrame copies that may not be released promptly.

**Locations**: 
- `modules/analyzer/logic/range_logic.py:73-79` (timezone normalization)
- `modules/analyzer/breakout_core/engine.py:66, 96` (data filtering)
- `modules/analyzer/logic/price_tracking_logic.py:129, 176, 193` (trade execution)

**Impact**: 
- High memory usage for large datasets
- Potential memory leaks if copies not released
- May cause OOM errors on systems with limited memory

**Recommendation**: 
- Use views where possible instead of copies
- Explicitly delete large objects when done
- Consider using `del` statement for large DataFrames

### 2. Large Dataset Processing

**Issue**: No chunking for very large datasets - could cause OOM errors.

**Location**: `modules/analyzer/scripts/run_data_processed.py:39-88`

**Current Logic**: Loads all parquet files into memory at once
```python
for f in parquet_files:
    df = pd.read_parquet(f)
    parts.append(df)
df = pd.concat(parts, ignore_index=True)
```

**Impact**: 
- Memory usage scales linearly with dataset size
- No upper limit on memory usage
- May fail on systems with limited memory

**Recommendation**: 
- Implement chunked loading for large file sets
- Process files in batches
- Add memory usage monitoring

### 3. Parallel Processing Overhead

**Issue**: Parallel processing disabled for < 100 ranges but threshold may be too high.

**Location**: `modules/analyzer/breakout_core/engine.py:409`
```python
use_parallel = len(ranges) > 100 and not debug
```

**Impact**: 
- May not use parallel processing when beneficial
- Threshold may be too high for some systems
- No consideration of system resources

**Recommendation**: 
- Make threshold configurable
- Auto-detect optimal threshold based on system resources
- Consider CPU count and dataset size

### 4. MFE Data Loading

**Issue**: MFE calculation loads extended data (until next day same slot) - could be memory intensive.

**Location**: `modules/analyzer/breakout_core/engine.py:66-99`

**Current Logic**: 
```python
mfe_df = df[(df["timestamp"] >= R.end_ts) & (df["timestamp"] < mfe_end_time)].copy()
```

**Impact**: 
- Loads 24+ hours of data for each range
- High memory usage for many ranges
- Data may be duplicated across ranges

**Recommendation**: 
- Consider lazy loading or streaming
- Cache MFE data across ranges for same day
- Use views instead of copies where possible

### 5. Progress Logging

**Issue**: Progress logging every 500 ranges but for very large datasets this may be too frequent.

**Location**: `modules/analyzer/breakout_core/engine.py:471-476`

**Current Logic**: 
```python
if ranges_processed == 1 or ranges_processed % progress_interval == 0:
    print(f"Processing range {ranges_processed}/{len(ranges)}: {len(rows)} trades generated")
```

**Impact**: 
- Fixed interval doesn't scale with dataset size
- May log too frequently for very large datasets
- May log too infrequently for small datasets

**Recommendation**: 
- Use percentage-based logging (e.g., every 1%)
- Or time-based logging (e.g., every 10 seconds)
- Make interval configurable

## Optimization Opportunities

### 1. DataFrame Operations

**Opportunities**:
- Use views instead of copies where possible
- Avoid unnecessary filtering operations
- Use in-place operations where safe

**Example**: 
```python
# Instead of:
df_filtered = df[df["timestamp"] >= start_ts].copy()

# Use:
df_filtered = df[df["timestamp"] >= start_ts]
```

### 2. Memory Management

**Opportunities**:
- Explicitly delete large objects when done
- Use context managers for temporary data
- Monitor memory usage

**Example**:
```python
# After processing range:
del mfe_df
del day_df
```

### 3. Caching

**Opportunities**:
- Cache MFE data across ranges for same day
- Cache range calculations
- Cache instrument configurations

**Example**: 
```python
# Cache MFE data by date
mfe_cache = {}
if date in mfe_cache:
    mfe_df = mfe_cache[date]
else:
    mfe_df = load_mfe_data(date)
    mfe_cache[date] = mfe_df
```

### 4. Parallel Processing

**Opportunities**:
- Optimize parallel processing threshold
- Use process pool for CPU-bound operations
- Consider using multiprocessing instead of threading

**Note**: Current implementation uses ThreadPoolExecutor which is good for I/O-bound operations but may not be optimal for CPU-bound operations.

## Performance Benchmarks

### Recommended Benchmarks

1. **Memory Usage**:
   - Peak memory usage for different dataset sizes
   - Memory usage per range processed
   - Memory leak detection

2. **Processing Speed**:
   - Time per range processed
   - Total processing time for different dataset sizes
   - Parallel vs sequential speedup

3. **Scalability**:
   - Performance with increasing dataset size
   - Performance with increasing number of ranges
   - Performance with different system configurations

## Recommendations Summary

### High Priority

1. **Implement Chunked Loading**: Process files in batches for large datasets
2. **Optimize DataFrame Copies**: Use views where possible
3. **Add Memory Monitoring**: Track memory usage during processing

### Medium Priority

1. **Optimize Parallel Processing**: Make threshold configurable and auto-detect optimal value
2. **Cache MFE Data**: Cache MFE data across ranges for same day
3. **Improve Progress Logging**: Use percentage-based or time-based logging

### Low Priority

1. **Code Optimization**: Review and optimize hot paths
2. **Add Performance Tests**: Benchmark performance and detect regressions
3. **Document Performance Characteristics**: Document expected performance for different dataset sizes
