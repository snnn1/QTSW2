# IEA adoption scan phase timing — runtime audit (2026-03-24)

**Scope:** `logs/robot/**/*.jsonl`, prioritizing `robot_ENGINE*.jsonl` and `robot_EXECUTION.jsonl` (hydration/ranges streams omitted; they do not contain these events).  
**Audit script:** `automation/scripts/iea_scan_phase_timing_audit.py` (default fast mode: engine + execution only).  
**Filter:** `scan_request_source = RecoveryAdoption` where applicable.

---

## Section 1 — Deployment validation

| Check | Result |
|--------|--------|
| `IEA_ADOPTION_SCAN_PHASE_TIMING` present | **Yes** — 4 events (all `RecoveryAdoption`) |
| `IEA_ADOPTION_SCAN_EXECUTION_COMPLETED` present | **Yes** — 28 events (`RecoveryAdoption`) |

**Verdict:** Deployment/logging for both event types is **confirmed**. No stop condition.

**Note:** Phase timing (4) is far below completed scans (28) because phase timing is **rate-limited** in code (expected); completed events fire every scan.

---

## Section 2 — Timing summary

### RecoveryAdoption — `IEA_ADOPTION_SCAN_PHASE_TIMING` only

| Metric | Value |
|--------|--------|
| Count | **4** |
| Average `total_wall_ms` | **6627.0** |
| Max `total_wall_ms` | **9485** |

**Field mapping (log vs. spec names):** payloads use `fingerprint_build_ms` (equivalent to **phase_fingerprint_build_ms** in the audit checklist).

### Per-phase statistics (same 4 events)

For each phase: **average ms**, **max ms**, **average % of `total_wall_ms`** (mean of per-event ratios, so rows need not sum to exactly 100%).

| Phase (log field) | Avg ms | Max ms | Avg % of total wall |
|-------------------|--------|--------|---------------------|
| `fingerprint_build_ms` | 2709.25 | 4138 | **40.23%** |
| `phase_candidates_ms` | 3904.75 | 5316 | **59.59%** |
| `phase_precount_ms` | 0 | 0 | 0% |
| `phase_journal_diag_ms` | 4.75 | 9 | 0.08% |
| `phase_pre_loop_log_ms` | 0.75 | 3 | 0.008% |
| `phase_main_loop_ms` | 0 | 0 | 0% |
| `phase_summary_ms` | 0.5 | 2 | 0.005% |

**Uninstrumented gap:** For each event, `total_wall_ms` minus the sum of the seven phase fields is small (order 1–23 ms on these rows), consistent with work outside the inner stopwatches (e.g. gating).

---

## Section 3 — Dominant phase ranking

### By average contribution (ms)

1. `phase_candidates_ms` — **3904.75** ms avg  
2. `fingerprint_build_ms` — **2709.25** ms avg  
3. `phase_journal_diag_ms` — 4.75 ms  
4. (remaining phases ≤ 0.75 ms avg)

### By worst-case single-phase duration (max ms across events)

1. `phase_candidates_ms` — **5316** ms max  
2. `fingerprint_build_ms` — **4138** ms max  
3. `phase_journal_diag_ms` — 9 ms  
4. `phase_pre_loop_log_ms` — 3 ms  
5. `phase_summary_ms` — 2 ms  
6. `phase_precount_ms` / `phase_main_loop_ms` — 0 ms  

### Answers

- **Which phase dominates average scans?** **`phase_candidates_ms`** (~59.6% of wall on average; ~3905 ms avg).  
- **Which phase dominates slow scans?** Still **`phase_candidates_ms`** on **max single-phase ms** (5316 > 4138). On the **single** event where fingerprint share was highest (MNQ, `total_wall_ms` = 5729), **`fingerprint_build_ms`** was the **larger** slice of *that* row (3224 ms vs 2501 ms ≈ **56.3%** vs **43.7%**).  
- **Are they the same?** **Mostly yes** across the sample; **one** of four rows is fingerprint-led on a within-row basis.

---

## Section 4 — Slow-scan subset

### A) `total_wall_ms >= 2000`

All **4** timing events satisfy this threshold, so **subset statistics equal Section 2** for averages and maxima.

### B) Scans near `EXECUTION_COMMAND_STALLED` (RecoveryAdoptionScan)

**Definition used:** a `IEA_ADOPTION_SCAN_PHASE_TIMING` row is “near” a stall if **same `execution_instrument_key`**, stall time **≥** timing time, and **Δt ≤ 10 s** (stall shortly after the logged scan completes).

| Stall `ts_utc` | Instrument | Nearest preceding `PHASE_TIMING` `ts_utc` | Δt |
|----------------|------------|-------------------------------------------|-----|
| 2026-03-24T02:48:53.7565284+00:00 | MNQ | 2026-03-24T02:48:50.7652794+00:00 | **2.99 s** |
| 2026-03-24T02:29:36.3960398+00:00 | MNQ | *none in corpus* | — |
| 2026-03-24T01:22:37.6840170+00:00 | MNG | *none in corpus* | — |

**Near-stall subset (n = 1):** MNQ @ 02:48:50 — `total_wall_ms` **5910**

| Phase | ms | % of this scan’s `total_wall_ms` |
|--------|-----|-------------------------------------|
| `phase_candidates_ms` | 3778 | **63.9%** |
| `fingerprint_build_ms` | 2127 | **36.0%** |
| `phase_journal_diag_ms` | 4 | 0.07% |
| Others | 0–1 | ~0% |

**What makes slow scans slow (from measured phases only):** Time is overwhelmingly in **`phase_candidates_ms`** plus **`fingerprint_build_ms`**. **`phase_main_loop_ms`** is **0** for every timed row; **`main_loop_orders_seen`** is **0** for every timed row — so **measured** wall time is **not** coming from the adoption `foreach` hot path in this sample.

---

## Section 5 — Order volume relationship

**Table (`IEA_ADOPTION_SCAN_PHASE_TIMING`, n = 4):**

| `total_wall_ms` | `phase_main_loop_ms` | `account_orders_total` | `candidate_intent_count` | `qtsw2_same_instrument_working` | `execution_instrument_key` |
|-----------------|----------------------|-------------------------|---------------------------|----------------------------------|------------------------------|
| 9485 | 0 | 16 | 82 | 0 | MNQ |
| 5384 | 0 | 16 | 73 | 0 | MNG |
| 5910 | 0 | 16 | 82 | 0 | MNQ |
| 5729 | 0 | 17 | 82 | 0 | MNQ |

**Does `total_wall_ms` scale with `account_orders_total`?** **Not in this sample.** `account_orders_total` only moves **16 → 17** while `total_wall_ms` spans **5384 → 9485** (range **4101 ms**). No monotonic relationship is observable from these four points.

**Does `phase_main_loop_ms` scale with `account_orders_total`?** **No measurable signal** — **`phase_main_loop_ms` is 0** for all four rows (integer-ms granularity; loop may still run briefly).

**Does `candidate_intent_count` matter materially?** **Not as a simple predictor here.** MNG has the **lowest** `candidate_intent_count` (**73**) but **not** the lowest `phase_candidates_ms` (4024); MNQ rows share **82** intents but `phase_candidates_ms` ranges **2501–5316**. So **intent count alone does not explain** the spread in **`phase_candidates_ms`** in this dataset.

---

## Section 6 — Instrument comparison

| Instrument | Scan count (phase timing) | Avg `total_wall_ms` | Avg `phase_main_loop_ms` | Avg `account_orders_total` |
|------------|---------------------------|---------------------|---------------------------|----------------------------|
| **MNQ** | 3 | **7041.3** | 0 | **16.33** |
| **MNG** | 1 | **5384** | 0 | **16** |

**Is one instrument consistently worse?** **MNQ has higher average wall time** in this **tiny** sample (3 vs 1 scan). **Is it account-level order volume?** **Not supported:** average account orders are **16 vs 16.33** — essentially identical; the gap is in **measured pre-loop phases** (`phase_candidates_ms` + `fingerprint_build_ms`), not in `account_orders_total`.

---

## Section 7 — Stall correlation

**Source:** `EXECUTION_COMMAND_STALLED` with `current_command_type`: **RecoveryAdoptionScan** — **3** events in `robot_EXECUTION.jsonl` (via audit script).

### Per-stall: nearest preceding `IEA_ADOPTION_SCAN_PHASE_TIMING` (same instrument)

| Stall time | Instrument | `iea_instance_id` | `runtime_ms` (stall payload) | Nearest preceding phase timing | Phase breakdown (from timing row) | Orders / candidates (timing row) |
|------------|------------|-------------------|------------------------------|----------------------------------|------------------------------------|-----------------------------------|
| 02:48:53Z | MNQ | 4 | 8901 | **02:48:50Z** (Δ 2.99 s) | `total_wall_ms` **5910**; `phase_candidates_ms` **3778** (63.9%); `fingerprint_build_ms` **2127** (36.0%); `phase_main_loop_ms` **0** | `account_orders_total` **16**, `candidate_intent_count` **82**, `main_loop_orders_seen` **0** |
| 02:29:36Z | MNQ | **6** | 14136 | **None** | No `IEA_ADOPTION_SCAN_PHASE_TIMING` in corpus for this instance/instrument before the stall | — |
| 01:22:37Z | MNG | 4 | 8496 | **None** | Phase timing for this instrument first appears **later** in the log window (~02:48Z) | — |

**Which phase was “responsible” for the stall?** **Telemetry does not label stall cause.** For the **only** stall with a nearby phase row (MNQ 02:48:53), the **measured** time in the preceding scan is split **~64%** **`phase_candidates_ms`** and **~36%** **`fingerprint_build_ms`**; **`phase_main_loop_ms` = 0**. We **cannot** assert the stall counter equals those phases (command `runtime_ms` **8901** ≠ scan `total_wall_ms` **5910**).

---

## Section 8 — Final diagnosis

| Item | Conclusion |
|------|------------|
| **Dominant phase (single biggest contributor)** | **`phase_candidates_ms`** — **~59.6%** of wall on average; **5316 ms** worst single-phase. |
| **Secondary phase** | **`fingerprint_build_ms`** — **~40.2%** average share; **4138 ms** max; leads **one** of four rows by within-row ms. |
| **Primary driver** (choose one) | **Mixed** between **candidate lookup cost** (dominant average and worst-case phase ms) and **fingerprint / pre-loop account work** (very large second bucket). **Not** journal diagnostics, **not** pre-loop logging, **not** summary — those are **under 10 ms** on average combined. **Not** “account order volume” as observed: **`account_orders_total` barely varies** while wall time varies by **seconds**. |
| **One recommended next fix** | **Reduce cost of adoption candidate discovery** — i.e. the work measured as **`phase_candidates_ms`** (largest average and largest max phase). |

---

## Companion plain English summary

Recovery adoption scans that emitted **full phase timing** spent **almost all** wall clock in two places: **building the list of candidate intent IDs** (**`phase_candidates_ms`**, typically **~60%**) and **building the recovery fingerprint** (**`fingerprint_build_ms`**, typically **~40%**). **Journal diagnostics, the big pre-scan log, and the summary** together are **negligible** (single-digit milliseconds on average). The **main adoption loop** reported **0 ms** and **zero orders seen** on every timed row — so the **slow part is before that loop**, not iterating working orders in this sample. **Account order count** barely changed (**16–17**) while total time swung by **seconds**, so **order volume is not the main lever in this dataset**. The **best next optimization** is to **speed up candidate intent discovery** (the **`phase_candidates_ms`** phase), since it is the **single largest measured slice** on average and at peak.

---

## Definitive answers (from this corpus)

1. **What part of one `RecoveryAdoption` scan is actually slow?** **`phase_candidates_ms`**, with **`fingerprint_build_ms`** a close second.  
2. **Is order volume the main reason?** **No** — **`account_orders_total`** is almost flat; **`phase_main_loop_ms`** is **0** in all timed rows.  
3. **Single best next optimization?** **Target **`phase_candidates_ms`** (candidate intent lookup / journal-backed discovery).**
