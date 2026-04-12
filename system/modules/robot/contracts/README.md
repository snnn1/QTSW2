# Robot.Contracts

**netstandard2.0** — Shared DTOs and interfaces for IEA replay.

## Purpose

- Referenced by **net48** RobotCore_For_NinjaTrader (IEA, LiveClocks)
- Referenced by **net8** Robot.Replay (loader, validation)
- Referenced by **net48** Robot.ReplayHost (replay execution)

**Layering:** Contracts = interfaces + DTOs only. No implementations (e.g. ReplayEventClock lives in Robot.Core).

## Contents

| Type | Purpose |
|------|---------|
| `IEventClock` | Event clock for BE/dedup (replay: event timestamp) |
| `IWallClock` | Wall clock for EnqueueAndWait (Full-System only) |
| `ReplayEventEnvelope` | Wrapper: source, sequence, executionInstrumentKey, type, payload |
| `ReplayEventType` | IntentRegistered, IntentPolicyRegistered, ExecutionUpdate, OrderUpdate, Tick |
| `ReplayIntent` | Branch-relevant intent fields |
| `ReplayIntentRegistered` | IntentRegistered payload |
| `ReplayIntentPolicyRegistered` | IntentPolicyRegistered payload |
| `ReplayExecutionUpdate` | ExecutionUpdate payload |
| `ReplayOrderUpdate` | OrderUpdate payload |
| `ReplayTick` | Tick/MarketData payload |

## Wiring

```
Replay runner (net8) → LoadAndValidate → Contracts DTO
                    → Map to IEA entry points (Phase 4)
```
