# Tick vs Minute Data Support

## 🎯 Overview

The Data Translator now supports **both tick data and minute bar data** and can automatically differentiate between them.

## 📊 What's the Difference?

### **Tick Data**
- **Raw trade-by-trade data**
- Timestamps can be **sub-second** (e.g., every 0.5 seconds)
- High frequency - many rows per minute
- Example: Individual trades as they execute

### **Minute Data**
- **Aggregated 1-minute bars**
- One row per minute (60-second intervals)
- Lower frequency - one row per minute
- Example: OHLC bars at 09:30, 09:31, 09:32, etc.

## 🔍 Automatic Detection

The system automatically detects data type by analyzing timestamp intervals:

### **Detection Logic:**
```python
Median time difference < 60 seconds → Tick Data
Median time difference ≈ 60 seconds → 1-Minute Bars
Median time difference ≈ 300 seconds → 5-Minute Bars
```

### **Detection Methods:**

1. **Filename Hints:**
   - Files with "tick", "trade", or "tx" in name → Likely tick data
   - Files with "minute" in name → Likely minute data

2. **Timestamp Analysis:**
   - Calculates time differences between consecutive rows
   - Uses median interval to determine frequency
   - More accurate than filename alone

## 💻 Usage

### **In Code:**
```python
from translator import load_single_file, detect_data_frequency, is_tick_data

# Load file (auto-detects frequency)
df = load_single_file("data.csv")

# Check data type
if is_tick_data(df):
    print("This is tick data!")
else:
    print("This is minute data!")

# Get detailed info
frequency = detect_data_frequency(df)
print(f"Detected frequency: {frequency}")
```

### **In Streamlit App:**
The web interface automatically shows:
- **Data Type**: "Tick Data" or "Minute Data"
- **Frequency**: Exact frequency detected (tick, 1min, 5min, etc.)

## 📋 Supported Frequencies

The system can detect:
- ✅ **Tick** - Sub-second to few seconds
- ✅ **1-Minute** - 60-second intervals
- ✅ **5-Minute** - 300-second intervals
- ✅ **15-Minute** - 900-second intervals
- ✅ **1-Hour** - 3600-second intervals
- ✅ **1-Day** - Daily bars

## 🔧 Data Loading

Both tick and minute data use the same loading function:

```python
df = load_single_file("tick_data.csv")      # Tick data
df = load_single_file("minute_data.csv")    # Minute data
```

The function:
1. Loads the data
2. Auto-detects frequency
3. Adds `frequency` column to DataFrame
4. Stores frequency metadata in `df.attrs['frequency']`

## 📊 Output Files

When processing:
- **Tick data** → Saved with `frequency='tick'` column
- **Minute data** → Saved with `frequency='1min'` (or appropriate frequency)

Both formats are saved the same way - the system handles them identically.

## 🧪 Testing

Unit tests verify:
- ✅ Tick data detection
- ✅ Minute data detection
- ✅ Frequency string generation
- ✅ Data type summary functions

## 💡 Tips

1. **Tick Data Files**:
   - Usually much larger (more rows)
   - High granularity
   - Good for detailed analysis

2. **Minute Data Files**:
   - Smaller file sizes
   - Pre-aggregated
   - Good for backtesting

3. **Automatic Handling**:
   - You don't need to specify data type
   - System detects it automatically
   - Both work with the same functions

