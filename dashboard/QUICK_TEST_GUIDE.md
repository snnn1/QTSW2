# Quick Test Guide - Orchestrator

## What We Just Fixed

✅ **Unicode encoding issues** - Removed emoji characters  
✅ **Import path issues** - Fixed relative imports  
✅ **Router access** - Fixed orchestrator_instance access (now dynamic)  
✅ **Batch file cleanup** - Removed 7 obsolete files  

## Next: Test the Orchestrator

### Step 1: Start the Backend (2 min)

**Option A: Use the batch file**
```bash
dashboard\START_ORCHESTRATOR.bat
```

**Option B: Manual start**
```bash
cd C:\Users\jakej\QTSW2
python -m uvicorn dashboard.backend.main:app --reload --host 0.0.0.0 --port 8000
```

**Look for:**
- ✅ "Pipeline Orchestrator started"
- ✅ "Scheduler started"  
- ✅ "Watchdog started"
- ❌ No errors

### Step 2: Test API (1 min)

**Open in browser:**
- http://localhost:8000/api/pipeline/status

**Should return:**
```json
{
  "status": null,
  "message": "No active pipeline run"
}
```

**If you see "Orchestrator not available":**
- The router fix didn't work - we need to debug further

### Step 3: Test Starting Pipeline (2 min)

**Option A: Use browser**
- Go to: http://localhost:8000/docs
- Find: `POST /api/pipeline/start`
- Click "Try it out" → "Execute"

**Option B: Use PowerShell**
```powershell
Invoke-WebRequest -Uri "http://localhost:8000/api/pipeline/start" -Method POST -UseBasicParsing
```

**Should return:**
```json
{
  "run_id": "...",
  "status": "starting",
  "message": "Pipeline started"
}
```

### Step 4: Watch It Run (5 min)

**Check status:**
```powershell
Invoke-WebRequest -Uri "http://localhost:8000/api/pipeline/status" -UseBasicParsing
```

**Watch state transitions:**
- `starting` → `running_translator` → `running_analyzer` → `running_merger` → `success`

**Check logs:**
- `logs\backend_debug.log` - Backend logs
- `automation\logs\events\pipeline_*.jsonl` - Event logs

## If Everything Works ✅

1. **Test frontend** - Start frontend and verify it connects
2. **Test scheduled runs** - Verify scheduler triggers at scheduled time
3. **Test error handling** - Try starting pipeline while one is running

## If Issues Appear ❌

**"Orchestrator not available":**
- Router still can't access orchestrator_instance
- Check if orchestrator actually started (look for "Pipeline Orchestrator started" in logs)

**Import errors:**
- Check `logs\backend_debug.log` for full traceback
- Verify automation package is accessible

**Pipeline doesn't start:**
- Check for lock file: `automation\logs\pipeline.lock`
- Remove if stale
- Check event log directory permissions

## Quick Status Check

```powershell
# Check if backend is running
Get-Process python -ErrorAction SilentlyContinue

# Test API
Invoke-WebRequest -Uri "http://localhost:8000/api/pipeline/status" -UseBasicParsing

# Check logs
Get-Content "logs\backend_debug.log" -Tail 20
```

---

**Ready to test? Start with Step 1!**

