# Installing the Modified Data Exporter in NinjaTrader

## What's Changed

The modified exporter now supports **both minute AND tick data**:
- ✅ Works on 1-minute charts (original functionality)
- ✅ Works on Tick charts (NEW)
- ✅ Automatically detects chart type
- ✅ Uses appropriate timestamp precision (milliseconds for ticks)
- ✅ Same CSV format - translator auto-detects tick vs minute

## Installation Steps

### 1. Copy the File

1. Open NinjaTrader
2. Go to **Tools** → **Edit NinjaScript** → **Indicator**
3. Find `MinuteDataExporter.cs` in the list (or create new if it doesn't exist)
4. Replace the entire contents with the code from `QTSW2/docs/MinuteDataExporter.cs`

**OR**

1. Copy `MinuteDataExporter.cs` from `QTSW2/docs/`
2. Navigate to: `Documents\NinjaTrader 8\bin\Custom\Indicators\`
3. Paste/replace the file there
4. Compile in NinjaTrader

### 2. Compile the Indicator

1. In NinjaTrader, press **F5** to compile
2. Or go to **Tools** → **Compile**
3. Check for any errors in the output window
4. Should show "NinjaScript compilation successful"

### 3. Verify Installation

1. Open any chart (1-minute or Tick)
2. Right-click chart → **Indicators**
3. Find "MinuteDataExporter" in the list
4. Add it to the chart
5. The output should show which data type it detected

## Usage

### For Minute Data:
1. Open a **1-minute chart**
2. Load historical data
3. Add `MinuteDataExporter` indicator
4. Exports to: `MinuteDataExport_{Instrument}_{timestamp}_UTC.csv`
5. **Bar time fix applied** (subtracts 1 minute for open time)

### For Tick Data:
1. Open a **Tick chart** (any tick value: 1 tick, 100 ticks, etc.)
2. Load historical data
3. Add `MinuteDataExporter` indicator
4. Exports to: `TickDataExport_{Instrument}_{timestamp}_UTC.csv`
5. **No time fix** (uses actual trade time)
6. **Includes milliseconds** in timestamp for precision

## Output Format

Both formats use the same CSV structure:
```csv
Date,Time,Open,High,Low,Close,Volume,Instrument
```

**Minute bars:**
```csv
2008-07-20,22:00:00,174.13,174.57,174.04,174.53,43.0,CL
```

**Tick data:**
```csv
2008-07-20,22:00:00.123,174.53,174.53,174.53,174.53,2.0,CL
```

Notice:
- Tick data has **milliseconds** (`.123`)
- Tick data has **same price for OHLC** (it's a single trade)
- Both formats work with the translator

## File Naming

- **Minute charts**: `MinuteDataExport_{Instrument}_{timestamp}_UTC.csv`
- **Tick charts**: `TickDataExport_{Instrument}_{timestamp}_UTC.csv`

The translator will detect the data type automatically based on:
- Time intervals between rows
- Filename patterns
- Timestamp precision

## Notes

### Tick Data Considerations:
- **Much larger files** - tick data has many more rows
- Export in **smaller date ranges** if needed
- Monitor file size (500MB limit warning still applies)
- Translator will handle it the same way as minute data

### Bar Time Convention:
- **Minute bars**: Fix applied (subtracts 1 minute)
- **Tick data**: No fix needed (uses actual trade time)

### Timestamp Precision:
- **Minute bars**: `HH:mm:ss` (no milliseconds)
- **Tick data**: `HH:mm:ss.fff` (with milliseconds)

## Troubleshooting

**"ERROR: This indicator only works on 1-minute charts OR Tick charts!"**
- Make sure you're on a 1-minute chart OR a Tick chart
- Check the chart's data series settings

**Tick data not exporting correctly:**
- Verify chart is set to Tick period type
- Check that timestamps include milliseconds in output
- Verify price values are being exported

**File not created:**
- Check Documents folder permissions
- Verify path is accessible
- Look for error messages in NinjaTrader output window

---

## What the Translator Does

After export, use the QTSW2 Data Translator to process the files:

1. **Detects data type** automatically (tick vs minute)
2. **Handles format** - works with both
3. **Converts timezone** - UTC → Chicago
4. **Validates data** - checks for issues
5. **Creates Parquet** - optimized storage

The translator doesn't care whether it's `MinuteDataExport_*.csv` or `TickDataExport_*.csv` - it detects the data type from the content itself!




