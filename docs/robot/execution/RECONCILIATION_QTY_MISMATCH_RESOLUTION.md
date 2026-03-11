# Reconciliation QTY_MISMATCH — Resolution Guide

**When**: Engine logs `RECONCILIATION_QTY_MISMATCH` (account_qty ≠ journal_qty)  
**Effect**: Instrument frozen, all streams stood down, no range calculation, no execution

---

## 1. Understanding the Mismatch

| Field | Meaning |
|-------|---------|
| `account_qty` | Broker-reported position (from `GetAccountSnapshot`) |
| `journal_qty` | Sum of `EntryFilledQuantityTotal` for open journals (EntryFilled=true, TradeCompleted=false) |

**Why it happens**: Positions were closed externally (manual flatten, stop hit, broker action) without the journal receiving exit fills. Those journals stay open. Reconciliation only runs when the broker is **flat**, so it cannot close orphan journals while you still have a position.

---

## 2. Resolution Steps

### Option A: Flatten Then Let Reconciliation Run (Recommended)

1. **Flatten the account** for the affected instrument (manual close or strategy flatten).
2. **Restart the robot** (or wait for the next reconciliation pass, every 60s).
3. Reconciliation will detect broker flat + open journals → close them with `RECONCILIATION_BROKER_FLAT`.
4. **Re-enter** positions if needed; streams will be unblocked once journals match account.

### Option B: Force-Reconcile Trigger (Recommended for Manual Override)

When you **confirm** the account is correct and journals are stale:

1. Run the script:
   ```powershell
   .\scripts\force_reconcile.ps1 MYM
   # Or multiple: .\scripts\force_reconcile.ps1 MYM MCL
   ```
2. This creates `data/pending_force_reconcile.json` with `{"instruments": ["MYM"]}`.
3. The robot picks it up on the next Tick cycle (~1 second).
4. Orphan journals are marked `RECONCILIATION_MANUAL_OVERRIDE` and closed.
5. Check logs for `FORCE_RECONCILE_EXECUTED` and `FORCE_RECONCILE_COMPLETE`.

**Risk**: Only use after verifying broker position. Prefer Option A when possible.

### Option C: Manual Journal Closure (Advanced)

If the script is unavailable, edit journals directly:

1. **Back up** `data/execution_journals/` before editing.
2. For each open journal contributing to the mismatch:
   - Open `{tradingDate}_{stream}_{intentId}.json`
   - Set `TradeCompleted: true`
   - Set `CompletionReason: "RECONCILIATION_MANUAL_OVERRIDE"`
   - Set `CompletedAtUtc` to current UTC (ISO 8601)
   - Set `ExitFilledQuantityTotal` = `EntryFilledQuantityTotal`
   - Set `ExitOrderType: "RECONCILIATION_MANUAL_OVERRIDE"`
3. **Restart the robot** so it picks up the updated journals.

**Risk**: Incorrect edits can corrupt state. Prefer Option A or B when possible.

### Option D: Identify and Resolve Orphans (Diagnostic)

1. List open MYM journals:
   ```powershell
   Get-ChildItem data\execution_journals -Filter "*YM*" | ForEach-Object {
     $j = Get-Content $_.FullName | ConvertFrom-Json
     if ($j.Instrument -eq "MYM" -and $j.EntryFilled -eq $true -and $j.TradeCompleted -ne $true) {
       Write-Host "$($_.Name) EntryFilledQty=$($j.EntryFilledQuantityTotal)"
     }
   }
   ```
2. For each file: verify the position was actually closed (broker statements, NT account).
3. If closed: use Option B to mark completed. If not: investigate why the journal thinks it’s open.

---

## 3. Prevention

| Action | Purpose |
|--------|---------|
| Avoid manual flattens during active slots | Reduces orphan journals (no exit fill) |
| Restart robot after manual flatten | Triggers reconciliation when broker goes flat |
| Monitor `RECONCILIATION_QTY_MISMATCH` | Catch mismatches early (push notification if HealthMonitor enabled) |
| Use `scripts/force_reconcile.ps1` | Force-close orphan journals when account confirmed correct |

---

## 4. Softer Freeze (Range Building Continues)

When a mismatch occurs, the engine now:
- **Blocks execution** for the instrument (RiskGate `INSTRUMENT_FROZEN`)
- **Stands down only streams with positions** (entry fills)
- **Allows streams without positions** to continue through PRE_HYDRATION → RANGE_BUILDING → RANGE_LOCKED

So YM2 can compute its range even during a mismatch; only order submission is blocked. Unfreeze happens automatically when the next reconciliation pass finds matching qty.

## 5. Related Code

- `ReconciliationRunner.cs` – qty check, orphan reconciliation
- `ExecutionJournal.GetOpenJournalQuantitySumForInstrument` – journal_qty calculation
- `ExecutionJournal.RecordReconciliationComplete` – marks journal closed
- `RobotEngine.StandDownStreamsForInstrument` – freeze on mismatch

---

## 6. Incident References

- `docs/robot/incidents/2026-02-17_YM2_RANGE_FAILURE_POSTMORTEM.md` — 2026-02-17 YM2 range failure caused by MYM RECONCILIATION_QTY_MISMATCH
- `docs/robot/incidents/2026-03-11_MYM_RECONCILIATION_QTY_MISMATCH_INVESTIGATION.md` — Root cause: per-instance ExecutionJournal cache serving stale data across multiple NinjaTrader strategy instances. Fix: always read from disk in GetOpenJournalEntriesByInstrument.
