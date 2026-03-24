# IEA adoption gate + no-progress guard — post-deployment validation (2026-03-24)

**Method:** Parse JSONL under `logs/robot/` (and `logs/robot/archive/` where noted). No code-path review.  
**Tools:** Line-level JSON parse over all `*.jsonl` in `logs/robot` + `logs/robot/archive` (Python); targeted reads of `robot_ENGINE.jsonl` and `robot_ENGINE_20260324_014600.jsonl`.

---

## Corpus split (important)

| Source | Role | Evidence |
|--------|------|----------|
| **`logs/robot/robot_ENGINE_20260324_014600.jsonl`** | Rotated capture; **earlier** wall-clock segment (`01:21–01:23` UTC) | **No** `recovery_fingerprint_at_start_*` on `IEA_ADOPTION_SCAN_EXECUTION_COMPLETED` → **pre–fingerprint-logging build** (or older DLL) for that segment. |
| **`logs/robot/robot_ENGINE.jsonl`** (current) | **Latest** continuous ENGINE file | **`recovery_fingerprint_at_start_*` present** on recovery `IEA_ADOPTION_SCAN_EXECUTION_COMPLETED` → **post–deploy / post–fingerprint-instrumentation DLL** for events in the `01:46` UTC burst. |

All quantitative totals below that say **“full corpus”** = every `*.jsonl` in `logs/robot` + `logs/robot/archive` (excluding files &gt;200MB in the script — none hit that cap).

---

## Section 1 — Deployment validation (telemetry presence)

| Event | Full corpus count | Verdict |
|--------|-------------------|---------|
| `IEA_ADOPTION_SCAN_REQUEST_ACCEPTED` | **13** | Present |
| `IEA_ADOPTION_SCAN_REQUEST_SKIPPED` | **6** | Present |
| `IEA_ADOPTION_SCAN_EXECUTION_STARTED` | **13** | Present |
| `IEA_ADOPTION_SCAN_EXECUTION_COMPLETED` | **13** | Present |
| `IEA_ADOPTION_SCAN_SKIPPED_NO_PROGRESS` | **0** | **Not observed** |

**Strict interpretation of your checklist (“if any are missing, STOP”):**  
**STOP for no-progress guard proof.** The event type is implemented in code and listed in `RobotEventTypes`, but **no `IEA_ADOPTION_SCAN_SKIPPED_NO_PROGRESS` line exists** in the available logs, so **runtime verification cannot confirm the skip path fired**.

**Deployment of fingerprint logging:** **PASS** — `robot_ENGINE.jsonl` contains `recovery_fingerprint_at_start_account_orders`, `recovery_fingerprint_at_start_candidate_intent_count`, etc., on recovery completions (sample lines ~9466+ in the file at audit time).

---

## Section 2 — Adoption behavior (full corpus)

| Metric | Count |
|--------|------:|
| Total `IEA_ADOPTION_SCAN_REQUEST_ACCEPTED` | 13 |
| Total `IEA_ADOPTION_SCAN_REQUEST_SKIPPED` | 6 |
| Total `IEA_ADOPTION_SCAN_EXECUTION_COMPLETED` | 13 |

**`IEA_ADOPTION_SCAN_REQUEST_SKIPPED` by `data.disposition` (full corpus):**

| Disposition | Count |
|-------------|------:|
| `skipped_throttled` | 4 |
| `skipped_already_running` | 2 |
| `skipped_no_progress` | **0** *(not a request-level skip; no-progress is a separate event — and that event count is 0)* |

**Ratios (full corpus):**

- **execution / request** = 13 / 13 = **1.0** (every accepted request in this corpus has a matching execution completed — consistent with 1:1 accepted→ran for this sample; throttled/blocked requests are separate “skipped” rows, not “accepted”).
- **skipped / request** = 6 / 13 ≈ **0.46** (includes throttled + already_running relative to accepted count — **interpret with care**: accepted and skipped are not mutually exclusive events in all engines; here they are distinct event types).
- **skipped_no_progress / request** = **0 / 13 = 0** (no `IEA_ADOPTION_SCAN_SKIPPED_NO_PROGRESS` lines).

**Answers (evidence-based):**

- **Duplicate triggers collapsing?** **Partially yes** — **6** request-level skips: **4** throttled, **2** already_running (single-flight / throttle working as designed in this sample).
- **No-progress scans skipped?** **Not demonstrable from logs** — **0** `IEA_ADOPTION_SCAN_SKIPPED_NO_PROGRESS`.

**Post–fingerprint slice (`robot_ENGINE.jsonl` only, full file):**

| Metric | Value |
|--------|------:|
| ACCEPTED | 7 |
| REQUEST_SKIPPED | 2 (both `skipped_throttled`) |
| EXECUTION_COMPLETED | 7 |
| execution / accepted | **1.0** |
| skipped / accepted | **0.286** |

---

## Section 3 — No-progress guard validation

**From `IEA_ADOPTION_SCAN_SKIPPED_NO_PROGRESS`:** **No rows** → cannot verify fingerprint match, `last_adopted_delta == 0`, or cooldown from that event.

**Cross-check (intended sequence COMPLETED `adopted_delta=0` → SKIPPED_NO_PROGRESS → next COMPLETED after cooldown/mutation):** **Not observable** — **no SKIPPED_NO_PROGRESS** lines.

**Is the guard preventing repeated identical scans?** **Unknown from telemetry.** We **do** see **multiple** recovery `IEA_ADOPTION_SCAN_EXECUTION_COMPLETED` with **`adopted_delta = 0`** and **multi-second** `scan_wall_ms` in **`robot_ENGINE.jsonl`** (7 completions in ~**51 s** window `01:46:04–01:46:55` UTC), i.e. **heavy work still ran repeatedly** without any logged skip.

**Plausible explanations (hypothesis — not proven without extra fields):**

- **`LastMutationUtc` &gt; `lastCompletedUtc`** between recovery attempts (guard correctly refuses skip).
- **Fingerprint instability** (e.g. `candidate_intent_count` / account order totals changing).
- Recovery rescans spaced **outside** 20s cooldown so skip condition not met.

**Evidence against “fingerprint never stable”:** consecutive MNQ recovery completions in `robot_ENGINE.jsonl` show repeated **`recovery_fingerprint_at_start_candidate_intent_count":"82"`** and **`account_orders_total":"11"`** — stable across several lines (grep sample). So **fingerprint *can* repeat**; **absence of skip still points to mutation clock or other guard clauses**, not log absence alone.

---

## Section 4 — Scan cost reduction

**`IEA_ADOPTION_SCAN_EXECUTION_COMPLETED` — `scan_wall_ms` (full corpus):**

| Stat | Value |
|------|------:|
| n | 13 |
| Average | **4653 ms** |
| Max | **9325 ms** |

**Post–fingerprint file (`robot_ENGINE.jsonl` only):**

| Stat | Value |
|------|------:|
| n | 7 |
| Average | **3946.7 ms** |
| Max | **5663 ms** |

**Scans per minute (before vs after):**

| Segment | Evidence | Approximate rate |
|---------|----------|------------------|
| **Earlier session** (`robot_ENGINE_20260324_014600.jsonl`, fixed window `01:21:50–01:23:10` UTC) | **6** COMPLETED in **80 s** | **6 / (80/60) ≈ 4.5 / min** |
| **90 s sliding max** (same file) | **6** COMPLETED in **90 s** | **4.0 / min** |
| **Post–fingerprint burst** (`robot_ENGINE.jsonl`, `01:46:04–01:46:55`) | **7** COMPLETED in **51 s** | **7 / (51/60) ≈ 8.2 / min** |

**Before/after “total scan count reduction”:** **Not established.** Archive `robot_ENGINE_*.jsonl` files contain **no** `IEA_ADOPTION_SCAN_EXECUTION_COMPLETED` matches under quick search (adoption telemetry absent or different run mode). **No comparable “before” adoption volume** in archive for a clean delta.

**Answer:** **Total heavy scans did not decrease in the only post-fingerprint burst we measured** — **7** full completions in **~51 s**, **0** skips. **Average wall time remains multi-second** (~**3.9–4.7 s**).

---

## Section 5 — Stall analysis

| Source | `EXECUTION_COMMAND_STALLED` count |
|--------|----------------------------------:|
| Full corpus (`logs/robot` + `archive`) | **1** |
| `robot_ENGINE.jsonl` (post–fingerprint file) | **0** |
| `robot_ENGINE_20260324_014600.jsonl` | **1** |

**Stall record (full corpus, only occurrence):**

- **ts_utc:** `2026-03-24T01:22:37.6840170+00:00`
- **data:** `iea_instance_id=4`, `execution_instrument_key=MNG`, `current_command_type=RecoveryAdoptionScan`, `runtime_ms=8496`, `threshold_ms=8000`, `policy=IEA_COMMAND_STALL_SUPERVISOR_REQUIRED`

**Answer:**

- **Stalls reduced or eliminated?** **Not eliminated in corpus** — **1** stall remains, **tied to `RecoveryAdoptionScan`**.
- **Post–fingerprint ENGINE tail:** **0** stalls in that file — **insufficient duration** to claim elimination.

---

## Section 6 — Queue health

**`IEA_QUEUE_PRESSURE_DIAG` (full corpus):** **1** line.

**Payload snapshot:** `queue_depth_current=1`, `queue_depth_high_water_mark=1`, `current_work_item_age_ms=11498`, `max_work_item_age_ms=4456`, `current_command_type=RecoveryAdoptionScan`, `iea_instance_id=4`, `execution_instrument_key=MNG`.

**`queue_depth_at_accept` from `IEA_ADOPTION_SCAN_REQUEST_ACCEPTED` (full corpus):** n = **13**, **average = 0.46**, **max = 6**.

**`LOG_PIPELINE_METRIC` (sample from tail of `robot_ENGINE.jsonl`, `01:47:54` UTC):** `peak_queue_depth=21`, `avg_queue_depth≈5.9` — **logging pipeline**, not IEA work queue; **do not conflate** with `queue_depth_at_accept`.

**Answer:** **IEA adoption accept queue depth is usually low** in this sample (max **6**). **One** pressure diag shows **RecoveryAdoptionScan** on the worker with **~11.5 s** current work age — **backlog was real in that moment**. Cannot claim “mostly idle” globally; **evidence is sparse** (single `IEA_QUEUE_PRESSURE_DIAG`).

---

## Section 7 — Correctness validation

- **`adopted_delta > 0`:** **0 / 13** completions in full corpus — **no positive adoption deltas** in logs (session may have had nothing to adopt).
- **When state changes, scan runs:** **Not directly provable** without controlled state change; we **do** see **13** executions completing after **13** starts (no orphaned START without COMPLETED in corpus).
- **Missed adoption / divergence persistence:** **No log-derived proof of missed adoption** in this corpus; **also no proof of stress** (no `adopted_delta > 0`).

**Answer:** **No evidence of broken adoption**; **no evidence of adoption under load** either.

---

## Section 8 — Repeated-state detection

- **Repeated fingerprints:** **Yes** — post–fingerprint `IEA_ADOPTION_SCAN_EXECUTION_COMPLETED` lines for **MNQ** show repeated **`recovery_fingerprint_at_start_*`** values across successive scans (same `candidate_intent_count` **82**, `account_orders_total` **11** in consecutive samples).
- **Sequences of `IEA_ADOPTION_SCAN_SKIPPED_NO_PROGRESS`:** **None** (count **0**).

**Answer:** **Repeated full scans still happen** (multiple COMPLETED with `adopted_delta=0` and similar fingerprints). **We are not observing skip-instead-of-rescan** in logs.

---

## Section 9 — Timeline (critical window)

**Window A — busiest adoption + stall (pre–fingerprint file):** `2026-03-24T01:21:50Z` → `01:23:10Z` (`robot_ENGINE_20260324_014600.jsonl`)

| Event type | Count |
|------------|------:|
| REQUEST_ACCEPTED | 6 |
| REQUEST_SKIPPED | 4 |
| EXECUTION_STARTED | 6 |
| EXECUTION_COMPLETED | 6 |
| EXECUTION_COMMAND_STALLED | 1 |
| IEA_QUEUE_PRESSURE_DIAG | 1 |

**Narrative (from ordered events in file):** Recovery adoption **accepted** then **throttled** (MNQ); a scan **starts** and **completes**; further **skipped_already_running** / **throttled**; overlapping **STARTs** on **MNG**; **`EXECUTION_COMMAND_STALLED`** on **MNG** while **`current_command_type=RecoveryAdoptionScan`**; **QUEUE_PRESSURE_DIAG** on **MNG** with work age **~11.5 s**; more **COMPLETED** / **SKIPPED**; later **MNQ** scan **COMPLETED**.

**Window B — post–fingerprint burst:** `01:46:04Z` → `01:46:55Z` (`robot_ENGINE.jsonl`)

- **7** accepted, **7** started, **7** completed, **2** throttled skips, **0** stalls, **0** `SKIPPED_NO_PROGRESS`.
- **Triggers:** `scan_request_source=RecoveryAdoption` on completions (per `data` in file).

---

## Section 10 — Final diagnosis

| Question | Answer |
|----------|--------|
| Did both fixes work? | **Gate / throttle:** **Yes** (skipped_throttled + skipped_already_running observed). **No-progress guard:** **Cannot confirm** ( **0** skip events; repeated zero-delta scans still observed). |
| Adoption gate (parallel control) | **Yes** (evidence: request skips). |
| No-progress guard | **No proof in logs** (no `IEA_ADOPTION_SCAN_SKIPPED_NO_PROGRESS`). |
| **Dominant cost (this corpus)** | **Scan cost (still heavy)** — mean **~4.7 s**, max **~9.3 s** per completion across corpus; post-fingerprint mean **~3.9 s** for 7 runs. |
| **Remaining risk** | **Stall risk present** — **1** `EXECUTION_COMMAND_STALLED` tied to **`RecoveryAdoptionScan`**. **Wasted work present** — multiple **full** scans with **`adopted_delta=0`** without logged no-progress skips. |

**Definitive goals:**

- **Eliminate adoption-driven stalls?** **No** — at least **one** stall remains, **explicitly** on **`RecoveryAdoptionScan`** (pre–fingerprint segment).
- **Eliminate repeated no-progress work?** **Not shown** — **no** skip telemetry; **multiple** zero-delta completions in short intervals.
- **Next real bottleneck?** **Heavy adoption scan wall time** while **`adopted_delta=0`**, with **no observed short-circuit** in logs.

---

## Section 11 — Next action (ONE only)

**Add one rate-limited diagnostic event when a recovery adoption scan *would* qualify for no-progress skip except for a single blocking clause (e.g. `LastMutationUtc` vs `lastCompletedUtc`, or fingerprint field mismatch), emitted only if fingerprint matches last completed and `last_adopted_delta == 0` within cooldown — so the next audit can prove why skips are not appearing without weakening safety.**

---

## Companion plain English summary

**What changed after the fix:** Logs show **fingerprint fields on completed recovery scans** in the latest ENGINE file — that confirms the **newer DLL** is emitting richer adoption telemetry. **Request-level throttling and “already running” skips** still show up as before.

**What improved:** In the **short post–fingerprint slice** of `robot_ENGINE.jsonl`, **no new stall** was recorded (the corpus still contains **one** stall **earlier** in the rotated file).

**What is still wrong:** **`IEA_ADOPTION_SCAN_SKIPPED_NO_PROGRESS` never appears**, while **full scans** with **`adopted_delta = 0`** **still run back-to-back** in places. **One stall** is still **explicitly tied to recovery adoption**.

**What the system is doing now:** **Recovery adoption** is still **driving multi-second work** on the IEA worker; **parallel triggers** are partly **collapsed** via **throttle/gate**, but **we cannot see the no-progress short-circuit operating** in these logs.

---

## Artifact note

If you re-run this audit after a longer session, re-use the same Python aggregation over `logs/robot/**/*.jsonl` and require **non-zero** `IEA_ADOPTION_SCAN_SKIPPED_NO_PROGRESS` to mark Section 1 green for the guard.
