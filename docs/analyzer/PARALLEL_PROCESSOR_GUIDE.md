# Parallel Processor Guide

## What It Does

The **Parallel Processor** (`modules/analyzer/parallel_processor.py`) is a performance optimization module that:

1. **Processes ranges in parallel** using ThreadPoolExecutor
2. **Splits work across multiple CPU cores** (uses 75% of available cores, max 8 threads)
3. **Speeds up analysis** for large datasets with many ranges
4. **Falls back to sequential** for small datasets (< 2 ranges)

### Key Features:
- Multi-threaded range processing
- Automatic CPU core detection
- Load balancing across threads
- Benchmarking capabilities (parallel vs sequential)
- Thread-safe DataFrame handling

## Current Status

**Location**: `modules/analyzer/parallel_processor.py`

**Status**: ✅ **Code exists but NOT integrated into main engine**

**Current Usage**:
- Referenced in `optimizations/research_runner.py` but not fully implemented (has TODO)
- Not used in `breakout_core/engine.py` (main engine)
- Has example usage code but not production-ready integration

## Where It Should Go

### Option 1: Integrate into Engine (Recommended)
**Location**: `modules/analyzer/breakout_core/engine.py`

**Integration Point**: In `run_strategy()` function, after building ranges:

```python
# Current code (line ~209):
ranges = range_detector.build_slot_ranges(df, rp, debug)

# Add parallel processing here:
if len(ranges) > 100:  # Only use parallel for large datasets
    from parallel_processor import ParallelProcessor
    processor = ParallelProcessor(enable_parallel=True)
    # Process ranges in parallel...
```

### Option 2: Keep in Optimizations (Current)
**Location**: `modules/analyzer/optimizations/`

**Status**: Already referenced but not fully implemented
- `research_runner.py` has placeholder code
- Would need to complete the `_run_parallel_strategy()` method

## What Needs to Be Changed

### 1. Integration into Engine

**File**: `modules/analyzer/breakout_core/engine.py`

**Changes Needed**:

```python
def run_strategy(df: pd.DataFrame, rp: RunParams, debug: bool = False) -> pd.DataFrame:
    # ... existing code ...
    
    ranges = range_detector.build_slot_ranges(df, rp, debug)
    
    # ADD: Parallel processing option
    use_parallel = len(ranges) > 100  # Threshold for parallel processing
    
    if use_parallel:
        from parallel_processor import ParallelProcessor
        processor = ParallelProcessor(enable_parallel=True)
        
        # Process ranges in parallel
        rows = processor.process_dataframe_parallel(
            df, ranges, _process_single_range, rp, config_manager, 
            instrument_manager, utility_manager, entry_detector, 
            price_tracker, result_processor, time_manager, debug
        )
    else:
        # Existing sequential processing
        rows = []
        for R in ranges:
            # ... existing range processing code ...
```

### 2. Extract Range Processing Logic

**Need to create**: A function that processes a single range (currently inline in the loop)

**New Function**:
```python
def _process_single_range(df: pd.DataFrame, R: SlotRange, rp: RunParams,
                          config_manager, instrument_manager, utility_manager,
                          entry_detector, price_tracker, result_processor,
                          time_manager, debug: bool) -> Dict:
    """Process a single range and return result row"""
    # Extract the range processing logic from the current loop
    # Return a dictionary (result row) or None
```

### 3. Update Parallel Processor (if needed)

**File**: `modules/analyzer/parallel_processor.py`

**Potential Issues**:
- Currently expects `ranges` as `List[Dict]` but we have `List[SlotRange]`
- May need to convert `SlotRange` objects to dictionaries or update processor

**Fix**:
```python
# Option A: Convert SlotRange to dict
range_dict = {
    'date': R.date,
    'session': R.session,
    'end_label': R.end_label,
    # ... other attributes
}

# Option B: Update processor to accept SlotRange objects directly
```

## Recommended Approach

### Step 1: Extract Range Processing
Create `_process_single_range()` function in `engine.py` to make it callable by parallel processor.

### Step 2: Add Parallel Option
Add optional parallel processing to `run_strategy()` with a threshold (e.g., > 100 ranges).

### Step 3: Test Integration
- Test with small dataset (should use sequential)
- Test with large dataset (should use parallel)
- Verify results match between parallel and sequential

### Step 4: Make It Optional
Add parameter to `RunParams` or `run_strategy()`:
```python
def run_strategy(df: pd.DataFrame, rp: RunParams, debug: bool = False,
                 enable_parallel: bool = True) -> pd.DataFrame:
```

## File Structure

```
modules/analyzer/
├── parallel_processor.py          # ✅ Exists - parallel processing logic
├── breakout_core/
│   └── engine.py                  # ⚠️ Needs integration
└── optimizations/
    └── research_runner.py         # ⚠️ Has placeholder (TODO)
```

## Benefits of Integration

1. **Performance**: 2-4x speedup for large datasets (many ranges)
2. **Scalability**: Better utilization of multi-core CPUs
3. **Transparency**: Works automatically when beneficial
4. **Fallback**: Automatically uses sequential for small datasets

## Considerations

1. **Thread Safety**: Ensure DataFrame operations are thread-safe
2. **Memory**: Parallel processing uses more memory
3. **Debugging**: Parallel execution can make debugging harder
4. **Determinism**: Ensure results are identical to sequential processing

## Next Steps

1. ✅ **Parallel processor code exists** - no changes needed to the module itself
2. ⚠️ **Extract range processing** - create `_process_single_range()` function
3. ⚠️ **Integrate into engine** - add parallel processing option
4. ⚠️ **Test thoroughly** - verify results match sequential processing
5. ⚠️ **Update documentation** - document parallel processing feature

## Summary

- **What**: Performance optimization for processing many ranges in parallel
- **Where**: `modules/analyzer/parallel_processor.py` (exists)
- **Status**: Not integrated into main engine yet
- **Needs**: Integration into `engine.py` and extraction of range processing logic
- **Benefit**: 2-4x speedup for large datasets


















