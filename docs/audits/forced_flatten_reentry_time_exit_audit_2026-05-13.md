# Forced Flatten / Reentry / Time Exit Audit - 2026-05-13

Scope: focused audit of the latest unsafe SIM/playback run, forced session flatten, market reentry, playback stall, deployed DLL identity, and current workspace proof.

Evidence levels used: source-only, build-proven, harness-proven, playback-proven, SIM/runtime-proven, deployed-live-proven.

## Controlling Runtime Evidence

- Active run pointer: `runs/LATEST_RUN.txt` -> `runs/991956c6fb1e40b58a4e6c3c21d9da81`.
- Prior audit map still references `runs/31c08ab5b9d342038b212bbdf2a75991`; that is now older comparison evidence, not the active pointer.
- `runs/991956c6fb1e40b58a4e6c3c21d9da81/summary.json` reports `FAIL`, `UNSAFE_EXPOSURE`, `STOP`, `OPEN_EXPOSURE_AT_SHUTDOWN`, confidence `HIGH`.
- Summary counts: 12 trades, 10 errors, broker open qty 8, broker working orders 8, ownership active slots 4, journal open qty 8, incomplete streams 4.
- `RUN_SHUTDOWN.json` reason: `playback_stall_live_exposure_timeout`, source `playback_stall_quiesce`, timestamp `2026-05-13T04:08:35.1618222+00:00`.
- Runtime DLL proof in `robot_ENGINE.jsonl`: `C:\Users\jakej\Documents\NinjaTrader 8\bin\Custom\Robot.Core.dll`, SHA-256 `b5f93af61e598c57b291b322b8a1de84fa49ad393081b7b47e1b864e06455dd1`, last write `2026-05-13T01:34:27.4409206Z`.

Verdict for the run: SIM/runtime-proven failure. It is not prop-ready and not quant-grade execution proof.

## Failure Timeline

| Time | Evidence | Meaning |
|---|---|---|
| `2026-05-12T20:55:00Z` | `KEY_EVENTS.jsonl` lines 67-68 | MYM session forced flatten requested and submitted for original intent `c2209618927d1426`. |
| `2026-05-12T20:56:00Z` | `KEY_EVENTS.jsonl` lines 71-72 | MNG session forced flatten requested and submitted for original intent `ee180b899a5d5bf6`. |
| `2026-05-12T22:00:00Z` | `robot_ENGINE.jsonl` lines 36971, 36980 | MYM market-reopen reentry attempts accepted for `YM2` and `YM1`. |
| `2026-05-12T22:01:00Z` | `robot_ENGINE.jsonl` lines 37172, 37182 | MNG market-reopen reentry attempts accepted for `NG2` and `NG1`. |
| `2026-05-13T03:59:43Z` | `KEY_EVENTS.jsonl` lines 69-70 | MYM late session-close flatten confirmation arrived in wall time. |
| `2026-05-13T04:01:53Z` to `04:02:01Z` | `KEY_EVENTS.jsonl` lines 73-88 | Reentry orders filled: MYM +4, MNG -4, with protectives submitted. |
| `2026-05-13T04:08:04Z` and `04:08:08Z` | `robot_ENGINE.jsonl` final authority audit | MNG and MYM each had broker qty 4, journal open qty 4, ownership gross 4, working count 4. |
| `2026-05-13T04:08:35Z` | `robot_ENGINE.jsonl` line 40773 | Playback stall forced unsafe finalization with broker gross exposure 8 and 8 robot working orders. |

The exposure was real and internally consistent: broker, journal, ownership, and working-order counts agreed. This was not a false summary.

## Root Cause

The failed deployed DLL allowed single-day isolated playback to open market-reentry positions after a session-close forced flatten. Playback then stalled while the reentry positions were still live, so shutdown correctly reported open exposure.

The current workspace already contains a targeted source fix that was not present in the failed runtime DLL:

- `system/modules/robot/core/StreamStateMachine.Reentry.cs:218` and `system/RobotCore_For_NinjaTrader/StreamStateMachine.Reentry.cs:218` now call `TrySuppressSingleDayPlaybackReentryAndTerminalize` before enqueuing market reentry.
- `system/modules/robot/core/StreamStateMachine.Reentry.cs:313` and `system/RobotCore_For_NinjaTrader/StreamStateMachine.Reentry.cs:313` terminalize completed forced-flatten lifecycles in single-day isolated playback instead of opening multi-session reentry.
- `ShouldSuppressMarketReentryForSingleDayPlaybackProof` requires `ExecutionMode.SIM` plus playback journal bypass at `StreamStateMachine.Reentry.cs:373`.
- Focused harness coverage exists at `system/modules/robot/core/Tests/ReentryMarketCloseCommitTests.cs:61` and `:1051`.

Interpretation: the root cause is not current source behavior. It is a deployed-runtime/source mismatch for this failure family. The latest failed run loaded hash `b5f93...`; the current source/build/deployed target hash is now `8bcf...`.

## Current Workspace Proof

Harness-proven:

- `dotnet run --project system/modules/robot/harness/Robot.Harness.csproj -- --test REENTRY_MARKET_CLOSE_COMMIT` -> PASS.
- `dotnet run --project system/modules/robot/harness/Robot.Harness.csproj -- --test STREAM_JOURNAL_PLAYBACK_BYPASS` -> PASS.
- `dotnet run --project system/modules/robot/harness/Robot.Harness.csproj -- --test PLAYBACK_SCENARIO` -> PASS.
- `dotnet run --project system/modules/robot/harness/Robot.Harness.csproj -- --test MARKET_REENTRY_SUBMIT_PATH` -> PASS.
- `dotnet run --project system/modules/robot/harness/Robot.Harness.csproj -- --test IEA_ALIGNMENT` -> PASS.

Build-proven:

- `dotnet build system/RobotCore_For_NinjaTrader/Robot.Core.csproj -c Release -v:minimal` -> PASS, 0 errors.
- Build warnings remain: NuGet vulnerability lookup blocked by network, `System.Text.Json` 8.0.0 high-severity advisories, and assembly version conflicts against NinjaTrader references.

Hash proof:

- Current build output `system/RobotCore_For_NinjaTrader/bin/Release/net48/Robot.Core.dll` SHA-256: `8BCF367E2D01F5F7343BC041935EC7A1A396F57A5A43A07D9BBFA314CA4EAF16`.
- Current deployed target `C:\Users\jakej\Documents\NinjaTrader 8\bin\Custom\Robot.Core.dll` SHA-256: `8BCF367E2D01F5F7343BC041935EC7A1A396F57A5A43A07D9BBFA314CA4EAF16`.

Current proof ceiling: build-proven plus harness-proven plus deployed-file hash match. It is not yet SIM/runtime-proven because no run has emitted `ROBOT_BUILD_SIGNATURE` with hash `8BCF367E2D01F5F7343BC041935EC7A1A396F57A5A43A07D9BBFA314CA4EAF16`.

## Required Next Proof

1. Restart NinjaTrader or otherwise force the runtime to load the current `Robot.Core.dll`.
2. Run the exact failing single-day playback/date again.
3. Confirm `ROBOT_BUILD_SIGNATURE` reports SHA-256 `8BCF367E2D01F5F7343BC041935EC7A1A396F57A5A43A07D9BBFA314CA4EAF16`.
4. Required pass criteria:
   - no post-flatten single-day playback reentry orders,
   - `SESSION_REENTRY_SUPPRESSED_SINGLE_DAY_PLAYBACK` or equivalent suppression evidence appears,
   - broker open qty 0 at shutdown,
   - broker working orders 0 at shutdown,
   - journal open qty 0 at shutdown,
   - no `OPEN_EXPOSURE_AT_SHUTDOWN`,
   - no `playback_stall_live_exposure_timeout` with live exposure.
5. After that passes, run one representative SIM/runtime day with the same deployed hash.

## Verdict

Current failed run remains SIM/runtime-proven unsafe. Current workspace appears to contain the targeted fix and focused tests, and the built/deployed DLL file now matches the fixed build hash. The system is still not runtime-proven fixed until a new run proves the `8BCF...` DLL under `ROBOT_BUILD_SIGNATURE` and ends broker-flat with no working orders.

## Post-Market Runtime Audit - 2026-05-13

Scope: read-only audit of the completed May 13 trading day after the market close, focused on M2K forced flatten, MYM registry divergence, duplicate entry idempotency, final broker/journal state, and remaining proof gaps.

Evidence level: SIM/runtime log evidence plus durable journal evidence. This is not deployed-live-proven evidence. No fresh `ROBOT_BUILD_SIGNATURE` for the active runtime was found in `logs/robot/robot_ENGINE.jsonl`; the deployed file on disk hashes to `8BCF367E2D01F5F7343BC041935EC7A1A396F57A5A43A07D9BBFA314CA4EAF16`, but this audit does not prove NinjaTrader loaded that hash.

Runtime context:

- Audit timestamp: `2026-05-13T21:07:55Z`.
- NinjaTrader was still running as PID `20224`, started `2026-05-13 14:23:36` local.
- `logs/robot/robot_ENGINE.jsonl`, `robot_M2K.jsonl`, `robot_MYM.jsonl`, and `daily_20260513.md` were the controlling active logs for this post-market read.
- `runs/LATEST_RUN.txt` still points at the older unsafe playback run `runs/991956c6fb1e40b58a4e6c3c21d9da81`; that pointer is not a finalized run artifact for this post-market live/SIM day.

### Final Exposure State

Latest authority rows after close:

| Instrument | Latest authority time | Broker qty | Real open qty | Journal open qty | Authority | Parity |
|---|---:|---:|---:|---:|---|---|
| M2K | `2026-05-13T21:06:04Z` | 0 | 0 | 0 | `REAL_DOMINANT` | `PARITY_OK` |
| MYM | `2026-05-13T21:05:58Z` | 0 | 0 | 0 | `REAL_DOMINANT` | `PARITY_OK` |
| MNG | `2026-05-13T20:56:50Z` | 0 | 0 | 0 | `REAL_DOMINANT` | `PARITY_OK` |

Durable journal state:

- All fourteen `state/execution_journals/2026-05-13_*.json` files were completed.
- No journal had nonzero open quantity.
- No journal reported `Rejected=true`.
- No journal reported `ProtectiveRejected=true`.
- `data/risk_latches` had no latch files.
- `configs/robot/kill_switch.json` had `"enabled": false`.

Interpretation: exposure and durable journal state are flat after close. The day is not clean because critical integrity events fired, but the final broker/journal state is not showing open exposure.

### M2K Forced Flatten

M2K / RTY2 lifecycle:

| Time | Event | Evidence |
|---|---|---|
| `2026-05-13T15:00:03Z` | Entry order registered | `ORDER_REGISTRY_REGISTERED`, intent `3d940c22c77a1b1f`, broker alias later resolved to `454542694448`. |
| `2026-05-13T18:15:07Z` | Entry filled | `ORDER_REGISTRY_TERMINAL_LIFECYCLE_RESOLVED_BY_ALIAS`, then entry execution mapped. |
| `2026-05-13T20:55:03Z` | Forced flatten started | `FLATTEN_REQUESTED`, `FLATTEN_SENT`, `FLATTEN_SUBMITTED`, qty 2 sell against long 2. |
| `2026-05-13T20:55:05Z` | Flatten filled | `FLATTEN_FILL_RECEIVED`, broker order `454542694691`; `EXECUTION_FILLED` mapped close qty 2. |
| `2026-05-13T20:55:05Z` | Trade completed | `TRADE_COMPLETED`, completion reason `FLATTEN`, entry qty 2, exit qty 2. |
| `2026-05-13T20:55:08Z` | Broker flat proof | `FLATTEN_VERIFY_PASS`; later authority rows show broker qty 0 and journal open qty 0. |

M2K concern:

- `RECONCILIATION_IEA_OWNED_WORKING_INVARIANT_BREACH` fired once at `2026-05-13T20:55:04.977Z`.
- Event data: broker working count 2, IEA owned/adopted working 0, mismatch-trusted working 0, pending execution workload 0, convergence active false.
- Immediately after, the adoption scan saw `broker_working_count=0` and adopted no orders. Flatten fill arrived and completed normally.

Interpretation: M2K flattened correctly from a capital-safety perspective. The critical invariant appears to be a short timing/classification gap around protective cancel/flatten convergence, not a final exposure failure. It still needs a targeted fix or reclassification because it currently emits a CRITICAL state during a successful forced flatten.

### MYM Registry / Duplicate Submit

MYM emitted three safety-relevant critical groups:

| Time | Event | Evidence | Interpretation |
|---|---|---|---|
| `2026-05-13T14:38:03Z` | `DUPLICATE_ORDER_SUBMISSION_DETECTED` x2 | Intents `82b2ba63cd1a3a4b` and `c99a969f2ad675ff`; existing order IDs `454542694436` and `454542694442`; `second_order_id=null`. | Duplicate same-intent submits were blocked and returned idempotent success. No second broker order was proven. Current severity is too strong for this benign/idempotent path. |
| `2026-05-13T20:16:12Z` | `REGISTRY_BROKER_DIVERGENCE` x2 | Broker IDs `454542694436` and `454542694442`; note says broker has live order but registry does not, adopting to restore consistency. | The same broker IDs had been linked at `14:31`, then repeatedly appeared as pending convergence, then escalated. This is a real registry lifecycle bug or stale-order cleanup bug. |
| `2026-05-13T20:16:13Z` | YM2 target completed | Intent `e7031eb4db3c41bb`, completion `TARGET`, entry qty 2, exit qty 2. | Exposure closed normally despite registry divergence. |

MYM session close:

- `YM1` forced flatten requested at `2026-05-13T20:55:02Z`.
- Flatten fill/trade completion occurred at `2026-05-13T20:55:05Z`.
- `FLATTEN_VERIFY_PASS` occurred at `2026-05-13T20:55:08Z`.
- Latest MYM authority after close: broker qty 0, journal open qty 0, parity OK.

Interpretation: MYM ended flat, but registry integrity is not green. Broker IDs that were known earlier were later treated as registry-missing while still live. That is the main audit finding from today.

### Source Cross-Check

Duplicate submit path:

- `system/modules/robot/core/Execution/NinjaTraderSimAdapter.cs:62-63` returns `OrderSubmissionResult.SuccessResult` for duplicate same-intent entry resubmits.
- `system/modules/robot/core/Execution/NinjaTraderSimAdapter.cs:99-128` logs `ENTRY_ORDER_DUPLICATE_BLOCKED` and then `DUPLICATE_ORDER_SUBMISSION_DETECTED`.
- `system/modules/robot/core/RobotEventTypes.LevelMap.cs:298` maps `DUPLICATE_ORDER_SUBMISSION_DETECTED` to `CRITICAL`.

Finding: the behavior is idempotent and blocks the duplicate, but the event name/severity still presents it as a critical duplicate submission. That is likely classification noise unless a second broker order ID is present.

M2K invariant path:

- `system/modules/robot/core/RobotEngine.Reconciliation.Observations.cs:216-255` emits `RECONCILIATION_IEA_OWNED_WORKING_INVARIANT_BREACH` when broker working count is positive but IEA mismatch-trusted working count is zero and no convergence window is active.
- In the M2K event, `convergence_active=false`, yet within about 300 ms the adoption scan saw no same-instrument broker working orders and the flatten filled.

Finding: the convergence-window inputs did not cover this forced-flatten/cancel transition. The invariant is useful, but its timing envelope is too narrow for this successful close path.

Registry divergence path:

- `system/RobotCore_For_NinjaTrader/Execution/InstrumentExecutionAuthority.OrderRegistryPhase2.cs:372-438` scans broker working/accepted orders and emits `REGISTRY_BROKER_DIVERGENCE` when a broker live order is not in the IEA registry outside pending convergence.
- The runtime-specific `OrderRegistryPhase2` implementation exists only under `system/RobotCore_For_NinjaTrader`; the shared core tree has only the harness/runtime-neutral order registry files.

Finding: MYM broker IDs `454542694436` and `454542694442` were linked into the registry at `14:31`, then repeatedly reported as pending convergence, then escalated at `20:16`. This suggests registry key/alias/lifecycle cleanup drift, not unknown manual orders.

### Verdict

Post-market capital-safety state: GREEN/YELLOW. Broker/journal exposure ended flat for M2K, MYM, and MNG, and all May 13 execution journals were closed with open qty zero.

Execution-integrity state: YELLOW/RED. Today is not clean enough for prop-readiness because critical registry/invariant events fired:

- MYM duplicate same-intent entry submit was safely blocked but logged as CRITICAL.
- MYM registry lost or failed to trust known broker IDs later in the day.
- M2K forced flatten worked, but reconciliation emitted a CRITICAL invariant during flatten convergence.

Smallest next fixes:

1. Reclassify idempotent duplicate same-intent entry resubmits when `second_order_id=null` and the return path is `DuplicateEntryResubmitResult`; keep CRITICAL only when a real second broker order exists or quantity exceeds policy.
2. Audit/fix MYM registry alias/lifecycle retention for broker IDs that were linked before fill but later appear registry-missing.
3. Extend the reconciliation transient window for forced-flatten protective cancel/flatten-fill convergence, or add a forced-flatten-aware reason so successful close does not emit `RECONCILIATION_IEA_OWNED_WORKING_INVARIANT_BREACH`.
4. Add focused harness coverage for those three paths, then rerun one SIM/runtime day and require no CRITICAL registry/invariant events plus broker flat/no working orders at shutdown.

## 2026-05-14 Implementation Update

Evidence level: build-proven and focused harness-proven only. Not runtime-proven and not deployed-live-proven.

Implemented:

- `StreamStateMachine.Commit` now defers terminal commits whenever the stream still has open lifecycle exposure or pending reentry, except for intentional slot-expiry/session-close terminal paths.
- Carry-forward terminal commits now mirror back to the prior-day stream journal, including already-committed startup/rollover cases, and emit `CARRY_FORWARD_PRIOR_JOURNAL_TERMINALIZED`.
- The NinjaTrader protective submit idempotent path now registers already-existing broker stop/target orders with IEA before returning success. This targets the last-runs shape where broker working orders existed but IEA mismatch-trusted working count was zero.

Focused proof run:

- `dotnet run --project system/modules/robot/harness/Robot.Harness.csproj -- --test REENTRY_MARKET_CLOSE_COMMIT`: PASS.
- `dotnet run --project system/modules/robot/harness/Robot.Harness.csproj -- --test ORDER_REGISTRY`: PASS.
- `dotnet build system/RobotCore_For_NinjaTrader/SiblingProtectiveCancelQueue.Test/SiblingProtectiveCancelQueue.Test.csproj -c Release -v:minimal --no-restore`: PASS, 0 errors, existing warning set only.
- Built DLL hash: `CD53286C22BCDD3944A705425E6390ED39B6FC4767937F41A34BF85DBF5DB198` at `system/RobotCore_For_NinjaTrader/bin/Release/net48/Robot.Core.dll`.

Still required:

- Rebuild/redeploy intentionally, then confirm deployed DLL hash equals runtime `ROBOT_BUILD_SIGNATURE`.
- Rerun the exact failing playback/SIM shape or closest controlled SIM equivalent.
- Pass criteria: no stale active prior-day stream journals, no `SESSION_CLOSE_GLOBAL_SWEEP_REQUESTED` against active post-reopen reentry, no `ORDER_REGISTRY_MISSING_FAIL_CLOSED` for robot-owned working protectives, broker/journal/protective quantities agree, and shutdown has no unintended open exposure or working orders.

## 2026-05-14 Authority Consolidation Update

Evidence level: build-proven and focused harness-proven only. Not deployed, not runtime-proven, and not deployed-live-proven.

Implemented:

- Added action-level lifecycle/session decisions to `UnifiedExecutionAuthority` through `ExecutionAuthorityAction`, `ExecutionAuthorityActionEvaluationRequest`, and `ExecutionAuthorityActionDecision`.
- Routed terminal stream commits through `UnifiedExecutionAuthority.EvaluateAction(...)` so open lifecycle exposure or pending reentry is denied by the central authority surface instead of by local-only commit logic.
- Routed `SESSION_CLOSE_GLOBAL_SWEEP` through `UnifiedExecutionAuthority.EvaluateAction(...)` before it can request flatten/cancel work.
- Session-close sweep skip logs now include `authority_gate`, `authority_deny_reason`, and `authority_detail`.
- `SESSION_CLOSE_GLOBAL_SWEEP_REQUESTED` now records `authority_gate` when UEA allows the sweep.

Focused proof run:

- `dotnet run --project system/modules/robot/harness/Robot.Harness.csproj -- --test AUTHORITY_CONTRADICTIONS`: PASS.
- `dotnet run --project system/modules/robot/harness/Robot.Harness.csproj -- --test REENTRY_MARKET_CLOSE_COMMIT`: PASS.
- `dotnet build system/RobotCore_For_NinjaTrader/SiblingProtectiveCancelQueue.Test/SiblingProtectiveCancelQueue.Test.csproj -c Release -v:minimal --no-restore`: PASS, 0 errors, existing warning set only.
- Built DLL hash: `BC6E311AFF081B4F5D62FA289BC7FE4594C36A09CEEE382D52A0114DAA42FA53` at `system/RobotCore_For_NinjaTrader/bin/Release/net48/Robot.Core.dll`.

Current consolidation status:

- UEA now owns the decision for terminal commit denial and session-close global sweep denial/allow.
- The sweep still uses the existing enqueue/cancel/flatten mechanics after UEA allows it.
- Remaining legacy decision owners to route next: market reentry admission, forced flatten admission/classification, protective submit admission, and mismatch/reconciliation release.

Next implementation slice:

1. Route market reentry admission through the same UEA action surface.
2. Add focused tests proving reentry is denied when broker/journal/session facts conflict and allowed only when broker-flat plus journal lifecycle facts agree.
3. Keep existing reentry execution mechanics untouched until the authority decision is harness-proven.
