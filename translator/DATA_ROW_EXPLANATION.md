# Understanding Translator Output Data Rows

## Your Data Row

```
64.66,64.67,64.65,64.65,16.0,DATAEXPORT,2025-01-01 17:00:00-06:00,DataExport_CL_20251127_212335_UTC,1min
```

## Column Breakdown

| Column | Value | Description |
|--------|-------|-------------|
| **1. open** | `64.66` | Opening price for this bar |
| **2. high** | `64.67` | Highest price during this bar |
| **3. low** | `64.65` | Lowest price during this bar |
| **4. close** | `64.65` | Closing price for this bar |
| **5. volume** | `16.0` | Trading volume (number of contracts) |
| **6. instrument** | `DATAEXPORT` | ⚠️ **Should be "CL"** - this looks like it came from the CSV Instrument column instead of filename |
| **7. timestamp** | `2025-01-01 17:00:00-06:00` | Time in Chicago timezone (-06:00) |
| **8. contract** | `DataExport_CL_20251127_212335_UTC` | Original filename/contract name |
| **9. frequency** | `1min` | Data frequency (1-minute bars) |

## Expected Format

The translator should output these columns in this order:

```
open, high, low, close, volume, instrument, timestamp, contract, frequency
```

### Notes

1. **Instrument should be "CL"** - Based on filename `DataExport_CL_20251127_212335_UTC`, the translator should extract "CL" as the instrument symbol. If you see "DATAEXPORT", it means the CSV's Instrument column was used instead of extracting from filename.

2. **Timestamp is in Chicago time** - The `-06:00` indicates Chicago timezone (CST), which is correct for trading data.

3. **This is CL (Crude Oil) futures data** - Price around $64.66 per barrel.

4. **1-minute bars** - The frequency column indicates this is minute-level data, not tick data.

## Checking the Data

If the instrument shows "DATAEXPORT" instead of "CL", the translator might be using the CSV's Instrument column value instead of extracting from the filename. The translator code should override this with the filename-derived value.

## Typical Output Structure

```
open,high,low,close,volume,instrument,timestamp,contract,frequency
64.66,64.67,64.65,64.65,16.0,CL,2025-01-01 17:00:00-06:00,DataExport_CL_20251127_212335_UTC,1min
```

Notice: `instrument` should be "CL", not "DATAEXPORT"









