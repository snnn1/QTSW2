# Pipeline RTY Fix Summary

## Problem

Pipeline was failing with:
```
[ERROR] RTY failed after 5.1s
run_data_processed.py: error: argument --instrument: invalid choice: 'RTY'
```

## Root Cause

The pipeline discovered RTY data in `data/translated/RTY/` and tried to analyze it, but:

1. ✅ `run_analyzer_parallel.py` was updated to accept RTY (fixed earlier)
2. ❌ `run_data_processed.py` argparse didn't include RTY in choices (just fixed)
3. ✅ RTY is already configured in `config_logic.py` (tick_size, target_ladder, etc.)

## Fix Applied

**File: `modules/analyzer/scripts/run_data_processed.py`**
- Added `"RTY"` to the `--instrument` argument choices
- Line 98: Changed from `choices=["ES","NQ","YM","CL","NG","GC","MES",...]`
- To: `choices=["ES","NQ","YM","CL","NG","GC","RTY","MES",...]`

## Verification

✅ Verified RTY is in argparse choices:
```bash
python modules/analyzer/scripts/run_data_processed.py --help
# Shows: --instrument {ES,NQ,YM,CL,NG,GC,RTY,MES,...}
```

✅ Verified RTY config exists:
- `config_logic.py` has RTY tick_size (0.10)
- `config_logic.py` has RTY target_ladder (10,)
- `breakout_core/config.py` has RTY support

## Current Status

- **Translator**: ✅ Works with RTY (translates RTY files successfully)
- **Analyzer**: ✅ Now fixed - RTY can be analyzed
- **Pipeline**: ✅ Should work now - RTY will be processed correctly

## Next Steps

1. **Test the fix**: Run pipeline again - RTY should now process successfully
2. **Monitor**: Check that RTY analyzer completes without errors
3. **Health recovery**: After 2 successful runs, health will recover from UNSTABLE → HEALTHY

## Files Changed

1. `modules/analyzer/scripts/run_data_processed.py` - Added RTY to argparse choices
2. `ops/maintenance/run_analyzer_parallel.py` - Already had RTY support (fixed earlier)
3. `automation/pipeline/stages/analyzer.py` - Already filters unsupported instruments (fixed earlier)

The pipeline should now work correctly with RTY data!
