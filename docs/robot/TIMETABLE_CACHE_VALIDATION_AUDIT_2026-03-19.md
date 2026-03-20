# TimetableCache Post-Implementation Validation Audit

**Date:** 2026-03-19  
**Scope:** RobotCore_For_NinjaTrader — TimetableCache wiring  
**Method:** Code audit + log availability check

---

## Phase 1 — Correctness Audit

### 1. Parse failure safety

**PARSE_FAILURE_PATH**

| Question | Answer |
|----------|--------|
| **Possible:** | Yes |
| **Call path:** | `TimetableCache.GetOrLoad` → parse fails → returns `(rawHash, null, true, false)` → `PollAndParseTimetable` returns `(FilePollResult(changed: true, hash, "PARSE_ERROR"), null, InvalidOperationException)` → `ReloadTimetableIfChanged(poll, null, ex)` |
| **Risk:** | None |

**Propagation analysis:**
- `ReloadTimetableIfChanged` first checks `poll.Error is not null` (line 3394).
- When `poll.Error == "PARSE_ERROR"`, it logs `TIMETABLE_INVALID`, calls `StandDown()`, and returns.
- Execution never reaches `if (timetable is null)` (line 3416) or any `timetable.` access.
- **Null dereference:** No — early return when `poll.Error` is set.
- **False reload:** No — `StandDown()` prevents further processing.
- **Inconsistent state:** No — engine stands down; no partial application.

---

### 2. Cache invariants

**CACHE_INVARIANTS**

| Invariant | Status |
|-----------|--------|
| **Atomic swap:** | Yes — All cache fields (`_cachedPath`, `_cachedLastWriteUtc`, `_cachedBytes`, `_cachedHash`, `_cachedTimetable`) are assigned together inside the lock before return (lines 84–88). |
| **Partial state possible:** | No — On parse failure (line 76–80), cache is not updated; returns `(rawHash, null, true, false)` without touching `_cached*`. |
| **Failure fallback correct:** | Yes — On `IOException` (lines 37–46, 63–68), returns previous `(_cachedHash, _cachedTimetable, false, true)` when available; otherwise `(null, null, false, false)`. |

---

### 3. Engine behavior unchanged

**ENGINE_BEHAVIOR**

| Question | Answer |
|----------|--------|
| **Missed reload risk:** | No |
| **Duplicate reload risk:** | No |
| **Reason:** | `ReloadTimetableIfChanged` logic unchanged. Still uses `poll.Changed` and `force` to gate application. `_lastTimetableHash` updated only when applying (line 3414). Cache returns `Changed` based on content hash vs `lastHash`; first engine to see new file gets `Changed=true`, applies; others get `Changed=false` (or hit cache with same hash) and skip. No change to reload semantics. |

---

## Phase 2 — File I/O Verification

### 4. File access audit

**FILE_IO_AUDIT**

| Question | Answer |
|----------|--------|
| **Engine-level timetable file reads present:** | No |

**Timetable-specific I/O in RobotEngine:**
- `File.Exists(_timetablePath)` (line 3346) — metadata only, not content read.
- `TimetableCache.GetOrLoad(_timetablePath, _lastTimetableHash)` — cache performs I/O; engine receives in-memory result.

**Other file I/O in RobotEngine (non-timetable):**
- `ParitySpec.LoadFromFile`, `ExecutionPolicy.LoadFromFile`, `EligibilityContract.LoadFromFile`, `LoggingConfig.LoadFromFile`, `ForceReconcileTrigger`, `HealthMonitorConfig`, etc. — config/eligibility/journal files, not timetable.

---

### 5. Single-reader guarantee

**SINGLE_READER**

| Question | Answer |
|----------|--------|
| **Multiple timetable readers present:** | No |

**Timetable file I/O locations:**
- `TimetableCache.cs` line 61: `File.ReadAllBytes(path)` — sole reader for timetable content.
- `TimetableFilePoller.Poll` (line 36): `File.ReadAllBytes(path)` — **not called** by RobotEngine; engine uses `MarkPolled` + `TimetableCache` only. Poll remains for backward compatibility but is dead in the hot path.

---

## Phase 3 — Cache Behavior Metrics (Log Analysis)

**CACHE_METRICS**

| Metric | Value |
|---------|-------|
| **Total hits:** | N/A |
| **Total refreshes:** | N/A |
| **Hit ratio:** | N/A |

**Reason:** No post-deployment logs available. `TIMETABLE_CACHE_HIT` and `TIMETABLE_CACHE_REFRESH` were added in this implementation; deployment has not yet occurred.

**REFRESH_ANALYSIS**

| Metric | Value |
|--------|-------|
| **Avg refreshes per update:** | N/A |
| **Max refreshes per update:** | N/A |
| **Correct (1 per update):** | Pending deployment |

---

## Phase 4 — Contention & Stability

**FILE_CONTENTION**

| Question | Answer |
|----------|--------|
| **Errors detected:** | Yes (pre-implementation) |
| **Examples:** | `logs/matrix_build_journal.jsonl` (2026-03-07): `[WinError 32] The process cannot access the file because it is being used by another process: 'timetable_current.tmp' -> 'timetable_current.json'` |

**Note:** This is from the **matrix pipeline** (writer), not the robot. Post-implementation robot logs are not yet available.

**CPU_ANALYSIS**

| Metric | Value |
|--------|-------|
| **Avg CPU before:** | N/A (no pre/post metrics) |
| **Avg CPU after:** | N/A |
| **Peak CPU before:** | N/A |
| **Peak CPU after:** | N/A |
| **Observed change:** | Pending deployment |

---

## Phase 5 — Disconnect Correlation

**DISCONNECT_ANALYSIS**

| Question | Answer |
|----------|--------|
| **Disconnects during timetable writes:** | N/A |
| **Disconnects during resequence:** | N/A |
| **Live/Sim cascade still present:** | N/A |

**Reason:** Implementation completed 2026-03-19; no post-deployment incident data yet.

---

## TOP_FINDINGS

### Confirmed improvements (from code audit)

1. **Single timetable reader** — Only `TimetableCache` performs `File.ReadAllBytes` for the timetable; engine no longer does direct reads or `LoadFromFile`.
2. **Fail-safe on contention** — On `IOException` (e.g. WinError 32), cache returns previous snapshot; no exception propagation to engine.
3. **Parse failure safety** — `(Changed=true, Timetable=null)` is handled via `poll.Error`; engine stands down without null dereference.

### Remaining risks

1. **`File.Exists` before `GetOrLoad`** — Small race: file can be deleted between `File.Exists` and `GetOrLoad`. `GetOrLoad` also checks existence; worst case is `(null, null, false, false)` and `MISSING` handling. Low risk.
2. **`TimetableFilePoller.Poll`** — Still contains `File.ReadAllBytes`; unused in hot path but could be called by other code. Grep shows no callers in RobotEngine.

### Incorrect assumptions detected

- None from code audit.

---

## CONCLUSION

**Timetable contention: Partially resolved (code complete, deployment pending)**

**Reasoning:**
- Code changes remove engine-level timetable file reads and centralize I/O in `TimetableCache`.
- Fail-safe behavior on `IOException` and parse failure is correct.
- Log-based validation (cache hit ratio, refresh multiplicity, CPU, disconnect correlation) requires deployment and live data.
- Historical WinError 32 was on the pipeline (writer) side; robot-side contention should be reduced by fewer readers (1 vs 7×2).

**Next steps:**
1. Deploy and collect logs with `TIMETABLE_CACHE_HIT` / `TIMETABLE_CACHE_REFRESH`.
2. Verify hit ratio > 95% and 1 refresh per timetable update.
3. Monitor for file contention errors from the robot.
4. Compare disconnect behavior before/after with resequence disabled during trading hours.
