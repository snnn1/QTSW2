# Phase 5 — Persistence model

**Version:** 1.0  
**Status:** Normative  
**Related:** [01_authority_model.md](01_authority_model.md)

---

## 1. Run boundary

- **`run_id`:** Generated at `RobotEngine.Start()` (or overridden by `QTSW2_RUN_ID` per engine semantics).  
- **Run root:** `{project_root}` for live/default SIM; **`{project_root}/data/playback/{run_id}`** when SIM + `ignoreExistingStreamJournals` (isolated playback).  
- **Single reconstructable universe:** All **truth-bearing** artifacts for answering Phase 1 questions MUST reside under the **active state root** for that run (see hard rule §3).

---

## 2. Namespace model

| Namespace | Path (pattern) | Holds |
|-----------|----------------|-------|
| **Project root** | `<QTSW2_PROJECT_ROOT>` | Config, kill switch, spec, **must migrate** truth artifacts per §3 |
| **State root** | `<persistenceBase>` | `ExecutionJournal`, `JournalStore`, incidents under state tree, optional logs when co-located |
| **Run-scoped playback** | `data/playback/{run_id}` | Isolated stream + execution journals when enabled |
| **Narrative (non-authoritative)** | `logs/robot`, health | May be symlinked or co-located per policy |

---

## 3. Hard rule — truth artifacts in run namespace

**Rule:** Any artifact required to answer a **truth-domain question** from the Phase 1 authority matrix **MUST** live inside the **active run’s state namespace** (`persistenceBase` for the engine run).

**Truth-domain artifacts include (non-exhaustive):**

- Execution journal files (`data/execution_journals`)  
- Stream journals (`logs/robot/journal`)  
- Risk latch files that encode **block** state  
- Control state snapshots if used for permission (if any)  
- Persisted reconciliation outcomes if durable  

**Explicit non-authoritative (may live outside or lag):**

- Primary robot JSONL, health sink, emergency fallback, daily `.md` summaries, strategy lifecycle trace, canonical execution JSONL (replay-supporting, not sole permission truth)

**Violation:** Risk latches or execution summaries written only under project root while stream journals live under `data/playback/...` — **forensic break**; listed in Phase 6.

---

## 4. Root ownership map

| Authority domain | Must write under |
|------------------|------------------|
| Intent/execution ledger | `<state_root>/data/execution_journals` |
| Stream journal | `<state_root>/logs/robot/journal` |
| Control (risk latch) | `<state_root>/data/risk_latches` (normative) — **currently code may use project root** (gap) |
| Kill switch | `<project_root>/configs/robot/` (global config; exception: not run-scoped by nature) |
| Narrative logs | `<resolved_log_dir>` — **not** authoritative |

---

## 5. Artifact classification

| Class | Examples |
|-------|----------|
| **Durable authoritative** | ExecutionJournal JSON, StreamJournal JSON |
| **Replay-supporting** | Canonical execution JSONL, incident packs |
| **Derived** | Execution summaries at stop |
| **Narrative** | Robot JSONL, health, emergency |

---

## 6. Co-location

For one run to reconstruct: **state_root** must contain **both** execution journals **and** stream journals **and** control persistence **if** used for permission (latches).

---

## 7. Pass criteria (Phase 5)

- Run boundary defined.  
- Hard rule §3 stated.  
- No authoritative artifact should float outside state namespace — Phase 6 measures compliance.
