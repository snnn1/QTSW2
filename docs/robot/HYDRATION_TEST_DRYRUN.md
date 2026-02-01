# Hydration Test Dryrun

**Purpose**: Specialized dryrun test focused on hydration scenarios and edge cases.

---

## Overview

The hydration test dryrun is a specialized test suite that focuses specifically on testing hydration behavior:

- **Zero-bar hydration** scenarios (missing CSV, empty CSV, timeout)
- **Normal hydration** (CSV present with bars)
- **Partial hydration** (CSV with very few bars)
- **Terminal state classification** (`ZERO_BAR_HYDRATION` vs `NO_TRADE`)

---

## Quick Start

### Run All Scenarios

```powershell
python scripts/robot/hydration_test_dryrun.py --start 2025-12-01 --end 2025-12-02
```

### Run Specific Scenario

```powershell
# Test missing CSV scenario
python scripts/robot/hydration_test_dryrun.py --start 2025-12-01 --end 2025-12-02 --scenario missing_csv

# Test empty CSV scenario
python scripts/robot/hydration_test_dryrun.py --start 2025-12-01 --end 2025-12-02 --scenario empty_csv

# Test normal hydration
python scripts/robot/hydration_test_dryrun.py --start 2025-12-01 --end 2025-12-02 --scenario normal
```

### Use Specific Instrument

```powershell
python scripts/robot/hydration_test_dryrun.py --start 2025-12-01 --end 2025-12-02 --instrument ES
```

---

## Test Scenarios

### 1. Normal Hydration
**Setup**: CSV file exists with bars  
**Expected**: 
- Normal operation
- Terminal state: `NO_TRADE` (not `ZERO_BAR_HYDRATION`)
- Zero-bar hydration events: 0

### 2. Missing CSV
**Setup**: CSV file is removed/backed up  
**Expected**:
- `PRE_HYDRATION_ZERO_BARS` log event
- `_hadZeroBarHydration = true` set
- Terminal state: `ZERO_BAR_HYDRATION`
- Zero-bar hydration events: ≥1

### 3. Empty CSV
**Setup**: CSV file exists but contains only header (no bars)  
**Expected**:
- `PRE_HYDRATION_ZERO_BARS` log event
- `_hadZeroBarHydration = true` set
- Terminal state: `ZERO_BAR_HYDRATION`
- Zero-bar hydration events: ≥1

### 4. Partial CSV
**Setup**: CSV file exists but has very few bars (e.g., 2 bars)  
**Expected**:
- Hydration completes (not zero-bar)
- Terminal state: `NO_TRADE` (not `ZERO_BAR_HYDRATION`)
- Zero-bar hydration events: 0

---

## What Gets Tested

### Hydration Detection
- ✅ Zero-bar hydration correctly detected when CSV missing
- ✅ Zero-bar hydration correctly detected when CSV empty
- ✅ Zero-bar hydration NOT triggered for partial CSV
- ✅ `_hadZeroBarHydration` flag set correctly

### Terminal State Classification
- ✅ `ZERO_BAR_HYDRATION` terminal state set correctly
- ✅ `NO_TRADE` vs `ZERO_BAR_HYDRATION` distinction maintained
- ✅ Trade completion takes precedence over zero-bar classification

### Logging
- ✅ `PRE_HYDRATION_ZERO_BARS` events logged
- ✅ `PRE_HYDRATION_COMPLETE` events logged
- ✅ Terminal state logged in `JOURNAL_WRITTEN` events

### Journal Files
- ✅ Journal files contain correct `TerminalState` values
- ✅ `ZERO_BAR_HYDRATION` persisted correctly
- ✅ No journal fallback (hydration logs are canonical)

---

## Output

The test script provides:

1. **Setup Phase**: Modifies CSV files to create test scenarios
2. **Execution Phase**: Runs DRYRUN replay
3. **Analysis Phase**: Analyzes logs and journal files
4. **Cleanup Phase**: Restores CSV files from backups
5. **Summary**: Test results and pass/fail status

### Example Output

```
============================================================
Scenario: Missing CSV
Description: CSV file is missing - should trigger zero-bar hydration
============================================================

[Setup]
  ✓ CSV backed up and removed: data/raw/es/1m/2025/12/ES_1m_2025-12-01.csv

[Running DRYRUN]
  Running: dotnet run --project modules/robot/harness/Robot.Harness.csproj ...
  ✓ DRYRUN completed successfully

[Analyzing Results]
  Log Analysis:
    - Zero-bar hydration events: 1
    - PRE_HYDRATION_ZERO_BARS: 1
    - PRE_HYDRATION_TIMEOUT: 0
    - PRE_HYDRATION_COMPLETE: 0
    - Terminal states in logs: {'ZERO_BAR_HYDRATION': 1}
  
  Journal Analysis:
    - Total journals: 1
    - ZERO_BAR_HYDRATION: 1
    - NO_TRADE: 0
    - TRADE_COMPLETED: 0
    - Other states: {}

[Cleanup]
  ✓ Restored CSV from backup: data/raw/es/1m/2025/12/ES_1m_2025-12-01.csv
```

---

## Command Line Options

```
--start YYYY-MM-DD      Start date for replay (required)
--end YYYY-MM-DD        End date for replay (required)
--instrument INSTR      Instrument to test (default: ES)
--scenario SCENARIO     Specific scenario to run:
                        - all (default)
                        - normal
                        - missing_csv
                        - empty_csv
                        - partial_csv
```

---

## How It Works

1. **CSV Manipulation**: The script backs up existing CSV files, then modifies them to create test scenarios
2. **DRYRUN Execution**: Runs the standard DRYRUN replay with modified CSV files
3. **Log Analysis**: Parses JSONL log files to find hydration-related events
4. **Journal Analysis**: Checks journal files for terminal state classification
5. **Restoration**: Restores CSV files from backups after each test

---

## Integration with Regular Tests

This hydration test complements the regular dryrun test:

- **Regular dryrun** (`DRYRUN_TEST_PLAN.md`): Tests overall system behavior
- **Hydration test**: Focuses specifically on hydration edge cases

Both tests should pass for full system verification.

---

## Troubleshooting

### CSV Files Not Found
If CSV files don't exist for the test date:
- The script will create minimal CSV files for normal/partial scenarios
- Missing CSV scenario will work as expected (file already missing)

### Test Fails Unexpectedly
Check:
1. Log directory exists and is writable
2. DRYRUN harness compiles successfully
3. Timetable file exists for test date
4. Translated data exists (if required)

### CSV Restoration Issues
If CSV files aren't restored:
- Check for `.csv.backup` files in `data/raw/{instrument}/1m/{year}/{month}/`
- Manually restore if needed: `mv file.csv.backup file.csv`

---

## Related Documentation

- `DRYRUN_TEST_PLAN.md` - General dryrun test plan
- `HYDRATION_COMPLETE_REVIEW.md` - Detailed hydration system documentation
- `ARCHITECTURAL_DOCTRINE.md` - Architectural decisions including zero-bar hydration

---

**Last Updated**: February 1, 2026
