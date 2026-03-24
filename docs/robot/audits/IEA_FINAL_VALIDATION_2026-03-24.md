# IEA adoption — post–mutation-alignment validation (2026-03-24)

**Method:** JSONL parse and time-order inspection of **`logs/robot/robot_ENGINE.jsonl`**. **Post–mutation-alignment anchor:** first **`IEA_ADOPTION_SCAN_MUTATION_TIME_ALIGNED`** at **`2026-03-24T02:29:00.7332977+00:00`**.

---

## Section 1 — Deployment validation

**Authoritative file for the latest continuous session:** `logs/robot/robot_ENGINE.jsonl`.

| Event | Count (`robot_ENGINE.jsonl`) |
|--------|------------------------------:|
| `IEA_ADOPTION_SCAN_MUTATION_TIME_ALIGNED` | **4** |
| `IEA_ADOPTION_SCAN_SKIPPED_NO_PROGRESS` | **2** |
| `IEA_ADOPTION_SCAN_EXECUTION_COMPLETED` | **18** |

All three are **present** → deployment / logging **OK**.

**`IEA_ADOPTION_SCAN_SKIPPED_NO_PROGRESS == 0`?** **No** — **2** lines → **Section 1 pass** (no-progress skip is active in this log).

*(A broader crawl of `logs/robot` + `archive` can double-count rotated files; ENGINE is the single source for this audit.)*

---

## Section 2 — Core metrics

**Source:** `robot_ENGINE.jsonl` (continuous adoption telemetry in this file).

| Metric | Count |
|--------|------:|
| `IEA_ADOPTION_SCAN_EXECUTION_COMPLETED` | **18** |
| `IEA_ADOPTION_SCAN_SKIPPED_NO_PROGRESS` | **2** |
| `IEA_ADOPTION_SCAN_REQUEST_ACCEPTED` | **21** |
| `IEA_ADOPTION_SCAN_REQUEST_SKIPPED` | **7** |

**Skip ratio (per your definition):**

\[
\frac{\text{SKIPPED\_NO\_PROGRESS}}{\text{SKIPPED\_NO\_PROGRESS} + \text{EXECUTION\_COMPLETED}}
= \frac{2}{2 + 18} \approx \mathbf{10.0\%}
\]
(ENGINE file)

---

## Section 3 — Sequence validation (critical)

**Target pattern:**  
`EXECUTION_COMPLETED` (`RecoveryAdoption`, `adopted_delta = 0`) → `IEA_ADOPTION_SCAN_MUTATION_TIME_ALIGNED` → `IEA_ADOPTION_SCAN_SKIPPED_NO_PROGRESS`  
(same `execution_instrument_key`, in time order; log lines may have other instruments between).

**Observed (post-alignment cluster, `robot_ENGINE.jsonl`):**

1. **MNQ**  
   - `02:29:00.7332977Z` — `IEA_ADOPTION_SCAN_EXECUTION_COMPLETED` (`adopted_delta=0`, `RecoveryAdoption`)  
   - **Same timestamp** — `IEA_ADOPTION_SCAN_MUTATION_TIME_ALIGNED` (`execution_instrument_key=MNQ`)  
   - `02:29:08.1108388Z` — `IEA_ADOPTION_SCAN_SKIPPED_NO_PROGRESS` (`execution_instrument_key=MNQ`, `seconds_since_last_scan≈7.38`)

2. **MNG**  
   - `02:29:21.2462995Z` — `IEA_ADOPTION_SCAN_EXECUTION_COMPLETED` (`adopted_delta=0`, `RecoveryAdoption`)  
   - **Same timestamp** — `IEA_ADOPTION_SCAN_MUTATION_TIME_ALIGNED` (`execution_instrument_key=MNG`)  
   - `02:29:32.7402116Z` — `IEA_ADOPTION_SCAN_SKIPPED_NO_PROGRESS` (`execution_instrument_key=MNG`, `seconds_since_last_scan≈11.49`)

**Count of this pattern (instrument/time corroborated):** **2**.

---

## Section 4 — Scan reduction

| Window | Recovery `EXECUTION_COMPLETED` count | Wall span | Approx. rate |
|--------|--------------------------------------|-----------|----------------|
| **Pre-fix burst** `02:07:50–02:09:00Z` | **7** | **70 s** | **7 / (70/60) ≈ 6.0 / min** |
| **Post-fix** from `02:29:00Z` (first align) through last recovery completion in trace | **4** | **32.4 s** | **4 / (32.4/60) ≈ 7.4 / min** |

**Compared to prior audit (~4–8 / min bursts):** Post-fix **hot slice** is **still in the same band** (~7.4 / min); **not** a clear material drop **in this short post-fix interval**.

**Note:** Skips **reduce** full scans when the guard hits; the **2** skips in this file are **additional** evidence of fewer heavy runs **for those requests**, not fully captured by completion rate alone.

---

## Section 5 — Stall analysis

**`robot_ENGINE.jsonl`:** **`EXECUTION_COMMAND_STALLED`** — **1** true stall row (`event` field).

**Payload:**

- `ts_utc`: `2026-03-24T02:29:36.3960398+00:00`  
- `current_command_type`: **`RecoveryAdoptionScan`**  
- `execution_instrument_key`: **`MNQ`**  
- `runtime_ms`: **`14136`**  
- `threshold_ms`: **`8000`**

**Answer:** **Adoption-driven stalls are not eliminated** in this log — **one** stall remains **explicitly** tied to **`RecoveryAdoptionScan`** **after** mutation alignment and **after** no-progress skips occurred in the same minute.

**Full corpus** also contains an **older** stall (`01:22:37Z`, MNG, `RecoveryAdoptionScan`) from an earlier segment; counted separately from the post-fix window above.

---

## Section 6 — Queue health

**`IEA_QUEUE_PRESSURE_DIAG`:** **1** sample in full corpus (same as prior audits — sparse).

**`queue_depth_at_accept`** (`IEA_ADOPTION_SCAN_REQUEST_ACCEPTED`, ENGINE): **n = 21**, **max = 12**, **mean ≈ 0.96**.

**Answer:** **Long scans can still block** — stall row shows **~14.1 s** on **`RecoveryAdoptionScan`**. Queue depth at accept is usually low; **worker time** on adoption scan remains the stress signal.

---

## Section 7 — Correctness validation

**Recovery completions with `adopted_delta > 0`:** **1** in **`robot_ENGINE.jsonl`** (`02:08:02Z`, MNQ, `adopted_delta=1`).

**Check:** Any **`IEA_ADOPTION_SCAN_SKIPPED_NO_PROGRESS`** for same instrument within **120 s** after that completion — **0** matches (scripted check on ordered ENGINE events).

**Answer:** **No evidence** in these logs that a **progress** adoption (`adopted_delta > 0`) was immediately followed by a no-progress skip for the same instrument.

---

## Section 8 — Residual repeated work

**Post `02:29:00Z` (ENGINE):** **`IEA_ADOPTION_SCAN_NO_PROGRESS_NOT_SKIPPED`:** **1** (diagnostic still occasionally emitted — fingerprint/mutation/cooldown edge).

**Fingerprints on the two skip rows:** stable vs prior scan (`account_orders_total`, `candidate_intent_count`, etc., match “no change” narrative in payloads).

**Answer:** **Skipping is happening** (**2** events). **Redundant scans are not fully gone** — **4** recovery **`EXECUTION_COMPLETED`** in **~32 s** post-anchor plus **1** stall on a **long** recovery scan.

---

## Section 9 — Timeline (critical window)

**Window:** **`2026-03-24T02:28:57Z` → `02:29:38Z`** (busiest post-alignment stretch in **`robot_ENGINE.jsonl`**).

| Time (UTC) | Event (abbrev.) | Instrument | Notes |
|------------|-----------------|------------|--------|
| 02:28:57 | REQUEST_ACCEPTED / STARTED | MNQ | |
| 02:29:00 | **COMPLETED** | MNQ | `adopted_delta=0`, Recovery |
| 02:29:00 | **MUTATION_TIME_ALIGNED** | MNQ | Same ts as COMPLETED |
| 02:29:06 | REQUEST_ACCEPTED / STARTED | MNG | |
| 02:29:08 | **REQUEST_ACCEPTED** + **SKIPPED_NO_PROGRESS** | MNQ | Skip (~7.4 s after last completed) |
| 02:29:09 | **COMPLETED** + **MUTATION_TIME_ALIGNED** | MNG | `adopted_delta=0` |
| 02:29:17–22 | REQUEST / STARTED / COMPLETED / ALIGNED | MNG, MNQ | MNQ scan runs (~11 s wall implied by next COMPLETED) |
| 02:29:32 | **SKIPPED_NO_PROGRESS** | MNG | |
| 02:29:33 | **COMPLETED** + **MUTATION_TIME_ALIGNED** | MNQ | `adopted_delta=0` |
| 02:29:36 | **EXECUTION_COMMAND_STALLED** | MNQ | `RecoveryAdoptionScan`, **14136 ms** |
| 02:29:38 | REQUEST_ACCEPTED | MNQ | |

**Summary:** **Mutation alignment** and **no-progress skip** both appear; **MNQ** still hits a **long** recovery scan and **stall** in the same minute.

---

## Section 10 — Final diagnosis

| Question | Answer |
|----------|--------|
| **Did the fix work?** | **Partially** — **no-progress skipping is active** (`SKIPPED_NO_PROGRESS` **> 0**); **mutation alignment** is present. |
| **No-progress guard** | **Yes** (log proof). |
| **Scan reduction** | **Unclear / not proven** in rate metric (post-fix slice **~7.4/min** vs pre-burst **~6/min** on comparable windows); **qualitatively** skips remove some heavy work. |
| **Stall elimination** | **No** — **1** `EXECUTION_COMMAND_STALLED` with **`RecoveryAdoptionScan`** at **02:29:36Z**. |
| **Dominant cost** | **Scan cost** (multi-second **`RecoveryAdoptionScan`**, stall **14.1 s**). |
| **Remaining issue** | **Long recovery adoption scans** can still exceed stall threshold **after** skips fire on other triggers. |

---

## Section 11 — Next action (ONE only)

**Profile and reduce wall time of a single `RecoveryAdoptionScan` execution path** (why **~14 s** on MNQ in this window) **without** loosening the no-progress or fail-closed rules — stalls are still tied to that work type.

---

## Companion plain English summary

- **What changed after the fix:** Logs show **`IEA_ADOPTION_SCAN_MUTATION_TIME_ALIGNED`** and **`IEA_ADOPTION_SCAN_SKIPPED_NO_PROGRESS`** — the guard **can** skip redundant recovery scans when state matches.  
- **What improved:** **No-progress skipping is real** (two documented skip events); **mutation self-invalidation** is addressed in the success path. **No** skip immediately after **`adopted_delta=1`** observed.  
- **What remains:** **Adoption-driven stalls are not gone** — one **MNQ** **`RecoveryAdoptionScan`** ran **~14.1 s** and tripped **`EXECUTION_COMMAND_STALLED`**. Scan **rate** in a short post-fix window stayed in the **same rough range** as earlier bursts.

---

## Definitive answers (this log set)

| Question | Answer |
|----------|--------|
| **Did we finally stop repeated no-op scans?** | **Partially** — **some** are skipped (**2** skips); **others** still complete (**4** recovery completions in **~32 s** post-anchor). |
| **Did we remove adoption-driven stalls?** | **No** — **1** stall **`RecoveryAdoptionScan`** post-fix. |
| **Is the IEA stable?** | **Improved** on redundant recovery work; **not stable** under stall policy while **long** recovery scans remain. |
