# Full-System Daily Audit Framework (Robot + Watchdog)

**Version:** 1.6  
**Date:** 2026-03-24  
**Purpose:** Decision-grade daily risk intelligence — not a log dump. Answers: *“Is it safe to trade tomorrow?”*

---

## 1. Audit philosophy

| Principle | Meaning |
|-----------|---------|
| **Summarize** | Aggregate counts, durations, rates, and worst-case — never paste raw lines unless attached as an appendix for forensics. |
| **Deterministic** | Same inputs (same files, same day window, same version of the audit script) → identical JSON and the same human summary text (sorted keys, fixed rounding). |
| **Actionable** | Every WARNING/CRITICAL must name *what* broke, *where* (instrument/stream/run), and *what to check next*. |
| **Domain-grouped** | Metrics roll up under fixed domains (below); cross-domain issues still surface in OVERALL STATUS. |
| **Trade-readiness** | Top-level **`trade_readiness`** (§4.8) is the **only** go/no-go line — derived strictly from `overall_status`, `confidence_enforced_status`, domain safety/execution, `silent_failure`, and `drift_risk`. |
| **Confidence** | **`confidence`** (HIGH / MEDIUM / LOW) states how trustworthy the conclusion is — **never** treat `overall_status: OK` as meaningful when confidence is **LOW**. |
| **Timeline** | A single compressed Chicago-time narrative of high-signal events; domain **[DETAILS]** cite relevant spans (`→ See timeline …`). |
| **Causality** | **Incident chains** group events into recoverable sequences so the report answers *why*, not only *what*. |
| **Predictive focus** | **Tier 1–2 signals only** (§3.0): early warning **without** expanding the report into a log viewer; Tier 3 stays **debug-only** (see §3.0). |

**Five layers (institutional stack):**

| Layer | Section | Question answered |
|-------|---------|-------------------|
| **1 — Outcome** | Domains + rollup | *What broke? (mismatches, fail-closed, disconnects)* |
| **2 — Timeline** | §5 | *What happened, when?* |
| **3 — Causality** | §6 | *What caused what?* |
| **4 — Early warning** | §3.0 + §5 selective + §6 extensions | *Gate / adoption / latency / stalls **before** terminal failure* |
| **5 — Confidence** | §4.3 + `meta.watchdog_crosscheck` | *Is this audit trustworthy?* |

**Cross-day (optional):** **§4.7** *drift* — *Is the system getting worse over time?*

### 1.1 Predictive narrative (reactive → predictive)

**Before (reactive):** mismatch → stuck → fail-closed.  
**After (predictive):** `STATE_CONSISTENCY_GATE_*` engaged → adoption strain (`GATE_ANOMALY` / non-convergence) → **slow** mismatch→recovery latency → *(optional)* `PASS_SUMMARY` hash loop → explicit mismatch → stuck → fail.

---

## 2. Data sources and extraction plan

### 2.1 Robot logs (authoritative for engine/IEA/reconciliation/protective)

| Location | Contents |
|----------|----------|
| `<log_dir>/robot_ENGINE.jsonl` | `stream == "__engine__"` traffic: `ENGINE_CPU_PROFILE`, `RECONCILIATION_*`, connectivity, timers, summaries |
| `<log_dir>/robot_<INSTRUMENT>.jsonl` | Per-instrument execution, adoption, protective, IEA queue events |
| `<log_dir>/archive/*` | Rotated `robot_ENGINE_*.jsonl`, `robot_<INST>_*.jsonl` — **must** be scanned for the audit day |
| Optional: `robot_logging_errors.txt` | Pipeline failures (meta: “we lost visibility”) |

**Resolving `log_dir`** (same as `tools/log_audit.py`): `--log-dir` → `QTSW2_LOG_DIR` → `QTSW2_PROJECT_ROOT/logs/robot` → `./logs/robot`.

**File discovery for a calendar day D (timezone-aware, e.g. Chicago):**

1. List all `robot_ENGINE.jsonl`, `robot_ENGINE_*.jsonl`, and `archive/robot_ENGINE*.jsonl` (and per-instrument files).
2. Include a file if **any** parsed event has `ts_utc` falling on D in the chosen timezone **or** the file’s mtime overlaps D (fallback when `ts_utc` missing — rare).
3. Sort files deterministically: `robot_ENGINE.jsonl` first, then alphabetical by path.

### 2.2 Watchdog logs (connectivity lens + feed copy)

Typical locations (config-dependent):

| File | Role |
|------|------|
| `logs/robot/frontend_feed.jsonl` | Mirrored/forwarded robot events the UI cares about (if enabled) |
| `logs/robot/incidents.jsonl` | Persisted incidents (if present) |

Use Watchdog for: disconnect narratives the operator saw, duplicate detection vs robot source, and gaps if robot files were unavailable. **Primary metrics still come from robot JSONL** when both exist; Watchdog is used to **reconcile** or **annotate**, not double-count.

**Source fallback (critical):**

- If robot logs are **missing**, **empty for the window**, or **incomplete** (e.g. no `ENGINE_START` / no ENGINE lines when the session should have run, or parse failure rate above a configured ceiling), **Watchdog becomes the authoritative event stream** for whatever it captured (`frontend_feed.jsonl`, `incidents.jsonl`).
- Set `meta.source_primary: "watchdog"` and `meta.source_fallback: true` (or `meta.source_fallback_reason: "<short string>"`).
- When robot is healthy, `meta.source_primary: "robot"`, `meta.source_fallback: false`.
- **Do not double-count** when merging: same event in both feeds → one deduped row, prefer robot timestamp/payload.

### 2.3 Ingestion algorithm (single pass per file)

1. Open each file in sorted order; read line by line.
2. Parse JSON; skip empty/malformed lines; record `parse_errors_count` in audit meta.
3. Normalize timestamp: `ts_utc` (ISO) preferred; accept legacy `ts` / `timestamp` / ms number (see `tools/log_audit.py::normalize_event`).
4. Filter to audit window **[day_start, day_end)** in the chosen TZ.
5. **Deduplicate** (see §9) before incrementing metrics.
6. Route event to domain classifiers by `event` string (and sometimes `data`).

---

## 3. Event → metric mapping (registry-aligned)

The robot’s canonical names live in `RobotEventTypes` / emitted `event` field. Your spec names map as follows.

### 3.0 Integrated predictive signals (Tier 1–2 only)

**Scope:** Integrate **only** the signals in this table into the **default** daily audit. **Do not** add Tier 3 (bootstrap narrative, bar/timetable, deferred execution ratio, canonical parity) to the daily summary or default timeline — reserve for deep-dive tools or optional `--verbose` output.

| Tier | Signal | Role |
|------|--------|------|
| **1** | **STATE_CONSISTENCY_GATE_LIFECYCLE** | True start of failure path; **Safety**, **Timeline**, **Incident chains** |
| **1** | **ADOPTION_NON_CONVERGENCE_ESCALATED**, **IEA_ADOPTION_SCAN_GATE_ANOMALY** (and related `IEA_ADOPTION_SCAN_*` anomalies) | Predicts execution failure before queue explodes; **Execution**, **Timeline**, **Incident chains** |
| **1** | **MISMATCH → RECOVERY LATENCY** | Quality metric; **`recovery_quality`** + **Reconciliation** domain metrics |
| **1** | **ENGINE_TICK_STALL_*** | Earliest system-wide stall signal; **Performance**, **Timeline**, **Incident chains** |
| **1** | **WATCHDOG CROSS-CHECK** | **`confidence`** + **`meta.watchdog_crosscheck`** |
| **2** | **PASS_SUMMARY_HASH_STABILITY** | Silent reconciliation loop; **§4.6** only **when triggered** (never emit noise on clean days) |
| **2** | **REGISTRY_BROKER_DIVERGENCE** (and `STALE_QTSW2_ORDER_DETECTED` if present) | Root cause of many failures; **Safety**, **Incident chains** |
| **2** | **EXECUTION_JOURNAL_INDEX_HEALTH** (`EXECUTION_JOURNAL_ADOPTION_INDEX_*`, `JOURNAL_INSTRUMENT_KEY_MISMATCH`) | Structural integrity; **Execution** domain **only if any event fires** |

**Noise control:** If a Tier 2 execution-journal signal is absent for the day, **omit** the metric from JSON (or `null`) — do not print “0 issues” for every instrument.

### A. SYSTEM SAFETY

| Track (spec) | Canonical `event` values (examples) | Metrics extracted from `data` |
|----------------|-------------------------------------|-------------------------------|
| RECONCILIATION_MISMATCH | `RECONCILIATION_MISMATCH_DETECTED`, `RECONCILIATION_MISMATCH_PERSISTENT`, `RECONCILIATION_MISMATCH_METRICS`, `RECONCILIATION_QTY_MISMATCH`, `RECONCILIATION_ASSEMBLE_MISMATCH_DIAG` | Count by subtype; “unresolved” from absence of `RECONCILIATION_MISMATCH_CLEARED` / `RECONCILIATION_RESOLVED` pairing per mismatch key (use `instrument`, `stream`, `intent_id`, `mismatch_id` from `data` when present) |
| RECONCILIATION_STUCK | `RECONCILIATION_STUCK` | `stuck_count`; duration if `data` has elapsed/pass info |
| RECONCILIATION_OSCILLATION | `RECONCILIATION_OSCILLATION` | `oscillation_count` |
| FAIL_CLOSED | `DISCONNECT_FAIL_CLOSED_ENTERED`, `RECONCILIATION_MISMATCH_FAIL_CLOSED`, `KILL_SWITCH_ERROR_FAIL_CLOSED`, `POSITION_FLATTEN_FAIL_CLOSED` | `fail_closed_count` by type |
| FORCED_FLATTEN | `FORCED_FLATTEN_*` family | Count; distinguish `FORCED_FLATTEN_FAILED` / exposure remaining as CRITICAL |
| UNOWNED_LIVE_ORDER_DETECTED | `UNOWNED_LIVE_ORDER_DETECTED`, `ADOPTION_GRACE_EXPIRED_UNOWNED` | Count |
| MANUAL_OR_EXTERNAL_ORDER_DETECTED | `MANUAL_OR_EXTERNAL_ORDER_DETECTED` | Count |
| **Tier 1 — Gate lifecycle** | `STATE_CONSISTENCY_GATE_ENGAGED`, `STATE_CONSISTENCY_GATE_PERSISTENT_MISMATCH`, `STATE_CONSISTENCY_GATE_RECONCILIATION_STARTED`, `STATE_CONSISTENCY_GATE_RECONCILIATION_RESULT`, `STATE_CONSISTENCY_GATE_RELEASED`, `STATE_CONSISTENCY_GATE_RESTABILIZATION_RESET`, `STATE_CONSISTENCY_GATE_RECOVERY_FAILED`, … | Count transitions per instrument; **first `GATE_ENGAGED`** is the predictive start of failure (§3.0). |
| **Tier 2 — Registry** | `REGISTRY_BROKER_DIVERGENCE`, `STALE_QTSW2_ORDER_DETECTED` | Count; CRITICAL path — must appear in **Safety** + **incident chain** triggers |

**Derived safety metrics:**

- `total_mismatch_signals` — union of mismatch-class events after dedupe.
- `resolved_vs_unresolved` — from explicit `RECONCILIATION_MISMATCH_CLEARED` / `RECONCILIATION_RESOLVED` vs open markers (end-of-window state).
- `max_time_to_converge_ms` — from `RECONCILIATION_PASS_SUMMARY` or mismatch metric lines that carry `time_to_converge_ms` / pass timing (if absent, report `null` and do not guess).

**Safety Status:**

- **CRITICAL** if: any fail-closed line; any `RECONCILIATION_MISMATCH_PERSISTENT` / `RECONCILIATION_MISMATCH_FAIL_CLOSED`; `UNOWNED_*` / `MANUAL_OR_EXTERNAL_ORDER_DETECTED` > 0; `FORCED_FLATTEN_FAILED` or `FORCED_FLATTEN_EXPOSURE_REMAINING`.
- **CRITICAL (mismatch at EOD — context-aware, not raw state):** escalate to CRITICAL only when **all** of the following hold:
  - a mismatch is **still unresolved** at end of window (no clear / converge for that key), **and**
  - the system was **connected** (not in a sustained disconnect / intentional stand-down — infer from `CONNECTION_*` / `ENGINE_STAND_DOWN` / `DISCONNECT_*` as configured), **and**
  - there was **no convergence attempt** within **X minutes** before EOD (define **X** in thresholds: e.g. no `RECONCILIATION_PASS_SUMMARY`, `RECONCILIATION_CONVERGED`, or `RECONCILIATION_MISMATCH_CLEARED` / `RECONCILIATION_RESOLVED` for that instrument in the trailing window).
- **Do not** CRITICAL solely because the last line of the day “looks open” when the market closed mid-recovery, the robot was paused, or the final event was not yet flushed to disk — those cases are **WARNING** or **OK** with `note` explaining ambiguity.
- **WARNING** if: high `RECONCILIATION_MISMATCH_DETECTED` rate, oscillation > 0, stuck > 0 but recovered, ambiguous EOD state, or qty mismatch cleared same day.
- **OK** otherwise.

---

### B. CONNECTIVITY & RECOVERY

| Event | Notes |
|-------|--------|
| `CONNECTION_LOST`, `CONNECTION_LOST_SUSTAINED` | Start downtime interval |
| `CONNECTION_RECOVERED`, `CONNECTION_RECOVERED_NOTIFICATION` | End interval (pair nearest) |
| `DISCONNECT_FAIL_CLOSED_ENTERED` | Fail-closed trigger (also Safety) |
| `DISCONNECT_RECOVERY_COMPLETE` | Recovery success |

**Metrics:**

- `total_disconnects` — count `CONNECTION_LOST` (dedupe paired “flap” within 1s if needed — optional policy).
- `downtime_seconds` — sum of paired (recovered − lost) intervals; unpaired lost at EOD → cap to end-of-window or mark “open”.
- `avg_downtime` / `max_downtime` — from intervals.
- `fail_closed_triggers` — count `DISCONNECT_FAIL_CLOSED_ENTERED`.

**Connectivity Status:**

- **UNSTABLE** if: `max_downtime` > threshold OR `total_disconnects` ≥ N OR any fail-closed from disconnect path.
- **STABLE** otherwise.

---

### C. EXECUTION ENGINE (IEA)

| Event | Metrics |
|-------|---------|
| `IEA_QUEUE_PRESSURE`, `IEA_QUEUE_PRESSURE_DIAG` | `queue_depth` avg/max; `current_work_item_age_ms` / wait as **max wait** |
| `IEA_EXPENSIVE_PATH_THREAD` | Count + duration if present |
| `IEA_ENQUEUE_AND_WAIT_TIMING` | Latency samples (avg/max wait ms) |
| `ADOPTION_SCAN_START`, `ADOPTION_SCAN_SUMMARY`, `IEA_ADOPTION_SCAN_*` | `total_adoption_scans`; `scan_wall_ms` from `ADOPTION_SCAN_SUMMARY` (avg/max) |
| **Tier 1 — Adoption strain** | `ADOPTION_NON_CONVERGENCE_ESCALATED` (**CRITICAL**), `IEA_ADOPTION_SCAN_GATE_ANOMALY` (**WARN**), `IEA_ADOPTION_SCAN_SKIPPED_NO_PROGRESS` | Count; **DEGRADED** if any **Tier 1** anomaly > 0 — predicts backlog **before** queue depth spikes |
| **Tier 2 — Journal index (only if fired)** | `EXECUTION_JOURNAL_ADOPTION_INDEX_FALLBACK`, `EXECUTION_JOURNAL_ADOPTION_INDEX_REBUILD_FAILED`, `JOURNAL_INSTRUMENT_KEY_MISMATCH` | Emit `execution_journal_index_issues: <count>` **only** when > 0; else omit |

**Execution Status:**

- **BACKLOGGED** if: max queue wait > T_wait (e.g. 5s sustained policy) OR `IEA_ENQUEUE_AND_WAIT_TIMEOUT` / `QUEUE_OVERFLOW` > 0.
- **DEGRADED** if: max wait > T_warn OR expensive-path count high OR adoption scan max ms > T_scan **OR any Tier 1 adoption-strain event (§3.0)**.
- **HEALTHY** otherwise.

---

### D. RECONCILIATION & STATE

| Event | Role |
|-------|------|
| `RECONCILIATION_PASS_SUMMARY` | Pass count, convergence hints |
| `RECONCILIATION_STUCK`, `RECONCILIATION_OSCILLATION` | Instability |
| `RECONCILIATION_CONVERGED` | Positive signal |
| `RECONCILIATION_MISMATCH_METRICS` | Volume / diagnostics |

**Derived:**

- `total_passes` — count `RECONCILIATION_PASS_SUMMARY`.
- `convergence_rate_pct` — `RECONCILIATION_CONVERGED` / passes (if fields align; else approximate from summaries).
- `stuck_pct` / `oscillation_pct` — stuck_events / passes, oscillation_events / passes (document formula; use `null` if denominator zero).
- **Tier 1 — Mismatch → recovery latency** — per instrument (or global max): `ts(STATE_CONSISTENCY_GATE_RECONCILIATION_STARTED or first recovery attempt)` − `ts(RECONCILIATION_MISMATCH_DETECTED or GATE_ENGAGED)` in ms; roll up **`median_ms`**, **`max_ms`** into **`recovery_quality`** (§4.5). This is a **quality** metric, not binary state.

**Reconciliation Status:**

- **UNSTABLE** if: stuck or oscillation present at non-trivial rate **or** convergence_rate below floor.
- **STABLE** otherwise.

---

### E. PROTECTIVE SYSTEM

| Event | Role |
|-------|------|
| `PROTECTIVE_AUDIT_METRICS` | Prefer this over generic “CONTEXT” (registry has `PROTECTIVE_AUDIT_METRICS`, `PROTECTIVE_AUDIT_OK`, `PROTECTIVE_AUDIT_FAILED`) |
| `PROTECTIVE_MISSING_STOP`, `PROTECTIVE_STOP_QTY_MISMATCH`, … | Issues detected |
| `PROTECTIVE_RECOVERY_*`, `PROTECTIVE_ORDERS_SUBMITTED` | Corrections |

**Metrics:** `total_audits` (from audit metrics + OK/FAILED); `issues_detected`; `issues_resolved` (recovery confirmed + orders submitted that close issues — use explicit events only).

**Protection Status:**

- **ISSUES DETECTED** if: any CRITICAL protective event or `PROTECTIVE_AUDIT_FAILED` > 0.
- **OK** otherwise.

---

### F. SYSTEM LOAD & PERFORMANCE

**Source:** `ENGINE_CPU_PROFILE` in `robot_ENGINE*.jsonl`.

**Tier 1 — Engine tick stall (early warning):** `ENGINE_TICK_STALL_DETECTED`, `ENGINE_TICK_STALL_RUNTIME`, `ENGINE_TICK_STALL_RECOVERED`, `ENGINE_TICK_STALL_STARTUP` — count stalls and max stall duration; **CRITICAL PERIOD** candidate (§5.5); **incident chain** trigger (§6.1). Distinct from normalized CPU load: a stall can precede profile degradation.

From `data` (see `docs/robot/ENGINE_CPU_PROFILE.md`):

| Field | Use |
|-------|-----|
| `lock_sum_ms` or slice fields | Attribute time to subsystems |
| Slices: `reconciliation_runner_ms`, `stream_tick_ms`, `tail_coordinators_ms`, `onbar_avg_lock_ms`, … | % of `lock_sum_ms` when sum > 0 |

**Metrics:**

- **Dominant subsystem** — largest mean share across profile lines (deterministic tie-break: alphabetical by name).
- **% time per subsystem** — mean of per-line percentages (not sum of raw ms across lines with different tick shapes — document chosen method).
- **Parallelism factor (raw)** — if not in log, **derive proxy**: `lock_sum_ms / wall_window_ms` when both present; else use explicit field if later added.
- **Parallelism normalized (machine-independent)** — `parallelism_normalized = parallelism_raw / max(1, logical_processor_count)` where `logical_processor_count` comes from the audit runtime (`os.cpu_count()` or `PSUTIL` on the host running the audit, **or** a fixed override in config for CI). Emit both `parallelism_raw` and `parallelism_normalized` in JSON.
- **Slowest stream** — if `ENGINE_CPU_PROFILE` or stream summaries include per-stream ms; else from highest `stream_tick_ms` attribution only.

**Performance Status (use normalized parallelism for thresholds):**

- **CRITICAL LOAD** — `parallelism_normalized` **> 0.7** **sustained** (e.g. ≥ **K** consecutive profile samples or median over a **T**-minute window — see §8) **or** reconciliation + tail + onbar consistently high **with** execution backlog.
- **HIGH LOAD** — `parallelism_normalized` **> 0.5** without safety-critical issues.
- **NORMAL** — otherwise.

**Deprecated:** do not use a fixed raw parallelism such as “≥ 5.0” — that is machine-dependent.

*(Thresholds: `configs/robot/daily_audit_thresholds.json`.)*

---

### G. SUPERVISORY / STATE MACHINE

| Event | Metrics |
|-------|---------|
| `SUPERVISORY_STATE_TRANSITION_INVALID` | Count; CRITICAL |
| `SUPERVISORY_TRIGGERED`, `SUPERVISORY_ESCALATED` | Escalation count |
| `SUPERVISORY_INSTABILITY_DETECTED` | Supporting signal |

**Supervisory Status:**

- **UNSTABLE** if: any invalid transition > 0 OR escalations/min > threshold.
- **STABLE** otherwise.

---

## 4. Summary generation logic

### 4.1 Overall status (rollup)

**CRITICAL** if **any** domain is CRITICAL **or** Safety CRITICAL **or** Execution BACKLOGGED with timeout events.

**WARNING** if no CRITICAL but any domain WARNING/DEGRADED/UNSTABLE/ISSUES DETECTED/HIGH LOAD.

**OK** only if all domains green within thresholds.

Deterministic ordering: evaluate domains in fixed order A→G; set **`overall_status`** to worst severity.

**Downstream (hard requirement):** do **not** stop at `overall_status`. Always compute **`confidence_enforced_status`** (§4.3.1) and **`trade_readiness`** (§4.8). The human report’s **decision line** uses **`trade_readiness`**, not raw `overall_status` alone.

### 4.2 Trade count

**Total trades** — from execution fills or strategy-level “trade complete” events in instrument logs (define one canonical source, e.g. fills with `execution_sequence` in `data`). If not available, show `null` and exclude from rollup logic.

### 4.3 Confidence level (CRITICAL)

Emit top-level:

```json
"confidence": "HIGH | MEDIUM | LOW"
```

**Purpose:** Separates “the conclusion is alarming” from “we barely had telemetry.” **Do not** treat `overall_status` alone as decision-grade when **`confidence`** is LOW.

| Level | Logic (all conditions are cumulative hints; implement as a scored checklist — see thresholds file) |
|-------|------------------------------------------------------------------------------------------------------|
| **HIGH** | Robot logs **complete** for the window (expected files present, **low** `parse_errors_count` / `parse_error_rate` below ceiling), **engine active** (`ENGINE_START` or sustained `ENGINE_TIMER_HEARTBEAT` / tick activity as configured), **no** watchdog fallback, **consistent** cross-logs (instruments present match config expectations), **no** missing-domain gap for critical domains, **and** **Tier 1 watchdog cross-check** passes: `meta.watchdog_crosscheck.divergence == false` (or absent when watchdog data unavailable). |
| **MEDIUM** | **Minor gaps** (e.g. partial archive, small parse-rate elevation, **partial** watchdog merge, one domain thin but not empty) **or** small robot↔watchdog count drift (documented in `watchdog_crosscheck`). |
| **LOW** | **Watchdog fallback** as primary (`source_primary: watchdog`), **high** parse errors, **missing key domains**, **inconsistent** or **empty** robot corpus, **or** **large divergence** between robot JSONL and `data/watchdog/incidents.jsonl` / `reliability_metrics` for the same window. |

**`meta.watchdog_crosscheck` (Tier 1 — when both sources exist):**

```json
"watchdog_crosscheck": {
  "available": true,
  "pairs_compared": ["CONNECTION_LOST", "ENGINE_STALLED", "RECONCILIATION_QTY_MISMATCH"],
  "max_count_delta": 2,
  "divergence": false
}
```

**Rule:** If **LOW**, prepend or highlight in human output: *“Low confidence — verify raw logs before acting.”* Optionally cap displayed `overall_status` severity or add `summary.confidence_note` (implementation choice; document in tool).

### 4.3.1 Confidence-enforced status (`confidence_enforced_status`)

Apply **after** `overall_status` and **`confidence`** are known. Emits **`OK` | `WARNING` | `CRITICAL`** (same enum as `overall_status`).

| `confidence` | `overall_status` | `confidence_enforced_status` |
|--------------|-------------------|------------------------------|
| **HIGH** | OK / WARNING / CRITICAL | **Unchanged** (same as `overall_status`) |
| **MEDIUM** | OK | **WARNING** |
| **MEDIUM** | WARNING / CRITICAL | **Unchanged** |
| **LOW** | OK | **WARNING** |
| **LOW** | WARNING | **CRITICAL** |
| **LOW** | CRITICAL | **CRITICAL** |

Emit top-level: `"confidence_enforced_status": "OK | WARNING | CRITICAL"`.

**Display:** In **[SUMMARY]**, show **prominently**: raw `OVERALL STATUS` **and** **`EFFECTIVE STATUS` = `confidence_enforced_status`** — operators treat the **effective** line as the severity floor for “all clear” semantics.

### 4.4 First failure point (triage anchor)

Add to **`summary`**:

```json
"first_failure": {
  "ts_chicago": "HH:mm:ss",
  "ts_utc": "<ISO8601>",
  "event": "<event_type>",
  "instrument": "<string or empty>",
  "chain_id": 2
}
```

**Definition (deterministic):**

1. Consider only events that are **adverse** for the day: `overall_status` is not OK **or** at least one incident chain has `classification` ∈ {`DEGRADED`, `FAILED`} **or** at least one domain status is not green.
2. If none of the above → **`first_failure: null`** (clean day).
3. Build the **candidate set** of adverse events (from timeline selection §5 + instrument logs), each with `event`, `ts_utc`, `instrument`.
4. **Type precedence (hard — tie-break and filtering):** assign **rank** `R` to each candidate by `event` (first match wins):

| `R` | `event` (exact or prefix rule in tool) |
|-----|----------------------------------------|
| **1** | `ENGINE_TICK_STALL_DETECTED` |
| **2** | `STATE_CONSISTENCY_GATE_ENGAGED` |
| **3** | `REGISTRY_BROKER_DIVERGENCE` |
| **4** | `ADOPTION_NON_CONVERGENCE_ESCALATED` |
| **5** | `RECONCILIATION_MISMATCH_DETECTED` |

   Events not in this table use **`R = 99`** (lower priority than 1–5).

5. **Selection:** sort candidates by **`(ts_utc` ascending, `R` ascending)** — **earliest time first**; if **equal `ts_utc`**, lower **`R`** wins.
6. **`first_failure`** = first row of that sorted list (or `null` if candidate set empty).
7. **`chain_id`:** ID of the incident chain containing that event, or `null` if not in a chain.

**Why:** Fast triage — *where did the day start going wrong?* Stalls and gates surface before generic mismatch when times tie or cluster.

### 4.5 Recovery quality score

From **incident chains** (§6) + connectivity recovery intervals, aggregate:

```json
"recovery_quality": {
  "avg_recovery_time_ms": 120000,
  "max_recovery_time_ms": 300000,
  "mismatch_to_recovery_median_ms": 4500,
  "mismatch_to_recovery_max_ms": 120000,
  "successful_recoveries": 4,
  "failed_recoveries": 1,
  "chains_classified_normal": 3,
  "chains_classified_degraded": 1,
  "chains_classified_failed": 1
}
```

**Definitions:**

- **Successful recovery** — chain ends with stabilizer (§6.3) and `classification: NORMAL` **or** disconnect interval with `CONNECTION_RECOVERED` and no fail-closed in same episode.
- **Failed recovery** — `classification: FAILED` **or** open disconnect at EOD **or** unresolved mismatch per §3.A rules.
- **`avg_recovery_time_ms` / `max_recovery_time_ms`** — mean and max of **`resolution_time_ms`** over chains that had a finite resolution (exclude `null` from mean; document `max` over same set).
- **`mismatch_to_recovery_median_ms` / `mismatch_to_recovery_max_ms` (Tier 1)** — from §3.D: time from **mismatch** detection (`RECONCILIATION_MISMATCH_DETECTED` or `STATE_CONSISTENCY_GATE_ENGAGED`) to **first recovery attempt** (`STATE_CONSISTENCY_GATE_RECONCILIATION_STARTED` or equivalent). **Null** if no pairs.

**Why:** Two days can both show **OK** while one recovers in **2s** and another in **5 minutes** — this field makes that visible.

### 4.6 Silent failure detection (audit-generated)

**Problem:** No mismatch, no fail-closed — but the system **quietly stops working**.

The audit **does not** emit a robot `RobotLogEvent` named `SILENT_FAILURE_DETECTED` unless the engine adds one later; the **daily report** emits a **`silent_failure`** block (or `signals` entry) when **heuristic checks** fire:

| Check | Example condition |
|-------|-------------------|
| **Expected activity missing** | Session is configured for trading (timetable / `TRADING_DATE_LOCKED` / `ENGINE_START`) but **no** `BAR_ACCEPTED` / stream tick indicators for **T_silent** minutes for an active instrument. |
| **Streams “active” but no progress** | Heartbeats or stream registration without **fills** (`ENTRY_FILLED` / execution fills) or **lifecycle transitions** when strategy expected trades — **policy-defined** per instrument. |
| **ENGINE_CPU_PROFILE gap** | Profiling enabled (`engine_cpu_profile.enabled` or presence of recent profiles) but **no** `ENGINE_CPU_PROFILE` for **T_profile_gap** minutes while market is open and engine should tick. |
| **`ENGINE_TIMER_HEARTBEAT` gap** | When the engine is expected to emit **`ENGINE_TIMER_HEARTBEAT`** (timer path per `RobotEventTypes` / `LOGGING.md`) but **no** such line for **≥ T_heartbeat** minutes while the process should still be running → **`silent_failure`** with code e.g. `engine_timer_heartbeat_gap`. **Does not** require market open — dead timer path = dead visibility. |
| **Tier 2 — Pass summary hash stability** | **Only when triggered:** same `RECONCILIATION_PASS_SUMMARY` **payload fingerprint** (hash of selected stable `data` fields) repeats **≥ N** times within **T_hash** minutes — indicates silent reconciliation loop **without** a dedicated `RECONCILIATION_OSCILLATION` line. **Do not** add to daily summary when not triggered. |

**Output:**

```json
"silent_failure": {
  "detected": true,
  "signals": [
    {
      "code": "SILENT_FAILURE_DETECTED",
      "reason": "no_engine_cpu_profile_for_18m",
      "severity": "WARNING",
      "instrument": "ES"
    }
  ]
}
```

If no checks fire: `"silent_failure": { "detected": false, "signals": [] }`.

**Interaction with overall status:** `silent_failure.detected` should typically force **WARNING** or **CRITICAL** (severity per policy) even when explicit error events are absent.

### 4.7 System drift indicator (multi-day)

**Requires:** Prior daily audit JSON files in `reports/daily_audit/` (or a configurable history dir), e.g. last **N = 14** business days.

**Compute rolling baselines** (median or trimmed mean — deterministic) for:

- mismatch rate (per day / per instrument aggregate)
- max queue wait (`max_queue_wait_ms`)
- max normalized CPU (`parallelism_normalized_max`)
- mean recovery time (`recovery_quality.avg_recovery_time_ms`)

**Emit:**

```json
"drift": {
  "window_days": 14,
  "mismatch_rate_trend": "UP | DOWN | STABLE",
  "cpu_trend": "UP | DOWN | STABLE",
  "recovery_time_trend": "UP | DOWN | STABLE",
  "queue_wait_trend": "UP | DOWN | STABLE",
  "notes": []
}
```

**Trend rules (example):** compare **today’s value** to baseline; **UP** if above **+k** σ or above **+p**% vs baseline (config). **STABLE** if within band. **DOWN** if improved.

If **no history** (first run): `"drift": null` or `drift.available: false`.

**Why:** “Today is fine” is not enough — you need *“the system is not slowly degrading.”*

### 4.7.1 Drift risk (`drift_risk`) — links to `trade_readiness`

Emit top-level (or inside `drift` if `null` history → **`NONE`**):

```json
"drift_risk": "NONE | ELEVATED | HIGH"
```

| Value | Rule (deterministic — use same thresholds as §4.7 trends) |
|-------|-----------------------------------------------------------|
| **NONE** | No drift object **or** all trends **STABLE** and within band. |
| **ELEVATED** | **One** metric **UP** vs baseline (or vs prior day), others stable. |
| **HIGH** | **Two or more** metrics **UP**, or **recovery_time_trend** **UP** + any other **UP**, or **queue_wait_trend** **UP** + **cpu_trend** **UP** (configurable disjunction — document in thresholds file). |

**Link to decision:** `drift_risk == HIGH` may downgrade **`trade_readiness.decision`** from **`GO`** → **`CAUTION`** only (§4.8). **Never** use drift alone to set **`NO_GO`** without a blocking factor from §4.8.

### 4.8 Trade readiness (hard requirement — top-level)

Emit **top-level** (same level as `overall_status`):

```json
"trade_readiness": {
  "decision": "GO | CAUTION | NO_GO",
  "reason": "<one short sentence, deterministic template>",
  "blocking_factors": ["<machine-readable codes>"]
}
```

**`decision` rules (strict — no other interpretation):**

1. Inputs: `overall_status`, `confidence`, **`confidence_enforced_status`** (§4.3.1), **`domains.safety.status`**, **`domains.execution.status`**, **`silent_failure.detected`**, **`drift_risk`** (§4.7.1).
2. **`NO_GO`** if **any** of:
   - `domains.safety.status` is **CRITICAL**, **or**
   - `domains.execution.status` is **BACKLOGGED** (timeouts / overflow), **or**
   - `silent_failure.detected == true`, **or**
   - `confidence_enforced_status` is **CRITICAL**, **or**
   - `overall_status` is **CRITICAL**.
3. **`CAUTION`** if not `NO_GO` and **any** of:
   - `confidence_enforced_status` is **WARNING**, **or**
   - `domains.execution.status` is **DEGRADED**, **or**
   - `overall_status` is **WARNING**, **or**
   - `drift_risk` is **HIGH** (optional: downgrade **`GO`→`CAUTION`** only), **or**
   - `drift_risk` is **ELEVATED** *(optional policy — default **do not** downgrade unless configured)*.
4. **`GO`** only if none of the above fire and `confidence_enforced_status` is **OK**.

**`blocking_factors`:** Codes such as `SAFETY_CRITICAL`, `EXECUTION_BACKLOGGED`, `SILENT_FAILURE`, `CONFIDENCE_ENFORCED_CRITICAL`, `OVERALL_CRITICAL`, `DRIFT_HIGH` (when used as CAUTION driver). Empty if **`GO`**.

**`reason`:** One sentence from the **highest-priority** blocking code, or “All gates clear” for **`GO`**.

---

## 5. Timeline Reconstruction Layer

**Objective:** A single **chronological**, **compressed** narrative of high-signal events for the trading day — supports root-cause reading and cross-links from domain summaries.

### 5.1 Event selection (high-signal only)

Include **only** events below (plus `data` for descriptions). All timestamps are interpreted in **America/Chicago** for display after sorting on **`ts_utc`**.

| Bucket | Events |
|--------|--------|
| **A. Connectivity** | `CONNECTION_LOST`, `CONNECTION_RECOVERED`, `DISCONNECT_FAIL_CLOSED_ENTERED`, `DISCONNECT_RECOVERY_COMPLETE` |
| **B. Safety / risk** | Any event whose `event` contains **`FAIL_CLOSED`** (e.g. `DISCONNECT_FAIL_CLOSED_ENTERED`, `RECONCILIATION_MISMATCH_FAIL_CLOSED`, `POSITION_FLATTEN_FAIL_CLOSED`, `KILL_SWITCH_ERROR_FAIL_CLOSED`); **`FORCED_FLATTEN_*`**; **`UNOWNED_LIVE_ORDER_DETECTED`** |
| **C. Reconciliation** | `RECONCILIATION_MISMATCH_DETECTED`, `RECONCILIATION_STUCK`, `RECONCILIATION_OSCILLATION`, `RECONCILIATION_CONVERGED` |
| **C2. Gate lifecycle (Tier 1)** | `STATE_CONSISTENCY_GATE_ENGAGED`, `STATE_CONSISTENCY_GATE_PERSISTENT_MISMATCH`, `STATE_CONSISTENCY_GATE_RECONCILIATION_STARTED`, `STATE_CONSISTENCY_GATE_RECONCILIATION_RESULT`, `STATE_CONSISTENCY_GATE_RELEASED`, `STATE_CONSISTENCY_GATE_RECOVERY_FAILED`, … — **always include** in timeline when present (compressed per §5.3) |
| **D. Execution (IEA)** | `IEA_ADOPTION_SCAN_EXECUTION_STARTED`, `IEA_ADOPTION_SCAN_EXECUTION_COMPLETED`, `IEA_QUEUE_PRESSURE` (**significant only** — see §5.4) |
| **D2. Adoption strain (Tier 1)** | `ADOPTION_NON_CONVERGENCE_ESCALATED`, `IEA_ADOPTION_SCAN_GATE_ANOMALY`, `IEA_ADOPTION_SCAN_SKIPPED_NO_PROGRESS` — **always include** when present |
| **E. Performance** | `ENGINE_CPU_PROFILE` (**only when normalized parallelism is above threshold** — align with §8), `HIGH_FREQUENCY_LOOP_DETECTED` |
| **E2. Engine tick stall (Tier 1)** | `ENGINE_TICK_STALL_DETECTED`, `ENGINE_TICK_STALL_RUNTIME`, `ENGINE_TICK_STALL_RECOVERED` — **always include** when present |
| **F. Trading (if present)** | `ENTRY_FILLED`, `EXIT_FILLED` **or** `LIFECYCLE_TRANSITIONED` / journal lines whose payload encodes those transitions; **`SESSION_FORCED_FLATTEN_TRIGGERED`**, **`SESSION_FORCED_FLATTEN_SUBMITTED`**, **`SESSION_FORCED_FLATTENED`** (registry uses this family — there is no single `SESSION_FORCED_FLATTEN` string) |

**Exclude:** heartbeats, bar spam, `RECONCILIATION_MISMATCH_METRICS` high-volume INFO lines unless needed for a specific critical period.

### 5.2 Timeline line format

1. Sort all selected events by **`ts_utc`** ascending (UTC), then deterministic tie-break: `event`, `instrument`, `run_id`, line hash.
2. Convert display time to **Chicago**: `[HH:mm:ss]` (24h, leading zero).
3. One line per logical entry:

```
[HH:mm:ss] EVENT_TYPE — short description (instrument / stream if relevant)
```

**Description rules:** Prefer `message`; else build from `data` (e.g. `intent_id`, `stream`, `current_work_item_age_ms`, `mismatch_id`). Omit empty parentheses.

### 5.3 Event compression (anti-spam)

**Collapse repetitive bursts** (same `event` + same `instrument` within window **W**, default **2 minutes**):

- Emit one line:  
  `[HH:mm:ss–HH:mm:ss] EVENT_TYPE (×N in 2m) — instrument`  
  Example: `[09:31:02–09:32:55] IEA_ADOPTION_SCAN_EXECUTION_COMPLETED (×12 in 2m) — MNQ`

**Do not collapse alternating patterns (causality preservation):**

- If the **sequence of event types** in chronological order **alternates** between two or more distinct types (e.g. `IEA_ADOPTION_SCAN_EXECUTION_COMPLETED` → `RECONCILIATION_MISMATCH_DETECTED` → `IEA_ADOPTION_SCAN_EXECUTION_COMPLETED` → …), **do not** merge into a single “×N” line — that hides **feedback loops**.
- **Rule:** collapse only when **consecutive** events share the **same** `(event, instrument[, stream])` (or same “collapse key”); if the collapse key **changes** from line to line, start a new group even within **W**.
- Repetitive **identical** events → collapse; **alternating** → preserve (each transition may be one compressed row or raw line per policy, but the alternation must remain visible).

**Collapse sustained states** (same condition, not necessarily identical log line):

- **09:30:00–09:45:12** — `RECONCILIATION_STUCK` (MNQ) — *duration 15m12s*  
  Merge consecutive stuck/oscillation samples for the same `(instrument[, stream])` when gaps between lines &lt; **G** (e.g. 30s) **and** the event type does not alternate with a different high-signal type in between (if it does, split the span).

**Do not collapse** connectivity pairings (`CONNECTION_LOST` / `CONNECTION_RECOVERED`) into a single line unless implementing **duration rows** (see §5.4).

Determinism: fixed **W**, **G**, and sort order for merged groups.

### 5.4 Duration tracking

Attach durations where the log supports it.

#### Pairing policy — disconnects (`CONNECTION_LOST` → recovery)

1. **Primary:** For each `CONNECTION_LOST`, find **`CONNECTION_RECOVERED`** (or `CONNECTION_RECOVERED_NOTIFICATION` / `CONNECTION_CONFIRMED` per watchdog policy) with **matching `run_id`** when both carry it.
2. **Fallback:** If `run_id` missing or no match, pair **same calendar audit day**, **`CONNECTION_RECOVERED` with minimum `ts_utc` strictly after** that `CONNECTION_LOST` (nearest forward in time).
3. **Window boundary:** **Never** pair a lost in the audit window to a recovered **before** `day_start` or **after** `day_end` except: mark **`open_ended`** if lost has **no** recovered by `day_end` (duration = to end of window or explicit “unclosed” in metrics — document one rule in the tool).
4. **No cross-midnight fantasy pairs:** do not link a lost on day **D** to the first recovered on **D+1** unless the audit is explicitly a multi-day run (default daily audit: **do not**).

#### Pairing policy — mismatch / gate episodes (open → close)

Closing events include: `RECONCILIATION_MISMATCH_CLEARED`, `RECONCILIATION_CONVERGED`, `STATE_CONSISTENCY_GATE_RELEASED`, `RECONCILIATION_RESOLVED`, and pass-summary lines that explicitly clear a keyed mismatch when present in `data`.

**Correlate open → close** using the **strongest** available key, in order:

1. **`mismatch_id`** (or equivalent) in `data` — if both sides have it, pair only when equal.
2. **`intent_id`** — pair when both sides reference the same intent.
3. **`(instrument, stream)`** — pair when both present and equal (normalize case).
4. If **no** valid closing event exists for an open by `day_end`, leave episode **`open`** (do not invent duration; `mismatch_to_recovery_*` excludes that episode or uses `null` for that key).

This keeps **recovery_quality** and mismatch latency metrics from drifting due to sloppy cross-instrument pairing.

| Phenomenon | How |
|------------|-----|
| **Disconnect** | Apply **Pairing policy — disconnects** above; interval = `recovered_ts − lost_ts`. |
| **Mismatch episode** | Apply **Pairing policy — mismatch** above; interval from first open to first matching close. |
| **Queue pressure** | Merge adjacent `IEA_QUEUE_PRESSURE` / `IEA_QUEUE_PRESSURE_DIAG` lines where **`current_work_item_age_ms` ≥ T_queue** or **depth ≥ D_queue**; emit span with max age in range. |

**Significant `IEA_QUEUE_PRESSURE`:** include a line only if `current_work_item_age_ms` (or equivalent) **≥ T_queue_warn** OR queue depth **≥ D_warn** (from `configs/robot/daily_audit_thresholds.json`).

**`ENGINE_CPU_PROFILE`:** include only when derived **normalized parallelism** exceeds the timeline floor (may be slightly below §8 WARNING threshold so the timeline is not empty).

### 5.5 Critical period highlights

Insert a banner **before** the first line of each critical window:

```
⚠ CRITICAL PERIOD — <short reason>
```

Mark **⚠ CRITICAL PERIOD** when **any** of:

- Any **fail-closed** class event in §5.1B  
- **`RECONCILIATION_STUCK`** or **`RECONCILIATION_OSCILLATION`** present in window  
- **`ENGINE_TICK_STALL_RUNTIME`** or sustained **`ENGINE_TICK_STALL_DETECTED`** (Tier 1) — system-wide early failure  
- **High CPU sustained:** ≥ **K** consecutive `ENGINE_CPU_PROFILE` rows (or ≥ **M**% of samples in a **T**-minute rolling window) with **normalized** parallelism above §8 **CRITICAL** floor

Close the period when the condition clears for a **quiet** interval **Q** (e.g. 5 minutes), or at session boundary.

Multiple non-overlapping periods → multiple banners.

### 5.6 Link from domain summaries to timeline

Each **[DETAILS]** subsection should end with an optional **timeline pointer** when non-empty:

- `→ See timeline <HH:mm>–<HH:mm> (<topic>)`  
  Example: `Reconciliation Status: WARNING → See timeline 09:42–09:55 (oscillation, MNQ)`

Implementation: while building the timeline, index **compressed spans** by `(domain, instrument)`; summary generator picks the **widest** or **most severe** span per domain.

### 5.7 Final daily report structure (human)

```
🔷 DAILY REPORT
DATE: YYYY-MM-DD
TIMEZONE: America/Chicago (display)

OVERALL STATUS: OK | WARNING | CRITICAL
EFFECTIVE STATUS (confidence-enforced): OK | WARNING | CRITICAL

TRADE READINESS: GO | CAUTION | NO_GO
Reason: <one line from trade_readiness.reason>

[SUMMARY]
Confidence: HIGH | MEDIUM | LOW
- Total trades: ...
- Total mismatches: ...
- Total disconnects: ...
- Max CPU parallelism (raw / normalized): ...
- Dominant subsystem: ...
- First failure: <HH:mm:ss Chicago — EVENT — instrument — chain #N> | none
- Recovery quality: chain avg/max <Xm>s/<Ym>s; mismatch→recovery median <Zm>s; success/fail: <a>/<b>
- Drift (if history): mismatch <UP|STABLE|DOWN>, CPU ..., recovery time ...; drift_risk: NONE | ELEVATED | HIGH
- Silent failure: none | SILENT_FAILURE_DETECTED (see details)
- Blocking factors (if any): <trade_readiness.blocking_factors>

[DETAILS]
[SAFETY] ... → See timeline ...
[CONNECTIVITY] ...
[EXECUTION] ...
[RECONCILIATION] ...
[PROTECTION] ...
[PERFORMANCE] ...
[SUPERVISORY] ...

[TIMELINE]
⚠ CRITICAL PERIOD — ...
[HH:mm:ss] ...
...

[INCIDENT CHAINS]
(see §6.6 — omit section if zero chains)
```

**Order (deterministic):** `[SUMMARY]` → `[DETAILS]` → `[TIMELINE]` → `[INCIDENT CHAINS]`.

### 5.8 Human report suppression (global editorial rule)

If a signal is **neither** status-changing (domain / `overall_status` / `confidence_enforced_status` / `trade_readiness`), **nor** timeline-worthy (§5.1 selection), **nor** incident-chain-worthy (§6), **nor** confidence-changing (`confidence` / `watchdog_crosscheck` / `silent_failure`), it **must not** appear in the **human** report body. It may still exist in **machine JSON** for forensics. This prevents implementation creep from flooding the daily narrative.

---

## 6. Incident chain builder (causal sequences)

**Objective:** Group high-signal timeline events into **incident chains** so the report answers *why*, not only *what*. This is **Layer 3** (see §1).

### 6.1 Start a chain when

Any of:

- `CONNECTION_LOST`
- `RECONCILIATION_MISMATCH_DETECTED`
- **`STATE_CONSISTENCY_GATE_ENGAGED`** (Tier 1 — preferred start when present before terminal mismatch)
- **`REGISTRY_BROKER_DIVERGENCE`** / **`STALE_QTSW2_ORDER_DETECTED`** (Tier 2)
- **`ADOPTION_NON_CONVERGENCE_ESCALATED`** / **`IEA_ADOPTION_SCAN_GATE_ANOMALY`** (Tier 1)
- **`ENGINE_TICK_STALL_DETECTED`** (Tier 1)
- Any `event` containing **`FAIL_CLOSED`**

### 6.2 Continue a chain while

- Subsequent selected events (from the same ingestion stream after dedupe) fall within a **chain window** **C** (default **5–10 minutes** from the last chain event — configurable), **or**
- the situation is still clearly the same episode (same `run_id` / `instrument` / mismatch key where applicable).

**Do not** merge two unrelated disconnects hours apart into one chain unless policy explicitly allows “same-day umbrella” (default: **no**).

### 6.3 End a chain when

Any of:

- **Stabilizers:** `RECONCILIATION_CONVERGED`, `DISCONNECT_RECOVERY_COMPLETE`, `CONNECTION_RECOVERED` (when closing a disconnect episode — pair sensibly), **`STATE_CONSISTENCY_GATE_RELEASED`** / **`RECONCILIATION_MISMATCH_CLEARED`** for gate episodes, or explicit mismatch clear events for that key.
- **Quiet:** no qualifying events for **quiet gap** **Q_chain** (e.g. same **Q** as timeline §5.5 or a dedicated value).
- **End of audit window.**

### 6.4 Classification (per chain)

| Value | Meaning |
|-------|--------|
| `NORMAL` | Trigger → recovery signals within **C**; no fail-closed; mismatch converged or cleared. |
| `DEGRADED` | Recovery completed but with stuck/oscillation/queue pressure inside the chain; or long duration. |
| `FAILED` | Fail-closed, unresolved mismatch at chain end, or no stabilizer before window end. |

**Classification precedence (deterministic — no ambiguity):** **`FAILED` > `DEGRADED` > `NORMAL`**.

1. If **any** condition in the **`FAILED`** row is true for this chain → **`classification = FAILED`**.
2. Else if **any** condition in the **`DEGRADED`** row is true → **`classification = DEGRADED`**.
3. Else → **`classification = NORMAL`**.

Evaluate conditions independently; **FAILED** always wins if multiple rows could apply.

Human labels may map to: **NORMAL RECOVERY**, **UNSTABLE SYSTEM**, etc., in the narrative block.

### 6.5 JSON: `incident_chains`

```json
"incident_chains": [
  {
    "id": 1,
    "start_utc": "...",
    "end_utc": "...",
    "trigger_event": "CONNECTION_LOST",
    "trigger_ts_utc": "...",
    "events": [
      { "ts_utc": "...", "event": "DISCONNECT_FAIL_CLOSED_ENTERED", "instrument": "" },
      { "ts_utc": "...", "event": "CONNECTION_RECOVERED", "instrument": "" }
    ],
    "resolution_time_ms": 300000,
    "classification": "NORMAL",
    "classification_detail": "NORMAL RECOVERY"
  }
]
```

- **`resolution_time_ms`:** `end_utc - start_utc` when a stabilizer ended the chain; `null` if **FAILED** / open.
- **`classification`:** `NORMAL` | `DEGRADED` | `FAILED` (machine); **`classification_detail`** optional string for desk language.

### 6.6 Human block: `[INCIDENT CHAINS]`

```
[INCIDENT CHAINS]

INCIDENT #1
Trigger: 09:31 — CONNECTION_LOST
Sequence:
  09:32 — DISCONNECT_FAIL_CLOSED_ENTERED
  09:33 — CONNECTION_RECOVERED
  ...
Resolution time: 5 minutes
Classification: NORMAL RECOVERY

INCIDENT #2
Trigger: 09:42 — RECONCILIATION_MISMATCH_DETECTED
Sequence:
  09:43 — RECONCILIATION_STUCK
  09:44 — RECONCILIATION_OSCILLATION
  09:45 — IEA_QUEUE_PRESSURE (significant)
Resolution: NOT RESOLVED
Classification: UNSTABLE SYSTEM
```

Domain summaries may reference chains: e.g. `Reconciliation Status: WARNING → See INCIDENT #2`.

---

## 7. Daily output formats

### 7.1 Human-readable (fixed template)

Same as §5.7, plus **[INCIDENT CHAINS]** after **[TIMELINE]** (or before it — pick one order and keep it **deterministic**; recommended: **SUMMARY → DETAILS → TIMELINE → INCIDENT CHAINS**). Apply **§5.8** so the human body never includes non-material noise.

**[TIMELINE]** is required when any selected event exists, else `[TIMELINE]` with `— no high-signal events in window —`. Omit **[INCIDENT CHAINS]** if zero chains.

### 7.2 JSON (machine-readable)

Top-level keys (stable, sorted):

- `schema_version`, `audit_date`, `timezone`, `generated_at_utc`
- **`confidence`**: `"HIGH"` \| `"MEDIUM"` \| `"LOW"` (§4.3)
- **`confidence_enforced_status`**: `"OK"` \| `"WARNING"` \| `"CRITICAL"` (§4.3.1)
- **`trade_readiness`**: `{ "decision", "reason", "blocking_factors" }` (§4.8 — **hard requirement**)
- `meta`: `files_read`, `lines_read`, `events_used`, `events_deduped`, `parse_errors_count`, **`parse_error_rate`** (optional), `timeline_raw_events`, `timeline_compressed_lines`, **`source_primary`** (`"robot"` \| `"watchdog"`), **`source_fallback`** (bool), **`source_fallback_reason`** (optional string), **`logical_processor_count`**, **`parallelism_normalized_max`**, **`engine_active`** (bool), **`domains_complete`** (bool or per-domain map), **`watchdog_crosscheck`** (§4.3)
- `overall_status`, `summary` (include **`parallelism_raw_max`**, **`parallelism_normalized_max`**, **`first_failure`** (§4.4), **`recovery_quality`** (§4.5), optional **`confidence_note`**)
- **`silent_failure`**: §4.6
- **`drift`**: §4.7 or `null`
- **`drift_risk`**: `"NONE"` \| `"ELEVATED"` \| `"HIGH"` (§4.7.1)
- `domains` — object with keys `safety`, `connectivity`, `execution`, `reconciliation`, `protection`, `performance`, `supervisory`
- Each domain: `status`, `metrics`, `highlights`, `critical_findings`, **`timeline_refs`**, optional **`incident_chain_ids`**: array of integers linking to `incident_chains[].id`
- **`timeline`**: `{ "compressed_entries": [...], "critical_periods": [...] }` (same shape as before)
- **`incident_chains`**: array (§6.5)

---

## 8. Alert rules (initial thresholds)

| Level | Rule |
|-------|------|
| **CRITICAL** | Context-aware **unresolved mismatch** (§3.A); any `RECONCILIATION_STUCK` without subsequent convergence **when** connectivity and recovery context demand it (same session rules as mismatch); any fail-closed event; `IEA_ENQUEUE_AND_WAIT_TIMEOUT` or `QUEUE_OVERFLOW`; **`parallelism_normalized` > 0.7** sustained (see §3.F / §5.5); any `SUPERVISORY_STATE_TRANSITION_INVALID` |
| **WARNING** | Mismatch count > **M_warn** per instrument/day; `RECONCILIATION_OSCILLATION` > 0; `total_disconnects` ≥ **3** OR `max_downtime` > **T_warn**; **`parallelism_normalized` > 0.5**; queue max wait > **T_q_warn**; ambiguous EOD mismatch (§3.A) |
| **OK** | No major anomalies per above |

**CPU:** use **normalized** parallelism only for alert levels; record raw alongside for forensics.

**Silent failure (§4.6):** if `silent_failure.detected`, elevate to at least **WARNING** (or **CRITICAL** when checks indicate hard stall) even if explicit mismatch/fail-closed events are absent.

Store thresholds in `configs/robot/daily_audit_thresholds.json` (future); hardcode defaults in tool until file exists.

---

## 9. Deduplication and determinism

**No double-counting across:**

- Rotated files (same event written once — but if replayed, dedupe).
- Robot vs Watchdog: **prefer robot** when complete; **Watchdog authoritative** when robot is missing/incomplete (§2.2); dedupe shared events once.

**Dedupe key (in order of preference):**

1. `data.event_id` or `data.idempotency_key` if present.
2. `data.execution_sequence` + `instrument` + `run_id` for execution-scoped events.
3. Composite: `(run_id, ts_utc, event, instrument, stable_json_hash(data_subset))` where `data_subset` excludes volatile fields (`wall_clock`, debug counters).

**Determinism:** sort deduped keys before hashing; round floats to 4 decimals in output JSON.

---

## 10. Missing fields

- If a metric’s inputs are absent → emit `null`, never infer from unrelated events.
- If entire domain has no source events → status **OK** with `note: "no_signals_in_window"` only if ENGINE was running (detect `ENGINE_START` / heartbeats); else **WARNING** “insufficient telemetry”.

---

## 11. Example JSON output (illustrative)

```json
{
  "audit_date": "2026-03-24",
  "generated_at_utc": "2026-03-25T06:00:00Z",
  "confidence": "MEDIUM",
  "confidence_enforced_status": "WARNING",
  "trade_readiness": {
    "decision": "CAUTION",
    "reason": "Execution is degraded and effective severity is WARNING (medium confidence; enforced status matches rollup).",
    "blocking_factors": ["EXECUTION_DEGRADED", "OVERALL_WARNING"]
  },
  "drift_risk": "NONE",
  "meta": {
    "files_read": 14,
    "events_used": 128904,
    "events_deduped": 42,
    "parse_errors_count": 0,
    "parse_error_rate": 0.0,
    "timeline_raw_events": 412,
    "timeline_compressed_lines": 38,
    "source_primary": "robot",
    "source_fallback": false,
    "logical_processor_count": 12,
    "parallelism_normalized_max": 0.2,
    "engine_active": true,
    "domains_complete": true,
    "watchdog_crosscheck": {
      "available": true,
      "pairs_compared": ["CONNECTION_LOST", "ENGINE_STALLED", "RECONCILIATION_QTY_MISMATCH"],
      "max_count_delta": 0,
      "divergence": false
    }
  },
  "overall_status": "WARNING",
  "summary": {
    "total_trades": 17,
    "total_mismatches": 23,
    "total_disconnects": 2,
    "max_cpu_parallelism_raw": 2.4,
    "max_cpu_parallelism_normalized": 0.2,
    "dominant_subsystem": "stream_tick_ms",
    "first_failure": {
      "ts_chicago": "09:42:10",
      "ts_utc": "2026-03-24T14:42:10.123Z",
      "event": "RECONCILIATION_MISMATCH_DETECTED",
      "instrument": "MNQ",
      "chain_id": 2
    },
    "recovery_quality": {
      "avg_recovery_time_ms": 180000,
      "max_recovery_time_ms": 300000,
      "mismatch_to_recovery_median_ms": 4500,
      "mismatch_to_recovery_max_ms": 120000,
      "successful_recoveries": 3,
      "failed_recoveries": 1,
      "chains_classified_normal": 3,
      "chains_classified_degraded": 0,
      "chains_classified_failed": 1
    }
  },
  "silent_failure": {
    "detected": false,
    "signals": []
  },
  "drift": {
    "window_days": 14,
    "mismatch_rate_trend": "STABLE",
    "cpu_trend": "STABLE",
    "recovery_time_trend": "UP",
    "queue_wait_trend": "STABLE",
    "notes": []
  },
  "domains": {
    "safety": {
      "status": "WARNING",
      "metrics": {
        "total_mismatch_signals": 23,
        "resolved": 21,
        "unresolved_eod": 0,
        "fail_closed_count": 0,
        "forced_flatten_count": 1,
        "unowned_or_manual_events": 0
      },
      "critical_findings": [],
      "timeline_refs": []
    },
    "connectivity": {
      "status": "STABLE",
      "metrics": {
        "disconnects": 2,
        "avg_downtime_s": 4.5,
        "max_downtime_s": 7.1
      },
      "timeline_refs": []
    },
    "execution": {
      "status": "DEGRADED",
      "metrics": {
        "max_queue_wait_ms": 3200,
        "adoption_scan_max_ms": 85
      },
      "timeline_refs": []
    },
    "reconciliation": {
      "status": "STABLE",
      "metrics": {
        "passes": 1840,
        "convergence_rate_pct": 99.2,
        "stuck_pct": 0.01,
        "oscillation_pct": 0.0
      },
      "timeline_refs": [
        {
          "start_chicago": "09:42",
          "end_chicago": "09:55",
          "topic": "oscillation, MNQ"
        }
      ],
      "incident_chain_ids": [2]
    },
    "protection": {
      "status": "OK",
      "metrics": {
        "audits": 120,
        "issues_detected": 0,
        "issues_resolved": 0
      },
      "timeline_refs": []
    },
    "performance": {
      "status": "HIGH_LOAD",
      "metrics": {
        "dominant_subsystem": "stream_tick_ms",
        "subsystem_pct": {
          "stream_tick_ms": 44.0,
          "reconciliation_runner_ms": 28.0
        }
      },
      "timeline_refs": []
    },
    "supervisory": {
      "status": "STABLE",
      "metrics": {
        "invalid_transitions": 0
      },
      "timeline_refs": []
    }
  },
  "timeline": {
    "critical_periods": [],
    "compressed_entries": [
      {
        "start_utc": "2026-03-24T14:42:10.123Z",
        "end_utc": "2026-03-24T14:55:02.000Z",
        "display_start_chicago": "09:42:10",
        "display_end_chicago": "09:55:02",
        "event": "RECONCILIATION_OSCILLATION",
        "summary": "MNQ — merged span (duration 12m52s)",
        "instrument": "MNQ",
        "critical": true
      }
    ]
  },
  "incident_chains": [
    {
      "id": 1,
      "start_utc": "2026-03-24T14:31:00.000Z",
      "end_utc": "2026-03-24T14:36:00.000Z",
      "trigger_event": "CONNECTION_LOST",
      "trigger_ts_utc": "2026-03-24T14:31:00.000Z",
      "events": [],
      "resolution_time_ms": 300000,
      "classification": "NORMAL",
      "classification_detail": "NORMAL RECOVERY"
    },
    {
      "id": 2,
      "start_utc": "2026-03-24T14:42:10.123Z",
      "end_utc": "2026-03-24T14:55:02.000Z",
      "trigger_event": "RECONCILIATION_MISMATCH_DETECTED",
      "trigger_ts_utc": "2026-03-24T14:42:10.123Z",
      "events": [],
      "resolution_time_ms": null,
      "classification": "FAILED",
      "classification_detail": "UNSTABLE SYSTEM"
    }
  ]
}
```

---

## 12. Implementation roadmap (next steps)

1. Add `tools/daily_audit.py` (or extend `log_audit.py`) implementing ingestion, dedupe, domain rollups, **Tier 1–2 predictive signals only** (§3.0 — gate lifecycle, adoption strain, mismatch→recovery latency, tick stalls, watchdog cross-check, conditional journal + pass-hash silent checks), **`confidence`** + **`meta.watchdog_crosscheck`** (§4.3), **`first_failure`** (§4.4), **`recovery_quality`** (§4.5), **`silent_failure`** heuristics (§4.6), **`drift`** vs prior `reports/daily_audit/*.json` (§4.7), **timeline** (§5), **incident chains** (§6), source fallback (§2.2), normalized CPU thresholds, JSON + `.md` output.
2. Add `configs/robot/daily_audit_thresholds.json` for tunable limits (including silent-failure **T_silent**, **T_profile_gap**, **`T_heartbeat`**, drift **N**, bands, **drift_risk** promotion rules).
3. Wire optional CI / morning task: `python tools/daily_audit.py --date yesterday --tz chicago --json-out reports/daily_audit/YYYY-MM-DD.json` (tracked history enables **drift**; use `logs/audit/` only if you prefer runtime-only output under the ignored `logs/` tree).
4. After review: trade-performance layer, session scoring, stream-level scoring (out of scope for v1 framework).

---

## 13. References

- `docs/robot/LOGGING.md` — log paths and rotation
- `docs/robot/ENGINE_CPU_PROFILE.md` — CPU profile fields
- `docs/robot/RUN_ID_AND_CRITICAL_DEDUPE.md` — run_id semantics
- `tools/log_audit.py` — timestamp normalization patterns
- `RobotEventTypes.cs` — authoritative event names
- `docs/robot/audits/LATENT_SIGNAL_DISCOVERY_AUDIT_2026-03-24.md` — broader signal inventory; **daily audit implements only Tier 1–2 in §3.0**
