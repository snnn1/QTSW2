# IEA Fill Path Audit — Before Changing IEA Behavior

**Date:** 2026-03-19  
**Scope:** Instrument Execution Authority (IEA) fill path, heartbeat, registry, journal  
**Framing:** Tail-latency and burst-cost audit on fills, not generic CPU

---

## 1. Does ProcessOrderUpdate / ProcessExecutionUpdate ever block on I/O?

**Clarification:** The fill path uses **ProcessExecutionUpdate** → `HandleExecutionUpdateReal`, not `ProcessOrderUpdate`. Both run on the same IEA worker. `ProcessOrderUpdate` handles order state changes (e.g. Working, Cancelled).

### Yes — blocking I/O in the fill path

| Source | Blocking? | Details |
|--------|-----------|---------|
| **Journal writes** | **Yes** | `RecordEntryFill` / `RecordExitFill` call `SaveJournal()` which does `FileStream` + `StreamWriter.Write(json)` — **sync disk I/O** |
| **Journal reads** | **Yes** | On cache miss: `File.ReadAllText(journalPath)` — **sync disk I/O** inside `lock (_lock)` |
| **File access** | **Yes** | Journal: `File.Exists`, `File.ReadAllText`, `SaveJournal` (FileStream write). All inside journal `_lock` |
| **Sync logging** | **No** | `RobotLoggingService.Write` enqueues to `_queue`; background worker flushes. Non-blocking |
| **External thread handoff** | **No** | Protective submission uses `EnqueueNtAction(cmd)` — enqueues for strategy thread, does not wait |

**Conclusion:** The fill path **does block on sync disk I/O** (journal). Cache hits avoid reads; every fill does at least one `SaveJournal` write. Blocking I/O in the worker turns CPU symptoms into latency symptoms.

---

## 2. Does ProcessOrderUpdate / ProcessExecutionUpdate hold locks during journal writes or expensive bookkeeping?

### Yes — journal holds lock during I/O

| Lock | Held during | Location |
|------|-------------|----------|
| `ExecutionJournal._lock` | Entire `RecordEntryFill` / `RecordExitFill` | `ExecutionJournal.cs` |
| | Includes: validation, cache lookup, **File.ReadAllText** (cache miss), **SaveJournal** (FileStream write) | |
| IEA locks | **No** | IEA does not hold its own locks during adapter work. `OrderMap` is `ConcurrentDictionary` |

**Conclusion:** The journal holds `_lock` for the full duration of journal writes and reads. This is a bigger architectural issue than raw CPU — any other thread needing the journal (e.g. `GetActiveIntentsForBEMonitoring` during heartbeat) will block on the same lock.

---

## 3. Can protective submission be decoupled from the heavy bookkeeping portion?

### Already decoupled

- Protective submission: `EnqueueNtAction(new NtSubmitProtectivesCommand(...))` — enqueues for strategy thread, returns immediately.
- Heavy bookkeeping: `RecordEntryFill` / `RecordExitFill`, OrderMap updates, etc. — all run on the worker before the enqueue.

**Flow:** Fill → HandleExecutionUpdateReal → RecordEntryFill (sync I/O) → OnEntryFill → EnqueueNtAction(protectives). The protectives are submitted asynchronously; the worker does not wait for strategy-thread execution.

**Conclusion:** Protective submission is already decoupled. The heavy part is journal I/O and in-memory bookkeeping.

---

## 4. Are registry cleanup and integrity verification full scans?

### Yes — both are full scans

| Operation | What it scans | Cost scaling |
|-----------|---------------|--------------|
| **RunRegistryCleanup** | `_orderRegistry.CleanupTerminalOrders(cutoff, exclude)` | Full scan of `_byBrokerOrderId` |
| | For each entry: check lifecycle, terminal time, exclude-if-active | O(registry size) |
| **VerifyRegistryIntegrity** | `account.Orders` (NT API) + `_orderRegistry.GetWorkingOrderIds()` | Full scan of broker orders + full scan of registry |
| | `GetWorkingOrderIds()` = `_byBrokerOrderId.Where(...).Select(...).ToList()` | O(broker orders) + O(registry) |

**Registry size:**

- `_byBrokerOrderId` holds: active (WORKING, SUBMITTED, PART_FILLED) + terminal (FILLED, CANCELED, REJECTED) within `TerminalRetentionMinutes` (10 min).
- Cost scales with **active intents + historical residue** (terminal orders in retention window).
- No explicit cap on registry size; `SharedAdoptedOrderRegistry` has `MaxEntries` for adoption, but main registry does not.

**GetActiveIntentsForBEMonitoring (called before RunRegistryCleanup):**

- Iterates `IntentMap` (all intents).
- For each: `GetEntry(intentId, tradingDate, stream)` and `IsBEModified` — both can do `File.ReadAllText` on cache miss, inside journal `_lock`.
- Cost: O(intents) × (cache hit or disk read).

---

## 5. Is heartbeat truly supervisory, or does it enforce safety-critical behavior?

### Mixed — some safety-critical, most observability/hygiene

| Helper | Role | Safety-critical? |
|--------|------|-------------------|
| **CheckFlattenLatchTimeouts** | Releases stuck flatten latches | **Yes** — prevents latch held indefinitely |
| **EmitRecoveryMetrics** | Metrics | No — observability |
| **TryExpireCooldown** | Cooldown expiry | Hygiene |
| **EmitSupervisoryMetrics** | Metrics | No — observability |
| **RunRegistryCleanup** | Remove old terminal orders | Hygiene — prevents unbounded growth |
| **VerifyRegistryIntegrity** | Detect registry vs broker divergence, adopt orphaned orders | **Yes** — consistency and adoption |
| **EmitRegistryMetrics** | Metrics | No — observability |
| **EmitExecutionOrderingMetrics** | Metrics | No — observability |
| **Log.Write (IEA_HEARTBEAT)** | Metrics | No — observability |

**Conclusion:** Heartbeat is **not purely supervisory**. Two parts are safety-critical:

1. **CheckFlattenLatchTimeouts** — releases stuck latches.
2. **VerifyRegistryIntegrity** — detects divergence and adopts broker orders not in registry.

The rest is observability and hygiene. Heartbeat should **not** tax fill completion latency, but it currently runs **immediately after** each work item when 60s has passed — so a fill that happens to land when heartbeat is due will see tail latency from the full heartbeat run.

---

## 6. Worker loop and heartbeat timing

```text
WorkerLoop:
  if (TryTake(work, 1000))
    work();           // Fill path runs here
    EmitHeartbeatIfDue();  // Runs on same thread, right after work
```

When `(now - _lastHeartbeatUtc).TotalSeconds >= 60`, `EmitHeartbeatIfDue` runs **on the same worker thread** after the fill. So:

- p50: fill-only cost (journal write, bookkeeping).
- p99: fill + full heartbeat (registry scans, VerifyRegistryIntegrity, GetActiveIntentsForBEMonitoring, Log.Write) when heartbeat is due.

**Expected pattern:** p50 low, p95 modest, **p99 spikes when heartbeat runs** (e.g. 80–150 ms vs 3–8 ms).

---

## 7. Recommended next step: instrumentation first

Before changing IEA behavior:

1. **Add stage-level timings** around:
   - `ProcessExecutionUpdate` (fill path) total
   - `RecordEntryFill` / `RecordExitFill` (journal)
   - `EmitHeartbeatIfDue` total
   - Each heartbeat helper: `CheckFlattenLatchTimeouts`, `RunRegistryCleanup`, `VerifyRegistryIntegrity`, `GetActiveIntentsForBEMonitoring`, etc.

2. **Tag whether heartbeat ran** on that work item (e.g. `heartbeat_ran: true` in metrics).

3. **Emit fill-path metrics** with percentiles (p50, p95, p99) and `heartbeat_ran` so we can see:
   - Baseline fill latency vs fill+heartbeat latency
   - Which helper dominates when heartbeat runs

After measurement, decide:

- Stall timer 2s → 5s (safe, minor gain)
- Heartbeat cadence split (safety every 60s, cleanup/integrity every 180s, metrics every 180s)
- Registry scan optimization
- Journal write optimization (async, or move off critical path)
- Fill-path decomposition

---

## 8. Summary table

| Question | Answer |
|----------|--------|
| Blocking I/O in fill path? | Yes — journal sync File.ReadAllText, SaveJournal (FileStream write) |
| Locks during journal I/O? | Yes — `ExecutionJournal._lock` held for full RecordEntryFill/RecordExitFill |
| Protective submission decoupled? | Yes — already enqueued for strategy thread |
| Registry cleanup full scan? | Yes — O(registry size) |
| VerifyRegistryIntegrity full scan? | Yes — O(broker orders) + O(registry) |
| Heartbeat safety-critical? | Partially — CheckFlattenLatchTimeouts, VerifyRegistryIntegrity |
| Next step | Instrument first: stage timings, heartbeat tag, percentiles |
