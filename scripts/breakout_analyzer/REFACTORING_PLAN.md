# Analyzer Refactoring Plan
**Goal**: Simplify code without changing any results

## Validation Strategy
1. **Baseline Capture**: Run analyzer on test dataset, save results + hash
2. **After Each Change**: Re-run and compare - must match exactly
3. **Rollback Plan**: Git commit after each validated step

## Phase 1: Safe Dead Code Removal (No Logic Changes)
✅ **Status**: Ready to start
- Remove disabled slot switching code blocks
- Remove disabled dynamic targets code blocks  
- Remove unused imports
- Remove commented-out code sections
- **Risk**: None (code is already disabled)

## Phase 2: Consolidate Duplicate Logic (Logic Preserved)
- Merge duplicate MFE calculations (currently calculated twice)
- Remove redundant target/stop hit checks
- Consolidate break-even logic paths
- **Risk**: Low (same logic, just reorganized)

## Phase 3: Split Large Files (Structure Only)
- Split `price_tracking_logic.py` (1700+ lines) into:
  - `execution_logic.py` - Trade execution flow
  - `mfe_logic.py` - MFE calculation (already exists, enhance)
  - `break_even_logic.py` - Break-even adjustments (already exists, enhance)
- **Risk**: Low (just moving code, no logic changes)

## Phase 4: Simplify Complex Functions (Logic Preserved)
- Simplify `execute_trade()` method
- Reduce special case handling where possible
- Consolidate similar code paths
- **Risk**: Medium (need careful validation)

## Validation Commands

### Capture Baseline
```bash
cd scripts/breakout_analyzer
python tests/test_analyzer_baseline.py --capture --data ../../data/processed/ES_2024.parquet --instrument ES --baseline baseline_ES.json
```

### Compare After Changes
```bash
python tests/test_analyzer_baseline.py --compare --data ../../data/processed/ES_2024.parquet --instrument ES --baseline baseline_ES.json
```

## Success Criteria
- ✅ All test results match baseline exactly (hash match)
- ✅ Row counts identical
- ✅ Column structure identical  
- ✅ Summary stats identical (wins, losses, profit)
- ✅ No performance degradation

