# MNQ_OCO_SIBLING_CANCELLED

**Source:** Synthetic incident pack for OCO entry bracket behavior.

## Scenario

- Long and short entry stops in OCO group (breakout strategy)
- Long fills at 21550.25
- Short receives OrderUpdate: Rejected + Comment "CancelPending" (NinjaTrader's OCO sibling cancellation)
- **Expected:** Order state = CANCELLED (not REJECTED). No RecordRejection.

## Run

```bash
dotnet run --project modules/robot/replay_host/Robot.ReplayHost.csproj -- \
  --determinism-test --file modules/robot/replay/incidents/MNQ_OCO_SIBLING_CANCELLED/canonical.json \
  --run-invariants --expected modules/robot/replay/incidents/MNQ_OCO_SIBLING_CANCELLED/expected.json
```

## Invariants

- `ORDER_STATE_BY_STEP`: Short intent (e079d2d1e350342d) order state = CANCELLED at step 6
