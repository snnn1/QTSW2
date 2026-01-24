# Phase 3.1 Assessment: Single-Executor Guard & Identity Invariants

**Date**: 2026-01-24  
**Purpose**: Assess existing guards and identity invariant monitoring

---

## Part A: Assessment Findings

### Single-Executor Guard Search

**Keywords searched**: "already active", "singleton", "mutex", "lock file", "canonical market active", "CANONICAL_MARKET_ALREADY_ACTIVE", "ExecutionContext registry", "global guard"

**Findings**:

1. **Orchestrator LockManager** (`modules/orchestrator/locks.py`)
   - **Purpose**: Prevents overlapping pipeline runs
   - **Key**: Pipeline run ID (not canonical market)
   - **Scope**: Pipeline orchestrator only, not robot instances
   - **Not applicable**: This is for pipeline runs, not robot strategy instances

2. **Robot Logging Service Singleton** (`modules/robot/core/RobotLoggingService.cs`)
   - **Purpose**: Prevents file lock contention for log files
   - **Key**: Project root (not canonical market)
   - **Scope**: Logging only, not execution guard

3. **KillSwitch** (`modules/robot/core/Execution/KillSwitch.cs`)
   - **Purpose**: Global execution kill switch (blocks all orders)
   - **Key**: Global (not per-market)
   - **Scope**: Execution blocking, not duplicate instance prevention

**Conclusion**: ❌ **NO single-executor guard exists for canonical markets**

- No mechanism prevents two robot instances (ES + MES) from running simultaneously
- No canonical market-level locking
- Risk: Both instances would execute same ES logic, causing double-execution

---

### Identity Invariants Monitoring Search

**Keywords searched**: "invariants", "identity", "self test", "diagnostic", "health", "leakage"

**Findings**:

1. **CANONICALIZATION_SELF_TEST** (`modules/robot/core/RobotEngine.cs:1779-1814`)
   - **Purpose**: Startup-only verification of canonicalization mapping
   - **Cadence**: Once per engine start
   - **Fields**: `instrument_mappings`, `canonical_stream_ids`
   - **Status**: ✅ Exists, but startup-only

2. **HealthMonitor** (`modules/robot/core/HealthMonitor.cs`)
   - **Purpose**: General health monitoring (data loss, stalls, etc.)
   - **Scope**: General health, not identity-specific invariants
   - **Status**: Exists, but no identity checks

3. **Orchestrator Heartbeat** (`modules/orchestrator/service.py:201-237`)
   - **Purpose**: System heartbeat for WebSocket/EventBus liveness
   - **Cadence**: Every 45 seconds
   - **Scope**: System liveness, not identity invariants
   - **Status**: Exists, but not identity-specific

**Conclusion**: ❌ **NO periodic identity invariants status event exists**

- `CANONICALIZATION_SELF_TEST` is startup-only
- No ongoing monitoring of identity consistency
- No periodic verification that:
  - Stream IDs remain canonical
  - Instrument properties match CanonicalInstrument
  - ExecutionInstrument is present
  - Events include both identities

---

## Summary

| Component | Status | Notes |
|-----------|--------|-------|
| Single-executor guard (canonical market) | ❌ **MISSING** | No guard prevents ES + MES instances from running simultaneously |
| Identity invariants periodic event | ❌ **MISSING** | Only startup self-test exists, no ongoing monitoring |
| Lock file mechanism | ✅ Exists (orchestrator) | But scoped to pipeline runs, not robot instances |
| Startup self-test | ✅ Exists | `CANONICALIZATION_SELF_TEST` at engine start |

---

## Required Implementation

### Part B: Single-Executor Guard
- **Required**: Lock file mechanism keyed by canonical instrument
- **Key**: `{projectRoot}/runtime_locks/canonical_{instrument}.lock`
- **Behavior**: Second instance fails closed with `CANONICAL_MARKET_ALREADY_ACTIVE` event

### Part C: Identity Invariants Status Event
- **Required**: Periodic `IDENTITY_INVARIANTS_STATUS` event
- **Cadence**: Every 60 seconds + on-change
- **Fields**: `pass`, `violations`, `canonical_instrument`, `execution_instrument`, `stream_ids`, `checked_at_utc`

### Part D: Watchdog Ingestion + UI
- **Required**: Process `IDENTITY_INVARIANTS_STATUS` events
- **Store**: `last_identity_invariants_pass`, `last_identity_invariants_event_chicago`, `last_identity_violations`
- **Expose**: `/api/watchdog/status` endpoint
- **UI**: Badge in WatchdogHeader showing identity health

### Part E: Acceptance Tests
- **Required**: Document test scenarios in `docs/phase3_1_acceptance_tests.md`

---

## Next Steps

Proceed to Part B: Implement single-executor guard using lock file approach (Option 2 - portable, deterministic).
