# Quick Test Guide - After Starting Backend

## Step 1: Check if It Started ✅

Look at the console output. You should see:

**✅ Good Signs:**
```
Pipeline Dashboard API started
Pipeline Orchestrator started
Scheduler started
Watchdog started
INFO:     Uvicorn running on http://0.0.0.0:8000
```

**❌ Bad Signs:**
```
ERROR: Failed to start orchestrator
ModuleNotFoundError
ImportError
```

## Step 2: Test the API

### Option A: Open in Browser
1. Open: **http://localhost:8000**
2. You should see: `{"message": "Pipeline Dashboard API", "status": "running"}`

### Option B: Test Status Endpoint
Open: **http://localhost:8000/api/pipeline/status**

You should see either:
- `{"status": null, "message": "No active pipeline run"}` (if no pipeline running)
- Or status info if a pipeline is running

### Option C: Check API Docs
Open: **http://localhost:8000/docs**
- Should show Swagger UI with all endpoints
- Try clicking "GET /api/pipeline/status" → "Try it out" → "Execute"

## Step 3: Test Starting a Pipeline

### In Browser:
1. Go to: **http://localhost:8000/docs**
2. Find: **POST /api/pipeline/start**
3. Click "Try it out" → "Execute"
4. Should return: `{"run_id": "...", "status": "starting", "message": "Pipeline started"}`

### Or with curl (if you have it):
```bash
curl -X POST http://localhost:8000/api/pipeline/start
```

### Watch the Console:
You should see events like:
```
State transition: idle -> starting
State transition: starting -> running_translator
```

## Step 4: Check WebSocket (If Frontend Running)

If you have the frontend open:
- It should automatically connect to WebSocket
- You should see events appearing in real-time
- Check browser console for WebSocket connection status

## Step 5: Monitor Status

Keep checking status:
```
http://localhost:8000/api/pipeline/status
```

Watch it change:
- `starting` → `running_translator` → `running_analyzer` → `running_merger` → `success`

## What to Look For

### ✅ Everything Working:
- Backend starts without errors
- Status endpoint returns data
- Can start pipeline
- Events appear in console/logs
- State transitions happen

### ❌ Problems:
- **Import errors** → Check Python path, install dependencies
- **"Orchestrator not available"** → Check startup logs for errors
- **No events** → Check EventBus is working
- **State stuck** → Check watchdog is running

## Quick Commands

**Check status:**
```
http://localhost:8000/api/pipeline/status
```

**Get snapshot (status + events):**
```
http://localhost:8000/api/pipeline/snapshot
```

**Check schedule:**
```
http://localhost:8000/api/schedule
```

## Next Steps

1. ✅ Verify backend started
2. ✅ Test status endpoint
3. ✅ Try starting a pipeline
4. ✅ Watch events in console
5. ✅ Check frontend (if running)

---

**Start with Step 1 - check the console output!**

