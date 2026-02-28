# Testing IEA Without a Live Trade

You can exercise the Instrument Execution Authority (IEA) logic without waiting for a real trade in two ways:

1. **Replay system** — Synthetic events, no-op executor (no NT orders). Tests IEA core logic.
2. **Test inject trade** — Real market order in SIM on first Realtime bar. Tests full IEA + adapter flow.

## What Gets Tested

| Path | Replay | Test Inject (SIM) | Live NT |
|------|--------|-------------------|---------|
| IntentMap, allocation, BE trigger evaluation | ✅ | ✅ | ✅ |
| EvaluateBreakEvenDirect, OrderMap updates | ✅ | ✅ | ✅ |
| ProtectionState, ModifyStopToBreakEven (adapter) | ❌ | ✅ | ✅ |
| ScanAndAdoptExistingProtectives, PruneIntentState | ❌ | ✅ | ✅ |

Replay tests **IEA core logic** only. **Test inject** runs the full flow in NinjaTrader SIM (real order → fill → protectives → BE).

---

## Test Inject Trade (Option 2)

Inject a synthetic market entry on the first Realtime bar to exercise the full IEA flow in NinjaTrader SIM.

### Setup

1. Deploy the strategy: `batch\DEPLOY_ROBOT_TO_NINJATRADER.bat`
2. In NinjaTrader, add a chart with MES, MNQ, or MYM (SIM account)
3. Add RobotSimStrategy to the chart
4. Set **Test inject trade on start** = true
5. Optionally set **Test inject direction** (Long/Short) and **Test inject quantity** (1–2)
6. Enable the strategy

### What Happens

On the first bar in Realtime, the strategy:

1. Creates a synthetic intent (stream `TEST_INJECT`)
2. Registers intent and policy in the IEA
3. Submits a **market order** (fills immediately in SIM)
4. On fill → protective stop + target are submitted
5. On tick at BE trigger → stop is modified to break-even

Log event `TEST_INJECT_EXECUTED` confirms success. Full IEA + adapter flow runs (ProtectionState, PruneIntentState, etc.).

### Notes

- **SIM only** — Uses real NT orders in the Sim account
- **Runs once** — Only on first Realtime bar
- **Flatten manually** — After testing, flatten the position or let stop/target work

### Stream isolation and journal hygiene

| Concern | Status |
|---------|--------|
| **Stream name** | Uses `TEST_INJECT` — unique, does not collide with production streams (ES1, ES2, NG1, YM1, etc.) |
| **Timetable** | Not in timetable — no timetable logic, risk gating, or health gating |
| **P&L metrics** | `StreamPnLAggregator` excludes streams starting with `TEST` — test trades do not contaminate day/portfolio summaries |
| **Risk gating** | Bypasses `StreamStateMachine` and `RiskGate` — not involved |
| **Journal persistence** | Test trades are written to `data/execution_journals/{date}_TEST_INJECT_{intentId}.json` — excluded from metrics at read time |

---

## Replay (Option 1)

### Quick Start

### 1. Determinism test (no invariants)

```powershell
dotnet run --project modules/robot/replay_host/Robot.ReplayHost.csproj -- --determinism-test --file modules/robot/replay/incidents/MYM_BE_TRIGGER/canonical.json
```

Expected: `DETERMINISM:PASS` and `DETERMINISM:steps=5`.

### 2. Determinism test with invariants

```powershell
dotnet run --project modules/robot/replay_host/Robot.ReplayHost.csproj -- --determinism-test --file modules/robot/replay/incidents/MYM_BE_TRIGGER/canonical.json --run-invariants --expected modules/robot/replay/incidents/MYM_BE_TRIGGER/expected.json
```

### 3. Single run + final hash

```powershell
dotnet run --project modules/robot/replay_host/Robot.ReplayHost.csproj -- --file modules/robot/replay/incidents/MYM_BE_TRIGGER/canonical.json
```

Output: `HASH:final=<sha256>`.

### 4. Per-step hashes (debugging)

```powershell
dotnet run --project modules/robot/replay_host/Robot.ReplayHost.csproj -- --per-step-hashes --file modules/robot/replay/incidents/MYM_BE_TRIGGER/canonical.json
```

---

## Using JSONL (net8 Replay)

If you have a JSONL file instead of canonical JSON:

```powershell
dotnet run --project modules/robot/replay/Robot.Replay.csproj -- --determinism-test --file modules/robot/replay/incidents/SAMPLE/events.jsonl
```

This loads JSONL, writes canonical to a temp file, and runs ReplayHost.

---

## Creating a Minimal Test File

Create `my_iea_test.jsonl` (one JSON object per line):

```jsonl
{"source":"ES","sequence":0,"executionInstrumentKey":"ES","type":"IntentRegistered","payload":{"intentId":"test_001","intent":{"tradingDate":"2026-02-18","stream":"ES1","instrument":"ES","executionInstrument":"ES","session":"RTH","slotTimeChicago":"09:30","direction":"Long","entryPrice":5000.0,"stopPrice":4990.0,"targetPrice":5010.0,"beTrigger":5005.0,"entryTimeUtc":"2026-02-18T15:30:00Z","triggerReason":"RANGE"}}}
{"source":"ES","sequence":1,"executionInstrumentKey":"ES","type":"IntentPolicyRegistered","payload":{"intentId":"test_001","expectedQty":1,"maxQty":2,"canonical":"ES","execution":"ES","policySource":"timetable"}}
{"source":"ES","sequence":2,"executionInstrumentKey":"ES","type":"ExecutionUpdate","payload":{"executionId":"exec_001","orderId":"ord_001","fillPrice":5000.25,"fillQuantity":1,"marketPosition":"Long","executionTime":"2026-02-18T15:31:00Z","tag":"test_001","intentId":"test_001","executionInstrumentKey":"ES"}}
{"source":"ES","sequence":3,"executionInstrumentKey":"ES","type":"Tick","payload":{"tickPrice":5005.0,"tickTimeFromEvent":"2026-02-18T15:31:05Z","executionInstrument":"ES"}}
```

Then convert to canonical and run:

```powershell
dotnet run --project modules/robot/replay/Robot.Replay.csproj -- --write-canonical --file my_iea_test.jsonl --out my_iea_test.canonical.json
dotnet run --project modules/robot/replay_host/Robot.ReplayHost.csproj -- --determinism-test --file my_iea_test.canonical.json
```

Or run determinism directly from JSONL:

```powershell
dotnet run --project modules/robot/replay/Robot.Replay.csproj -- --determinism-test --file my_iea_test.jsonl
```

---

## Extracting Incidents from Logs

To replay a real incident from robot logs:

```powershell
dotnet run --project modules/robot/replay/Robot.Replay.csproj -- --extract-incident --from data/robot_logs/2026-02-18.jsonl --out incidents/my_incident --instrument ES --pre-events 200 --post-events 200
```

This produces `canonical.json` in the output directory. Run determinism on it as above.

---

## Reference

- **IEA_REPLAY_CONTRACT.md** — Event contract and invariants
- **modules/robot/replay/incidents/** — Example incident packs (SAMPLE, MYM_BE_TRIGGER, MNQ_BE_NO_TRIGGER, etc.)
