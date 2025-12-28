# Analyzer Known Issues & Limitations

## Design Decisions

### Intentionally Excluded Features

**Slot Switching**:
- **Status**: Not included by design
- **Reason**: Keeps analyzer simple and focused on core strategy analysis
- **Behavior**: All enabled slots are processed independently
- **Impact**: Each slot is analyzed separately without cross-slot optimization

**Dynamic Target Changes**:
- **Status**: Not included by design
- **Reason**: Maintains consistent analysis across all trades
- **Behavior**: All trades use the base target (first level of target ladder)
- **Impact**: No progression to higher targets based on peak performance

**Note**: These features are intentionally excluded. For advanced features, use the sequential processor.

## Potential Issues

### 1. Timezone Handling
**Location**: `logic/range_logic.py`

**Details**:
- **Timezone is handled by the translator** - data arrives in correct format (America/Chicago)
- Defensive normalization code handles edge cases (different timezone object instances)
- Multiple checks for timezone-aware vs naive timestamps (defensive)

**Risk**: 
- Low risk - translator handles timezone conversion
- Defensive code ensures consistency even with edge cases

**Mitigation**:
- Translator ensures correct timezone
- Defensive normalization handles edge cases
- Well-documented defensive checks

### 2. MFE Data Gap Handling
**Location**: `logic/price_tracking_logic.py` lines 163-186

**Details**:
- MFE calculation may encounter data gaps
- Falls back to available data if MFE end time not available
- Logs warnings for gaps > 5 minutes

**Risk**:
- MFE may be incomplete if data doesn't extend to next day
- Silent degradation (uses available data)

**Mitigation**:
- Logging provides visibility into gaps
- Graceful fallback to available data
- Expected behavior documented

### 3. Encoding Issues (Windows)
**Location**: Multiple files

**Details**:
- Code has try/except blocks for UnicodeEncodeError
- Some print statements avoid emojis for Windows compatibility
- Comments mention "no emojis to avoid Windows encoding errors"

**Risk**:
- Debug output may be incomplete on Windows
- Some characters may not display correctly

**Mitigation**:
- Fallback print statements without emojis
- Error handling prevents crashes

## Performance Considerations

### 1. Large Dataset Processing
**Location**: `breakout_core/engine.py`

**Details**:
- Processes all ranges sequentially
- No built-in chunking for very large datasets
- Progress logging every 500/1000 ranges

**Recommendation**:
- Use year filtering for large datasets
- Process instruments separately
- Consider using optimizations module for parallel processing

### 2. Memory Usage
**Location**: Multiple files

**Details**:
- Loads entire parquet file into memory
- Creates copies of DataFrames for filtering
- MFE calculation may hold extended data in memory

**Recommendation**:
- Filter data early (by year, instrument)
- Use optimizations for memory-efficient data types
- Process in chunks for very large datasets

## Documentation Gaps

### 1. Missing Feature Documentation
**Issue**: Disabled features (slot switching, dynamic targets) not clearly documented in main docs

**Recommendation**: 
- Add section to deep dive about disabled features
- Document why they're disabled
- Explain where they're available (sequential processor)

### 2. Sequential Processor Relationship
**Issue**: References to "sequential processor" but no documentation of what it is or how it differs

**Recommendation**:
- Document relationship between analyzer and sequential processor
- Explain when to use each
- Document feature differences

## Testing Gaps

### 1. Edge Cases
**Potential gaps**:
- Very small ranges (< 1 tick)
- Ranges where high == low
- Data with missing bars
- Timezone edge cases (DST transitions)

### 2. Integration Testing
**Potential gaps**:
- End-to-end pipeline testing
- Error recovery testing
- Large dataset testing

## Recommendations

### High Priority
1. **Add integration tests** for common scenarios
2. **Document sequential processor** relationship (if it exists)

### Medium Priority
1. **Add chunking** for very large datasets
2. **Optimize memory usage** for large datasets

### Low Priority
1. **Improve Windows encoding** handling
2. **Add more edge case tests**

## Workarounds

### For Slot Switching
- Use sequential processor if dynamic slot selection is needed
- Manually analyze different slot combinations
- Use external optimization tools

### For Dynamic Targets
- Use sequential processor if target progression is needed
- Manually analyze different target levels
- Post-process results to simulate target changes

### For Large Datasets
- Filter by year before processing
- Process instruments separately
- Use year filtering in UI
- Enable optimizations if available

