# IEA runtime behavior audit (post adoption gate deploy)

**Artifact:** `IEA_RUNTIME_BEHAVIOR_2026-03-24.md`  
**Scope:** Data-driven only. Counts from JSONL; no code review.  
**Inputs:** `logs/robot/robot_ENGINE.jsonl`, all `logs/robot/robot_*.jsonl`, `logs/robot/archive/*.jsonl` (included in **global** totals where noted).  
**Tooling:** `automation/scripts/iea_runtime_audit_v2.py` (aggregate JSON) plus targeted Python checks.

**Proof of post-fix build:** The corpus contains **`IEA_ADOPTION_SCAN_*`** events (first seen in `robot_ENGINE.jsonl` ~line 58964, `ts_utc` **`2026-03-24T01:21:51Z`**). Earlier pre-deploy sessions in the same file have **no** such events.

**Primary analysis window:** Last **4 wall-clock hours** of `robot_ENGINE.jsonl` ending at file max `ts_utc` (**`2026-03-24T01:23:27Z`** unless the file grew later). **Window:** **`2026-03-23T21:23:27Z` → `2026-03-24T01:23:27Z`**. **ENGINE events in window:** **21,824**.

**Full ENGINE file span (context):** `ts_utc` from **`2026-03-22T22:02:00Z`** through **`2026-03-24T01:23:27Z`** (rolling log).

---

## Section 1 — Executive summary

### What % of activity (ENGINE, **4h window**)

Denominator **21,824** events.

| Category | Rule (event name) | Count (approx) | % of window |
|----------|-------------------|----------------|-------------|
| **Recovery / reconciliation** | `RECONCILIATION*` or `RECOVERY*` | **3,781** | **17.33%** |
| **Heartbeat / timer** | `HEARTBEAT` in `event` | **5,405** | **24.77%** |
| **Adoption (all adoption-related names)** | `ADOPTION` in `event` or `IEA_ADOPTION*` | **84** | **0.38%** |
| **Execution-like (broad)** | `EXECUTION` or `EXEC_UPDATE` or `FILL` in `event` | **125** | **0.57%** |
| **Supervisory** | `SUPERVISORY` in `event` | **59** | **0.27%** |
| **Registry (ENGINE only)** | `REGISTRY` or `VERIFY_REGISTRY` in `event` | **2** | **0.009%** |
| **Remainder** | everything else | **~12,368** | **~56.7%** |

**Note:** `IEA_EXEC_UPDATE_ROUTED` / `ORDER_UPDATE` / `EXECUTION_UPDATE` as **exact** names = **0** in this window — execution work may appear under other `event` strings (e.g. fills, gate eval). The **0.57%** row is a **broader** proxy.

**Idle:** Not logged as a dedicated event; **heartbeat/timer** is the closest observable “background” signal (**~24.8%**).

### Dominant CPU driver

**Not measurable from logs** (no `ENGINE_CPU_PROFILE` / OS CPU in this slice). **Dominant log throughput** in the **4h ENGINE window** is **heartbeat (~25%)** plus **reconciliation/recovery (~17%)** plus **remainder (~57%)** (bars/streams/protective-style traffic).

**Dominant *latency* signal tied to IEA in the critical minute:** **`EXECUTION_COMMAND_STALLED`** during **`RecoveryAdoptionScan`** (**8496 ms** > **8000 ms** threshold) on **`iea_instance_id` 4** / **`MNG`** at **`2026-03-24T01:22:37.684017Z`** — i.e. **one long adoption scan work item** on the IEA worker, not parallel scans on the same IEA.

### Did the adoption gate fix reduce duplicate scans?

**Yes — for recovery scheduling, with evidence:**

| Metric | Value |
|--------|------:|
| `IEA_ADOPTION_SCAN_REQUEST_ACCEPTED` | **6** |
| `IEA_ADOPTION_SCAN_REQUEST_SKIPPED` | **4** |
| `skipped_throttled` | **2** |
| `skipped_already_running` | **2** |
| `skipped_already_queued` | **0** (none logged) |
| `IEA_ADOPTION_SCAN_EXECUTION_COMPLETED` | **6** |

So **10** recovery **scheduling attempts** in the aggregated corpus collapsed to **6** executions via **throttle + single-flight** (4 skips). **Not every trigger ran a full scan.**

**Caveat:** `ADOPTION_SCAN_START` in the **same 4h ENGINE window** = **31** (legacy inner scan logging). Only **6** of those align with **gated** `IEA_ADOPTION_SCAN_EXECUTION_COMPLETED` in the global pass — the rest are **other scan sources** (e.g. first execution path, other IEAs/ticks) **not** attributed to `RecoveryAdoption` in the gate events.

### Most likely cause of CPU pressure now (log-inferred)

1. **Long single-threaded adoption scan wall time** on the worker (**`scan_wall_ms` up to 9325 ms** on `RecoveryAdoption`) → **stall** / **queue pressure** while **`current_command_type` = `RecoveryAdoptionScan`**.  
2. **High-volume reconciliation/mismatch logging** (`RECONCILIATION_MISMATCH_METRICS` **2747** in the **4h ENGINE window**).  
3. **Historical registry divergence volume** in full multi-file logs (**252** `REGISTRY_BROKER_DIVERGENCE` when **archive + all files** counted; **0** in **last 4h** on `robot*.jsonl` — see §6).

---

## Section 2 — Adoption behavior analysis (gate events)

### Totals (all scanned `logs/robot/**/*.jsonl`)

| Event | Count |
|--------|------:|
| `IEA_ADOPTION_SCAN_REQUEST_ACCEPTED` | **6** |
| `IEA_ADOPTION_SCAN_REQUEST_SKIPPED` | **4** |
| `IEA_ADOPTION_SCAN_EXECUTION_STARTED` | **6** |
| `IEA_ADOPTION_SCAN_EXECUTION_COMPLETED` | **6** |
| `IEA_ADOPTION_SCAN_OFF_WORKER_VIOLATION` | **0** |
| `ADOPTION_DEFERRAL_HEARTBEAT_RETRY` | **0** |

### Skips (from `IEA_ADOPTION_SCAN_REQUEST_SKIPPED` `data.disposition`)

| disposition | Count |
|-------------|------:|
| `skipped_throttled` | **2** |
| `skipped_already_running` | **2** |

### `scan_wall_ms` (from `IEA_ADOPTION_SCAN_EXECUTION_COMPLETED`)

| Stat | Value |
|------|------:|
| Samples | **6** |
| **Average** | **5477 ms** |
| **Max** | **9325 ms** |

### `scan_request_source`

| Source | Accepted (logged) | Completed |
|--------|------------------|-----------|
| `RecoveryAdoption` | **6** | **6** |

No `FirstExecutionUpdate`, `DeferredRetry`, or `Bootstrap` appeared in these gate events in the current corpus (may simply not have fired in the short window where gate events exist).

### Distinct `adoption_scan_episode_id`

**6** distinct IDs (matches **6** completed scans).

### Key questions

| Question | Answer |
|----------|--------|
| Multiple triggers collapsing into one scan? | **Partially.** **4** requests **skipped** vs **6** runs; **no** `skipped_already_queued` in data. |
| Identical fingerprints repeating? | **Yes across runs** for a given instrument: e.g. MNQ completions repeat **`account_orders_total` = 8**, **`candidate_intent_count` = 82**, **`same_instrument_qtsw2_working_count` = 0**, **`deferred_state` = False** — with **`adopted_delta` = 0** each time. |
| Deferred retry hammering? | **`ADOPTION_DEFERRAL_HEARTBEAT_RETRY` = 0** — **no** evidence in logs. |

---

## Section 3 — Duplicate / no-progress detection

### `adoption_scan_episode_id`

**6** unique IDs; **no** single ID repeats across multiple **`EXECUTION_COMPLETED`** lines in the extracted set (1 completion per episode).

### Fingerprint repetition (from `IEA_ADOPTION_SCAN_EXECUTION_COMPLETED`)

**MNQ (`iea_instance_id` 3):** repeated **`(account_orders_total=8, candidate_intent_count=82, same_instrument_qtsw2_working_count=0, deferred_state=False)`** across **multiple episodes** — **same broker/journal picture**, **`adopted_delta=0`** each time.

**MNG (`iea_instance_id` 4):** repeated **`(8, 73, 0, False)`** with **`adopted_delta=0`**.

### Consecutive legacy scans

Using **only** `ADOPTION_SCAN_START` / `ADOPTION_SCAN_SUMMARY` pairing on **`robot_ENGINE.jsonl`**: **0** overlapping START depth per `iea_instance_id` (**no** second START before SUMMARY for the same IEA).

### `IEA_REPEATED_UNCHANGED_STATE`

**0** occurrences (event not present).  
**`ADOPTION_SAME_STATE_RETRY_WINDOW`:** **0**.

---

## Section 4 — Queue and latency analysis

### `queue_depth_at_accept` (`IEA_ADOPTION_SCAN_REQUEST_ACCEPTED`)

Observed values: **6**, **0**, **0** (multiple accepts). **Not** a sustained backlog signal from this field alone.

### `IEA_QUEUE_PRESSURE_DIAG`

**1** line (global scan). Example (**`2026-03-24T01:22:40.6866097Z`**):

| Field | Value |
|-------|------|
| `iea_instance_id` | **4** |
| `execution_instrument_key` | **MNG** |
| `queue_depth_current` | **1** |
| `queue_depth_high_water_mark` | **1** |
| `current_work_item_age_ms` | **11498** |
| `max_work_item_age_ms` | **4456** |
| `enqueue_rate_10s` / `dequeue_rate_10s` | **1** / **0** |
| `current_command_type` | **`RecoveryAdoptionScan`** |
| `enqueue_wait_timeout_count` | **0** |
| `enqueue_wait_slow_count` | **0** |

**Interpretation:** **Depth stayed low** while **one work item ran very long** (age **~11.5 s**), consistent with a **single expensive adoption scan** occupying the worker.

### EnqueueAndWait

| Event | Count (all logs scanned) |
|-------|--------------------------|
| `IEA_ENQUEUE_AND_WAIT_TIMEOUT` | **0** |
| `IEA_ENQUEUE_AND_WAIT_TIMING` | **0** |

**Slow waits / timeouts:** **No** timing rows — **cannot** compute average/max wait from these files.

### Stalls

| Event | Count | Detail |
|-------|------:|--------|
| `EXECUTION_COMMAND_STALLED` | **1** | **`RecoveryAdoptionScan`**, **8496 ms**, **`iea_instance_id` 4**, **`2026-03-24T01:22:37.684017Z`** |

**Falling behind?** **One** observed episode: **worker busy ~8–9 s+** on adoption scan → **stall** fired; queue **depth 1** in the pressure diag **3 s later** — **not** a deep queue, **long-running head item**.

---

## Section 5 — Threading / concurrency verification

### `on_iea_worker` on gate events

All sampled **`IEA_ADOPTION_SCAN_REQUEST_ACCEPTED`** / **`SKIPPED`** lines show **`on_iea_worker`:** **`False`** — consistent with **reconciliation/timer threads scheduling** work onto the IEA queue (expected).

### `IEA_ADOPTION_SCAN_OFF_WORKER_VIOLATION`

**0**.

### Concurrent heavy scans **per IEA**

**`ADOPTION_SCAN_START` / `ADOPTION_SCAN_SUMMARY` stack simulation:** **0** violations (no stacked START for same `iea_instance_id`).

**Note:** Do **not** treat `IEA_ADOPTION_SCAN_EXECUTION_STARTED` + `ADOPTION_SCAN_START` as two concurrent scans in a naive stack — the inner **`ADOPTION_SCAN_START`** is **part of** the gated execution.

**Explicit answer:** **No evidence** of **two heavy scans overlapping on the same IEA** in log order; **skipped_already_running** shows **duplicate recovery requests were dropped** while a scan was **Running**.

---

## Section 6 — Registry & reconciliation cost

### Global counts (parser over **all** `logs/robot/**/*.jsonl` including archive)

| Event | Count |
|--------|------:|
| `REGISTRY_BROKER_DIVERGENCE` | **252** |
| `REGISTRY_BROKER_DIVERGENCE_ADOPTED` | **39** |
| `ORDER_REGISTRY_METRICS` | **114** |
| `RECONCILIATION_ORDER_SOURCE_BREAKDOWN` | **210,362** |

The **`210,362`** figure is dominated by **historical / multi-day** combined logs (includes **archive** and long-running streams). **Not** comparable to a single 4h session without time filtering.

### ENGINE **4h window**

| Event | Count | Approx / minute (÷ 240) |
|--------|------:|-------------------------|
| `RECONCILIATION_ORDER_SOURCE_BREAKDOWN` | **133** | **0.55** |
| `RECONCILIATION_MISMATCH_METRICS` | **2747** | **11.45** |
| `RECONCILIATION_PASS_SUMMARY` | **229** | **0.95** |

### `REGISTRY_BROKER_DIVERGENCE` in **last 4h** on **`logs/robot/robot*.jsonl` only** (no archive)

**0** lines — divergences in this workspace are **not** occurring in that wall window on those files (they live **earlier** in the rolling instrument logs or only in archive).

### Correlation with CPU-heavy periods

**Not measurable** (no CPU time series).

### Is registry verification the dominant repeated workload?

**In the last 4h ENGINE window:** **No** — **`RECONCILIATION_MISMATCH_METRICS`** (**2747**) >> adoption gate lines (**22** total `IEA_ADOPTION_*` in window).  
**In the full accumulated log corpus:** **reconciliation** events dwarf adoption; **registry** divergence totals are **material historically** (**252** with archive) but **not** in the **last 4h** slice above.

---

## Section 7 — Event rate / storm detection

### ENGINE **4h window** (rates / minute ≈ count / 240)

| Signal | Count | ≈ / min |
|--------|------:|--------:|
| `RECONCILIATION_MISMATCH_METRICS` | 2747 | 11.45 |
| `RECONCILIATION_ORDER_SOURCE_BREAKDOWN` | 133 | 0.55 |
| `ADOPTION_SCAN_START` | 31 | 0.13 |
| `IEA_ADOPTION_SCAN_*` (all kinds) | 22 | 0.09 |

### Spikes (adoption-related lines, ENGINE, by minute UTC)

| Minute (UTC) | Adoption-related event lines |
|--------------|-----------------------------|
| `2026-03-24T01:02:00` | **30** |
| `2026-03-24T01:22:00` | **26** |
| `2026-03-24T01:03:00` | **20** |

**Sustained:** `RECONCILIATION_MISMATCH_METRICS` is **continuously** high relative to adoption (see §6).

---

## Section 8 — Timeline (critical period — gate + stall)

**UTC `2026-03-24T01:21:51` → `~01:23:01`** (`run_id` **`2e2e76938d8c4b9aa776e86682dadbe8`** / **`3d86481e484d4066909a676ffb9c4385`**)

| Time (UTC) | Event / effect |
|------------|----------------|
| **01:21:51.540** | `IEA_ADOPTION_SCAN_REQUEST_ACCEPTED` — MNQ, **`accepted_and_queued`**, **`queue_depth_at_accept` = 6**, **`on_iea_worker` = false** |
| **01:21:52.067** | `IEA_ADOPTION_SCAN_REQUEST_SKIPPED` — **`skipped_throttled`** (MNQ) |
| **01:21:53.797** | `IEA_ADOPTION_SCAN_EXECUTION_STARTED` — episode `5020590d…` |
| **01:22:03.123** | `IEA_ADOPTION_SCAN_EXECUTION_COMPLETED` — **`scan_wall_ms` = 9325**, **`adopted_delta` = 0** |
| **01:22:04.315** | `IEA_ADOPTION_SCAN_REQUEST_SKIPPED` — **`skipped_already_running`** (MNQ) |
| **01:22:18.429** | MNG `REQUEST_ACCEPTED` + near-immediate **`EXECUTION_STARTED`** |
| **01:22:19.367** | MNQ **`EXECUTION_STARTED`** (new episode) + another **`REQUEST_ACCEPTED`** |
| **01:22:20.441** | MNG **`skipped_throttled`** |
| **01:22:20.495** | MNG **`EXECUTION_COMPLETED`** — **`scan_wall_ms` = 2065** |
| **01:22:27.357** | MNQ **`EXECUTION_COMPLETED`** — **`scan_wall_ms` = 7989** |
| **01:22:32.812** | MNQ new **`EXECUTION_STARTED`** + **`REQUEST_ACCEPTED`** |
| **01:22:35.915** | MNQ **`EXECUTION_COMPLETED`** — **`scan_wall_ms` = 3102** |
| **01:22:37.349** | MNG **`EXECUTION_COMPLETED`** — **`scan_wall_ms` = 8161** |
| **01:22:37.684** | **`EXECUTION_COMMAND_STALLED`** — **`RecoveryAdoptionScan`**, **8496 ms** — **`FLATTEN_EMERGENCY_ON_BLOCK_FAILED`**, **`STREAM_FROZEN_NO_STAND_DOWN`** |
| **01:22:40.686** | **`IEA_QUEUE_PRESSURE_DIAG`** — depth **1**, **`current_work_item_age_ms` = 11498**, command **`RecoveryAdoptionScan`** |

**Cause → effect (data-only):** **Recovery adoption scans** ran **serially per IEA** with **multi-second `scan_wall_ms`** → **one** scan exceeded **stall threshold** → **`EXECUTION_COMMAND_STALLED`** and downstream **flatten/stream freeze** events at the **same timestamp**.

---

## Section 9 — Before vs after reasoning (observed)

| Question | Answer |
|----------|--------|
| Parallel scan storms eliminated? | **For the same IEA, yes** — **no stacked `ADOPTION_SCAN_START`**; **`skipped_already_running`** logged. |
| Duplicate triggers skipped? | **Yes** — **4** skips (**2** throttle, **2** already running). |
| What replaced adoption as dominant load? | **Reconciliation/mismatch metrics volume** on ENGINE; **long single-scan wall time** became the acute **latency** driver (stall). |

---

## Section 10 — Final diagnosis

### Top 3 load drivers (log-based)

| # | Driver | Likelihood | Impact | Classification |
|---|--------|------------|--------|------------------|
| 1 | **Long `RecoveryAdoptionScan` on IEA worker** (`scan_wall_ms` multi-second, **stall**) | **High** | **High** (blocks queue / triggers supervisor) | **Queue backlog amplification** (head-of-line) + **synchronous long work** on worker |
| 2 | **`RECONCILIATION_MISMATCH_METRICS` rate** in ENGINE window | **High** | **High log / coordination volume** | **Registry/reconciliation overhead** (engine-side) |
| 3 | **Repeated `adopted_delta=0` scans** with **identical completion fingerprint** | **High** | **Medium** (work done, no state change) | **Retry loop (logical)** |

**Single classification (primary acute failure mode):** **Queue backlog amplification** is **not** deep **depth** here — it is **one expensive head item** (**RecoveryAdoptionScan**) → **`EXECUTION_COMMAND_STALLED`**.

**Scan storm:** **Partially fixed** — **duplicate scheduling** suppressed; **serial** scans still **dense** and **heavy**.

---

## Section 11 — Next action recommendation (one only)

**Reduce wall time of `ScanAndAdoptExistingOrders` / `RecoveryAdoptionScan` for the hot path** (e.g. **early-exit** when **fingerprint unchanged** and **`adopted_delta` would be 0**, without weakening fail-closed) **or** **split** “full account order walk” from “same-instrument QTSW2 slice” so recovery does not hold the IEA worker for **~8–9+ seconds** — because logs show **that** duration directly triggered **`EXECUTION_COMMAND_STALLED`** and emergency flatten/freeze side effects.

---

## Data gaps (explicit)

- **CPU %** — not in JSONL.  
- **`IEA_ENQUEUE_AND_WAIT_TIMING`** — **0** rows.  
- **`ORDER_REGISTRY_METRICS` / registry divergence** — **not** in the **last 4h** slice of `robot*.jsonl` (historical elsewhere).  
- **Execution updates** — no **`IEA_EXEC_UPDATE_ROUTED`** lines in window.

---

## Companion plain English summary

- **What the system actually did:** After deploy, **recovery adoption** started using the **gate**: **off-worker** threads **queued** scans; **some** extra recovery calls were **throttled** or **skipped as already running**. **Six** full gated scans **completed**, with **multi-second** **`scan_wall_ms`**.  
- **What caused the most load:** By **volume**, **reconciliation/mismatch** logging dominated the ENGINE window. By **severity**, **one long `RecoveryAdoptionScan`** caused a **command stall** and **emergency flatten / stream freeze** side effects.  
- **Whether the fix worked:** **Yes for its intent** — **duplicate recovery triggers were suppressed** (**4** skips) and **no off-worker violation**; **no same-IEA overlapping START/SUMMARY** pairs.  
- **What is still wrong:** Scans still **cost seconds** and can **block** the IEA worker long enough to **trip the stall supervisor**, even with **queue depth ~1**. **Repeated** scans show **no adoptions** with the **same** completion fingerprint.  
- **What we should do next:** **Shorten or short-circuit** the **recovery adoption scan** when it is **provably no-progress** on the same fingerprint, **without** removing fail-closed guarantees — see §11.

---

## Appendix

- Regenerate stats: `python automation/scripts/iea_runtime_audit_v2.py logs/robot`  
- `robot_ENGINE.jsonl` may begin with a **UTF-8 BOM**; parsers should use **`utf-8-sig`**.
