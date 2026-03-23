# Verification — flatten exposure, multi-instance, timing, journal (post–broker-identity unification)

**Purpose:** Answer “what does the system **do** next?” for residual exposure and related risks, with **code-backed** facts. Not a design proposal unless noted.

---

## 1. Exposure persists after correct flatten attempts — what happens next?

### Implemented behavior (not silent)

**A. Verify window:** After a successful flatten command path, `RegisterPendingFlattenVerification` sets a deadline **`utcNow + FLATTEN_VERIFY_WINDOW_SEC`** (4.0s). `OnVerifyPendingFlattens` runs at the end of **`DrainNtActions`** (strategy thread), i.e. when that chart’s adapter drains the NT queue.

**B. If canonical exposure still &gt; 0 after the window:**

1. Logs **`FLATTEN_VERIFY_FAIL`** and **`FLATTEN_BROKER_POSITION_REMAINS`** (with `reconciliation_abs_remaining`, rows, etc.).
2. **If `retryCount < FLATTEN_VERIFY_MAX_RETRIES` (4):** enqueues a new **`NtFlattenInstrumentCommand`** with reason `VERIFY_FAIL_RETRY_{n}` and **extends** the pending entry with **`retryCount + 1`** and a new 4s deadline → **automatic re-flatten** (full flatten path again; per-leg exposure is re-read at submit time).

3. **Else (exhausted retries):** logs **`FLATTEN_FAILED_PERSISTENT`** (CRITICAL), invokes **`_standDownStreamCallback("", …)`**, **`_blockInstrumentCallback(instrumentKey, …)`**, removes the pending entry.

**C. Terminal / escalation:** There is **no** separate event name `FAIL_CLOSED_ENTERED`. The **terminal** flatten-verify outcome is **`FLATTEN_FAILED_PERSISTENT`**.

**D. What `blockInstrument` does (this engine):** `RobotEngine` wires `blockInstrumentCallback` to supervisory action + **`FlattenEmergency`** + stand-down streams for that instrument + health critical report (**instrument-scoped**, not “whole account” unless other code does). See `RobotEngine.cs` ~1038–1056.

**Gaps / caveats:**

- **Not account-global:** “Fail-closed” here is **instrument + this engine’s streams**, not a single global account kill across every chart.
- **Silent loop:** After max retries, pending verify is **removed**; exposure may remain at broker until manual intervention — but **`FLATTEN_FAILED_PERSISTENT`** + block/stand-down **are** emitted (not silent).

**Code:** `NinjaTraderSimAdapter.cs` (`FLATTEN_VERIFY_WINDOW_SEC`, `FLATTEN_VERIFY_MAX_RETRIES`), `NinjaTraderSimAdapter.NT.cs` `OnVerifyPendingFlattens` ~1249–1315.

---

## 2. Multi-instance coordination — one authority per canonical instrument?

### Reconciliation

**Single-writer / debounce:** `ReconciliationRunner` + `ReconciliationStateTracker` can **skip** or **debounce** mismatch handling for **non-owner** instances (`RECONCILIATION_SECONDARY_INSTANCE_SKIPPED`, etc.). That reduces **reconciliation** noise and duplicate recovery **requests**.

### Flatten execution — **not globally serialized per canonical instrument**

| Mechanism | Scope |
|-----------|--------|
| **`_flattenLatchByInstrument`** (IEA) | **Per `InstrumentExecutionAuthority` instance** (typically **per chart / per execution key**), keyed by string `instrumentKey` (e.g. `MNQ`). **Not** process-wide. Two MNQ charts ⇒ **two** IEAs ⇒ **two** independent latches for the same logical `MNQ`. |
| **`_pendingFlattenVerifications`** | **Per `NinjaTraderSimAdapter` instance** (per chart). Same string key can exist on **multiple** adapters. |
| **`StrategyThreadExecutor` / `DrainNtActions`** | **Per adapter** (per strategy instance). |

**Conclusion:** There is **no** single global mutex “only one flatten for `MNQ` across all charts.” **Duplicate flatten attempts** from multiple strategies on the same account are **structurally possible** (duplicate orders, racing verifies).

**Mitigation in place:** Reconciliation **coordination** (owner instance); flatten **not** unified across instances.

---

## 3. Partial fill / residual exposure

### Detection

Post-submit verification uses **`GetBrokerCanonicalExposure`** → **`ReconciliationAbsQuantityTotal`**. If one leg of three is still open, **remaining abs = 1** is visible on the next verify cycle.

### Re-targeting

**Yes — automatic retry path:** On verify fail (any non-zero remainder), the code **re-enqueues** `NtFlattenInstrumentCommand` (until retry cap). The next flatten pass **re-reads** exposure and submits **per remaining leg** (design A). So **partial fill** → **re-flatten** is intended.

**Stop condition:** After **`FLATTEN_VERIFY_MAX_RETRIES`** failed verifies, **no further automatic verify-driven flattens** for that pending entry; **`FLATTEN_FAILED_PERSISTENT`** fires and instrument-level block/stand-down run.

---

## 4. Timing / snapshot consistency

### What exists

- **One** canonical re-read **per verify deadline** (not “N consecutive non-zero samples” before acting).
- **Fixed delay** of **4s** (`FLATTEN_VERIFY_WINDOW_SEC`) before each check after registration or retry.

### Risks (still real)

- **False positive “still exposed”:** Market fill lag &gt; 4s or snapshot timing can cause **`FLATTEN_BROKER_POSITION_REMAINS`** and **unnecessary retry flattens** (may be benign or may churn).
- **False negative:** Unlikely from a single read; more risk from **multi-chart** interference than from single-read logic.

**Not implemented:** “Must stay non-zero for M samples over X seconds before escalation” (only **retry count** cap, not persistence-of-signal debounce).

---

## 5. Journal ↔ broker alignment after flatten

### Broker identity fix

Reconciliation **`broker_qty`** and flatten **canonical exposure** are aligned by design.

### Journal updates

- **`OnFlattenFillReceived`** (IEA) releases flatten latch and calls **`OnRecoveryFlattenResolved`** → recovery state **FLATTENING → RESOLVED** (per that IEA). It does **not** by itself set **`RECONCILIATION_BROKER_FLAT`** on journal entries.
- **`RECONCILIATION_BROKER_FLAT`** / journal completion is driven by **`ReconciliationRunner`** when it detects broker flat vs open journals (separate path).

**Residual risk:** You can still see **reconciliation / recovery loops** if journal and broker diverge for reasons other than instrument key (intent ownership, partial journal updates, external closes). **No new guarantee** that **`account_qty == journal_qty == 0`** immediately after every flatten; eventual consistency depends on reconciliation + journal rules.

---

## 6. Emergency / constraint paths — can flatten be impossible?

### Documented constraint

**`EmergencyFlatten`** returns failure and logs **`EMERGENCY_FLATTEN_CONSTRAINT_VIOLATION`** if **not** on the strategy thread (`NinjaTraderSimAdapter.NT.cs` ~366–374).

### Paths that must hit strategy thread for real flatten

- **`NtFlattenInstrumentCommand`** → **`DrainNtActions`** (intended design).
- **`EmergencyFlatten`** when called **from** `blockInstrumentCallback` during **`OnVerifyPendingFlattens`** runs inside **`DrainNtActions`** → **strategy context** should be set → **can** succeed.

### Remaining gap

Any code path that calls **`EmergencyFlatten`** or **`SubmitFlattenOrder`** **without** strategy thread context **cannot** flatten via that API. The system does **not** universally guarantee “if we detect risk, we always flatten” — it guarantees **correct thread** for those entry points.

**Also:** If **`nativeInstrumentForBrokerOrder`** is null and **`_ntInstrument`** is null, **`SubmitFlattenOrder`** fails with **NT context not set** — separate failure mode.

---

## 7. Logging — “truth snapshot”

**Already partially present** on verify pass/fail:

- **`FLATTEN_BROKER_FLAT_CONFIRMED`** / **`FLATTEN_BROKER_POSITION_REMAINS`**: `canonical_broker_key`, `reconciliation_abs_remaining`, `broker_position_rows`, `host_chart_instrument`.

**Optional improvement (not implemented here):** One consolidated **`FLATTEN_VERIFY_TRUTH_SNAPSHOT`** after every verify cycle (including pass) with `last_action_taken` — would reduce grep time for operators.

---

## 8. Operator rule (post-change)

| Signal | Use |
|--------|-----|
| **`NT_ACTION_SUCCESS`** | **Do not** infer exposure flat. |
| **`FLATTEN_COMMAND_COMPLETED`** | Delegate finished without exception; **not** flat proof. |
| **`FLATTEN_BROKER_FLAT_CONFIRMED`** | Canonical bucket **0** after verify window. |
| **`FLATTEN_BROKER_POSITION_REMAINS`** | Still exposed (or timing artifact); retries may follow. |
| **`FLATTEN_FAILED_PERSISTENT`** | Terminal verify path for that pending chain; instrument block / stand-down **on this engine**. |

---

## Prioritized follow-ups (if you change code later)

| Tier | Item |
|------|------|
| **1** | **Cross-chart flatten serialization** (one active flatten per canonical key per account) if multiple strategies share an account. |
| **1** | **Persistence debounce** for verify failure (non-zero must hold across 2+ windows) to reduce false recovery churn. |
| **2** | **Explicit `FAIL_CLOSED` / health** taxonomy distinct from IEA-queue `blockInstrument` reuse (clarity). |
| **3** | **Single “truth snapshot”** event as in §7. |

---

*References: `NinjaTraderSimAdapter.cs`, `NinjaTraderSimAdapter.NT.cs` (`OnVerifyPendingFlattens`), `InstrumentExecutionAuthority.Flatten.cs` (latch), `RobotEngine.cs` (`blockInstrumentCallback`), `InstrumentExecutionAuthority.RecoveryPhase3.cs` (`OnRecoveryFlattenResolved`).*
