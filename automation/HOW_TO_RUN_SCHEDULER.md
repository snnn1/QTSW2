# How to Run the Pipeline Scheduler

## 🎯 Quick Answer: Yes, you can run it through the dashboard!

The easiest way is to use the **Pipeline Dashboard**.

---

## 📊 Option 1: Run Through Dashboard (Recommended)

### Step 1: Start the Dashboard
Double-click: `batch/START_DASHBOARD.bat`

Or manually:
1. **Backend**: Open PowerShell/CMD and run:
   ```powershell
   cd C:\Users\jakej\QTSW2\dashboard\backend
   python main.py
   ```

2. **Frontend**: Open a NEW PowerShell/CMD and run:
   ```powershell
   cd C:\Users\jakej\QTSW2\dashboard\frontend
   npm run dev
   ```

### Step 2: Open Dashboard
Open your browser: **http://localhost:5173**

### Step 3: Run Pipeline
Click the **"Run Now"** button in the dashboard.

The dashboard will:
- ✅ Show real-time progress
- ✅ Display file counts
- ✅ Show export status
- ✅ Monitor translator progress

---

## 💻 Option 2: Command Line

### Run Immediately (Process Existing Files)
```powershell
cd C:\Users\jakej\QTSW2
python automation/daily_data_pipeline_scheduler.py --now
```

This will:
- ✅ Find all CSV files in `data/raw/`
- ✅ Process them through the translator
- ✅ Put processed files in `data/processed/`

### Run with Export Waiting
If you want to wait for new exports from NinjaTrader:
```powershell
python automation/daily_data_pipeline_scheduler.py --now --wait-for-export
```

### Launch NinjaTrader + Wait for Exports
Full automated flow:
```powershell
python automation/daily_data_pipeline_scheduler.py --now --launch-ninjatrader --wait-for-export
```

This will:
1. Launch NinjaTrader
2. Wait for exports to complete
3. Process files through translator
4. Run analyzer

### Run on Schedule (Background Service)
To run automatically every day at 7:30 AM Chicago time:
```powershell
python automation/daily_data_pipeline_scheduler.py --schedule 07:30
```

To change the schedule time:
```powershell
python automation/daily_data_pipeline_scheduler.py --schedule 08:00
```

### Test Configuration
Check if everything is set up correctly:
```powershell
python automation/daily_data_pipeline_scheduler.py --test
```

---

## 📋 Command Options Summary

| Command | What It Does |
|---------|-------------|
| `--now` | Run immediately (process existing files) |
| `--now --wait-for-export` | Wait for new exports before processing |
| `--now --launch-ninjatrader --wait-for-export` | Full automated flow |
| `--schedule 07:30` | Run automatically at scheduled time |
| `--test` | Check configuration without running |

---

## 🔍 What Happens When You Run It?

1. **File Detection**: Finds all CSV files in `data/raw/` (including `DataExport_*.csv`)
2. **Translation**: Processes files through translator
3. **Output**: Creates processed files in `data/processed/`
4. **Archiving**: Moves processed CSV files to `data/archived/`
5. **Analysis**: Runs analyzer on processed data (if configured)

---

## 🐛 Troubleshooting

### "No CSV files found"
- Check that files exist in `C:\Users\jakej\QTSW2\data\raw\`
- Files should be named `DataExport_*.csv`, `MinuteDataExport_*.csv`, or `TickDataExport_*.csv`

### "Translator failed"
- Check the log file in `automation/logs/`
- Verify Python dependencies are installed: `pip install -r requirements.txt`

### Dashboard won't start
- See `dashboard/QUICK_START.md` for troubleshooting
- Make sure backend (port 8000) and frontend (port 5173) are available

---

## 📝 Notes

- **Default behavior**: `--now` processes existing files without waiting
- **Scheduled runs**: Automatically launch NinjaTrader and wait for exports
- **File patterns**: Now detects `DataExport_*.csv` files (fixed!)
- **Logs**: Check `automation/logs/` for detailed execution logs



