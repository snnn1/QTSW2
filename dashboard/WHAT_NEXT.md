# What's Next - Testing & Verification

## Current Status

✅ **Orchestrator implemented** - All components created  
✅ **Routers replaced** - New orchestrator-based routers active  
✅ **Startup scripts fixed** - Ready to run  
✅ **Cleanup done** - Unneeded files removed  

## Next Steps (In Order)

### 1. Test Backend Startup ⚠️ **DO THIS FIRST**

**Start the backend:**
```
dashboard\START_ORCHESTRATOR.bat
```

**Check for:**
- ✅ "Pipeline Orchestrator started"
- ✅ "Scheduler started"
- ✅ "Watchdog started"
- ✅ No errors

**If errors appear:**
- Copy the error message
- Check `logs/backend_debug.log`
- Run diagnostic: `dashboard\RUN_DIAGNOSTIC.bat`

### 2. Test API Endpoints

**Open in browser:**
- http://localhost:8000 - Should show API message
- http://localhost:8000/api/pipeline/status - Should return status
- http://localhost:8000/docs - Should show Swagger UI

**Test starting pipeline:**
- Go to http://localhost:8000/docs
- Find `POST /api/pipeline/start`
- Click "Try it out" → "Execute"
- Should return run_id and status

### 3. Test Pipeline Execution

**Start a pipeline run:**
```bash
curl -X POST http://localhost:8000/api/pipeline/start
```

**Watch the console:**
- Should see state transitions
- Should see events being emitted
- Should see stages running

**Check status:**
```bash
curl http://localhost:8000/api/pipeline/status
```

**Watch it progress:**
- `starting` → `running_translator` → `running_analyzer` → `running_merger` → `success`

### 4. Test WebSocket Connection

**If frontend is running:**
- Frontend should auto-connect
- Events should appear in real-time
- Check browser console for WebSocket status

**Or test manually:**
- Use WebSocket client to connect to `ws://localhost:8000/ws/events`
- Should receive snapshot on connect
- Should receive events as pipeline runs

### 5. Test Frontend Integration

**Start frontend:**
```bash
cd dashboard\frontend
npm run dev
```

**Open:** http://localhost:5173

**Check:**
- ✅ Dashboard loads
- ✅ Can see pipeline status
- ✅ Can start pipeline
- ✅ Events appear in real-time
- ✅ WebSocket connects

### 6. Test Scheduled Runs

**Check schedule:**
```bash
curl http://localhost:8000/api/schedule
```

**Update schedule:**
- Go to dashboard or use API
- Change schedule time
- Verify scheduler reloads

**Wait for scheduled time:**
- Pipeline should start automatically
- Check logs to confirm

### 7. Test Error Handling

**Test lock prevention:**
- Start a pipeline
- Try to start another while first is running
- Should get "pipeline already running" error

**Test retry logic:**
- If a stage fails, should retry
- Check logs for retry attempts

**Test watchdog:**
- If pipeline hangs, watchdog should detect it
- Should transition to FAILED state

## If Everything Works ✅

### 8. Remove Legacy Code

Once orchestrator is proven stable:
- Remove old scheduler process management from `main.py`
- Remove legacy scheduler script references
- Clean up any remaining old code

### 9. Final Documentation

- Update main README with orchestrator info
- Document any configuration changes
- Note any breaking changes for future reference

## If Issues Appear ❌

### Common Issues & Fixes

**Orchestrator fails to start:**
- Check import errors
- Verify automation package is accessible
- Check config paths

**Pipeline doesn't run:**
- Check lock file isn't stuck
- Verify services can be imported
- Check event log directory permissions

**WebSocket doesn't connect:**
- Check EventBus is working
- Verify WebSocket endpoint is registered
- Check CORS settings

**Frontend doesn't show events:**
- Check WebSocket connection
- Verify event format matches frontend expectations
- Check browser console for errors

## Priority Order

1. **Test backend startup** (5 min) - Most important!
2. **Test API endpoints** (5 min)
3. **Test pipeline execution** (10 min)
4. **Test WebSocket** (5 min)
5. **Test frontend** (10 min)
6. **Fix any issues** (varies)
7. **Remove legacy code** (5 min)

## Success Criteria

✅ Backend starts without errors  
✅ Can start pipeline via API  
✅ Pipeline runs through all stages  
✅ Events appear in WebSocket  
✅ Frontend displays events correctly  
✅ Scheduler triggers runs automatically  
✅ Watchdog detects issues  

---

**Start with Step 1 - Test the backend startup!**

Run: `dashboard\START_ORCHESTRATOR.bat`

