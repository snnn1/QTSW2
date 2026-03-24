# IEA no-progress guard — blocker analysis (instrumentation + validation)

**Date:** 2026-03-24  
**Scope:** Runtime diagnosis of why `IEA_ADOPTION_SCAN_SKIPPED_NO_PROGRESS` may never appear, without changing skip safety.

---

## 1. New diagnostic event

**`IEA_ADOPTION_SCAN_NO_PROGRESS_NOT_SKIPPED`** (INFO, rate-limited)

- **Emit when** (all true):
  - `scan_request_source == RecoveryAdoption`
  - Prior completed recovery snapshot exists (`has_last_completed_recovery_scan`)
  - `last_completed_adopted_delta == 0` on that snapshot
  - Current vs previous recovery fingerprint: **0 or 1** field mismatch (`fingerprint_field_mismatch_count` ≤ 1)
  - Heavy scan is **about to run** (the normal no-progress skip path did **not** return early)

- **Rate limit:** ~**50 s** per **`iea_instance_id` + `skip_blocked_reason`** (key includes reason so different blockers are not collapsed).

- **Payload (clause-level):**
  - `has_last_completed_recovery_scan`
  - `last_completed_adopted_delta_zero`
  - `fingerprint_equal`
  - `fingerprint_field_mismatch_count`
  - `cooldown_positive`
  - `last_completed_utc_valid`
  - `within_cooldown`
  - `last_iea_mutation_lte_last_completed`
  - `skip_blocked_reason` — one of:
    - `no_last_completed`
    - `last_adopted_delta_nonzero`
    - `fingerprint_changed`
    - `cooldown_expired`
    - `mutation_after_last_completed`
    - `non_recovery_source` *(reserved if snapshot builder used off recovery path)*
    - `other`
  - `scan_request_source`, `execution_instrument_key`, `iea_instance_id`, `adoption_scan_episode_id`
  - `seconds_since_last_completed`, `cooldown_sec`
  - `last_scan_completed_utc`, `last_iea_mutation_utc` (ISO-8601, nullable)
  - **Current** fingerprint fields: `current_*`
  - **Previous** snapshot fingerprint fields: `prev_*`

**Skip logic is unchanged.** This event only adds observability.

---

## 2. How to answer the product questions (after one real session)

| Question | Method |
|----------|--------|
| **Which clause blocks skip most often?** | Count `skip_blocked_reason` in `logs/robot/robot_ENGINE*.jsonl` (and rotated archives). |
| **Is `lastIeaMutationUtc > lastCompletedUtc` the main blocker?** | Share of `skip_blocked_reason == "mutation_after_last_completed"` among `IEA_ADOPTION_SCAN_NO_PROGRESS_NOT_SKIPPED` when `fingerprint_equal == true`. |
| **Are fingerprints stable enough?** | When `fingerprint_field_mismatch_count == 0` but scan still runs → block is **not** fingerprint drift. When count `== 1` → `fingerprint_changed` dominates; compare `current_*` vs `prev_*`. |
| **Cooldown too short / long / irrelevant?** | If `skip_blocked_reason == "cooldown_expired"` with `fingerprint_equal == true` and mutation OK → lengthening cooldown could reduce scans (policy decision later). If almost never appears → cooldown not the limiter. |
| **Is the guard reached?** | Presence of **`IEA_ADOPTION_SCAN_NO_PROGRESS_NOT_SKIPPED`** proves the recovery path evaluated skip and proceeded to heavy scan under “close” conditions. No events → either no qualifying recovery bursts, or fingerprint usually differs by **≥2** fields. |

**Aggregation one-liner (PowerShell, from repo root):**

```powershell
Select-String -Path "logs\robot\robot_ENGINE*.jsonl" -SimpleMatch "IEA_ADOPTION_SCAN_NO_PROGRESS_NOT_SKIPPED" | Measure-Object
```

Parse JSON lines for `data.skip_blocked_reason` for histograms.

---

## 3. Code reference: what updates `LastMutationUtc`?

In `InstrumentExecutionAuthority.cs`, **`_lastMutationUtc` is set to `DateTimeOffset.UtcNow` after every successful `work()` completion on the IEA worker loop** (not only registry/adoption mutations):

```csharp
work();
_lastMutationUtc = DateTimeOffset.UtcNow;
```

**Implications for `mutation_after_last_completed`:**

1. **Any** queued work item finishing before the next recovery adoption scan advances `LastMutationUtc`.
2. **Ordering:** `_lastRecoveryScanCompletedUtc` is set inside `RunGatedAdoptionScanBody`’s `finally` using `NowEvent()`; `LastMutationUtc` is updated **after** the delegate returns, typically with **`DateTimeOffset.UtcNow`**. If that timestamp is **strictly greater** than `last_scan_completed_utc` recorded in the same scan, the next recovery scan can see **`last_iea_mutation_lte_last_completed: false`** even with **no other work** — i.e. potential **clock / ordering noise**, not necessarily a material registry change.

**Decision rule (per user):** Do **not** weaken safety until runtime counts show the **dominant** `skip_blocked_reason`. If the dominant blocker is `mutation_after_last_completed`, the next engineering step is to **classify** whether timestamps reflect true state change vs worker-loop bookkeeping — **not** to relax the comparison blindly.

---

## 4. Placeholder results (pre–runtime)

| Metric | Status |
|--------|--------|
| Histogram of `skip_blocked_reason` | **Pending** — deploy DLL, capture session, paste counts here. |
| Dominant blocker | **TBD** |
| `mutation_after_last_completed` with `fingerprint_equal` | **TBD** |

After you have logs, append a **Section 5 — Measured results** with tables and 2–3 sentences on the dominant clause and whether mutation is likely noise vs real churn.

---

## 5. Related code / docs

- Skip evaluator: `modules/robot/core/Execution/RecoveryNoProgressSkipEvaluator.cs`
- Fingerprint mismatch count: `AdoptionScanRecoveryFingerprint.CountFieldMismatches`
- Emission site: `InstrumentExecutionAuthority.NT.cs` → `MaybeLogAdoptionScanNoProgressNotSkipped`
- Prior audit: `docs/robot/audits/IEA_NO_PROGRESS_GUARD_2026-03-24.md`, `IEA_POSTFIX_VALIDATION_2026-03-24.md`
