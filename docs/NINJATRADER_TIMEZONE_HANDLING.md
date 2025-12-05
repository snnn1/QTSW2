# NinjaTrader Timezone Handling - Critical Architecture

## Overview

This document describes the strict timezone handling rules enforced throughout the data pipeline for NinjaTrader exports.

## Key Facts About NinjaTrader 8.1

**CRITICAL:** NinjaTrader 8.1 does NOT allow overriding the data-series timezone.

1. **Bar timestamps are ALWAYS in the Trading Hours timezone**
   - The timestamp of every bar in NinjaTrader is always in the timezone defined by the instrument's Trading Hours template
   - This cannot be changed in NinjaTrader

2. **For all CME futures (CL, GC, ES, NQ, YM, NG)**
   - Trading Hours timezone = **America/Chicago (CST/CDT)**
   - This is fixed and cannot be changed

3. **NinjaTrader "General → Time zone" setting**
   - Only affects the **UI display**
   - Does **NOT** change bar timestamps
   - Bar timestamps remain in Trading Hours timezone (Chicago for CME futures)

## Architecture

### Translator Layer (`translator/file_loader.py`)

**Responsibilities:**
- Interprets all naive timestamps as **America/Chicago**
- Validates tz-aware timestamps are Chicago (warns if not)
- Optionally normalizes to UTC or other timezone (configurable)
- Never uses system timezone or OS timezone
- Never defaults to local time

**Rules Enforced:**
1. All naive timestamps → interpreted as America/Chicago
2. Any tz-aware timestamp that is NOT Chicago → indicates upstream error (warns)
3. Mixed timezones → error (converts all to Chicago)
4. Deterministic conversion path only

### DataManager Layer (`scripts/breakout_analyzer/logic/data_logic.py`)

**Responsibilities:**
- Enforces strict timezone rules
- Validates no naive timestamps
- Validates all timestamps are in expected timezone (Chicago or normalized target)
- Rejects data that violates timezone invariants

**Rules Enforced:**
1. No naive timestamps allowed (error)
2. All timestamps must be America/Chicago (or normalized target)
3. No mixed timezones allowed (error)
4. No system/local timezone allowed (warning)
5. Non-Chicago tz-aware timestamps indicate upstream error (warning)

## Implementation Details

### Translator: `load_single_file()`

```python
def load_single_file(
    filepath: Path, 
    auto_detect_frequency: bool = True,
    normalize_timezone: Optional[str] = None
) -> Optional[pd.DataFrame]:
```

**Parameters:**
- `normalize_timezone`: Target timezone for normalization
  - `None` or `"America/Chicago"`: Keep timestamps in Chicago (default)
  - `"UTC"`: Convert Chicago timestamps to UTC
  - Other timezone string: Convert to specified timezone

**Process:**
1. Load data (timestamps are naive)
2. **Localize to America/Chicago** (all naive timestamps)
3. If tz-aware timestamps exist:
   - Validate they are Chicago (warn if not)
   - Convert mixed timezones to Chicago
4. **Normalize** (if `normalize_timezone` specified):
   - Convert from Chicago to target timezone
   - Deterministic: CHICAGO → TARGET

### DataManager: `_enforce_timezone_awareness()`

Validates that:
- All timestamps are timezone-aware (no naive datetimes)
- All timestamps are in America/Chicago (or normalized target)
- No mixed timezones
- No system/local timezone usage

## Usage Examples

### Default (Keep in Chicago)

```python
from translator.file_loader import load_single_file

# All timestamps interpreted as America/Chicago, kept in Chicago
df = load_single_file(filepath)
# Result: All timestamps in America/Chicago timezone
```

### Normalize to UTC

```python
# All timestamps interpreted as America/Chicago, converted to UTC
df = load_single_file(filepath, normalize_timezone="UTC")
# Result: All timestamps in UTC timezone
```

### DataManager Validation

```python
from scripts.breakout_analyzer.logic.data_logic import DataManager

data_manager = DataManager(enforce_timezone=True)
result = data_manager.load_parquet(filepath)
# Automatically validates timezone rules
```

## Error Handling

### Translator Warnings

- **Non-Chicago tz-aware timestamp**: Warns but converts to Chicago
- **Mixed timezones**: Error, converts all to Chicago
- **Normalization failure**: Warns, keeps in Chicago

### DataManager Errors

- **Naive timestamps**: Error (must be fixed in Translator)
- **Mixed timezones**: Error (must be fixed in Translator)
- **Non-Chicago timezone**: Warning (may be valid normalization target)

## Testing Requirements

All tests must verify:

1. ✅ Naive timestamps are localized to America/Chicago
2. ✅ Chicago timestamps convert correctly to UTC
3. ✅ DST transitions (CST/CDT) preserve correct ordering
4. ✅ No timestamp uses system timezone or UI timezone
5. ✅ Translator rejects/warns against tz-aware timestamps that are not Chicago
6. ✅ DataManager enforces all rules strictly

## Migration Notes

**Before:**
- Translator tried to detect timezone from filename (`_UTC` pattern)
- Used heuristics to guess if data was UTC or Chicago
- Could use system timezone as fallback

**After:**
- All naive timestamps → America/Chicago (deterministic)
- All tz-aware timestamps → validated as Chicago (warns if not)
- Configurable normalization (CHICAGO → TARGET)
- No system timezone usage
- Strict validation in DataManager

## Configuration

To normalize timestamps to UTC during translation:

```python
# In translator/core.py or calling code
df = load_single_file(filepath, normalize_timezone="UTC")
```

To keep timestamps in Chicago (default):

```python
df = load_single_file(filepath)  # or normalize_timezone=None
```

## Summary

- **NinjaTrader exports**: Always naive timestamps in Chicago time
- **Translator**: Interprets as Chicago, optionally normalizes
- **DataManager**: Enforces strict rules, validates compliance
- **No system timezone**: Never used, never assumed
- **Deterministic**: Same input → same output, always









