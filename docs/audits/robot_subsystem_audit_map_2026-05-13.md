# QTSW2 Robot Subsystem Audit Map - 2026-05-13

Scope: read-only subsystem mapping for QTSW2 NinjaTrader robot, control plane, timetable, watchdog, dashboard, tools, configs, and evidence artifacts.

Evidence discipline:

- Evidence levels used here: source-only, build-proven, harness-proven, playback-proven, SIM/runtime-proven, deployed-live-proven.
- No deployed-live-proven evidence was found in this pass.
- The current controlling runtime evidence is `runs/31c08ab5b9d342038b212bbdf2a75991`, pointed to by `runs/LATEST_RUN.txt`.
- That run is SIM/runtime-proven failure evidence: `summary.json` reports `FAIL`, `UNSAFE_EXPOSURE`, `STOP`, `OPEN_EXPOSURE_AT_SHUTDOWN`, 12 trades, 34 errors, 29 robot criticals, broker open qty 8, broker working orders 8.
- That run includes deployed assembly proof: `ROBOT_BUILD_SIGNATURE` in `runs/31c08ab5b9d342038b212bbdf2a75991/logs/robot/robot_ENGINE.jsonl` identifies `C:\Users\jakej\Documents\NinjaTrader 8\bin\Custom\Robot.Core.dll`, SHA-256 `ae804c4e445742aeb141503448a233690f6e94da3783348f842e6741e2669560`, last write `2026-05-12T22:27:49.5564436Z`.
- Prior audit docs record earlier clean playback/build/harness proof, but the latest run supersedes those for readiness because it proves an unsafe runtime outcome on the current deployed DLL.
- The worktree was already dirty before this audit. This document does not normalize, revert, or overwrite run artifacts.

## Section 1 - Source Of Truth Map

| Path | Role | Runtime used? | Build target | Risk if edited | Notes |
|---|---:|---:|---|---|---|
| `system/RobotCore_For_NinjaTrader` | Active NinjaTrader runtime DLL source | YES | `system/RobotCore_For_NinjaTrader/Robot.Core.csproj` -> net48 `Robot.Core.dll` | Critical | Runtime project has `TargetFramework net48` and `DefineConstants NINJATRADER` at `Robot.Core.csproj:3-6`; links shared source from core at `Robot.Core.csproj:39-127`. |
| `system/RobotCore_For_NinjaTrader/Robot.Core.csproj` | Active DLL build definition | YES | `Robot.Core.dll` | Critical | Excludes strategy/harness files from DLL compile at `Robot.Core.csproj:27-35`; references NinjaTrader.Core at `Robot.Core.csproj:21`. |
| `system/modules/robot/core` | Shared core source plus harness/net8 target | YES, linked subset | `system/modules/robot/core/Robot.Core.csproj` net8; linked into net48 runtime | Critical | Net8 core target at `Robot.Core.csproj:3`; runtime uses linked files from this tree through the NT project. |
| `system/modules/robot/contracts` | Shared contracts and validators | YES | Referenced by both core and NT projects | Critical | Runtime project references contracts at `system/RobotCore_For_NinjaTrader/Robot.Core.csproj:14`. |
| `system/RobotCore_For_NinjaTrader/Strategies/RobotSimStrategy.cs` | NinjaTrader strategy host source | YES as NT strategy source, not inside `Robot.Core.dll` | NinjaTrader strategy compile/manual copy path | Critical | Runtime DLL project explicitly removes `Strategies\RobotSimStrategy.cs` at `Robot.Core.csproj:31`; class and callbacks at `RobotSimStrategy.cs:42`, `:499`, `:1722`, `:2793`, `:2876`. |
| `system/modules/robot/ninjatrader/RobotSimStrategy.cs` | Strategy source/template mirror | Not proven as loaded runtime | Manual/NT strategy source | High | Similar callback shell; do not assume loaded until NinjaTrader strategy source path is confirmed. |
| `C:\Users\jakej\Documents\NinjaTrader 8\bin\Custom\Robot.Core.dll` | Deployed runtime binary | YES in latest run | Output of net48 build/deploy | Critical | Latest run proves this path/hash via `ROBOT_BUILD_SIGNATURE`; binary is evidence/deploy artifact, not source. |
| `system/NT_ADDONS` | Legacy/manual mirror | NO unless explicitly proven | Not normal deploy target | High confusion risk | Prior audit and deploy script state DLL-only deploy. `tools/deploy_to_ninjatrader.ps1:3` says do not copy AddOns source. |
| `data/timetable` | Live/replay timetable contract and history | YES, read by robot/control plane | Generated JSON authority | Critical | `timetable_current.json` is live authority; `timetable_replay_current.json` is playback authority. Timetable engine documents this at `system/modules/timetable/timetable_engine.py:5-6`. |
| `data/risk_latches` | Durable instrument/risk block state | YES if latch files exist | Runtime state | Critical | `RiskLatchManager` persists/hydrates/clears at `RiskLatchManager.cs:79`, `:100`, `:137`. Never clean blindly. |
| `state/` | Durable journals in non-isolated runs | YES | Runtime state/evidence | Critical | Execution/stream journals survive restart and can block/release trading. |
| `runs/` | Run-scoped evidence and isolated playback state | NO source | Evidence artifact root | Critical evidence integrity | Includes `summary.json`, `AUDIT_MANIFEST.json`, `KEY_EVENTS.jsonl`, `events/`, `state/`, `logs/`. Never manually edit during cleanup. |
| `logs/` | Operational/audit logs | NO source | Evidence | High evidence integrity | Includes timetable publish ledger and runtime logs. |
| `docs/audits` | Audit records | NO runtime | Documentation/evidence | Medium | Prior audit evidence: `robot_cleanup_master_audit_2026-04-30.md`, `mismatch_authority_next_level_map_2026-05-01.md`. |
| `tools/` | Build/deploy/playback/audit tooling | Indirect | Scripts | High | Deploy copies DLL and verifies hashes in `tools/copy_dll_when_ready.ps1:14-39`, `:125-155`. |
| `tools/playback` | Playback scenario tooling | Indirect | Scenario builder | High | Multi-day scenario builder creates run-scoped manifests at `tools/playback/build_multi_day_scenario.py:86-153`. |
| `configs/robot` | Runtime/operator configs and playback scenario pointer archive | YES/indirect | Config | Critical | Includes `kill_switch.json`, `health_monitor.json`, `session_calendar.json`, playback scenario archive. |

Answers:

1. Code that becomes `Robot.Core.dll`: `system/RobotCore_For_NinjaTrader/Robot.Core.csproj` compiles NT-local runtime files plus linked `system/modules/robot/core` shared files and `system/modules/robot/contracts`. It does not compile `Strategies/RobotSimStrategy.cs`.
2. Linked/shared code: the NT project link list at `system/RobotCore_For_NinjaTrader/Robot.Core.csproj:39-127` pulls run summary, key events, execution authority, reconciliation, mismatch, ownership, QEC, session, tests/helpers, and shared models from `system/modules/robot/core`.
3. Legacy/manual mirror only: `system/NT_ADDONS`, unless a separate proof shows NinjaTrader loaded source from there. Normal deploy is DLL-only.
4. Never manually edit during cleanup: `runs/`, `logs/`, `state/`, `data/risk_latches`, live/replay timetable current files, deployed DLLs, KEY_EVENTS, execution/ownership events, stream/execution journals, `AUDIT_MANIFEST.json`, `summary.json`, playback manifests/pointers, bin/obj generated outputs unless doing a controlled build cleanup.
5. Runtime evidence, not source: everything under run roots (`summary.json`, `AUDIT_MANIFEST.json`, `RUN_SHUTDOWN.json`, `KEY_EVENTS.jsonl`, `events/`, `state/`, `logs/`, `derived/`), deployed DLL hash/signature, `logs/timetable_publish.jsonl`, runtime journals/latches.

## Section 2 - Full Subsystem Inventory

| Subsystem | Purpose / primary files | Inputs -> outputs | State owned | Safety responsibility | What can go wrong | Existing tests / recent evidence | Proof / grade | Audit? |
|---|---|---|---|---|---|---|---|---|
| 1. Strategy / NinjaTrader callback shell | NT strategy host: `Strategies/RobotSimStrategy.cs:42`, `OnStateChange:499`, `OnBarUpdate:1722`, `OnOrderUpdate:2793`, `OnExecutionUpdate:2876` | NT bars/orders/executions/account -> engine/adapter calls | NT context, account subscriptions | Keep callbacks fast, wire context before bars/executions | Callback blocking, context race, thread violation | Context-ready comments at `RobotSimStrategy.cs:741-795`; latest run started and processed 43k events | SIM/runtime-proven operation, unsafe run; YELLOW | YES |
| 2. RobotEngine orchestration | `RobotEngine.cs:32`, `Start:1196`, reconciliation wiring `:1698-1751`, timetable reload `RobotEngine.Timetable.cs:195`, stop/summary `RobotEngine.Shutdown.cs:259-384` | Strategy ticks/timetable/config/state -> streams, IEA, reconciliation, summaries | Engine locks, stream set, latch manager, run root | Single orchestration frame for session, streams, audit, shutdown | Conflicting authority, stale session, unsafe stop | Latest run ended `OPEN_EXPOSURE_AT_SHUTDOWN`; hash proven | SIM/runtime-proven failure; RED | YES |
| 3. Timetable/session authority | `RobotEngine.Timetable.cs:63`, `:478`; `RobotEngine.Session.cs:1088`; timetable engine `timetable_engine.py:5-6`, `:2262`, `:2295` | `timetable_current/replay`, session authority, CME calendar -> stream schedule | Active session window, stream specs | Only valid streams may arm; live/replay separation | Stale day, wrong replay file, rollover clears active streams | Current multi-day playback run failed; dashboard tests for publish authority | Source + harness, latest SIM failure context; RED | YES |
| 4. StreamStateMachine lifecycle | `StreamStateMachine.cs:13`, `:332`, `Tick:1097`; range flow `RangeFlow.cs:18`, `:1027`, `:1355`; commit `Commit.cs:362` | Bars/session/engine events -> stream states, intents, journals | Per-stream state, lock snapshots, journal status | State transitions must be deterministic and terminal only when safe | Same-stream collisions, stale DONE, premature commit | Stream journal playback bypass tests; current incomplete streams at shutdown = 4 | Harness + SIM failure; RED | YES |
| 5. Range building / bracket entry logic | `RangeFlow.cs:1146`, `:1355`; `EntryBrackets.cs:299`, `:330`, `:1760` | Bars/range config -> bracket/market entry decision | Range high/low, breakout decision | Avoid invalid/crossed stop and OCO reuse | Invalid stop, duplicate bracket, missed breakout | Latest run had 12 range locks and 78 submits, zero rejects | SIM/runtime-proven partial; YELLOW | YES |
| 6. Entry submit / order command path | `NinjaTraderSimAdapter.SubmitGate.cs:456`, `:933`, `:1050`, `:1075`; `InstrumentExecutionAuthority.Commands.cs:290` | Stream intent -> submit gate -> IEA -> NT | Submit preflight frame, command queue | Serialize entry submit and deny unsafe submits | Duplicate entry, submit while blocked, race with recovery | No rejects in latest run; extra late intents need audit | SIM/runtime-proven operation, failure context; YELLOW | YES |
| 7. IEA / Instrument Execution Authority | `InstrumentExecutionAuthority.cs:20`, stall timer `:225`, command stall `:322`, register intent `:765`; commands `Commands.cs:19`, `:67` | Commands, order/execution ingress -> order actions/state | Per-instrument command queue, lifecycle, registry | Serialized per-instrument execution authority | Command stall, recovery normal queue starvation, wrong flatten path | Execution command and stall tests exist; current MNG/MYM exposure persisted | Harness + SIM failure; RED | YES |
| 8. Order registry | `InstrumentExecutionAuthority.OrderRegistry.cs:14`, working count `:130`, adoption policy restore `:376-470`, adopted order `:480` | NT order updates/adoption snapshots -> owned/adopted registry | Registry rows, order-id mapping, quantity policy | Identify robot-owned/adopted orders and working exposure | False manual/external, missing policy, stale working orders | Adoption recovery and hydration tests; latest working orders counted 8 | Harness + SIM failure; YELLOW | YES |
| 9. Intent lifecycle | `IntentLifecycle.cs:15`, transition `:29`; `IntentLifecycleValidator.cs:8`, valid transitions `:10-30` | Commands/fills/cancels -> lifecycle states | Intent lifecycle map | Prevent invalid command/order sequences | `INTENT_LIFECYCLE_TRANSITION_INVALID`, duplicate reentry | Intent lifecycle tests; current derived summary shows 28 intents, 16 executed | Harness + SIM evidence; YELLOW | YES |
| 10. Quantity policy / overfill guard | `ExecutionJournal.cs:1147`, `OrderRegistry.cs:376-470`, QEC `QuantExecutionControlStore.cs:254`, `:350` | Fill quantities, adopted orders -> remaining/overfill classification | Intent qty policy, open qty, QEC flags | Prevent overfill and unknown quantity adoption | `INTENT_OVERFILL_EMERGENCY`, missing policy, silent under/over-protection | Quantity/QEC tests; no latest overfill but open qty remained | Harness + source; YELLOW | YES |
| 11. Execution update handling | Strategy callback `RobotSimStrategy.cs:2876`, IEA host `HostIea.cs:52`, ingress queue `CallbackIngress.cs:131` | NT executions -> IEA/journal/ownership/protection | Fill callbacks, execution events | Fast callback, complete journal and ownership truth | Callback block, missed fill, orphan fill | Latest orphan fills files empty; fills processed 31 | SIM/runtime-proven partial; YELLOW | YES |
| 12. Order update handling | Strategy callback `RobotSimStrategy.cs:2793`, IEA host `HostIea.cs:24`, ingress queue `CallbackIngress.cs:149` | NT order updates -> registry/QEC/protection | Order state registry, pending orders | Track acceptance/rejection/cancel/working | Coalescing hides terminal, stale working order | Latest orders rejected 0, working at shutdown 8 | SIM/runtime-proven partial; YELLOW | YES |
| 13. ExecutionJournal | `ExecutionJournal.cs:14`, submission `:103`, entry fill `:387`, exit fill `:659`, overfill `:1147`, rejection `:1262` | Submit/fill/reject/cancel -> durable execution rows | Execution journals | Durable fill truth and remaining qty | Stale open rows, false broker-flat closure, corruption fail-closed | Latest journal open qty shutdown 8; prior audit had clean run | SIM/runtime-proven detection; YELLOW | YES |
| 14. Stream journal | `JournalStore.cs:78`, `TryLoad:264`, `Save:287`, `StreamJournal:342`, forced flatten marker `:99-195` | Stream state -> durable stream journal | Stream journal, forced flatten/reentry markers | Restart/reentry/time-exit continuity | Stale terminal, hidden flatten marker, carryover wrong day | Playback bypass tests; latest incomplete streams 4 | Harness + SIM failure; RED | YES |
| 15. Ownership ledger / snapshots | `InstrumentOwnershipLedger.cs:10`, entry fill `:49`, exit fill `:72`, orphan `:100`, snapshot `:320`; `RobotEngine.Ownership.cs:109` | Mapped fills/journals/broker snapshots -> ownership events/snapshots | Ledger slots, orphan fills, snapshots | Broker exposure must be explainable by robot ownership | Ledger disagreement, orphan exposure, wrong slot close | Latest MNG/MYM snapshots correctly showed open qty and working orders | SIM/runtime-proven detection; YELLOW/GREEN | YES |
| 16. ReconciliationRunner | `ReconciliationRunner.cs:13`, run internal `:360`, mismatch `:542-683`, broker-flat closure guard `:689-812` | Broker/account/order/journal/ledger -> mismatch or release | Mismatch observations, closure decisions | Broker is primary truth; do not falsely claim flat | False complete, missed mismatch, repair hides issue | Latest `FLATTEN_BROKER_POSITION_REMAINS`; did not claim safe | SIM/runtime-proven guard; YELLOW | YES |
| 17. MismatchEscalationCoordinator | `MismatchEscalationCoordinator.cs:25`, block authority `:133`, consistency gate `:146`, forced convergence `:577`, release checks `:592+` | Reconciliation observations -> blocks/releases/fail-closed | Mismatch state, convergence windows | Block trading on unexplained exposure and release only after stable proof | Block never clears, clears too early, overlap with UEA/structural | Mismatch convergence/contract tests; latest exposure explainable, not mismatch-blocked | Harness + source; YELLOW | YES |
| 18. QuantExecutionControlStore | `QuantExecutionControlStore.cs:11`, protective pending `:36`, stop submitted `:100`, trusted fill `:254`, unmapped `:350` | Order/fill/protective state -> quant safety flags | Per-instrument execution control rows | Coordinate protection alignment and submit windows | Stale pending window, wrong trusted fill, blocks protective | QEC tests; current open exposure needs audit | Harness + source; YELLOW | YES |
| 19. UEA / Unified Execution Authority | `UnifiedExecutionAuthority.cs:15`, evaluate `:63`, kill/recovery/EPA `:103-117`, mismatch `:125-128`, structural `:149-163`, overlay `:167-177` | Preflight frame -> allow/deny | No durable state | Single decision facade for submit permission | Conflicts with EPA/structural/overlay, bypass risk | Runtime flags enabled in `AUDIT_MANIFEST`; authority contradiction tests | Harness + SIM active; YELLOW | YES |
| 20. EPA / Execution Permission Authority | `ExecutionPermissionAuthority.cs:12`, adapter preflight `:48` | Submit request -> preflight allow/deny | No durable state | Early block unsafe adapter submit | Duplicates UEA/submit gate, stale block reason | EPA adapter preflight tests | Harness + source; YELLOW | YES |
| 21. Structural safety layer | `ExecutionStructuralLayer.cs:17`, order structure `:430`, flatten `:787`, authority matrix `:927` | Broker/account/journal/registry frame -> structural decision | No durable state | Deny structurally inconsistent submits | Fallback/bypass complexity, false allow | Authority contradiction tests; runtime flag active | Harness + SIM active; YELLOW | YES |
| 22. Overlay / execution safety gate | `ExecutionSafetyGate.cs:10`, unsafe lock `:41`, overlay eval `:124`, broker snapshot acceptable `:220` | Overlay locks/broker snapshot -> allow/deny | Overlay lock state | Last safety lock over submits | Stale hard lock or false clear | Source/test coverage only in this pass | Source/harness; YELLOW | YES |
| 23. Durable risk latches / instrument freezes | `RiskLatchManager.cs:79`, clear `:100`, hydrate `:137`; watchdog clear readiness `aggregator_main.py:255-318` | Critical events/operator clear -> durable latch | Latch files | Fail-closed instrument block | Hidden block, auto-clear too broad, stale latch | Watchdog operator tests read latch files | Harness + source; YELLOW | YES |
| 24. Protective stop/target handling | `NinjaTraderSimAdapter.NT.ProtectiveSubmit.cs:110`, `:147`, submit stop `:569`, target `:664`; SSM reentry protection `Reentry.cs:511-522` | Fills/reentry -> stop/target orders | Protective OCO/order IDs | Full broker exposure must be covered | Missing stop, qty mismatch, protective reject, crossed stop | Latest had 25 stops/25 targets submitted, no rejects, but exposure remained | SIM/runtime partial; YELLOW | YES |
| 25. Protective audit / coverage verification | `ProtectiveCoverageAudit.cs:32`, missing stop `:114-127`, qty mismatch `:132-146`, critical `:225`; coordinator `ProtectiveCoverageCoordinator.cs:221-435` | Broker snapshot/QEC -> coverage status/recovery/flatten | Coverage state, recovery attempts | Detect under-protection and escalate | Pending window hides real gap, recovery loop fails | Protective audit tests; latest no protective critical in summary but open exposure | Harness + SIM gap; YELLOW | YES |
| 26. Break-even logic | `NinjaTraderSimAdapter.NT.BreakEven.cs:27`, `:52`; backlog timeout `NinjaTraderSimAdapter.BreakEven.cs:37-74` | Price/fill/order state -> modify stop | BE pending/backlog | Move stop safely without blocking callback | Modify wrong order, timeout flatten, thread violation | Tests exist in broader suite; not current focus | Source/harness; YELLOW | YES |
| 27. Forced flatten | `NinjaTraderSimAdapter.FlattenCommands.cs:27-36`, `:384`, `:676-688`; SSM forced flatten files; latest engine events | Session/UEA/recovery -> cancel/flatten orders | Flatten latches, pending commands | Get broker flat, verify flat, fail if not | `FLATTEN_BROKER_POSITION_REMAINS`, latch timeout, order remains | Latest run has MNG position remains and final open exposure | SIM/runtime-proven failure; RED | YES |
| 28. Market reentry | `StreamStateMachine.Reentry.cs:20`, broker-flat gate `:111`, canonical id `:218`, enqueue `:250`, pending `:273-274`, protectives `:511-522` | Flatten result/original entry/session -> reentry intent | Reentry markers/intent id | Reenter exactly once only when broker flat and allowed | Duplicate reentry, reentry after time exit, protectives fail | Latest derived summary shows extra MNG/MYM reentry intents late | SIM/runtime failure context; RED | YES |
| 29. Scheduled time exit / result commit | `Commit.cs:362`, session window `RobotEngine.Session.cs:1295`, time exit flows in engine/SSM | Session close/time exit -> flatten/commit/DONE | Commit flags, trade result | Do not mark terminal until broker/journal safe | Commit while open, stale terminal, time exit race | Latest shutdown unsafe, incomplete streams 4 | SIM/runtime-proven failure; RED | YES |
| 30. Restart/re-enable adoption | Adapter hydration `Context.cs:504`, adoption candidates `:542`; order registry adoption `OrderRegistry.cs:480`; recovery `RobotEngine.Reconciliation.Recovery.cs:508` | Existing broker orders/positions/journals -> adopted state or fail-closed | Adopted registry rows, adoption markers | Restart cannot hide live exposure | Missing qty policy, false manual/external, broker-flat false close | Adoption recovery tests; no latest restart proof | Harness-proven only; YELLOW | YES |
| 31. Carryover / prior-day recovery | `StreamStateMachine.cs:878-1052`, `ExecutionJournal.cs:292`, `:421`, `PlaybackScenarioTests.cs:248` | Prior day journals/scenario -> next-day carryover | Carryover stream/execution state | Preserve active exposure across day boundary | Multi-day clears active streams, wrong day scope | Latest multi-day playback failed with open exposure | SIM/runtime failure; RED | YES |
| 32. Multi-day playback scenario | `PlaybackScenarioManifest` usage in `RobotEngine.cs:538-549`; builder `tools/playback/build_multi_day_scenario.py:86-153`; tests `PlaybackScenarioTests.cs:11` | Manifest/timetables/journals -> run-scoped playback | Scenario manifest/pointer | Single explicit playback scope; not live proof | Scenario pointer stale, false live inference, rollover bug | Latest run `isolated_playback=true`, failed unsafe | SIM/runtime failure; RED | YES |
| 33. Playback runtime clock / event-clock handling | `HealthMonitor.Evaluation.cs:41`, `:129-174`; `RobotEngine.PlaybackStall.cs:39-518` | Event-clock/wall-clock/tick stall -> stall or shutdown signal | Playback stall latches | Do not spin forever; classify stall without hiding exposure | False crash/freeze, force finalize with exposure | Latest `playback_stall_live_exposure_timeout` critical | SIM/runtime-proven detection; YELLOW/RED | YES |
| 34. Run summary / verdict builder | `RunSummaryBuilder.cs:10`, `Build:17`, status logic `:52-67`, recommended action `:307`; shutdown writer `RobotEngine.Shutdown.cs:305-384` | KEY_EVENTS/logs/journals/snapshots -> summary verdict | Run summary files | Operator verdict must not claim safe falsely | Empty KEY_EVENTS, zeroed summary, false safe | Latest summary correctly STOP/UNSAFE_EXPOSURE | SIM/runtime-proven detection; GREEN/YELLOW | YES |
| 35. KEY_EVENTS / execution event audit | `RunRootArtifacts.cs:15-18`, key writer paths; `ExecutionEventWriter.cs:19` | Critical lifecycle events -> run-root timeline | KEY_EVENTS, execution_events | Audit trail for operator and summary | Empty key events, missing critical, duplicate sequence | Latest KEY_EVENTS populated with protection/flatten events | SIM/runtime partial; YELLOW | YES |
| 36. Watchdog backend/operator state | `aggregator_main.py:181`, `:670`, `:822`, startup liveness invalidation `:1462`, stale tick rejection `:2697-2718`, operator snapshot `:3371` | Logs/runs/timetable/latches -> operator state | Watchdog state/cache/cursors | Surface exact unsafe/stale/deployment states | False safe, false critical, stale run context | Watchdog tests; no current live operator proof | Harness/source; YELLOW | YES |
| 37. Matrix/timetable generation | `timetable_engine.py:2262`, `:2295`, live write guards `:2470-2589`; write guard `timetable_write_guard.py:2`; publish ledger `timetable_publish_ledger.py:18` | Matrix/filter/session -> live/replay timetable | Timetable files/history/ledger | Publish complete, session-aligned execution contract | Wrong day, incomplete stream set, stale eligibility | Dashboard timetable execution authority tests | Harness/source; YELLOW | YES |
| 38. Dashboard/frontend controls | Backend publish `main.py:905`, authority state `:948-981`, live current read `:1975`; frontend modes `App.jsx:180`, `:3494-3515`, explanatory UI `:3670-3675` | Operator actions -> publish/preview/scenario pointer | In-memory matrix, session authority, UI state | Avoid accidental live mutation; expose authority mode | UI hides proof level, stale matrix, wrong mode | Backend tests; frontend not runtime proof | Harness/source; YELLOW | YES |
| 39. Deployment/build/hash verification | Deploy script `copy_dll_when_ready.ps1:14-39`, hash verify `:125-155`; deploy script says DLL-only at `deploy_to_ninjatrader.ps1:3` | Build output -> deployed DLL -> runtime signature | Build/deploy hashes | Source/binary/runtime identity | DLL mismatch, OneDrive path, stale DLL | Latest run has deployed hash proof | SIM/runtime-proven hash; GREEN/YELLOW | YES |
| 40. Config/feature flags | `FeatureFlags.cs:29`, `:68`, `:123`; `configs/robot/*`; playback pointer archive | Config -> authority windows/modes | JSON configs/env vars/static flags | Guardrails default fail-closed | Unsafe shadow/repair flag, stale scenario pointer | Source/tests; latest manifest lists flags | Source + SIM active; YELLOW | YES |
| 41. Kill switch / operator disable | `KillSwitch.cs:13`; UEA kill/recovery gate `UnifiedExecutionAuthority.cs:103-117`; configs `kill_switch.json` | Operator/config -> entry/submit disable | Kill switch file/state | Disable must block trading deterministically | Stale disable, bypass via protective/flatten path | Source only in this pass | Source-only; YELLOW | YES |
| 42. Health monitor / crash/freeze diagnostics | `HealthMonitor.cs:56-136`, evaluation `HealthMonitor.Evaluation.cs:68`, engine stall `:97`, lifecycle `HealthMonitor.Lifecycle.cs:11` | Engine ticks/bars/connections -> WARN/CRITICAL | Health state, dedupe timers | Detect stall/freeze without blocking callbacks | False freeze, missed stall, alert spam | Latest playback stall detected | SIM/runtime-proven detection; YELLOW | YES |
| 43. Logging service / event severity registry | `RobotLoggingService.cs:20`, worker `Worker.cs:22`, daily summary `DailySummary.cs:22`; `RobotEventTypes.LevelMap.cs:20`, critical map examples `:842-852`, `:965`, `:993` | Runtime events -> async logs/summary severity | Log queues/files | Preserve evidence and severity | Dropped logs, wrong severity, backpressure | Latest daily summary and summary populated | SIM/runtime-proven; YELLOW/GREEN | YES |
| 44. Harness/test framework | `system/modules/robot/core/Tests`, `SiblingProtectiveCancelQueue.Test/Program.cs:113-196`, watchdog tests | Unit/harness scenarios -> pass/fail | Temp state only | Prove contracts below playback/SIM level | Harness drift, no runtime equivalence | Many named harness tests exist; not rerun here | Harness-proven historically, not current; YELLOW | YES |

## Section 3 - Subsystem Dependency Graph

| Subsystem | Depends on | Feeds into | Durable writes | NT API touch? | Can block entry? | Can affect protection? | Can affect flatten? |
|---|---|---|---:|---:|---:|---:|---:|
| Strategy shell | NinjaTrader callbacks/account/instrument | RobotEngine, adapter | No | YES | Indirect | Indirect | Indirect |
| RobotEngine | Strategy, timetable, configs, journals, latches | Streams, IEA, reconciliation, summary | YES | Indirect | YES | YES | YES |
| Timetable/session | Timetable JSON, session authority, CME calendar | Engine stream enable/session windows | YES via timetable publish | No | YES | Indirect | YES via time exit |
| StreamStateMachine | Engine bars/session/journals/adapter | Entry/reentry/flatten/protection commands | YES | No | YES | YES | YES |
| Submit gate/UEA/EPA/structural/overlay | Preflight frame, broker/account/journal/registry | IEA/NT submit allow-deny | No | Indirect | YES | YES | YES |
| IEA | Adapter commands, order/execution ingress | NT actions, registry, journal updates | No direct durable except through collaborators | Indirect | YES | YES | YES |
| Adapter NT runtime queues | IEA/strategy thread | Real NT account/order calls | No | YES | YES | YES | YES |
| ExecutionJournal | Adapter fills/orders | Reconciliation, summary, adoption | YES | No | YES via fail-closed | YES | YES |
| Ownership ledger | Execution fills, journals, broker snapshots | Reconciliation, snapshots, watchdog | YES | No | YES via truth frame | YES | YES |
| Reconciliation/mismatch | Broker/account/journal/ledger/registry | Blocks/releases/repair/summary | YES logs/events | Indirect | YES | YES | YES |
| Protective handling/audit | Fills, broker positions/orders, QEC | Protective submits/recovery/flatten | YES events/logs | YES via adapter | YES for unsafe state | YES | YES |
| Forced flatten/reentry/time exit | Session/stream/journal/broker state | Cancel/flatten/reentry/protection | YES journals/markers | YES | YES | YES | YES |
| Health/logging/summary | Engine/log/events/journals | Operator verdict, watchdog | YES | No | No direct | No direct | No direct |
| Watchdog/dashboard/timetable UI | Runs/logs/timetable/latches/matrix | Operator state, timetable publish | YES for timetable/session authority | No | Indirect | Indirect | Indirect |
| Deploy/build/hash | Build outputs, target NT path | Deployed DLL/runtime signature | YES evidence | No | Indirect | Indirect | Indirect |

Plain-English flow:

Market data enters `RobotSimStrategy.OnBarUpdate`, which calls `RobotEngine.OnBar`. The engine resolves timetable/session authority, ticks each `StreamStateMachine`, and a stream decides whether range/bracket conditions justify an entry. The stream builds an intent and sends it through submit gates (`UEA`, `EPA`, structural layer, overlay) into the IEA command queue. IEA serializes the command and the adapter executes the NT submit on the strategy thread. NT order updates flow back through callback ingress into IEA/order registry/QEC. NT execution updates flow through IEA into `ExecutionJournal`, ownership ledger, protective submit logic, and execution events. Protective coverage and reconciliation compare broker truth against journals/ledger/registry. Watchdog and run summary consume logs, KEY_EVENTS, execution events, ownership snapshots, timetable state, latches, and deployed hash proof to report operator truth.

## Section 4 - Safety-Critical Authority Map

| Decision | Current owner(s) | File/function | Inputs | Output | Overlap/conflict risk | Correct single owner? |
|---|---|---|---|---|---|---|
| Allow/deny entry | UEA, EPA, structural layer, overlay, RiskGate, StreamStateMachine | `UnifiedExecutionAuthority.Evaluate:63`, `ExecutionPermissionAuthority:48`, `ExecutionStructuralLayer:430`, `ExecutionSafetyGate:124`, `RiskGate.CheckGates:47` | Preflight frame, latches, mismatch, broker/account, stream state | Allow/deny reason | High: many gates can deny; must share one coherent authority frame | UEA should be facade; sublayers supply reasoned inputs |
| Allow/deny reentry | StreamStateMachine reentry, broker-flat gate, UEA/EPA/structural, latches | `Reentry.cs:20`, `:111`, `:218`, `:250` | Forced flatten state, broker flat, original entry, session | Reentry intent or denial | High: latest run suggests reentry/time-exit conflict | Needs audit |
| Allow/deny protective submit | Protective submit path, EPA risk coverage path, UEA, coordinator | `ProtectiveSubmit.cs:110`, `:147`; `UnifiedExecutionAuthority.cs:103-182`; `ExecutionPermissionAuthority.cs:14` | Filled exposure, pending windows, latches, mismatch | Stop/target submit or deny | High: protective submit must not be blocked by ordinary entry latches unless explicitly safe | Needs audit |
| Allow/deny flatten | IEA command validator, adapter flatten gate, structural layer | `Commands.cs:206`, `IntentLifecycleValidator.cs:52`, `SubmitGate.cs:926`, `FlattenCommands.cs:27-36` | Exposure, lifecycle, NT context | Flatten command/action | Medium-high: flatten must bypass entry blocks but stay broker-safe | Needs audit |
| Classify mismatch | ReconciliationRunner, ReconciliationClassifier, MismatchCoordinator | `ReconciliationRunner.cs:542-683`, `ReconciliationClassifier.cs:13`, `MismatchEscalationCoordinator.cs:146` | Broker/journal/ledger/registry | Tier/block/release | High: must not contradict ownership ledger | Coordinator owns block/release; runner owns observation |
| Mark journal complete | ExecutionJournal, ReconciliationRepairExecutor | `ExecutionJournal.RecordExitFill:659`; repair `ReconciliationRepairExecutor.cs:48-83` | Exit fills, broker-flat repair proof | Closed journal row | High: false complete hides exposure | ExecutionJournal primary; repair narrowly scoped |
| Mark stream terminal | StreamStateMachine commit/time exit | `Commit.cs:362-450` | Journal saved, stream state, broker/journal proof | DONE/terminal | High: terminal stream with open exposure is unsafe | Stream owns only after broker/journal safe proof |
| Create/clear latch | RiskLatchManager, protective coordinator, watchdog operator clear | `RiskLatchManager.cs:79`, `:100`; `ProtectiveCoverageCoordinator.cs:393-399`; `aggregator_main.py:255-318` | Critical reason, flat proof, operator action | Durable block/clear | Medium: stale hidden block vs unsafe auto-clear | Runtime owner creates; operator/watchdog clear only with proof |
| Trigger fail-closed | UEA, mismatch coordinator, journal corruption, protective coordinator, kill switch | UEA gates `:103-128`; mismatch `:577+`; protective `:421-435`; kill switch config | Critical inconsistency | Entry block/flatten/latch | High: multiple fail-closed paths must agree | Allowed multi-owner, but all must report same reason frame |
| Claim broker flat | ReconciliationRunner, adapter flatten verify, run summary | `ReconciliationRunner.cs:689-812`, `FlattenCommands.cs:676-688`, `RunSummaryBuilder.cs:52-67` | Broker account/positions/orders | Flat/safe claim | Critical: broker is primary truth | Broker snapshot/reconciliation should be source; summary consumes |
| Claim shutdown safe | RunSummaryBuilder, RobotEngine shutdown, watchdog | `RunSummaryBuilder.Build:17`, `RobotEngine.Shutdown.cs:305-384`, `aggregator_main.py:3371` | Summary inputs, ownership, broker, criticals | Verdict/recommend action | Critical: latest correctly claimed unsafe | Summary should consume evidence, not override |

Authority conflict hotspots: `UEA`, `EPA`, `RiskGate`, structural layer, overlay, reconciliation, `MismatchEscalationCoordinator`, `QuantExecutionControlStore`, `StreamStateMachine`, `ExecutionJournal`, and watchdog all influence safety decisions. The desired model is one coherent authority frame: broker truth and durable journals/ledger feed reconciliation; UEA is submit facade; StreamStateMachine owns lifecycle intent only after authority approval; watchdog/summary report, not decide runtime truth.

## Section 5 - Durable State Inventory

| State store | Path | Writer | Reader | Schema/key | Lifetime | Clear/retire condition | Failure mode |
|---|---|---|---|---|---|---|---|
| Execution journals | `state/execution_journals`, `runs/<id>/state/execution_journals` | `ExecutionJournal` | Reconciliation, adoption, summary, watchdog | trading date, stream, intent, instrument, order/fill rows | Trade/restart/recovery | Entry fully exited or explicit repair with proof | Stale open row blocks trading; false closed row hides exposure |
| Stream journals | `state/stream_journals`, `runs/<id>/state/stream_journals` | `JournalStore`, SSM | SSM, playback scenario, watchdog | trading date, stream id, slot instance, state | Stream/day/carryover | Stream safely terminal and retained as evidence | Stale DONE or missing carryover causes skipped/duplicated trades |
| Risk latches | `data/risk_latches` | `RiskLatchManager`, protective/mismatch paths | Engine, UEA/EPA, watchdog | account/instrument/reason/created time | Until cleared with proof/operator | Broker flat and clear-readiness or explicit operator clear | Hidden block or unsafe auto-clear |
| Live timetable | `data/timetable/timetable_current.json` | Dashboard/API matrix publish | Engine, watchdog, dashboard | session_trading_date, streams/slot times | Current live session | Next approved publish | Stale day blocks/arms wrong streams |
| Replay timetable | `data/timetable/timetable_replay_current.json` | Historical publish/playback tools | Playback engine/watchdog | replay session streams | Playback run | New replay publish/clear | Replay authority contaminates live or stale playback |
| Timetable history/ledger | `data/timetable/history`, `logs/timetable_publish.jsonl` | Timetable engine/ledger | Audit/watchdog/operator | content hash, publish context | Permanent evidence | Never manually cleanup without audit | Lost publish provenance |
| Playback scenario manifests/pointers | `runs/<scenario>/playback_scenario/playback_scenario.json`, `configs/robot/playback_scenario_archive/*` | `tools/playback`, dashboard backend | RobotEngine, dashboard | scenario_id, run_id, day timetables/hashes | Scenario run | Explicit pointer clear/archive | Stale pointer triggers wrong multi-day playback |
| Ownership events/snapshots | `runs/<id>/events/ownership_*` | Ownership ledger/emitter | Reconciliation, summary, watchdog | account/instrument/slot/open qty/time | Run/evidence | Never manual cleanup in active run | False exposure explanation or missing proof |
| Execution events | `runs/<id>/events/execution_events` | `ExecutionEventWriter` | replay/audit/summary | canonical execution event | Run/evidence | Never manual cleanup in active run | Replay parity loss |
| KEY_EVENTS | `runs/<id>/KEY_EVENTS.jsonl` | Run root/key event writer | Run summary/audits/operator | key event type, time, stream/instrument | Run/evidence | Never manual edit | Empty/missing critical leads false summary |
| Run summary/shutdown/manifest | `runs/<id>/summary.json`, `RUN_SHUTDOWN.json`, `AUDIT_MANIFEST.json` | RobotEngine shutdown/run artifacts | Operator, watchdog, tools | status/verdict/hash/mode | Run/evidence | Immutable evidence | False go/no-go or lost DLL proof |
| Watchdog state/cache/cursors | Watchdog state files and in-memory state | Watchdog aggregator/state manager | Dashboard/operator | stream status/cursors/liveness | Runtime/watchdog process | Startup invalidation or controlled reset | False safe/critical from stale state |
| Robot configs/flags | `configs/robot/*`, `FeatureFlags.cs` | Operator/source | Engine/health/kill switch/tools | JSON/static flags | Until changed | Controlled config change with audit | Unsafe mode, stale kill switch, bad health thresholds |

Mandatory questions:

1. Can stale state block trading? Yes: latches, stream journals, execution journals, timetable, kill switch, playback scenario pointer.
2. Can stale state hide exposure? Yes: false-closed execution journals, stale watchdog cache, bad summary inputs, missing ownership snapshots.
3. Can stale state create false safety? Yes: stale broker-flat/session-safe claims or empty KEY_EVENTS can make a run look safer than broker truth.
4. Can stale state survive restart? Yes: journals, latches, timetable files, configs, playback pointers, run roots.
5. Is it scoped by account/instrument/trading date/run id? Partially. Run-scoped playback evidence is strong; journals are trading date/stream/intent/instrument scoped; account scoping should be explicitly audited, especially for latches and non-isolated state.

## Section 6 - Known Failure Family Map

| Failure family | Subsystem | Last seen evidence | Current fix status | Proof level | Remaining audit needed |
|---|---|---|---|---|---|
| OCO reuse | Range/bracket/protective submit | Unique OCO handling in `EntryBrackets.cs:1760`, `ProtectiveSubmit.cs:285` | Appears addressed in source | Source/harness | Verify no OCO reuse in current runtime logs |
| Crossed stop rejection | Range/bracket, protective submit | Breakout validity `EntryBrackets.cs:299-323`; latest run rejected 0 | Not current failure | SIM/runtime no rejects | Replay exact day with stop-crossing assertions |
| NT playback crash/freeze | Health/playback stall | Latest `playback_stall_live_exposure_timeout`; `RunSummaryBuilderTests.cs:565-582` covers durable signal | Detection works, root cause open exposure remains | SIM/runtime failure | Separate stall classification from exposure root cause |
| NT thread violation | Runtime queues/adapter | `EnsureStrategyThreadOrEnqueue` at `RuntimeQueues.cs:299`, violation at `:315`; canary tests | Guard exists | Harness/source | Confirm latest run no thread violations |
| Execution command stall | IEA | Stall timer/check `InstrumentExecutionAuthority.cs:225`, `:322` | Guard exists | Source/harness | Check latest run command stall count and IEA pending |
| `INTENT_LIFECYCLE_TRANSITION_INVALID` | Intent lifecycle/IEA | Validator `IntentLifecycleValidator.cs:10-30`; tests | Guard exists | Harness | Audit duplicate late reentry lifecycle |
| `INTENT_OVERFILL_EMERGENCY` | Quantity policy/journal | Overfill detection `ExecutionJournal.cs:1147` | Guard exists | Harness/source | Prove with current fill/reentry paths |
| `INTENT_QUANTITY_POLICY_MISSING` | Adoption/order registry | Adopted qty policy restore `OrderRegistry.cs:376-470` | Guard exists | Harness/source | Restart/adoption playback/SIM proof |
| `RECONCILIATION_BROKER_FLAT` false completion | Reconciliation/journal repair | Broker-flat closure guard `ReconciliationRunner.cs:689-812`; latest did not falsely close | Improved but high risk | SIM/runtime guard | Focus audit on false completion release proof |
| Broker/journal/ledger mismatch | Reconciliation/ownership | Latest MNG/MYM explainable open exposure, ownership open qty 8 | Detection present | SIM/runtime failure | Audit explainable exposure vs block behavior |
| `EXIT_OVERFLOW` | Ownership/journal qty | Ledger exit fill `InstrumentOwnershipLedger.cs:72` | Guard exists | Source/harness | Runtime assertions in exit storms |
| `WORKING_ORDER_COUNT_CONVERGENCE` | QEC/mismatch | QEC pending windows and mismatch convergence tests | Guard exists | Harness | Audit stale working orders in latest run |
| `PROTECTIVE_MISSING_STOP` | Protective audit | `ProtectiveCoverageAudit.cs:114-127`; tests | Guard exists | Harness | Confirm no hidden missing stop in latest open exposure |
| `PROTECTIVE_QTY_MISMATCH` | Protective audit | `ProtectiveCoverageAudit.cs:132-146`; tests | Guard exists | Harness | Prove aggregate exposure across streams |
| Durable latch not clearing | Risk latches/watchdog | `RiskLatchManager.Clear:100`; watchdog clear readiness `aggregator_main.py:255-318` | Needs audit | Source/harness | Verify latch scope and clear proof |
| Hidden instrument block | Risk latches/UEA/EPA | Latch hydration `RiskLatchManager.cs:137`; UEA kill/recovery gate | Needs audit | Source | Operator visibility and stale file test |
| False `MANUAL_OR_EXTERNAL_ORDER_DETECTED` | Order registry/adoption | Severity map `RobotEventTypes.LevelMap.cs:993`; registry adoption | Needs audit | Source/harness | Restart with tagged/adopted orders |
| Duplicate reentry | Reentry/SSM/IEA | Latest derived summary has 28 intents, 16 executed, late MNG/MYM reentry intents | Active concern | SIM/runtime failure context | Exact failing day reentry audit |
| `FLATTEN_LATCH_TIMEOUT` | Forced flatten | Severity map `RobotEventTypes.LevelMap.cs:965` | Needs audit | Source | Prove latch transitions in failure run |
| `FLATTEN_BROKER_POSITION_REMAINS` | Forced flatten/reconciliation | Latest MNG errors at `2026-05-12T23:13:52` and `23:13:56` | Active failure family | SIM/runtime-proven | Root cause audit before prop |
| `BAR_RECEIVED_NO_STREAMS` spam | Strategy/engine/timetable | Known warning family; not primary latest failure | Unknown current status | Source/run evidence needed | Count recent runs and dedupe |
| KEY_EVENTS empty | Run artifacts/summary | Prior audit fixed; latest KEY_EVENTS populated | Not current | SIM/runtime-proven populated | Ensure criticals mirrored into KEY_EVENTS |
| Derived execution summary zeroed | Summary builder | Prior audit fixed; latest derived summary populated | Not current | SIM/runtime-proven populated | Regression test with empty/partial events |
| Watchdog false safe/critical | Watchdog | Startup liveness invalidation and stale tick rejection in `aggregator_main.py:1462`, `:2697-2718` | Needs live proof | Harness/source | Feed latest unsafe run to watchdog |
| Multi-day rollover clearing streams | SSM/playback scenario | Latest multi-day playback failed open exposure; carryover code `StreamStateMachine.cs:878-1052` | Active concern | SIM/runtime failure | Focused rollover audit |
| Same-stream id collision/defer | Stream journal/adoption | Playback scenario tests and stream journal carryover | Needs audit | Harness | Runtime slot-instance evidence |
| Adoption missing quantity policy | Adoption/order registry | `OrderRegistry.cs:376-470` | Guard exists | Harness | Restart/re-enable SIM proof |
| Protective submit blocked by latch | Protective/UEA/EPA/latches | Risk coverage paths exist; no direct latest proof | Needs audit | Source | Prove protective bypass/allow path while entries blocked |
| Open exposure at shutdown | Forced flatten/reentry/time exit/protection | Latest `summary.json` and ownership snapshots: MNG -4, MYM +4, working orders 8 | Active blocker | SIM/runtime-proven failure | P0 root cause audit/fix |

## Section 7 - Existing Tests And Proof Coverage

| Test | Subsystem covered | What it proves | What it does not prove | Last known status |
|---|---|---|---|---|
| `IntentLifecycle` / `IntentLifecycleValidator` | Intent lifecycle | Valid/invalid transitions | Runtime duplicate/reentry races | Harness exists; not rerun this pass |
| `ExecutionCommand` tests | IEA command serialization | Command contracts and ordering | Real NT queue latency/callback timing | Harness exists; not rerun |
| `AdoptionRecoveryTests`, `ReconciliationRecoveryScenarioTests` | Restart/adoption/recovery | Journal/order adoption scenarios | Live NT restart with deployed DLL | Harness exists; not current runtime proof |
| `ReentryMarketCloseCommitTests.cs` | Reentry/time exit/commit | Market-close commit contracts | Latest multi-day runtime failure | Harness exists; latest SIM failure supersedes readiness |
| `ForcedFlattenPolicyTests` | Forced flatten | Policy-level flatten behavior | Broker position remains in NT runtime | Harness exists; needs exact failing day |
| `StreamJournalPlaybackBypassTests.cs:13` | Stream journal/playback bypass | Playback bypass ignores committed stream rows | Multi-day live rollover proof | Harness exists |
| `PlaybackScenarioTests.cs:11` | Multi-day playback manifest/carryover | Manifest loading, pointer, carryover scenario contracts | Full NT playback runtime safety | Harness exists; latest SIM failed |
| `OrderReconciliationRecoveryTests` | Reconciliation/adoption | Recovery scenarios | Broker/account live edge timing | Harness exists |
| `AuthorityContradictionTests.cs:12` | UEA/EPA/structural contradictions | Coherent authority deny reasons | Runtime all-gate parity under NT | Harness exists; referenced by runner |
| `RunSummaryBuilderTests.cs:7` | Summary/verdict | Summary classification including exposure/stall | Real run completeness of all inputs | Harness exists; latest summary detected unsafe |
| `RUN_SUMMARY_BUILDER` in NT harness | Summary in net48 harness | NT-target summary builder | Live deployment | Harness entry in `Program.cs:194-199` |
| `MismatchEscalation` / `StateConsistencyGateTests.cs` | Mismatch coordinator | Block/release/gate lifecycle | Real broker convergence timing | Harness exists |
| `MismatchConvergenceContractTests.cs` | Mismatch convergence | Suppress/release contracts | Latest explainable exposure decisions | Harness exists |
| `MismatchConvergenceBridgeProbe` | Reconciliation bridge | Bridge/probe contracts | Runtime IEA/NT race | Harness exists |
| `ProtectiveCoverageAuditTests.cs:11` | Protective coverage | Missing/qty mismatch/pending windows | Full NT protective submit acceptance/modify | Harness exists |
| `ExecutionEventReplay` / `ExecutionScenarioRunner.cs` | Event replay | Canonical event replay parity | NT account truth | Harness exists |
| Watchdog operator tests | Watchdog truth/latches/timetable | Stale/latch/current/replay status contracts | Live operator display during NT run | Python tests exist, not rerun |
| Platform diagnostics tests | Crash/freeze diagnostics | Summary/watchdog classification | Actual NT process failure semantics | Python tests exist |

Coverage gaps:

- No current build was run in this audit turn.
- No current harness suite was run in this audit turn.
- Latest proof is runtime failure, not a pass.
- Forced flatten/reentry/time exit, carryover, and multi-day playback need exact failing-day replay audit first.
- Protective coverage needs aggregate multi-stream exposure proof, not just per-intent tests.
- Watchdog/dashboard need proof that latest unsafe run is surfaced as unsafe with deployed hash and next operator action.
- Deployed-live-proven evidence is absent.

## Section 8 - Grade Every Subsystem

| Subsystem | Grade | Why | Evidence | Biggest risk | Next audit/fix |
|---|---:|---|---|---|---|
| Strategy callback shell | YELLOW | Runtime callbacks work, but callback/thread safety remains execution-critical | Source + latest runtime | Blocking NT callback | Callback ingress/thread audit |
| RobotEngine orchestration | RED | Latest deployed run ended unsafe | SIM/runtime failure | Conflicting lifecycle/session/shutdown authority | Forced flatten/reentry/time-exit root cause |
| Timetable/session authority | RED | Multi-day playback/rollover is not safe proof and latest failed | SIM/runtime failure | Wrong day/session with open streams | Rollover audit |
| StreamStateMachine lifecycle | RED | Incomplete streams/open exposure at shutdown | SIM/runtime failure | Premature terminal or late reentry | Lifecycle audit |
| Range/bracket entry | YELLOW | Zero rejects/latest entries work, but late intents need audit | SIM/runtime partial | Duplicate/invalid entry | Entry audit |
| Entry submit/order command | YELLOW | Submit path active, but command/reentry race possible | SIM/runtime partial | Duplicate or blocked safety submit | IEA audit |
| IEA | RED | Serialized authority did not end flat in latest run | SIM/runtime failure | Command convergence after flatten | IEA/flatten audit |
| Order registry | YELLOW | Working orders counted; adoption risks remain | Harness + runtime | Stale registry rows | Registry/adoption audit |
| Intent lifecycle | YELLOW | Harnessed, but latest has extra intents | Harness + runtime | Duplicate reentry lifecycle | Intent audit |
| Quantity/overfill | YELLOW | Guards exist, no latest overfill | Harness/source | Missing adoption policy | Quantity audit |
| Execution updates | YELLOW | Fills processed/no orphan files | SIM/runtime partial | Missed or duplicated execution | Callback/journal audit |
| Order updates | YELLOW | Orders tracked/no rejects, working remained | SIM/runtime partial | Stale working order state | Order registry audit |
| ExecutionJournal | YELLOW | Correctly retained open qty, but stale/repair risk high | SIM/runtime detection | False complete | Journal truth audit |
| Stream journal | RED | Carryover/terminal state implicated | SIM/runtime failure | Stale terminal/carryover | Stream journal audit |
| Ownership ledger | YELLOW/GREEN | Correctly explained open exposure | SIM/runtime detection | Ledger/account mismatch | Ownership audit |
| ReconciliationRunner | YELLOW | Did not claim false safe, but did not resolve exposure | SIM/runtime detection | False release | Mismatch release audit |
| Mismatch coordinator | YELLOW | Harnessed, complex release paths | Harness/source | Release too early/stuck block | Mismatch audit |
| QEC store | YELLOW | Harnessed, but runtime protection windows complex | Harness/source | Stale pending | QEC audit |
| UEA/EPA/structural/overlay | YELLOW | Active and tested, many overlapping gates | Harness + manifest | Conflicting authority | Authority audit |
| Latches/freezes | YELLOW | Persistent safety rail, clear semantics need proof | Source/harness | Hidden block or unsafe clear | Latch audit |
| Protectives/audit | YELLOW | Stops/targets submitted, no rejects, exposure remained | SIM/runtime partial | Silent under-protection | Protective audit |
| Break-even | YELLOW | Source/harness only this pass | Harness/source | Wrong stop modify | Focus later unless implicated |
| Forced flatten | RED | Latest MNG position remained and shutdown unsafe | SIM/runtime failure | Broker not flat | P0 audit/fix |
| Market reentry | RED | Late MNG/MYM reentry intents in failure context | SIM/runtime failure context | Duplicate reentry | P0 audit/fix |
| Scheduled time exit | RED | Session shutdown failed safe criterion | SIM/runtime failure | Commit while exposed | P0 audit/fix |
| Restart/adoption | YELLOW | Harnessed, no latest restart proof | Harness | False manual/adoption miss | Audit before prop |
| Carryover/prior-day | RED | Latest multi-day playback failed | SIM/runtime failure | Clears/duplicates active streams | P0 audit |
| Playback scenario/clock | RED/YELLOW | Correctly reported stall, but failure root remains | SIM/runtime failure | False proof from playback | Workflow audit |
| Run summary/verdict | GREEN/YELLOW | Correctly called unsafe/STOP | SIM/runtime | Missing future critical inputs | Summary regression audit |
| KEY_EVENTS/events | YELLOW | Populated; need critical completeness | SIM/runtime partial | Empty/missing event | Event audit |
| Watchdog/operator | YELLOW | Source/tests, no latest operator proof | Harness/source | False safe | Feed latest run |
| Matrix/timetable generation | YELLOW | Guarded source/tests | Harness/source | Wrong live publish | Timetable audit |
| Dashboard/frontend | YELLOW | Modes present; proof-level UI not verified | Source/harness | Hides WARN/CRITICAL | UI audit |
| Deploy/hash | GREEN/YELLOW | Latest hash proved deployed DLL | SIM/runtime | Source/DLL mismatch later | Keep as release gate |
| Config/flags | YELLOW | Many active flags, repair default false | Source + manifest | Unsafe flag combination | Config audit |
| Kill switch | YELLOW | Source/config path exists | Source | Bypass | Kill-switch audit |
| Health/logging/severity | YELLOW/GREEN | Stall and unsafe summary captured | SIM/runtime | Misclassification/noise | Severity audit |
| Harness framework | YELLOW | Broad tests exist, not current pass | Harness historically | Harness drift | Run focused suites |
| `system/NT_ADDONS` | GRAY | Legacy/inactive unless proven | Prior audit/source | Confusion | Do not use as runtime source |

## Section 9 - Focused Audit Prompt Backlog

| Audit name | Subsystem | Why needed | Core question | Files | Known risks | Required output | Priority |
|---|---|---|---|---|---|---|---|
| Execution Recovery / Adoption Audit | Restart/adoption, registry, journal | Restart can hide broker exposure | Can broker positions/orders be adopted or fail-closed with quantity policy intact? | Adapter context, IEA order registry, ExecutionJournal, ReconciliationRecovery | Missing qty policy, false manual/external | Source map, tests, exact restart scenario | P1 |
| Forced Flatten / Reentry / Time Exit Audit | Forced flatten, reentry, time exit | Latest runtime failure | Why did MNG/MYM remain open after flatten and late reentries? | SSM forced flatten/reentry/commit, adapter flatten, IEA | Duplicate reentry, flatten verification, session close race | Root cause and focused fix plan | P0 |
| Latch / Block Authority Audit | Latches, UEA/EPA, watchdog | Hidden blocks can stop/protect incorrectly | Who can create/clear instrument blocks and can protectives bypass entry blocks safely? | RiskLatchManager, UEA/EPA, watchdog | Hidden stale latch, unsafe clear | Authority table and tests | P1 |
| Protective Coverage Audit | Protectives, audit, QEC | Capital safety depends on coverage | Does total stop/target coverage match broker exposure across streams? | ProtectiveSubmit, ProtectiveCoverageAudit/Coordinator, QEC | Under-protection, stale pending | Per-instrument aggregate proof | P0 |
| Stream Lifecycle / Timetable Rollover Audit | SSM, journals, timetable | Latest multi-day failure | Can active streams and exposure survive day rollover without duplicate/clear? | SSM, JournalStore, PlaybackScenario, Timetable | Carryover bug, terminal stale | Exact failing day and representative day proof | P0 |
| Reconciliation / Mismatch Release Audit | Reconciliation, mismatch | False release can hide exposure | Does release require broker/journal/ledger/order convergence? | ReconciliationRunner, MismatchCoordinator | Broker-flat false complete | Release contract proof | P1 |
| Ownership / Journal Truth Audit | Ledger/journal/events | Broker truth must be explainable | Do journal, ledger, broker, and events agree after fills/exits/restart? | ExecutionJournal, OwnershipLedger, ExecutionEventWriter | False safety, stale open qty | Cross-store truth matrix | P1 |
| Order Registry / Intent Lifecycle Audit | IEA, registry, lifecycle | Duplicate/invalid intents are high risk | Can commands, registry rows, and lifecycle diverge? | IEA partials, IntentLifecycleValidator | Duplicate reentry, stale working | Invariant list and tests | P1 |
| Watchdog / Operator Truth Audit | Watchdog, dashboard, summary | Operators must see exact unsafe action | Does latest unsafe run surface as STOP with hash/exposure/action? | Watchdog aggregator, dashboard backend/frontend | False safe, false critical | Operator proof snapshot | P1 |
| Deployment / Runtime Proof Audit | Build/deploy/hash | Source proof is not runtime proof | Does deployed DLL match source/build for every run? | deploy scripts, RobotEngine signature, run artifacts | Stale DLL, OneDrive path | Release checklist and hash gate | P1 |
| Playback / SIM Proof Workflow Audit | Playback tools, run summary | Playback has limits and latest failed | Which run proves what, and what cannot be inferred? | tools/playback, RunSummaryBuilder, docs | Treat playback as live proof | Proof matrix | P0 |
| Config / Feature Flags Audit | Configs, FeatureFlags, kill switch | Flags can alter safety semantics | Which flags can block/protect/repair/reenter? | `FeatureFlags.cs`, `configs/robot` | Unsafe combo, stale scenario pointer | Flag table and defaults | P1 |

## Section 10 - Prop Evaluation Readiness By Subsystem

| Subsystem | Prop readiness requirement | Current grade | Required proof before prop | Can wait? |
|---|---|---:|---|---:|
| Forced flatten/reentry/time exit | MUST_BE_GREEN_BEFORE_PROP | RED | Exact failing-day fix, then representative playback/SIM with broker flat shutdown | NO |
| Protective coverage | MUST_BE_GREEN_BEFORE_PROP | YELLOW | Aggregate broker exposure coverage proof and no missing/qty mismatch | NO |
| Reconciliation/mismatch release | MUST_BE_GREEN_BEFORE_PROP | YELLOW | No false broker-flat, stable release only after convergence | NO |
| Ownership/journal truth | MUST_BE_GREEN_BEFORE_PROP | YELLOW | Broker/journal/ledger/events agree at shutdown and restart | NO |
| Timetable/session rollover | MUST_BE_GREEN_BEFORE_PROP | RED | Single-day playback for failing day; SIM/runtime for live rollover if used | NO |
| IEA/order registry/intent lifecycle | MUST_BE_GREEN_BEFORE_PROP | YELLOW/RED | No duplicate reentry, no command stall, registry converges | NO |
| Deployment/hash verification | MUST_BE_GREEN_BEFORE_PROP | GREEN/YELLOW | Build hash = deployed hash = runtime signature every run | NO |
| Watchdog/operator truth | MUST_BE_GREEN_BEFORE_PROP | YELLOW | Latest unsafe/safe states visible with next action | NO |
| Kill switch/config flags | MUST_BE_GREEN_BEFORE_PROP | YELLOW | Disable/repair/default flags audited | NO |
| Run summary/KEY_EVENTS/logging | CAN_BE_YELLOW_WITH_GUARDS | YELLOW/GREEN | Summary catches unsafe; key events populated | YES, with guards |
| Matrix/timetable generation UI | CAN_BE_YELLOW_WITH_GUARDS | YELLOW | Publish guard and no accidental live mutation | YES, if live publish controlled |
| Dashboard/frontend polish | CAN_WAIT_UNTIL_AFTER_PROP | YELLOW | Must not hide WARN/CRITICAL; polish can wait | YES |
| Break-even | CAN_BE_YELLOW_WITH_GUARDS | YELLOW | No active BE criticals in prop path | YES, unless implicated |
| Harness framework cleanup | POST_EVALUATION_CLEANUP | YELLOW | Focused harnesses pass | YES |
| `system/NT_ADDONS` cleanup | POST_EVALUATION_CLEANUP | GRAY | Keep untouched until source boundary final | YES |

## Section 11 - Ranked Master Plan

### P0 - before next SIM/runtime

| Task | Subsystem | Why it matters | Files likely involved | Behavior change? | Proof needed | Risk | Rollback plan |
|---|---|---|---|---:|---|---|---|
| Re-run/parse exact latest failing day before changing code | Forced flatten/reentry/time exit | Establish root cause from evidence first | `runs/31c...`, SSM, adapter flatten, IEA, journals | NO | Evidence-only root cause | Low | No code change |
| Audit why MNG/MYM remained open after session flatten | Forced flatten/reconciliation | Latest shutdown unsafe | `FlattenCommands`, `Reentry`, `ReconciliationRunner`, ownership snapshots | NO first | Source/run crosswalk | Medium | Evidence doc only |
| Audit duplicate/late reentry intents | Reentry/SSM/IEA | Could create/extend exposure | `Reentry.cs`, `JournalStore`, IEA lifecycle | NO first | Intent timeline | Medium | Evidence doc only |
| Verify protective coverage in latest open exposure | Protective audit | Determine protected vs under-protected | Protective audit/coordinator, KEY_EVENTS, orders | NO | Aggregate exposure/stop/target table | Low | Evidence doc only |
| Run focused harnesses after any fix | Relevant P0 subsystem | Prevent regressions | Tests named above | NO/YES depending fix | Harness-proven | Low | Revert only own patch |

### P1 - before prop evaluation

| Task | Subsystem | Why it matters | Files likely involved | Behavior change? | Proof needed | Risk | Rollback plan |
|---|---|---|---|---:|---|---|---|
| Implement smallest fix for root cause found in P0 | Targeted | Latest runtime failure blocks prop | Narrow files from audit | YES | Build, harness, failing-day playback/SIM | Medium | Revert own change |
| Prove deployment hash gate | Deploy/runtime | No source-only readiness | Deploy scripts, signature log | Possibly | Build hash/deployed hash/runtime hash | Low | Restore prior DLL only with audit |
| Feed latest unsafe run through watchdog/operator path | Watchdog/dashboard | Operator must see STOP/exposure/hash/action | Watchdog aggregator, dashboard API/UI | Maybe | Harness/operator snapshot | Low | Revert UI/backend patch |
| Audit configs/feature flags and scenario pointer clear | Config/playback | Stale pointer can alter next run | `configs/robot`, `FeatureFlags.cs` | Maybe | Config matrix | Low | Restore config from git with approval |
| Run one representative clean SIM/runtime after fix | Full robot | Need runtime proof | Runtime deployment | NO code | SIM/runtime-proven pass | Medium | Stop, inspect exposure |

### P2 - after prop start / during monitored SIM

| Task | Subsystem | Why it matters | Files likely involved | Behavior change? | Proof needed | Risk | Rollback plan |
|---|---|---|---|---:|---|---|---|
| Strengthen watchdog proof-level labels | Operator UI | Better operator decisions | dashboard/frontend/watchdog | YES UI only | UI tests/manual check | Low | Revert UI patch |
| Broaden random/event replay coverage | Harness | Catch edge event ordering | Execution scenario tests | NO runtime | Harness | Low | Remove new tests |
| Tighten run artifact integrity audit | Tools | Evidence confidence | `tools/run_folder_integrity_audit.py` | YES tool only | Tool run on latest | Low | Revert tool patch |

### P3 - architecture cleanup later

| Task | Subsystem | Why it matters | Files likely involved | Behavior change? | Proof needed | Risk | Rollback plan |
|---|---|---|---|---:|---|---|---|
| Retire or clearly fence `system/NT_ADDONS` mirror | Source hygiene | Avoid editing wrong source | docs/scripts only first | NO | Source boundary proof | Medium | Restore docs/scripts |
| Separate mechanical refactors from authority changes | All | Reduce review risk | Narrow batches | NO | Build/harness per batch | Medium | Revert own batch |
| Simplify overlapping authority layers only after runtime proof | UEA/EPA/structural/overlay | Reduce contradiction risk | Authority files | YES | Build/harness/SIM | High | Keep fallbacks until proven |

## Section 12 - Final Verdict

1. Major robot subsystems: strategy callback shell, RobotEngine, timetable/session authority, StreamStateMachine, range/bracket entry, submit path, IEA, order registry, intent lifecycle, quantity/overfill, order/execution callbacks, execution journal, stream journal, ownership ledger, reconciliation, mismatch coordinator, QEC, UEA/EPA/structural/overlay, latches, protectives/audit, break-even, forced flatten, reentry, time exit, restart/adoption, carryover, playback scenario/clock, run summary/KEY_EVENTS, watchdog, timetable/matrix, dashboard/frontend, deploy/hash, configs/flags, kill switch, health/logging, harness framework.
2. Strongest subsystems: deployment hash recording, run summary unsafe verdict, ownership snapshots, logging/severity capture, broad harness coverage. These are strong at detecting/reporting, not at proving current trading safety.
3. Weakest subsystems: forced flatten/reentry/time exit, multi-day playback/carryover, stream journal terminal/carryover behavior, IEA convergence during shutdown, and the combined authority frame around reentry/protection/flatten.
4. Prop blockers: latest runtime `OPEN_EXPOSURE_AT_SHUTDOWN`, broker working orders at shutdown, MNG/MYM exposure after flatten, duplicate/late reentry concern, no current clean SIM/runtime pass on the deployed DLL.
5. Cleanup later: `system/NT_ADDONS` mirror, broad authority simplification, UI polish, mechanical refactors, harness framework organization.
6. Where the robot still argues with itself: submit authority is split across UEA/EPA/structural/overlay/RiskGate; stream lifecycle, journal completion, reconciliation release, and run summary each can express terminal/safe state; reentry/time exit/flatten authority overlaps with latches and broker-flat proof.
7. Focused audits next: forced flatten/reentry/time exit first; protective aggregate coverage second; stream lifecycle/timetable rollover third; reconciliation/journal/ownership truth fourth; watchdog/operator truth and deployment proof in parallel.
8. Smallest master plan to prop safely: do not rewrite. Audit the exact failing run, isolate root cause, apply the smallest targeted fix, run focused harnesses, redeploy with hash proof, rerun exact failing day, rerun one representative day/SIM, and require broker flat/no working orders/no criticals before prop.

Final recommendation: NO-GO for prop evaluation and no serious next runtime until the P0 forced-flatten/reentry/time-exit failure is root-caused and proven fixed. The latest deployed DLL is not safe enough for prop because it ended with open broker exposure and working orders at shutdown. Source-only or harness-only proof cannot override that runtime failure.
