# Run Policy - Automatic Health-Based Gate

## Overview

The run policy system introduces a system-driven decision layer that automatically allows or blocks pipeline execution based on recent run history — independent of UI toggles.

**This is a safety layer, not a feature.**

## Architecture

### Health Model (`run_health.py`)

Pure function that computes pipeline health from run history:
- **No I/O** - reads from RunHistory only
- **No logging** - pure computation
- **No side effects** - deterministic and explainable

### Policy Gate (`run_policy.py`)

Single gate function that determines if pipeline can run:
- **This is the only place** that decides run permission
- No other part of the system makes this decision
- Applied before any locks are acquired

### Integration (`service.py`)

Policy gate is enforced at the very start of `start_pipeline()`:
- Before lock acquisition
- Before state changes
- Before any work begins

## Health States

### `healthy`
- No health issues detected
- All runs completing normally

### `degraded`
- Same stage failed in 2 consecutive runs
- Indicates potential systemic issue with specific stage

### `unstable`
- ≥ 3 failures in last 5 runs
- Indicates general instability

### `blocked`
- Last run ended with force lock clear
- Or last run failed due to infrastructure error
- Indicates critical system problem

## Policy Rules

### Auto-Runs
- **Allow ONLY if health == healthy**
- All other health states block auto-runs
- No override possible

### Manual Runs
- **blocked**: Never allowed (even manual)
- **degraded/unstable**: Allowed with explicit override
- **healthy**: Always allowed

### Guarantees
- UI toggles cannot override `blocked` health
- Auto-runs are policy-gated
- Manual runs require override for degraded/unstable

## Health Computation Rules

### Rule 1: BLOCKED - Force Lock Clear
```python
if last_run.metadata.get("force_lock_clear"):
    return BLOCKED
```

### Rule 2: BLOCKED - Infrastructure Error
```python
if last_run.result == FAILED:
    if "lock" or "permission" or "disk" or "memory" in failure_reason:
        return BLOCKED
```

### Rule 3: UNSTABLE - Multiple Failures
```python
if failure_count >= 3 in last_5_runs:
    return UNSTABLE
```

### Rule 4: DEGRADED - Consecutive Stage Failure
```python
if same_stage_failed in last_2_runs:
    return DEGRADED
```

### Rule 5: HEALTHY - Default
```python
if none of above:
    return HEALTHY
```

## Policy Gate Logic

```python
def can_run_pipeline(auto_run, manual_override):
    health = compute_run_health(run_history)
    
    if auto_run:
        return health == HEALTHY
    else:  # manual
        if health == BLOCKED:
            return False  # Never allowed
        elif health in {DEGRADED, UNSTABLE}:
            return manual_override  # Requires override
        else:  # HEALTHY
            return True  # Always allowed
```

## Integration Points

### 1. Start Pipeline (`service.py`)
```python
async def start_pipeline(manual=False, manual_override=False):
    # POLICY GATE: Check before anything else
    allowed, reason, health, reasons = can_run_pipeline(
        run_history=self.run_history,
        auto_run=not manual,
        manual_override=manual_override
    )
    
    if not allowed:
        # Emit run_blocked event
        # Update state with health
        # Return cleanly (no exceptions)
        raise ValueError(f"Pipeline run blocked: {reason}")
    
    # Continue with normal start logic...
```

### 2. State Updates
Health is included in canonical state:
```python
{
    "run_health": "healthy",
    "run_health_reasons": ["No health issues detected"]
}
```

### 3. Health Updates
Health is recomputed and updated:
- After run completion (SUCCESS, FAILED, STOPPED)
- Before starting new run (policy check)
- Included in all state_change events

## Events

### `pipeline/run_blocked`
Emitted when a run is blocked by policy:
```json
{
    "run_id": "__system__",
    "stage": "pipeline",
    "event": "run_blocked",
    "data": {
        "run_health": "unstable",
        "health_reasons": ["3 failures in last 5 runs"],
        "auto_run": true,
        "manual_override": false,
        "block_reason": "Auto-run blocked: health is unstable (requires healthy)"
    }
}
```

**Note**: Blocked runs do NOT create RunSummary (no run occurred).

## API Contract

### No Breaking Changes
- Existing `/api/pipeline/start` endpoint unchanged
- New optional parameter: `manual_override` (for manual runs)
- Returns same format, but may raise `ValueError` if blocked

### State Includes Health
- `/api/pipeline/status` now includes `run_health` and `run_health_reasons`
- WebSocket `state_change` events include health in `canonical_state`
- Frontend receives health automatically (no changes needed)

## Examples

### Example 1: Auto-Run Blocked
```
Health: unstable (3 failures in last 5 runs)
Auto-run: ON
Result: Blocked
Event: pipeline/run_blocked
```

### Example 2: Manual Run with Override
```
Health: degraded (same stage failed in 2 consecutive runs)
Manual run: Yes
Override: Yes
Result: Allowed
```

### Example 3: Blocked Health
```
Health: blocked (last run had force lock clear)
Manual run: Yes
Override: Yes
Result: Still blocked (cannot override blocked)
```

## Testing

### Acceptance Criteria

✅ Auto-run ON + unstable health → pipeline does not start  
✅ Manual run + degraded → allowed with override  
✅ blocked health → nothing runs  
✅ Dashboard shows health state consistently  
✅ No change to existing APIs  
✅ No silent failures  

## Design Principles

1. **Deterministic**: Same history always produces same health
2. **Explainable**: Health reasons are always provided
3. **Non-negotiable**: Policy cannot be bypassed (except manual override for degraded/unstable)
4. **Transparent**: Health state visible in all state responses
5. **Safe**: Blocks runs when system is in bad state

## Future Enhancements (Not in Scope)

- ML-based health prediction
- Health persistence (currently computed on-demand)
- UI health indicators (separate work)
- Scheduler integration (separate work)
- Execution logic changes (separate work)

## Files

- `orchestrator/run_health.py` - Health computation
- `orchestrator/run_policy.py` - Policy gate
- `orchestrator/service.py` - Integration
- `orchestrator/state.py` - Health in canonical state
- `orchestrator/events.py` - run_blocked event type


