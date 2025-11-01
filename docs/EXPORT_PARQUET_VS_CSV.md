# Export Format: Parquet vs CSV

## Current Flow
```
NinjaTrader → CSV → Translator → Parquet (processed/validated)
```

## Option 1: Keep CSV Export (Recommended ✅)

### Pros:
- ✅ **Preserves translator pipeline** - validation, deduplication, contract rollover
- ✅ **No dependencies** - CSV is universal, no additional libraries needed
- ✅ **Easier debugging** - can open CSV in Excel/text editor
- ✅ **Flexibility** - translator handles timezone conversion, data cleaning
- ✅ **Frequency detection** - translator automatically detects tick vs minute data
- ✅ **Contract rollover** - translator can merge multiple contracts into continuous series

### Cons:
- ⚠️ **Slightly larger files** - CSV is text-based (but compressed if zipped)
- ⚠️ **Extra step** - need to run translator

### Data Preservation:
The translator uses `pd.read_csv()` which preserves:
- Float precision (no loss)
- Timestamps (proper parsing)
- All data types

**Conclusion**: CSV export is recommended because the translator provides valuable processing that you'd lose with direct Parquet.

---

## Option 2: Export Directly to Parquet

### Pros:
- ✅ **Smaller files** - Parquet is compressed columnar format
- ✅ **Faster** - skip translator step
- ✅ **Better type preservation** - no string conversion intermediate step
- ✅ **Direct usage** - can load Parquet directly in analysis tools

### Cons:
- ⚠️ **Skip translator pipeline** - lose validation, deduplication, contract rollover
- ⚠️ **Dependencies required** - Need C# Parquet library (Apache.Parquet, ParquetSharp, etc.)
- ⚠️ **Less flexible** - harder to inspect/debug binary format
- ⚠️ **Manual timezone handling** - must do in NinjaTrader
- ⚠️ **No frequency detection** - would need manual metadata
- ⚠️ **No contract rollover** - would need to handle separately

### Data Preservation:
Parquet preserves data perfectly, BUT:
- You lose the translator's validation and cleaning
- You'd need to reimplement timezone conversion, deduplication in C#

---

## Recommendation

### Keep CSV Export ✅

**Why**: 
The translator pipeline (`translator/core.py`) does important work:
1. **Validation** - checks for invalid OHLC relationships
2. **Deduplication** - removes duplicate timestamps
3. **Contract rollover** - merges multiple contracts into continuous series
4. **Timezone handling** - proper UTC → Chicago conversion
5. **Frequency detection** - automatically identifies tick vs minute data
6. **Data cleaning** - handles edge cases

If you export directly to Parquet, you'd need to:
- Reimplement all validation in C#
- Handle contract rollover separately
- Manually manage timezones
- Add Parquet dependencies to NinjaTrader

**Better approach**: Keep CSV export and let the translator handle it. The translator is optimized for this workflow.

---

## If You Still Want Parquet Export

You would need to:

1. **Add Parquet library to NinjaTrader**:
   - Install NuGet package: `Apache.Parquet` or `ParquetSharp`
   - Add to NinjaTrader's references

2. **Modify the C# exporter** to write Parquet instead of CSV:
   ```csharp
   using Parquet;
   using Parquet.Data;
   using Parquet.File;
   
   // Create Parquet file instead of CSV
   var schema = new Schema(
       new DataField<DateTimeOffset>("timestamp"),
       new DataField<double>("open"),
       new DataField<double>("high"),
       // ... etc
   );
   ```

3. **Handle all translator logic in C#**:
   - Timezone conversion
   - Deduplication
   - Contract rollover
   - Validation

**This is significantly more work and less maintainable.**

---

## Performance Comparison

### CSV → Translator → Parquet
- Export time: Fast (text write)
- Translator time: ~30 seconds for 300MB file
- **Total**: ~1-2 minutes
- File size: ~50-100MB Parquet (from 300MB CSV)

### Direct Parquet Export
- Export time: Moderate (binary write + compression)
- Translator time: **SKIPPED** (but lose validation)
- **Total**: ~30 seconds
- File size: ~50-100MB Parquet

**Time savings**: ~1 minute
**Cost**: Lose all translator validation and processing

---

## Data Safety

### CSV Export + Translator
- ✅ Validates OHLC relationships
- ✅ Removes duplicates
- ✅ Handles contract rollover
- ✅ Timezone correction
- ✅ Error detection and reporting

### Direct Parquet Export
- ⚠️ No validation (must trust NinjaTrader data)
- ⚠️ Potential duplicates if multiple exports
- ⚠️ No contract rollover
- ⚠️ Manual timezone handling required

---

## Final Recommendation

**Keep CSV export** and use the translator pipeline. The one-minute translation step is worth it for the validation and processing you get.

If you really want Parquet directly, it's possible but requires significant C# development and you'll lose valuable data processing features.




