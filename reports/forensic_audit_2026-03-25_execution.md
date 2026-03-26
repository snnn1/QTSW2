# Forensic Audit — Execution & Runtime State (2026-03-25)

**Scope:** Causal reconstruction (not a log dump).  
**Slots:** NQ1 / NG1 stop-entry failure ~09:16 Chicago vs NQ2 success ~10:30 Chicago.  
**Sources:** `logs/robot/robot_MNQ.jsonl`, `robot_MNG.jsonl`, archived `robot_ENGINE_*.jsonl`, `data/execution_journals/*.json`, `RobotCore_For_NinjaTrader/Execution/NinjaTraderSimAdapter.cs`.

---

## 1. Two timelines

### Window A — FAILURE (~14:15–14:18 UTC ≈ 09:15–09:18 Chicago)

| UTC time | Chicago | Instrument / scope | Event | Interpretation |
|---------|---------|---------------------|--------|----------------|
| 14:16:35.117 | 09:16:35 | MNG engine `run_id=0379aef3…` | `MID_SESSION_RESTART_NOTIFICATION` stream **NG1**, RANGE_BUILDING restore | Mid-session restart path for gas; tries to resume slot |
| 14:16:35.125 | 09:16:35 | MNG | `ORDER_SUBMIT_FAIL` ×2 — `SIM account not verified - not placing stop entry orders` | **Submit rejected before SIM flag true on executor used** |
| 14:16:35.434 | 09:16:35 | MNG engine | `RANGE_BUILDING_RESTORE_SLOT_PASSED_LOCK_ATTEMPT` | Stream restoration continues **after** failed submit |
| 14:16:35.445 | 09:16:35 | MNG | `INTENT_POLICY_REGISTERED` NG1 intents `7c01407fb1114252`, `e728b0877c3bb370` | Policy registered **after** submit failure (ordering in JSONL) |
| 14:16:35.456 | 09:16:35 | MNG engine | `IEA_BINDING` `execution_instrument_key=MNG`, **`iea_instance_id=3`** | IEA finally bound for this chart |
| 14:16:35.456 | 09:16:35 | MNG engine | `SIM_ACCOUNT_VERIFIED` same `run_id` | Verification completes **~330 ms after** submit fail |
| 14:16:37.899–38.116 | 09:16:37–38 | MNQ engine `run_id=ef547a5b…` | `ENGINE_START` (MNQ), `ADAPTER_SELECTED`, timetable load, `MID_SESSION_RESTART_NOTIFICATION` **NQ1** | Parallel MNQ chart boot + NQ1 restore |
| 14:16:38.124 | 09:16:38 | MNQ | `ORDER_SUBMIT_FAIL` ×2 — same SIM message | MNQ legs fail |
| 14:16:38.374 | 09:16:38 | MNQ | `INTENT_POLICY_REGISTERED` NQ1 intents `a6401ba68ed12643`, `36e03e03a1a4dfdd` | Policy after fail timestamp in per-sink ordering |
| 14:16:38.380 | 09:16:38 | MNQ engine | `IEA_BINDING` `MNQ`, **`iea_instance_id=4`** | Binding **after** submit fail |
| 14:16:38.381 | 09:16:38 | MNQ engine | `SIM_ACCOUNT_VERIFIED` | **~257 ms after** MNQ submit fail |

**Execution journals (aligned):**  
`data/execution_journals/2026-03-25_NQ1_*.json`, `2026-03-25_NG1_*.json` — `RejectedAt` ≈ **14:16:38** / **14:16:38** NG, `RejectionReason` = same SIM string.

**Not observed in Window A on `robot_MNQ.jsonl`:** `ENTRY_SUBMIT_PRECHECK`, `PROTECTIVE_*`, `STATE_CONSISTENCY_GATE_*`, `RECONCILIATION_MISMATCH` in the ±3s slice around the fails (those lines are absent; broader day had earlier `BROKER_AHEAD` episodes at other UTC times).

---

### Window B — SUCCESS (~15:25–15:35 UTC ≈ 10:25–10:35 Chicago)

| UTC time | Chicago | Instrument / scope | Event | Interpretation |
|---------|---------|---------------------|--------|----------------|
| 14:36:28–29 | 09:36:28–29 | MNQ engine `run_id=2a40a26e…` | `ENGINE_START`, `ADAPTER_SELECTED`, timetable refresh, mid-session restarts for other streams | **New** MNQ strategy session (**different `run_id`**) |
| 14:36:30.632 | 09:36:30 | MNQ engine | `IEA_BINDING` `MNQ`, **`iea_instance_id=5`** + `EXEC_ROUTER_ENDPOINT_REGISTERED` `IEA:5` | Full bind **before** any 10:30 slot submit |
| 14:36:30.632 | 09:36:30 | MNQ engine | `SIM_ACCOUNT_VERIFIED` | SIM confirmed **≈54 minutes** before NQ2 entries |
| 15:30:01.199 | 10:30:01 | MNQ | `ENTRY_SUBMIT_PRECHECK` ×2 `allowed=True` intents `a2b11a8fd9bf480e`, `5236c557e6002d2b` | Risk/intent checks logged |
| 15:30:01.214–216 | 10:30:01 | MNQ | `ORDER_SUBMIT_SUCCESS` ×2 — `OCO_ENTRY:2026-03-25:NQ2:10:30:…` | Broker accepted stops |
| 15:30:01.215 | 10:30:01 | MNQ | `ORDER_REGISTRY_METRICS` **`iea_instance_id=5`** | Same IEA id as bind |
| 15:30:01.774 | 10:30:01 | MNQ | `ORDER_REGISTRY_ADOPTED` / `ADOPTION_SUCCESS` `RESTART_ADOPTION_ENTRY` | Working orders adopted |

**Note:** Success window uses **NQ2 / 10:30** OCO group (not NQ1); NQ1 had already failed earlier the same day — comparison is **failure vs later successful SIM path**, same calendar session after re-init.

---

## 2. Execution path — FAILURE (representative: MNQ NQ1 legs)

Intent ids: **`36e03e03a1a4dfdd`**, **`a6401ba68ed12643`** (from `INTENT_POLICY_REGISTERED` and journals).

| Step | Time (UTC) | Evidence | Notes |
|------|------------|----------|-------|
| Session / restart context | 14:16:38.116 | `MID_SESSION_RESTART_NOTIFICATION` NQ1 | RANGE_BUILDING restore |
| Submit attempt → **FAIL** | **14:16:38.124** | `ORDER_SUBMIT_FAIL` | `error` = SIM not verified; `order_type=ENTRY_STOP` |
| Policy visible (sink order) | 14:16:38.374 | `INTENT_POLICY_REGISTERED` | Appears after fail in `robot_MNQ.jsonl` |
| IEA bind | **14:16:38.380** | `IEA_BINDING` `iea_instance_id=4` | **After** fail |
| SIM verified | **14:16:38.381** | `SIM_ACCOUNT_VERIFIED` | **After** fail |
| Journal | 14:16:38.124Z | `Rejected: true`, same reason | Ground truth for “no broker id” |

**MNG (NG1)** mirrors this: **`ORDER_SUBMIT_FAIL` at 35.125** → **`IEA_BINDING` / `SIM_ACCOUNT_VERIFIED` at 35.456**.

`SubmitStopEntryOrder` explicitly fails when `!_simAccountVerified` (`NinjaTraderSimAdapter.cs` ~1327–1336).

---

## 3. Execution path — SUCCESS (MNQ NQ2 @ 10:30)

Intent ids: **`a2b11a8fd9bf480e`**, **`5236c557e6002d2b`**.

| Step | Time (UTC) | Evidence |
|------|------------|----------|
| Engine + IEA bind + SIM | 14:36:30.632 | `IEA_BINDING` `iea_instance_id=5`, `SIM_ACCOUNT_VERIFIED` |
| Precheck | 15:30:01.199 | `ENTRY_SUBMIT_PRECHECK` allowed |
| Submit success | 15:30:01.214–216 | `ORDER_SUBMIT_SUCCESS`, broker_order_id set, OCO NQ2 |
| Registry | 15:30:01.215+ | `ORDER_REGISTRY_METRICS` `iea_instance_id=5`, adoption |

---

## 4. Runtime identity comparison (failure vs success)

Logs do **not** emit `RUNTIME_FINGERPRINT`, `EXECUTOR_REBOUND`, `DUPLICATE_STRATEGY_INSTANCE_DETECTED`, or a distinct `strategy_instance_id` field on these events. Comparable fields:

| Field | Failure window (MNQ) | Success window (MNQ) | Same? |
|--------|----------------------|----------------------|-------|
| **Engine `run_id`** | `ef547a5b247d42bfa41ca965b2dd803c` | `2a40a26e49da4b25890dc3190f792825` | **No** — new strategy session |
| **`iea_instance_id` (MNQ)** | **4** (at `IEA_BINDING`) | **5** | **No** |
| **Account** | `****64-2` (SIM) | same pattern | Yes |
| **`execution_instrument_key`** | MNQ | MNQ | Yes |
| **SIM verified before submit?** | **No** (fail **before** `SIM_ACCOUNT_VERIFIED`) | **Yes** (~54 min before NQ2 submit) | **Different** |
| **NG1 chart `run_id`** | `0379aef327c44023a32ae27af761eba9` | N/A (different chart) | **Different** from MNQ failure run |

**Dominant difference:** In the failure window, **`ORDER_SUBMIT_FAIL` precedes `IEA_BINDING` and `SIM_ACCOUNT_VERIFIED`** for the **same** chart `run_id`. In the success window, **`IEA_BINDING` and `SIM_ACCOUNT_VERIFIED` occur at session start (14:36:30)** and submits at **15:30** see `_simAccountVerified == true`.

---

## 5. Root cause category

**Primary (evidence-weighted):** **`SIM_VERIFICATION_STATE`** on the **`NinjaTraderSimAdapter` / IEA executor actually hit by `SubmitStopEntryOrder`** — the boolean was still **false** at submit time.

**Mechanism (supports `STALE_EXECUTOR_BINDING` / init race):**  
For **both** MNG and MNQ failure runs, **binding + verification (`IEA_BINDING`, `SIM_ACCOUNT_VERIFIED`) are timestamped after the failed submit**. That is inconsistent with “SIM never works on this account” and instead matches:

- a **stale executor** (IEA still dispatching to an adapter that never got `VerifySimAccount` on **this** boot), **and/or**
- **mid-session restart** scheduling **entry resubmission before `SetNTContext` completes** (rebind + verify run **after** the early submit attempt).

`SetNTContext` in current code calls **`RebindExecutor` then later `VerifySimAccount()`** (`NinjaTraderSimAdapter.cs` ~800–841). Any **entering** `SubmitStopEntryOrder` **before that sequence finishes** on the **invoked** adapter yields exactly this error string.

**Not primary here:** timetable disable for NQ1/NG1 (streams were armed; engine attempted submission). **`ORDER_PATH_BLOCKED_BY_GATE`** for the narrow MNQ slice (no gate lines adjacent to fail in `robot_MNQ.jsonl`).

**Classification label for runbooks:** **`STALE_EXECUTOR_BINDING` → `SIM_VERIFICATION_STATE`** (symptom), under **`MID_SESSION_RESTART`** timing.

---

## 6. Protective + reconciliation context (failure window)

**Narrow window** `robot_MNQ.jsonl` 14:15–14:18 UTC:

| event_type | before_submit | after_submit | count (MNQ file) |
|------------|---------------|--------------|-------------------|
| `PROTECTIVE_*` | 0 | 0 | 0 |
| `STATE_CONSISTENCY_GATE_*` | 0 | 0 | 0 |
| `RECONCILIATION_MISMATCH*` | 0 | 0 | 0 |

**Earlier same UTC day** (outside this 3-minute window), MNQ had `BROKER_AHEAD` / gate traffic — relevant for **overall** session health, **not** the immediate submit predicate (SIM flag).

---

## 7. What actually happened

1. **System belief at fail time:** NQ1/NG1 streams were in **mid-session restart** recovery (`MID_SESSION_RESTART_NOTIFICATION`, range-restore warnings). The engine **attempted** to place **stop entry** orders for the hydrated NQ1/NG1 intents.

2. **Adapter / IEA state:** The code path that executed **`SubmitStopEntryOrder`** saw **`_simAccountVerified == false`**. Per ENGINE ordering, **`SIM_ACCOUNT_VERIFIED` for the same chart `run_id` had not yet been emitted** — it followed **within ~0.25–0.33 s** for MNQ/MNG. **`IEA_BINDING` also followed the failures**, meaning the **documented “IEA bound for execution routing” milestone had not yet been logged** when the adapter returned the SIM error.

3. **Why submit failed:** Hard guard in `SubmitStopEntryOrder` (`NinjaTraderSimAdapter.cs`) rejects when SIM is not verified on the **instance executing the call**.

4. **Why it later succeeded (NQ2):** A **later** MNQ engine session (`run_id` **2a40a26e**) completed **`SetNTContext`**, **`IEA_BINDING` (`iea_instance_id=5`)**, and **`SIM_ACCOUNT_VERIFIED` at 14:36:30 UTC** — **before** the **10:30** slot. At **15:30**, `ENTRY_SUBMIT_PRECHECK` ran and **`ORDER_SUBMIT_SUCCESS`** followed: same account, **verified executor**, **consistent IEA id** in registry metrics.

5. **What changed:** Not “SIM account name fixed” — **full execution bootstrap order completed before the slot fired**, and a **fresh** strategy **`run_id`** / **IEA instance id** with **verify-before-submit** ordering satisfied in practice.

---

## 8. Fix recommendation (evidence-tied)

| What | Where | Why it prevents recurrence |
|------|--------|----------------------------|
| Keep **`RebindExecutor(this)` immediately after `GetOrCreate`** in `SetNTContext` | `NinjaTraderSimAdapter.cs` ~804–807 | Minimizes window where IEA points at an unverified / prior adapter. |
| **Do not** arm RANGE_LOCKED / restart **entry** placement until **`SetNTContext` has finished** (atomically: after `VerifySimAccount` + optional hydration flags) | `RobotEngine` / stream recovery path that emits `RANGE_BUILDING_RESTORE_SLOT_PASSED_LOCK_ATTEMPT` **before** SIM verified (see MNG 35.117→35.434 vs fail 35.125) | Eliminates **race** where submission runs **before** SIM flag and **before** `IEA_BINDING` is complete. |
| Regression test / assertion: first `ORDER_SUBMIT_*` per instrument after restart must follow `SIM_ACCOUNT_VERIFIED` in monotonic clock | logging or integration test | Makes the ordering **testable**. |

`RebindExecutor` alone **narrows** stale-IEA risk; the **log-proven** issue on 2026-03-25 is **submit before verify/bind completes** on the hot path, so **gating restart submissions** is the complementary fix.

---

## 9. Success criteria checklist

| Criterion | Met? |
|-----------|------|
| Explains failure ≠ success later | Yes — different `run_id`, verify ordering, IEA id, time gap |
| Identifies exact runtime difference | Yes — **SIM false at submit** vs **verify ~54 min before success** |
| One dominant root cause | **SIM verification ordering / executor readiness** under restart |
| Testable fix direction | Yes — defer submit until post-`SetNTContext` + keep rebind |

---

*Generated from repo logs on disk; re-run after deploy to confirm `ORDER_SUBMIT_FAIL` no longer precedes `SIM_ACCOUNT_VERIFIED` on the same `run_id`.*
