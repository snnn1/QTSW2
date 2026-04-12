# Phase 8 — Validation and proof

**Version:** 1.0  
**Status:** Test plan  
**Prerequisites:** Phases 1–7 approved

---

## Scenario matrix

| Scenario | What must be true | Who decides (normative) | Persistence proof |
|----------|-------------------|-------------------------|-------------------|
| Cold restart | Stream journal reload or fresh if bypass | Engine + `JournalStore` | `TryLoad` result |
| Recovery | Trading blocked until recovery OK | EPA + recovery guard | ENGINE state + logs narrative |
| Mismatch | Single block reason | EPA | Journal + snapshot evidence |
| Flatten | Flatten only via EPA-authorized path | EPA → adapter | Journal exit fills + broker flat |
| Orphan fill | No new risk; incident recorded | EPA blocks | Incident file + journal |
| Playback | Isolated `persistenceBase` | Engine flags | Files under `data/playback/{run_id}` |
| Multi-run same process | No singleton root bleed | Design + WS-3 | Per-run directories |
| Watchdog / operator | Sees narrative; truth = journals | N/A | JSONL optional |

---

## Contradiction tests

1. **Broker qty ≠ journal qty:** Inject divergence → exactly **one** `CONTROL_DECISION` reason from EPA (after WS-1).  
2. **Kill switch on + EPA allow:** Must deny — kill switch wins per Phase 2 order.  
3. **Committed stream journal vs memory:** Disk wins on reload.

---

## Reconstruction tests

- From **ExecutionJournal** + **StreamJournal** + **run_id** — rebuild intent + stream story.  
- MVEC chain present or derivable from authoritative stores.

---

## Pass criteria (Phase 8)

Each scenario answers: **what was true**, **who decided**, **why allowed/blocked**, **what persistence proves it**.
