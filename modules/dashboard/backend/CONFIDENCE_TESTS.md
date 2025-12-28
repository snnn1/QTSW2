# Confidence Tests - Invariant Verification

These tests verify that the critical fixes for memory leaks, lock races, and resource leaks are working correctly. These are **NOT feature tests** - they test system invariants.

## Tests

### 1. Idle Soak Test
**Purpose**: Verify event count plateaus and memory stabilizes during idle periods.

**What it tests**:
- Event count plateaus (no unbounded growth)
- Memory stabilizes (no memory leak)
- Ring buffer size stays within bounds
- TTL enforcement works correctly

**How to run**:
```bash
# Quick test (5 minutes - for CI/CD)
pytest modules/dashboard/backend/test_confidence_invariants.py::TestIdleSoak -v -s

# Full validation (2-4 hours - for manual testing)
SOAK_TEST_DURATION=7200 pytest modules/dashboard/backend/test_confidence_invariants.py::TestIdleSoak -v -s
```

**Expected results**:
- Ring buffer never exceeds `buffer_size` (1000)
- Ring buffer size plateaus (variance < 10% of buffer size)
- Memory growth < 50MB over test duration
- Memory stabilizes (variance < 20MB in second half)

### 2. Rapid Restart Test
**Purpose**: Verify no stale locks and no false "already running" errors.

**What it tests**:
- No stale locks remain after rapid start/stop cycles
- No false "already running" errors
- Lock is always released (even on exceptions)
- Lock acquisition/release race conditions are handled

**How to run**:
```bash
# Default (25 cycles)
pytest modules/dashboard/backend/test_confidence_invariants.py::TestRapidRestart -v -s

# Extended (50 cycles)
RAPID_RESTART_CYCLES=50 pytest modules/dashboard/backend/test_confidence_invariants.py::TestRapidRestart -v -s
```

**Expected results**:
- All locks acquired successfully
- All locks released successfully
- No stale locks after all cycles
- No false "already running" errors
- Final state is idle/terminal

### 3. WebSocket Churn Test
**Purpose**: Verify subscriber count never exceeds cap and drops to zero.

**What it tests**:
- Subscriber count never exceeds hard cap (100)
- Subscriber count drops to zero when all disconnected
- No zombie subscribers remain after disconnects
- Guaranteed cleanup works even on abrupt disconnects

**How to run**:
```bash
# Default (50 cycles)
pytest modules/dashboard/backend/test_confidence_invariants.py::TestWebSocketChurn -v -s

# Extended (100 cycles)
WEBSOCKET_CHURN_CYCLES=100 pytest modules/dashboard/backend/test_confidence_invariants.py::TestWebSocketChurn -v -s
```

**Expected results**:
- Max subscriber count ≤ 100 (hard cap)
- Final subscriber count = 0 (all cleaned up)
- No zombie subscribers after abrupt disconnects
- Subscriber cleanup happens even on exceptions

## Running All Tests

```bash
# Run all confidence tests
pytest modules/dashboard/backend/test_confidence_invariants.py -v -s

# Run with extended parameters
SOAK_TEST_DURATION=7200 RAPID_RESTART_CYCLES=30 WEBSOCKET_CHURN_CYCLES=50 \
  pytest modules/dashboard/backend/test_confidence_invariants.py -v -s
```

## Dependencies

These tests require:
- `pytest` - Testing framework
- `pytest-asyncio` - Async test support
- `psutil` - Memory monitoring (for idle soak test)

Install dependencies:
```bash
pip install pytest pytest-asyncio psutil
```

## Test Markers

Tests are marked with:
- `@pytest.mark.asyncio` - Async test support
- `@pytest.mark.slow` - Long-running tests (can be skipped with `-m "not slow"`)

Skip slow tests:
```bash
pytest modules/dashboard/backend/test_confidence_invariants.py -v -s -m "not slow"
```

## Environment Variables

- `SOAK_TEST_DURATION` - Duration in seconds for idle soak test (default: 300 = 5 minutes)
- `RAPID_RESTART_CYCLES` - Number of start/stop cycles (default: 25)
- `WEBSOCKET_CHURN_CYCLES` - Number of connect/disconnect cycles (default: 50)

## Success Criteria

All three tests must pass to confirm the critical fixes are working:

✅ **Idle Soak Test**: Event count plateaus, memory stabilizes  
✅ **Rapid Restart Test**: No stale locks, no false positives  
✅ **WebSocket Churn Test**: Subscriber cap enforced, no zombies  

If all tests pass, the critical audit items (memory leaks, lock races, resource leaks) are fully closed.

## Manual Validation

For production validation, run extended tests:

```bash
# 2-hour idle soak
SOAK_TEST_DURATION=7200 pytest modules/dashboard/backend/test_confidence_invariants.py::TestIdleSoak -v -s

# 30 rapid restart cycles
RAPID_RESTART_CYCLES=30 pytest modules/dashboard/backend/test_confidence_invariants.py::TestRapidRestart -v -s

# 100 WebSocket churn cycles
WEBSOCKET_CHURN_CYCLES=100 pytest modules/dashboard/backend/test_confidence_invariants.py::TestWebSocketChurn -v -s
```

