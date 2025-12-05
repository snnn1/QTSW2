# Fixed! How to Start

## The Issue

The backend uses relative imports (`from .routers import ...`), so it must be run from the `backend` directory, not the `dashboard` directory.

## ✅ Fixed Startup Scripts

Both startup scripts have been fixed:
- `START_ORCHESTRATOR.bat` - Now changes to backend directory first
- `START_ORCHESTRATOR.ps1` - Now changes to backend directory first

## How to Start Now

### Option 1: Use the Fixed Batch File
```
Double-click: dashboard\START_ORCHESTRATOR.bat
```

### Option 2: Manual Start
```bash
cd dashboard\backend
python -m uvicorn main:app --reload
```

## What You Should See

When it starts successfully:
```
INFO:     Started server process
INFO:     Waiting for application startup.
Pipeline Dashboard API started
Pipeline Orchestrator started
Scheduler started
Watchdog started
INFO:     Application startup complete.
INFO:     Uvicorn running on http://0.0.0.0:8000
```

## Test It

1. **Start the backend** (use the fixed batch file)
2. **Open browser**: http://localhost:8000
3. **Check status**: http://localhost:8000/api/pipeline/status
4. **API docs**: http://localhost:8000/docs

## If It Still Doesn't Work

Run the diagnostic:
```
dashboard\RUN_DIAGNOSTIC.bat
```

And share the output!

---

**Try the fixed batch file now: `dashboard\START_ORCHESTRATOR.bat`**

