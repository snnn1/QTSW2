# IEA no-progress guard — blocker diagnostic results (2026-03-24)

**Method:** Parsed all `*.jsonl` under `logs/robot/` and `logs/robot/archive/` (Python; skipped files &gt;250MB). **Primary session:** `logs/robot/robot_ENGINE.jsonl` around `2026-03-24T02:07–02:09Z` for ordered sequences.

---

## Section 1 — Deployment validation

| Check | Result |
|--------|--------|
| `IEA_ADOPTION_SCAN_NO_PROGRESS_NOT_SKIPPED` present | **Yes** — **2** lines (full corpus) |
| `IEA_ADOPTION_SCAN_SKIPPED_NO_PROGRESS` | **0** lines (explicitly still zero) |

**Verdict:** Diagnostic deployment **confirmed** — not a deployment failure.

**Context:** `robot_ENGINE.jsonl` alone: **14** `IEA_ADOPTION_SCAN_EXECUTION_COMPLETED` with `scan_request_source=RecoveryAdoption`, **13** with `adopted_delta=0`. Only **2** `NOT_SKIPPED` events — expected because emission is **narrow** (prior snapshot, `last_adopted_delta==0`, fingerprint mismatch ≤1, skip actually false) and **rate-limited** (~50s × IEA × reason).

---

## Section 2 — Blocker breakdown

**Population:** **2** × `IEA_ADOPTION_SCAN_NO_PROGRESS_NOT_SKIPPED`

| `skip_blocked_reason` | Count | % of diagnostic rows |
|------------------------|------:|----------------------:|
| `mutation_after_last_completed` | **2** | **100%** |
| *other* | 0 | 0% |

---

## Section 3 — Clause-level analysis

Payload fields are logged as **strings** `"True"` / `"False"` in these rows; interpreted as booleans below.

**Among the 2 diagnostic events:**

| Clause (`data.*`) | TRUE count | FALSE count |
|---------------------|------------|-------------|
| `has_last_completed_recovery_scan` | **2** | 0 |
| `last_completed_adopted_delta_zero` | **2** | 0 |
| `fingerprint_equal` | **2** | 0 |
| `cooldown_positive` | **2** | 0 |
| `last_completed_utc_valid` | **2** | 0 |
| `within_cooldown` | **2** | 0 |
| `last_iea_mutation_lte_last_completed` | **0** | **2** |

**Interpretation:** For both observed cases, **every** skip prerequisite passed **except** the mutation clause — **`last_iea_mutation_lte_last_completed` is FALSE** while fingerprints matched and cooldown was active.

---

## Section 4 — Primary blocker identification

1. **`mutation_after_last_completed`** — **100%** (2/2)  
2. *(none in sample)*  
3. *(none in sample)*

**Dominant reason scans were not skipped (in this dataset):** **`LastMutationUtc` strictly after `last_scan_completed_utc`** per payload, with **identical** current/prev fingerprints and **within** cooldown.

---

## Section 5 — Mutation analysis (critical)

### Occurrences

**2** / **2** diagnostic rows carry `skip_blocked_reason=mutation_after_last_completed`.

### Timestamps (exact `data` fields)

| IEA | `execution_instrument_key` | `last_scan_completed_utc` | `last_iea_mutation_utc` | Δ (mutation − completed) |
|-----|----------------------------|---------------------------|-------------------------|---------------------------|
| 3 | MNQ | `2026-03-24T02:08:10.9967242+00:00` | `2026-03-24T02:08:12.1177927+00:00` | **+1,121 ms** |
| 6 | MNG | `2026-03-24T02:08:13.0865566+00:00` | `2026-03-24T02:08:16.6055842+00:00` | **+3,519 ms** |

### Answers (data-bound)

- **Frequently?** Only **2** diagnostic samples in corpus; both show mutation **after** completed.
- **Immediately after scan completion?** **Yes** — deltas are **1.1 s** and **3.5 s**, not minutes apart.
- **Real vs noise?** Logs **do not** label the work item that advanced `last_iea_mutation_utc`. From code (see `IEA_NO_PROGRESS_GUARD_BLOCKER_ANALYSIS_2026-03-24.md`), **`LastMutationUtc` is set on every IEA worker `work()` completion**. The adoption scan itself runs as such a work item; **`last_scan_completed_utc` is recorded in the scan’s `finally`**, then control returns to the worker loop, which sets **`LastMutationUtc` ≈ `UtcNow`** — **consistent with post-completion “noise” ordering**, not necessarily a new fill/order. **No log field proves a broker event** in these two rows.

---

## Section 6 — Fingerprint stability

**In the 2 diagnostic rows:**

- `fingerprint_equal == true`: **2** / **2** (**100%**)
- `fingerprint_equal == false`: **0** / **2**
- `fingerprint_field_mismatch_count`: **0** in both cases

**Field drift among diagnostics:** **None** — `current_*` matches `prev_*` for all fingerprint components in both payloads.

**Note:** Scans that **do not** emit this diagnostic may still have **&gt;1** field mismatch or `last_adopted_delta≠0` on snapshot; this section only describes **diagnostic-eligible** rows.

---

## Section 7 — Cooldown analysis

| Metric | Value (n=2) |
|--------|-------------|
| `within_cooldown == true` | **2** |
| `within_cooldown == false` | **0** |
| `skip_blocked_reason == cooldown_expired` | **0** |

**Answer:** **Cooldown is not blocking skip** in these events; **`mutation_after_last_completed` blocks first** while still inside the 20s window (`seconds_since_last_completed`: **8.55 s** and **4.57 s**).

---

## Section 8 — Expected vs actual behavior

**Ordered excerpt, `robot_ENGINE.jsonl` (`02:07–02:09Z`):**

1. MNQ `EXECUTION_COMPLETED` `adopted_delta=1` `wall_ms=7321` — **progress**; updates recovery snapshot with **non-zero** adopted delta → later scans **correctly** not eligible for no-progress skip on that basis.
2. MNQ `EXECUTION_COMPLETED` `adopted_delta=0` `wall_ms=3384` @ `02:08:10.9967242Z`
3. MNG `EXECUTION_COMPLETED` `adopted_delta=0` `wall_ms=5566` @ `02:08:13.0865566Z`
4. MNG `NO_PROGRESS_NOT_SKIPPED` `mutation_after_last_completed` @ `02:08:17.6516476Z` → **same timestamp** `EXECUTION_STARTED` MNG
5. MNQ `NO_PROGRESS_NOT_SKIPPED` `mutation_after_last_completed` @ `02:08:19.5469677Z` → **same timestamp** `EXECUTION_STARTED` MNQ
6. MNQ / MNG `EXECUTION_COMPLETED` again `adopted_delta=0`

**Why the second scan was not skipped:** Payload shows **`last_iea_mutation_utc` &gt; `last_scan_completed_utc`** while **`fingerprint_equal=true`** and **`within_cooldown=true`** → guard **by design** refuses skip.

**Correct or wasteful?** **By current rules, the decision is “correct”** (fail-closed). **Whether it is wasteful** depends on whether that mutation timestamp reflects **material** registry change; **these two rows are consistent with worker-loop timestamp ordering after the same scan’s `finally`**, i.e. **potentially overly conservative** relative to intent — **not provable as broker activity from JSON alone**.

---

## Section 9 — Final diagnosis

| Item | Conclusion |
|------|------------|
| **Dominant blocker** | **`mutation_after_last_completed`** (100% of diagnostic sample) |
| **Secondary blockers** | **None observed** in diagnostic rows (fingerprint and cooldown OK) |
| **Guard working as designed?** | **Yes** — it **refuses** skip when `last_iea_mutation_lte_last_completed` is false |
| **Correct or overly conservative?** | **Overly conservative if** `LastMutationUtc` is only advancing due to **generic work completion** (including the adoption scan) rather than **adoption-relevant** state; **data aligns with sub-second-to-a-few-second gap after `last_scan_completed_utc`** |

---

## Section 10 — Recommended next action (ONE only)

**Replace use of `LastMutationUtc` (updated on every IEA worker work completion) in `ShouldSkipRecoveryNoProgress` with a dedicated timestamp that advances only on paths that can change adoption outcomes** (e.g. order/registry updates relevant to the execution instrument), **or** align the mutation clock with the same semantic instant as `_lastRecoveryScanCompletedUtc` when the completed work is a recovery adoption scan so the worker epilog cannot spuriously invalidate the next skip check.

*(Single change family: **split or align “adoption-relevant mutation” time** — derived from 100% `mutation_after_last_completed` with **1–3.5 s** post-`last_scan_completed_utc` and **identical** fingerprints.)*

---

## Companion plain English summary

- **Why scans still run:** The guard **requires** `last_iea_mutation_utc <= last_scan_completed_utc`. In both measured cases, **`last_iea_mutation_utc` is slightly later** than **`last_scan_completed_utc`**, so the guard **forces a full scan** even though **fingerprints are identical** and **cooldown is satisfied**.
- **What is preventing the guard:** **`mutation_after_last_completed`** — **only** blocker in this sample.
- **Valid or overly strict?** **Strict by code**; **likely noisy** relative to “real” adoption state because the **mutation clock is tied to generic worker completion**, and the **gaps (≈1.1 s and ≈3.5 s)** match **post-scan completion ordering**, not a documented broker event.
- **What needs to change next:** **One** adoption-relevant mutation signal (or alignment after recovery scan completion) **before** weakening fingerprint or cooldown rules.

---

## Definitive answers (from this log set)

| Question | Answer |
|----------|--------|
| Why is the no-progress guard not firing (`SKIPPED_NO_PROGRESS` still 0)? | **`ShouldSkip` returns false** because **`last_iea_mutation_lte_last_completed` is false** in eligible cases we observed. |
| Which condition blocks it? | **`mutation_after_last_completed`** (**100%** of `NOT_SKIPPED` rows). |
| Smallest safe fix direction? | **Decouple or align mutation time** from **generic worker `work()` completion** for this guard (see Section 10). |

---

## Files and scope

- **Inputs:** `logs/robot/**/*.jsonl`, `logs/robot/archive/**/*.jsonl`
- **Counts:** `NOT_SKIPPED=2`, `SKIPPED_NO_PROGRESS=0`, `RecoveryAdoption COMPLETED=20` (all files), `RecoveryAdoption COMPLETED=14` (`robot_ENGINE.jsonl` only)
