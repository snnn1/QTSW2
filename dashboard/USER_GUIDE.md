# Pipeline Dashboard - Complete User Guide

## 🎯 What It Does

The Pipeline Dashboard is a **real-time monitoring and control system** for your data processing pipeline. It provides:

- **Visual monitoring** of all pipeline stages (Export → Translator → Analyzer)
- **Real-time progress tracking** with live updates
- **Manual pipeline control** (start runs on demand)
- **Schedule management** (set daily run times)
- **Failure alerts** and error notifications
- **Complete audit trail** of all pipeline activities

---

## 🏗️ Architecture Overview

### Components

```
┌─────────────────────────────────────────────────────────┐
│              Pipeline Dashboard System                  │
├─────────────────────────────────────────────────────────┤
│                                                         │
│  ┌──────────────┐         ┌──────────────┐              │
│  │   Frontend   │ ◄─────► │   Backend   │              │
│  │  (React)     │ WebSocket│  (FastAPI)  │              │
│  │  Port 5173   │         │  Port 8000  │              │
│  └──────────────┘         └──────┬───────┘              │
│                                  │                       │
│                                  │ Reads                 │
│                                  ▼                       │
│                          ┌──────────────┐               │
│                          │  Event Log   │               │
│                          │   (JSONL)    │               │
│                          └──────┬───────┘               │
│                                  │                       │
│                                  │ Writes                │
│                                  ▼                       │
│                          ┌──────────────┐               │
│                          │   Pipeline   │               │
│                          │  Scheduler   │               │
│                          └──────────────┘               │
│                                                         │
└─────────────────────────────────────────────────────────┘
```

### Data Flow

1. **User clicks "Run Now"** → Backend launches pipeline
2. **Pipeline runs** → Emits structured events to log file
3. **Backend tails log** → Streams events via WebSocket
4. **Frontend receives** → Updates UI in real-time

---

## 🚀 Setup Instructions

### Prerequisites

1. **Python 3.8+** with pip
2. **Node.js 16+** with npm
3. **All Python dependencies** installed

### Step 1: Install Python Dependencies

```bash
pip install -r requirements.txt
```

This installs:
- FastAPI (backend server)
- Uvicorn (ASGI server)
- WebSockets (real-time communication)
- All other project dependencies

### Step 2: Install Frontend Dependencies

```bash
cd dashboard/frontend
npm install
```

This installs:
- React
- Vite (build tool)
- Tailwind CSS (styling)

### Step 3: Start the Dashboard

**Option A: Use Batch File (Easiest)**
- Double-click: `batch/START_DASHBOARD.bat`
- This starts both backend and frontend automatically

**Option B: Manual Start**

**Terminal 1 - Backend:**
```bash
cd dashboard/backend
python main.py
```

**Terminal 2 - Frontend:**
```bash
cd dashboard/frontend
npm run dev
```

### Step 4: Open Dashboard

Open your browser to: **http://localhost:5173**

---

## 📊 Dashboard Interface

### Main Sections

#### 1. **Controls Panel** (Top)
- **Schedule Time**: Set daily automated run time (HH:MM format)
- **Run Now Button**: Start pipeline immediately

#### 2. **Metrics Cards** (Top Row)
- **Run ID**: Current pipeline run identifier
- **Current Stage**: Active pipeline stage (export, translator, analyzer, etc.)
- **Raw Files**: Count of files in `data/raw/`
- **Processed Files**: Count of files in `data/processed/`

#### 3. **Export Stage Panel** (New!)
- **Status**: Not Started / Active / Complete / Failed
- **Progress Bar**: Visual progress indicator (when active)
- **Rows Processed**: Total records exported
- **Files**: Number of export files created
- **Instrument**: Trading instrument being exported (ES, NQ, etc.)

#### 4. **Events Log** (Bottom)
- **Live Event Stream**: Real-time pipeline events
- **Color Coding**:
  - 🟢 Green = Success
  - 🔴 Red = Failure
  - ⚪ Gray = Info/Log

---

## 🎮 How to Use

### Starting a Pipeline Run

1. **Click "Run Now"** button
2. Dashboard shows:
   - Run ID appears
   - Current Stage updates
   - Export stage becomes active

### Monitoring Progress

**Export Stage:**
- Watch rows processed increase
- See file count update
- Progress bar shows completion percentage
- Status changes: Not Started → Active → Complete

**Translator Stage:**
- File counts update
- Events show processing status
- Completion message appears

**Analyzer Stage:**
- Per-instrument progress
- Success/failure for each instrument

### Setting Schedule

1. **Enter time** in HH:MM format (24-hour, e.g., "07:30")
2. **Click "Update"**
3. Schedule saved for daily automated runs

### Viewing Events

- **Scroll** through events log
- **Expand** data sections to see details
- **Filter** by stage using event colors

---

## 🔄 Complete Pipeline Flow

### Stage 1: Export
1. **Conductor launches NinjaTrader**
2. **DataExporter indicator runs**
3. **Exports CSV files** to `data/raw/`
4. **Creates signal files**:
   - `export_start_*.json` - Export begins
   - `export_progress_*.json` - Progress updates
   - `export_complete_*.json` - Export finished
5. **Dashboard shows**:
   - Export status: Active
   - Rows processed: Updates live
   - Files: Count increases
   - Progress bar: Fills up

### Stage 2: Translator
1. **Conductor detects export completion**
2. **Runs translator** on raw CSV files
3. **Processes data**:
   - Converts timezones (UTC → Chicago)
   - Removes duplicates
   - Validates data
   - Handles contract rollovers
4. **Saves processed files** to `data/processed/`
5. **Dashboard shows**:
   - Translator status: Active → Complete
   - File counts update
   - Events show processing

### Stage 3: Analyzer
1. **Runs breakout analyzer** on processed data
2. **Processes each instrument** (ES, NQ, YM, etc.)
3. **Generates analysis results**
4. **Dashboard shows**:
   - Analyzer status: Active
   - Per-instrument progress
   - Completion status

### Stage 4: Audit
1. **Generates audit report**
2. **Saves execution summary**
3. **Dashboard shows**:
   - Pipeline complete
   - Success/failure status

---

## 📡 Real-Time Features

### WebSocket Connection
- **Automatic connection** when pipeline starts
- **Auto-reconnect** if connection drops
- **Live updates** every 30 seconds (progress events)
- **Instant updates** for stage changes

### Event Types

**Start Events:**
- `export/start` - Export begins
- `translator/start` - Translation begins
- `analyzer/start` - Analysis begins

**Progress Events:**
- `export/metric` - Export progress (rows, files, size)
- `translator/metric` - Translation progress
- `analyzer/metric` - Analysis progress

**Completion Events:**
- `export/success` - Export complete
- `translator/success` - Translation complete
- `analyzer/success` - Analysis complete

**Failure Events:**
- `export/failure` - Export failed/stalled
- `translator/failure` - Translation failed
- `analyzer/failure` - Analysis failed

---

## 🚨 Alerts & Notifications

### Failure Alerts
- **Automatic popup** when any stage fails
- **Red alert box** in top-right corner
- **Auto-dismisses** after 5 seconds
- **Click X** to dismiss manually

### Error Detection
- **Export timeout**: No completion after 60 minutes
- **Export stalled**: File not growing for 5+ minutes
- **Translator failure**: Processing errors
- **Analyzer failure**: Per-instrument failures

---

## ⚙️ Configuration

### Backend Configuration

Edit `automation/daily_data_pipeline_scheduler.py`:

```python
# Paths
QTSW_ROOT = Path(r"C:\Users\jakej\QTSW")
QTSW2_ROOT = Path(r"C:\Users\jakej\QTSW2")

# NinjaTrader
NINJATRADER_EXE = Path(r"C:\Program Files\NinjaTrader 8\bin\NinjaTrader.exe")
NINJATRADER_WORKSPACE = Path(r"C:\Users\jakej\Documents\NinjaTrader 8\workspaces\DataExport.ntworkspace")
```

### Schedule Configuration

- **Default**: 07:30 CT (Chicago Time)
- **Change via**: Dashboard UI or edit `automation/schedule_config.json`
- **Format**: HH:MM (24-hour)

---

## 🔍 Troubleshooting

### Dashboard Won't Start

**Backend Issues:**
- Check Python version: `python --version` (need 3.8+)
- Install dependencies: `pip install -r requirements.txt`
- Check port 8000 is available

**Frontend Issues:**
- Check Node.js: `node --version` (need 16+)
- Install dependencies: `cd dashboard/frontend && npm install`
- Check port 5173 is available

### No Events Showing

1. **Check backend is running** (http://localhost:8000)
2. **Check WebSocket connection** (browser console)
3. **Verify pipeline is running** (check logs)
4. **Check event log file exists** in `automation/logs/events/`

### Export Not Detected

1. **Check DataExporter** is running in NinjaTrader
2. **Verify export path**: `QTSW2/data/raw/`
3. **Check signal files** are being created
4. **Review conductor logs** for detection attempts

### Pipeline Stuck

1. **Check current stage** in dashboard
2. **Review events log** for errors
3. **Check backend logs** for details
4. **Verify file permissions** on data directories

---

## 📁 File Locations

### Event Logs
- **Location**: `automation/logs/events/`
- **Format**: `pipeline_{run_id}.jsonl`
- **Content**: Structured JSON events (one per line)

### Schedule Config
- **Location**: `automation/schedule_config.json`
- **Format**: JSON with `schedule_time` field

### Data Directories
- **Raw Data**: `QTSW2/data/raw/`
- **Processed Data**: `QTSW/data_processed/`
- **Export Signals**: `QTSW2/data/raw/export_*.json`

---

## 🎯 Key Features Summary

✅ **Real-time monitoring** - Live updates via WebSocket  
✅ **Export stage tracking** - Full visibility into data export  
✅ **Progress indicators** - Visual progress bars and metrics  
✅ **Failure alerts** - Automatic error notifications  
✅ **Schedule management** - Set daily run times  
✅ **Manual control** - Start pipelines on demand  
✅ **Complete audit trail** - All events logged  
✅ **Persistent pipeline** - Continues even if GUI closes  

---

## 🚀 Quick Start Checklist

- [ ] Install Python dependencies: `pip install -r requirements.txt`
- [ ] Install Node.js dependencies: `cd dashboard/frontend && npm install`
- [ ] Start backend: `python dashboard/backend/main.py`
- [ ] Start frontend: `cd dashboard/frontend && npm run dev`
- [ ] Open browser: http://localhost:5173
- [ ] Click "Run Now" to test
- [ ] Watch export stage progress
- [ ] Monitor pipeline completion

---

## 📞 Support

For issues or questions:
1. Check logs in `automation/logs/`
2. Review events in dashboard
3. Check browser console for errors
4. Verify all paths are correct in configuration

The dashboard provides complete visibility into your pipeline operations!






