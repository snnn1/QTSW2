# Mixed STOP/MARKET Entry Handling Audit

**Date:** 2026-03-12  
**Context:** After breakout execution decision logic (submit MARKET when breakout already crossed to prevent "price outside limits" rejection).

## 1. OCO Cancellation When One Side Fills

### Verified

- **Both orders share the same OCO group:** `ocoGroup` is passed to both `SubmitEntryOrder` (MARKET) and `SubmitStopEntryOrder` in `SubmitStopEntryBracketsAtLock`.
- **NinjaTrader OCO behavior:** When one order in an OCO group fills, the broker automatically cancels the other(s).
- **OCO sibling handling:** `HandleOrderUpdateReal` (NinjaTraderSimAdapter.NT.cs ~1879–1923) treats `Rejected` + `CancelPending` + non-empty OCO as `OCO_SIBLING_CANCELLED` — logs and returns early, does not record rejection.
- **Order creation:** Both `CreateOrder` paths (StopMarket and Market/Limit) pass `ocoGroup` to the Oco parameter.

### Conclusion

When one side is MARKET and the other is STOP, the opposite entry order is cancelled correctly after fill via NinjaTrader OCO.

---

## 2. Intent Tracking and Journal State

### Verified

- **Intent registration:** Both `longIntent` and `shortIntent` are registered before any submission. Same intents used for both MARKET and STOP.
- **Journal RecordSubmission:** MARKET orders use `RecordSubmission(..., "ENTRY_STOP_LONG"|"ENTRY_STOP_SHORT", ...)` — same journal type as STOP.
- **OrderInfo:** `SubmitEntryOrderReal` creates `OrderInfo` with `IsEntryOrder = true`, same as STOP path.
- **OrderMap:** Entry orders (MARKET or STOP) are stored in `OrderMap` with `IsEntryOrder = true`.
- **RecordEntryFill:** Both paths call `_executionJournal.RecordEntryFill(...)` on fill.

### Resolved

- **RecordSubmission ocoGroup:** `RecordSubmission` now receives `ocoGroup` for MARKET orders in `SubmitEntryOrderReal` (modules, RobotCore, NT_ADDONS).

---

## 3. Protective Orders After Fill

### Verified

- **Entry fill detection:** `isEntryFill = !isProtectiveOrder && orderInfo.IsEntryOrder == true`. MARKET orders have `IsEntryOrder = true` and no `:STOP`/`:TARGET` tag, so they are treated as entry fills.
- **HandleEntryFill:** Called for both MARKET and STOP entry fills. Uses `Intent` (StopPrice, TargetPrice, Direction) — same intents for both.
- **NtSubmitProtectivesCommand:** Built from `intent.StopPrice`, `intent.TargetPrice`, `intent.Direction` — identical for MARKET and STOP.
- **ExecuteSubmitProtectives:** Same execution path; protective OCO group is generated internally.

### Conclusion

Protective orders are attached correctly after market-entry fills, same as stop-entry fills.

---

## 4. Lifecycle Events and Cleanup

### Verified

| Event / Path              | MARKET Entry | STOP Entry | Divergence |
|---------------------------|-------------|------------|------------|
| RegisterIntent            | Yes         | Yes        | None       |
| RegisterIntentPolicy      | Yes         | Yes        | None       |
| OrderMap + IsEntryOrder   | Yes         | Yes        | None       |
| RecordSubmission          | Yes         | Yes        | ocoGroup passed for both |
| OnEntryFill / Coordinator | Yes         | Yes        | None       |
| HandleEntryFill           | Yes         | Yes        | None       |
| ExecuteSubmitProtectives  | Yes         | Yes        | None       |
| OCO_SIBLING_CANCELLED     | Yes (when STOP cancelled) | Yes (when MARKET cancelled) | None |
| CheckAllInstrumentsForFlatPositions | Yes | Yes | None |

### Conclusion

No meaningful divergence in lifecycle events, journal flags, or cleanup between MARKET and STOP entry paths.

---

## 5. Remaining Risk Areas (Non-Blocking)

| Risk | Notes |
|------|-------|
| Live adapter under reconnect stress | NinjaTraderLiveAdapter is stub; MARKET+OCO not exercised in LIVE. |
| Market price null in restart windows | `GetCurrentMarketPrice` can return (null, null); logic treats as not crossed → STOP. Safe. |
| Fallback rebuild stall | If BarsRequest succeeds late/partially, stream may stay PRE_HYDRATION until bars arrive. Timeout exists. |
| 3-minute freshness window | Instrument-specific; may need tuning per symbol. |
| MARKET substitution slippage | MARKET fills at current price; STOP would have filled at breakout. Slippage difference possible for NG/RTY/CL. |

---

## 6. Test Coverage Added

- `BreakoutExecutionDecisionTests`: STOP vs MARKET decision logic (crossed / not crossed).
- `MixedStopMarketEntryTests`: Integration-style tests for:
  - **Long MARKET + short STOP**: bid=4494.25, ask=4501.25 → long SubmitEntryOrder(MARKET), short SubmitStopEntryOrder, both share same ocoGroup.
  - **Short MARKET + long STOP**: bid=4494, ask=4500 → short SubmitEntryOrder(MARKET), long SubmitStopEntryOrder, both share same ocoGroup.
- Run: `dotnet run --project modules/robot/harness/Robot.Harness.csproj -- --test MIXED_STOP_MARKET_ENTRY`
