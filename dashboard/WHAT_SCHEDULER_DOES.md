# What the Scheduler Does

## 🎯 Purpose

The scheduler is a **background process** that automatically runs your data pipeline at regular intervals (every 15 minutes).

## 📋 What It Does

### 1. **Runs Continuously**
- Starts when backend starts (auto-start)
- Runs in the background
- Never stops (until backend stops)

### 2. **Runs Pipeline Every 15 Minutes**
- Waits for next 15-minute mark (:00, :15, :30, :45)
- Example: If it's 23:47, waits until 00:00
- Then runs pipeline
- Then waits for 00:15, runs again
- Then waits for 00:30, runs again
- And so on...

### 3. **What Pipeline Does**
When scheduler triggers a run, it:
1. **Checks for raw files** in `data/raw/`
2. **Runs Translator** (if raw files exist)
   - Converts CSV → Parquet
   - Processes and cleans data
3. **Runs Analyzer** (if processed files exist)
   - Analyzes breakout patterns
   - Processes all instruments in parallel
4. **Runs Data Merger** (after analyzer)
   - Merges daily files into monthly files
   - Consolidates data

### 4. **No Manual Intervention Needed**
- You don't need to click "Run Now"
- It just runs automatically
- Processes whatever files are in `data/raw/`

## 🔄 Schedule Pattern

```
Time    | Action
--------|------------------
23:00   | Wait...
23:15   | Wait...
23:30   | Wait...
23:45   | Wait...
00:00   | ✅ RUN PIPELINE
00:15   | ✅ RUN PIPELINE
00:30   | ✅ RUN PIPELINE
00:45   | ✅ RUN PIPELINE
01:00   | ✅ RUN PIPELINE
...     | (continues forever)
```

## 📊 Example Flow

### At 00:00:
1. Scheduler wakes up
2. Checks `data/raw/` → Finds 6 CSV files
3. Runs Translator → Converts to Parquet
4. Runs Analyzer → Analyzes all instruments
5. Runs Merger → Consolidates results
6. Done! Waits for 00:15

### At 00:15:
1. Scheduler wakes up again
2. Checks `data/raw/` → No new files
3. Checks `data/processed/` → Has files from previous run
4. Skips Translator (no raw files)
5. Runs Analyzer → Re-analyzes processed files
6. Runs Merger → Updates consolidated results
7. Done! Waits for 00:30

## ⚙️ Configuration

**Schedule Time**: Currently set to "07:30" (but doesn't matter - runs every 15 min)

**Note**: The schedule time in config is ignored - scheduler always runs every 15 minutes.

## 🎯 Why Every 15 Minutes?

- **Frequent enough**: Catches new data quickly
- **Not too frequent**: Doesn't overload system
- **Predictable**: Easy to know when it runs
- **Flexible**: Can process data as it arrives

## 📝 What You See

### In Dashboard:
- **Live Events**: Shows events from each scheduled run
- **Pipeline Status**: Shows if pipeline is currently running
- **File Counts**: Updates after each run

### In Logs:
- `automation/logs/pipeline_*.log` - Detailed logs
- `automation/logs/events/pipeline_*.jsonl` - Event logs

## 🔍 Current Status

**Scheduler Status**: Check via `/api/scheduler/status`
- `running: true` = Scheduler is active
- `running: false` = Scheduler stopped/crashed

**Pipeline Status**: Check via `/api/pipeline/status`
- `active: true` = Pipeline currently running
- `active: false` = No active pipeline

## 🛠️ Troubleshooting

### Scheduler Not Running?
1. Check backend logs for errors
2. Restart backend (scheduler auto-starts)
3. Check `/api/scheduler/status` endpoint

### No Events Showing?
1. Scheduler might not be running
2. Check if event log files are being created
3. Check `automation/logs/events/` folder

### Pipeline Not Running?
1. Check if files exist in `data/raw/` or `data/processed/`
2. Scheduler only runs if there are files to process
3. Check scheduler logs for errors

---

## Summary

**The scheduler is an automated background process that:**
- ✅ Runs continuously
- ✅ Triggers pipeline every 15 minutes
- ✅ Processes whatever files are available
- ✅ No manual intervention needed
- ✅ Keeps your data up-to-date automatically

**Think of it as:** A robot that checks for new data every 15 minutes and processes it automatically!



