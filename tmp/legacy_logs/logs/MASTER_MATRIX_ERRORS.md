# Master Matrix Error Summary
Generated: 2025-12-21

## Master Matrix Specific Errors

### 1. **Excluded Times Not Being Filtered** ⚠️ CRITICAL
**Location**: `modules/matrix/sequencer_logic.py` (lines 479-508)

**Error Message**:
```
[ERROR] Excluded times still present in result: ['07:30', '10:30']
  All excluded times: ['07:30', '10:30']
  All times in result: ['07:30', '08:00', '09:00', '09:30', '10:00', '10:30', '11:00']
```

**Description**: 
When a stream has excluded times configured (e.g., ES1 excluding '07:30'), those times are still appearing in the final master matrix output. The sequencer logic detects this but doesn't actually remove them.

**Impact**: 
- **Data quality issue**: Trades at excluded times are being included when they should be filtered out
- **Affects streams**: ES1, NQ1, and others with exclude_times configured
- **Most recent occurrence**: 2025-12-07 18:47:43

**Root Cause**:
Looking at the code in `sequencer_logic.py` lines 479-498, the filtering logic creates a check but the actual removal may not be working correctly. The code checks `result_df_check` but may not be properly applying the filter to `result_df`.

**Affected Streams**:
- ES1: Excluding '07:30' but it still appears
- NQ1: Excluding '07:30' but it still appears  
- Various streams: Excluding '10:30' but it still appears

---

### 2. **Invalid trade_date Rows** ⚠️ MEDIUM PRIORITY
**Location**: `modules/matrix/master_matrix.py` (line ~339)

**Error Message**:
```
[ERROR] ES1 has 201 trades with invalid trade_date! These will be removed!
Found 201 rows with invalid trade_date out of 2273 total rows - filtering them out
```

**Description**: 
201 trades from ES1 stream have invalid or missing trade_date values. These rows are being automatically filtered out during master matrix building.

**Impact**: 
- **Data loss**: 201 trades are being excluded from the master matrix
- **Affects**: ES1 stream specifically
- **Most recent occurrence**: 2025-11-22 00:29:56

**Root Cause**:
The trade_date column is either:
- Missing from the source analyzer data
- In an invalid format that can't be parsed
- Contains null/NaN values

**Recommendation**: 
1. Check the analyzer output files for ES1 to see why trade_date is missing
2. Verify the schema normalizer is properly handling date columns
3. Add validation in the data loader to catch this earlier

---

## Error Statistics

### By Type:
- **Filtering errors**: 49 occurrences (excluded times not removed)
- **Data validation errors**: 4+ occurrences (invalid trade_date)

### By Stream:
- **ES1**: Both excluded times issue AND invalid trade_date issue
- **NQ1**: Excluded times issue
- **Multiple streams**: Excluded times issue

### Timeline:
- **Most recent**: 2025-12-07 (excluded times)
- **Older**: 2025-11-22 (invalid trade_date)

---

## Recommended Actions

### Immediate (Critical):
1. ✅ **Fix excluded times filtering bug**
   - File: `modules/matrix/sequencer_logic.py`
   - Lines: 479-508 (final cleanup section)
   - Issue: The filtering logic creates a check but doesn't properly remove excluded times
   - Action: Review and fix the filtering logic to ensure excluded times are actually removed

### Short Term:
2. ✅ **Investigate invalid trade_date issue**
   - Check ES1 analyzer output files in `data/analyzed/ES1/`
   - Verify date parsing/normalization in schema_normalizer.py
   - Add data validation earlier in the pipeline

---

## How to Check Master Matrix Logs

```bash
# View recent master matrix errors
grep -i "ERROR" logs/master_matrix.log | tail -50

# View excluded times errors specifically
grep -A 3 "Excluded times still present" logs/master_matrix.log

# View invalid date errors
grep "invalid trade_date" logs/master_matrix.log
```


