# Next Steps - Pipeline Orchestrator

## Immediate Actions

### 1. Test the Orchestrator ✅ **DO THIS FIRST**

**Start the backend and verify orchestrator initializes:**

```bash
cd dashboard/backend
python -m uvicorn main:app --reload
```

**Check logs for:**
- ✅ "Pipeline Orchestrator started"
- ✅ "Scheduler started"
- ✅ "Watchdog started"
- ❌ Any import errors or exceptions

**Test endpoints:**
```bash
# Check status
curl http://localhost:8000/api/pipeline/status

# Get snapshot
curl http://localhost:8000/api/pipeline/snapshot

# Check schedule
curl http://localhost:8000/api/schedule
```

### 2. Test Pipeline Execution

**Start a pipeline run:**
```bash
curl -X POST http://localhost:8000/api/pipeline/start
```

**Monitor:**
- Check backend logs for events
- Check `automation/logs/events/pipeline_*.jsonl` files are created
- Verify state transitions in logs

**Check status:**
```bash
curl http://localhost:8000/api/pipeline/status
```

### 3. Test WebSocket Connection

**Connect frontend or use WebSocket client:**
- Frontend should automatically connect to `ws://localhost:8000/ws/events`
- Should receive snapshot on connect
- Should receive events in real-time

**Or test with Python:**
```python
import asyncio
import websockets
import json

async def test_websocket():
    async with websockets.connect("ws://localhost:8000/ws/events") as ws:
        # Receive snapshot
        snapshot = await ws.recv()
        print("Snapshot:", json.loads(snapshot))
        
        # Receive events
        for i in range(10):
            event = await ws.recv()
            print("Event:", json.loads(event))

asyncio.run(test_websocket())
```

## Potential Issues to Fix

### Issue 1: Import Errors
**If you see:** `ModuleNotFoundError: No module named 'automation'`

**Fix:** The runner.py imports from automation package. Make sure:
- QTSW2_ROOT is correct in config
- Python path includes QTSW2_ROOT
- automation package is accessible

### Issue 2: State Transition Errors
**If you see:** `ValueError: Invalid transition`

**Fix:** Check state.py - transitions might need adjustment based on actual flow

### Issue 3: EventBus Not Broadcasting
**If events don't appear in WebSocket:**

**Fix:** 
- Check EventBus.subscribe() is working
- Verify events are being published
- Check WebSocket connection is active

### Issue 4: Scheduler Not Running
**If scheduled runs don't trigger:**

**Fix:**
- Check scheduler loop is running
- Verify schedule config file exists
- Check scheduler logs

### Issue 5: Lock Issues
**If you see:** "Failed to acquire lock"

**Fix:**
- Check lock file exists: `automation/logs/pipeline.lock`
- Remove stale lock file if needed
- Verify lock directory permissions

## After Testing Works

### 4. Update Frontend (If Needed)

**Check if frontend needs updates:**
- API response format might have changed
- WebSocket message format should be compatible
- Status endpoint response might differ

**Test frontend:**
1. Start backend
2. Start frontend
3. Open dashboard
4. Try starting pipeline
5. Verify events appear in real-time

### 5. Remove Legacy Code

**Once orchestrator is stable, remove:**
- Legacy scheduler process management in main.py
- Old scheduler script references
- `pipeline_old.py` and `websocket_old.py` backups (after confirming new ones work)

### 6. Configuration Tuning

**Adjust if needed:**
- `orchestrator/config.py` - Timeouts, retry counts
- `orchestrator/scheduler.py` - Schedule calculation
- `orchestrator/watchdog.py` - Health check intervals

## Monitoring

### Check Logs
- Backend logs: `logs/backend_debug.log`
- Pipeline logs: `automation/logs/pipeline_*.log`
- Event logs: `automation/logs/events/pipeline_*.jsonl`

### Check Status
```bash
# Get current status
curl http://localhost:8000/api/pipeline/status

# Get snapshot with events
curl http://localhost:8000/api/pipeline/snapshot

# Check scheduler status
curl http://localhost:8000/api/scheduler/status
```

## Success Criteria

✅ Backend starts without errors  
✅ Orchestrator initializes successfully  
✅ Can start pipeline via API  
✅ Events appear in WebSocket  
✅ Status endpoint returns correct state  
✅ Scheduler triggers runs at scheduled times  
✅ Watchdog detects and handles hung runs  
✅ Frontend displays events correctly  

## If Something Breaks

### Quick Rollback
1. Rename `pipeline_old.py` → `pipeline.py`
2. Rename `websocket_old.py` → `websocket.py`
3. Restart backend

### Debug Mode
Add more logging:
```python
# In orchestrator/service.py
self.logger.setLevel(logging.DEBUG)
```

### Check State
```python
# In Python console
from dashboard.backend.main import orchestrator_instance
status = await orchestrator_instance.get_status()
print(status)
```

## Documentation

- Full implementation: `ORCHESTRATOR_IMPLEMENTATION.md`
- Activation status: `ORCHESTRATOR_ACTIVATED.md`
- This guide: `NEXT_STEPS.md`

## Priority Order

1. **Test backend startup** (5 min)
2. **Test pipeline execution** (10 min)
3. **Test WebSocket** (5 min)
4. **Test frontend integration** (10 min)
5. **Fix any issues** (varies)
6. **Remove legacy code** (5 min)
7. **Tune configuration** (as needed)

---

**Start with step 1 - test the backend startup!**

