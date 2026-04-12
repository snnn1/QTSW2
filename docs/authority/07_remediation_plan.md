# Phase 7 — Remediation plan

**Version:** 1.0  
**Status:** Planning only — execute after sign-off  
**Input:** [06_gap_map_robotcore.md](06_gap_map_robotcore.md)

---

## Sequencing (mandatory order)

1. **Decision authority (EPA)** — address **V-01**, **V-02**, **V-09**  
2. **Transition contract enforcement** — align coordinator with EPA + delegation register in code  
3. **Persistence / run namespace** — **V-04**, **V-05**, **V-08**  
4. **Event / MVEC** — **V-07**  
5. **Narrative / log cleanup** — lowest priority  

---

## Workstreams

| WS | Violations | Goal | Regression risk |
|----|------------|------|-----------------|
| WS-1 | V-01, V-02, V-09 | Introduce explicit EPA (facade) so all gates resolve to one allow/deny + reason; mismatch detection feeds EPA only | Order submission regressions — full adapter test matrix |
| WS-2 | V-04 | Move risk latches + execution summaries under `persistenceBase` or document exceptions | Playback vs live path tests |
| WS-3 | V-05 | Replace or reset singleton persisters per run / state root | Stream restore tests |
| WS-4 | V-08 | Sync RobotCore with modules log rebind or generate from single source | NT compile + sim run |
| WS-5 | V-07 | Emit MVEC-aligned events or document mapping from existing journals | Forensic replay harness |

---

## Required tests / proof points

- **EPA:** Unit tests: each gate produces one reason; no duplicate blockers.  
- **Mismatch:** Contradiction test — broker vs journal inject → **one** EPA outcome.  
- **Persistence:** Single tree listing for isolated playback run.  
- **Regression:** Existing `RobotCore_For_NinjaTrader` test projects where applicable.

---

## Pass criteria (Phase 7)

- Every workstream references **violation IDs**.  
- Ordering respects **S/T** before **F/O** where dependencies require.
