# Chart Lag — Production Runbook

**Purpose:** When enabling strategies, anything below can contribute to chart lag.  
This runbook lists known sources, diagnostics, and fixes.

---

## 1. Symptoms

| Symptom | What user sees |
|---------|----------------|
| **UI freeze on enable** | Charts stall during DataLoaded / Historical |
| **Slow Historical** | Calculating phase takes minutes; progress bar crawls |
| **Realtime lag** | Bars render late; feels sluggish after market open |
| **In-position sluggishness** | Noticeable delay when BE path is active |
| **Log errors** | `robot_logging_errors.txt`: "file in use", queue backpressure |

---

## 2. Likely causes by phase

### Phase 1: DataLoaded (strategy startup)

| Source | Severity | Status | Description |
|--------|----------|--------|-------------|
| **Timetable load** | Medium | Mitigated | `File.ReadAllText(robotConfigPath)`, timetable parse. Once at startup. |
| **SessionIterator** | High | Deferred | `ResolveAndSetSessionCloseTime` uses `SessionIterator` — can block UI. **Deferred to Realtime**. |
| **BarsRequest** | Low | Non-blocking | Fire-and-forget; runs in background. |
| **Engine.Start()** | Medium | Mitigated | Loads timetable, creates streams, policy hash. No long synchronous I/O. |
| **TradingHours access** | Low | Wrapped | try-catch around TradingHours; can block if data not ready. |

### Phase 2: Historical / Calculating

| Source | Severity | Status | Description |
|--------|----------|--------|-------------|
| **Timetable poll** | **Critical** | **Fixed** | Was polling every bar during Historical → 4000+ disk reads. **Now skipped** (`shouldPoll = isRealtime && ...`). |
| **Forced flatten on wall clock** | **Critical** | **Fixed** | Was firing every tick during Historical. **OnMarketData removed**; Tick() only from OnBarUpdate. |
| **Per-bar work volume** | High | Ongoing | ~390 bars/day × days loaded. Each bar: bar-time logic, lock, stream loop, OnBar(), Tick(). 30 days = ~11,700 bars. |
| **TraceLifecycle** | Medium | **Gated** | `File.AppendAllText` every 250 bars. **Gated** when `diagnostic_logging_during_historical=false` (default). |
| **LogEngineEvent** | Medium | **Gated** | ONBARUPDATE_*, BAR_TIME_DETECTION_*, TICK_TIME_SOURCE. **Gated** when `diagnostic_logging_during_historical=false`. |
| **BAR_TIME_INTERPRETATION_MISMATCH** | Low | Rate-limited | Out-of-order bars after disconnect. Rate-limited 1/min per instrument. |
| **Bar-time interpretation** | Low | One-time | First bar: UTC vs Chicago detection. Locked afterward. |

**Observed from BAR_PROFILE_SLOW (Historical):**
- **onbar**: 18–155 ms (MGC hit 155 ms — main lag source)
- **tick**: 5–17 ms
- **diagnostics**: 13–15 ms

### Phase 3: Realtime

| Source | Severity | Status | Description |
|--------|----------|--------|-------------|
| **BIP 1 (1-second series)** | Medium | **Fixed** | BE + heartbeat. **Now**: only when in position, every 5s. Flat guard exits immediately. |
| **Heartbeat** | Low | **Moved** | Was in BIP 1. **Now**: BIP 0, every 1 bar (~1/min), Realtime only. |
| **OnMarketData** | **Critical** | **Removed** | Was driving Tick() every Last tick. **Removed entirely** — no per-tick path. |
| **Tick() from OnBarUpdate** | Medium | Ongoing | Once per bar. Lock, stream loop, forced-flatten check (when Realtime), timetable poll. |
| **Timetable poll** | Medium | Throttled | Every 2s when Realtime. `File.ReadAllBytes` + SHA256. 7 charts × 0.5/sec ≈ 3.5 disk reads/sec. |
| **Forced-flatten block** | Medium | Mitigated | HasEntryFillForStream (cached), session close, HandleForcedFlatten. Runs when `isRealtime`. |
| **Logging enqueue** | Low | Rate-limited | ENGINE_ALIVE: 120/min. Payload build + enqueue on strategy thread. |

### Phase 4: When in a position (BE path)

| Source | Severity | Status | Description |
|--------|----------|--------|-------------|
| **RunBreakEvenCheck** | Medium | **Fixed** | Runs every 5s (BIP 1 throttle). **Flat guard** skips when no position. |
| **GetActiveIntentsForBEMonitoring** | Medium | **Cached** | 200 ms cache. Journal lock + file reads when cache stale. |
| **ModifyStopToBreakEven** | High | **Throttled** | Was every tick until success. **Now**: 200 ms per intent. account.Orders + NT Change(). |
| **BE_EVALUATION_TICK** | Low | Minimal payload | Log once/sec per instrument. No intent_details. |

### Infrastructure / global

| Source | Severity | Status | Description |
|--------|----------|--------|-------------|
| **Log file contention** | **High** | **Mitigated** | `FileShare.Read` (reverted from ReadWrite) — avoids I/O race; "file in use" may return if multiple NT processes. |
| **Log queue backpressure** | Medium | Observed | Queue ~40k; writer can fall behind. Allocations + enqueue on hot path. |
| **Engine per chart** | Medium | Structural | 7 charts = 7 engines = 7× lock, stream loop, Tick(). |
| **Engine lock** | Low | Per-engine | **Per-engine**, not global. Each strategy creates its own `RobotEngine`; each engine has its own `_engineLock`. Charts do **not** block each other. |

---

## 3. Engine lock: global vs per-engine

**Answer: Per-engine instance.**

- Each strategy creates its own `RobotEngine` in DataLoaded (`_engine = new RobotEngine(...)`).
- Each engine has `private readonly object _engineLock = new object()` — an instance field.
- 7 charts = 7 engines = 7 separate locks.
- Only serializes Tick(), OnBar(), etc. **within** a single engine.
- Charts do **not** block each other on the engine lock.

---

## 4. Binary tests

| Test | What to do | Expected if lag |
|------|------------|-----------------|
| **Single chart** | Enable strategy on 1 chart only | Much faster than 7 charts |
| **Historical vs Realtime** | Enable during pre-market vs after open | Historical lag ≈ per-bar cost × bars; Realtime adds BIP 1, timetable poll |
| **Days to load** | 5 days vs 30 days | 30 days ≈ 6× slower Historical |
| **Diagnostic gating** | `diagnostic_logging_during_historical: true` vs `false` | `true` adds disk writes every 250 bars |
| **Log contention** | Check `robot_logging_errors.txt` | "file in use" → contention; queue backpressure → writer overload |

---

## 5. Config toggles

| Config | File | Effect |
|--------|------|--------|
| `diagnostic_logging_during_historical` | `configs/robot/robot.json` | `false` (default) = skip TraceLifecycle, ONBARUPDATE_*, BAR_TIME_* during Historical |
| `diagnostic_slow_logs` | `configs/robot/robot.json` | `true` = emit TICK_SLOW, BE_CHECK_SLOW when thresholds exceeded |
| `event_rate_limits.ENGINE_ALIVE` | `configs/robot/logging.json` | 120/min |
| `max_queue_size` | `configs/robot/logging.json` | 50000 — backpressure if exceeded |

---

## 6. What to check in logs

| Log | What to look for |
|-----|------------------|
| `logs/robot/robot_logging_errors.txt` | "file in use" → log contention; queue depth → backpressure |
| `logs/robot/robot_ENGINE.jsonl` | BAR_PROFILE_SLOW events → section (diagnostics, bar_time, onbar, tick) and duration |
| `logs/robot/strategy_lifecycle.log` | TraceLifecycle frequency during Historical (if gating off) |
| BAR_TIME_INTERPRETATION_MISMATCH | Out-of-order bars; rate-limited 1/min per instrument |

---

## 7. Fix playbook

### Already applied

1. **Timetable poll** — skipped during Historical
2. **OnMarketData** — removed
3. **BE flat guard** — BIP 1 exits when flat
4. **Heartbeat** — moved to BIP 0, every 1 bar
5. **BE throttle** — 5s BIP 1, 200 ms intent cache, 200 ms modify
6. **Diagnostic gating** — `diagnostic_logging_during_historical` gates Historical verbose logs
7. **BAR_PROFILE_SLOW** — identifies slow sections (diagnostics, bar_time, onbar, tick)
8. **Shared timetable cache** — `TimetableCache` reduces disk reads, hashing, parsing across all charts
9. **Log file FileShare.Read** — reverted from ReadWrite to avoid I/O race; "file in use" may occur with multiple NT processes
10. **Entry-fill cache** — `_entryFillByStream` + `WarmEntryFillCacheForTradingDate`; `HasEntryFillForStream` O(1) in hot path (no disk per bar in forced-flatten)
11. **Timetable poll interval** — increased from 2s to 5s to reduce disk I/O across charts

### Recommended next steps

#### Recommendation A: Shared timetable cache (fast win) — **IMPLEMENTED**

Right now each strategy hashing the same file is duplicated work. A **shared static cache** with:

- Last read timestamp
- Last hash
- Parsed timetable object

…reduces across all charts:

- Disk reads
- Hashing
- Parsing

**Status:** Implemented in `TimetableCache.cs`. Uses `(path, LastWriteTimeUtc)` as cache key; first engine to poll does I/O, others hit cache when file unchanged.

#### Recommendation B: Fix log file contention first (highest impact) — **IMPLEMENTED**

"File in use" errors are especially toxic because they:

- Add exception overhead
- Often trigger retries
- Can stall the writer
- Inflate the queue

Even if OnBar is heavy, **contention-induced exceptions can dominate user experience.**

**Fix:** Reverted to `FileShare.Read` — StreamWriter is not thread-safe; ReadWrite increased race probability. "File in use" may return with multiple NT processes; prefer that over silent corruption.

**Status:** Implemented in `RobotLoggingService.cs` — both main log and health sink use `FileShare.Read`.

### Remaining high-impact items

1. **OnBar() cost** — 155 ms observed; profile StreamStateMachine.Tick() and stream loop
4. **Days to load** — fewer days = fewer bars = less Historical work
5. **Number of charts** — each chart adds one engine; consider consolidating if possible

---

## 8. Tier 3: Performance (document only)

**Heavy instruments (MGC/MCL):** Observed MGC onbar 155 ms, MCL diagnostics 144 ms, M2K tick 84 ms. Cumulative CPU pressure on UI thread causes lag.

- **Profile** `StreamStateMachine.Tick()` for MGC/MCL
- **Reduce heavy charts** when testing MNQ (e.g. run fewer MGC/MCL strategies)
- **Long-term:** OnBar hot-path optimization — reduce allocations, streamline per-stream loops

---

## 9. OnBar() — main lag source (observed)

**BAR_PROFILE_SLOW** shows `onbar` up to **155 ms** (MGC). `OnBar()` does:

- Lock
- Bar date validation
- Stream routing (foreach stream)
- `StreamStateMachine.Tick(utcNow)` × N streams
- Range lock, PRE_HYDRATION, breakout detection, state transitions
- Hydration event persistence
- Logging

**Mitigation options:** Profile deeper; consider reducing streams per instrument; optimize stream.Tick() hot path.
