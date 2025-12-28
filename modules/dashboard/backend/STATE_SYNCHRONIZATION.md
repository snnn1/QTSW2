# State Synchronization - Single Source of Truth

## Problem Solved

Previously, polling and WebSocket paths could disagree about:
- Current pipeline state
- Active run ID
- Stage progress

This caused the dashboard to show inconsistent state under load or edge conditions.

## Solution: Single Authoritative State Source

### Architecture

1. **One Canonical In-Memory State Object**
   - Owned by `PipelineStateManager` (`_current_context: RunContext`)
   - All state reads go through `state_manager.get_state()`
   - State is persisted to disk but memory is authoritative

2. **WebSocket Publishes State Deltas**
   - `state_change` events now include complete canonical state
   - Frontend uses `canonical_state` from events for truthfulness
   - No state reconstruction needed

3. **Polling Endpoint Reads Same Object**
   - `/api/pipeline/status` calls `orchestrator.get_status()`
   - Which calls `state_manager.get_state()` (same source)
   - Never recomputes state - always reads from canonical source

### Implementation Details

#### Backend Changes

**1. Enhanced `state_change` Events** (`orchestrator/state.py`)
```python
# state_change events now include complete canonical state
await self.event_bus.publish({
    "run_id": self._current_context.run_id,
    "stage": "pipeline",
    "event": "state_change",
    "data": {
        "old_state": old_state.value,
        "new_state": new_state.value,
        "canonical_state": self._current_context.to_dict(),  # COMPLETE STATE
    }
})
```

**2. Polling Endpoint Uses Canonical Source** (`routers/pipeline.py`)
```python
# Reads from single source of truth
status = await orchestrator.get_status()  # Calls state_manager.get_state()
# Returns same format as canonical_state in events
```

**3. Added Helper Method** (`orchestrator/state.py`)
```python
async def get_canonical_state_dict(self) -> Optional[Dict[str, Any]]:
    """Get current canonical state as dictionary - single source of truth"""
    if self._current_context is None:
        return None
    return self._current_context.to_dict()
```

#### Frontend Changes

**1. Use Complete State from Events** (`hooks/usePipelineState.js`)
```javascript
// Prefer canonical_state if available (complete truth)
if (event.data.canonical_state) {
  const canonical = event.data.canonical_state
  // Map internal FSM state to canonical state (idle, running, stopped, error)
  updatePipelineStatus({
    state: canonicalState,
    run_id: canonical.run_id,
    current_stage: canonical.current_stage,
    started_at: canonical.started_at,
    // ... complete state
  })
}
```

### State Flow

```
┌─────────────────────────────────────────────────────────────┐
│         PipelineStateManager (Single Source of Truth)      │
│                    _current_context: RunContext             │
└─────────────────────────────────────────────────────────────┘
                            │
                ┌───────────┴───────────┐
                │                       │
                ▼                       ▼
    ┌──────────────────────┐  ┌──────────────────────┐
    │  Polling Endpoint    │  │  WebSocket Events    │
    │  /api/pipeline/status│  │  state_change        │
    │                       │  │                      │
    │  Reads:               │  │  Publishes:          │
    │  get_state()          │  │  canonical_state     │
    └──────────────────────┘  └──────────────────────┘
                │                       │
                └───────────┬───────────┘
                            ▼
                ┌──────────────────────┐
                │   Frontend State     │
                │   (Always Truthful)  │
                └──────────────────────┘
```

### Benefits

1. **Truthfulness**: Dashboard always reflects reality
2. **Consistency**: Polling and WebSocket always agree
3. **Simplicity**: Single source of truth, no state reconstruction
4. **Reliability**: No race conditions or stale state

### Canonical State Format

The canonical state includes:
- `run_id`: Unique run identifier
- `state`: Internal FSM state (idle, starting, running_translator, etc.)
- `current_stage`: Current pipeline stage (translator, analyzer, merger)
- `started_at`: Run start timestamp
- `updated_at`: Last state update timestamp
- `retry_count`: Number of retries
- `error`: Error message (if any)
- `metadata`: Additional run metadata

### Canonical State Mapping

Internal FSM states map to canonical states:
- `idle`, `success` → `"idle"`
- `stopped` → `"stopped"`
- `failed` → `"error"`
- `starting`, `running_*`, `scheduled`, `retrying` → `"running"`

### Testing

To verify state synchronization:
1. Start a pipeline run
2. Check `/api/pipeline/status` (polling)
3. Check WebSocket `state_change` events
4. Both should show identical state

### Future Enhancements

- Add state version numbers for conflict detection
- Add state checksums for integrity verification
- Add state history for debugging

