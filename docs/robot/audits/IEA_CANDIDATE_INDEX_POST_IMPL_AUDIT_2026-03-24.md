# IEA adoption candidate index — post-implementation runtime audit (2026-03-24)

**Corpus:** `logs/robot/robot_ENGINE.jsonl`, `logs/robot/robot_EXECUTION.jsonl` (archive not used — all signals present in active files).  
**Method:** Full file parse; filter **after** first `EXECUTION_JOURNAL_ADOPTION_INDEX_REBUILT` by `ts_utc`.

---

## Section 1 — Deployment validation

| Check | Result |
|--------|--------|
| 1. `EXECUTION_JOURNAL_ADOPTION_INDEX_REBUILT` present | **Yes** |
| First occurrence `ts_utc` | **`2026-03-24T03:08:04.2913780+00:00`** |
| Total `EXECUTION_JOURNAL_ADOPTION_INDEX_REBUILT` rows (entire file) | **7** (multiple `ExecutionJournal` instances / strategies) |
| 2. `IEA_ADOPTION_SCAN_PHASE_TIMING` **after** first rebuild | **Yes** — **2** rows (`RecoveryAdoption`) |
| 3. `EXECUTION_JOURNAL_ADOPTION_INDEX_FALLBACK` (after first rebuild) | **0** |
| `EXECUTION_JOURNAL_ADOPTION_INDEX_REBUILD_FAILED` (entire file) | **0** |

**STOP conditions:** Not triggered — index rebuild and post-rebuild phase timing both present.

---

## Section 2 — Timing comparison (core validation)

### A) Phase timing — post-index (after `2026-03-24T03:08:04.2913780+00:00`)

**Filter:** `scan_request_source = RecoveryAdoption`, `IEA_ADOPTION_SCAN_PHASE_TIMING`.

| Metric | Value |
|--------|--------|
| Count | **2** |
| Average `total_wall_ms` | **40.0** |
| Max `total_wall_ms` | **41** |

| Phase | Avg ms | Max ms | Avg % of `total_wall_ms`* |
|--------|--------|--------|-----------------------------|
| `phase_candidates_ms` | **23.0** | **35** | **58.25%** |
| `fingerprint_build_ms` | **0.5** | **1** | **2.44%** |

\*Average of per-row percentages: row 1 MNQ `11/41` = 26.83%; row 2 MNG `35/39` = 89.74%; mean = **58.25%**.

**Raw rows (post-index):**

| `ts_utc` | Instrument | `total_wall_ms` | `phase_candidates_ms` | `fingerprint_build_ms` |
|----------|------------|-----------------|-------------------------|-------------------------|
| 2026-03-24T03:08:14.0135151+00:00 | MNQ | 41 | 11 | 1 |
| 2026-03-24T03:08:16.9262650+00:00 | MNG | 39 | 35 | 0 |

### B) Before vs after

**Before (pre-index baseline)** — from [IEA_SCAN_PHASE_TIMING_RESULTS_2026-03-24.md](IEA_SCAN_PHASE_TIMING_RESULTS_2026-03-24.md), **n = 4** `RecoveryAdoption` timing rows (`2026-03-24T02:48:14Z`–`02:49:02Z`):

| Metric | Before (avg) | Before (max) | After (avg) | After (max) | Δ avg | Δ max |
|--------|----------------|--------------|-------------|-------------|-------|-------|
| `phase_candidates_ms` | **3904.75** | **5316** | **23.0** | **35** | **−3881.75** | **−5281** |
| `fingerprint_build_ms` | **2709.25** | **4138** | **0.5** | **1** | **−2708.75** | **−4137** |
| `total_wall_ms` | **6627.0** | **9485** | **40.0** | **41** | **−6587** | **−9444** |

**Note:** **After** sample is **n = 2** because `IEA_ADOPTION_SCAN_PHASE_TIMING` is rate-limited; many recovery scans completed without a phase-timing row (see Section 3).

---

## Section 3 — Scan frequency (after first index rebuild)

**Filter:** `IEA_ADOPTION_SCAN_EXECUTION_COMPLETED`, `scan_request_source = RecoveryAdoption`, `ts_utc` ≥ first rebuild.

| Metric | Value |
|--------|--------|
| Count | **5** |
| First `ts_utc` | **2026-03-24T03:08:14.0135151+00:00** |
| Last `ts_utc` | **2026-03-24T03:08:39.0444905+00:00** |
| Time span | **25.031 s** |
| Scans per minute | **11.99** (= 5 / (25.031/60)) |

**Comparison:** Prior qualitative note was **~4–8 scans/min bursts**; this window is **shorter** and **higher** instantaneous rate — not directly comparable without a matched pre-index window of the same length.

---

## Section 4 — Stall analysis

**Source:** `logs/robot/robot_EXECUTION.jsonl` (entire file).

| Metric | Count |
|--------|--------|
| `EXECUTION_COMMAND_STALLED` **after** first rebuild | **0** |
| `EXECUTION_COMMAND_STALLED` **entire file** | **0** |

**RecoveryAdoptionScan stalls in this file:** **none** logged.

**Answer:** With this corpus, **no** adoption-command stalls appear in `robot_EXECUTION.jsonl`, so **no** post-rebuild stall tied to long scan duration is observable here.

---

## Section 5 — Index usage validation

### Fallback

| Event | Count (after first rebuild) |
|--------|-----------------------------|
| `EXECUTION_JOURNAL_ADOPTION_INDEX_FALLBACK` | **0** |

**Interpretation:** Logs give **no evidence** of full-scan fallback after index warm-up — **consistent with index-only candidate lookups** for the audited window.

### Candidate correctness proxy

**Filter:** `IEA_ADOPTION_SCAN_EXECUTION_COMPLETED`, `RecoveryAdoption`, after first rebuild.

| Condition | Count |
|-----------|--------|
| `adopted_delta > 0` | **0** |
| Total completed rows | **5** |

**Interpretation:** In this window, recovery scans **did not** record adoptions (`adopted_delta` always **0**). That does **not** prove adoption is broken — only that **no adoptable broker/registry gap** was resolved in these five runs.

---

## Section 6 — Dominant phase (post-fix)

From the **2** post-rebuild `IEA_ADOPTION_SCAN_PHASE_TIMING` rows, ranking **average ms**:

1. `phase_candidates_ms` — **23.0**
2. `phase_summary_ms` — **2.0** (avg of 4 and 0)
3. `phase_journal_diag_ms` — **2.5**
4. `phase_pre_loop_log_ms` — **1.0**
5. `fingerprint_build_ms` — **0.5**
6. `phase_main_loop_ms`, `phase_precount_ms` — **0**

**Answer:** **`phase_candidates_ms` remains the largest instrumented phase** by average ms, but **absolute time is orders of magnitude smaller** than pre-index (23 ms avg vs ~3905 ms baseline).

---

## Section 7 — Final diagnosis (log-backed only)

1. **Did the candidate index fix the bottleneck?** **Yes, for this corpus:** post-rebuild `phase_candidates_ms` and `total_wall_ms` dropped from **multi-second** to **tens of ms** on the two timed scans; **0** fallback events.
2. **Did `phase_candidates_ms` decrease materially?** **Yes:** avg **3904.75 → 23.0** ms (Δ **−3881.75**); max **5316 → 35** (Δ **−5281**).
3. **Did total scan time drop below ~1000 ms?** **Yes:** avg **40.0** ms, max **41** ms on timed rows; completed `scan_wall_ms` for post-rebuild recovery scans: **41, 39, 13, 32, 17** — all **< 1000**.
4. **Stalls eliminated or still present?** In **`robot_EXECUTION.jsonl`**, **0** `EXECUTION_COMMAND_STALLED` rows **total** — **no** stall evidence in this file.
5. **Stable under RecoveryAdoption?** **Within this 25 s window:** five completed recovery scans, wall times **13–41 ms**, **0** fallbacks, **0** stalls in execution log — **stable on these metrics**.

**Caveat:** Only **2** phase-timing rows post-rebuild; broader stability claims need more samples when rate limiting emits timing again.

---

## Section 8 — Single next action

**Continue routine operation and re-audit after additional runtime** (or temporarily relax phase-timing rate limit for validation) **if** you need a **larger n** for `IEA_ADOPTION_SCAN_PHASE_TIMING` post-index — measured gains already meet stated success criteria on available data; **no further candidate-index optimization** is indicated by these logs.

---

## Success criteria checklist

| Criterion | Met? | Evidence |
|-----------|------|----------|
| `phase_candidates_ms` significantly reduced (<500 ms target) | **Yes** | Avg **23**, max **35** (n=2 timed) |
| `total_wall_ms` significantly reduced (<1000 ms target) | **Yes** | Avg **40**, max **41**; completed scans **13–41** ms |
| Reduced or zero `EXECUTION_COMMAND_STALLED` for `RecoveryAdoptionScan` | **Yes (in file)** | **0** stalled rows in `robot_EXECUTION.jsonl` |
| Near-zero `INDEX_FALLBACK` | **Yes** | **0** after rebuild |

**New bottleneck:** Not identified from these logs — remaining phase ms are **already sub-50 ms** on timed scans.
