# MNQ_BE_NO_TRIGGER — Synthetic Incident Pack

**Scenario:** Long MNQ position, entry fill at 21500.25, BE trigger at 21505. Tick at 21502 is **below** trigger → BE does NOT fire (correct).

| Step | Event | Purpose |
|------|-------|---------|
| 0 | IntentRegistered | intent nq_be_001, beTrigger=21505, entry=21500 |
| 1 | IntentPolicyRegistered | Policy for intent |
| 2 | ExecutionUpdate | Entry fill |
| 3 | Tick | tickPrice=21502 < 21505 → BE does not trigger |

**Invariants:** NO_DUPLICATE_EXECUTION_PROCESSED, INTENT_REQUIRES_POLICY_BEFORE_SUBMISSION. No BE invariants (price correctly did not cross).
