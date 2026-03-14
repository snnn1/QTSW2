# Phase 4 Bootstrap: Final Verification Pass

Verification focused on: bootstrap guard coverage, bootstrap/recovery interaction rules, hydration vs snapshot timing.

## 1. Bootstrap Guard Coverage

### Current State

| Guard Point | Location | Behavior | Status |
|-------------|----------|----------|--------|
| **Enqueue** | InstrumentExecutionAuthority.cs L149 | When `IsInRecovery`, returns early without adding work. **Blocks execution updates, order updates, BE evaluation.** | ⚠️ **Over-blocking** |
| **EnqueueAndWait** | InstrumentExecutionAuthority.cs L194 | When `IsInRecovery`, returns (false, default). Blocks entry submission, flatten. | ✅ Correct |
| **GuardNormalManagement** | RecoveryPhase3.cs L343 | Emits event when in recovery. **Not called by any caller** – available but unused. | ⚠️ Unused |

### Issue: Execution Updates Dropped During Recovery

When `IsInRecovery` is true, `Enqueue` returns without adding work. That affects:

- **EnqueueExecutionUpdate** – execution updates (fills, order state) are **dropped**
- **EnqueueOrderUpdate** – order updates are **dropped**
- **EvaluateBreakEvenCore** – BE evaluation is dropped (correct – we want to block BE)

**Problem:** Execution and order updates are recovery-essential. We need them for:

1. **Flatten fills** – recovery flatten cannot complete if we never see the fill
2. **Stale snapshot detection** – critical events during bootstrap are detected inside the worker; if we drop execution updates, we never run that logic
3. **Registry lifecycle** – order state changes must update the registry

**Fix applied:** Added `EnqueueRecoveryEssential` that bypasses `IsInRecovery`. Used for `EnqueueExecutionUpdate`, `EnqueueOrderUpdate`, and `DeferUnresolvedExecution`. `Enqueue` still blocks for BE evaluation and other normal-management work.

### Guard Coverage Summary

- **EnqueueAndWait** – correctly blocks entry submission, flatten during recovery ✅
- **Enqueue** – blocks BE evaluation, normal management ✅
- **EnqueueRecoveryEssential** – allows execution/order updates during recovery ✅
- **GuardNormalManagement** – not wired; consider calling from RiskGate/StreamStateMachine for explicit guard points

---

## 2. Bootstrap/Recovery Interaction Rules

### State Transitions

Bootstrap states integrate with Phase 3:

- `NORMAL → BOOTSTRAP_PENDING → SNAPSHOTTING → BOOTSTRAP_DECIDING → (RESOLVED | RECOVERY_ACTION_REQUIRED | HALTED | BOOTSTRAP_ADOPTING)`
- `BOOTSTRAP_ADOPTING → RESOLVED`
- `BOOTSTRAP_DECIDING → SNAPSHOTTING` (rerun on stale)

### Interaction Rules

| Rule | Implementation | Status |
|------|----------------|--------|
| Bootstrap FLATTEN routes through Phase 3 | `ExecuteRecoveryFlatten` called from `ProcessBootstrapResult` | ✅ |
| Bootstrap ADOPT runs ScanAndAdopt then RESOLVED | `RunBootstrapAdoption` → `OnBootstrapAdoptionCompleted` | ✅ |
| Bootstrap HALT transitions to HALTED | `ProcessBootstrapResult` sets `_recoveryState = HALTED` | ✅ |
| `IsInRecovery` includes bootstrap states | `state != NORMAL` | ✅ |
| `IsInBootstrap` subset of `IsInRecovery` | BOOTSTRAP_* states all != NORMAL | ✅ |

### Flatten Latch

Flatten latch prevents repeated flatten requests. `CanResumeNormalExecution` and `CanCompleteBootstrap` both check `_flattenLatchByInstrument`. ✅

---

## 3. Hydration vs Snapshot Timing

### Current Order (SetNTContext)

1. IEA binding
2. **BeginBootstrapForInstrument** (invokes callback synchronously)
3. Callback: TryTransitionToSnapshooting, gather broker/journal/registry/**runtime**
4. **HydrateIntentsFromOpenJournals** (runs after bootstrap callback returns)

### Issue: Runtime Snapshot Before Hydration

`GetRuntimeIntentSnapshotForRecovery` reads from `IntentMap`:

```csharp
foreach (var kvp in IntentMap)
{
    if (OrderMap.TryGetValue(kvp.Key, out var oi) && (oi.State == "WORKING" || ...))
        activeIntents.Add(kvp.Key);
}
```

`IntentMap` is populated by `HydrateIntentsFromOpenJournals` via `RegisterIntent`. When bootstrap runs **before** hydration, `IntentMap` is **empty**. Therefore:

- Runtime snapshot has no active intents
- `ScanAndAdoptExistingProtectives` (ADOPT path) uses `GetActiveIntentsForBEMonitoring`, which uses `IntentMap` + journal – with empty `IntentMap`, adoption cannot match broker orders to intents

**Fix:** Run `HydrateIntentsFromOpenJournals` **before** `BeginBootstrapForInstrument`. That ensures:

1. Runtime snapshot reflects journal-derived intents
2. ADOPT path can match broker protectives to hydrated intents

### Correct Order

1. IEA binding
2. **HydrateIntentsFromOpenJournals** (populate IntentMap from journal)
3. **BeginBootstrapForInstrument** (snapshot now has correct runtime view; ADOPT can match)

---

## 4. Fixes Applied

1. **Enqueue recovery-essential work:** Added `EnqueueRecoveryEssential`. Used for `EnqueueExecutionUpdate`, `EnqueueOrderUpdate`, `DeferUnresolvedExecution`. `Enqueue` still blocks for BE and normal management.

2. **Hydration before bootstrap:** In `SetNTContext`, `HydrateIntentsFromOpenJournals` now runs before `BeginBootstrapForInstrument`.

3. **Optional:** Wire `GuardNormalManagement` at key call sites (e.g., RiskGate, StreamStateMachine) for explicit guard logging, if not redundant with Enqueue/EnqueueAndWait.

---

## 5. Verification Checklist

| Item | Status |
|------|--------|
| Bootstrap guard blocks normal management | ✅ Enqueue blocks BE; EnqueueRecoveryEssential allows fills/order updates |
| Recovery-essential work (fills, order updates) allowed during recovery | ✅ Via EnqueueRecoveryEssential |
| Bootstrap FLATTEN uses canonical RequestFlatten | ✅ |
| Bootstrap ADOPT uses ScanAndAdopt with strong evidence | ✅ Hydration before bootstrap |
| Hydration before snapshot for correct runtime view | ✅ Fixed |
| Stale snapshot detection can run (execution updates processed) | ✅ Via EnqueueRecoveryEssential |
| Flatten latch prevents loops | ✅ |
| State transitions valid | ✅ |
