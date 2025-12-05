# Analyzer Speed Optimization Guide

## Current Bottlenecks

1. **Sequential Instrument Processing**: Instruments are processed one at a time
2. **Full Data Loading**: All parquet files loaded into memory at once
3. **Sequential Range Processing**: Ranges processed one by one within each instrument
4. **No Caching**: Same data processed repeatedly

## Optimization Strategies

### 1. Parallel Instrument Processing ⚡ (RECOMMENDED)

**Speedup**: 3-6x faster (depending on CPU cores)

Process multiple instruments simultaneously instead of sequentially.

**Implementation**: Use `tools/run_analyzer_parallel.py`

```bash
# Process all instruments in parallel
python tools/run_analyzer_parallel.py --instruments ES NQ YM CL GC NG

# Limit to 4 parallel workers
python tools/run_analyzer_parallel.py --instruments ES NQ YM CL --workers 4
```

**How it works**:
- Uses `ProcessPoolExecutor` to run multiple analyzer processes simultaneously
- Auto-detects optimal number of workers (75% of CPU cores)
- Each instrument runs in its own process (no shared memory issues)

**Expected speedup**:
- 6 instruments sequentially: ~6 hours
- 6 instruments in parallel (4 workers): ~1.5-2 hours

### 2. Enable Parallel Range Processing (Within Analyzer)

**Speedup**: 1.5-3x faster per instrument

The analyzer has a `ParallelProcessor` module that can process ranges in parallel.

**Status**: Module exists but may not be enabled by default

**To enable**: Check if `scripts/breakout_analyzer/breakout_core/engine.py` uses `ParallelProcessor`

### 3. Optimize Data Loading

**Speedup**: 10-30% faster

**Current**: Loads all parquet files, concatenates into single DataFrame

**Optimizations**:
- Process files in chunks (if memory is an issue)
- Use `pd.read_parquet()` with `columns` parameter to load only needed columns
- Cache loaded data if processing multiple times

### 4. Incremental Processing

**Speedup**: 50-90% faster for daily runs

Only process new data since last run.

**Implementation**:
- Track last processed date per instrument
- Only load data files newer than last run
- Skip dates already processed

### 5. Reduce Verbose Logging

**Speedup**: 5-10% faster

Reduce console output during processing.

**Current**: Logs every range, every date, every slot
**Optimized**: Log only milestones (every 1000 ranges, completion per date)

## Quick Wins (Easy to Implement)

### Option A: Use Parallel Instrument Runner

**Easiest**: Just use the parallel runner script

```bash
# In dashboard, modify analyzer stage to use:
python tools/run_analyzer_parallel.py --instruments ES NQ YM CL GC NG
```

### Option B: Modify Scheduler to Use Parallel Processing

Update `automation/daily_data_pipeline_scheduler.py`:

```python
# Instead of sequential loop:
for instrument in instruments:
    run_analyzer(instrument)

# Use parallel processing:
from concurrent.futures import ProcessPoolExecutor
with ProcessPoolExecutor(max_workers=4) as executor:
    futures = [executor.submit(run_analyzer, inst) for inst in instruments]
    for future in as_completed(futures):
        result = future.result()
```

## Recommended Approach

**For Dashboard**: Use the parallel analyzer runner

1. **Update dashboard backend** to use `run_analyzer_parallel.py` instead of sequential processing
2. **Auto-detect instruments** from available data files
3. **Set max_workers** based on CPU cores (default: 75% of cores)

**Expected Results**:
- **Before**: 6 instruments × 1 hour each = 6 hours total
- **After**: 6 instruments ÷ 4 workers = ~1.5 hours total
- **Speedup**: **4x faster**

## Implementation Steps

### Step 1: Test Parallel Runner

```bash
# Test with 2 instruments first
python tools/run_analyzer_parallel.py --instruments ES NQ
```

### Step 2: Update Dashboard Backend

Modify `dashboard/backend/main.py` to use parallel runner for analyzer stage.

### Step 3: Monitor Performance

- Track total time per run
- Monitor CPU/memory usage
- Adjust `max_workers` if needed

## Performance Monitoring

Track these metrics:
- **Total analyzer time**: Should decrease by 3-6x
- **CPU utilization**: Should increase (multiple cores active)
- **Memory usage**: May increase slightly (multiple processes)
- **Success rate**: Should remain 100%

## Notes

- **Memory**: Parallel processing uses more RAM (one process per instrument)
- **CPU**: Uses 75% of available cores by default
- **Compatibility**: Each instrument runs in separate process (no shared state issues)
- **Error handling**: One instrument failure doesn't stop others

## Future Optimizations

1. **Incremental processing**: Only process new data
2. **Caching**: Cache processed ranges
3. **Distributed processing**: Use multiple machines
4. **GPU acceleration**: For range calculations (if applicable)



