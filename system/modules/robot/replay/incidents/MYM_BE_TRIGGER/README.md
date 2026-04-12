# MYM_BE_TRIGGER — Synthetic Incident Pack

**Source:** Hand-crafted synthetic events based on [2026-02-18 Break-Even Detection Investigation](../../../docs/robot/incidents/2026-02-18_BREAK_EVEN_DETECTION_INVESTIGATION.md).

**Scenario:** Long MYM position, entry fill at 49875.5, BE trigger at 49940. Tick at 49940 crosses trigger → BE should fire.

| Step | Event | Purpose |
|------|-------|---------|
| 0 | IntentRegistered | intent mym_be_001, beTrigger=49940, entry=49875 |
| 1 | IntentPolicyRegistered | Policy for intent (INTENT_REQUIRES_POLICY invariant) |
| 2 | ExecutionUpdate | Entry fill (tag mym_be_001) |
| 3 | Tick | tickPrice=49940 >= beTrigger → BE triggers |

**Invariants:** NO_DUPLICATE_EXECUTION_PROCESSED, INTENT_REQUIRES_POLICY_BEFORE_SUBMISSION, BE_PRICE_CROSSED_BY_STEP, BE_TRIGGERED_BY_STEP.

**Run:**
```bash
dotnet run --project modules/robot/replay_host/Robot.ReplayHost.csproj -- \
  --determinism-test --file modules/robot/replay/incidents/MYM_BE_TRIGGER/canonical.json \
  --run-invariants --expected modules/robot/replay/incidents/MYM_BE_TRIGGER/expected.json
```
