# Test Suite for Run Health and Policy Gate

This document describes the test suite for the automatic run health and policy gate system.

## Test Files

### 1. `test_run_health.py`
**Purpose**: Unit tests for the `compute_run_health()` function

**Coverage**:
- ✅ No history returns healthy
- ✅ Healthy system with successful runs
- ✅ Blocked health (force lock clear)
- ✅ Blocked health (infrastructure errors: lock, permission, disk, connection, timeout)
- ✅ Unstable health (3+ failures in last 5 runs)
- ✅ Degraded health (same stage failed in 2 consecutive runs)
- ✅ Priority rules (blocked > unstable > degraded)
- ✅ Application errors don't block
- ✅ Only last 5 runs checked for unstable
- ✅ Stopped runs not counted as failures

**Run**:
```bash
cd modules/dashboard/backend
pytest test_run_health.py -v
```

### 2. `test_run_policy.py`
**Purpose**: Unit tests for the `can_run_pipeline()` function

**Coverage**:
- ✅ Auto-run with healthy system → allowed
- ✅ Auto-run with unstable/degraded/blocked → blocked
- ✅ Auto-run override ignored
- ✅ Manual run with healthy → allowed
- ✅ Manual run with blocked → never allowed (even with override)
- ✅ Manual run with degraded/unstable → requires override
- ✅ No history → allowed (healthy by default)
- ✅ Reason includes health reasons
- ✅ Health reasons always present

**Run**:
```bash
cd modules/dashboard/backend
pytest test_run_policy.py -v
```

### 3. `test_policy_integration.py`
**Purpose**: Integration tests for policy gate in service layer

**Coverage**:
- ✅ Auto-run blocked emits `run_blocked` event
- ✅ Auto-run blocked updates state with health
- ✅ Auto-run healthy starts pipeline
- ✅ Manual run blocked never allowed
- ✅ Manual run degraded requires override
- ✅ Manual run unstable requires override
- ✅ Policy gate checked before locks
- ✅ Blocked runs don't create RunSummary
- ✅ Health reasons in event

**Run**:
```bash
cd modules/dashboard/backend
pytest test_policy_integration.py -v
```

## Running All Tests

```bash
cd modules/dashboard/backend
pytest test_run_health.py test_run_policy.py test_policy_integration.py -v
```

## Running with Coverage

```bash
cd modules/dashboard/backend
pytest test_run_health.py test_run_policy.py test_policy_integration.py --cov=orchestrator.run_health --cov=orchestrator.run_policy --cov-report=html
```

## Test Structure

All tests use:
- **pytest** as the testing framework
- **tempfile** for isolated test directories
- **unittest.mock** for mocking (integration tests)
- **asyncio** for async test support

## Key Test Scenarios

### Health Model Tests
1. **Healthy**: All successful runs or no history
2. **Blocked**: Force lock clear or infrastructure error
3. **Unstable**: 3+ failures in last 5 runs
4. **Degraded**: Same stage failed in 2 consecutive runs

### Policy Gate Tests
1. **Auto-run**: Only allowed if healthy
2. **Manual run**: 
   - Healthy → always allowed
   - Degraded/Unstable → requires override
   - Blocked → never allowed

### Integration Tests
1. **Event emission**: Blocked runs emit `run_blocked` events
2. **State updates**: Health information in canonical state
3. **Gate timing**: Policy checked before locks
4. **No persistence**: Blocked runs don't create summaries

## Notes

- Tests use temporary directories that are cleaned up automatically
- Integration tests mock the pipeline runner to avoid actual execution
- All tests are deterministic and isolated
- Tests verify both positive and negative cases


