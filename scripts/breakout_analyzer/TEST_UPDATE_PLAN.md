# Test Update Plan: DataManager Timezone Architecture

## Current State

- ✅ Translator exists and handles timezone conversion (`translator/file_loader.py`)
- ✅ DataManager has `_enforce_timezone_awareness()` method (enforcement only)
- ❌ Tests still call `_normalize_timezone()` (conversion method - wrong!)
- ❌ Tests test conversion logic (should test enforcement logic)

## Required Changes

### 1. Update Test Class: `TestTimezoneNormalization`

**Current tests (WRONG - test conversion):**
- `test_naive_timestamps_localized` - Tests conversion (should test rejection)
- `test_utc_timestamps_converted` - Tests conversion (should test rejection)
- `test_dst_correctness` - Tests DST handling (Translator handles this)

**New tests (CORRECT - test enforcement):**
- `test_rejects_naive_timestamps` - Should raise ValueError
- `test_rejects_non_chicago_timezone` - Should raise ValueError
- `test_accepts_chicago_timezone_aware` - Should pass validation
- `test_enforces_monotonic_timestamps` - Should sort if needed

### 2. Update Fixtures

**Current fixtures:**
- `timezone_mixed_dataset` - Has naive, UTC, and Chicago timestamps (for conversion testing)

**New fixtures needed:**
- `naive_timestamp_dataset` - Only naive timestamps (for rejection testing)
- `utc_timestamp_dataset` - Only UTC timestamps (for rejection testing)
- `chicago_timestamp_dataset` - Only Chicago timezone-aware (for acceptance testing)

### 3. Update Test Assertions

**Old (conversion):**
```python
normalized_df, warnings = data_manager._normalize_timezone(timezone_mixed_dataset)
assert (normalized_df['timestamp'].dt.tz == CHICAGO_TZ).all()
```

**New (enforcement):**
```python
with pytest.raises(ValueError, match="timezone-aware"):
    data_manager._clean_dataframe(naive_timestamp_dataset)
```

## Implementation Steps

1. ✅ Create `_enforce_timezone_awareness()` method
2. ⏳ Update `TestTimezoneNormalization` class
3. ⏳ Update fixtures in `conftest.py`
4. ⏳ Remove/update tests that call `_normalize_timezone()`
5. ⏳ Update all test references from `auto_normalize_timezone` to `enforce_timezone`

## Test Count Impact

- Current: 3 timezone conversion tests
- New: 4 timezone enforcement tests
- Net: +1 test (better coverage)









