# Storage / logging audit — replay capture (filled)

**Machine / root:** `C:\Users\jakej\QTSW2` (repo used as NinjaTrader **project root** / `_root` in typical runs).

**Sample isolated run folder:** `data\playback\bfb077d0c86241a1b867cafc4cef45e3\` (captured 2026-04-11 per directory listing).

**Evidence date:** 2026-04-11 (filesystem enumeration).

---

## Section 1 — Root mapping (actual values)

| root_name | actual_path | how_set | when_set | changes_runtime? | used_by | run_scoped? |
|-----------|---------------|---------|----------|------------------|---------|-------------|
| **`_root`** | `C:\Users\jakej\QTSW2` | Strategy `projectRoot` / engine ctor | Engine init | **No** | `configs/**`, `KillSwitch` (`configs\robot\kill_switch.json`), `data\timetable`, `data\session`, `RobotLoggingService` ctor **first arg** → **`logs\health`**, logging config load, `EngineCpuProfile`, test triggers under `data\` | **No** |
| **`_persistenceBase`** (default) | `C:\Users\jakej\QTSW2` (same as `_root`) | Ctor `= projectRoot` | Engine init | **Yes** when rebind runs | Same tree as root until rebind | **No** (when equal to `_root`) |
| **`_persistenceBase`** (isolated example) | `C:\Users\jakej\QTSW2\data\playback\bfb077d0c86241a1b867cafc4cef45e3` | `RebindPersistenceIfNeeded` → `data\playback\{run_id}` | **`Start()`** path (SIM + ignore stream journals / playback) | **Yes** | `JournalStore`, `ExecutionJournal`, `StreamStateMachine` state root, decision JSONL under `data\`, `RiskLatchManager`, `ExecutionEventWriter` root, execution summaries | **Yes** |
| **`_resolvedLogDir`** (typical) | `C:\Users\jakej\QTSW2\logs\robot` | Default or `configs\robot\logging.json` / `QTSW2_LOG_DIR` | Ctor | **Yes** after rebind → aligns with `_persistenceBase\logs\robot` when SIM isolated | `RobotLogger` JSONL, `RobotLoggingService` primary writers | **Tied to persistence when isolated** |
| **`automation_logs` (engine)** | `C:\Users\jakej\QTSW2\automation\logs\...` when `_persistenceBase` = repo root | `ExecutionEventWriter`: `<persistenceBase>\automation\logs\execution_events\{yyyy-MM-dd}\` | First canonical event write | Root follows `_persistenceBase` | Gap 5 canonical execution events | **Under persistence root** (not a separate drive) |
| **Health sink root** | `C:\Users\jakej\QTSW2\logs\health` | `RobotLoggingService`: `Path.Combine(projectRoot, "logs", "health")` with **`projectRoot` = `_root` always** | Service ctor | **No** | Filtered WARN+ / selected INFO → `*.jsonl` per slot key | **No — global under `_root`** |

**Conclusion (Section 1):** There are **multiple effective roots**: **`_root`** (configs + **health**), **`_persistenceBase`** (journals, decisions, canonical events when using that base), and **pipeline `automation\logs`** (orchestrator markers + any engine events if persistence = repo root).

---

## Section 2 — Actual folder tree (from disk)

### A. Global repo tree (not per-run)

**`<root>\logs\robot\`** — mixed: narrative JSONL, hydration/ranges, diagnostics, daily markdown, scripts.

Representative **files** (not exhaustive — journal subfolder alone has **thousands** of stream journal JSONs across dates):

- `robot_ENGINE.jsonl`, `robot_NQ.jsonl`, `robot_CL.jsonl`, `robot_GC.jsonl`, `robot_NG.jsonl`, `robot_RTY.jsonl`, `robot_ES.jsonl`, `robot_MNQ.jsonl`, `robot_YM.jsonl`
- Rotated ENGINE files: e.g. `robot_ENGINE_20260411_155616.jsonl`, `robot_ENGINE_20260411_183200.jsonl`, …
- `execution_trace.jsonl`, `frontend_feed.jsonl`, `iea_execution_latency.jsonl`, `order_update_integrity_forensic.jsonl`
- `hydration_YYYY-MM-DD.jsonl`, `ranges_YYYY-MM-DD.jsonl`, `range_building_YYYY-MM-DD.jsonl` (many dates)
- `journal\` — **stream journals** `YYYY-MM-DD_<stream>.json` (large historical set)
- `daily_20260411.md`, … `daily_*.md`, investigation markdowns, `analyze_today.*`

**`<root>\logs\health\`** — health sink (examples from listing):

- `2026-01-23_NQ_NQ1.jsonl`, `2026-01-24_ES_ES1.jsonl`, … (per-day / per-stream keys)

**`<root>\data\`** (selected):

- `execution_journals\` — intent ledger JSON files (many)
- `execution_summaries\` — `summary_*.json` (present in workflows; not re-listed here)
- `playback\` — **many** run-id folders (GUID-style names); see **B** below
- `risk_latches\`, `session\`, `timetable\`, `translated\`, `analyzed\`, etc. (matrix / pipeline — not all robot engine authority)

**`<root>\data\` (authority filenames at repo root):**

- `mismatch_execution_block_decisions.jsonl` — **not present** at capture time (`Test-Path` → false)
- `flatten_broker_flat_completions.jsonl` — **not present** at capture time

**`<root>\automation\logs\`** — **pipeline / orchestrator**:

- Very large set of `.merge_complete_*.marker` files
- `execution_events\YYYY-MM-DD\*.jsonl` — e.g. `M2K.jsonl`, `MNQ_*.jsonl` with **run-id suffix** on 2026-03-19 (`M2K_0FA2A42B.jsonl`, …)

### B. Sample isolated run: `data\playback\bfb077d0c86241a1b867cafc4cef45e3\`

**Everything created under this folder (complete recursive list from `dir /s /b`):**

```
data\playback\bfb077d0c86241a1b867cafc4cef45e3\data
data\playback\bfb077d0c86241a1b867cafc4cef45e3\data\execution_journals
data\playback\bfb077d0c86241a1b867cafc4cef45e3\logs
data\playback\bfb077d0c86241a1b867cafc4cef45e3\logs\robot
data\playback\bfb077d0c86241a1b867cafc4cef45e3\logs\robot\archive
data\playback\bfb077d0c86241a1b867cafc4cef45e3\logs\robot\daily_20260411.md
data\playback\bfb077d0c86241a1b867cafc4cef45e3\logs\robot\journal
data\playback\bfb077d0c86241a1b867cafc4cef45e3\logs\robot\robot_CL.jsonl
data\playback\bfb077d0c86241a1b867cafc4cef45e3\logs\robot\robot_ENGINE.jsonl
data\playback\bfb077d0c86241a1b867cafc4cef45e3\logs\robot\robot_GC.jsonl
data\playback\bfb077d0c86241a1b867cafc4cef45e3\logs\robot\robot_logging_errors.txt
data\playback\bfb077d0c86241a1b867cafc4cef45e3\logs\robot\robot_NG.jsonl
data\playback\bfb077d0c86241a1b867cafc4cef45e3\logs\robot\robot_NQ.jsonl
data\playback\bfb077d0c86241a1b867cafc4cef45e3\logs\robot\robot_RTY.jsonl
```

**Observed:** This run has **no** `automation\logs` subtree under the playback folder; **`data\execution_journals`** and **`logs\robot\journal`** exist but contained **no files** at capture time (empty run or early abort).

**Physical fragmentation:** Journals + robot JSONL for this run sit under **`playback\{run_id}`**; **health** for the same wall-clock period would still land in **`<root>\logs\health`** (not under `playback`).

---

## Section 3 — Artifact classification (exact table)

| artifact | actual_path | class | should_be_run_scoped | actually_run_scoped | issue? |
|----------|-------------|-------|----------------------|---------------------|--------|
| ExecutionJournal | `...\data\execution_journals\` (under whichever `_persistenceBase` is active) | authoritative ledger | YES | YES when isolated to `playback\{run_id}` | **OK** when isolated; **global `data\execution_journals`** mixes runs when not isolated |
| StreamJournal | `...\logs\robot\journal\` | authoritative state | YES | Same | Same |
| MismatchDecision | `...\data\mismatch_execution_block_decisions.jsonl` | authoritative decision | YES | Under `_persistenceBase\data` | **N/A at root** — file **missing** at capture; when present, scoped to persistence base |
| FlattenDecision | `...\data\flatten_broker_flat_completions.jsonl` | authoritative decision | YES | Same | **Missing** at capture |
| Canonical execution events | `...\automation\logs\execution_events\{date}\*.jsonl` | semi-authoritative (replay chain) | YES | Lives under **`_persistenceBase`** in engine; **repo-level** tree observed when base = `_root` | **MIXED** — same path also used over time; **run prefix** in filename when `run_id` set (see 2026-03-19 files) |
| RobotLogs | `...\logs\robot\robot_*.jsonl` | narrative | NO (but must not be sole authority) | **Global** at `_root`; **also** under `playback\{run_id}\logs\robot` when isolated | **MIXED RUNS** in global tree |
| HealthLogs | `...\logs\health\*.jsonl` | derived | NO | **GLOBAL** under `_root` | **GLOBAL** — not under `playback` |
| Hydration / ranges | `...\logs\robot\hydration_*.jsonl`, `ranges_*.jsonl` | operational / restore | Prefer run scope | **Global** in observed layout | **HIGH** mixing across sessions |
| Execution summaries | `...\data\execution_summaries\summary_*.json` | derived | YES | Under `_persistenceBase` when written | OK relative to base |

---

## Section 4 — Run namespace audit

| artifact | fully_run_isolated? | shared_across_runs? | split_across_roots? | severity |
|----------|---------------------|------------------------|---------------------|----------|
| ExecutionJournal | **YES** only when `_persistenceBase` = `playback\{run_id}` | **YES** if using shared `data\execution_journals` | **YES** vs global | **HIGH** when not isolated |
| StreamJournal | Same | Same | Same | **HIGH** when not isolated |
| MismatchDecision / FlattenDecision | Scoped to `_persistenceBase` | Shared file if same base | With logs/health | **MEDIUM** |
| RobotLogs | **NO** at `_root` | **YES** | **YES** (global vs `playback` copy) | **HIGH** |
| ExecutionEvents | Under persistence base; filenames can include run prefix | Same trading-day folder can hold multiple runs | Can split vs journals if bases differ | **HIGH** |
| HealthLogs | **NO** | **YES** | **YES** (`_root` vs `playback`) | **MEDIUM** |

---

## Section 5 — Reconstruction test (honest)

| folder | can_reconstruct_run? | missing | why |
|--------|----------------------|---------|-----|
| `data\playback\bfb077d0c86241a1b867cafc4cef45e3` | **PARTIAL** | Health timeline; canonical `automation\logs\execution_events` if engine used repo root for events while journals isolated; broker truth | This sample had **empty** `execution_journals` / `journal` |
| `logs\robot` (global) | **NO** | Per-run isolation; execution ledger under another path | Narrative + hydration; **mixed dates/runs** |
| `automation\logs\execution_events` | **NO** | Stream journals, execution journals, decisions | **Subset** of execution story only |
| `logs\health` | **NO** | Ledgers | Filtered **derivative** |
| **FULL SYSTEM** | **YES only with multiple roots** | — | **Requires** `_persistenceBase` tree + **`_root`** health/config + **broker** |

**Core design issue (per your criterion):** **No single folder** contains all truth artifacts; **health** is always outside an isolated `playback\{run_id}` tree.

---

## Section 6 — Violations (real only)

| id | description | paths | severity |
|----|-------------|-------|----------|
| **V1** | Authoritative / semi-authoritative artifacts split across **`data\`**, **`logs\robot\`**, **`automation\logs\execution_events\`**, and (when isolated) **`data\playback\{run_id}`** | `C:\Users\jakej\QTSW2\data\*`, `logs\robot\*`, `automation\logs\*` | **TRUTH** |
| **V2** | Run namespace broken for **narrative** logs: global `logs\robot\robot_*.jsonl` mixes engine sessions | `logs\robot\` | **FORENSIC / OPERATOR** |
| **V3** | **Health** always at **`_root\logs\health`**, not co-located with `playback\{run_id}` | `logs\health\` | **FORENSIC** |
| **V4** | **Dual history:** execution intent ledger (`execution_journals`) vs canonical execution events (`automation\logs\execution_events`) — two representations | `data\execution_journals`, `automation\logs\execution_events` | **TRUTH** (must know precedence) |
| **V5** | Hydration / ranges **JSONL** in global `logs\robot` accumulate **many trading days** | `hydration_*.jsonl`, `ranges_*.jsonl` | **OPERATOR** |

---

## Section 7 — Observed patterns

- **Isolated playback runs** create a **mirror** under `data\playback\{run_id}\` with `logs\robot\` + `data\execution_journals\` layout; **health does not move** with that run.
- **Global** `logs\robot\` holds **long-lived** `robot_*.jsonl`, **hydration/ranges/range_building** by date, and **massive** `journal\` history — **not** run-scoped.
- **`automation\logs`** contains **orchestrator** markers (`.merge_complete_*.marker`) **and** `execution_events\` JSONL; **not** the same subtree as a single playback folder unless `_persistenceBase` is unified.
- **No single directory** on disk contains **all** authoritative + derived artifacts for one run.

---

## Section 8 — Initial diagnosis

The layout is **messy** because **three systems evolved independently**: (1) **stream + execution journals** and **SIM playback isolation** (`data\playback\{run_id}`), (2) **async robot JSONL + hydration** under a **global** `logs\robot`, and (3) **canonical execution events** under `automation\logs\execution_events` plus **health** under `logs\health` keyed only to **`_root`**. Run scoping was applied to **persistence base** for journals but **not** to health and only **partially** to narrative logs (ENGINE rotation files help but do not equal a run folder).

There is **no single enforced storage contract** that places **all** truth-bearing and **decision** artifacts under one **`/runs/{run_id}`** (or equivalent) with **narrative** clearly subordinate; until that exists, reconstruction **requires multiple roots** and **external broker truth**.

---

## How to refresh this capture

1. Run a **SIM playback** session with isolation so `_persistenceBase` = `data\playback\{run_id}`.
2. Re-run:  
   `dir /s /b C:\Users\jakej\QTSW2\data\playback\<that_run_id>`  
   `dir /b C:\Users\jakej\QTSW2\logs\health`  
   `dir /s /b C:\Users\jakej\QTSW2\automation\logs\execution_events\<trading_date>`
3. Replace **sample run id** and **file lists** in Section 2–3.

---

*This document is evidence from the developer machine filesystem; paths will match your repo if `QTSW2` lives at `C:\Users\jakej\QTSW2`.*
