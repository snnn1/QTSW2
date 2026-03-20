# Complete Disconnect Investigation — 2026-03-19

**Date:** 2026-03-19  
**Scope:** All disconnects today with full pre-event context, root-cause analysis, and evidence  
**Sources:** Incident reports, watchdog incidents.jsonl, matrix_timing.jsonl, matrix_build_journal.jsonl, strategy assessment, connection loss incident (Mar 17)

---

## Executive Summary

Today had **5 distinct disconnect episodes** (2 overnight, 3 during market hours). Evidence points to **multiple contributing factors**:

1. **Matrix pipeline ↔ Robot file contention** — Timetable file (`timetable_current.json`) is written by the matrix pipeline and read by the robot every 5s. Historical `WinError 32` (file in use) proves contention. Episode 5 shows disconnect **during** a 37-second resequence.
2. **Temporal correlation with matrix activity** — Episodes 3 and 5 show disconnects within 15–30 seconds of heavy matrix I/O (matrix_load, rolling_resequence).
3. **Process-wide stress** — Episode 4 (Sim) followed Episode 3 (Live) by 76 seconds, same `run_id`; suggests NinjaTrader process under load.
4. **7:30 AM session open** — Episode 2 (7:30 Chicago) aligns with regular session open; possible data-provider/platform load spike.

The strategy **does not** call NinjaTrader connection APIs (per 2026-03-18 assessment). Disconnects are driven by NinjaTrader/platform/network, but **robot and matrix pipeline load can indirectly contribute** via disk I/O contention and CPU pressure.

---

## 1. Episode Summary

| # | UTC | Chicago | Connection | Duration | Suspicious? | Pre-event activity |
|---|-----|---------|------------|----------|-------------|--------------------|
| 1 | 02:16:40 | 21:16 (Mar 18) | Live | 1s | Low | Matrix load every 2 min (normal overnight) |
| 2 | 12:38:52 | 07:38 | Simulation | 1793s (~30 min) | Medium | Resequence completed 5 min prior; robot silent at 7:30:02 |
| 3 | 14:01:48 | 09:01 | Live | 64s | **High** | **matrix_load 15 seconds before** |
| 4 | 14:03:04 | 09:03 | Simulation | 99s | **High** | 76s after Live; same run_id (cascading) |
| 5 | 15:36:17 | 10:36 | Live | 71s | **High** | **Disconnect during 37s rolling_resequence** |

---

## 2. Episode 1 — 02:16:40 UTC (9:16 PM Chicago, Mar 18)

**Connection:** Live  
**Duration:** 1 second (brief)

### Before disconnect

- `matrix_timing.jsonl`: matrix_load at 02:10, 02:12, 02:14 (every ~2 min)
- No resequence or heavy I/O in the 10 minutes before
- Overnight session — possible network idle timeout, broker maintenance, or brief blip

### Conclusion

**Low suspicion.** No correlation with matrix pipeline. Likely network/broker.

---

## 3. Episode 2 — 12:38:52 UTC (7:38 AM Chicago)

**Connection:** Simulation  
**Duration:** 1793 seconds (~30 minutes)  
**Watchdog:** CONNECTION_LOST at 12:38:52, recovery at 13:08:45

### Timeline

| UTC | Chicago | Event |
|-----|---------|-------|
| 12:30:01–02 | 7:30:01–02 | **Last robot activity:** ENGINE_TIMER_HEARTBEAT, ONBARUPDATE_CALLED (MCL, MES, MNQ, M2K, MGC), RECONCILIATION_PASS_SUMMARY |
| 12:33:22 | 7:33:22 | matrix_build_journal: resequence_start |
| 12:33:43 | 7:33:43 | resequence_complete 9615ms, timetable_complete 159ms |
| 12:33:44 | 7:33:44 | api_matrix_load 508ms |
| 12:35:12 | 7:35:12 | matrix_load 81ms |
| 12:37:12 | 7:37:12 | matrix_load 78ms |
| **12:38:52** | **7:38:52** | **CONNECTION_LOST** (watchdog infers from absence of events) |

### Analysis

- Robot went **silent at 7:30:02** — no DISCONNECT_FAIL_CLOSED_ENTERED in feed (robot likely could not log).
- Resequence completed **~5 minutes before** watchdog detected CONNECTION_LOST.
- 7:30 AM Chicago = regular session open; possible data-provider/platform load.
- **Medium suspicion:** Resequence not immediately before disconnect, but 7:30 session open is a known stress point.

---

## 4. Episode 3 — 14:01:48 UTC (9:01 AM Chicago)

**Connection:** Live  
**Duration:** 64 seconds  
**Run ID:** c11de4640d524bf6b3a5bcb37152b646

### Timeline

| UTC | Chicago | Event |
|-----|---------|-------|
| **14:01:33** | **9:01:33** | **matrix_load 153ms** |
| **14:01:48** | **9:01:48** | **CONNECTION_LOST** |

**Gap:** 15 seconds between matrix_load and CONNECTION_LOST.

### Analysis

**High suspicion.** Matrix pipeline loaded parquet at 14:01:33. Robot timetable poll runs every 5s (7 engines). If both touched the same disk (timetable, parquet, logs), I/O contention is plausible. The 15-second window suggests either:

1. Matrix load caused disk/CPU contention that delayed or stressed NinjaTrader.
2. Coincidental timing (less likely given Episode 5 pattern).

---

## 5. Episode 4 — 14:03:04 UTC (9:03 AM Chicago)

**Connection:** Simulation  
**Duration:** 99 seconds (sustained)  
**Run ID:** Same as Episode 3 (c11de4640d524bf6b3a5bcb37152b646)

### Timeline

| UTC | Chicago | Event |
|-----|---------|-------|
| 14:01:48 | 9:01:48 | CONNECTION_LOST (Live) |
| **14:03:04** | **9:03:04** | **CONNECTION_LOST (Simulation)** |
| 14:03:34 | 9:03:34 | matrix_load 142ms |

### Analysis

**High suspicion.** Simulation disconnected **76 seconds after** Live. Same NinjaTrader process, different connection. Suggests **process-wide stress**: after Live dropped, Sim dropped shortly after. Possible causes:

- NinjaTrader under load from recovery/catch-up.
- Cascading connection failure.
- Shared resource (CPU, memory, disk) pressure.

---

## 6. Episode 5 — 15:36:17 UTC (10:36 AM Chicago)

**Connection:** Live  
**Duration:** 71 seconds (sustained)  
**Run ID:** 09933396ca714ff5be8a6fbcfdd40925

### Timeline

| UTC | Chicago | Event |
|-----|---------|-------|
| 15:31:56 | 10:31:56 | matrix_load 214ms |
| 15:33:57 | 10:33:57 | matrix_load 252ms |
| **15:35:47** | **10:35:47** | **resequence_start (rolling_resequence)** |
| 15:35:57 | 10:35:57 | matrix_load 354ms |
| **15:36:17** | **10:36:17** | **CONNECTION_LOST** (30 seconds into resequence) |
| 15:36:25 | 10:36:25 | matrix_saved 602ms |
| 15:36:25 | 10:36:25 | resequence_complete **37743ms** |
| 15:36:25 | 10:36:25 | timetable_complete 378ms |
| 15:36:27 | 10:36:27 | matrix_load 621ms |
| 15:36:40 | 10:36:40 | api_matrix_load **13857ms** (13.8 seconds!) |

### Analysis

**Highly suspicious.** Disconnect occurred **during** a 37.7-second `rolling_resequence`:

- 15:35:47 — Resequence started
- 15:36:17 — CONNECTION_LOST (30 seconds into resequence)
- 15:36:25 — Resequence completed

The matrix pipeline and robot share:

1. **Timetable file** — Pipeline writes `timetable_current.tmp` → rename to `timetable_current.json`. Robot polls with `File.ReadAllBytes` every 5s per engine.
2. **Disk I/O** — Parquet read/write, log files.

**Evidence of file contention:** `matrix_build_journal.jsonl` (2026-03-07) shows:

```json
{"event": "failure", "mode": "timetable", "error": "[WinError 32] The process cannot access the file because it is being used by another process: 'data\\\\timetable\\\\timetable_current.tmp' -> 'data\\\\timetable\\\\timetable_current.json'"}
```

This proves the timetable file is contended. During the 37s resequence, the pipeline writes the timetable; the robot (7 engines) polls it. Blocking or delayed `File.ReadAllBytes` on the strategy thread could stress NinjaTrader's event loop and contribute to connection timeout.

**api_matrix_load 13857ms** at 15:36:40 — 13.8 second load after recovery — indicates the system was under heavy I/O load.

---

## 7. Root Cause Synthesis

### Primary (External)

1. **NinjaTrader / data connection** — Platform or data provider drops connection. Strategy does not call connection APIs.
2. **Network** — Brief drops affecting NinjaTrader ↔ broker/data provider.
3. **Session open (7:30)** — Provider/platform load.

### Contributing (Internal)

1. **Timetable file contention** — Robot polls `timetable_current.json` every 5s (7×); pipeline writes it during resequence. `WinError 32` confirms contention.
2. **Matrix resequence during market hours** — 37s resequence at 10:35 AM; disconnect 30s in. Heavy parquet + timetable I/O.
3. **Disk I/O contention** — Same disk for parquet, timetable, logs. Robot + matrix pipeline + NinjaTrader all active.
4. **Strategy load** — 7 instances, 7× timetable poll, journal I/O, sync file reads. Can add CPU and I/O pressure.

---

## 8. Recommendations

### Immediate (Reduce Contention)

1. **Stagger matrix pipeline vs trading**  
   Avoid heavy resequence during market hours (e.g. 9:30–16:00 ET). Schedule for off-hours or low-activity windows.

2. **Increase timetable poll interval**  
   Change from 5s to 10s (already done in Phase A per NINJATRAER_CPU_AUDIT). Reduces read frequency.

3. **Timetable write atomicity**  
   Ensure pipeline uses atomic rename (write to .tmp, then rename). Avoid long-held file locks.

### Short-term (Observability)

4. **Add CONNECTION_CONTEXT to health log**  
   Log context at disconnect (last timetable poll, last matrix activity) for post-incident review.

5. **Monitor disk I/O**  
   If resequence and robot contend on same disk, consider separate drives or I/O scheduling.

### Platform / Environment

6. **NinjaTrader**  
   - Options → Strategies: increase disconnect delay if available  
   - Contact NinjaTrader support with timestamps and logs for connection diagnosis

7. **Network**  
   - Check home/office network stability  
   - Consider wired connection if on Wi‑Fi  
   - VPS for more stable network

---

## 9. Data Sources Verified

| Source | Path | Used for |
|--------|------|----------|
| Incident report | `docs/robot/incidents/2026-03-19_INCIDENT_REPORT_0730_DISCONNECT.md` | Episode 2 timeline |
| Full investigation | `docs/robot/incidents/2026-03-19_DISCONNECT_INVESTIGATION_FULL.md` | Episodes 1–5, matrix correlation |
| Watchdog incidents | `data/watchdog/incidents.jsonl` | CONNECTION_LOST timestamps, durations |
| Matrix timing | `logs/matrix_timing.jsonl` | matrix_load, matrix_save, resequence timing |
| Matrix build journal | `logs/matrix_build_journal.jsonl` | resequence_start/complete, timetable_complete, WinError 32 |
| Strategy assessment | `docs/robot/incidents/2026-03-18_STRATEGY_DISCONNECT_ASSESSMENT.md` | Strategy does not call connection APIs |
| Connection loss incident | `docs/robot/incidents/2026-03-17_INCIDENT_REPORT_CONNECTION_LOSS_RESTART_ORDERS_DELETED.md` | Bootstrap/adoption behavior |

---

## 10. Conclusion

Today's disconnects were **not** caused by the strategy directly. The strategy does not call NinjaTrader connection APIs. However, **evidence strongly suggests** that:

1. **Matrix pipeline ↔ Robot file contention** — Timetable file and shared disk I/O can stress NinjaTrader during heavy resequence.
2. **Episode 5** — Disconnect during 37s resequence is the strongest correlation.
3. **Episode 3** — 15s before matrix_load is a plausible contributing factor.
4. **Episode 4** — Cascading Sim after Live suggests process-wide stress.

**Recommendation:** Stagger matrix resequence outside market hours and increase timetable poll interval. Monitor for recurrence. If disconnects persist, escalate to NinjaTrader support with this evidence.

---

## 11. Critical Question: Same Process or Separate?

### Answer: **Separate processes, same machine**

| Component | Process | Technology |
|-----------|---------|------------|
| **Robot** | NinjaTrader | C# .NET (RobotSimStrategy, RobotEngine) |
| **Matrix pipeline** | Python | FastAPI (modules/matrix/api.py), scripts, dashboard backend |

- Robot runs inside NinjaTrader's process.
- Matrix pipeline runs as Python (dashboard backend, matrix API, or `run_matrix_and_timetable.py`).
- **Different processes** — no shared memory.
- **Same machine** — shared disk, filesystem, CPU. Contention is disk I/O and file locks, not process memory.

### Implication

Contention is **less severe** than if they were in the same process (no direct CPU/memory contention), but **still significant**:

- Both touch `data/timetable/timetable_current.json`
- Both touch `data/master_matrix/*.parquet`
- Both touch `logs/`
- Heavy I/O from one can stall the other via disk queue and file locks

---

## 12. Priority-Ordered Next Steps (Aligned with Audit)

### Priority 1 — Hard isolation of pipeline vs robot

| Option | Feasibility | Impact |
|--------|-------------|--------|
| **A: Separate machine** | High | Best — robot and pipeline on different hosts; no shared disk |
| **B: Strict scheduling** | Medium | No resequence during 9:30–16:00 ET; no heavy builds during active sessions |

### Priority 2 — Fix timetable contention

**Current state:** Robot uses `TimetableFilePoller` (not `TimetableCache`). Each engine polls every 5–10s:

- `File.Exists` + `File.ReadAllBytes` in `TimetableFilePoller.Poll`
- `TimetableContract.LoadFromFile` — **second read** of same file
- 7 engines → up to 14 reads per poll cycle when file changes

**TimetableCache exists** but is **not used** by RobotEngine. It would reduce reads when file is unchanged.

**Target state (from audit):**

```
Pipeline → writes file
     ↓
Single TimetableCache (1 reader / background poll)
     ↓
Engines read in-memory copy
```

- Removes 7× file reads
- Removes lock contention on read side

### Priority 3 — Journal I/O optimization

From IEA fill-path audit: `RecordEntryFill` / `RecordExitFill` hold `_lock` and do sync `File.ReadAllText` + `SaveJournal` (FileStream write). With pipeline disk contention, this becomes riskier. Consider async journal or moving writes off the critical path.

### Priority 4 — ThreadPool / concurrency observation

- Observe ThreadPool queue length if possible
- Watch for worker starvation (e.g. via diagnostics)

### What NOT to do

- Do not blame NinjaTrader first
- Do not chase network issues without evidence
- Do not over-optimize small timers

### Bottom line

Disconnects are **system load events tied to heavy I/O + shared resources**, not random. Next move: **resource isolation + removing shared file contention**.
