# Latent Signal Discovery Audit (Robot + Watchdog)

**Date:** 2026-03-24  
**Scope:** Signals logged or derivable in-repo that are **not** covered by the current [Daily Audit Framework](DAILY_AUDIT_FRAMEWORK_ROBOT_WATCHDOG.md) (v1.3) in a first-class way — candidates for **early anomaly detection**, **root cause**, and **confidence**.

**Explicitly out of scope for this document:** Re-stating metrics already mapped in §3 of the daily audit (e.g. `ENGINE_CPU_PROFILE`, `RECONCILIATION_PASS_SUMMARY` counts, core `IEA_QUEUE_PRESSURE`, primary connectivity events).

---

## A. Summary

| Metric | Value |
|--------|-------|
| **New signals catalogued** | 18 |
| **By source** | event: 14 · derived: 4 |
| **By domain** | Safety: 5 · Execution: 4 · Reconciliation: 4 · Performance: 2 · Supervisory: 1 · Watchdog: 2 |

**Themes:** (1) **State-consistency gate** and **bootstrap** narratives are rich but absent from audit domains. (2) **Watchdog** already computes **reliability_metrics** and **metrics_history** from `incidents.jsonl` — not wired to the daily audit. (3) **Pre-failure** signals cluster around **engine tick stalls**, **bar/data stall**, and **adoption/registry** degradation before fail-closed.

---

## B. Signals list (structured)

### Safety

```json
{
  "signal_name": "STATE_CONSISTENCY_GATE_LIFECYCLE",
  "source": "event",
  "origin": "MismatchEscalationCoordinator",
  "description": "Sequence including RECONCILIATION_MISMATCH_DETECTED, STATE_CONSISTENCY_GATE_ENGAGED, STATE_CONSISTENCY_GATE_PERSISTENT_MISMATCH, STATE_CONSISTENCY_GATE_RECONCILIATION_STARTED, and related gate releases — emitted via RobotLogger (may not all appear in RobotEventTypes.cs as named levels but present in JSONL `event` field).",
  "why_it_matters": "Shows escalation path to persistent mismatch / fail-closed before terminal events; finer than a single mismatch count.",
  "how_to_compute": "Filter `event` prefix or exact match for STATE_CONSISTENCY_GATE_*; build per-instrument timelines; count phase transitions.",
  "example": "GATE_ENGAGED → PERSISTENT_MISMATCH → RECONCILIATION_STARTED within 90s on MNQ",
  "recommended_audit_section": "Safety | NewSection: GateEscalation",
  "priority": "HIGH"
}
```

```json
{
  "signal_name": "DUPLICATE_INSTANCE_DETECTED",
  "source": "event",
  "origin": "RobotEngine / coordination",
  "description": "CRITICAL: multiple strategy instances; registry in `RobotEventTypes` as DUPLICATE_INSTANCE_DETECTED.",
  "why_it_matters": "Catastrophic correctness risk; incident recorder maps to instant CRITICAL incident.",
  "how_to_compute": "Count lines where `event` == DUPLICATE_INSTANCE_DETECTED.",
  "example": "1 occurrence → force overall CRITICAL regardless of other domains",
  "recommended_audit_section": "Safety",
  "priority": "HIGH"
}
```

```json
{
  "signal_name": "REGISTRY_BROKER_DIVERGENCE_AND_STALE_ORDER",
  "source": "event",
  "origin": "IEA / registry",
  "description": "REGISTRY_BROKER_DIVERGENCE (CRITICAL), STALE_QTSW2_ORDER_DETECTED (CRITICAL), REGISTRY_BROKER_DIVERGENCE_ADOPTED — registry truth vs broker.",
  "why_it_matters": "Direct precursor to recovery / flatten / adoption storms; not in current Safety table as explicit row.",
  "how_to_compute": "Per-day counts by instrument; time-to-subsequent ADOPTION_* or BOOTSTRAP_*.",
  "example": "REGISTRY_BROKER_DIVERGENCE at 10:01 → BOOTSTRAP_DECISION_ADOPT at 10:03",
  "recommended_audit_section": "Safety | Execution",
  "priority": "HIGH"
}
```

```json
{
  "signal_name": "BOOTSTRAP_DECISION_NARRATIVE",
  "source": "event",
  "origin": "RobotEngine / recovery",
  "description": "BOOTSTRAP_* family: BOOTSTRAP_DECISION_RESUME | ADOPT | FLATTEN | HALT, BOOTSTRAP_HALTED, BOOTSTRAP_OPERATOR_ACTION_REQUIRED, BOOTSTRAP_SNAPSHOT_STALE, CONNECTION_RECOVERY_REQUIRES_BOOTSTRAP.",
  "why_it_matters": "Explains post-disconnect or cold-start decisions; daily audit timeline only partially overlaps.",
  "how_to_compute": "Chronological list; pair with CONNECTION_RECOVERY_*; classify outcome.",
  "example": "CONNECTION_RECOVERY_REQUIRES_BOOTSTRAP → BOOTSTRAP_DECISION_ADOPT → BOOTSTRAP_ADOPTION_COMPLETED",
  "recommended_audit_section": "Connectivity | NewSection: Bootstrap",
  "priority": "MEDIUM"
}
```

```json
{
  "signal_name": "DATA_LOSS_AND_DATA_STALL",
  "source": "event",
  "origin": "Stream / data path + Watchdog",
  "description": "DATA_LOSS_DETECTED, DATA_STALL_RECOVERED (robot); Watchdog `incident_recorder` maps DATA_LOSS_DETECTED / DATA_STALL_DETECTED → DATA_STALL incidents with duration.",
  "why_it_matters": "Silent degradation of bar quality before strategy starvation; overlaps §4.6 silent-failure heuristics but with explicit events.",
  "how_to_compute": "Count from robot ENGINE; merge with `data/watchdog/incidents.jsonl` type DATA_STALL for durations.",
  "example": "3 DATA_STALL incidents, max duration 120s",
  "recommended_audit_section": "Performance | Connectivity",
  "priority": "HIGH"
}
```

### Execution

```json
{
  "signal_name": "ADOPTION_NON_CONVERGENCE_AND_GATE_ANOMALY",
  "source": "event",
  "origin": "IEA / adoption",
  "description": "ADOPTION_NON_CONVERGENCE_ESCALATED (CRITICAL), IEA_ADOPTION_SCAN_GATE_ANOMALY (WARN), IEA_ADOPTION_SCAN_SKIPPED_NO_PROGRESS, ADOPTION_SAME_STATE_RETRY_WINDOW (WARN).",
  "why_it_matters": "Early warning of adoption loops before queue backlog peaks.",
  "how_to_compute": "Counts per instrument; rate per hour; co-occurrence with IEA_QUEUE_PRESSURE_DIAG.",
  "example": "GATE_ANOMALY ×5 then NON_CONVERGENCE_ESCALATED ×1",
  "recommended_audit_section": "Execution",
  "priority": "HIGH"
}
```

```json
{
  "signal_name": "EXECUTION_JOURNAL_ADOPTION_INDEX_HEALTH",
  "source": "event",
  "origin": "ExecutionJournal / writer",
  "description": "EXECUTION_JOURNAL_ADOPTION_INDEX_FALLBACK, EXECUTION_JOURNAL_ADOPTION_INDEX_REBUILD_FAILED, JOURNAL_INSTRUMENT_KEY_MISMATCH.",
  "why_it_matters": "Journal/index drift breaks intent↔fill correlation; incident types include journal corruption.",
  "how_to_compute": "Count WARN/ERROR events; any JOURNAL_INSTRUMENT_KEY_MISMATCH → elevate confidence risk.",
  "example": "ADOPTION_INDEX_FALLBACK twice same day",
  "recommended_audit_section": "Execution | Safety",
  "priority": "HIGH"
}
```

```json
{
  "signal_name": "EXECUTION_DEFERRED_CHAIN_RATE",
  "source": "derived",
  "origin": "Execution path",
  "description": "EXECUTION_DEFERRED_FOR_REGISTRY_RESOLUTION / RETRY / RESOLVED — DEBUG/INFO in registry.",
  "why_it_matters": "Rising deferrals without RESOLVED → registry pressure before visible mismatch.",
  "how_to_compute": "Count DEFERRED_* per window; ratio resolved/deferred; median time DEFERRED → RESOLVED from timestamps.",
  "example": "deferrals 400, resolutions 380 → stuck deferral rate 5%",
  "recommended_audit_section": "Execution",
  "priority": "MEDIUM"
}
```

```json
{
  "signal_name": "ORDER_REGISTRY_METRICS_THROUGHPUT",
  "source": "event",
  "origin": "Order registry",
  "description": "ORDER_REGISTRY_METRICS (INFO) with structured counters in `data`.",
  "why_it_matters": "Single place for registry churn; complements ORDER_REGISTRY_* lifecycle spam.",
  "how_to_compute": "Sample last METRICS line per instrument per day; delta vs prior day if stored.",
  "example": "reconciliation_mismatch_total field inside metrics payload (when present)",
  "recommended_audit_section": "Execution",
  "priority": "MEDIUM"
}
```

### Reconciliation

```json
{
  "signal_name": "MISMATCH_TO_FIRST_RECOVERY_ATTEMPT_LATENCY",
  "source": "derived",
  "origin": "MismatchEscalationCoordinator + ReconciliationRunner",
  "description": "Wall-clock from first RECONCILIATION_MISMATCH_DETECTED (or STATE_CONSISTENCY_GATE_ENGAGED) to STATE_CONSISTENCY_GATE_RECONCILIATION_STARTED or first ForceRun / gate recon callback (proxy: next RECONCILIATION_CONTEXT or RECONCILIATION_PASS_SUMMARY for instrument).",
  "why_it_matters": "Distinguishes fast vs slow institutional response; not the same as convergence time.",
  "how_to_compute": "Per instrument: min(ts_recovery) - min(ts_detect) from JSONL; deterministic pairing rules.",
  "example": "4500ms median latency",
  "recommended_audit_section": "Reconciliation | recovery_quality",
  "priority": "HIGH"
}
```

```json
{
  "signal_name": "RECONCILIATION_DEBOUNCED_AND_SECONDARY_SKIPPED",
  "source": "event",
  "origin": "ReconciliationRunner",
  "description": "RECONCILIATION_SECONDARY_INSTANCE_SKIPPED; debounced mismatch payloads ('Mismatch unchanged within debounce — recovery not re-requested') with metrics reconciliation_debounced_total.",
  "why_it_matters": "High debounce rate can mask instability; secondary skip explains missing recovery on satellite instances.",
  "how_to_compute": "Count SECONDARY_INSTANCE_SKIPPED; from mismatch metric payloads extract debounced_total trend.",
  "example": "debounced_total +15% vs 7d median",
  "recommended_audit_section": "Reconciliation",
  "priority": "MEDIUM"
}
```

```json
{
  "signal_name": "RECONCILIATION_CONTEXT_TAXONOMY",
  "source": "event",
  "origin": "ReconciliationRunner",
  "description": "RECONCILIATION_CONTEXT with taxonomy journal_ahead vs broker_ahead and open intent summaries.",
  "why_it_matters": "Root-cause vector for qty mismatch without scanning raw pass summaries only.",
  "how_to_compute": "Aggregate counts by taxonomy field; top instruments.",
  "example": "journal_ahead: 12, broker_ahead: 3",
  "recommended_audit_section": "Reconciliation",
  "priority": "MEDIUM"
}
```

```json
{
  "signal_name": "PASS_SUMMARY_HASH_STABILITY",
  "source": "derived",
  "origin": "Reconciliation",
  "description": "Repeated RECONCILIATION_PASS_SUMMARY lines with identical fingerprint (hash of selected `data` fields) over short window — indicates stuck loop without RECONCILIATION_OSCILLATION emitted.",
  "why_it_matters": "Catches 'quiet' reconciliation churn (silent anomaly).",
  "how_to_compute": "Bucket by 5min; hash subset of data; flag if same hash > N times.",
  "example": "same pass fingerprint 20× in 10m",
  "recommended_audit_section": "Reconciliation | silent_failure",
  "priority": "HIGH"
}
```

### Performance

```json
{
  "signal_name": "ENGINE_TICK_STALL_AND_CALLSITE",
  "source": "event",
  "origin": "RobotEngine",
  "description": "ENGINE_TICK_STALL_DETECTED, ENGINE_TICK_STALL_RUNTIME, ENGINE_TICK_STALL_RECOVERED, ENGINE_TICK_CALLSITE (watchdog liveness).",
  "why_it_matters": "Pre-failure before total loss of progress; Watchdog builds ENGINE_STALLED incidents from stall events.",
  "how_to_compute": "Count stalls; max duration until RECOVERED; correlate with ENGINE_CPU_PROFILE spikes.",
  "example": "STALL_RUNTIME 3×, max gap 8s to recovered",
  "recommended_audit_section": "Performance | Supervisory",
  "priority": "HIGH"
}
```

```json
{
  "signal_name": "BAR_AND_TIMETABLE_HEALTH",
  "source": "event",
  "origin": "StreamStateMachine / timetable",
  "description": "BAR_FLOW_STALLED, BAR_REJECTION_RATE_HIGH, BAR_REJECTION_SUMMARY, TIMETABLE_POLL_STALL_DETECTED / RECOVERED.",
  "why_it_matters": "Upstream cause of 'no trades' without execution errors.",
  "how_to_compute": "Counts and max rejection rate from summaries; timetable stall as CRITICAL path signal.",
  "example": "BAR_REJECTION_RATE_HIGH ×2 before BAR_FLOW_STALLED",
  "recommended_audit_section": "Performance | NewSection: DataFeed",
  "priority": "MEDIUM"
}
```

### Supervisory

```json
{
  "signal_name": "EXECUTION_GATE_INVARIANT_VIOLATION",
  "source": "event",
  "origin": "Execution / gates",
  "description": "EXECUTION_GATE_INVARIANT_VIOLATION — ERROR in RobotEventTypes.",
  "why_it_matters": "Hard invariant break; should surface in supervisory and often chain to recovery.",
  "how_to_compute": "Count; first occurrence timestamp for first_failure candidate.",
  "example": "1 event → chain trigger",
  "recommended_audit_section": "Supervisory",
  "priority": "HIGH"
}
```

### Watchdog

```json
{
  "signal_name": "WATCHDOG_RELIABILITY_METRICS_CROSSCHECK",
  "source": "derived",
  "origin": "modules/watchdog/reliability_metrics.py",
  "description": "Aggregates from `data/watchdog/incidents.jsonl`: disconnect_incidents, engine_stalls, data_stalls, forced_flatten_count, reconciliation_mismatch_count, uptime_percent over window_hours.",
  "why_it_matters": "Independent path vs robot JSONL for **confidence** and **meta.source_fallback** validation.",
  "how_to_compute": "Run same calendar day window; compare counts to robot-derived metrics; flag large divergence.",
  "example": "robot disconnects 2 vs incidents 5 → MEDIUM confidence",
  "recommended_audit_section": "Connectivity | meta.confidence",
  "priority": "HIGH"
}
```

```json
{
  "signal_name": "WATCHDOG_METRICS_HISTORY_ROLLUP",
  "source": "derived",
  "origin": "modules/watchdog/metrics_history.py",
  "description": "Weekly/monthly aggregates of incidents to metrics_history.jsonl.",
  "why_it_matters": "Extends §4.7 drift with ops-maintained history not only daily_audit JSON.",
  "how_to_compute": "Read last week bucket; compare incident rates to today’s robot audit.",
  "example": "disconnect_incidents week-over-week +40%",
  "recommended_audit_section": "drift | Watchdog",
  "priority": "MEDIUM"
}
```

### Other (canonical execution stream)

```json
{
  "signal_name": "CANONICAL_EXECUTION_JSONL_INTENT_MISMATCH",
  "source": "derived",
  "origin": "ExecutionEventWriter → per-instrument JSONL",
  "description": "Append-only canonical execution events (event_id, event_sequence) — can detect orphan executions vs robot `intent_id` lifecycle in robot JSONL.",
  "why_it_matters": "Catches desync between canonical stream and RobotLogEvent routing.",
  "how_to_compute": "Join on intent_id + day; flag executions without matching ORDER_REGISTERED / lifecycle in robot log.",
  "example": "3 EXECUTION_OBSERVED without LIFECYCLE_TRANSITIONED in robot",
  "recommended_audit_section": "Execution | NewSection: Parity",
  "priority": "MEDIUM"
}
```

---

## C. Recommendations (where to add)

| Signal / theme | **Summary** | **Timeline** | **Incident chains** | **New section** |
|------------------|-------------|--------------|---------------------|-----------------|
| Gate lifecycle + mismatch→recovery latency | ✓ recovery_quality / Safety bullets | ✓ trigger/steps | ✓ link phases | GateEscalation |
| Adoption non-convergence + gate anomaly | ✓ Execution | ✓ | ✓ | — |
| Registry divergence / stale order | ✓ Safety | ✓ | ✓ | — |
| Engine tick stall + bar/timetable | ✓ Performance / DataFeed | ✓ | ✓ pre-failure | **DataFeed** |
| Watchdog reliability crosscheck | ✓ **confidence** | optional | — | — |
| Pass-summary hash stability | ✓ silent_failure | sparse | ✓ unstable loop | — |
| Bootstrap narrative | ✓ connectivity row | ✓ | ✓ | **Bootstrap** |
| Canonical execution parity | optional | — | — | **Parity** |

---

## D. Implementation notes

1. **STATE_CONSISTENCY_** strings: confirm exact `event` names in a sample `robot_<instrument>.jsonl` (coordinator emits long names; some may be canonical-only). Audit tool should match **prefix** `STATE_CONSISTENCY_GATE` and related.
2. **Watchdog paths:** `modules/watchdog/config.py` sets `INCIDENTS_FILE` under project data — daily audit should accept `--watchdog-data-dir` for parity.
3. **DEBUG lines:** `EXECUTION_DEFERRED_*` may be DEBUG level — verify logging config enables them in production audit windows.

---

## E. References (code)

- `RobotCore_For_NinjaTrader/RobotEventTypes.cs` — event registry
- `RobotCore_For_NinjaTrader/Execution/MismatchEscalationCoordinator.cs` — gate emissions
- `RobotCore_For_NinjaTrader/Execution/ReconciliationRunner.cs` — context, debounce, secondary skip
- `RobotCore_For_NinjaTrader/Execution/ProtectiveCoverageCoordinator.cs` — protective audit paths
- `modules/watchdog/incident_recorder.py` — `INCIDENT_START_EVENTS` / `INCIDENT_END_EVENTS`
- `modules/watchdog/reliability_metrics.py`, `metrics_history.py`
- `docs/robot/audits/DAILY_AUDIT_FRAMEWORK_ROBOT_WATCHDOG.md`
