# Full System Investigation Summary — 2026-03-17

**Scope**: Bootstrap fix, order adoption, reconciliation, push notifications, watchdog, slot journals, multi-instance architecture.

---

## 1. Bootstrap Fix (Orders Preserved on Restart)

### What Was Implemented
- **BootstrapPhase4Types.cs**: Added `SlotJournalShowsEntryStopsExpected` to snapshot; when true + `LIVE_ORDERS_PRESENT_NO_POSITION` → ADOPT instead of FLATTEN.
- **NinjaTraderSimAdapter**: New callback `HasSlotJournalWithEntryStopsForInstrument`; sets flag when building snapshot.
- **RobotEngine**: `HasSlotJournalWithEntryStopsForInstrument(instrument)` checks slot journals for streams with `LastState=RANGE_LOCKED` and `StopBracketsSubmittedAtLock=true`.

### Slot Journal State (2026-03-17)
| Stream | LastState | StopBracketsSubmittedAtLock | Committed |
|--------|-----------|-----------------------------|-----------|
| NQ2    | RANGE_LOCKED | true                     | true      |
| GC2    | RANGE_LOCKED | true                     | true      |
| YM1    | RANGE_LOCKED | true                     | true      |
| NG1, NG2, YM2, CL2, RTY2 | (similar) | - | - |

For MNQ and MGC, slot journals **do** show RANGE_LOCKED + StopBracketsSubmittedAtLock. The fix logic should therefore return true for those instruments.

### Verification Gap
- **BOOTSTRAP_SNAPSHOT_CAPTURED** is not present in logs (event may be routed elsewhere or not emitted).
- Cannot confirm from logs whether the new bootstrap path ran or what decision was taken.

---

## 2. Current Issue: ORDER_REGISTRY_MISSING

### Observed State (20:34 UTC)
```
MNQ: broker_working=2, iea_working=0, journal_working=0
MGC: broker_working=2, iea_working=0, journal_working=0
```

- Broker has 2 working orders per instrument.
- IEA registry has 0.
- Reconciliation treats this as ORDER_REGISTRY_MISSING and blocks instruments.

### Earlier Adoption (15:30)
- `REGISTRY_BROKER_DIVERGENCE_ADOPTED` for MNQ (broker_order_id 408453624052, 408453624055).
- Adoption did run successfully in that session.

### Root Cause Hypotheses

1. **Bootstrap timing**: Bootstrap may run before broker has fully reported orders. At snapshot time, `broker_working` could be 0 → CLEAN_START/RESUME, no adoption. Later, broker reports 2 orders, but no adoption path runs.
2. **Process restart**: New run_ids at 20:34 indicate NinjaTrader/engine restart. IEA registry is in-process and is cleared on restart. Bootstrap runs again; if it sees `broker_working=0` initially, adoption is never triggered.
3. **ScanAndAdopt scope**: `ScanAndAdoptExistingProtectives` may only adopt protective orders (stop/target), not entry stops. Entry stops might need a separate adoption path.

---

## 3. Multi-Instance Architecture

### Observed
- 9+ distinct `run_id`s → 9+ engine instances (one per chart).
- Each engine has its own adapter (ExecutionAdapterFactory creates a new adapter per engine).
- IEA is shared per (account, executionInstrumentKey) via `InstrumentExecutionAuthorityRegistry`.
- Each engine calls `SetEngineCallbacks` on its own adapter; no cross-engine overwrite.

### Implication
- Bootstrap for MNQ uses the MNQ engine’s callback, which has NQ streams. Callback routing is correct.
- RECONCILIATION_ORDER_SOURCE_BREAKDOWN is logged by each engine; they all see the same broker vs IEA mismatch.

---

## 4. MNG: RECONCILIATION_QTY_MISMATCH

### State
- MNG IEA in **FLATTENING** (classification: JOURNAL_BROKER_MISMATCH).
- RECONCILIATION_QTY_MISMATCH triggered flatten.
- Account has 3 positions; BE gate blocked (WRONG_MARKETDATA_TYPE, INSTRUMENT_MISMATCH).

### Earlier Bootstrap (15:14)
- MNG: `BOOTSTRAP_FLATTEN_THEN_RECONSTRUCT` (old behavior before deploy).
- `FLATTEN_SKIPPED_ACCOUNT_FLAT` — account flat, no flatten order sent.

---

## 5. Break-Even (BE) Issues

### BE_FILTER_EXCLUDED_ACTIVE_EXPOSURE (CRITICAL)
- M2K chart: account has 3 positions, but BE filter returned 0 intents.
- Suggests execution instrument mismatch (chart vs intent) or intent map not populated.

### BE_GATE_BLOCKED
- MGC: INSTRUMENT_MISMATCH — account position in M2K, chart is MGC.
- MNG: WRONG_MARKETDATA_TYPE.

### BE_TICK_STALE_WARNING
- M2K: tick_age_seconds ≈ 63.9 billion (likely uninitialized or bad timestamp).

---

## 6. Protective Audit
- M2K: `protective_missing_stop_count=3`, `protective_audit_failure_count=3`.
- Indicates positions without matching protective stops in the registry.

---

## 7. Session Close
- **SESSION_CLOSE_HOLIDAY** (St. Patrick’s Day): S1, S2 closed per TradingHours.
- Markets closed early; some behavior may be session-close related.

---

## 8. Push Notifications

### Recent Successes (2026-03-17)
- RECONCILIATION_QTY_MISMATCH — sent 15:47.
- DISCONNECT_FAIL_CLOSED_ENTERED — sent 16:03.

### Historical Failures
- **Priority 2**: Missing `expire` and `retry` (Pushover API requirement).
- **Timeouts**: "Pushover send operation timed out after 10 seconds".
- **TaskCanceledException**: "A task was canceled" during CONNECTION_LOST sends.
- **DISCONNECT_FAIL_CLOSED**: Many failures, especially overnight.

---

## 9. Error Log Summary

### error_log.jsonl
- Old entries (Dec 2025): Task Scheduler access denied, pipeline lock, master matrix excluded times, orchestrator state transitions.

### notification_errors.log
- Mix of INFO (heartbeats, successes) and ERROR (Pushover failures).
- Recent tail: mostly INFO; last ERROR entries from earlier dates.

### Robot Logs
- ORDER_REJECTED: margin excess, outside market hours.
- INSTRUMENT_RESOLUTION_FAILED: MNG, M2K (fallback used).
- EXECUTION_UPDATE_UNKNOWN_ORDER: order not in tracking map.
- MANUAL_OR_EXTERNAL_ORDER_DETECTED: order not in registry → fail-closed flatten.

---

## 10. Recommendations

### Immediate
1. **Bootstrap timing**: Consider delaying bootstrap until broker order state is available, or run adoption when first seeing broker orders not in registry.
2. **Adoption scope**: Confirm whether `ScanAndAdopt` (or equivalent) should adopt entry stops, not only protectives.
3. **Bootstrap logging**: Emit `BOOTSTRAP_SNAPSHOT_CAPTURED` (including `slot_journal_shows_entry_stops_expected`) to a log file that is easy to search.
4. **Unblock instruments**: Resolve ORDER_REGISTRY_MISSING (e.g., flatten and re-enable, or manual reconciliation) so trading can resume.

### Push Notifications
- Ensure `expire` and `retry` are set for Pushover priority 2.
- Consider retries and backoff for timeouts.

### BE / Protective
- Investigate BE_FILTER_EXCLUDED_ACTIVE_EXPOSURE and intent map population.
- Fix BE_TICK_STALE_WARNING (tick timestamp initialization).
- Resolve protective audit failures (missing stops).

---

## 11. Deploy Status

- Deploy completed during this session.
- NinjaTrader cache cleared (NinjaTrader.Custom.dll, NinjaTrader.Vendor.dll).
- 20:34 logs may be from **before** the deploy; next restart will use the new DLL.
- To validate the fix: restart NinjaTrader, enable strategies, and check for:
  - `BOOTSTRAP_SNAPSHOT_CAPTURED` with `slot_journal_shows_entry_stops_expected=true`
  - ADOPT decision
  - `REGISTRY_BROKER_DIVERGENCE_ADOPTED` or `iea_working` matching `broker_working` after adoption.
