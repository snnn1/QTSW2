# Authority Model Lock Program — documentation index

This folder implements the **Authority Model Lock Program** (Phases 1–8) as **normative documentation**. It does not modify robot code by itself; [07_remediation_plan.md](07_remediation_plan.md) ties to future implementation.

| Phase | Document | Purpose |
|------:|----------|---------|
| 1 | [01_authority_model.md](01_authority_model.md) | Authority matrix, tie-breaks, Market Reality, execution-history window |
| 2 | [02_decision_authority.md](02_decision_authority.md) | EPA, RI/RC/RR classes, gate stacks, implementation audit matrix |
| 2A–2B | [02_phase2ab_addendum.md](02_phase2ab_addendum.md) | RR policy freeze (binary) + path audit vs class model |
| Audit | [phase3_enforcement_audit_robotcore.md](phase3_enforcement_audit_robotcore.md) | Read-only Phase 3 enforcement audit (`RobotCore_For_NinjaTrader`) |
| Gaps | [phase3_gap_pressure_rank.md](phase3_gap_pressure_rank.md) | P3-G1…G5 pressure rank and implementation order |
| Plan | [phase3_remediation_execution_plan.md](phase3_remediation_execution_plan.md) | Refined remediation steps G5→G2→G1→G4→G3 |
| 3 | [03_transition_contract.md](03_transition_contract.md) | Transitions, delegation register, committed/lag |
| 4 | [04_event_model.md](04_event_model.md) | Event taxonomy, MVEC, fields |
| 5 | [05_persistence_model.md](05_persistence_model.md) | Run namespace, root map, co-location |
| 6 | [06_gap_map_robotcore.md](06_gap_map_robotcore.md) | Violations vs `RobotCore_For_NinjaTrader` |
| 7 | [07_remediation_plan.md](07_remediation_plan.md) | Workstreams, sequencing, tests |
| 8 | [08_validation.md](08_validation.md) | Scenario matrix, contradiction/reconstruction tests |

**Read order:** 1 → 2 → 3 → 4 → 5, then 6 → 7 → 8.
