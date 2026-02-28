# NT Thread Guard & Flatten Verification Canary Validation

## Purpose

Validate that the runtime guard and flatten verification behave correctly under controlled scenarios.

---

## Canary A: NT Thread Safety (Guard from Worker)

### Objective

Prove that when a guarded NT API method is called from a non-strategy thread (IEA worker), the guard:
1. Emits `NT_THREAD_VIOLATION` (CRITICAL)
2. Enqueues the action for strategy thread
3. Does NOT call the NT API directly

### Validation Steps

1. **Trigger path**: Call `IIEAOrderExecutor.CancelOrders(orders)` from a thread that has NOT called `EnterStrategyThreadContext()`. This simulates the IEA worker calling the adapter.

2. **Expected behavior**:
   - Log event `NT_THREAD_VIOLATION` with `method = "CancelOrders"`
   - Action enqueued (check `NT_ACTION_ENQUEUED` with `action_type = "DEFERRED"`)
   - No `account.Cancel` executed on the worker thread
   - On next strategy-thread drain, the deferred action runs

3. **How to run** (manual/integration):
   - Enable IEA in config
   - From IEA's aggregation path (which runs on worker), it calls `Executor.CancelOrders(ordersToCancel)`
   - The adapter's `CancelOrders` hits the guard (no EnterStrategyThreadContext on worker)
   - Grep logs for `NT_THREAD_VIOLATION` — should appear if guard is hit
   - To force: temporarily add a test code path that calls `CancelOrders` from `Task.Run` without entering context

4. **Automated test** (when NT available):
   ```csharp
   // Use NtThreadGuardCanary.RunFromWorkerThreadAsync from QTSW2.Robot.Core.Tests
   var (violationFired, error) = await NtThreadGuardCanary.RunFromWorkerThreadAsync(adapter, orders);
   Assert.True(violationFired, "Guard violation callback must fire when CancelOrders called from worker");
   // Adapter.SetGuardViolationCallback captures the method name when guard triggers
   ```

---

## Canary B: Flatten Verification Escalation

### Objective

Prove that when flatten is requested but position does not go flat:
1. `FLATTEN_VERIFY_FAIL` is emitted after deadline
2. Retries are enqueued (up to `FLATTEN_VERIFY_MAX_RETRIES`)
3. After max retries, `FLATTEN_FAILED_PERSISTENT` is emitted
4. Stand down + block instrument callbacks are invoked

### Validation Steps

1. **Trigger path**: Enqueue a flatten command, but simulate/mock that `account.GetPosition` still returns non-zero (or temporarily stub `FlattenIntentReal` to skip the actual flatten).

2. **Expected sequence**:
   - `FLATTEN_REQUESTED` → `FLATTEN_SUBMITTED` (or stub to skip)
   - After `FLATTEN_VERIFY_WINDOW_SEC` (4s), `OnVerifyPendingFlattens` runs
   - Position != 0 → `FLATTEN_VERIFY_FAIL`
   - Retry enqueued → `FLATTEN_REQUESTED` (retry 1)
   - Repeat until `FLATTEN_VERIFY_MAX_RETRIES` (4)
   - Final: `FLATTEN_FAILED_PERSISTENT`, `_standDownStreamCallback`, `_blockInstrumentCallback`

3. **How to run** (manual):
   - Add a temporary config flag `force_flatten_verify_fail` that makes `OnVerifyPendingFlattens` treat position as non-zero
   - Enqueue a flatten (e.g. via orphan fill path)
   - Wait for verification window
   - Confirm `FLATTEN_VERIFY_FAIL` → retries → `FLATTEN_FAILED_PERSISTENT`

4. **Automated test** (when NT available):
   - Use a mock/stub that makes `GetPosition` return 1 (non-flat)
   - Register pending verification manually
   - Call `OnVerifyPendingFlattens` repeatedly (simulating ticks) until `FLATTEN_FAILED_PERSISTENT`
   - Assert stand down and block callbacks were invoked

---

## Invariant Checks (Ongoing)

| Check | Location | Expected |
|-------|----------|----------|
| Enter/Exit in try/finally | OnMarketData, OnBarUpdate | Yes |
| Thread ID matches | EnsureStrategyThreadOrEnqueue | `_strategyThreadId == CurrentThread.ManagedThreadId` |
| Ref-count for nested Enter/Exit | SetStrategyThreadContext | Count tracks nesting |
| Cancel before flatten | ExecuteFlattenInstrument | `CancelRobotOwnedWorkingOrdersReal` called first |
| 1-sec drain heartbeat | OnBarUpdate BIP 1 | Drain runs on 1-sec series during low liquidity |
| CorrelationId dedupe | StrategyThreadExecutor | `CANCEL:{intentId}:POSITION_FLAT_CLEANUP` dedupes |
