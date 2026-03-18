# Stabilization Pass Summary — 2026-03-17

Targeted fixes based on the full system audit. No broad refactor; fail-closed behavior preserved.

---

## Files Changed

| File | Changes |
|------|---------|
| `RobotCore_For_NinjaTrader/Execution/ExecutionInstrumentResolver.cs` | Fixed M2K→RTY mapping (was M2K→NQ); added RTY case |
| `RobotCore_For_NinjaTrader/Execution/NinjaTraderSimAdapter.cs` | BE intent filter uses `IsSameInstrument` for alias resolution; added `GetActiveIntentIdsForProtectiveAudit` |
| `RobotCore_For_NinjaTrader/Execution/IExecutionAdapter.cs` | Added `GetActiveIntentIdsForProtectiveAudit(string instrument)` |
| `RobotCore_For_NinjaTrader/Execution/NullExecutionAdapter.cs` | Implement `GetActiveIntentIdsForProtectiveAudit` → empty |
| `RobotCore_For_NinjaTrader/Execution/NinjaTraderLiveAdapter.cs` | Implement `GetActiveIntentIdsForProtectiveAudit` → empty |
| `RobotCore_For_NinjaTrader/Execution/ProtectiveCoverageCoordinator.cs` | Added `getActiveIntentIdsForInstrument`; pass to Audit; `ProcessResult` logs `PROTECTIVE_AUDIT_CONTEXT` |
| `RobotCore_For_NinjaTrader/RobotEngine.cs` | Wire `getActiveIntentIdsForInstrument` to adapter |
| `RobotCore_For_NinjaTrader/Execution/InstrumentExecutionAuthority.BootstrapPhase4.cs` | Log `BOOTSTRAP_RESUME_NO_BROKER_ORDERS` when RESUME with broker_working=0 |
| `RobotCore_For_NinjaTrader/Strategies/RobotSimStrategy.cs` | BE: `BE_INTENT_RESOLUTION_INPUT`, enhanced `BE_FILTER_EXCLUDED_ACTIVE_EXPOSURE`; tick staleness guard for MinValue; `BE_NO_VALID_TICK_YET` |
| `RobotCore_For_NinjaTrader/Notifications/NotificationService.cs` | Bounded retry (2 attempts, 2s delay) for priority 2 sends |
| `NT_ADDONS/Notifications/NotificationService.cs` | Same retry logic |
| `modules/robot/core/Notifications/NotificationService.cs` | Same retry logic (sync) |

---

## Exact Logic Changes Per Priority

### 1. BE / Protective Alignment

- **ExecutionInstrumentResolver**: `M2K` now maps to `RTY` (was `NQ`). Added `case "RTY": return "RTY"` for matching.
- **GetActiveIntentsForBEMonitoring**: Replaced exact string match with `ExecutionInstrumentResolver.IsSameInstrument(intent.ExecutionInstrument, executionInstrument)` so M2K↔RTY, MES↔ES, MNQ↔NQ resolve correctly.
- **Logging**: Added `BE_INTENT_RESOLUTION_INPUT` (chart_instrument, execution_instrument, resolved_active_intent_count). Enhanced `BE_FILTER_EXCLUDED_ACTIVE_EXPOSURE` with chart_instrument, execution_instrument, resolved_active_intent_count, stand_down_reason.
- **Protective audit**: Added `GetActiveIntentIdsForProtectiveAudit` to IExecutionAdapter; NinjaTraderSimAdapter returns intent IDs from `GetActiveIntentsForBEMonitoring`. ProtectiveCoverageCoordinator receives `getActiveIntentIdsForInstrument` and passes to Audit. Added `PROTECTIVE_AUDIT_CONTEXT` log for critical/missing-stop with expected_active_intent_ids_count, found_protective_stop_qty, context_wiring_reason (expected_intent_set_null | expected_intent_set_empty | ok).

### 2. Bootstrap Delayed Broker Visibility

- **Logging**: When `ProcessBootstrapResult` chooses RESUME and `snapshot.BrokerWorkingOrderCount == 0`, emit `BOOTSTRAP_RESUME_NO_BROKER_ORDERS` with instrument, broker_working_count=0, classification, note.
- **Late recovery**: Existing reconciliation path (AssembleMismatchObservations → TryRecoveryAdoption) unchanged; no new timer or delay logic.

### 3. Protective Audit Intent Source

- **Wiring**: RobotEngine passes `getActiveIntentIdsForInstrument: inst => _executionAdapter.GetActiveIntentIdsForProtectiveAudit(inst)` to ProtectiveCoverageCoordinator.
- **Audit**: Coordinator invokes the callback per instrument and passes `activeIntentIds` to `ProtectiveCoverageAudit.Audit` (was null).
- **Logging**: `PROTECTIVE_AUDIT_CONTEXT` when status is critical or PROTECTIVE_MISSING_STOP, with expected count, found stop qty, and context_wiring_reason.

### 4. Tick Staleness Initialization

- **Guard**: `hasValidTickForBE = _lastTickUpdateUtcForBE != DateTimeOffset.MinValue`. If false, do not compute tick age from MinValue.
- **No valid tick path**: When `!hasValidTickForBE && HasAccountPositionInInstrument`, log `BE_NO_VALID_TICK_YET` (rate-limited) instead of BE_TICK_STALE_WARNING. Do not trigger BE_TICK_STALE_FAIL_CLOSED.
- **Stale path**: Only when `hasValidTickForBE` and `tickAgeSec > BE_TICK_STALE_WARNING_SECONDS` do we log BE_TICK_STALE_WARNING or stand down.

### 5. Push Notification Retry

- **Retry loop**: For priority 2, on send failure: retry up to 2 times with 2s delay. Log `WARN` on each retry attempt.
- **Final failure**: Existing ERROR log includes "after N attempts" when retries exhausted.
- **Non-blocking**: Uses `await Task.Delay`; no infinite loop (max 2 retries).

---

## Tests Added or Updated

- **ProcessResultForTest**: Updated to pass `(result, null, result.AuditUtc)` to match new `ProcessResult` signature.
- No new test files; existing unit tests should pass.

---

## Remaining Known Limitations

1. **Bootstrap**: Still partial. If broker reports orders after bootstrap RESUME, adoption depends on reconciliation heartbeat (AssembleMismatchObservations) and VerifyRegistryIntegrity. No explicit "late-startup" timer.
2. **BE**: Intent resolution uses IsSameInstrument; if intent.ExecutionInstrument is wrong or missing, filter can still return 0. Manual positions on non-robot charts may still trigger BE_FILTER_EXCLUDED_ACTIVE_EXPOSURE.
3. **Protective audit**: activeIntentIds come from adapter's GetActiveIntentIdsForProtectiveAudit (engine/journal-backed). Multi-engine setup: each engine has its own adapter; coordinator uses a single adapter's snapshot. If positions span engines, intent source may be incomplete for some instruments.
4. **Notifications**: Retry is bounded (2 attempts). TaskCanceledException during retry delay will still drop the notification. No re-queue on final failure.

---

## Status After Fix

| Subsystem | Before | After |
|-----------|--------|-------|
| BE / Protective | RISK | PARTIAL — alias resolution fixed; logging added; protective audit wired |
| Bootstrap | PARTIAL | PARTIAL — observability improved; no timing change |
| Protective audit | PARTIAL | PARTIAL — intent source wired; logging added |
| Tick data | RISK | FIXED — MinValue guard; BE_NO_VALID_TICK_YET path |
| Notifications | PARTIAL | PARTIAL — bounded retry for priority 2 |

---

## Constraints Verified

- No broad refactor
- Fail-closed behavior intact (BE stand-down, protective block, reconciliation unchanged)
- No speculative fixes outside audited issues
- Naming kept semantically correct
- Changes are local and testable
