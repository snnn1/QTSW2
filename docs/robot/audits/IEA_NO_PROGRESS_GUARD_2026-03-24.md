# IEA recovery adoption — no-progress early-exit guard (2026-03-24)

## Problem

Repeated **RecoveryAdoption** adoption scans were executing the full `ScanAndAdoptExistingOrders` path every time recovery logic re-requested a scan, even when **broker + IEA state relevant to adoption was unchanged** and the previous recovery scan had **adopted nothing** (`adopted_delta == 0`). Each scan was **~5–9s**, tying up the IEA worker and contributing to **EXECUTION_COMMAND_STALLED** when the command pipeline waited behind redundant work.

## Approach

A **minimal, fail-safe guard** runs **only** inside the gated adoption scan body, **after** a cheap fingerprint is built and **before** `ScanAndAdoptExistingOrders()`.

- **Non-recovery** sources (`Bootstrap`, `FirstExecutionUpdate`, `DeferredRetry`, `Other`) **never** use this skip — they never call `ShouldSkipRecoveryNoProgress`.
- If fingerprint cannot be built (`TryBuildRecoveryScanFingerprint` fails), we **do not skip** (uncertainty → run scan).

## Fingerprint (`AdoptionScanRecoveryFingerprint`)

Immutable struct; value equality. Built in **one pass** over `account.Orders` (already required for counts), plus counts already available from the executor/registry:

| Field | Meaning |
|--------|---------|
| `ExecutionInstrumentKey` | IEA execution instrument identity |
| `AccountOrdersTotal` | `account.Orders.Count` |
| `CandidateIntentCount` | `GetAdoptionCandidateIntentIds(ExecutionInstrumentKey).Count` |
| `SameInstrumentQtsw2WorkingCount` | Working/Accepted orders on this instrument with `QTSW2:` tag |
| `DeferredState` | `_adoptionDeferred` |
| `BrokerWorkingExecutionInstrumentCount` | Working/Accepted on execution instrument (broker view) |
| `IeaRegistryWorkingCount` | `GetOwnedPlusAdoptedWorkingCount()` at scan start |

No extra full-account iterations, no object hashing, no heavy allocations.

## Per-IEA snapshot (updated only after a real recovery scan completes)

Updated **only** when:

1. `scan_request_source == RecoveryAdoption`
2. Fingerprint at start was valid (`recoveryFpOk`)
3. `IEA_ADOPTION_SCAN_EXECUTION_COMPLETED` is emitted (i.e. the heavy body ran: `heavyScanExecuted`)

Stored fields:

- `_lastRecoveryScanFingerprint`
- `_lastRecoveryScanAdoptedDelta`
- `_lastRecoveryScanCompletedUtc`
- `_hasLastRecoveryScanSnapshot`

**Skipped** no-progress exits **do not** update this snapshot (no `COMPLETED` for that attempt).

## Early-exit boolean logic (`RecoveryNoProgressSkipEvaluator.ShouldSkipRecoveryNoProgress`)

Let `hasLast` = prior completed recovery scan recorded.

**Skip** iff **all** of:

1. `hasLast == true`
2. `lastCompletedAdoptedDelta == 0`
3. `current == lastCompleted` (fingerprint value equality, case-insensitive instrument key)
4. `cooldownSeconds > 0` and `lastCompletedUtc != DateTimeOffset.MinValue`
5. `(utcNow - lastCompletedUtc).TotalSeconds < cooldownSeconds` (default **20s** on IEA)
6. `lastIeaMutationUtc <= lastCompletedUtc` (any IEA mutation after the last completed scan forces a re-run)

Otherwise **run** the heavy scan (fail-safe).

**Call-site constraint:** the above is only invoked when `source == RecoveryAdoption` (non-recovery always runs full scan path).

## Events

| Event | Role |
|--------|------|
| `IEA_ADOPTION_SCAN_SKIPPED_NO_PROGRESS` | Proof that a recovery scan was skipped under the guard. Payload includes fingerprint fields, `last_scan_completed_utc`, `seconds_since_last_scan`, `scan_request_source`, `cooldown_sec`, `adoption_scan_episode_id`. **Rate limit:** ~**45s** per IEA for this event type. |
| `IEA_ADOPTION_SCAN_EXECUTION_COMPLETED` | Extended with nullable `recovery_fingerprint_at_start_*` fields when recovery fingerprint was captured, plus `last_scan_completed_utc_before_this_run` for context. |

## Expected impact

- **Fewer** `IEA_ADOPTION_SCAN_EXECUTION_COMPLETED` events for recovery when state is stuck and unchanged.
- **More** `IEA_ADOPTION_SCAN_SKIPPED_NO_PROGRESS` (bounded by rate limiter).
- **Lower** incidence of **EXECUTION_COMMAND_STALLED** attributed to recovery adoption CPU when the system is idle with respect to adoption.
- **Correctness:** unchanged when there is any fingerprint change, prior adoption progress, cooldown expiry, IEA mutation since last completion, or non-recovery scan — we **run** the scan.

## Tests

Harness:

`dotnet run --project modules/robot/harness/Robot.Harness.csproj -- --test IEA_ADOPTION_NO_PROGRESS`

Covers evaluator rules: first scan, delta &gt; 0, fingerprint change, cooldown expiry, mutation-after-last, invalid cooldown / MinValue time, case-insensitive key.

## Files touched (conceptual)

- `modules/robot/core/Execution/AdoptionScanRecoveryFingerprint.cs`
- `modules/robot/core/Execution/RecoveryNoProgressSkipEvaluator.cs`
- `RobotCore_For_NinjaTrader/Execution/InstrumentExecutionAuthority.NT.cs` — `RunGatedAdoptionScanBody`, fingerprint builder, skip log, `COMPLETED` payload
- `modules/robot/core/RobotEventTypes.cs`, `RobotCore_For_NinjaTrader/RobotEventTypes.cs` — `IEA_ADOPTION_SCAN_SKIPPED_NO_PROGRESS`
- `modules/robot/core/Tests/AdoptionScanNoProgressGuardTests.cs`
- `modules/robot/harness/Program.cs` — `--test IEA_ADOPTION_NO_PROGRESS`
- `RobotCore_For_NinjaTrader/Robot.Core.csproj` — linked shared files
