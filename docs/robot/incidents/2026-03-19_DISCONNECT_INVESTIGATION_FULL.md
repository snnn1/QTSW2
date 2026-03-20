# Full Disconnect Investigation — 2026-03-19

**Date**: 2026-03-19  
**Scope**: Every suspicious disconnect today with exact logging context before each event  
**Source**: `logs/health/2026-03-19_ENGINE___ENGINE__.jsonl`, `logs/matrix_timing.jsonl`, `logs/matrix_build_journal.jsonl`

---

## Summary

| # | UTC Time | Chicago | Connection | Duration | Suspicious? | Pre-disconnect activity |
|---|----------|---------|------------|----------|-------------|-------------------------|
| 1 | 02:16:40 | 21:16 (Mar 18) | Live | — | Low | Matrix load every 2 min (normal overnight) |
| 2 | 12:38:52 | 07:38 | Simulation | 293s sustained | Medium | Matrix save + resequence 5 min prior |
| 3 | 14:01:48 | 09:01 | Live | — | **High** | Matrix load **15 seconds before** |
| 4 | 14:03:04 | 09:03 | Simulation | 99s sustained | **High** | 76s after Live; same session |
| 5 | 15:36:17 | 10:36 | Live | 71s sustained | **High** | Disconnect **during** 37s rolling_resequence |

---

## Episode 1 — 02:16:40 UTC (Live)

**Connection**: Live  
**Run ID**: c0206ed3dfc245a79596fe8f09c6adf9  
**Instance count**: 1 (first to detect)

### Logging before disconnect

`logs/health/2026-03-19_ENGINE___ENGINE__.jsonl` has **no prior events** — this is the first event in the file. The health log for ENGINE appears to be a filtered/rotated stream containing only connection-related events.

**matrix_timing.jsonl** (external pipeline, not robot):

| Timestamp (UTC) | Event |
|-----------------|-------|
| 02:10:40 | matrix_load 67ms |
| 02:12:41 | matrix_load 97ms |
| 02:14:42 | matrix_load 78ms |
| **02:16:40** | **CONNECTION_LOST** |
| 02:16:42 | matrix_load 71ms |

**Conclusion**: Matrix pipeline runs every ~2 minutes. Disconnect occurred between scheduled matrix loads. No obvious correlation. Overnight session (9:16 PM CT) — possible network/broker idle timeout or maintenance.

---

## Episode 2 — 12:38:52 UTC (Simulation)

**Connection**: Simulation  
**Run ID**: 7d3bbc3efec740d78a7de77bed9cb9bd  
**Instance count**: 1 → 8 by CONNECTION_LOST_SUSTAINED  
**Sustained**: 293.67 seconds (~5 min) — CONNECTION_LOST_SUSTAINED at 12:43:46

### Logging before disconnect

| Timestamp (UTC) | Source | Event |
|-----------------|--------|-------|
| 12:31:11 | matrix_timing | matrix_load 76ms |
| 12:33:11 | matrix_timing | matrix_load 70ms |
| 12:33:37 | matrix_timing | matrix_load 67ms |
| 12:33:43 | matrix_timing | matrix_save 138ms |
| 12:33:43 | matrix_build_journal | resequence_start (rolling_resequence) |
| 12:33:43 | matrix_build_journal | resequence_complete 9615ms |
| 12:33:43 | matrix_build_journal | timetable_complete 159ms |
| 12:33:44 | matrix_timing | api_matrix_load 508ms |
| 12:33:45 | matrix_timing | matrix_load 157ms, 161ms, 198ms |
| 12:35:12 | matrix_timing | matrix_load 81ms |
| 12:37:12 | matrix_timing | matrix_load 78ms |
| **12:38:52** | **health** | **CONNECTION_LOST** |

**Conclusion**: Matrix pipeline completed a full resequence (~9.6s) and timetable generation ~5 minutes before disconnect. No immediate correlation. Simulation connection — NinjaTrader Sim broker/data feed. Possible: Sim feed instability, or strategy load (8 instances) contributing.

---

## Episode 3 — 14:01:48 UTC (Live)

**Connection**: Live  
**Run ID**: c11de4640d524bf6b3a5bcb37152b646  
**Instance count**: 1 → 8 by CONNECTION_LOST_SUSTAINED

### Logging before disconnect

| Timestamp (UTC) | Source | Event |
|-----------------|--------|-------|
| **14:01:33** | matrix_timing | **matrix_load 153ms** |
| **14:01:48** | **health** | **CONNECTION_LOST** |

**Gap**: 15 seconds between matrix_load and CONNECTION_LOST.

**Conclusion**: **SUSPICIOUS**. Matrix load occurred 15 seconds before Live disconnect. The timetable poll in RobotEngine reads the timetable file every 5s. If the matrix pipeline or another process was writing the timetable at 14:01:33, the robot could have been contending on file I/O. However, matrix_load is the orchestrator loading the parquet — timetable is written by the pipeline. Correlation is temporal but worth noting.

---

## Episode 4 — 14:03:04 UTC (Simulation)

**Connection**: Simulation  
**Run ID**: c11de4640d524bf6b3a5bcb37152b646 (same as Episode 3)  
**Instance count**: 1  
**Sustained**: 98.89 seconds — CONNECTION_LOST_SUSTAINED at 14:04:43 (reported as Live, incident_id from 14:03:04)

### Logging before disconnect

| Timestamp (UTC) | Source | Event |
|-----------------|--------|-------|
| 14:01:48 | health | CONNECTION_LOST (Live) |
| 14:03:04 | health | CONNECTION_LOST (Simulation) |
| 14:03:34 | matrix_timing | matrix_load 142ms |

**Conclusion**: **SUSPICIOUS**. Simulation disconnected 76 seconds after Live. Same run_id — same NinjaTrader process, different connection (Live vs Sim). Suggests process-wide or platform-level stress: after Live dropped, Sim dropped shortly after. Possible: NinjaTrader under load, or cascading connection failure.

---

## Episode 5 — 15:36:17 UTC (Live)

**Connection**: Live  
**Run ID**: 09933396ca714ff5be8a6fbcfdd40925  
**Instance count**: 1 → 15 by CONNECTION_LOST_SUSTAINED  
**Sustained**: 70.95 seconds — CONNECTION_LOST_SUSTAINED at 15:37:28

### Logging before disconnect

| Timestamp (UTC) | Source | Event |
|-----------------|--------|-------|
| 15:31:56 | matrix_timing | matrix_load 214ms |
| 15:33:57 | matrix_timing | matrix_load 252ms |
| 15:35:47 | matrix_build_journal | **resequence_start (rolling_resequence)** |
| 15:35:57 | matrix_timing | matrix_load 354ms |
| **15:36:17** | **health** | **CONNECTION_LOST** |
| 15:36:25 | matrix_build_journal | matrix_saved 602ms |
| 15:36:25 | matrix_build_journal | resequence_complete **37743ms** |
| 15:36:25 | matrix_build_journal | timetable_complete 378ms |

**Conclusion**: **HIGHLY SUSPICIOUS**. Disconnect occurred **during** a 37.7-second `rolling_resequence`. Timeline:

- 15:35:47 — Resequence started
- 15:36:17 — CONNECTION_LOST (30 seconds into resequence)
- 15:36:25 — Resequence completed (37.7s total)

The matrix pipeline runs in a separate process. If both share disk I/O (timetable file, parquet, logs), heavy I/O from the 37s resequence could have:

1. Caused disk/CPU contention affecting NinjaTrader
2. Delayed or blocked the robot’s timetable poll (synchronous `File.ReadAllBytes`)
3. Contributed to NinjaTrader connection timeout or data-feed stall

The Strategy Disconnect Assessment (2026-03-18) notes: *"Timetable poll: Every 5s per engine (7 engines). Yes, synchronous file read."* — so 7× `File.ReadAllBytes` every 5s. If the matrix pipeline is writing the timetable or doing heavy parquet I/O, contention is plausible.

---

## Cross-cutting observations

### 1. No CONNECTION_CONTEXT or CONNECTION_RECOVERED in health log

The health log does not contain `CONNECTION_CONTEXT` or `CONNECTION_RECOVERED` / `CONNECTION_CONFIRMED`. Those may be written to a different stream or not yet implemented in the aggregated health output. Recovery visibility improvements (derived_connection_state) will help future analysis.

### 2. Matrix pipeline overlap

Episodes 3 and 5 show disconnects close in time to matrix activity:

- Episode 3: matrix_load 15s before
- Episode 5: disconnect during 37s resequence

### 3. Live vs Simulation

- Episodes 1, 3, 5: Live
- Episodes 2, 4: Simulation

Live and Sim can disconnect independently. Episode 4 (Sim) followed Episode 3 (Live) by 76s in the same process — suggests process-wide stress.

### 4. Instance count growth

By CONNECTION_LOST_SUSTAINED, instance counts were 8 or 15 — multiple strategy instances detected the same incident. Deduplication is working; the first instance reports, others are rate-limited.

---

## Recommendations

1. **Stagger matrix pipeline vs trading**  
   Avoid running heavy resequence during market hours. Schedule resequence for off-hours or low-activity windows.

2. **Reduce timetable poll contention**  
   - Increase poll interval (e.g. 5s → 10s) during known matrix run windows  
   - Or use file-change notification instead of polling

3. **Add CONNECTION_CONTEXT to health log**  
   Ensure `CONNECTION_CONTEXT` (context at disconnect) is written to the aggregated health stream for post-incident review.

4. **Monitor disk I/O**  
   If matrix resequence and robot timetable poll contend on the same disk, consider separate drives or I/O scheduling.

5. **Platform / environment**  
   - VPS or more stable network for NinjaTrader  
   - NinjaTrader Options → Strategies: increase disconnect delay if available  
   - Contact NinjaTrader support with timestamps and logs for connection diagnosis

---

## Files referenced

- `logs/health/2026-03-19_ENGINE___ENGINE__.jsonl` — CONNECTION_LOST, DISCONNECT_FAIL_CLOSED_ENTERED, CONNECTION_LOST_SUSTAINED
- `logs/matrix_timing.jsonl` — matrix_load, matrix_save, rolling_resequence, timetable_generation
- `logs/matrix_build_journal.jsonl` — resequence_start, resequence_complete, timetable_complete
- `docs/robot/incidents/2026-03-18_STRATEGY_DISCONNECT_ASSESSMENT.md` — Strategy does not call connection APIs; load can contribute indirectly
