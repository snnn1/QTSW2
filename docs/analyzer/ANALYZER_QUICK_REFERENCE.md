# Analyzer Quick Reference Guide

## Quick Start

### Running the Analyzer

**Via Streamlit UI**:
```bash
cd modules/analyzer
streamlit run analyzer_app/app.py
```

**Via Python Script**:
```python
from breakout_core.engine import run_strategy
from logic.config_logic import RunParams
import pandas as pd

# Load data
df = pd.read_parquet("data/processed/ES_2006-2025.parquet")

# Configure run
rp = RunParams(
    instrument="ES",
    enabled_sessions=["S1", "S2"],
    enabled_slots={"S1": ["07:30", "08:00"], "S2": ["09:30", "10:00"]},
    trade_days=[0, 1, 2, 3, 4],  # Mon-Fri
    write_no_trade_rows=True
)

# Run analysis
results = run_strategy(df, rp, debug=False)
```

## Key Concepts

### Sessions & Slots

**S1 (Overnight)**:
- Start: 02:00
- Slots: 07:30, 08:00, 09:00
- Range: 02:00 to slot end

**S2 (Regular Hours)**:
- Start: 08:00
- Slots: 09:30, 10:00, 10:30, 11:00
- Range: 08:00 to slot end

### Breakout Strategy

1. **Calculate Range**: High/Low from session start to slot end
2. **Breakout Levels**: 1 tick above/below range
3. **Entry**: First breakout (Long or Short)
4. **Target**: Base target (e.g., 10 pts for ES)
5. **Stop Loss**: Range-based, max 3× target
6. **T1 Trigger**: 65% of target → Move stop to break-even
7. **Exit**: Target, stop, or time expiry

### Result Types

- **Win**: Target hit → Full target profit
- **BE**: T1 triggered + stop hit → 1 tick loss
- **Loss**: Stop hit without T1 → Actual loss
- **TIME**: Time expired → Actual PnL
- **NoTrade**: No entry occurred

## Instrument Configuration

| Instrument | Tick | Base Target | Ladder |
|------------|------|-------------|--------|
| ES         | 0.25 | 10          | 10-40  |
| NQ         | 0.25 | 50          | 50-200 |
| YM         | 1.0  | 100         | 100-400 |
| CL         | 0.01 | 0.5         | 0.5-2.0 |
| NG         | 0.001| 0.05        | 0.05-0.20 |
| GC         | 0.1  | 5           | 5-20   |

**Micro-Futures**: Same ladder, profit scaled 1/10th

## Common Patterns

### Filter by Year
```python
df['year'] = pd.to_datetime(df['timestamp']).dt.year
df = df[df['year'] == 2025]
```

### Process Single Session
```python
rp = RunParams(
    instrument="ES",
    enabled_sessions=["S1"],  # Only S1
    enabled_slots={"S1": ["08:00"]},  # Only 08:00 slot
    trade_days=[0, 1, 2, 3, 4]
)
```

### Debug Mode
```python
results = run_strategy(df, rp, debug=True)  # Detailed output
```

## File Locations

- **Input**: `data/processed/*.parquet`
- **Output (Manual)**: `data/manual_analyzer_runs/{date}/`
- **Output (Auto)**: `data/analyzer_temp/`
- **Logs**: `modules/analyzer/data/analyzer_runs/logs/`

## Troubleshooting

### No Ranges Found
- Check if data contains trading days (Mon-Fri)
- Verify timezone is America/Chicago
- Check if slot times match data timestamps

### No Trades Generated
- Verify breakout levels are reasonable
- Check if data extends past slot end time
- Enable debug mode to see entry detection

### Timezone Issues
- Data should be in America/Chicago timezone
- Check timestamp column: `df['timestamp'].dt.tz`

### Performance Issues
- Use year filtering for large datasets
- Process instruments separately
- Enable optimizations (if available)

## Key Functions

### Range Detection
```python
from logic.range_logic import RangeDetector

detector = RangeDetector(slot_config)
ranges = detector.build_slot_ranges(df, rp, debug=False)
```

### Entry Detection
```python
from logic.entry_logic import EntryDetector

detector = EntryDetector()
entry_result = detector.detect_entry(df, range_result, brk_long, brk_short, freeze_close, end_ts)
```

### Trade Execution
```python
from logic.price_tracking_logic import PriceTracker

tracker = PriceTracker(debug_manager)
execution = tracker.execute_trade(df, entry_time, entry_price, direction, target_level, stop_loss, expiry_time, ...)
```

## Output Schema

**Result DataFrame Columns**:
- `Date`: Trading date (YYYY-MM-DD)
- `Time`: Slot time (HH:MM)
- `EntryTime`: Entry timestamp (DD/MM/YY HH:MM)
- `ExitTime`: Exit timestamp (DD/MM/YY HH:MM)
- `Target`: Target points
- `Peak`: Maximum favorable excursion
- `Direction`: "Long", "Short", or "NA"
- `Result`: "Win", "BE", "Loss", "TIME", or "NoTrade"
- `Range`: Range size in points
- `Stream`: Stream tag (e.g., "ES1", "ES2")
- `Instrument`: Instrument code
- `Session`: "S1" or "S2"
- `Profit`: Profit in points

## Performance Tips

1. **Year Filtering**: Filter data before processing
2. **Slot Selection**: Only enable needed slots
3. **Parallel Processing**: Use `run_analyzer_parallel.py` for multiple instruments
4. **Caching**: Enable optimizations for repeated runs
5. **Memory**: Process large datasets in chunks

## Integration

### With Pipeline
```python
# Analyzer runs after Translator
# Results go to analyzer_temp/ for Data Merger
```

### With Data Merger
```python
# Merger combines analyzer results
# Creates master matrix with all instruments
```

## Debug Output

Enable debug mode to see:
- Range calculations
- Entry detection details
- MFE tracking
- T1 trigger events
- Trade execution decisions
- Performance timing

## Common Issues & Solutions

| Issue | Solution |
|-------|----------|
| No ranges found | Check trade_days, timezone, data coverage |
| All NoTrade | Check breakout levels, data after slot end |
| Wrong timezone | Ensure data is America/Chicago |
| Memory errors | Use year filtering, process separately |
| Slow performance | Enable optimizations, filter data first |

















