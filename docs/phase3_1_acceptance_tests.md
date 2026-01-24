# Phase 3.1: Acceptance Tests

**Date**: 2026-01-24  
**Purpose**: Acceptance test scenarios for single-executor guard and identity invariants monitoring

---

## Test 1: MES-Only Start During Market Closed

### Setup
- Market is closed
- No existing robot instances running
- Strategy attached to MES chart in NinjaTrader

### Steps
1. Start robot strategy on MES chart
2. Wait for engine startup to complete

### Expected Results

#### ✅ Lock Acquisition
- Event: `CANONICAL_MARKET_LOCK_ACQUIRED`
- Fields:
  - `canonical_instrument` = `"ES"`
  - `execution_instrument` = `"MES"` (from constructor)
  - `run_id` = GUID
  - `lock_file_path` = `{projectRoot}/runtime_locks/canonical_ES.lock`

#### ✅ Self-Test Diagnostic
- Event: `CANONICALIZATION_SELF_TEST`
- Fields:
  - `instrument_mappings` shows `{"execution_instrument": "MES", "canonical_instrument": "ES"}`
  - `canonical_stream_ids` = `["ES1", "ES2"]` (not MES1, MES2)

#### ✅ Identity Invariants Status
- Event: `IDENTITY_INVARIANTS_STATUS` (within 60 seconds)
- Fields:
  - `pass` = `true`
  - `violations` = `[]`
  - `canonical_instrument` = `"ES"`
  - `execution_instrument` = `"MES"`
  - `stream_ids` = `["ES1", "ES2"]`

#### ✅ Lock File Created
- File exists: `{projectRoot}/runtime_locks/canonical_ES.lock`
- Contains: `run_id`, `acquired_at_utc`, `canonical_instrument`

---

## Test 2: ES Strategy Start While MES Strategy Already Running

### Setup
- MES strategy already running (Test 1 completed)
- Lock file exists: `canonical_ES.lock`
- Lock file age < 10 minutes (fresh)

### Steps
1. Start robot strategy on ES chart in NinjaTrader (second instance)
2. Wait for engine startup attempt

### Expected Results

#### ✅ Lock Detection
- Event: `CANONICAL_MARKET_ALREADY_ACTIVE`
- Fields:
  - `canonical_instrument` = `"ES"`
  - `execution_instrument` = `"ES"` (from constructor)
  - `run_id` = New GUID (second instance)
  - `active_run_id` = GUID from first instance (from lock file)
  - `lock_file_path` = `{projectRoot}/runtime_locks/canonical_ES.lock`
  - `lock_age_minutes` < 10
  - `note` = "Another robot instance is already executing this canonical market..."

#### ✅ Engine Fails Closed
- Exception: `InvalidOperationException`
- Message contains: "Another robot instance is already executing canonical market 'ES'"
- Engine does not start
- No streams created
- No trading occurs

#### ✅ Notification Sent
- Notification: `CANONICAL_MARKET_ALREADY_ACTIVE`
- Priority: 2 (Emergency)
- Title: "CRITICAL: Duplicate Executor Blocked"

#### ✅ Lock File Unchanged
- Lock file still exists
- Lock file still contains first instance's `run_id`
- Lock file timestamp unchanged

---

## Test 3: Force One SIM Order on MES

### Setup
- MES strategy running (Test 1)
- Market is open
- Range locked, entry signal triggered

### Steps
1. Wait for entry order submission
2. Verify order placement

### Expected Results

#### ✅ Order Placement Event
- Event: `ORDER_SUBMIT_ATTEMPT`
- Fields:
  - `instrument` = `"MES"` (execution instrument)
  - `data.execution_instrument` = `"MES"` (if present)
  - `data.canonical_instrument` = `"ES"` (if present)

#### ✅ Execution Allowed Event
- Event: `EXECUTION_ALLOWED`
- Fields:
  - `stream` = `"ES1"` (canonical stream ID)
  - `instrument` = `"ES"` (canonical instrument, top-level)
  - `data.execution_instrument` = `"MES"`
  - `data.canonical_instrument` = `"ES"`

#### ✅ Identity Invariants Remain Pass
- Event: `IDENTITY_INVARIANTS_STATUS` (periodic)
- Fields:
  - `pass` = `true`
  - `violations` = `[]`
- Watchdog status shows: `last_identity_invariants_pass` = `true`

---

## Test 4: Stale Lock Recovery

### Setup
- Lock file exists: `canonical_ES.lock`
- Lock file age > 10 minutes (stale)
- Previous instance crashed/stopped without cleanup

### Steps
1. Start robot strategy on MES chart
2. Wait for engine startup

### Expected Results

#### ✅ Stale Lock Detection
- Event: `CANONICAL_MARKET_LOCK_STALE`
- Fields:
  - `canonical_instrument` = `"ES"`
  - `run_id` = New GUID
  - `lock_age_minutes` > 10
  - `note` = "Lock file is stale (older than 10 minutes) - reclaiming"

#### ✅ Lock Reclaimed
- Event: `CANONICAL_MARKET_LOCK_ACQUIRED`
- Lock file updated with new `run_id`
- Engine starts normally

---

## Test 5: Identity Invariants Violation Detection

### Setup
- MES strategy running
- Simulate identity violation (if possible) or wait for natural violation

### Steps
1. Monitor `IDENTITY_INVARIANTS_STATUS` events
2. Check watchdog status

### Expected Results

#### ✅ Violation Detected
- Event: `IDENTITY_INVARIANTS_STATUS`
- Fields:
  - `pass` = `false`
  - `violations` = `["Stream 'MES1': ..."]` (non-empty list)
  - `canonical_instrument` = `"ES"`
  - `execution_instrument` = `"MES"`

#### ✅ Watchdog Status Updated
- API: `/api/watchdog/status`
- Fields:
  - `last_identity_invariants_pass` = `false`
  - `last_identity_violations` = `["..."]` (non-empty)
  - `last_identity_invariants_event_chicago` = ISO timestamp

#### ✅ UI Badge Shows Violation
- WatchdogHeader shows: "IDENTITY VIOLATION" (red badge)
- Tooltip lists violations

---

## Test 6: Lock Release on Shutdown

### Setup
- MES strategy running
- Lock file exists

### Steps
1. Stop robot strategy (engine.Stop() called)
2. Wait for shutdown to complete

### Expected Results

#### ✅ Lock Released
- Event: `CANONICAL_MARKET_LOCK_RELEASED`
- Fields:
  - `canonical_instrument` = `"ES"`
  - `run_id` = Instance's run_id
  - `lock_file_path` = `{projectRoot}/runtime_locks/canonical_ES.lock`

#### ✅ Lock File Deleted
- Lock file no longer exists
- `{projectRoot}/runtime_locks/canonical_ES.lock` removed

---

## Test 7: Periodic Identity Invariants Emission

### Setup
- MES strategy running
- No violations detected

### Steps
1. Monitor events for 2 minutes
2. Check emission cadence

### Expected Results

#### ✅ Periodic Emission
- Event: `IDENTITY_INVARIANTS_STATUS`
- Cadence: At least once per 60 seconds
- Fields always include: `pass`, `violations`, `canonical_instrument`, `execution_instrument`, `stream_ids`, `checked_at_utc`

#### ✅ On-Change Emission
- If status changes (pass → fail or fail → pass), event emitted immediately
- Not rate-limited when status changes

---

## Test 8: Watchdog UI Badge

### Setup
- Dashboard running
- Watchdog page loaded

### Steps
1. Navigate to Watchdog page
2. Check WatchdogHeader component

### Expected Results

#### ✅ Identity OK Badge (Green)
- When `last_identity_invariants_pass` = `true`
- Badge text: "IDENTITY OK"
- Color: Green

#### ✅ Identity Violation Badge (Red)
- When `last_identity_invariants_pass` = `false`
- Badge text: "IDENTITY VIOLATION"
- Color: Red
- Tooltip shows violations list

---

## Success Criteria

✅ **Test 1**: MES strategy acquires ES lock, emits self-test, passes invariants  
✅ **Test 2**: ES strategy blocked when MES already running  
✅ **Test 3**: Orders use MES execution instrument, invariants remain pass  
✅ **Test 4**: Stale locks are reclaimed  
✅ **Test 5**: Violations detected and surfaced in watchdog  
✅ **Test 6**: Lock released on shutdown  
✅ **Test 7**: Periodic emission every 60s + on-change  
✅ **Test 8**: UI badge shows identity health  

---

## Notes

- **Lock Scope**: One canonical market per machine (simplest and safest)
- **Stale Threshold**: 10 minutes (configurable in `CanonicalMarketLock`)
- **Rate Limiting**: Identity invariants check every 60 seconds, or on-change
- **Fail-Closed**: Second instance throws exception and does not start
