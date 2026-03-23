# IEA adoption / reconciliation hardening (stale & foreign broker orders)

## Problem addressed

- Multi-chart deployments share one **account**; each IEA scanned **all** QTSW2-tagged working orders.
- **Foreign-instrument** protectives (e.g. MNG intent tag on MES/MYM/MNQ) were classified as **UNOWNED** on every micro IEA → recovery / flatten pressure and CPU-heavy repeat work.
- **Semantic logging bug**: `RobotEvents.EngineBase(utcNow, "", instrument, "REAL_EVENT", payload)` put **instrument** into `event_type` and the real name into `state`, so JSONL was not queryable by real event names.

## Policy (exact rules)

1. **Instrument gate (first)**  
   Before tag decode cost, adoption, candidate lookup, or UNOWNED/stale classification:  
   `BrokerOrderMatchesExecutionInstrument(ExecutionInstrumentKey, brokerMasterInstrumentName)` must be true.  
   If false → **`FOREIGN_INSTRUMENT_QTSW2_ORDER_SKIPPED`** (sampled 1/50) + metric; **no** UNOWNED, **no** recovery from this IEA.

2. **Deferral / grace**  
   `qtsw2_working_same_instrument_count` replaces account-wide QTSW2 count so deferral does not trigger on **only** foreign-instrument orders.

3. **Classifications (same instrument)**  
   - Valid tag + `GetAdoptionCandidateIntentIds` contains intent → **`ADOPTION_CANDIDATE_FOUND`** then normal adopt + **`ADOPTION_SUCCESS`**.  
   - Else → **`ADOPTION_CANDIDATE_NOT_FOUND`** + **`STALE_QTSW2_ORDER_DETECTED`** (logged once per fingerprint episode).  
   - Register UNOWNED with source **`STALE_QTSW2_ORDER_DETECTED`** (not `UNOWNED_LIVE_ORDER_DETECTED`).  
   - **`RequestRecovery` / `RequestSupervisoryAction`**: only on **`unchangedStreak == 1`** for that fingerprint (first sighting), not every scan.

4. **Convergence**  
   - Fingerprint: `orderState|inRegistry|hasCandidate|role|intentId`  
   - **`AdoptionConvergenceUnchangedThreshold` = 4** unchanged evaluations → **`ADOPTION_NON_CONVERGENCE_ESCALATED`** once + quarantine **`AdoptionConvergenceCooldownSeconds` = 120** s.  
   - While quarantined: skip heavy path; increment **`suppressed_rechecks_total`**.

5. **`TryAdoptBrokerOrderIfNotInRegistry`**  
   Same instrument gate at entry; **no** adopt/unowned for foreign instrument.

6. **Logging**  
   - `LogIeaEngine` → `EngineBase(..., eventType: "<SEMANTIC>", state: "ENGINE", ...)`  
   - IEA **Recovery / Bootstrap / Supervisory / Flatten** partials: positional `EngineBase` bugs fixed the same way (script + manual guard fix).

## Metrics (cumulative per IEA instance)

Logged on **`ADOPTION_SCAN_START`** / **`ADOPTION_SCAN_SUMMARY`**:

- `scanned_orders_total`  
- `skipped_foreign_instrument_orders_total`  
- `stale_qtsw2_orders_total`  
- `successful_adoptions_total`  
- `non_convergent_orders_total` (escalations)  
- `suppressed_rechecks_total`  

Per-scan deltas on **`ADOPTION_SCAN_SUMMARY`**.

## Files touched

| Area | Path |
|------|------|
| Instrument gate | `modules/robot/core/Execution/AdoptionScanInstrumentGate.cs` |
| Convergence | `modules/robot/core/Execution/AdoptionReconciliationConvergence.cs` |
| NT project link | `RobotCore_For_NinjaTrader/Robot.Core.csproj` |
| IEA core | `RobotCore_For_NinjaTrader/Execution/InstrumentExecutionAuthority.cs` |
| Adoption scan + registry adopt | `RobotCore_For_NinjaTrader/Execution/InstrumentExecutionAuthority.NT.cs` |
| EngineBase fixes | `InstrumentExecutionAuthority.RecoveryPhase3.cs`, `BootstrapPhase4.cs`, `SupervisoryPhase5.cs`, `Flatten.cs` |
| Event registry | `modules/robot/core/RobotEventTypes.cs`, `RobotCore_For_NinjaTrader/RobotEventTypes.cs` |
| Tests + harness | `modules/robot/core/Tests/AdoptionScanHardeningTests.cs`, `modules/robot/harness/Program.cs` |
| One-off fix script | `tools/_fix_iea_enginebase.py` (optional delete) |

## Tests

```bash
dotnet run --project modules/robot/harness/Robot.Harness.csproj -- --test ADOPTION_SCAN_HARDENING
```

Covers: foreign skip, stale convergence, fingerprint reset, non-convergence quarantine, correct `EngineBase` `event_type`.

## Operator notes

- Query JSONL by **`event` / `event_type`** for: `ADOPTION_SCAN_START`, `STALE_QTSW2_ORDER_DETECTED`, `ADOPTION_NON_CONVERGENCE_ESCALATED`, `FOREIGN_INSTRUMENT_QTSW2_ORDER_SKIPPED`, etc.
- Recovery may arrive with reason **`STALE_QTSW2_ORDER_DETECTED`** instead of **`UNOWNED_LIVE_ORDER_DETECTED`** for same-instrument orphan QTSW2 protectives without a journal candidate on **that** IEA.
- Fail-closed: first sighting of a stale same-instrument order still triggers **one** recovery + supervisory escalation; repeats are suppressed until convergence cooldown or fingerprint change.
