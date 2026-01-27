# WebSocket Connection Debugging Guide

## Symptom: Error 1006 (Abnormal Closure)

If you're seeing `Firefox can't establish a connection` with error code 1006, follow this systematic debugging approach.

## Debugging Sequence

### Step 1: Confirm Route Handler is Invoked

**Check**: Look for `WS_CONNECT_ATTEMPT` in backend logs when frontend connects.

**Expected**: You should see:
```
WS_CONNECT_ATTEMPT client=127.0.0.1:XXXXX path=/ws/events run_id=None
```

**If missing**:
- Route isn't matching (check path, prefix, port)
- Wrong backend process running
- WebSocket upgrade blocked before handler

**If present**: Route handler is being invoked → proceed to Step 2

### Step 2: Disable GZip Middleware

GZipMiddleware can interfere with WebSocket upgrades. Temporarily disable it:

**File**: `modules/watchdog/backend/main.py`

**Change**:
```python
# Gzip compression middleware
# app.add_middleware(GZipMiddleware, minimum_size=1000)  # DISABLED FOR DEBUGGING
```

**Test**: Restart backend and try connecting again.

**If this fixes it**: GZipMiddleware is the culprit. Consider excluding WebSocket paths or using a different compression strategy.

**If still failing**: Proceed to Step 3

### Step 3: Use Minimal Handler

Switch to the minimal handler to isolate snapshot/stream logic:

**File**: `modules/watchdog/backend/main.py`

**Change**:
```python
# Import and include watchdog router
try:
    from .routers import watchdog, websocket_minimal as websocket  # Use minimal handler
except ImportError:
    # ... fallback imports ...
    from modules.watchdog.backend.routers import watchdog, websocket_minimal as websocket

app.include_router(watchdog.router)
app.include_router(websocket.router)  # Now using minimal handler
```

**What minimal handler does**:
- Logs `WS_CONNECT_ATTEMPT` (proves route match)
- Accepts connection
- Sends heartbeat every 30 seconds
- Logs `WS_CLOSED` on disconnect
- **No snapshot, no file I/O, no event streaming**

**Test**: Restart backend and try connecting.

**Expected logs**:
```
WS_CONNECT_ATTEMPT client=127.0.0.1:XXXXX path=/ws/events run_id=None [MINIMAL HANDLER]
WS_ACCEPTED client=127.0.0.1:XXXXX path=/ws/events [MINIMAL HANDLER]
Heartbeat sent to 127.0.0.1:XXXXX [MINIMAL HANDLER]
WS_CLOSED client=127.0.0.1:XXXXX path=/ws/events duration_seconds=XX.X close_code=1000 close_reason=none [MINIMAL HANDLER]
```

**If minimal handler works**:
- Issue is in snapshot/stream logic
- Proceed to Step 4 (incremental re-add)

**If minimal handler still fails**:
- Issue is in accept phase or middleware
- Check for exceptions in `WS_ERROR phase=accept` logs
- Verify CORS configuration
- Check if aggregator initialization is blocking

### Step 4: Incremental Re-add (if minimal works)

If minimal handler works, re-add features one at a time:

#### 4a. Add Snapshot (without file I/O)
Modify minimal handler to send a hardcoded snapshot:
```python
# After accept, before heartbeat loop:
await websocket.send_json({
    "type": "snapshot_chunk",
    "server_time_utc": datetime.now(timezone.utc).isoformat(),
    "snapshot_size": 0,
    "events": []  # Empty for testing
})
```

**Test**: If this works, snapshot send logic is fine → proceed to 4b
**If fails**: Issue is in `send_json` or message serialization

#### 4b. Add File Reading
Add file reading logic (but don't send snapshot yet):
```python
# Test file reading
if FRONTEND_FEED_FILE.exists():
    with open(FRONTEND_FEED_FILE, 'rb') as f:
        # Just read, don't send
        pass
```

**Test**: If this works, file I/O is fine → proceed to 4c
**If fails**: Issue is in file access or permissions

#### 4c. Add Full Snapshot
Add full snapshot send logic from `_send_snapshot()`.

**Test**: If this works, snapshot is fine → proceed to 4d
**If fails**: Issue is in snapshot send logic

#### 4d. Add Event Streaming
Add event streaming loop.

**Test**: If this works, full handler should work
**If fails**: Issue is in streaming loop logic

## Quick Test Checklist

1. ✅ **WS_CONNECT_ATTEMPT appears** → Route handler invoked
2. ✅ **GZip disabled** → Middleware not interfering  
3. ✅ **Minimal handler works** → Accept phase OK
4. ✅ **Snapshot added** → Snapshot logic OK
5. ✅ **Streaming added** → Full handler works

## Common Issues and Fixes

### Issue: WS_CONNECT_ATTEMPT never appears

**Causes**:
- Wrong port (frontend connecting to 8001 instead of 8002)
- Route path mismatch (`/ws/events` vs `/api/ws/events`)
- Wrong backend process running

**Fix**: 
- Verify frontend connects to `ws://localhost:8002/ws/events`
- Check backend logs for "Registered WebSocket routes"
- Verify correct backend process is running

### Issue: WS_CONNECT_ATTEMPT appears but WS_ACCEPTED doesn't

**Causes**:
- Exception during `websocket.accept()`
- CORS blocking upgrade
- Middleware interfering

**Fix**:
- Check logs for `WS_ERROR phase=accept`
- Verify CORS allows WebSocket upgrades
- Disable GZip middleware (Step 2)

### Issue: Minimal handler works but full handler doesn't

**Causes**:
- Snapshot file I/O failing
- JSON serialization error
- Send timeout too aggressive
- Exception in streaming loop

**Fix**:
- Follow Step 4 (incremental re-add)
- Check logs for `WS_ERROR phase=snapshot` or `phase=stream`
- Verify file permissions for `frontend_feed.jsonl`
- Check send timeout configuration

## Restoring Full Handler

Once debugging is complete, restore full handler:

**File**: `modules/watchdog/backend/main.py`

```python
from .routers import watchdog, websocket  # Back to full handler
```

And re-enable GZip if it wasn't the issue:
```python
app.add_middleware(GZipMiddleware, minimum_size=1000)
```

## Health Endpoint Check

Always check `/api/watchdog/ws-health` during debugging:

```bash
curl http://localhost:8002/api/watchdog/ws-health
```

**Look for**:
- `ws_enabled: true`
- `active_connections_total` changes on connect/disconnect
- `accept_fail_count` increases if accept fails
- `last_accept_error` shows error message if accept fails
