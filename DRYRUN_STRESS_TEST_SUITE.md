# DRYRUN Stress Test Suite

## Overview

Four critical stress tests to validate DRYRUN mode robustness and correctness.

---

## Test 1: Late-Start DRYRUN (Critical Realism)

### Question
**"What happens if the system starts after range start?"**

### How
Start DRYRUN at:
- **07:45** (15 minutes after range start)
- **08:10** (40 minutes after range start)
- **08:45** (75 minutes after range start, near slot time)

**Same trading day, same timetable, same bars on disk**

### What You Verify
- ✅ BarsRequest window truncation behaves correctly
- ✅ Range still computes correctly from partial data
- ✅ No future bars are injected
- ✅ State machine does not mis-transition

### Why It Matters
Eval failures often come from:
- PC reboot
- NinjaTrader restart
- Network hiccup

**Late starts are real. You want them boring.**

### Run Test
```bash
python test_late_start_dryrun.py
```

---

## Test 2: Missing-Data DRYRUN (Robustness)

### Question
**"What if data is imperfect?"**

### How
Create controlled defects:
- **Remove 5-10 random bars** from the range window
- **Remove first bar** of the session
- **Remove last bar** before slot time
- **Remove entire 10-minute block**

### What You Verify
- ✅ System does not crash
- ✅ Range still computes from available bars
- ✅ Logs clearly show reduced bar counts
- ✅ No silent assumptions

### Why It Matters
**Data vendors are not perfect.**  
Your system must degrade gracefully.

### Run Test
```bash
python test_missing_data_dryrun.py
```

---

## Test 3: Duplicate/Overlapping Bar DRYRUN

### Question
**"Does deduplication really hold?"**

### How
Inject:
- **Duplicate bars** with same timestamp
- **Bars with same timestamp** but slightly different OHLC
- **Duplicates from different sources** (CSV vs BarsRequest equivalent)

### What You Verify
- ✅ Deduplication precedence works (`LIVE > BARSREQUEST > CSV`)
- ✅ Final range is deterministic
- ✅ Logs show dedupe counts

### Why It Matters
This prevents **"ghost edge" bugs** that only show up once a month.

### Run Test
```bash
python test_duplicate_bars_dryrun.py
```

---

## Test 4: Multi-Day Continuous DRYRUN

### Question
**"Does strategy logic remain stable across days with streams active?"**

### How
Run DRYRUN over:
- **30-90 trading days**
- Real timetable
- Real bar data
- **No resets**

### What You Verify
- ✅ No drift
- ✅ No memory growth
- ✅ No state leakage
- ✅ Journals written every day

### Why It Matters
This simulates **"eval grind mode"** - continuous operation over weeks/months.

### Run Test
```bash
python test_multiday_dryrun.py
```

---

## Running All Tests

```bash
# Run all tests sequentially
python test_late_start_dryrun.py
python test_missing_data_dryrun.py
python test_duplicate_bars_dryrun.py
python test_multiday_dryrun.py

# Or create a runner script
python run_all_stress_tests.py
```

---

## Expected Results

### Test 1: Late-Start
- All start times should produce valid ranges
- No future bars in logs
- State transitions correct

### Test 2: Missing-Data
- System continues with reduced bar counts
- Range computed from available bars
- Clear logging of missing data

### Test 3: Duplicate Bars
- Deduplication logged
- Final range deterministic
- No double-counting

### Test 4: Multi-Day
- All days process successfully
- Journals created for each day
- No memory leaks or state drift

---

## Success Criteria

All tests should:
1. ✅ Complete without crashes
2. ✅ Produce valid ranges
3. ✅ Log clearly what happened
4. ✅ Handle edge cases gracefully

---

## Notes

- Tests use temporary directories to avoid polluting main project
- **Tests require parquet files in `data/translated/`** (DRYRUN replay mode uses parquet, not CSV)
- Tests timeout after reasonable duration
- Tests clean up temporary files automatically
- Tests use `--replay` mode for DRYRUN execution
- **Prerequisites**: Ensure parquet files exist for test dates (e.g., `data/translated/es/1m/2026/01/ES_1m_2026-01-16.parquet`)

## Implementation Details

### Late-Start Test
- Simulates late start by filtering CSV bars before start time
- Verifies range computation works with partial data
- Tests BarsRequest window truncation logic

### Missing-Data Test
- Creates defective CSVs with missing bars
- Tests graceful degradation
- Verifies logging of reduced bar counts

### Duplicate Bars Test
- Injects duplicate bars into CSV
- Tests deduplication precedence (`LIVE > BARSREQUEST > CSV`)
- Verifies deterministic range computation

### Multi-Day Test
- Runs continuous DRYRUN over 30-90 trading days
- Tests for memory leaks and state drift
- Verifies journal creation for each day
