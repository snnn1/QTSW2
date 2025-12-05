# How to Check Orchestrator Status

## Quick Check

**Test the API:**
```bash
curl http://localhost:8000/api/pipeline/status
```

**Expected if working:**
```json
{"run_id": "...", "state": "idle", ...}
```

**Expected if not working:**
```json
{"status": null, "message": "Orchestrator not available"}
```

## Check Console Output

When you start the backend, look for:

**✅ Success:**
```
INITIALIZING ORCHESTRATOR
Step 1: Importing orchestrator...
   ✅ Imported
Step 2: Creating config...
   ✅ Config created
Step 3: Creating orchestrator instance...
   ✅ Instance created
Step 4: Starting orchestrator...
   ✅ Orchestrator started successfully!
```

**❌ Failure:**
```
INITIALIZING ORCHESTRATOR
Step 1: Importing orchestrator...
   ✅ Imported
Step 2: Creating config...
   ✅ Config created
Step 3: Creating orchestrator instance...
   ✅ Instance created
Step 4: Starting orchestrator...
❌ ERROR: Orchestrator failed to start!
[Error details here]
```

## What to Do

1. **Start backend**: `dashboard\START_ORCHESTRATOR.bat`
2. **Watch console** for the initialization messages
3. **Check which step fails** (if any)
4. **Copy the error message** and share it

The improved logging will now show exactly where it fails!

