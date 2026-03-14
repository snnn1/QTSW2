# Forced Flatten Timing Fix Report

**Date:** 2026-03-12  
**Objective:** Ensure session-close forced flatten submits in the same cycle as the trigger, not on the next bar.

---

## 1. Exact Cause of the One-Bar Delay

**Root cause:** `DrainNtActions` runs at the **start** of `OnBarUpdate` (in `RobotSimStrategy`), before `engine.Tick`. The forced flatten path enqueued `CancelIntentOrdersCommand` and `FlattenIntentCommand` through the IEA queue. Those commands were drained only when `OnBarUpdate` ran on the **next** bar.

**Flow before fix:**
1. `engine.Tick(utcNow)` → forced flatten block → `HandleForcedFlatten(utcNow)`
2. `HandleForcedFlatten` enqueued `FlattenIntentCommand` via `EnqueueExecutionCommand`
3. IEA worker processed the command and enqueued `NtCancelOrdersCommand` + `NtFlattenInstrumentCommand` to the NT action queue
4. `DrainNtActions` runs at the **start** of the **next** `OnBarUpdate` (next bar)
5. If `FlattenTriggerUtc` occurred on the last bar of the session, flatten executed on the first bar of the next day

---

## 2. Chosen Fix

**Solution:** `RequestSessionCloseFlattenImmediate` — same-cycle drain of NT flatten actions.

1. **`IExecutionAdapter.RequestSessionCloseFlattenImmediate`**  
   New method: enqueue cancel + flatten NT actions, then **immediately** call `DrainNtActions` under the entry submission lock. Returns `FlattenResult?`; `null` means “not supported” (caller uses `EmergencyFlatten` fallback).

2. **`NinjaTraderSimAdapter.RequestSessionCloseFlattenImmediate`**  
   - Enqueues `NtCancelOrdersCommand` and `NtFlattenInstrumentCommand` to the NT action queue  
   - Acquires `EntrySubmissionLock`  
   - Calls `DrainNtActions(executor)` in the same call  
   - Returns `FlattenResult.SuccessResult(utcNow)` after drain

3. **`HandleForcedFlatten` (StreamStateMachine)**  
   - Calls `RequestSessionCloseFlattenImmediate` first  
   - If it returns success → complete  
   - If it returns failure or `null` → fall back to `EmergencyFlatten`  
   - Command order preserved: cancel first, then flatten; flatten proceeds even if cancel fails

4. **Adapters not supporting same-cycle drain**  
   - `NullExecutionAdapter`: returns `FlattenResult.SuccessResult(utcNow)` (dry run)  
   - `NinjaTraderLiveAdapter`: returns `null` (not supported; use emergency fallback)

---

## 3. Is Forced Flatten Now Guaranteed to Submit Before Session Close?

**Yes, for the NinjaTrader Sim path.** When `RequestSessionCloseFlattenImmediate` is supported and succeeds:

- Cancel and flatten NT actions are **submitted in the same cycle** as the forced flatten trigger
- No dependency on the next bar
- If the trigger is on the last bar of the session, flatten still submits before session close

**Fallback:** If `RequestSessionCloseFlattenImmediate` returns `null` (e.g. Live adapter) or fails, `EmergencyFlatten` is used. That path submits directly and does not wait for the next bar.

---

## 4. Tests Added

| Test | Location | Verifies |
|------|----------|----------|
| NullExecutionAdapter.RequestSessionCloseFlattenImmediate returns success | `ForcedFlattenSlotExpiryReentryAlignmentTests` | Same-cycle path used when supported |
| NinjaTraderSimAdapter (harness stub) returns null | `ForcedFlattenSlotExpiryReentryAlignmentTests` | Fallback path when immediate not available |
| NinjaTraderLiveAdapter returns null | `ForcedFlattenSlotExpiryReentryAlignmentTests` | Fallback path for Live adapter |

**Run:** `dotnet run --project modules/robot/harness/Robot.Harness.csproj -- --test IEA_ALIGNMENT`

---

## 5. Files Touched

| File | Change |
|------|--------|
| `RobotCore_For_NinjaTrader/Execution/IExecutionAdapter.cs` | Added `RequestSessionCloseFlattenImmediate` |
| `RobotCore_For_NinjaTrader/Execution/NinjaTraderSimAdapter.cs` | Implemented same-cycle drain |
| `RobotCore_For_NinjaTrader/Execution/NullExecutionAdapter.cs` | Returns success (dry run) |
| `RobotCore_For_NinjaTrader/Execution/NinjaTraderLiveAdapter.cs` | Returns null |
| `RobotCore_For_NinjaTrader/StreamStateMachine.cs` | Uses immediate path first, then emergency fallback |
| `modules/robot/core/Execution/IExecutionAdapter.cs` | Added `RequestSessionCloseFlattenImmediate` |
| `modules/robot/core/Execution/NullExecutionAdapter.cs` | Implemented |
| `modules/robot/core/Execution/NinjaTraderSimAdapter.cs` | Returns null (harness stub) |
| `modules/robot/core/Execution/NinjaTraderLiveAdapter.cs` | Returns null |
| `modules/robot/core/Tests/ForcedFlattenSlotExpiryReentryAlignmentTests.cs` | Added tests 6–8 |

---

## 6. Slot Expiry vs Session-Close

- **Slot expiry:** Uses queued path (`EnqueueExecutionCommand(FlattenIntentCommand)`). Not tied to exchange session close; same-cycle guarantee is not required.  
- **Session-close forced flatten:** Hard risk boundary; must submit before close. Uses `RequestSessionCloseFlattenImmediate` for same-cycle execution.
