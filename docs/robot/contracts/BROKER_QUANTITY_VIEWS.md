# Broker quantity views (contract)

Three **different** ways the codebase derives “broker vs journal” exposure. They answer different questions and **must not be compared as if they were the same number**.

---

## 1. Parity view (integrity)

| | |
|---|---|
| **Implementation** | `JournalParityChecker.CheckJournalParity` (`JournalIntegrityGuarantee.cs`) |
| **Keying** | Single `instrument` string plus optional `canonicalInstrument` for **journal** bucket matching. **Broker positions** are summed with **`Abs(quantity)`** only for snapshot rows whose `Instrument` **exactly matches** that `instrument` (trimmed, case-insensitive). |
| **Scope** | One execution/call-site instrument at a time: orphan (non–robot-tagged) working orders, then position delta, then unexplained working orders using **IEA/registry** when enabled. |
| **Valid consumers** | `JournalIntegrityGuarantee.EnsureJournalIntegrity`, release/input audits that intentionally use the same keying, any new “is the journal consistent with the broker **for this instrument key**?” check. |
| **Invalid comparisons** | Mismatch-sweep totals for a **different** instrument string; family reconciliation totals; `StateConsistencyReleaseEvaluator` numeric inputs unless they were filled using this same keying. |

---

## 2. Mismatch-sweep view (coordinator / assembly)

| | |
|---|---|
| **Implementation** | `RobotEngine.AssembleMismatchObservations` (and observations fed into `MismatchEscalationCoordinator`) |
| **Keying** | **Per distinct** `PositionSnapshot.Instrument` / working-order instrument string: broker qty is accumulated into a dictionary keyed by that string. Journal side uses `GetOpenJournalQuantitySumForInstrumentFromMap` with **canonical** from engine policy (`GetCanonicalInstrument`). |
| **Scope** | **Union of instruments** present on the account or in open journal buckets—multi-row, multi-contract sweep. Feeds `MismatchObservation` and `MismatchClassification` (separate taxonomy from `JournalParityStatus`). |
| **Valid consumers** | Mismatch escalation, coordinator gate, recovery adoption scheduling tied to **per-instrument** observations, diagnostics that list all instruments with drift. |
| **Invalid comparisons** | Parity view for a **single** normalized key without mapping; family reconciliation **family** totals; assuming `BrokerQty` on an observation equals account-wide family exposure. |

---

## 3. Family reconciliation view (runner)

| | |
|---|---|
| **Implementation** | `ReconciliationRunner` (canonical **family** keys, `SumBrokerAbsForCanonicalFamily`, journal open qty with execution root / canonical mapping) |
| **Keying** | **Product family** (e.g. micro vs index future mapped to a canonical journal family), not necessarily one NT contract string. |
| **Scope** | Periodic / startup reconciliation: quantity alignment messaging, broker-flat journal closure paths, tagged repair hooks—**different aggregation** than per-string sweep or single-instrument parity. |
| **Valid consumers** | Reconciliation runner passes, `RECONCILIATION_QTY_MISMATCH`-style diagnostics that are explicitly family-scoped. |
| **Invalid comparisons** | Parity or mismatch-sweep numbers **without** applying the same family mapping; single-instrument integrity conclusions from runner totals alone. |

---

## Cross-view rules

1. **Do not dedupe** these into one function without a formal design: keying and failure modes differ on purpose.
2. New **integrity / fail-closed parity** questions should go through **`JournalParityChecker`** with explicit `(instrument, canonicalInstrument, registry view)` (see comments on `JournalParityChecker` in `JournalIntegrityGuarantee.cs`). Keep **mismatch-sweep** (`AssembleMismatchObservations`) and **family** (`ReconciliationRunner`) scopes distinct unless you formally redesign them.
3. **Instrumentation:** several gate and release-cache events include a string field **`instrumentation_source`** identifying the emitting layer (e.g. `MismatchEscalationCoordinator.*`, `ReleaseReconciliationRedundancySuppression.release_readiness_cache`). Use it when triaging progress vs suppression vs skip; recovery adoption no-progress skip is separate (IEA / `RecoveryNoProgressSkipEvaluator` — events vary by host).
