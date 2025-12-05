# How to Start the Dashboard Backend

## Quick Start (Easiest)

### Option 1: Use the Batch File
Double-click or run:
```batch
dashboard\START_ORCHESTRATOR.bat
```

### Option 2: Command Line
```bash
cd dashboard\backend
python -m uvicorn main:app --reload
```

## What Happens When You Start

1. **Backend starts** on `http://localhost:8000`
2. **Orchestrator initializes** (you'll see "Pipeline Orchestrator started" in logs)
3. **Scheduler starts** (runs in background)
4. **Watchdog starts** (monitors health)
5. **API endpoints** become available

## Check if It's Working

### 1. Look for Success Messages
In the console, you should see:
```
Pipeline Dashboard API started
Pipeline Orchestrator started
Scheduler started
Watchdog started
```

### 2. Test the API
Open in browser or use curl:
- **API Root**: http://localhost:8000
- **API Docs**: http://localhost:8000/docs
- **Status**: http://localhost:8000/api/pipeline/status

### 3. Check for Errors
If you see errors:
- **Import errors** → Check Python path
- **Module not found** → Install dependencies
- **Port already in use** → Stop other instance on port 8000

## Starting Frontend (Optional)

If you want the full dashboard:

```bash
cd dashboard\frontend
npm run dev
```

Then open: http://localhost:5173

## Troubleshooting

### Port Already in Use
```bash
# Find what's using port 8000
netstat -ano | findstr :8000

# Kill the process (replace PID with actual process ID)
taskkill /PID <PID> /F
```

### Python Not Found
Make sure Python is in your PATH or use full path:
```bash
C:\Python\python.exe -m uvicorn main:app --reload
```

### Import Errors
Make sure you're in the right directory:
```bash
cd C:\Users\jakej\QTSW2\dashboard\backend
python -m uvicorn main:app --reload
```

## What to Watch For

✅ **Good Signs:**
- "Pipeline Orchestrator started"
- "Scheduler started"
- "Watchdog started"
- Server running on port 8000

❌ **Bad Signs:**
- Import errors
- "Failed to start orchestrator"
- Port already in use
- Module not found errors

## Next Steps After Starting

1. **Test Status**: `curl http://localhost:8000/api/pipeline/status`
2. **Start Pipeline**: `curl -X POST http://localhost:8000/api/pipeline/start`
3. **Check Logs**: Look at console output for events

---

**Just run `dashboard\START_ORCHESTRATOR.bat` to get started!**

