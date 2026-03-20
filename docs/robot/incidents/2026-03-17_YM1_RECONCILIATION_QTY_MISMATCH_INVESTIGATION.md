# YM1 RECONCILIATION_QTY_MISMATCH — Full Investigation

**Date**: 2026-03-17  
**Scope**: Critical event RECONCILIATION_QTY_MISMATCH for YM1 (stream) / MYM (execution instrument)  
**Instrument mapping**: YM1 stream → MYM (micro Dow) execution instrument

---

## 1. WHAT IS RECONCILIATION_QTY_MISMATCH

### Trigger Condition

`ReconciliationRunner` compares **broker position quantity** vs **execution journal quantity** per instrument. When they differ, it emits:

- `RECONCILIATION_CONTEXT` (diagnostic: intent_ids, last_fills, journal_dir, open_instruments_qty)
- `RECONCILIATION_QTY_MISMATCH` (critical)
- `POSITION_DRIFT_DETECTED`
- `EXPOSURE_INTEGRITY_VIOLATION`
- `_onQuantityMismatch` callback

### Downstream Effects

| Action | Code Path |
|--------|-----------|
| **StandDownStreamsForInstrument** | All streams for instrument (YM1 for MYM) enter recovery |
| **RequestRecoveryForInstrument** | IEA recovery → reconstruction → FLATTEN if JOURNAL_BROKER_MISMATCH |
| **RequestSupervisoryAction** | REPEATED_RECONCILIATION_MISMATCH, MEDIUM severity |
| **ReportCritical** | HealthMonitor → push notification |
| **Instrument freeze** | `_frozenInstruments.Add(instrument)` — blocks new execution |

---

## 2. CODE PATH (Full Trace)

### 2.1 Entry Points

| Step | File | Method | When |
|------|------|--------|------|
| 1 | `RobotEngine.cs` | `RunReconciliationOnRealtimeStart` | State.Realtime transition |
| 2 | `RobotEngine.cs` | `RunPeriodicThrottle` (via timer) | Every 60s (`ThrottleIntervalSeconds`) |
| 3 | `ReconciliationRunner.cs` | `RunInternal` | Called by both above |

### 2.2 Reconciliation Logic

```
ReconciliationRunner.RunInternal(utcNow):
  1. snap = _adapter.GetAccountSnapshot(utcNow)
  2. accountQtyByInstrument = from snap.Positions (inst → sum of |Quantity|)
  3. openByInstrument = _journal.GetOpenJournalEntriesByInstrument()  // reads from DISK (no cache)
  4. For each inst in accountQtyByInstrument:
       execVariant = inst.StartsWith("M") ? inst : "M" + inst   // MYM stays MYM, YM → MYM
       journalQty = _journal.GetOpenJournalQuantitySumForInstrument(inst, execVariant)
       if journalQty != accountQty:
           Emit RECONCILIATION_CONTEXT, RECONCILIATION_QTY_MISMATCH, etc.
           _onQuantityMismatch(inst, utcNow, "QTY_MISMATCH:account=X,journal=Y")
```

### 2.3 Journal Lookup

**GetOpenJournalQuantitySumForInstrument(executionInstrument, canonicalInstrument)**:
- Iterates `GetOpenJournalEntriesByInstrument()` result
- Matches key where: `key == executionInstrument` OR `key == canonicalInstrument`
- Sums `entry.EntryFilledQuantityTotal` for matching entries

**GetOpenJournalEntriesByInstrument()** (disk read, no cache since 2026-03-11):
- Scans `_journalDir/*.json`
- Filename format: `{tradingDate}_{stream}_{intentId}.json` (e.g. `2026-03-17_YM1_abc123.json`)
- Includes entry only if: `EntryFilled && !TradeCompleted && EntryFilledQuantityTotal > 0`
- Keys result by `entry.Instrument` (e.g. MYM, YM)

### 2.4 YM1 / MYM Mapping

| Concept | Value |
|---------|-------|
| **Stream** | YM1 (slot journal, stream state) |
| **Execution instrument** | MYM (micro Dow) |
| **Broker position Instrument** | Typically MYM or YM (from NinjaTrader) |
| **Journal entry.Instrument** | MYM (from intent at record time) |
| **Canonical** | YM (ExecutionInstrumentResolver: MYM → YM) |

Reconciliation uses `inst` from broker positions. If broker reports "MYM", we look up journal by MYM. If broker reports "YM", execVariant = "MYM", and we match journal key MYM or YM.

---

## 3. KNOWN ROOT CAUSES (From Past Incidents)

### 3.1 Per-Instance Stale Cache (2026-03-11 — FIXED)

**Symptom**: journal_qty=0 while broker has position; journal on disk correct.

**Cause**: Each strategy instance had its own ExecutionJournal cache. Instance B cached journal before Instance A (YM1) wrote the fill. Cache returned stale EntryFilled=false.

**Fix**: `GetOpenJournalEntriesByInstrument` always reads from disk; bypasses cache. See `ExecutionJournal.cs` lines 1442–1456.

### 3.2 File Lock / Read Failure (2026-03-04, 2026-03-06)

**Symptom**: journal_qty=0; journal exists on disk.

**Cause**: `File.ReadAllText` threw (file lock from another instance). Catch block skipped file silently.

**Fix**: `ReadJournalFileWithRetry` (3 retries, FileShare.ReadWrite), `EXECUTION_JOURNAL_READ_SKIPPED` when read fails. Journal writes use `FileShare.Read` so concurrent reads succeed.

### 3.3 Instrument Key Mismatch (2026-02-26 M2K)

**Symptom**: Journal had Instrument=RTY, reconciliation looked up M2K.

**Cause**: Journal stored canonical (RTY) while reconciliation used execution (M2K). No match.

**Fix**: Journal now stores execution instrument (MYM, M2K). Reconciliation uses both inst and execVariant (M+inst for YM→MYM).

### 3.4 Wrong Project Root / Journal Path

**Symptom**: journal_qty=0; journal in different directory.

**Cause**: NinjaTrader cwd differs from project root; `_journalDir` resolves to wrong path.

**Mitigation**: Set `QTSW2_PROJECT_ROOT` before starting NinjaTrader. `RECONCILIATION_CONTEXT` includes `journal_dir` for verification.

---

## 4. YM1-SPECIFIC RISKS

| Risk | Description |
|------|--------------|
| **Multi-instance** | 9+ engines; YM1 chart is one. Others (MNQ, MES, etc.) also run reconciliation. All share same journal dir. Disk read ensures consistency. |
| **Broker instrument naming** | NinjaTrader may report position as "YM" or "MYM". Reconciliation handles both via execVariant. |
| **Journal filename** | `{date}_YM1_{intentId}.json` — stream in filename; `entry.Instrument` in body must be MYM. |
| **Partial exit** | During target/stop hit, broker qty can transiently differ from journal (e.g. 3→1). May cause brief mismatch. |

---

## 5. DIAGNOSTIC STEPS

### 5.1 Run Diagnostic Script

```powershell
.\scripts\diagnose_reconciliation.ps1
```

Output includes:
- Risk latches (frozen instruments)
- Open journals by instrument (MYM journal_qty)
- Recent RECONCILIATION_QTY_MISMATCH / RECONCILIATION_CONTEXT from logs

### 5.2 Search Logs for YM1/MYM

```powershell
Select-String -Path "logs\robot\*.jsonl" -Pattern "RECONCILIATION_QTY_MISMATCH|RECONCILIATION_CONTEXT" | Where-Object { $_ -match "MYM|YM1" }
```

### 5.3 Check RECONCILIATION_CONTEXT Payload

When RECONCILIATION_QTY_MISMATCH fires, RECONCILIATION_CONTEXT is emitted first. Verify:

| Field | Expected |
|-------|----------|
| `instrument` | MYM |
| `broker_qty` | Broker position (e.g. 2) |
| `journal_qty` | Sum from journal (0 = problem) |
| `journal_dir` | `...\data\execution_journals` |
| `open_instruments_qty` | Dict of instrument → qty; MYM should have value if journal correct |
| `intent_ids` | List of open intent IDs |
| `mismatch_taxonomy` | `broker_ahead` (broker > journal) or `journal_ahead` (journal > broker) |

### 5.4 Check for EXECUTION_JOURNAL_READ_SKIPPED

```powershell
Select-String -Path "logs\robot\*.jsonl" -Pattern "EXECUTION_JOURNAL_READ_SKIPPED"
```

If present: file read failed (lock, corrupt, path error).

### 5.5 Verify Journal on Disk

```powershell
Get-ChildItem data\execution_journals -Filter "*YM1*" | ForEach-Object {
    $j = Get-Content $_.FullName -Raw | ConvertFrom-Json
    Write-Host "$($_.Name) Instrument=$($j.Instrument) EntryFilled=$($j.EntryFilled) TradeCompleted=$($j.TradeCompleted) Qty=$($j.EntryFilledQuantityTotal)"
}
```

---

## 6. RESOLUTION OPTIONS

### Option A: Force Reconcile (when broker position is correct)

```powershell
.\scripts\force_reconcile.ps1 MYM
```

Creates `pending_force_reconcile.json`; robot picks up on next Tick and unfreezes.

### Option B: Flatten and Restart

1. Manually flatten MYM position in NinjaTrader.
2. Restart robot. Reconciliation will close orphan journals when broker is flat.

### Option C: Fix Journal (if corrupt)

If journal has wrong Instrument, EntryFilled, or TradeCompleted:
- Correct the JSON file (risky; prefer Option B).
- Or delete journal and flatten (journal will be orphaned; reconciliation closes when flat).

---

## 7. PREVENTION CHECKLIST

| Check | Status |
|-------|--------|
| GetOpenJournalEntriesByInstrument reads from disk (no cache) | ✅ 2026-03-11 |
| ReadJournalFileWithRetry + FileShare.ReadWrite | ✅ |
| EXECUTION_JOURNAL_READ_SKIPPED on read failure | ✅ |
| Journal write uses FileShare.Read | ✅ |
| RECONCILIATION_CONTEXT includes journal_dir, open_instruments_qty | ✅ |
| execVariant handles YM→MYM | ✅ |
| QTSW2_PROJECT_ROOT set correctly | Operator |

---

## 8. RELATED INCIDENTS

| Doc | Instrument | Root Cause |
|-----|------------|------------|
| 2026-03-04_MYM_RECONCILIATION_QTY_MISMATCH_INVESTIGATION | MYM | File read failure (suspected) |
| 2026-03-11_MYM_RECONCILIATION_QTY_MISMATCH_INVESTIGATION | MYM | Per-instance cache (fixed) |
| 2026-03-06_ES2_MISSING_LIMIT_ORDER_INVESTIGATION | MES | RECONCILIATION_QTY_MISMATCH timing |
| 2026-03-17_YM1_TICK_STALE_TRADE_COMPLETED | YM1 | BE issues; trade completed |

---

## 9. SUMMARY

RECONCILIATION_QTY_MISMATCH for YM1/MYM occurs when broker position quantity ≠ journal quantity. Primary causes (now mitigated): stale per-instance cache (fixed 2026-03-11), file lock (retry + FileShare), instrument mismatch (journal uses MYM). For new incidents: run `diagnose_reconciliation.ps1`, check RECONCILIATION_CONTEXT and EXECUTION_JOURNAL_READ_SKIPPED, verify journal on disk. Resolve via force_reconcile or flatten+restart.
