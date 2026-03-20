# Strategy Assessment: NinjaTrader Disconnect & Reconnect

**Date**: 2026-03-18  
**Scope**: Can the RobotSimStrategy cause NinjaTrader to disconnect or stay disconnected until restart?

---

## Executive Summary

**Conclusion**: The strategy does **not** directly cause disconnects or prevent NinjaTrader from reconnecting. It does **not** call any NinjaTrader connection APIs. It could **indirectly** contribute to platform strain through load (7 instances, synchronous I/O, API contention), but the "stays red until restart" behavior is consistent with NinjaTrader/data-provider behavior, not strategy logic.

---

## 1. Connection API Usage

### Does the strategy call Connection.Disconnect, Connection.Connect, or any connection control API?

**No.** Grep of `RobotCore_For_NinjaTrader` and `modules/robot`:

- **Connection.Disconnect** – Not called
- **Connection.Connect** – Not called  
- **Reconnect** – Only used in recovery state names (`RECONNECTED_RECOVERY_PENDING`, `BeginReconnectRecovery`); no NinjaTrader connection API calls

The strategy only **reads** connection status via `OnConnectionStatusUpdate()`, which NinjaTrader invokes when status changes.

---

## 2. OnConnectionStatusUpdate Behavior

**Location**: `RobotEngine.cs` lines 4517–4594

**What it does**:
1. Locks `_engineLock`
2. Updates `_lastConnectionStatus`
3. Forwards to `HealthMonitor.OnConnectionStatusUpdate()` (logging, push notification)
4. Updates recovery state (`DISCONNECT_FAIL_CLOSED`, `RECONNECTED_RECOVERY_PENDING`)
5. Releases lock

**Duration**: Short (state updates, no I/O, no network). No Connection API calls.

**Risk**: None identified. Does not block or interfere with NinjaTrader’s connection logic.

---

## 3. Blocking Operations (Potential Load)

Operations that run on NinjaTrader’s strategy/event thread and could add load:

| Operation | Location | Frequency | Blocking? |
|-----------|----------|-----------|-----------|
| **Timetable poll** | `TimetableFilePoller.Poll()` → `File.ReadAllBytes()` | Every 5s per engine (7 engines) | Yes, synchronous file read |
| **TimetableCache** | `TimetableCache.GetOrLoad()` | Shared across engines; cache reduces reads | Yes, but cached |
| **EnqueueAndWait** | `InstrumentExecutionAuthority.EnqueueAndWait()` → `done.Wait(timeoutMs)` | On order submit, protective submit, flatten | Yes, blocks until worker completes or timeout (5–10s) |
| **ExecutionJournal** | `File.ReadAllText()`, `File.Exists()`, `Directory.GetFiles()` | Bootstrap, reconciliation, journal lookups | Yes, synchronous |
| **JournalStore** | `File.ReadAllText()`, `File.WriteAllText()` | Stream journal read/write | Yes; retry uses `Thread.Sleep(10)` |
| **Logging** | `RobotLoggingService.Log()` | High volume | No – enqueue only; worker does I/O |

**Impact**: These can add CPU and disk I/O load. Under heavy load, NinjaTrader’s API (e.g. `CreateOrder`/`Submit`) has been observed to block for >6s (YM1 incident). That can cause `EnqueueAndWait` timeouts and instrument blocks, but does not control connection state.

---

## 4. ConnectionStatusAddOn

**Location**: `modules/robot/ninjatrader/ConnectionStatusAddOn.cs`

**Behavior**: Subscribes to `Connection.ConnectionStatusUpdate` and forwards to `RobotEngine.OnConnectionStatusUpdate()`.

**Connection API usage**: None. Only subscribes to events and forwards them.

---

## 5. What Could Cause "Stays Red Until Restart"?

NinjaTrader support and docs indicate:

1. **Manual reconnect** – If the connection is physically down, NinjaTrader often requires manual reconnect (Tools → Connections → Connect).
2. **Data provider** – Recovery depends on the provider (CQG, Kinetick, etc.) re-establishing the connection.
3. **4+ disconnects in 5 minutes** – NinjaTrader disables strategies; restart is required.

The strategy does not:
- Initiate disconnects
- Block NinjaTrader’s connection recovery logic
- Call any connection control APIs

---

## 6. Indirect Contribution to Load

The strategy can add platform load:

| Factor | Effect |
|--------|--------|
| 7 strategy instances | 7× engines, adapters, HealthMonitors, IEAs |
| Timetable poll (5s) | 7 × `File.ReadAllBytes` every 5s (mitigated by cache) |
| Order/execution callbacks | All on NinjaTrader event thread; 7 instances share it |
| Journal I/O | Synchronous reads during bootstrap and reconciliation |
| Logging backpressure | Queue reached 41,425 (Mar 18); indicates heavy logging load |

Under load, NinjaTrader may be more likely to drop or fail to recover the connection, but that is platform/network behavior, not a direct strategy bug.

---

## 7. Recommendations

### No Code Changes Required for Connection Logic

The strategy does not need changes to connection handling. It correctly observes status and does not interfere with NinjaTrader’s connection management.

### Optional Load Reductions

| Action | Purpose |
|--------|---------|
| Increase timetable poll interval | e.g. 5s → 10s to reduce file I/O |
| Reduce instruments | Fewer strategy instances to test impact on disconnects |
| Disable diagnostic logging in production | Lower logging volume and backpressure |
| Stagger strategy startup | Avoid 7 simultaneous BarsRequests and initializations |

### Platform / Environment

| Action | Purpose |
|--------|---------|
| Increase Disconnect delay | Tools → Options → Strategies |
| Use VPS | More stable network for NinjaTrader |
| Contact NinjaTrader support | Send logs/traces for connection diagnosis |

---

## 8. Files Reviewed

- `RobotCore_For_NinjaTrader/RobotEngine.cs` – `OnConnectionStatusUpdate`, Tick, timetable poll
- `RobotCore_For_NinjaTrader/Strategies/RobotSimStrategy.cs` – Strategy host, no connection APIs
- `RobotCore_For_NinjaTrader/HealthMonitor.cs` – Connection status handling
- `RobotCore_For_NinjaTrader/Execution/InstrumentExecutionAuthority.cs` – `EnqueueAndWait`
- `RobotCore_For_NinjaTrader/TimetableCache.cs`, `TimetableFilePoller.cs` – File I/O
- `RobotCore_For_NinjaTrader/Execution/ExecutionJournal.cs` – Journal I/O
- `modules/robot/ninjatrader/ConnectionStatusAddOn.cs` – Event forwarding only

---

## 9. Conclusion

The strategy does **not** cause disconnects or prevent reconnect through any direct mechanism. It does not call NinjaTrader connection APIs and only reacts to connection status. The "stays red until restart" behavior aligns with NinjaTrader and data-provider behavior, not with strategy logic. The strategy can contribute indirectly through load; reducing load (fewer instruments, less I/O, less logging) may help stability.
