# Pipeline Dashboard Setup Guide

## Prerequisites

1. **Python 3.8+** with pip
2. **Node.js 16+** with npm
3. **All Python dependencies** from main `requirements.txt`

## Installation Steps

### 1. Install Python Dependencies

From the project root:
```bash
pip install -r requirements.txt
```

This installs:
- FastAPI
- Uvicorn
- WebSockets
- All other project dependencies

### 2. Install Frontend Dependencies

```bash
cd dashboard/frontend
npm install
```

This installs:
- React
- Vite
- Tailwind CSS
- All frontend dependencies

## Running the Dashboard

### Option 1: Start Everything (Easiest)

Double-click: `batch/START_DASHBOARD.bat`

This starts both backend and frontend in separate windows.

### Option 2: Manual Start

**Backend:**
```bash
cd dashboard/backend
python main.py
```
Or:
```bash
uvicorn dashboard.backend.main:app --reload --port 8000
```

**Frontend:**
```bash
cd dashboard/frontend
npm run dev
```

### Option 3: Use Batch Files

- `dashboard/START_BACKEND.bat` - Start backend only
- `dashboard/frontend/START_FRONTEND.bat` - Start frontend only

## Accessing the Dashboard

- **Frontend**: http://localhost:5173
- **Backend API**: http://localhost:8000
- **API Docs**: http://localhost:8000/docs (Swagger UI)

## Features

✅ **Manual Pipeline Start**: Click "Run Now" to start pipeline immediately  
✅ **Schedule Management**: Update daily scheduled time  
✅ **Real-time Monitoring**: Live events, stage updates, file counts  
✅ **Failure Alerts**: Automatic popup alerts on errors  
✅ **Persistent Pipeline**: Pipeline continues even if GUI closes  

## Troubleshooting

### Backend won't start
- Check Python version: `python --version` (need 3.8+)
- Install dependencies: `pip install -r requirements.txt`
- Check port 8000 is not in use

### Frontend won't start
- Check Node.js version: `node --version` (need 16+)
- Install dependencies: `cd dashboard/frontend && npm install`
- Check port 5173 is not in use

### WebSocket connection fails
- Ensure backend is running on port 8000
- Check browser console for errors
- Verify CORS settings in backend

### No events showing
- Check that pipeline is actually running
- Verify event log file exists in `automation/logs/events/`
- Check backend console for errors

## Architecture

```
Dashboard Frontend (React)
    ↕ WebSocket + REST API
Dashboard Backend (FastAPI)
    ↕ Reads Event Log
Pipeline Scheduler (Python)
    ↕ Writes Events
Event Log File (JSONL)
```

## Event Log Format

Events are written as newline-separated JSON:
```json
{"run_id": "uuid", "stage": "translator", "event": "start", "msg": "...", "timestamp": "..."}
{"run_id": "uuid", "stage": "translator", "event": "metric", "data": {...}, "timestamp": "..."}
{"run_id": "uuid", "stage": "translator", "event": "success", "msg": "...", "timestamp": "..."}
```

## Next Steps

1. Start the dashboard
2. Click "Run Now" to test a pipeline run
3. Watch real-time events appear
4. Update scheduled time as needed

