# Full Observability & Logging System Audit (Quant-Level)

**Date**: 2026-03-18  
**Scope**: Robot logging and observability at production trading infrastructure level  
**Constraint**: Audit only — no implementation or refactoring

---

## 1. LOGGING ARCHITECTURE OVERVIEW

### 1.1 Logging Entry Points

| Component | Location | Role |
|-----------|----------|------|
| **RobotEngine** | RobotEngine.cs | ~100 LogEvent calls; engine lifecycle, connection, reconciliation, mismatch observations |
| **StreamStateMachine** | StreamStateMachine.cs | Stream state transitions, range events, bar routing, HEARTBEAT |
| **InstrumentExecutionAuthority (IEA)** | IEA.*.cs | Bootstrap, recovery, adoption, flatten authority, order registry |
| **NinjaTraderSimAdapter** | NinjaTraderSimAdapter.NT.cs | Execution updates, fills, protective recovery, bootstrap snapshot |
| **ReconciliationRunner** | ReconciliationRunner.cs | RECONCILIATION_CONTEXT, RECONCILIATION_PASS_SUMMARY, RECONCILIATION_QTY_MISMATCH |
| **HealthMonitor** | HealthMonitor.cs | Connection status, data loss, ReportCritical callback |
| **NotificationService** | NotificationService.cs | Pushover enqueue, CRITICAL_EVENT_REPORTED |
| **RobotLoggingService** | RobotLoggingService.cs | LOG_BACKPRESSURE_DROP, LOG_PIPELINE_METRIC, LOG_WRITE_FAILURE |
| **ExecutionEventWriter** | ExecutionEventWriter.cs | Canonical execution events (separate pipeline) |
| **EmergencyLogger** | EmergencyLogger.cs | Fallback when primary logging fails |

### 1.2 Output Destinations

| Destination | Path | Content |
|-------------|------|---------|
| **Robot ENGINE** | `logs/robot/robot_ENGINE.jsonl` | stream=__engine__, instrument="" |
| **Robot per-instrument** | `logs/robot/robot_<INSTRUMENT>.jsonl` | Per-instrument stream/execution events |
| **Execution events** | `automation/logs/execution_events/<date>/<instrument>.jsonl` | Canonical execution events (CanonicalExecutionEvent) |
| **Health sink** | `logs/health/<trading_date>_<instrument>_<stream>_<slot>.jsonl` | WARN+ERROR+CRITICAL + selected INFO (TRADE_COMPLETED, CRITICAL_EVENT_REPORTED) |
| **Emergency fallback** | `logs/robot/robot_ENGINE_fallback.jsonl` | When RobotLoggingService unavailable for ENGINE events |
| **Error sidecar** | `logs/robot/robot_logging_errors.txt` | Backpressure, worker errors, write failures |
| **Notification errors** | `notification_errors.log` | Pushover send failures |
| **Daily summary** | `logs/robot/daily_YYYYMMDD.md` | Human-readable summary (best-effort) |
| **Frontend feed** | `logs/robot/frontend_feed.jsonl` | Watchdog-consumable events (filtered by LIVE_CRITICAL) |

### 1.3 Consumers

| Consumer | Source | Purpose |
|----------|--------|---------|
| **EventFeedGenerator** | robot_*.jsonl | Filters LIVE_CRITICAL, rate-limits, writes frontend_feed.jsonl |
| **Watchdog Aggregator** | frontend_feed.jsonl | Orchestrates processing |
| **EventProcessor** | frontend_feed.jsonl | Updates state manager |
| **Incident Recorder** | frontend_feed.jsonl | Start/end incident pairs |
| **Notification Service** | HealthMonitor.ReportCritical | Pushover alerts (whitelist: EXECUTION_GATE_INVARIANT_VIOLATION, DISCONNECT_FAIL_CLOSED_ENTERED) |
| **Tools** | robot_*.jsonl, execution_events | log_audit.py, check_loud_errors.py, check_recent_critical_events.py |

---

## 2. EVENT TAXONOMY & STRUCTURE

### 2.1 RobotEventTypes Coverage

- **~100+ event types** in `_levelMap` and `_allEvents`
- Centralized level assignment via `RobotEventTypes.GetLevel()`
- Validation via `RobotEventTypes.IsValid()` — unregistered types trigger `UNREGISTERED_EVENT_TYPE` warning

### 2.2 Redundant / Unclear Events

| Issue | Events | Notes |
|-------|--------|-------|
| **Heartbeat overlap** | ENGINE_TICK_CALLSITE, ENGINE_TICK_HEARTBEAT, ENGINE_TIMER_HEARTBEAT, ENGINE_ALIVE, HEARTBEAT (stream) | Multiple concepts for "engine is alive"; ENGINE_HEARTBEAT deprecated |
| **Legacy aliases** | BE_TRIGGER_REACHED = BE_TRIGGERED | Comment says "Legacy alias" |
| **Similar semantics** | RECONCILIATION_CONTEXT vs RECONCILIATION_ORDER_SOURCE_BREAKDOWN | Both diagnostic for reconciliation; different granularity |

### 2.3 Missing from RobotEventTypes Registry

| Event | Emitted By | Consequence |
|-------|------------|-------------|
| **RECONCILIATION_ORDER_SOURCE_BREAKDOWN** | RobotEngine.AssembleMismatchObservations | UNREGISTERED_EVENT_TYPE warning; falls to heuristic (INFO) |
| **EXECUTION_EVENT_WRITE_FAILED** | ExecutionEventWriter | UNREGISTERED_EVENT_TYPE |
| **CRITICAL_NOTIFICATION_SKIPPED** | NotificationService | UNREGISTERED_EVENT_TYPE |
| **LOGGER_CONVERSION_RETURNED_NULL** | RobotLogger fallback | UNREGISTERED_EVENT_TYPE |

### 2.4 Severity Levels

- **INFO**, **WARN**, **ERROR**, **CRITICAL**, **DEBUG**
- ERROR and CRITICAL bypass rate limiting, diagnostics filter, and backpressure (never dropped)
- Under backpressure (queue ≥50k): DEBUG dropped first, then INFO; WARN/ERROR/CRITICAL retained

---

## 3. SIGNAL vs NOISE ANALYSIS

### 3.1 High-Value Signals (MUST KEEP)

| Event | Purpose |
|-------|---------|
| CONNECTION_LOST, CONNECTION_LOST_SUSTAINED, CONNECTION_RECOVERED | Connection lifecycle |
| DISCONNECT_FAIL_CLOSED_ENTERED, DISCONNECT_RECOVERY_* | Fail-closed state |
| ENGINE_TICK_STALL_DETECTED, ENGINE_TICK_STALL_RECOVERED | Engine liveness |
| ENGINE_TICK_CALLSITE | Watchdog liveness (rate-limited 5s in feed) |
| RECONCILIATION_QTY_MISMATCH, RECONCILIATION_PASS_SUMMARY | Position reconciliation |
| EXECUTION_FILLED, EXECUTION_FILL_UNMAPPED, EXECUTION_GHOST_FILL_DETECTED | Fill integrity |
| BOOTSTRAP_*, RECOVERY_DECISION_*, RECOVERY_TRIGGERED | Bootstrap/recovery decisions |
| FLATTEN_*, FORCED_FLATTEN_* | Flatten lifecycle |
| KILL_SWITCH_ACTIVE, DUPLICATE_INSTANCE_DETECTED | Critical safety |
| LOG_BACKPRESSURE_DROP, LOG_WRITE_FAILURE | Logging health |

### 3.2 Medium Diagnostic (Conditional Usefulness)

| Event | Condition |
|-------|-----------|
| RECONCILIATION_ORDER_SOURCE_BREAKDOWN | Useful when broker_working ≠ iea_working; can be noisy if persistent mismatch |
| RECONCILIATION_CONTEXT | Useful for RECONCILIATION_QTY_MISMATCH forensics |
| BAR_ACCEPTED, BAR_DELIVERY_TO_STREAM | Rate-limited; useful for bar flow debugging |
| RANGE_COMPUTE_*, PRE_HYDRATION_* | Range-building diagnostics |
| IEA_HEARTBEAT | Rate-limited; IEA liveness |

### 3.3 Low-Value Noise (Should Remove or Throttle)

| Event | Issue |
|-------|-------|
| **ENGINE_TICK_CALLSITE** (every tick) | Emitted every OnBarUpdate; rate-limited to 5s in feed, but still floods robot_*.jsonl when diagnostics enabled |
| **BAR_ROUTING_DIAGNOSTIC** | 1/min rate limit; low decision value |
| **TIMETABLE_VALIDATED, TIMETABLE_UPDATED, TIMETABLE_LOADED** | Rate-limited 30 min in feed; still periodic noise |
| **RECONCILIATION_ORDER_SOURCE_BREAKDOWN** | Fires every reconciliation pass per instrument when mismatch; no throttling; not in EngineLogDedupe |
| **CRITICAL_NOTIFICATION_SKIPPED** | Fires for every run_id when rate-limited; can flood (e.g. 7+ instances × 1 event) |
| **UPDATE_APPLIED** | DEBUG; per-tick stream state; very high volume |
| **BE_INTENTS_SCANNED, BE_SCAN_THROTTLED** | DEBUG; break-even scan diagnostics |

### 3.4 Exact Events Too Noisy

- **RECONCILIATION_ORDER_SOURCE_BREAKDOWN**: Every 60s per instrument with broker≠IEA; no dedupe
- **CRITICAL_NOTIFICATION_SKIPPED**: One per strategy instance when emergency rate limit hits
- **ENGINE_TICK_CALLSITE**: Every tick in robot logs (feed rate-limits, but robot files get all)

### 3.5 Events That Should Be Throttled or Removed

- **RECONCILIATION_ORDER_SOURCE_BREAKDOWN**: Add to EngineLogDedupe or rate-limit (e.g. 1/min per instrument)
- **CRITICAL_NOTIFICATION_SKIPPED**: Throttle or aggregate; avoid per-instance flood
- **ENGINE_TICK_CALLSITE**: Consider robot-side rate limit (not just feed-side) when diagnostics enabled

---

## 4. LOG FREQUENCY & THROTTLING

### 4.1 Current Throttling

| Mechanism | Scope | Config |
|-----------|-------|--------|
| **event_rate_limits** | RobotLoggingService | logging.json: BAR_ACCEPTED 12/min, BAR_DELIVERY_TO_STREAM 12/min, IEA_HEARTBEAT 12/min, etc. |
| **EngineLogDedupe** | 4 event types | 10s window: EXECUTION_BLOCKED, RECONCILIATION_QTY_MISMATCH, POSITION_DRIFT_DETECTED, EXPOSURE_INTEGRITY_VIOLATION |
| **Watchdog feed** | EventFeedGenerator | ENGINE_TICK_CALLSITE 5s, BAR_RECEIVED_NO_STREAMS 60s, TIMETABLE_* 30 min |
| **DEBUG volume cap** | RobotLoggingService | debug_volume_cap_per_minute when diagnostics enabled |
| **Stream HEARTBEAT** | StreamStateMachine | 7 min interval |
| **BAR_HEARTBEAT** | RobotEngine | 5 min per instrument |

### 4.2 Missing Throttling

| Event | Current | Risk |
|-------|---------|------|
| **RECONCILIATION_ORDER_SOURCE_BREAKDOWN** | None | Every reconciliation tick per instrument; can flood during persistent mismatch |
| **CRITICAL_NOTIFICATION_SKIPPED** | None | One per instance on rate limit; multi-instance flood |
| **RECONCILIATION_CONTEXT** | None | Emitted before RECONCILIATION_QTY_MISMATCH; acceptable |
| **Repeated recovery attempts** | Per-attempt | RECOVERY_* events; some may repeat |

### 4.3 Incorrectly Applied Throttling

- **ENGINE_TICK_CALLSITE**: WARN level (never dropped under backpressure) — correct for watchdog, but robot logs still get every tick when diagnostics on
- **RECONCILIATION_QTY_MISMATCH**: In EngineLogDedupe (10s) — may suppress legitimate repeated mismatches if they persist >10s

### 4.4 Log Flooding Risk

- **Backpressure**: Queue 50k → DEBUG dropped, then INFO. ENGINE_TICK_CALLSITE is WARN, so retained.
- **Multi-instance**: 7+ NinjaTrader strategies → 7× CONNECTION_LOST, 7× CRITICAL_NOTIFICATION_SKIPPED
- **ExecutionEventWriter**: File lock contention (MYM.jsonl) when multiple instances write; EXECUTION_EVENT_WRITE_FAILED floods

---

## 5. READABILITY & DEBUGGING USABILITY

### 5.1 Strengths

- **Structured JSONL**: Machine-parseable; tools exist (log_audit.py, check_loud_errors.py)
- **Standardized fields**: ts_utc, event, instrument, trading_date, stream, run_id at top level
- **Daily summary**: daily_YYYYMMDD.md for human scan

### 5.2 Logs Missing Critical Fields

| Event | Missing | Impact |
|-------|---------|--------|
| **RECONCILIATION_ORDER_SOURCE_BREAKDOWN** | run_id in some code paths | Harder to correlate with engine instance |
| **EXECUTION_FILLED** | intent_id sometimes in data | Fill-to-intent trace harder |
| **BOOTSTRAP_DECISION_*** | adoption_candidate_count, broker_working at decision time | Decision audit incomplete |

### 5.3 Too Verbose to Read

- **ENGINE_TICK_CALLSITE** at full rate in robot_ENGINE.jsonl
- **BAR_ACCEPTED**, **BAR_DELIVERY_TO_STREAM** when diagnostics on
- **PRE_HYDRATION_***, **RANGE_COMPUTE_*** DEBUG events

### 5.4 Too Sparse to Diagnose

- **RECOVERY_DECISION_*** without full reconstruction context (broker snapshot, journal state)
- **BOOTSTRAP_DECISION_*** without snapshot details (broker_working, journal_qty at capture time)
- **FLATTEN_*** without order IDs when flatten fails

### 5.5 What Makes Debugging Harder

- **Split pipelines**: robot_*.jsonl vs execution_events/*.jsonl — two places to grep
- **run_id proliferation**: Multiple strategies → many run_ids; correlation requires run_id filter
- **Event type not in registry**: UNREGISTERED_EVENT_TYPE adds noise
- **Health sink**: Separate files under logs/health/; not obvious for incident triage

---

## 6. INCIDENT RECONSTRUCTION QUALITY

### 6.1 Test Case: 2026-03-17 Disconnect + Order Loss

**Can logs reconstruct:**

| Question | Answer | Gap |
|----------|--------|-----|
| **Timeline** | Partially | CONNECTION_LOST, DISCONNECT_FAIL_CLOSED_ENTERED present; bootstrap timing race not explicitly logged |
| **Decisions taken** | Partially | BOOTSTRAP_DECISION_RESUME present; "why RESUME" (broker_working=0) not always clear |
| **State transitions** | Yes | DISCONNECT_FAIL_CLOSED_ENTERED, RECOVERY_* |
| **Root cause** | Partially | Incident report identified bootstrap timing + adoption gap; logs don't explicitly record "snapshot taken before broker repopulated" |

### 6.2 Missing Links for Forensic Reconstruction

1. **Bootstrap snapshot timing**: No log of "snapshot captured at T, broker_working=X" with explicit timestamp of when broker state was read
2. **Adoption attempt before fail-closed**: RECONCILIATION_RECOVERY_ADOPTION_ATTEMPT added in fix; before fix, no log of "tried adoption, failed"
3. **Order IDs in flatten path**: When FLATTEN_FAIL or FLATTEN_FAILED_ALL_RETRIES, which orders were targeted?
4. **ExecutionEventWriter failures**: EXECUTION_EVENT_WRITE_FAILED goes to robot_ENGINE.jsonl (stream=__engine__) but event type unregistered; execution_events/*.jsonl may have gaps when write fails

---

## 7. REAL-TIME OPERATIONAL VALUE

### 7.1 Can Operator Quickly Answer?

| Question | Logs That Help | Gap |
|----------|----------------|------|
| **Am I safe?** | KILL_SWITCH_ACTIVE, DUPLICATE_INSTANCE_DETECTED, CONNECTION_LOST | No single "SAFETY_STATUS" event; must infer from multiple |
| **Am I blocked?** | EXECUTION_BLOCKED, DISCONNECT_FAIL_CLOSED_ENTERED, INSTRUMENT_HALTED | Scattered across event types |
| **Do I have unmanaged exposure?** | RECONCILIATION_QTY_MISMATCH, EXPOSURE_INTEGRITY_VIOLATION, UNOWNED_LIVE_ORDER_DETECTED | Good coverage |
| **Is the system recovering?** | DISCONNECT_RECOVERY_*, RECOVERY_DECISION_*, BOOTSTRAP_* | Good coverage |

### 7.2 Logs That Should Exist But Don't

- **OPERATOR_STATUS_SNAPSHOT**: Periodic (e.g. 1/min) summary: connection, blocked instruments, exposure, recovery state
- **FAIL_CLOSED_REASON**: When instrument blocked, explicit reason (e.g. REGISTRY_BROKER_DIVERGENCE, RECONCILIATION_QTY_MISMATCH)
- **ADOPTION_OUTCOME**: When TryRecoveryAdoption runs, log success/failure with counts

### 7.3 Logs That Exist But Are Buried in Noise

- **RECOVERY_DECISION_*** in ENGINE log; mixed with ENGINE_TICK_CALLSITE
- **BOOTSTRAP_DECISION_*** same
- **RECONCILIATION_ORDER_SOURCE_BREAKDOWN** when mismatch persistent — repeats every 60s

---

## 8. LOG DESTINATION & ACCESSIBILITY

### 8.1 Routing Rules

- **stream=__engine__** OR **instrument=""** → robot_ENGINE.jsonl
- **intent_id present** OR **execution event** → robot_<instrument>.jsonl
- **Health sink** (if enabled): WARN+ERROR+CRITICAL + TRADE_COMPLETED, CRITICAL_EVENT_REPORTED

### 8.2 BOOTSTRAP / ADOPTION / FAIL_CLOSED

| Event | Destination | In LIVE_CRITICAL? |
|-------|-------------|-------------------|
| BOOTSTRAP_STARTED, BOOTSTRAP_DECISION_* | robot_ENGINE.jsonl | No (most) |
| RECOVERY_DECISION_*, RECOVERY_TRIGGERED | robot_ENGINE.jsonl | No (most) |
| RECONCILIATION_QTY_MISMATCH | robot_ENGINE.jsonl | Yes |
| RECONCILIATION_PASS_SUMMARY | robot_ENGINE.jsonl | Yes |
| FORCED_FLATTEN_* | robot_<instrument>.jsonl | Yes (some) |

### 8.3 Logs Hard to Find

- **Health sink**: logs/health/ — separate from main robot logs; not in standard grep path
- **Execution events**: automation/logs/execution_events/ — different directory tree
- **BOOTSTRAP_***, **RECOVERY_DECISION_***: In robot_ENGINE.jsonl but not in LIVE_CRITICAL → don't reach frontend_feed → watchdog/incident recorder may miss

### 8.4 Logs Not Surfaced Correctly

- **RECONCILIATION_ORDER_SOURCE_BREAKDOWN**: Not in LIVE_CRITICAL → never reaches watchdog
- **EXECUTION_EVENT_WRITE_FAILED**: In robot_ENGINE.jsonl; unregistered type; not in LIVE_CRITICAL
- **CRITICAL_NOTIFICATION_SKIPPED**: In robot_ENGINE.jsonl; unregistered; not in LIVE_CRITICAL

---

## 9. DUPLICATION & REDUNDANCY

### 9.1 Same Event, Multiple Modules

| Concept | RobotEngine | IEA | Adapter | Watchdog |
|---------|-------------|-----|---------|----------|
| **Connection status** | CONNECTION_LOST | — | — | Consumes |
| **Execution blocked** | EXECUTION_BLOCKED | — | — | — |
| **Fill** | — | — | EXECUTION_FILLED | — |
| **Recovery** | DISCONNECT_RECOVERY_* | RECOVERY_DECISION_* | — | — |
| **Bootstrap** | — | BOOTSTRAP_* | OnBootstrapSnapshotRequested | — |

### 9.2 Overlapping Responsibilities

- **RobotEngine** vs **IEA**: RobotEngine orchestrates; IEA executes. Recovery decisions in IEA; disconnect state in RobotEngine. Clear separation but both log recovery-related events.
- **Adapter** vs **IEA**: Adapter reports fills, IEA manages registry. Adapter logs EXECUTION_FILLED; IEA logs ORDER_REGISTRY_*.
- **RobotLogger** vs **RobotLoggingService**: Logger converts and enqueues; Service routes and writes. Single pipeline; no duplication.

### 9.3 Redundant Logs

- **ENGINE_TICK_CALLSITE** and **ENGINE_ALIVE**: Both indicate liveness; ENGINE_ALIVE is fallback when ENGINE_TICK_CALLSITE not emitted
- **RECONCILIATION_CONTEXT** and **RECONCILIATION_ORDER_SOURCE_BREAKDOWN**: Both diagnostic for reconciliation; CONTEXT is before QTY_MISMATCH; BREAKDOWN is per-instrument broker vs IEA

---

## 10. MISSING CRITICAL OBSERVABILITY

| Gap | Impact |
|-----|--------|
| **Final decision outcome** | RECOVERY_DECISION_RESUME/ADOPT/FLATTEN logged; but "adoption succeeded, N orders adopted" not always explicit |
| **Execution blocks** | EXECUTION_BLOCKED has reason; "order not submitted because X" not always logged for every block path |
| **Ownership resolution** | TryAdoptBrokerOrderIfNotInRegistry: success/failure not always logged per order |
| **Recovery success vs failure** | RECOVERY_RESOLVED, RECOVERY_HALTED exist; "recovery completed with/without flatten" could be clearer |
| **Bootstrap snapshot timing** | No explicit "snapshot at T, broker_working=X, journal_qty=Y" |
| **OPERATOR_STATUS_SNAPSHOT** | No periodic summary for "am I safe?" |
| **EXECUTION_EVENT_WRITE_FAILED** in registry | Unregistered; noisy UNREGISTERED_EVENT_TYPE |

---

## 11. FINAL CLASSIFICATION TABLE

| Event/System | Status | Action | Reason |
|--------------|--------|--------|--------|
| CONNECTION_*, DISCONNECT_*, ENGINE_TICK_STALL_* | KEEP | — | Critical signals |
| RECONCILIATION_QTY_MISMATCH, RECONCILIATION_PASS_SUMMARY | KEEP | — | Critical |
| EXECUTION_FILLED, EXECUTION_FILL_UNMAPPED, EXECUTION_GHOST_* | KEEP | — | Fill integrity |
| BOOTSTRAP_*, RECOVERY_DECISION_* | KEEP | — | Decision audit |
| FLATTEN_*, FORCED_FLATTEN_* | KEEP | — | Flatten lifecycle |
| ENGINE_TICK_CALLSITE | REDUCE | Throttle at robot (not just feed) | Every tick floods |
| RECONCILIATION_ORDER_SOURCE_BREAKDOWN | REDUCE | Add to EngineLogDedupe or rate-limit; add to RobotEventTypes | Noisy when persistent mismatch |
| CRITICAL_NOTIFICATION_SKIPPED | REDUCE | Throttle/aggregate | Multi-instance flood |
| BAR_ACCEPTED, BAR_DELIVERY_TO_STREAM | REDUCE | Already rate-limited | OK if diagnostics off |
| OPERATOR_STATUS_SNAPSHOT | ADD | — | "Am I safe?" |
| ADOPTION_OUTCOME | ADD | — | TryRecoveryAdoption result |
| BOOTSTRAP_SNAPSHOT_TIMING | ADD | — | Forensic reconstruction |
| RECONCILIATION_ORDER_SOURCE_BREAKDOWN | ADD | To RobotEventTypes, LIVE_CRITICAL (optional) | Registry + visibility |
| EXECUTION_EVENT_WRITE_FAILED | ADD | To RobotEventTypes | Registry |
| CRITICAL_NOTIFICATION_SKIPPED | ADD | To RobotEventTypes | Registry |
| BOOTSTRAP_*, RECOVERY_DECISION_* | MOVE | Add to LIVE_CRITICAL for watchdog | Incident reconstruction |
| Health sink | MOVE | Document; consider merging into main log or dedicated "critical only" | Hard to find |

---

## 12. FINAL ANSWER

### 12.1 Is the Logging System Currently:

| Dimension | Assessment |
|-----------|------------|
| **Usable** | Yes, for routine debugging and tooling |
| **Overloaded** | Partially — ENGINE_TICK_CALLSITE, RECONCILIATION_ORDER_SOURCE_BREAKDOWN, CRITICAL_NOTIFICATION_SKIPPED can flood |
| **Insufficient** | Partially — missing OPERATOR_STATUS_SNAPSHOT, adoption outcome, bootstrap timing; BOOTSTRAP/RECOVERY not in watchdog feed |
| **Well-structured** | Partially — good taxonomy and routing; unregistered types, split pipelines (robot vs execution_events), health sink obscurity |

### 12.2 Top 5 Logging Problems

1. **RECONCILIATION_ORDER_SOURCE_BREAKDOWN unthrottled and unregistered** — Fires every reconciliation pass per instrument when broker≠IEA; triggers UNREGISTERED_EVENT_TYPE; not in LIVE_CRITICAL.
2. **Critical events not reaching watchdog** — BOOTSTRAP_*, RECOVERY_DECISION_* not in LIVE_CRITICAL → incident recorder and real-time UI miss key decision events.
3. **Multi-pipeline fragmentation** — robot_*.jsonl, execution_events/*.jsonl, logs/health/; operator must check multiple locations.
4. **Unregistered event types** — RECONCILIATION_ORDER_SOURCE_BREAKDOWN, EXECUTION_EVENT_WRITE_FAILED, CRITICAL_NOTIFICATION_SKIPPED cause UNREGISTERED_EVENT_TYPE noise.
5. **No single "am I safe?" signal** — Operator must infer from CONNECTION_LOST, EXECUTION_BLOCKED, RECONCILIATION_QTY_MISMATCH, etc.

### 12.3 Single Highest-Impact Improvement

**Add BOOTSTRAP_* and RECOVERY_DECISION_* to LIVE_CRITICAL_EVENT_TYPES** so the watchdog and incident recorder receive these events. This would:

- Enable incident reconstruction for disconnect → restart → order loss scenarios
- Close the gap identified in the 2026-03-17 incident (bootstrap/resume decisions not visible to monitoring)
- Require no robot code changes — only `modules/watchdog/config.py`

Secondary: **Register RECONCILIATION_ORDER_SOURCE_BREAKDOWN in RobotEventTypes** and add it to EngineLogDedupe (or rate-limit) to reduce noise and eliminate UNREGISTERED_EVENT_TYPE.

---

## Appendix: Key File References

| File | Purpose |
|------|---------|
| RobotCore_For_NinjaTrader/RobotEventTypes.cs | Event registry, level map |
| RobotCore_For_NinjaTrader/RobotLoggingService.cs | Routing, backpressure, rate limit |
| RobotCore_For_NinjaTrader/RobotLogger.cs | Conversion, routing to service |
| RobotCore_For_NinjaTrader/EngineLogDedupe.cs | Engine-level dedupe |
| modules/watchdog/config.py | LIVE_CRITICAL_EVENT_TYPES |
| modules/watchdog/event_feed.py | Feed filtering, rate limits |
| configs/robot/logging.json | event_rate_limits, diagnostics |
