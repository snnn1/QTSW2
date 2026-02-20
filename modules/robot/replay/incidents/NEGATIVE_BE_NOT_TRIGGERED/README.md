# NEGATIVE_BE_NOT_TRIGGERED — Negative Control Pack

**Excluded from CI.** Run manually to validate that the invariant harness correctly fails when BE does not trigger.

**Scenario:** Same as MNQ_BE_NO_TRIGGER — tick 21502 < trigger 21505, so BE correctly does NOT fire.

**Expected:** Invariant `BE_TRIGGERED_BY_STEP` fails with reason `BE_NOT_TRIGGERED`.

```bash
dotnet run --project modules/robot/replay_host/Robot.ReplayHost.csproj -- \
  --determinism-test --file modules/robot/replay/incidents/NEGATIVE_BE_NOT_TRIGGERED/canonical.json \
  --run-invariants --expected modules/robot/replay/incidents/NEGATIVE_BE_NOT_TRIGGERED/expected.json
# Expect: exit code 1, INVARIANTS:FAIL, reason=BE_NOT_TRIGGERED
```
