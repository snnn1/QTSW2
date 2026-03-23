# Implementation summary — unified broker position identity + flatten verification

## Canonical model (explicit)

| Aspect | Choice |
|--------|--------|
| **Canonical key** | NinjaTrader `MasterInstrument.Name` (e.g. `MNQ`, `MYM`), case-insensitive match. |
| **Normalization** | `BrokerPositionResolver.NormalizeCanonicalKey`: first token before space (e.g. `MNQ 06-26` → `MNQ`). |
| **Reconciliation quantity** | Sum of `Math.Abs(quantity)` over **every** non-flat `PositionSnapshot` row whose `Instrument` equals the canonical key (unchanged behavior, centralized in `BrokerPositionResolver.BuildReconciliationAbsTotalsByCanonicalKey`). |
| **Flatten (design A)** | Close **each** broker row in the canonical bucket using that row’s **native NT `Instrument`** (`CreateOrder` target), not only the chart’s `_ntInstrument`. |
| **“Flat”** | `GetBrokerCanonicalExposure(key).ReconciliationAbsQuantityTotal == 0` after the verify window (same definition reconciliation uses). |

## Files changed

### Shared (modules + RobotCore)

- `modules/robot/core/Execution/BrokerCanonicalExposure.cs` (new) — also copied to `RobotCore_For_NinjaTrader/Execution/`.
- `modules/robot/core/Execution/BrokerPositionResolver.cs` (new) — also in RobotCore.
- `modules/robot/core/Execution/IExecutionAdapter.cs` — `PositionSnapshot.ContractLabel`; RobotCore mirror.
- `modules/robot/core/Execution/ReconciliationRunner.cs` — uses resolver for totals + `RECONCILIATION_CONTEXT` row diagnostics; RobotCore mirror.

### NinjaTrader / IEA (RobotCore)

- `Execution/IIEAOrderExecutor.cs` — `GetBrokerCanonicalExposure`, `SubmitFlattenOrder(..., object? nativeInstrumentForBrokerOrder = null)`.
- `Execution/NinjaTraderSimAdapter.NT.cs` — `GetBrokerCanonicalExposureInternal`, refactored `GetAccountPositionForInstrument`, `SubmitFlattenOrder` uses native instrument when provided, snapshot `ContractLabel`, bootstrap/recovery broker qty from exposure, `ExecuteFlattenInstrument` precheck log, `FlattenIntentReal` multi-leg + no chart-only early flat, `EmergencyFlatten` multi-leg, post-flatten verification via canonical exposure, new engine events.
- `Execution/NinjaTraderSimAdapter.cs` — `_pendingFlattenVerifications` tuple (dropped chart `InstrumentRef`).
- `Execution/InstrumentExecutionAuthority.Flatten.cs` — `RequestFlatten` uses exposure, per-leg submit + `FLATTEN_ORDER_SUBMITTED` / `FLATTEN_CANONICAL_EXPOSURE`.
- `Execution/ReplayExecutor.cs` — `GetBrokerCanonicalExposure`, updated `SubmitFlattenOrder` signature.
- `Execution/FlattenDecisionSnapshot.cs` — leg / canonical diagnostic fields.
- `Execution/StrategyThreadExecutor.cs` — `FLATTEN_COMMAND_COMPLETED` + `NT_ACTION_SUCCESS` note for flatten.
- `RobotEventTypes.cs` — severities for new event types.

### Tests / harness

- `modules/robot/core/Tests/BrokerPositionIdentityTests.cs`
- `modules/robot/harness/Program.cs` — `--test BROKER_POSITION_IDENTITY`

## Operator-facing behavior

1. **Flatten** may submit **multiple** market orders when multiple NT positions share the same master symbol.
2. **Broker flat** is asserted only when **`FLATTEN_BROKER_FLAT_CONFIRMED`** fires (after verify window), not when **`NT_ACTION_SUCCESS`** / **`FLATTEN_COMMAND_COMPLETED`** fire.
3. **`FLATTEN_BROKER_POSITION_REMAINS`** + **`FLATTEN_VERIFY_FAIL`** when canonical exposure still &gt; 0 after the window (recovery should stay active until flat or fail-closed path).
4. **`RECONCILIATION_CONTEXT`** now includes `canonical_broker_key`, `broker_exposure_aggregated`, `broker_position_rows`.

## Log semantics (changed / clarified)

| Event | Meaning |
|-------|---------|
| `NT_ACTION_SUCCESS` | **Unchanged emission** for all NT actions; for flatten, `data.note` explains it is **not** flat confirmation. |
| `FLATTEN_COMMAND_COMPLETED` | Flatten NT action delegate returned without exception. |
| `FLATTEN_ORDER_SUBMITTED` | At least one flatten order submit path completed (IEA per leg + adapter summary after `ExecuteFlattenInstrument`). |
| `FLATTEN_BROKER_FLAT_CONFIRMED` | Verify window elapsed; canonical `ReconciliationAbsQuantityTotal == 0`. |
| `FLATTEN_BROKER_POSITION_REMAINS` | Verify window elapsed; canonical exposure still non-zero. |
| `FLATTEN_BROKER_EXPOSURE_PRECHECK` | Canonical exposure snapshot before cancel+flatten. |
| `FLATTEN_CANONICAL_EXPOSURE` | IEA `RequestFlatten` exposure detail. |
| `BROKER_EXPOSURE_NET_ZERO_MULTI_LEG` | Rare: offsetting signed legs same master; `GetAccountPositionForInstrument` returns flat — flatten uses `GetBrokerCanonicalExposure` / per-leg path. |

## Tests

```bash
dotnet run --project modules/robot/harness/Robot.Harness.csproj -- --test BROKER_POSITION_IDENTITY
```

**Result (local):** PASS.

**NT build:** `dotnet build RobotCore_For_NinjaTrader/Robot.Core.csproj` — 0 errors (warnings pre-existing).

Scenarios B/D/E (submit without flat, flat confirmed, already flat) are covered in NT code paths; scenario B is validated in production logs via `FLATTEN_BROKER_POSITION_REMAINS` + absence of `FLATTEN_BROKER_FLAT_CONFIRMED` until exposure clears.

## Fail-closed

- `FLATTEN_FAILED_PERSISTENT` / stand-down still apply after max verify retries.
- Policy still uses `ReconciliationAbsQuantityTotal` for broker qty inputs.
