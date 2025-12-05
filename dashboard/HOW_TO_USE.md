# How to Use the Dashboard & Orchestrator

## Quick Start (3 Steps)

### Step 1: Start the Backend

**Option A: Use the batch file (easiest)**
```bash
dashboard\START_ORCHESTRATOR.bat
```

**Option B: Manual start**
```bash
cd C:\Users\jakej\QTSW2
python -m uvicorn dashboard.backend.main:app --reload --host 0.0.0.0 --port 8000
```

**What happens:**
- Backend starts on port 8000
- Orchestrator initializes
- Scheduler starts (runs every 15 minutes)
- Watchdog starts (monitors health)
- You'll see: "Pipeline Orchestrator started"

**Keep this window open!**

---

### Step 2: Start the Frontend (Optional)

**Open a new terminal:**
```bash
cd dashboard\frontend
npm run dev
```

**What happens:**
- Frontend starts on port 5173
- Opens in browser automatically
- Connects to backend via WebSocket

**Or just open in browser:**
- http://localhost:5173

---

### Step 3: Use the Dashboard

**The dashboard will:**
- ✅ Show current pipeline status
- ✅ Display real-time events
- ✅ Show metrics (processing rates, file counts)
- ✅ Allow you to start/stop pipeline manually
- ✅ Show next scheduled run time

---

## What Happens Automatically

### Every 15 Minutes (Automatic)

The scheduler automatically runs the pipeline at:
- :00 (e.g., 12:00, 1:00, 2:00)
- :15 (e.g., 12:15, 1:15, 2:15)
- :30 (e.g., 12:30, 1:30, 2:30)
- :45 (e.g., 12:45, 1:45, 2:45)

**You don't need to do anything** - it just runs!

---

## Manual Controls

### Start Pipeline Now

**Via Dashboard:**
- Click "Start Pipeline" button

**Via API:**
```powershell
Invoke-WebRequest -Uri "http://localhost:8000/api/pipeline/start" -Method POST -UseBasicParsing
```

**Via Browser:**
- Go to: http://localhost:8000/docs
- Find: `POST /api/pipeline/start`
- Click "Try it out" → "Execute"

### Check Status

**Via Dashboard:**
- Status shown at top of page

**Via API:**
```powershell
Invoke-WebRequest -Uri "http://localhost:8000/api/pipeline/status" -UseBasicParsing
```

**Via Browser:**
- http://localhost:8000/api/pipeline/status

### Run Individual Stage

**Via Dashboard:**
- Click stage button (Translator, Analyzer, Merger)

**Via API:**
```powershell
# Run translator
Invoke-WebRequest -Uri "http://localhost:8000/api/pipeline/stage/translator" -Method POST -UseBasicParsing

# Run analyzer
Invoke-WebRequest -Uri "http://localhost:8000/api/pipeline/stage/analyzer" -Method POST -UseBasicParsing

# Run merger
Invoke-WebRequest -Uri "http://localhost:8000/api/pipeline/stage/merger" -Method POST -UseBasicParsing
```

### Stop Pipeline

**Via Dashboard:**
- Click "Stop Pipeline" button

**Via API:**
```powershell
Invoke-WebRequest -Uri "http://localhost:8000/api/pipeline/stop" -Method POST -UseBasicParsing
```

---

## Monitoring

### View Events in Real-Time

**Dashboard:**
- Events log panel shows all events as they happen
- Scrolls automatically
- Color-coded by stage

**Log Files:**
- Location: `automation\logs\events\`
- Files: `pipeline_{run_id}.jsonl`
- One file per pipeline run
- JSON format, one event per line

### View Backend Logs

**Log File:**
- Location: `logs\backend_debug.log`
- Contains all backend activity
- Includes orchestrator logs

### Check Next Scheduled Run

**Dashboard:**
- Shows "Next run: HH:MM (X minutes)"

**API:**
```powershell
Invoke-WebRequest -Uri "http://localhost:8000/api/pipeline/status" -UseBasicParsing
```

---

## Common Tasks

### 1. Start Everything

```bash
# Terminal 1: Backend
dashboard\START_ORCHESTRATOR.bat

# Terminal 2: Frontend (optional)
cd dashboard\frontend
npm run dev
```

### 2. Check if Backend is Running

```powershell
# Check if port 8000 is listening
netstat -an | findstr "8000"

# Or test API
Invoke-WebRequest -Uri "http://localhost:8000/api/pipeline/status" -UseBasicParsing
```

### 3. View Current Status

**Browser:**
- http://localhost:8000/api/pipeline/status
- http://localhost:8000/api/pipeline/snapshot (more detailed)

**Dashboard:**
- Open http://localhost:5173
- Status shown at top

### 4. Manually Trigger Pipeline

**Dashboard:**
- Click "Start Pipeline"

**API:**
```powershell
Invoke-WebRequest -Uri "http://localhost:8000/api/pipeline/start" -Method POST -UseBasicParsing
```

### 5. Stop Everything

**Backend:**
- Press `Ctrl+C` in backend terminal

**Frontend:**
- Press `Ctrl+C` in frontend terminal
- Or just close browser

---

## Troubleshooting

### Backend Won't Start

**Check:**
1. Is port 8000 already in use?
   ```powershell
   netstat -an | findstr "8000"
   ```
2. Are there Python errors in the console?
3. Check `logs\backend_debug.log` for errors

**Fix:**
- Kill process using port 8000
- Check Python dependencies are installed
- Verify paths are correct

### Dashboard Can't Connect

**Check:**
1. Is backend running? (http://localhost:8000)
2. Check browser console for errors (F12)
3. Check WebSocket connection

**Fix:**
- Start backend first
- Check CORS settings
- Verify ports match (8000 for backend, 5173 for frontend)

### Pipeline Not Running Automatically

**Check:**
1. Is scheduler started? (check backend logs)
2. Is orchestrator initialized? (check logs)
3. Check next run time

**Fix:**
- Restart backend
- Check `automation\schedule_config.json` exists
- Verify orchestrator started successfully

### Events Not Appearing

**Check:**
1. Is WebSocket connected? (check browser console)
2. Are events being written to files?
   - Check: `automation\logs\events\pipeline_*.jsonl`
3. Is EventBus working? (check backend logs)

**Fix:**
- Refresh dashboard
- Check WebSocket connection
- Verify event files are being created

---

## API Endpoints Reference

### Pipeline Control
- `GET /api/pipeline/status` - Get current status
- `GET /api/pipeline/snapshot` - Get status + recent events
- `POST /api/pipeline/start` - Start pipeline
- `POST /api/pipeline/stop` - Stop pipeline
- `POST /api/pipeline/reset` - Reset pipeline state
- `POST /api/pipeline/stage/{stage}` - Run specific stage

### Schedule
- `GET /api/schedule` - Get schedule
- `PUT /api/schedule` - Update schedule

### WebSocket
- `ws://localhost:8000/ws/events` - Real-time event stream

### Documentation
- `http://localhost:8000/docs` - Swagger UI (interactive API docs)

---

## Typical Workflow

### Daily Use

1. **Morning:**
   - Start backend: `dashboard\START_ORCHESTRATOR.bat`
   - (Optional) Start frontend: `cd dashboard\frontend && npm run dev`
   - Open dashboard: http://localhost:5173

2. **During Day:**
   - Pipeline runs automatically every 15 minutes
   - Monitor via dashboard
   - Check events log if needed

3. **Evening:**
   - Pipeline continues running automatically
   - Can leave backend running 24/7

### Manual Run

1. Open dashboard
2. Click "Start Pipeline"
3. Watch events in real-time
4. See results when complete

---

## Tips

✅ **Leave backend running** - Scheduler works 24/7  
✅ **Check dashboard periodically** - See what's happening  
✅ **Review event logs** - If something goes wrong  
✅ **Use API docs** - http://localhost:8000/docs for testing  
✅ **Check logs** - `logs\backend_debug.log` for details  

---

## Quick Reference

| Task | Command/URL |
|------|-------------|
| Start backend | `dashboard\START_ORCHESTRATOR.bat` |
| Start frontend | `cd dashboard\frontend && npm run dev` |
| Dashboard | http://localhost:5173 |
| API docs | http://localhost:8000/docs |
| Status API | http://localhost:8000/api/pipeline/status |
| Event logs | `automation\logs\events\` |
| Backend logs | `logs\backend_debug.log` |

---

**That's it! The system runs automatically every 15 minutes. Just start the backend and let it run!**

