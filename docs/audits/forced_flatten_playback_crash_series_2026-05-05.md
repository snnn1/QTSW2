# Forced Flatten Playback Crash Series Audit

Audit date: 2026-05-05

Scope:
- Audit every NinjaTrader crash on 2026-05-05 that matched the forced-flatten/session-close failure mode.
- Separate OCO reuse, forced flatten, cancel-only cleanup, callback recursion, and deployment proof.
- Identify the smallest fix needed before replaying the failing day again.

Evidence level:
- Crash evidence: playback/runtime crash evidence from Windows Application Error events, NinjaTrader logs/traces, run journals, key events, and flatten completion journals.
- Fix evidence: source-proven, build-proven, harness-proven, and deployed-DLL-hash-proven.
- Not yet proven: playback-proven for the fix. The next exact-day playback run must prove this.

## Crash Inventory

All seven NinjaTrader process crashes on 2026-05-05 had exception code `0xc00000fd` (stack overflow).

| Crash wall time | Run | NT log/trace | Fault module | OCO reuse? | Last observed failure area |
|---|---|---|---|---|---|
| 16:17:54 | `e64def2d11e849f2933553db2d32c742` | `00003` | `ntdll.dll` | Yes, NG1/MNG | Forced flatten fills, then entry terminalization burst |
| 17:02:48 | `9183ca572b474ba4ab54420bd60acb62` | `00004` | `clr.dll` | No | Forced flatten fills across MNG/MYM/MNQ |
| 18:00:44 | `0f1afd66687a47398c17084ed32c2006` | `00005` | `clr.dll` | No | Forced flatten fill burst; not all confirmations flushed |
| 18:48:38 | `63d95e3d7124411c8bdc39ae53cf2e9f` | `00006` | `ntdll.dll` | No | Cancel-only session-close OCO cleanup for RTY2/M2K |
| 20:14:06 | `18c4d416c97e4025a1e074e3c44947b2` | `00007` | `clr.dll` | Yes, NG1/MNG | Forced flatten fills; MNG fill did not fully journal before crash |
| 20:49:14 | `e75b1bf9da6847bbbcb3730db59e79d2` | `00008` | `clr.dll` | Yes, NG1/MNG | Forced flatten all-flat path still crashed after completions |
| 21:20:15 | `69632725b3f44826913fbbebf3d53564` | `00009` | `ntdll.dll` | Yes, NG1/MNG | Cross-instrument forced-flatten owner assignment; YM1/MYM still open at crash |

Runs at 03:35 and 04:45 produced logs but did not have matching Windows Application Error crash records.

## Findings

1. OCO reuse is real but not the only crash cause.
   - NG1/MNG mixed MARKET/STOP entry OCO reuse appeared in four of seven crashes.
   - Three crashes had no OCO reuse.
   - Therefore OCO reuse explains some rejection/mismatch noise, but it cannot be the complete stack-overflow root cause.

2. The common crash shape is session-close order cleanup under high playback speed.
   - Crashes occurred after forced-flatten fills.
   - One crash occurred with no flatten order, only session-close OCO cancel cleanup for RTY2/M2K.
   - Several traces ended inside NinjaTrader synchronous order/execution/position callback processing.

3. Cross-instrument ownership was wrong in the latest crash.
   - Latest crashed run `69632725b3f44826913fbbebf3d53564` showed flatten coordination owner assignment for:
     - MNQ hosted from MGC chart
     - MNG hosted from MES chart
     - MYM hosted from M2K chart
   - That means forced-flatten work could be initiated by a chart/adapter that did not own the target execution instrument.
   - Under accelerated playback, NinjaTrader then emitted synchronous cancel/fill/position callbacks recursively.

4. The cancel-only crash is the strongest proof that this is not just market flatten.
   - Run `63d95e3d7124411c8bdc39ae53cf2e9f` crashed after Account/Simulator cancel routing and OCO cancel callbacks for RTY2/M2K.
   - No flatten fill was involved.
   - Session-close cancel cleanup needs the same owner-thread routing as flatten.

5. Latest crash did not use the deployed fix.
   - Latest crash wall time: 2026-05-05 21:20:15.
   - Current deployed DLL timestamp: 2026-05-05 21:35:11 UTC.
   - Current deployed DLL hash: `9E1C2495494A6781190F86BD6F349724082A0F6E8DBF8DC3FD1CE41980C75E17`.

## Root Cause Model

The best-supported model is:

- Mixed MARKET/STOP OCO entry ordering caused intermittent OCO reuse rejection on NG1/MNG.
- Independently, session-close cancel/flatten work was able to originate from a non-owner instrument adapter/chart.
- During high-speed playback, NinjaTrader processed OCO cancels, fills, position updates, and strategy callbacks synchronously.
- Cross-owner session-close cleanup increased recursive callback pressure until NinjaTrader/CLR stack overflowed with `0xc00000fd`.

Confidence:
- High that owner-thread routing and OCO ordering are real defects.
- Medium-high that these defects explain the stack overflow series.
- Not final until the exact failing day replays cleanly with the verified deployed DLL.

## Fixes Applied

Runtime path:
- Mixed STOP/MARKET entry brackets now submit the passive STOP side before the marketable side, avoiding immediate OCO reuse on the already-filled side.
- Session-close flatten now routes cancel and flatten commands through the target execution-instrument owner.
- Session-close sibling cancel cleanup now routes through the target execution-instrument owner.
- Flatten execution has an execute-phase guard that forwards work to the target owner if a non-owner adapter receives it.
- Added `NT_ACTION_ROUTED_TO_INSTRUMENT_OWNER` evidence when routing occurs.

Mirror/source consistency:
- `system/NT_ADDONS/Execution/IExecutionAdapter.cs` now declares session-close flatten/cancel methods.
- `system/NT_ADDONS/Execution/NinjaTraderSimAdapter.cs` mirrors owner-routed session-close flatten/cancel behavior.
- `system/NT_ADDONS/Execution/NullExecutionAdapter.cs` and `NinjaTraderLiveAdapter.cs` now implement coherent dry-run/live-stub no-op methods.

## Verification Completed

Builds:
- `system/modules/robot/core/Robot.Core.csproj`: 0 errors.
- `system/RobotCore_For_NinjaTrader/Robot.Core.csproj`: 0 errors.
- `system/modules/robot/harness/Robot.Harness.csproj`: 0 errors.

Focused harnesses:
- `SESSION_CLOSE_OWNER_ROUTING`
- `FLATTEN_COORDINATION_TRACKER`
- `FORCED_FLATTEN_POLICY`
- `IEA_FLATTEN`
- `EXECUTION_ORDERING`
- `BREAKOUT_EXECUTION_DECISION`
- `MIXED_STOP_MARKET_ENTRY`
- `ORDER_RECONCILIATION`
- `AUTHORITY_CONTRADICTIONS`

Deploy:
- Built DLL hash: `9E1C2495494A6781190F86BD6F349724082A0F6E8DBF8DC3FD1CE41980C75E17`.
- Deployed DLL hash: `9E1C2495494A6781190F86BD6F349724082A0F6E8DBF8DC3FD1CE41980C75E17`.
- Deployed path: `C:\Users\jakej\Documents\NinjaTrader 8\bin\Custom\Robot.Core.dll`.
- Deployed timestamp: `2026-05-05T21:35:11Z`.
- NinjaTrader process check after deploy: not running.

## 2026-05-05 22:24 Crash Follow-Up

Latest crashed run:
- Run: `runs/2cc1dd6b64ac4e1db359eb24c5297198`.
- Windows Application Error: NinjaTrader `0xc00000fd` stack overflow at `2026-05-05 22:24:21`.
- Immediate raw NT trace: M2K/RTY2 pre-entry OCO cancel at playback time `2026-04-27 20:56:01`.
- Orders involved:
  - Long entry `7fa654cb1c602a1f`, broker order `acc7f567a0504a5a957f5e0d2a2226ae`.
  - Short entry `2e8132926dce8749`, broker order `8cb7ec34a90b442d907cf0df813e3052`.
  - Shared OCO `QTSW2:OCO_ENTRY:2026-04-27:RTY2:10:30:1940e00be71a488bb056aa1389b7cd88`.

Additional root cause refinement:
- The previous owner-routing fix was necessary but incomplete.
- Pre-entry forced-flatten cleanup still cancelled by explicit broker order id set and then by both long/short intent ids.
- `CancelIntentOrdersReal` also recursively attempted opposite-entry cancellation even though NinjaTrader OCO already cancels the sibling.
- That created duplicate OCO sibling cancel pressure in the exact crash window.

Additional fixes applied after this crash:
- Pre-entry forced-flatten cleanup now routes cancel cleanup through `RequestSessionCloseCancelIntents`.
- For a live long/short entry OCO pair, cleanup requests one primary entry-intent cancel and allows NinjaTrader OCO to cancel the sibling.
- Explicit broker-order-id cancel for the NinjaTrader pre-entry OCO pair is no longer used on the sim adapter path.
- `CancelIntentOrdersReal` no longer recursively calls itself for the opposite entry stop.
- `RequestSessionCloseCancelIntents` no longer requires the caller adapter to have a local NT queue when it can route to the target instrument owner.
- The same changes were mirrored into the shared core, NinjaTrader runtime copy, and NT_ADDONS copy.

Additional verification:
- Core build: `0 Error(s)`.
- NinjaTrader runtime build/deploy: `0 Error(s)`.
- Harnesses passed:
  - `SESSION_CLOSE_OWNER_ROUTING`
  - `FORCED_FLATTEN_POLICY`
  - `FLATTEN_COORDINATION_TRACKER`
  - `IEA_FLATTEN`
  - `EXECUTION_ORDERING`
  - `BREAKOUT_EXECUTION_DECISION`
  - `MIXED_STOP_MARKET_ENTRY`
  - `ORDER_RECONCILIATION`
  - `AUTHORITY_CONTRADICTIONS`
  - `EXECUTION_TRIGGER_JOURNAL_INTEGRITY`
  - `REENTRY_MARKET_CLOSE_COMMIT`
  - `MARKET_REENTRY_SUBMIT_PATH`

Latest deployment:
- Deployed DLL hash: `055C4AEE768DAF04D67EC5B876C9E6906EA31BBA20493C1E08355395B1885431`.
- Deployed path: `C:\Users\jakej\Documents\NinjaTrader 8\bin\Custom\Robot.Core.dll`.
- Deployed timestamp: `2026-05-05T22:34:36Z`.
- Proof level: build-proven and harness-proven only. Not playback-proven until the failing day reruns cleanly with this hash loaded.

## Required Next Proof

Run the exact failing playback date again:
- Date: 2026-04-27.
- Priority: keep playback speed conservative through the session-close/forced-flatten boundary.
- Required checks:
  - No NinjaTrader `0xc00000fd` Application Error.
  - No NG1/MNG OCO reuse rejection.
  - If cross-owner cleanup occurs, `NT_ACTION_ROUTED_TO_INSTRUMENT_OWNER` appears.
  - Forced flatten confirmations and broker-flat completions for all exposed instruments.
  - No open filled journals, orphan fills, working orders, or broker exposure at shutdown.

Only after that run is clean should this fix be called playback-proven.
