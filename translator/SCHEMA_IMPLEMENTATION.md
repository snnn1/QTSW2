# Strict Quant-Grade Schema Implementation

## Overview

The translator now enforces a strict, quant-grade output schema for all exported 1-minute bar data. This ensures consistency, reproducibility, and compatibility across the entire pipeline.

## Schema Definition

### Required Columns (Exact Order)

1. **timestamp** - ISO8601 string format, timezone-aware, America/Chicago
2. **open** - float64
3. **high** - float64
4. **low** - float64
5. **close** - float64
6. **volume** - float64
7. **instrument** - string (e.g., "CL", "GC", "ES")
8. **source** - string (pipeline origin identifier)
9. **interval** - string (always "1min")
10. **synthetic** - boolean (True when bar was reconstructed, False otherwise)

### Field Behavior

#### timestamp
- **Format**: ISO8601 string with timezone offset (e.g., "2025-01-01 17:00:00-06:00")
- **Timezone**: Must be timezone-aware and in America/Chicago
- **Export**: CSV exports use ISO8601 string format; Parquet uses native datetime type

#### open/high/low/close
- **Type**: float64
- **Validation**: Must be numeric

#### volume
- **Type**: float64
- **Validation**: Must be numeric

#### instrument
- **Type**: string
- **Requirement**: Must be explicitly provided and non-empty
- **Example**: "CL", "GC", "ES", "NQ"

#### source
- **Type**: string
- **Purpose**: Describes pipeline origin (e.g., "translator:filename.csv")
- **Requirement**: Must be non-empty

#### interval
- **Type**: string
- **Value**: Always "1min"
- **Enforcement**: Automatically set to "1min" regardless of input

#### synthetic
- **Type**: boolean
- **Purpose**: Indicates if bar was reconstructed/synthetic
- **Default**: False
- **Requirement**: Must always exist and be boolean

## Schema Enforcement

### Automatic Enforcement

All data exported from the translator automatically:
1. **Validates** all required fields exist and have correct types
2. **Removes** all non-schema fields (contract, frequency, metadata, etc.)
3. **Reorders** columns to match exact schema order
4. **Adds** missing required fields with defaults
5. **Converts** types to match schema requirements
6. **Validates** before export (raises exception on failure)

### Validation Rules

Before export, the system validates:
- ✅ All required columns exist
- ✅ No extraneous columns
- ✅ Column order is correct
- ✅ Timestamp is timezone-aware in Chicago
- ✅ OHLC/volume are numeric
- ✅ Instrument is non-empty
- ✅ Interval is "1min"
- ✅ Synthetic is boolean

### Error Handling

If validation fails, a `SchemaValidationError` is raised with detailed error messages. Export is prevented until all issues are resolved.

## Deterministic Output

### Row Ordering
- All exported data is sorted by timestamp (ascending)
- No randomness in row ordering
- Consistent across multiple exports

### Format Consistency
- Timestamp formatting is consistent (ISO8601)
- No locale/OS-dependent formatting
- Bit-for-bit reproducible exports

## Export Formats

### CSV Export
- Timestamp exported as ISO8601 string: "2025-01-01 17:00:00-06:00"
- All columns in exact schema order
- UTF-8 encoding

### Parquet Export
- Timestamp stored as native datetime type (timezone-aware)
- All columns in exact schema order
- Optimized for performance

## Migration Notes

### Backward Compatibility

The schema enforcement is applied automatically during export. Existing code that uses the translator will work without changes, but exported files will now conform to the strict schema.

### What Changed

1. **Non-schema columns removed**: contract, frequency, and other metadata columns are no longer in exported data
2. **New columns added**: source, interval, synthetic are now always present
3. **Column order enforced**: Columns are always in the exact schema order
4. **Stricter validation**: Invalid data will cause export to fail rather than silently exporting

## Testing

Comprehensive test suite in `tests/test_schema.py` verifies:
- ✅ Correct column order
- ✅ Correct dtype enforcement
- ✅ Presence of instrument
- ✅ Presence of synthetic
- ✅ Timestamp is tz-aware Chicago
- ✅ No extraneous fields appear
- ✅ Validation triggers errors when fields are missing or invalid
- ✅ Deterministic output

All tests use synthetic data only (no I/O beyond final exported output).

## Usage

### Basic Usage

```python
from translator import process_data

# Process files - schema enforcement is automatic
success, df = process_data(
    input_folder="data/raw",
    output_folder="data/processed",
    separate_years=True,
    output_format="parquet"
)
```

### Manual Schema Enforcement

```python
from translator.schema import prepare_for_export

# Manually enforce schema before custom export
df_export = prepare_for_export(
    df,
    source="custom:pipeline",
    instrument="CL",
    validate=True
)
```

## Implementation Files

- `translator/schema.py` - Schema definition, validation, and enforcement
- `translator/core.py` - Updated to use schema enforcement before export
- `translator/file_loader.py` - Adds source, interval, synthetic columns during load
- `tests/test_schema.py` - Comprehensive test suite









