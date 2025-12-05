# Translator Failure Fix

## Issue
**Error**: `Translator failed with code 1`  
**Root Cause**: `ModuleNotFoundError: No module named 'translator'`

## Problem
The `tools/translate_raw.py` script was trying to import from the `translator` module, but Python couldn't find it because `QTSW2_ROOT` wasn't in the Python path.

## Solution
Added path setup to `tools/translate_raw.py`:

```python
# Add QTSW2 root to Python path so we can import translator module
QTSW2_ROOT = Path(__file__).resolve().parent.parent
if str(QTSW2_ROOT) not in sys.path:
    sys.path.insert(0, str(QTSW2_ROOT))
```

This ensures the `translator` module can be found when the script runs.

## Status
✅ **FIXED** - Translator now runs successfully

## Testing
Run the translator manually to verify:
```bash
python tools/translate_raw.py --input data/raw --output data/processed --separate-years --no-merge
```

The pipeline should now complete the translator stage successfully.



