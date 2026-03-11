# ENGINE_TICK_STALL_RUNTIME — False Positive Investigation

## Executive Summary

Most `ENGINE_TICK_STALL_RUNTIME` notifications are **false alarms** caused by per-instrument stall detection combined with low-volume instruments and bar-driven tick cadence. This document summarizes root causes and mitigations.

---

## 1. How Stall Detection Works

| Component | Location | Behavior |
|-----------|----------|----------|
| **Tick source** | `RobotSimStrategy.OnBarUpdate` → `_engine.Tick(tickTimeUtc)` | `Tick()` is called when a **bar closes** (Calculate.OnBarClose) |
| **Update** | `RobotEngine.Tick()` → `HealthMonitor.UpdateEngineTick(utcNow)` | Updates `_lastEngineTickUtc` |
| **Evaluation** | `HealthMonitor.EvaluateEngineTickStall()` | Runs every 5 seconds in background thread |
| **Threshold** | `ENGINE_TICK_STALL_SECONDS = 180` | If no tick for 180s → stall |
| **Grace** | `ENGINE_START_GRACE_PERIOD_SECONDS = 180` | First 3 min after engine start: log only (no notification) |

**Suppression conditions** (all must pass for stall check to run):
- `HasActiveStreams()` — streams in ARMED, RANGE_BUILDING, or RANGE_LOCKED
- `_isMarketOpenCallback()` — CME market must be open
- `_lastEngineTickUtc != MinValue` — engine must have started

---

## 2. Root Causes of False Positives

### 2.1 Per-Instrument HealthMonitor (Primary)

**Architecture:** Each strategy instance (one per instrument) creates its own `RobotEngine` and `HealthMonitor`. There is no shared engine.

- **MES strategy** → MES HealthMonitor gets ticks only when MES bars close
- **M2K strategy** → M2K HealthMonitor gets ticks only when M2K bars close
- **MCL strategy** → MCL HealthMonitor gets ticks only when MCL bars close

If M2K (micro Russell) has low volume and no bar closes for 2+ minutes, M2K’s HealthMonitor fires `ENGINE_TICK_STALL_RUNTIME` even though MES, NQ, etc. are ticking normally.

### 2.2 Bar-Driven Tick Cadence

- Strategy uses **Calculate.OnBarClose** (1-minute primary bars + 1-second secondary for BE).
- `Tick()` is invoked from `OnBarUpdate` when a bar closes.
- For 1-minute bars: expect ticks roughly every 60 seconds.
- For low-volume instruments (M2K, MCL, MNG, MYM): 1-minute bars may not form every minute during:
  - Pre-market / extended hours
  - Lunch lulls
  - Session transitions
  - Maintenance breaks (if market-open callback is wrong)

### 2.3 120-Second Threshold Too Tight

- 1-minute bars → ~60s between ticks.
- 120s threshold allows only one missed bar before stall.
- Any brief gap (e.g. 90s without a bar) triggers a notification.

### 2.4 No Hysteresis

- Stall is reported on the first evaluation that exceeds the threshold.
- Brief spikes (e.g. 125s gap, then tick) still produce a notification.
- No requirement for the stall to persist across multiple evaluations.

---

## 3. Evidence from Prior Incidents

| Source | Finding |
|-------|---------|
| `2026-02-19_DAILY_SUMMARY_AND_ERROR_INVESTIGATION.md` | 17 `ENGINE_TICK_STALL_DETECTED` notifications during hydration; fix: 3-min startup grace |
| `BREAK_EVEN_FAILURE_INVESTIGATION_2026-02-16.md` | “ENGINE_TICK_STALL shows 120s+ gaps” for MNQ; M2K, MCL, MNG, MYM have “1-second bars may not update reliably” |
| `ERROR_CATALOG_INCIDENT_PACKS.md` | Incident #2: Tick() not called for 120+ s during startup/hydration |

---

## 4. Implemented Mitigations

### 4.1 Hysteresis (Consecutive Stall Count)

- Require stall to persist for **2 consecutive evaluations** before sending notification.
- Evaluations run every 5 seconds.
- If a tick arrives between evaluations, the stall is cleared and no notification is sent.
- Reduces false positives from brief spikes.

### 4.2 Increased Threshold

- `ENGINE_TICK_STALL_SECONDS`: 120 → **180 seconds**.
- Allows ~3 missed 1-minute bars before stall.
- Better for low-volume instruments.

### 4.3 Fix Typo in HasActiveStreams

- `RobotEngine.HasActiveStreams()` had `StreamState.RANGE_LOCKED` twice.
- Removed duplicate (no functional change).

---

## 5. Future Mitigations (Not Implemented)

| Mitigation | Complexity | Notes |
|-----------|-------------|-------|
| Instrument-specific thresholds | Medium | M2K, MCL, MNG, MYM: 240s; ES, NQ: 180s |
| Suppress when bars loading | Medium | Add `IsBarsRequestPending` callback; suppress during hydration |
| Aggregate across instruments | High | Shared HealthMonitor or coordinator; notify only if all instruments stalled |
| Log instrument in stall event | Low | Add `instrument` to event payload for debugging |

---

## 6. Verification

After deploying mitigations:

1. **No increase in missed real stalls** — 180s + hysteresis should still catch genuine engine freezes.
2. **Fewer false notifications** — Monitor Pushover for `ENGINE_TICK_STALL` over 1–2 weeks.
3. **Log analysis** — Run `automation/scripts/analyze_engine_tick_stalls.py` (if created) to summarize stall patterns.

---

## 7. Related Files

- `RobotCore_For_NinjaTrader/HealthMonitor.cs` — stall detection, thresholds, hysteresis
- `RobotCore_For_NinjaTrader/RobotEngine.cs` — `HasActiveStreams`, `UpdateEngineTick` wiring
- `RobotCore_For_NinjaTrader/Strategies/RobotSimStrategy.cs` — `OnBarUpdate` → `_engine.Tick()`
- `docs/robot/incidents/ERROR_CATALOG_INCIDENT_PACKS.md` — incident #2
