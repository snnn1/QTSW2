# Watchdog ↔ Robot Observability Audit (Pre-Snapshot Implementation)

**Date**: 2026-03-18  
**Scope**: System-level observability audit of Robot + Watchdog architecture for read-only operator snapshot layer  
**Constraint**: Audit only — no implementation, refactoring, or UI design

---

## 1. DEFINE CURRENT DATA FLOW (SOURCE OF TRUTH MAPPING)

### 1.1 Diagram

```
┌─────────────────────────────────────────────────────────────────────────────────────────┐
│ ROBOT OUTPUTS                                                                             │
├─────────────────────────────────────────────────────────────────────────────────────────┤
│                                                                                          │
│  RobotLoggingService ──► logs/robot/robot_ENGINE.jsonl                                   │
│       │                   logs/robot/robot_<instrument>.jsonl                            │
│       │                   (EngineLogDedupe: EXECUTION_BLOCKED, RECONCILIATION_QTY_       │
│       │                    MISMATCH, etc. 10s/30s windows)                              │
│       │                                                                                  │
│       ├──► logs/health/<date>_<instrument>_<stream>_<slot>.jsonl  [NOT INGESTED]         │
│       │                                                                                  │
│  ExecutionEventWriter ──► automation/logs/execution_events/<date>/<instrument>.jsonl      │
│                          [NOT INGESTED BY WATCHDOG]                                       │
│                                                                                          │
│  ExecutionJournal ──────► data/execution_journals/<date>_<stream>_<intent>.json           │
│                          [HYDRATION ONLY: hydrate_intent_exposures_from_journals]         │
│                                                                                          │
│  StreamStateMachine ────► logs/robot/journal/<date>_<stream>.json                        │
│                          [HYDRATION ONLY: hydrate_stream_states_from_slot_journals]       │
│                                                                                          │
└─────────────────────────────────────────────────────────────────────────────────────────┘
                                        │
                                        ▼
┌─────────────────────────────────────────────────────────────────────────────────────────┐
│ EVENTFEEDGENERATOR (modules/watchdog/event_feed.py)                                       │
├─────────────────────────────────────────────────────────────────────────────────────────┤
│  Input:  robot_*.jsonl (via _read_log_file_incremental, byte positions in                 │
│          data/robot_log_read_positions.json)                                             │
│  Filter: LIVE_CRITICAL_EVENT_TYPES only (config.py)                                      │
│  Filter: STREAM_STATE_TRANSITION UNKNOWN→* skipped                                      │
│  Rate limit: ENGINE_TICK_CALLSITE 5s, BAR_RECEIVED_NO_STREAMS 60s, TIMETABLE_* 30min     │
│  Assign: event_seq per run_id (in-memory; resets on EventFeedGenerator restart)           │
│  Output: logs/robot/frontend_feed.jsonl                                                 │
│  Order:  Merge by (timestamp_utc, file_path, index) before processing                     │
└─────────────────────────────────────────────────────────────────────────────────────────┘
                                        │
                                        ▼
┌─────────────────────────────────────────────────────────────────────────────────────────┐
│ WATCHDOG AGGREGATOR (modules/watchdog/aggregator.py)                                     │
├─────────────────────────────────────────────────────────────────────────────────────────┤
│  _process_feed_events_sync:                                                               │
│    - CursorManager (data/frontend_cursor.json): run_id → event_seq for incremental      │
│    - EventProcessor.process_event() → WatchdogStateManager                               │
│    - _add_to_ring_buffer_if_important() → _important_events_buffer (max 1000)            │
│                                                                                          │
│  REST fetchWatchdogEvents (get_events_since):                                            │
│    - _read_events_tail(1000) from frontend_feed.jsonl                                    │
│    - _filter_events_for_live_feed() excludes: tick/bar heartbeats, RANGE_LOCK_SNAPSHOT   │
│    - Filter by run_id (current_run_id from feed tail)                                    │
│    - Merge ring buffer (derived: ORDER_STUCK_DETECTED, etc.)                             │
│    - Return last 300 by timestamp                                                        │
│    - Cache: EVENTS_CACHE_TTL_SECONDS                                                      │
│                                                                                          │
│  WebSocket /ws/events (get_important_events_since):                                      │
│    - Returns ONLY _important_events_buffer (seq > last_sent_seq)                          │
│    - seq is watchdog-local counter, NOT feed event_seq                                    │
└─────────────────────────────────────────────────────────────────────────────────────────┘
```

### 1.2 Data Flow Summary

| Stage | Real-time vs Batch | Filtered vs Preserved | Information Lost/Transformed |
|-------|--------------------|------------------------|------------------------------|
| Robot → robot_*.jsonl | Real-time (append) | EngineLogDedupe drops identical events within 10s (30s for RECONCILIATION_ORDER_SOURCE_BREAKDOWN) | Duplicate EXECUTION_BLOCKED, RECONCILIATION_QTY_MISMATCH, etc. within window |
| robot_*.jsonl → EventFeedGenerator | Polling (~1s) | LIVE_CRITICAL_EVENT_TYPES only; STREAM_STATE_TRANSITION UNKNOWN→* skipped; rate limits | All non-LIVE_CRITICAL events; rate-limited ENGINE_TICK_CALLSITE, BAR_RECEIVED_NO_STREAMS, TIMETABLE_* |
| EventFeedGenerator → frontend_feed.jsonl | Append | event_seq assigned (per run_id, in-memory) | event_seq resets on EventFeedGenerator restart |
| frontend_feed → Aggregator | CursorManager incremental | Cursor advances; events processed once | Cursor is server-side; REST clients use since_seq (client-managed) |
| REST get_events_since | Tail read (cached) | _filter_events_for_live_feed excludes noisy types | Last 300 only; run_id filter can hide multi-run history |
| WebSocket get_important_events_since | Ring buffer | important_types subset only | BOOTSTRAP_*, RECOVERY_DECISION_*, DISCONNECT_FAIL_CLOSED_*, FORCED_FLATTEN_*, ADOPTION_* NOT in ring buffer |

### 1.3 Pipelines NOT Ingested by Watchdog

| Pipeline | Path | Purpose | Gap |
|----------|------|---------|-----|
| Execution events | automation/logs/execution_events/*.jsonl | Canonical execution events (event_family, event_sequence) | Not read by Watchdog; replay/diagnostics only |
| Health logs | logs/health/*.jsonl | WARN+ and selected INFO | Not read by Watchdog |
| Hydration logs | logs/robot/hydration_*.jsonl | Hydration diagnostics | Not in EventFeedGenerator |

---

## 2. EVENT COVERAGE AUDIT

### 2.1 Critical Event Groups

| Event Group | Emitted by Robot? | In LIVE_CRITICAL_EVENT_TYPES? | Watchdog Receives? | Rate-limited/Deduped? | Transitions Reconstructible? |
|-------------|-------------------|-------------------------------|--------------------|----------------------|-----------------------------|
| **BOOTSTRAP_*** | Yes (BootstrapPhase4Types.cs, NinjaTraderSimAdapter) | Yes: BOOTSTRAP_DECISION_RESUME, ADOPT, FLATTEN, HALT, BOOTSTRAP_ADOPTION_COMPLETED | Yes (feed) | No | Partial: decisions yes; BOOTSTRAP_READY_TO_RESUME, BOOTSTRAP_HALTED, BOOTSTRAP_OPERATOR_ACTION_REQUIRED, BOOTSTRAP_ADOPTION_ATTEMPT NOT in LIVE_CRITICAL |
| **RECOVERY_*** | Yes (InstrumentExecutionAuthority.RecoveryPhase3.cs) | Yes: RECOVERY_DECISION_RESUME, ADOPT, FLATTEN, HALT | Yes (feed) | No | Partial: decisions yes; RECOVERY_POSITION_UNMATCHED, RECOVERY_PROTECTIVE_ORDERS_PLACED NOT in LIVE_CRITICAL |
| **RECONCILIATION_*** | Yes (ReconciliationRunner, RobotEngine) | Yes: RECONCILIATION_RECOVERY_ADOPTION_SUCCESS, RECONCILIATION_PASS_SUMMARY, RECONCILIATION_QTY_MISMATCH | Yes (feed) | Robot: EngineLogDedupe 10s for RECONCILIATION_QTY_MISMATCH | Yes |
| **EXECUTION_*** | Yes | Yes (EXECUTION_BLOCKED, ALLOWED, FILLED, etc.) | Yes (feed) | Robot: EngineLogDedupe 10s for EXECUTION_BLOCKED | Yes |
| **CONNECTION_*** | Yes (HealthMonitor) | Yes: CONNECTION_LOST, LOST_SUSTAINED, RECOVERED, RECOVERED_NOTIFICATION, CONFIRMED | Yes (feed) | Watchdog: DISCONNECT_DEDUPE_WINDOW_SECONDS 30s for ConnectionLost | Yes |
| **DISCONNECT_FAIL_CLOSED_*** | Yes (RobotEngine) | Yes: DISCONNECT_FAIL_CLOSED_ENTERED | Yes (feed) | No | Yes |
| **DISCONNECT_RECOVERY_*** | Yes | Yes: DISCONNECT_RECOVERY_STARTED, COMPLETE, ABORTED | Yes (feed) | No | Yes |
| **FLATTEN_*** | Yes | Yes: FORCED_FLATTEN_TRIGGERED, FORCED_FLATTEN_POSITION_CLOSED, SESSION_FORCED_FLATTENED, FORCED_FLATTEN_FAILED, etc. | Yes (feed) | No | Yes |
| **ADOPTION_*** | Yes | Yes: ADOPTION_SUCCESS; RECONCILIATION_RECOVERY_ADOPTION_SUCCESS | Yes (feed) | No | Yes |

### 2.2 Gaps: Events Emitted but NOT in LIVE_CRITICAL_EVENT_TYPES

| Event | Emitted By | Consequence |
|-------|------------|-------------|
| **RECOVERY_POSITION_UNMATCHED** | RobotEngine.cs (recovery path) | Watchdog never receives; cannot reconstruct "position unmatched, operator action required" |
| **RECOVERY_PROTECTIVE_ORDERS_PLACED** | IEA | Protective placement confirmation not visible |
| **BOOTSTRAP_READY_TO_RESUME** | Bootstrap flow | Bootstrap readiness not visible |
| **BOOTSTRAP_HALTED** | Bootstrap flow | Halted state not visible |
| **BOOTSTRAP_OPERATOR_ACTION_REQUIRED** | Bootstrap flow | Operator action signal not visible |
| **BOOTSTRAP_ADOPTION_ATTEMPT** | RunBootstrapAdoption | Adoption attempt not visible (only BOOTSTRAP_ADOPTION_COMPLETED is) |
| **RECONCILIATION_ORDER_SOURCE_BREAKDOWN** | RobotEngine.AssembleMismatchObservations | Diagnostic for reconciliation; not in LIVE_CRITICAL; also EngineLogDedupe 30s |
| **RECONCILIATION_CONTEXT** | ReconciliationRunner | Context before RECONCILIATION_QTY_MISMATCH; not in LIVE_CRITICAL |

### 2.3 WebSocket Ring Buffer Gap

`_add_to_ring_buffer_if_important` (aggregator.py:2539) uses a **subset** of event types. The following critical types are **NOT** in `important_types` and thus **never appear on WebSocket**:

- DISCONNECT_FAIL_CLOSED_ENTERED
- DISCONNECT_RECOVERY_STARTED, DISCONNECT_RECOVERY_COMPLETE, DISCONNECT_RECOVERY_ABORTED
- BOOTSTRAP_DECISION_*, BOOTSTRAP_ADOPTION_COMPLETED
- RECOVERY_DECISION_*, ADOPTION_SUCCESS, RECONCILIATION_RECOVERY_ADOPTION_SUCCESS
- FORCED_FLATTEN_TRIGGERED, FORCED_FLATTEN_POSITION_CLOSED, SESSION_FORCED_FLATTENED, FORCED_FLATTEN_FAILED
- PROTECTIVE_ORDERS_SUBMITTED, PROTECTIVE_ORDERS_FAILED_FLATTENED
- RECONCILIATION_PASS_SUMMARY

**Impact**: REST clients (fetchWatchdogEvents) receive these from feed tail. WebSocket-only clients **miss** them. Real-time snapshot over WebSocket would be incomplete.

---

## 3. STATE RECONSTRUCTION CAPABILITY

### 3.1 Required State Dimensions (Per Instrument / Stream)

| Dimension | Can it be derived? | From what signals? | Deterministic? | Gaps |
|-----------|-------------------|------------------|---------------|------|
| **Current system state** (ACTIVE / RECOVERY / FAIL_CLOSED / DISCONNECTED) | Partially | DISCONNECT_FAIL_CLOSED_ENTERED, DISCONNECT_RECOVERY_*, CONNECTION_*, update_recovery_state, update_connection_status | **Not fully**: Watchdog has CONNECTED_OK, DISCONNECT_FAIL_CLOSED, RECOVERY_RUNNING, RECOVERY_COMPLETE. Per-instrument ACTIVE/RECOVERY (IEA-level) not explicitly emitted. Instrument freeze (RECONCILIATION_QTY_MISMATCH) is recorded but not as a first-class state dimension. | No per-instrument recovery/freeze state; infer from RECONCILIATION_QTY_MISMATCH events only |
| **Position (broker-side)** | No | Robot has broker snapshot; Watchdog has no direct position feed | **No**: Watchdog infers from INTENT_EXPOSURE (journal-derived) + EXECUTION_FILLED. No broker position API. | Broker position is robot-only; snapshot would need robot cooperation or separate broker API |
| **Ownership status** (owned / adopted / unknown) | Partially | ADOPTION_SUCCESS, RECONCILIATION_RECOVERY_ADOPTION_SUCCESS, INTENT_EXPOSURE_REGISTERED | **Ambiguous**: Adoption success implies ownership; no explicit "ownership proven" or "ownership unknown" signal. RECOVERY_POSITION_UNMATCHED (not in feed) would indicate "unknown". | RECOVERY_POSITION_UNMATCHED missing; no explicit ownership_proven signal |
| **Protective status** (valid / missing / unknown) | Partially | PROTECTIVE_ORDERS_SUBMITTED, PROTECTIVE_ORDERS_FAILED_FLATTENED, PROTECTIVE_DRIFT_DETECTED | **Not deterministic**: PROTECTIVE_ORDERS_SUBMITTED → valid until failure. No explicit "protective confirmed" or "protective missing" per intent. PROTECTIVE_DRIFT_DETECTED indicates problem. | No protective confirmation signal; infer from absence of failure |
| **Recovery status** (in progress / complete / blocked) | Partially | DISCONNECT_RECOVERY_STARTED, DISCONNECT_RECOVERY_COMPLETE, DISCONNECT_RECOVERY_ABORTED | **Yes** for disconnect recovery. **No** for per-instrument recovery (RECOVERY_DECISION_* → adoption/flatten). No "recovery blocked" signal. | Per-instrument recovery completion inferred from RECOVERY_DECISION_* + RECONCILIATION_PASS_SUMMARY |
| **Action required** (none / flatten / reconcile / restart) | Partially | RECOVERY_POSITION_UNMATCHED (missing), RECONCILIATION_QTY_MISMATCH, FORCED_FLATTEN_FAILED, BOOTSTRAP_OPERATOR_ACTION_REQUIRED (missing) | **Not deterministic**: RECONCILIATION_QTY_MISMATCH → reconcile or flatten. RECOVERY_POSITION_UNMATCHED → operator action (not in feed). No explicit "action required" enum. | RECOVERY_POSITION_UNMATCHED and BOOTSTRAP_OPERATOR_ACTION_REQUIRED not in feed |

### 3.2 Strict Assessment

- **Deterministic**: Only when a single interpretation exists and all required signals are present.
- **Ambiguous**: Multiple interpretations (e.g., "reconcile" vs "flatten" after RECONCILIATION_QTY_MISMATCH — robot decides, Watchdog observes outcome).
- **Unsafe for operator decision**: When critical signals are missing (RECOVERY_POSITION_UNMATCHED) or inference is heuristic.

---

## 4. TEMPORAL CONSISTENCY / ORDERING

### 4.1 event_seq Correctness

| Aspect | Behavior | Issue |
|--------|----------|-------|
| Assignment | EventFeedGenerator assigns event_seq per run_id; increments for each event written to feed | In-memory; resets when EventFeedGenerator restarts |
| Scope | Per run_id | Correct for single-run ordering |
| Gap | event_seq resets on restart | After Watchdog/EventFeedGenerator restart, event_seq can repeat across runs; frontend sorts by timestamp for display |
| CursorManager | data/frontend_cursor.json: run_id → event_seq | Used by aggregator for incremental processing; NOT used by REST API (clients pass since_seq) |

### 4.2 REST + WebSocket Merge Consistency

| Aspect | Behavior | Issue |
|--------|----------|-------|
| REST | Reads feed tail (last 1000 lines), filters, merges ring buffer (derived events), returns last 300 by timestamp | Derived events (ORDER_STUCK_DETECTED, etc.) have run_id=None or event_id="watchdog:..."; merged by timestamp |
| WebSocket | Returns ring buffer events only; seq is watchdog-local counter | seq ≠ event_seq; different ordering domain |
| Deduplication | event_id = run_id:event_seq for feed events; dedupe by event_id on frontend | REST and WebSocket can deliver same event via different paths; event_id enables dedupe |
| Ordering | Feed: append order. Merge: sort by timestamp_utc | Timestamp ordering can be ambiguous when events have same or very close timestamps across files |

### 4.3 Deduplication Effects on Ordering

| Mechanism | Effect |
|-----------|--------|
| EngineLogDedupe (Robot) | Drops duplicate EXECUTION_BLOCKED, RECONCILIATION_QTY_MISMATCH, etc. within 10s. **Ordering**: First occurrence in window is kept; subsequent duplicates lost. Transition sequence can be incomplete. |
| EventFeedGenerator rate limits | Drops ENGINE_TICK_CALLSITE, BAR_RECEIVED_NO_STREAMS, TIMETABLE_* within windows. **Ordering**: Sampled events only; no ordering corruption. |
| Connection dedupe (Watchdog) | DISCONNECT_DEDUPE_WINDOW_SECONDS 30s; rapid ConnectionLost flaps not counted. **Ordering**: First LOST in window drives state; ordering preserved. |

### 4.4 Missing Intermediate Transitions

- **EngineLogDedupe**: Identical RECONCILIATION_QTY_MISMATCH within 10s → only first logged. If state changes (e.g., broker_qty changes), second event may be dropped. Transition from "mismatch A" to "mismatch B" can be lost.
- **STREAM_STATE_TRANSITION UNKNOWN→***: Skipped. Initialization transitions not in feed. If Watchdog starts mid-session, stream state comes from hydrate_stream_states_from_slot_journals, not from events.

### 4.5 Can Watchdog Reconstruct Exact Sequences?

| Question | Answer |
|----------|--------|
| Can Watchdog reconstruct exact sequences of state transitions? | **Partially**. Within a single run, feed order is preserved. Across EventFeedGenerator restarts, event_seq resets. EngineLogDedupe can drop transitions. |
| Are there race conditions where ordering is ambiguous? | **Yes**. Multi-file merge uses (timestamp, file_path, index). Same timestamp across files → order depends on file sort. REST merges feed + ring buffer by timestamp; derived events can interleave. |

---

## 5. INCIDENT RECONSTRUCTION TEST

### Scenario A: RECOVERY_POSITION_UNMATCHED

| Question | Answer |
|----------|--------|
| Can Watchdog reconstruct what happened? | **No**. RECOVERY_POSITION_UNMATCHED is NOT in LIVE_CRITICAL_EVENT_TYPES. Event never reaches frontend_feed.jsonl. |
| Can Watchdog reconstruct current state? | **No**. "Position unmatched, operator action required" is unknown. |
| Can Watchdog determine required operator action? | **No**. |
| **Exact missing signal** | RECOVERY_POSITION_UNMATCHED |

### Scenario B: RECONCILIATION_QTY_MISMATCH → Adoption Success

| Question | Answer |
|----------|--------|
| Can Watchdog reconstruct what happened? | **Yes**. RECONCILIATION_QTY_MISMATCH, RECONCILIATION_RECOVERY_ADOPTION_SUCCESS (or RECONCILIATION_PASS_SUMMARY) in feed. |
| Can Watchdog reconstruct current state? | **Yes**. Incident recorder closes on RECONCILIATION_PASS_SUMMARY. State: reconciled. |
| Can Watchdog determine required operator action? | **Yes**. None if adoption succeeded. |
| **Gap** | EngineLogDedupe can drop repeated RECONCILIATION_QTY_MISMATCH within 10s. If adoption fails and retry happens quickly, sequence may be incomplete. |

### Scenario C: Disconnect → Reconnect → Bootstrap → Resume/Adopt

| Question | Answer |
|----------|--------|
| Can Watchdog reconstruct what happened? | **Partially**. CONNECTION_LOST, DISCONNECT_FAIL_CLOSED_ENTERED, CONNECTION_RECOVERED, DISCONNECT_RECOVERY_STARTED, DISCONNECT_RECOVERY_COMPLETE, ENGINE_START (new run_id), BOOTSTRAP_DECISION_RESUME or BOOTSTRAP_DECISION_ADOPT, BOOTSTRAP_ADOPTION_COMPLETED (if adopt). |
| Can Watchdog reconstruct current state? | **Partially**. Connection and recovery state yes. Bootstrap decision and adoption outcome yes. **Missing**: BOOTSTRAP_READY_TO_RESUME, BOOTSTRAP_HALTED, BOOTSTRAP_OPERATOR_ACTION_REQUIRED, BOOTSTRAP_ADOPTION_ATTEMPT. |
| Can Watchdog determine required operator action? | **Partially**. If BOOTSTRAP_DECISION_HALT or BOOTSTRAP_OPERATOR_ACTION_REQUIRED (not in feed), operator action would be unclear. |
| **Exact missing signals** | BOOTSTRAP_READY_TO_RESUME, BOOTSTRAP_HALTED, BOOTSTRAP_OPERATOR_ACTION_REQUIRED, BOOTSTRAP_ADOPTION_ATTEMPT (lower priority than RECOVERY_POSITION_UNMATCHED) |

### Scenario D: Unknown Order → Fail-Closed → Flatten

| Question | Answer |
|----------|--------|
| Can Watchdog reconstruct what happened? | **Yes**. ORDER_REGISTRY_MISSING / REGISTRY_BROKER_DIVERGENCE → RequestRecovery → RECOVERY_DECISION_FLATTEN. DISCONNECT_FAIL_CLOSED_ENTERED, RECOVERY_DECISION_*, FORCED_FLATTEN_* in feed. |
| Can Watchdog reconstruct current state? | **Yes**. Recovery state, flatten outcome. |
| Can Watchdog determine required operator action? | **Yes**. If FORCED_FLATTEN_FAILED, action = retry or manual flatten. |
| **Gap** | ORDER_REGISTRY_MISSING, REGISTRY_BROKER_DIVERGENCE may not be explicit event types; reconstruction from RECOVERY_DECISION_FLATTEN + context. |

---

## 6. OPERATOR DECISION GAP ANALYSIS

| Question | Can Watchdog Answer? | How? | Gap |
|----------|----------------------|------|-----|
| Is system safe? | Partially | Connection status, recovery state, kill switch, ENGINE_TICK. No per-instrument safety. | Per-instrument freeze (RECONCILIATION_QTY_MISMATCH) recorded but not as "system safe" dimension |
| Is there exposure? | Yes | Intent exposures from INTENT_EXPOSURE_REGISTERED + journal hydration. | Broker position not verified; exposure is journal-derived |
| Is ownership proven? | No | No explicit signal. Adoption success implies ownership; RECOVERY_POSITION_UNMATCHED (missing) would indicate unproven | RECOVERY_POSITION_UNMATCHED not in feed |
| Are protectives valid? | Partially | PROTECTIVE_ORDERS_SUBMITTED → assume valid until PROTECTIVE_ORDERS_FAILED_FLATTENED or PROTECTIVE_DRIFT_DETECTED | No explicit "protective confirmed" |
| Is intervention required? | Partially | FORCED_FLATTEN_FAILED, RECONCILIATION_QTY_MISMATCH (incident), DUPLICATE_INSTANCE_DETECTED. RECOVERY_POSITION_UNMATCHED (missing) would be key | RECOVERY_POSITION_UNMATCHED not in feed |
| What exact action is required? | Partially | Infer from incident type. RECONCILIATION_QTY_MISMATCH → reconcile or flatten (robot decides). RECOVERY_POSITION_UNMATCHED would indicate "operator must decide" | No explicit action enum; RECOVERY_POSITION_UNMATCHED missing |

---

## 7. SNAPSHOT READINESS ASSESSMENT

### 7.1 Can Snapshot Be Built Without Modifying Robot?

**Partially.** A snapshot can be built from:

- frontend_feed.jsonl (LIVE_CRITICAL events)
- Execution journals (hydrate_intent_exposures_from_journals)
- Slot journals (hydrate_stream_states_from_slot_journals)
- WatchdogStateManager derived state (recovery, connection, stream states, intent exposures)

**But** critical gaps prevent a complete, operator-actionable snapshot:

1. **RECOVERY_POSITION_UNMATCHED** not in feed → cannot represent "position unmatched, operator action required"
2. **Broker position** not available → snapshot cannot verify position vs journal
3. **WebSocket** ring buffer excludes BOOTSTRAP_*, RECOVERY_DECISION_*, DISCONNECT_FAIL_CLOSED_*, FORCED_FLATTEN_*, ADOPTION_* → real-time snapshot over WS incomplete
4. **Ownership status** has no explicit signal → infer only from adoption success/failure

### 7.2 What Data Is Missing?

| Data | Source | Status |
|------|--------|--------|
| RECOVERY_POSITION_UNMATCHED | Robot | Not in LIVE_CRITICAL; add to config |
| Broker position | Robot or broker API | Not available to Watchdog |
| BOOTSTRAP_OPERATOR_ACTION_REQUIRED | Robot | Not in LIVE_CRITICAL |
| Protective confirmation | Robot | No explicit event |
| WebSocket important_types | Watchdog config | Extend ring buffer to include BOOTSTRAP_*, RECOVERY_*, DISCONNECT_*, FORCED_FLATTEN_*, ADOPTION_* |

### 7.3 What Signals Need to Be Added (If Any)?

**Minimum additions (observability only, no robot decision changes):**

1. **RECOVERY_POSITION_UNMATCHED** → add to LIVE_CRITICAL_EVENT_TYPES
2. **BOOTSTRAP_OPERATOR_ACTION_REQUIRED** → add to LIVE_CRITICAL_EVENT_TYPES (if robot emits it)
3. **Extend WebSocket important_types** → add BOOTSTRAP_*, RECOVERY_DECISION_*, DISCONNECT_FAIL_CLOSED_ENTERED, DISCONNECT_RECOVERY_*, FORCED_FLATTEN_*, ADOPTION_SUCCESS, RECONCILIATION_RECOVERY_ADOPTION_SUCCESS, RECONCILIATION_PASS_SUMMARY to ring buffer

**Optional (lower priority):**

- RECOVERY_PROTECTIVE_ORDERS_PLACED
- BOOTSTRAP_READY_TO_RESUME, BOOTSTRAP_HALTED, BOOTSTRAP_ADOPTION_ATTEMPT

---

## 8. MINIMAL SIGNAL ADDITIONS (IF REQUIRED)

### 8.1 Strict Rules Applied

- No duplication of state
- No new "truth" sources (Robot remains authoritative)
- No robot decision changes
- Only additional observability signals

### 8.2 Proposed Additions

| Addition | Type | Rationale |
|----------|------|-----------|
| RECOVERY_POSITION_UNMATCHED in LIVE_CRITICAL_EVENT_TYPES | Config | Critical for operator: "position unmatched, operator must decide". Robot already emits. |
| BOOTSTRAP_OPERATOR_ACTION_REQUIRED in LIVE_CRITICAL_EVENT_TYPES | Config | Operator action required at bootstrap. Robot must emit (verify). |
| Extend _add_to_ring_buffer_if_important | Code | WebSocket clients need BOOTSTRAP_*, RECOVERY_*, DISCONNECT_*, FORCED_FLATTEN_*, ADOPTION_* for real-time snapshot |

### 8.3 NOT Recommended

- **OPERATOR_STATUS_SNAPSHOT**: Would duplicate state; snapshot should be derived from events.
- **New robot decision logic**: Audit is observability-only.

---

## 9. ARCHITECTURE VALIDATION

### 9.1 Principle

> Watchdog is a read-only interpretation layer. Robot remains the sole decision engine.

### 9.2 Does Current System Respect This?

**Yes.** Watchdog:

- Reads robot logs and journals
- Does not send commands to Robot
- Does not gate execution
- Derives state from events (EventProcessor → StateManager)
- Incident recorder is observational
- hydrate_intent_exposures_from_journals and hydrate_stream_states_from_slot_journals are read-only

### 9.3 Would Snapshot Violate It?

**No.** A snapshot layer that:

- Consumes frontend_feed.jsonl + journals
- Computes derived state (current system state, ownership inference, action required)
- Exposes read-only API to operator UI

would remain interpretation-only. Robot would not be modified.

### 9.4 Constraints to Enforce

| Constraint | Enforcement |
|------------|-------------|
| Snapshot is derived, not authoritative | Snapshot computed from events + journals; no new state source |
| Robot is sole decision engine | No Watchdog→Robot command path |
| Observability signals only | Additions are config (LIVE_CRITICAL, important_types), not robot logic |

---

## 10. FINAL OUTPUT

### A. System Readiness Score

**Needs minor additions**

- Core pipeline (Robot → EventFeedGenerator → frontend_feed → Aggregator) is sound
- Most critical events (BOOTSTRAP_DECISION_*, RECOVERY_DECISION_*, CONNECTION_*, DISCONNECT_*, FORCED_FLATTEN_*, RECONCILIATION_*) reach the feed
- State reconstruction works for many scenarios
- **Blocker**: RECOVERY_POSITION_UNMATCHED not in feed
- **Blocker for WebSocket snapshot**: Ring buffer excludes key event types

### B. Exact Gaps

| Gap | Severity | Fix |
|-----|----------|-----|
| RECOVERY_POSITION_UNMATCHED not in LIVE_CRITICAL_EVENT_TYPES | Critical | Add to config.py |
| WebSocket ring buffer excludes BOOTSTRAP_*, RECOVERY_*, DISCONNECT_*, FORCED_FLATTEN_*, ADOPTION_* | High | Extend important_types in _add_to_ring_buffer_if_important |
| Broker position not available | Medium | Out of scope for snapshot v1; document as limitation |
| BOOTSTRAP_OPERATOR_ACTION_REQUIRED not in LIVE_CRITICAL | Medium | Add if robot emits |
| event_seq resets on EventFeedGenerator restart | Low | Document; use timestamp for cross-restart ordering |
| EngineLogDedupe can drop transition events | Low | Document; accept for snapshot v1 |

### C. Recommended Next Step

**Add signals first**, then implement snapshot:

1. Add RECOVERY_POSITION_UNMATCHED to LIVE_CRITICAL_EVENT_TYPES
2. Extend WebSocket important_types to include BOOTSTRAP_*, RECOVERY_DECISION_*, DISCONNECT_FAIL_CLOSED_ENTERED, DISCONNECT_RECOVERY_*, FORCED_FLATTEN_*, ADOPTION_SUCCESS, RECONCILIATION_RECOVERY_ADOPTION_SUCCESS, RECONCILIATION_PASS_SUMMARY
3. Verify BOOTSTRAP_OPERATOR_ACTION_REQUIRED is emitted by robot; if yes, add to LIVE_CRITICAL_EVENT_TYPES
4. Implement snapshot layer (read-only, event-derived)
5. Document limitations: broker position not verified, ownership inferred, action required heuristic

---

## APPENDIX: Key File References

| Component | File |
|-----------|------|
| LIVE_CRITICAL_EVENT_TYPES | modules/watchdog/config.py:143-264 |
| EventFeedGenerator | modules/watchdog/event_feed.py |
| Aggregator, get_events_since, _filter_events_for_live_feed | modules/watchdog/aggregator.py |
| _add_to_ring_buffer_if_important | modules/watchdog/aggregator.py:2539 |
| CursorManager | modules/watchdog/state_manager.py:2083 |
| hydrate_intent_exposures_from_journals | modules/watchdog/state_manager.py:579 |
| EventProcessor | modules/watchdog/event_processor.py |
| EngineLogDedupe | RobotCore_For_NinjaTrader/EngineLogDedupe.cs |
| REST events API | modules/watchdog/backend/routers/watchdog.py:102 |
| WebSocket events | modules/watchdog/backend/routers/websocket.py |
