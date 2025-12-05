# Timezone Architecture: Translator vs DataManager

## Overview

The system uses a **two-layer architecture** for timezone handling:

1. **Translator Layer** (`translator/file_loader.py`) - Handles timezone conversion
2. **DataManager Layer** (`logic/data_logic.py`) - Enforces timezone invariants

---

## Translator Layer Responsibilities

**Location:** `translator/file_loader.py` (lines 241-267)

**What it does:**
- ✅ Timezone inference (detects UTC vs Chicago from filename)
- ✅ Conversion to America/Chicago timezone
- ✅ DST normalization (handles daylight saving transitions)
- ✅ Removing invalid local times (spring forward - 2 AM becomes 3 AM)
- ✅ Resolving overlap ambiguities (fall back - 2 AM appears twice)

**Code:**
```python
# Detects UTC data from filename
is_utc_data = "_utc" in filename_lower

# Converts UTC → Chicago
if is_utc_data:
    df["timestamp"] = df["timestamp"].dt.tz_localize("UTC").dt.tz_convert("America/Chicago")
else:
    # Assumes Chicago time
    df["timestamp"] = df["timestamp"].dt.tz_localize("America/Chicago")
```

**Output:** All timestamps are timezone-aware and in `America/Chicago` timezone

---

## DataManager Layer Responsibilities

**Location:** `logic/data_logic.py`

**What it does:**
- ✅ **Enforce** tz awareness (fail if naive timestamps)
- ✅ **Enforce** monotonic timestamps (sort if needed)
- ✅ **Enforce** no duplicates (remove duplicates, keep="last")
- ✅ **Accept** only properly normalized timestamps (Chicago timezone)
- ✅ **Fail** if timestamps violate invariants

**What it does NOT do:**
- ❌ Timezone conversion (Translator handles this)
- ❌ DST handling (Translator handles this)
- ❌ Timezone inference (Translator handles this)

**Code:**
```python
def _enforce_timezone_awareness(self, df: pd.DataFrame):
    # Check if timezone-aware
    if df['timestamp'].dt.tz is None:
        raise ValueError("Timestamps must be timezone-aware. Translator should handle conversion.")
    
    # Check if all in Chicago timezone
    if not all timestamps are in CHICAGO_TZ:
        raise ValueError("All timestamps must be in America/Chicago. Translator should handle conversion.")
```

---

## Data Flow

```
Raw CSV Files (UTC or naive)
    ↓
Translator (translator/file_loader.py)
    ├─ Detects timezone from filename
    ├─ Converts UTC → Chicago
    ├─ Handles DST transitions
    └─ Outputs: timezone-aware Chicago timestamps
    ↓
Processed Parquet Files (data/processed/)
    ├─ All timestamps: timezone-aware
    ├─ All timestamps: America/Chicago
    └─ All timestamps: DST-normalized
    ↓
DataManager (logic/data_logic.py)
    ├─ Validates timezone awareness
    ├─ Validates Chicago timezone
    ├─ Removes duplicates
    ├─ Sorts timestamps
    └─ Outputs: Clean, validated data
```

---

## Why This Architecture?

1. **Separation of Concerns:**
   - Translator = Data ingestion and conversion
   - DataManager = Data validation and cleaning

2. **Single Responsibility:**
   - Translator handles messy raw data
   - DataManager handles clean processed data

3. **Fail Fast:**
   - If Translator didn't do its job, DataManager fails immediately
   - Prevents silent data corruption

4. **Testability:**
   - Translator tests: timezone conversion logic
   - DataManager tests: validation logic (assumes already converted)

---

## Testing Implications

### Translator Tests Should Test:
- UTC → Chicago conversion
- DST transition handling
- Invalid time removal
- Overlap resolution

### DataManager Tests Should Test:
- ✅ Rejects naive timestamps (raises error)
- ✅ Rejects non-Chicago timezones (raises error)
- ✅ Accepts Chicago timezone-aware timestamps
- ✅ Enforces monotonic timestamps
- ✅ Removes duplicates

**Note:** DataManager tests should use fixtures with **already-converted** timestamps (Chicago timezone-aware), not raw/naive timestamps.

---

## Migration Notes

If you're updating DataManager tests:

1. **Remove timezone conversion tests** - These belong in Translator tests
2. **Add timezone enforcement tests** - Test that DataManager rejects invalid timezones
3. **Update fixtures** - All test data should have Chicago timezone-aware timestamps
4. **Update error messages** - Reference Translator layer in error messages









