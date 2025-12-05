# Analyzer Used by Scheduler

## Answer

The scheduler uses the **Parallel Analyzer Runner** which then calls the individual analyzer for each instrument.

## Flow

```
Scheduler
  └──> tools/run_analyzer_parallel.py (Parallel Runner)
         └──> scripts/breakout_analyzer/scripts/run_data_processed.py (Individual Analyzer)
                └──> (One process per instrument: ES, NQ, YM, CL, NG, GC)
```

## Scripts

### 1. Parallel Analyzer Runner
**File**: `tools/run_analyzer_parallel.py`

**What it does**:
- Processes multiple instruments in parallel
- Uses `ProcessPoolExecutor` to run multiple analyzer processes simultaneously
- Speeds up analysis by processing all instruments at once

**Command**:
```bash
python tools/run_analyzer_parallel.py --instruments ES NQ YM CL NG GC --folder data/processed --run-id {run_id}
```

### 2. Individual Analyzer
**File**: `scripts/breakout_analyzer/scripts/run_data_processed.py`

**What it does**:
- Analyzes a single instrument
- Processes breakout patterns for that instrument
- Analyzes time slots:
  - **S1**: 07:30, 08:00, 09:00
  - **S2**: 09:30, 10:00, 10:30, 11:00

**Command** (called by parallel runner):
```bash
python scripts/breakout_analyzer/scripts/run_data_processed.py --folder data/processed --instrument ES --sessions S1 S2 --slots S1:07:30 S1:08:00 S1:09:00 S2:09:30 S2:10:00 S2:10:30 S2:11:00 --debug
```

## Why Parallel?

**Before (Sequential)**:
- Process ES → wait → Process NQ → wait → Process YM → ...
- Total time: ~30-60 minutes for 6 instruments

**After (Parallel)**:
- Process ES, NQ, YM, CL, NG, GC all at once
- Total time: ~5-10 minutes (time of longest instrument)

## Configuration

**Instruments processed**:
- ES (E-mini S&P 500)
- NQ (E-mini NASDAQ)
- YM (E-mini Dow)
- CL (Crude Oil)
- NG (Natural Gas)
- GC (Gold)

**Time Slots**:
- Session 1 (S1): 07:30, 08:00, 09:00
- Session 2 (S2): 09:30, 10:00, 10:30, 11:00

## Output

Results saved to:
- `data/analyzer_runs/{instrument}{session}/{year}/`
- Example: `data/analyzer_runs/ES1/2025/ES1_an_2025_11.parquet`

---

## Summary

✅ **Scheduler calls**: `tools/run_analyzer_parallel.py`  
✅ **Which calls**: `scripts/breakout_analyzer/scripts/run_data_processed.py` (per instrument)  
✅ **Processes**: 6 instruments in parallel  
✅ **Result**: Much faster analysis (5-10 min vs 30-60 min)



