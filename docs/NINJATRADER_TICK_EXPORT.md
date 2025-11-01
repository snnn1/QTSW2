# NinjaTrader Tick Data Export

## Current Status

The existing `MinuteDataExporter` **only works on 1-minute charts**. It currently has this check:

```csharp
// Only export if this is a 1-minute chart
if (BarsPeriod.BarsPeriodType != BarsPeriodType.Minute)
{
    if (CurrentBar == 0)
        Print("WARNING: This indicator only works on 1-minute charts!");
    return;
}
```

## Can It Export Tick Data?

**Yes, but it needs modifications.** The exporter can be extended to support tick data export.

## Modifications Needed

### 1. Detect Chart Type
Allow both minute and tick charts:

```csharp
// Allow both minute and tick data
if (BarsPeriod.BarsPeriodType != BarsPeriodType.Minute && 
    BarsPeriod.BarsPeriodType != BarsPeriodType.Tick)
{
    if (CurrentBar == 0)
        Print("WARNING: This indicator only works on 1-minute or Tick charts!");
    return;
}
```

### 2. Handle Tick Data Format

For **tick data**, the format is different from minute bars:

**Minute Bars (Current):**
```csv
Date,Time,Open,High,Low,Close,Volume,Instrument
2008-07-20,22:00:00,174.13,174.57,174.04,174.53,43.0,CL
```

**Tick Data (Suggested):**
```csv
Date,Time,Price,Volume,Instrument
2008-07-20,22:00:00.123,174.53,2.0,CL
2008-07-20,22:00:00.456,174.55,1.0,CL
2008-07-20,22:00:01.789,174.57,5.0,CL
```

**Key Differences:**
- **Tick data**: Has `Price` instead of `Open,High,Low,Close`
- **Timestamps**: Include milliseconds/sub-seconds (`.123`)
- **Volume**: Usually per trade (1, 2, 5, etc.) rather than cumulative

### 3. Modified Export Logic

Here's how to modify the `OnBarUpdate()` method:

```csharp
protected override void OnBarUpdate()
{
    if (State != State.Historical)
        return;

    // Allow minute OR tick charts
    bool isMinuteChart = (BarsPeriod.BarsPeriodType == BarsPeriodType.Minute);
    bool isTickChart = (BarsPeriod.BarsPeriodType == BarsPeriodType.Tick);
    
    if (!isMinuteChart && !isTickChart)
    {
        if (CurrentBar == 0)
            Print("WARNING: This indicator only works on 1-minute or Tick charts!");
        return;
    }

    // Validate data...
    
    // Write header based on chart type
    if (!headerWritten)
    {
        if (isMinuteChart)
        {
            sw.WriteLine("Date,Time,Open,High,Low,Close,Volume,Instrument");
        }
        else // Tick chart
        {
            sw.WriteLine("Date,Time,Price,Volume,Instrument");
        }
        headerWritten = true;
    }

    // Export data based on chart type
    DateTime exportTime;
    string instrumentName = Instrument?.MasterInstrument?.Name ?? "UNKNOWN";
    
    // Apply bar time fix for minute charts
    if (isMinuteChart)
    {
        DateTime barOpenTime = Time[0].AddMinutes(-1);
        // ... timezone conversion ...
        exportTime = barOpenTime; // Converted to UTC
    }
    else // Tick chart
    {
        // Tick data uses actual trade time, no need to subtract minute
        exportTime = Time[0]; // Convert to UTC
    }
    
    // Format line based on chart type
    string line;
    if (isMinuteChart)
    {
        line = $"{exportTime:yyyy-MM-dd},{exportTime:HH:mm:ss},{Open[0]:F2},{High[0]:F2},{Low[0]:F2},{Close[0]:F2},{Volume[0]:F1},{instrumentName}";
    }
    else // Tick chart
    {
        // For tick data, use Close[0] as Price and Volume[0] as trade volume
        line = $"{exportTime:yyyy-MM-dd},{exportTime:HH:mm:ss.fff},{Close[0]:F2},{Volume[0]:F1},{instrumentName}";
        // Note: .fff includes milliseconds for tick precision
    }
    
    sw.WriteLine(line);
    // ... rest of export logic ...
}
```

## Translator Support

The QTSW2 translator **already supports tick data**:

1. **Frequency Detection**: Automatically detects if data is tick or minute
   - Checks time intervals between rows
   - Median interval < 60 seconds → Tick Data

2. **Format Handling**: Can handle both formats:
   - **Minute**: `Date,Time,Open,High,Low,Close,Volume,Instrument`
   - **Tick**: Would need `Date,Time,Price,Volume,Instrument` (or can use same format with Price as OHLC)

3. **Processing**: 
   - Converts timestamps
   - Handles timezone conversion
   - Validates data
   - Detects frequency automatically

## Recommended Approach

### Option 1: Use Same Format for Both (Easier) ✅

Export tick data using the same CSV format as minute data, but with Price values:

```csv
Date,Time,Open,High,Low,Close,Volume,Instrument
2008-07-20,22:00:00.123,174.53,174.53,174.53,174.53,2.0,CL
```

**Pros:**
- ✅ Uses same translator pipeline
- ✅ Same file format = easier to handle
- ✅ Translator can detect it's tick data automatically
- ✅ Minimal code changes

**Implementation:**
```csharp
// For tick data, set Price to all OHLC columns
if (isTickChart)
{
    double price = Close[0]; // Use close price as the tick price
    line = $"{exportTime:yyyy-MM-dd},{exportTime:HH:mm:ss.fff},{price:F2},{price:F2},{price:F2},{price:F2},{Volume[0]:F1},{instrumentName}";
}
```

### Option 2: Different Format for Tick Data

Create separate header for tick data:
```csv
Date,Time,Price,Volume,Instrument
```

**Pros:**
- ✅ More accurate representation
- ✅ Smaller file size (fewer columns)

**Cons:**
- ⚠️ Need to modify translator to handle this format
- ⚠️ Two different formats to maintain

## File Naming

For tick data exports, you could use a different naming pattern:

**Minute Data:**
```
MinuteDataExport_ES_20250920_082412_UTC.csv
```

**Tick Data (Suggested):**
```
TickDataExport_ES_20250920_082412_UTC.csv
```

Or keep the same pattern and let the translator detect it:
```
MinuteDataExport_ES_20250920_082412_UTC.csv  (but contains tick data - detected automatically)
```

## Performance Considerations

**Tick data exports are much larger:**
- **Minute data**: ~1 bar per minute = ~390 bars per day
- **Tick data**: Thousands/millions of ticks per day

**Recommendations:**
- Export in smaller date ranges
- Monitor file size (500MB limit might be hit faster)
- Consider compression or splitting files

## Summary

**Yes, the exporter can be modified to export tick data**, but:

1. ✅ **Use same CSV format** (Date,Time,Open,High,Low,Close,Volume,Instrument) - easiest option
2. ✅ **Set Price to all OHLC columns** for tick data
3. ✅ **Include milliseconds** in timestamp for precision
4. ✅ **Translator will auto-detect** tick vs minute data
5. ✅ **Minimal code changes needed**

The translator already supports tick data, so once exported, it will be processed correctly!




