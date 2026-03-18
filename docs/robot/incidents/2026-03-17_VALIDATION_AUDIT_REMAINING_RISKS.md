# Validation Audit — Remaining Risks (Targeted)

**Date**: 2026-03-17  
**Scope**: 4 areas only — verify risk reality, hidden protections, overestimated risks  
**No broad audit. No new features. Verification only.**

---

## A. RISK REALITY TABLE

| Area | Real Risk? | Why | Action Needed |
|------|------------|-----|---------------|
| **1. BE / Intent Integrity** | **No** (for robot positions) | `GetActiveIntentsForBEMonitoring` uses `IsSameInstrument(intent.ExecutionInstrument, executionInstrument)` — M2K↔RTY, MES↔ES, MNQ↔NQ resolve. `HydrateIntentsFromOpenJournals` runs before bootstrap and populates IntentMap from journal. For robot positions, intents are hydrated and match. `BE_FILTER_EXCLUDED_ACTIVE_EXPOSURE` fires when `hasExposure && activeCount==0` — that is (a) manual position (no intent; correct to not run BE), or (b) journal not loaded/corrupt (rare). | MONITOR |
| **2. Bootstrap Late Broker Visibility** | **No** | `TryRecoveryAdoption` runs in `AssembleMismatchObservations` **before** adding any observation when `brokerWorking > 0 && effectiveLocalWorking == 0`. Reconciliation ticks every 1s. If adoption succeeds (`localAfter == brokerWorking`), we `continue` — no observation, no escalation. Fail-closed requires 30s persistence. Adoption is attempted every tick. No additional trigger required. | IGNORE |
| **3. Journal Dependency Risk** | **Yes** (narrow) | If journal is missing or incorrect (wrong path, corrupt, `TradeCompleted` wrong), `GetAdoptionCandidateIntentIds` returns empty or excludes valid intents. Adoption fails → observation added → escalation → fail-closed. System blocks; `VerifyRegistryIntegrity` may trigger `RequestRecovery` → FLATTEN. Valid orders could be flattened if journal is wrong. Fail-safe: we block. Risk: we may flatten valid orders when journal is corrupt. | MONITOR |
| **4. Protective Audit Reliability** | **No** | `ProtectiveCoverageAudit.Audit` does **not** use `activeIntentIds` for the result. It uses broker position + broker working orders only. `robotProtectiveStops` = orders with `QTSW2:xxx:STOP` tag. `activeIntentIds` is used only for `PROTECTIVE_AUDIT_CONTEXT` logging (expectedCount). Audit result (PROTECTIVE_OK, PROTECTIVE_MISSING_STOP, etc.) is broker-truth based. No false negative from intent source. | IGNORE |

---

## B. HIDDEN PROTECTIONS

| Protection | Where | What It Does |
|------------|-------|--------------|
| **HydrateIntentsFromOpenJournals before bootstrap** | `NinjaTraderSimAdapter.SetNTContext` line 553 | Populates IntentMap from execution journal **before** bootstrap. Ensures BE has intents after restart when journal exists. |
| **TryRecoveryAdoption before observation** | `RobotEngine.AssembleMismatchObservations` lines 4800–4838 | Adoption attempted **before** adding ORDER_REGISTRY_MISSING to list. If `localAfter == brokerWorking`, we skip adding observation entirely — coordinator never sees it. |
| **Reconciliation every 1s** | `MismatchEscalationPolicy.MISMATCH_AUDIT_INTERVAL_MS = 1000` | Adoption retry every second. Broker orders appearing at any time get adoption attempt within 1s. |
| **Protective audit is broker-truth only** | `ProtectiveCoverageAudit.Audit` | Result based on `snapshot.Positions` and `snapshot.WorkingOrders`. No filtering by intent list. Stops are identified by tag pattern `QTSW2:xxx:STOP`. |
| **IsSameInstrument in BE filter** | `NinjaTraderSimAdapter.GetActiveIntentsForBEMonitoring` lines 2392–2396 | M2K↔RTY, MES↔ES, MNQ↔NQ canonicalized. Chart instrument vs intent ExecutionInstrument mismatch no longer excludes valid intents. |

---

## C. OVERESTIMATED RISKS

| Previously Labeled Risk | Reality |
|-------------------------|---------|
| **Bootstrap timing requires additional trigger** | TryRecoveryAdoption already runs every reconciliation tick. No extra trigger needed. |
| **BE_FILTER_EXCLUDED_ACTIVE_EXPOSURE = unmanaged exposure** | For robot positions: intents are hydrated; IsSameInstrument fixes alias. For manual positions: we correctly do not run BE. The log is a diagnostic; it does not imply a bug. |
| **Protective audit false negatives from intent source** | Audit does not use activeIntentIds for the result. It is broker-truth based. Intent source is for logging only. |
| **Delayed broker visibility causes incorrect fail-closed** | Adoption runs every 1s. When broker orders appear, adoption is attempted. Valid QTSW2 orders with correct journal are adopted. Fail-closed only when adoption fails (no adoptable evidence). |

---

## D. FINAL RECOMMENDATION

| Area | Recommendation | Rationale |
|------|----------------|------------|
| **1. BE / Intent Integrity** | **MONITOR** | IsSameInstrument and hydration address the main case. BE_FILTER_EXCLUDED_ACTIVE_EXPOSURE on manual positions is expected. If it appears for robot positions, investigate journal path/hydration. |
| **2. Bootstrap Late Broker Visibility** | **IGNORE** | TryRecoveryAdoption fully covers. No code change needed. |
| **3. Journal Dependency Risk** | **MONITOR** | Real but narrow. Journal corruption/wrong path can cause valid orders to be treated as unowned → fail-closed. Mitigation: ensure journal path correct, add diagnostic when adoption fails with empty candidates. No immediate fix required; risk is acceptable for normal operation. |
| **4. Protective Audit Reliability** | **IGNORE** | Audit is broker-truth based. Intent source is for context only. Trustworthy. |

---

## CODE PATH REFERENCES (Verification)

| Area | File | Key Logic |
|------|------|-----------|
| BE intent filter | `NinjaTraderSimAdapter.cs` 2392–2396 | `IsSameInstrument(intent.ExecutionInstrument, executionInstrument)` |
| Hydration | `NinjaTraderSimAdapter.cs` 566–603 | `HydrateIntentsFromOpenJournals` before bootstrap |
| TryRecoveryAdoption | `RobotEngine.cs` 4800–4838 | Before adding observation; `continue` if `localAfter == brokerWorking` |
| Adoption candidates | `ExecutionJournal.cs` 1487–1533 | `EntrySubmitted && !TradeCompleted`; instrument match |
| Protective audit | `ProtectiveCoverageAudit.cs` 62–156 | Uses `positions`, `workingOrders`; `activeIntentIds` not in result logic |
| Reconciliation interval | `MismatchEscalationModels.cs` 87 | `MISMATCH_AUDIT_INTERVAL_MS = 1000` |
