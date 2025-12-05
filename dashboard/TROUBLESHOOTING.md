# Troubleshooting - Backend Not Working

## Quick Diagnosis

### 1. What Error Do You See?

**Common Errors:**

#### A. Import Errors
```
ModuleNotFoundError: No module named 'automation'
ImportError: cannot import name 'PipelineOrchestrator'
```
**Fix:** Make sure you're running from the right directory and Python can find the automation package.

#### B. Port Already in Use
```
ERROR:    [Errno 10048] Only one usage of each socket address
Address already in use
```
**Fix:** Stop other instances or change port.

#### C. Orchestrator Failed to Start
```
ERROR: Failed to start orchestrator
```
**Fix:** Check the full error message - usually a config or import issue.

#### D. Python Not Found
```
'python' is not recognized as an internal or external command
```
**Fix:** Use full path to Python or add to PATH.

## Step-by-Step Fix

### Step 1: Check Console Output
**What does the console show?**
- Copy the full error message
- Look for lines starting with "ERROR" or "Exception"

### Step 2: Check Python Path
```bash
# Make sure you're in the right directory
cd C:\Users\jakej\QTSW2\dashboard\backend

# Check Python works
python --version

# Try starting manually
python -m uvicorn main:app --reload
```

### Step 3: Check Dependencies
```bash
pip install fastapi uvicorn
```

### Step 4: Check for Import Issues
The orchestrator imports from `automation` package. Make sure:
- You're running from `QTSW2` directory structure
- `automation` folder exists at `C:\Users\jakej\QTSW2\automation`

### Step 5: Try Starting Without Orchestrator (Test)
Temporarily comment out orchestrator initialization in `main.py` to see if basic FastAPI works.

## Common Fixes

### Fix 1: Port Already in Use
```bash
# Find what's using port 8000
netstat -ano | findstr :8000

# Kill it (replace PID)
taskkill /PID <PID> /F
```

### Fix 2: Import Path Issues
Edit `dashboard/backend/orchestrator/runner.py` - the path might be wrong.

### Fix 3: Missing Dependencies
```bash
pip install fastapi uvicorn pydantic pytz
```

### Fix 4: Execution Policy (PowerShell)
```powershell
Set-ExecutionPolicy -ExecutionPolicy RemoteSigned -Scope CurrentUser
```

## Get More Info

**Run with more verbose output:**
```bash
cd dashboard\backend
python -m uvicorn main:app --reload --log-level debug
```

**Check logs:**
- `logs/backend_debug.log` - Backend logs
- Console output - Real-time errors

## What I Need From You

1. **Full error message** - Copy/paste the exact error
2. **Console output** - What do you see when you start it?
3. **What happens** - Does it start then crash? Or fail immediately?

---

**Please share the exact error message you're seeing!**

