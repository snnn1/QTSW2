# NinjaTrader CPU Audit — 2026-03-19

## Executive Summary

NinjaTrader exhibits high CPU usage, traced to **recent additions** (Gap 3/4 coordinators, IEA stall checks, logging, timetable polling). With **7 engines** running, multiple 1-second timers and full account snapshots compound into significant load.

**Primary culprits:**
1. **MismatchEscalationCoordinator** and **ProtectiveCoverageCoordinator** — 1s audit timers each calling `GetAccountSnapshot()` → **14 full account snapshots/second** (7 engines × 2 coordinators)
2. **InstrumentExecutionAuthority** stall check — 2s timer per IEA → 7 timers
3. **Diagnostic logging** enabled (`enable_diagnostic_logs: true`) — queue reached 41,425 events (Mar 18)
4. **Timetable poll** — 5s per engine, synchronous `File.ReadAllBytes()` × 7
5. **EmitMetrics** — RECONCILIATION_MISMATCH_METRICS and PROTECTIVE_AUDIT_METRICS every Tick (every bar) per engine

---

## 1. Timer Inventory (Per Engine; 7 Engines Total)

| Component | Interval | Per Engine | Total (7 engines) | Notes |
|-----------|----------|------------|-------------------|-------|
| **MismatchEscalationCoordinator** | 1000 ms | 1 timer | 7 | `OnAuditTick` → `GetAccountSnapshot()` + `GetMismatchObservations()` |
| **ProtectiveCoverageCoordinator** | 1000 ms | 1 timer | 7 | `OnAuditTick` → `GetAccountSnapshot()` + `ProtectiveCoverageAudit.Audit()` per instrument |
| **InstrumentExecutionAuthority** | 2000 ms | 1 per IEA | 7 | `CheckCommandStall` — lightweight but 7× every 2s |
| **Engine heartbeat** | 5000 ms | via Tick | 7 | Tick from OnBarUpdate (1/min per bar) + ENGINE_TICK_CALLSITE rate-limited 5s |
| **Timetable poll** | 5 s | 1 | 7 | `PollAndParseTimetable` → `File.ReadAllBytes()` + SHA256 + JSON parse |
| **ReconciliationRunner** | 60 s | 1 | 7 | `RunPeriodicThrottle` → `GetAccountSnapshot()` |
| **HealthMonitor** | 5 s | 1 bg thread | 1 | Shared; `Evaluate()` rate-limited internally |
| **Identity invariants** | 60 s | 1 | 7 | `CheckIdentityInvariantsIfNeeded` in Tick |

---

## 2. Heavy Work in Coordinator Timers

### 2.1 MismatchEscalationCoordinator.OnAuditTick (every 1s per engine)

**Source:** `RobotCore_For_NinjaTrader/Execution/MismatchEscalationCoordinator.cs`

```csharp
_auditTimer = new Timer(OnAuditTick, null, MismatchEscalationPolicy.MISMATCH_AUDIT_INTERVAL_MS, MismatchEscalationPolicy.MISMATCH_AUDIT_INTERVAL_MS);
// MISMATCH_AUDIT_INTERVAL_MS = 1000
```

**Work per tick:**
- `_getSnapshot()` → `GetAccountSnapshot()` — iterates `account.Positions` and `account.Orders`
- `_getActiveInstruments()` — engine state
- `_getMismatchObservations()` — LINQ, journal/registry queries
- Per-instrument processing: `ProcessObservation` / `ProcessCleanPass`

### 2.2 ProtectiveCoverageCoordinator.OnAuditTick (every 1s per engine)

**Source:** `RobotCore_For_NinjaTrader/Execution/ProtectiveCoverageCoordinator.cs`

```csharp
_auditTimer = new Timer(OnAuditTick, null, ProtectiveAuditPolicy.PROTECTIVE_AUDIT_INTERVAL_ACTIVE_MS, ...);
// PROTECTIVE_AUDIT_INTERVAL_ACTIVE_MS = 1000
```

**Work per tick:**
- `_getSnapshot()` → same `GetAccountSnapshot()` path
- `_getActiveInstruments()`
- Per instrument: `ProtectiveCoverageAudit.Audit()` — broker position vs stop/target coverage

### 2.3 GetAccountSnapshotReal (NinjaTrader API)

**Source:** `RobotCore_For_NinjaTrader/Execution/NinjaTraderSimAdapter.NT.cs`

```csharp
foreach (var position in account.Positions) { ... }
foreach (var order in account.Orders) { ... }
```

- NT `Account.Positions` and `Account.Orders` are live collections; iteration may involve locks/allocations
- Called **14×/second** from coordinators alone (7 engines × 2 coordinators)
- Additional calls: ReconciliationRunner (1×/60s), second reconciliation (~5 min), stream reconciliation, Phase 5 checks

---

## 3. IEA Stall Check Timer

**Source:** `RobotCore_For_NinjaTrader/Execution/InstrumentExecutionAuthority.cs`

```csharp
_stallCheckTimer = new Timer(CheckCommandStall, null, 2000, 2000);
```

- One timer per IEA; 7 IEAs → 7 timers firing every 2 seconds
- `CheckCommandStall` is lightweight (elapsed time check) but adds timer callback overhead

---

## 4. Logging Load

**Config:** `configs/robot/logging.json`

```json
{
  "enable_diagnostic_logs": true,
  "max_queue_size": 50000,
  "flush_interval_ms": 500,
  "max_batch_per_flush": 2000
}
```

- **enable_diagnostic_logs: true** — enables BAR_ACCEPTED, BAR_DELIVERY_TO_STREAM, SLOT_GATE_DIAGNOSTIC, etc.
- Queue reached **41,425** events (Mar 18) — near `max_queue_size`
- Flush every 500 ms with batches of 2000 — serialization + I/O on main/log thread

### EmitMetrics (every Tick)

**Source:** `RobotEngine.cs` lines 1814–1815

```csharp
_protectiveCoordinator?.EmitMetrics(utcNow);
_mismatchCoordinator?.EmitMetrics(utcNow);
```

- Called inside `TickInternal` (from OnBarUpdate) — **every bar** per engine
- With OnBarClose + 1-min bars: ~7 emissions/minute for each of RECONCILIATION_MISMATCH_METRICS and PROTECTIVE_AUDIT_METRICS
- Lower frequency than coordinators but adds to log volume

---

## 5. Timetable Poll

**Source:** `RobotEngine.cs`, `TimetableFilePoller`, `RobotSimStrategy.cs`

- Poll interval: **5 seconds** (`TimeSpan.FromSeconds(5)`)
- Per poll: `File.ReadAllBytes()` + SHA256 + JSON parse
- 7 engines → up to 7 polls every 5 seconds (each engine polls when `ShouldPoll` is true)
- Poll is **synchronous** and runs during Tick (inside `_engineLock`)

---

## 6. OnBarUpdate / OnMarketData

- **Calculate.OnBarClose** — bars every 1 minute
- **OnMarketData(Last)** — BE evaluation with 200 ms throttle per instrument
- Tick runs on every bar close; all periodic work (forced flatten, gap violations, health monitor, EmitMetrics) runs inside Tick under `_engineLock`

---

## 7. Recent Commits (Likely Contributors)

| Commit | Changes |
|--------|---------|
| `e7ef6ab` | Execution IEA phases, order registry, **MismatchEscalationCoordinator**, **ProtectiveCoverageCoordinator**, watchdog updates |
| `a4d2fae` | IEA forced flatten, eventWriter, emergencyFlatten |
| `c5dbe1e` | Data stall detection redesign |
| `5f28a32` | ORDER_STUCK fixes |

The **MismatchEscalationCoordinator** and **ProtectiveCoverageCoordinator** (1s timers + GetAccountSnapshot) are the most likely CPU drivers from recent work.

---

## 8. Recommended Mitigations

### 8.1 High impact — coordinator intervals

**Change:** Increase audit intervals from 1s to 5s (or configurable)

| Constant | Current | Proposed | File |
|----------|---------|----------|------|
| `MISMATCH_AUDIT_INTERVAL_MS` | 1000 | 5000 | `MismatchEscalationModels.cs` |
| `PROTECTIVE_AUDIT_INTERVAL_ACTIVE_MS` | 1000 | 5000 | `ProtectiveCoverageModels.cs` |

**Effect:** Reduces coordinator-triggered `GetAccountSnapshot` from 14/s to ~2.8/s.

### 8.2 High impact — shared/cached account snapshot

**Change:** Cache `AccountSnapshot` at engine level, refresh on a single timer (e.g. 2–5s), and pass the cached snapshot to both coordinators.

- Avoids 14 independent snapshots per second
- Coordinators would use slightly stale data (2–5s) — acceptable for mismatch/protective audits

### 8.3 Medium impact — diagnostic logging

**Change:** Set `enable_diagnostic_logs: false` in production.

- Reduces log volume and queue pressure
- Re-enable only for targeted debugging

### 8.4 Medium impact — timetable poll interval

**Change:** Increase from 5s to 10s or 15s.

- Timetable changes are infrequent; 10–15s is sufficient for config reactivity

### 8.5 Lower impact — IEA stall check interval

**Change:** Increase from 2s to 5s.

- Stall detection is for debugging; 5s is still responsive

### 8.6 EmitMetrics rate limit

**Change:** Rate-limit `EmitMetrics` to once per 60s (or when state changes) instead of every Tick.

- Reduces RECONCILIATION_MISMATCH_METRICS and PROTECTIVE_AUDIT_METRICS volume

---

## 9. Verification Plan

1. **Baseline:** Run with current settings, capture CPU % for NinjaTrader process over 5–10 minutes.
2. **Mitigation 1:** Apply coordinator interval increase (1s → 5s); re-measure CPU.
3. **Mitigation 2:** Disable diagnostic logs; re-measure.
4. **Mitigation 3:** If needed, implement shared account snapshot cache.

---

## 10. References

- `RobotCore_For_NinjaTrader/Execution/MismatchEscalationCoordinator.cs`
- `RobotCore_For_NinjaTrader/Execution/ProtectiveCoverageCoordinator.cs`
- `RobotCore_For_NinjaTrader/Execution/MismatchEscalationModels.cs` (MISMATCH_AUDIT_INTERVAL_MS)
- `RobotCore_For_NinjaTrader/Execution/ProtectiveCoverageModels.cs` (PROTECTIVE_AUDIT_INTERVAL_ACTIVE_MS)
- `RobotCore_For_NinjaTrader/Execution/InstrumentExecutionAuthority.cs` (stall timer)
- `RobotCore_For_NinjaTrader/Execution/NinjaTraderSimAdapter.NT.cs` (GetAccountSnapshotReal)
- `RobotCore_For_NinjaTrader/RobotEngine.cs` (Tick, EmitMetrics, timetable poll)
- `configs/robot/logging.json`
