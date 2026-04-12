# Dry Run Test Plan - Architectural Doctrine Verification

**Date**: February 1, 2026  
**Purpose**: Verify all architectural doctrine changes work correctly in DRYRUN mode

---

## Test Objectives

Verify that the following changes work correctly:

1. ✅ **ZERO_BAR_HYDRATION Terminal State**: Streams with zero-bar hydration are correctly classified
2. ✅ **Zero-Bar Tracking**: `_hadZeroBarHydration` flag is set in all zero-bar scenarios
3. ✅ **Terminal State Classification**: `DetermineTerminalState()` correctly identifies zero-bar hydration
4. ✅ **Journal Fallback Removal**: System correctly handles missing hydration logs (no journal fallback)
5. ✅ **Hydration Logs Canonical**: Hydration logs are used as single source of truth

---

## Test Scenarios

### Scenario 1: Normal Operation (Baseline)
**Purpose**: Verify normal operation still works  
**Expected**: Streams process normally, terminal states set correctly

### Scenario 2: Zero-Bar Hydration (CSV Missing)
**Purpose**: Verify zero-bar hydration tracking when CSV file is missing  
**Expected**: 
- `_hadZeroBarHydration = true` set
- Terminal state = `ZERO_BAR_HYDRATION`
- Log shows `PRE_HYDRATION_ZERO_BARS`

### Scenario 3: Zero-Bar Hydration (Hard Timeout)
**Purpose**: Verify zero-bar hydration tracking when hard timeout forces transition  
**Expected**:
- `_hadZeroBarHydration = true` set
- Terminal state = `ZERO_BAR_HYDRATION` (if no trade)
- Log shows `PRE_HYDRATION_FORCED_TRANSITION` or `PRE_HYDRATION_TIMEOUT_NO_BARS`

### Scenario 4: Trade Completed (Despite Zero Bars)
**Purpose**: Verify trade completion takes precedence over zero-bar classification  
**Expected**:
- Terminal state = `TRADE_COMPLETED` (not `ZERO_BAR_HYDRATION`)
- Trade completion checked first in `DetermineTerminalState()`

### Scenario 5: Missing Hydration Log (Restart)
**Purpose**: Verify journal fallback removal - system should recompute, not use journal  
**Expected**:
- No journal fallback log (`RANGE_LOCKED_RESTORE_FALLBACK`)
- Range recomputed from available bars
- Hydration log is canonical source

---

## Test Execution

### Step 1: Run DRYRUN Replay

```powershell
cd c:\Users\jakej\QTSW2
dotnet run --project modules/robot/harness/Robot.Harness.csproj `
  --mode DRYRUN `
  --replay `
  --start 2025-12-01 `
  --end 2025-12-02 `
  --timetable-path data/timetable/timetable_current.json `
  --log-dir logs/robot/dryrun_test_$(Get-Date -Format 'yyyyMMdd_HHmmss')
```

### Step 2: Verify Logs

Check for:
- `ZERO_BAR_HYDRATION` terminal state in journal files
- `PRE_HYDRATION_ZERO_BARS` log events
- `terminal_state` field in `JOURNAL_WRITTEN` events
- No `RANGE_LOCKED_RESTORE_FALLBACK` logs (journal fallback removed)

### Step 3: Verify Journal Files

Check `logs/robot/journal/` for:
- `TerminalState` field set correctly
- `ZERO_BAR_HYDRATION` values where appropriate
- `NO_TRADE` vs `ZERO_BAR_HYDRATION` distinction

### Step 4: Verify Hydration Logs

Check `logs/robot/hydration_*.jsonl` for:
- `RANGE_LOCKED` events present
- Hydration events logged correctly
- No fallback to ranges log or journal

---

## Expected Log Patterns

### Zero-Bar Hydration Detection
```json
{
  "event_type": "PRE_HYDRATION_ZERO_BARS",
  "data": {
    "instrument": "ES",
    "slot": "ES1",
    "note": "Pre-hydration file not found - zero bars loaded"
  }
}
```

### Terminal State Classification
```json
{
  "event_type": "JOURNAL_WRITTEN",
  "data": {
    "committed": true,
    "commit_reason": "NO_TRADE_MARKET_CLOSE",
    "terminal_state": "ZERO_BAR_HYDRATION"
  }
}
```

### No Journal Fallback
Should NOT see:
```json
{
  "event_type": "RANGE_LOCKED_RESTORE_FALLBACK",
  "data": {
    "note": "Restoring range lock from journal (hydration/ranges log not found)"
  }
}
```

---

## Verification Checklist

- [ ] DRYRUN replay completes without errors
- [ ] Zero-bar hydration scenarios detected and logged
- [ ] Terminal states set correctly (`ZERO_BAR_HYDRATION` where appropriate)
- [ ] Trade completion takes precedence over zero-bar classification
- [ ] No journal fallback logs present
- [ ] Hydration logs are canonical (used for range restoration)
- [ ] Journal files contain correct `TerminalState` values
- [ ] Statistics can distinguish zero-bar days from regular no-trade days

---

## Success Criteria

✅ **Test Passes If**:
1. All streams process without errors
2. Zero-bar hydration correctly classified as `ZERO_BAR_HYDRATION`
3. Normal no-trade streams classified as `NO_TRADE` (not `ZERO_BAR_HYDRATION`)
4. Trade completion takes precedence
5. No journal fallback occurs
6. Hydration logs are used as canonical source

❌ **Test Fails If**:
1. Compilation errors
2. Runtime exceptions
3. Zero-bar hydration not detected
4. Terminal states incorrect
5. Journal fallback still occurs
6. Missing hydration logs cause errors (should recompute gracefully)

---

## Post-Test Analysis

After running the test, analyze:

1. **Log Analysis**: Search logs for `ZERO_BAR_HYDRATION` occurrences
2. **Journal Analysis**: Check journal files for terminal state distribution
3. **Statistics**: Verify zero-bar days can be filtered from performance stats
4. **Edge Cases**: Check for any unexpected behaviors

---

**Ready to execute?** Run the test command above and verify results match expectations.
