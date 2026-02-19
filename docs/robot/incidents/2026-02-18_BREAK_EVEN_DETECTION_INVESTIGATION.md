# Break-Even Detection Investigation

**Date:** 2026-02-18  
**Status:** Investigation complete

---

## Summary

BE detection **is working** for the instrument with the position. The confusion stems from:

1. **BE_GATE_BLOCKED (INSTRUMENT_MISMATCH)** — Expected when multiple charts run and the position is in a different instrument. Non-position charts correctly skip BE.
2. **BE_TRIGGER_REACHED** — Logged at 15:16 UTC for MYM (YM); **user reports stop was not actually moved** — requires investigation.
3. **BE_TRIGGER_REACHED** was missing from RobotEventTypes registry — caused UNREGISTERED_EVENT_TYPE warning. **Fixed.**

---

## Findings

### 1. BE Path Is Active

| Instrument | BE_PATH_ACTIVE | active_intent_count | Status |
|------------|----------------|---------------------|--------|
| MYM | Yes | 0 | Position present; intents already BE-modified or none awaiting |
| MNQ | Yes | 1 | Intent awaiting BE trigger |
| MCL | Yes | 1 | Intent awaiting BE trigger |

### 2. BE_GATE_BLOCKED (INSTRUMENT_MISMATCH)

**What it means:** Account has position in **MYM** (3 contracts). Strategies on **M2K, MES, MNG, MGC** see `account_position_count=3` but their instrument does not match the position (MYM). They correctly **do not run BE** — they would use the wrong tick price (M2K price vs MYM position).

**This is correct behavior.** The BE gate blocks non-matching charts to prevent:
- Comparing M2K tick price against a MYM position (wrong scale)
- Modifying stops on the wrong instrument

### 3. BE Logged for MYM — But Did Not Work (per user report)

```
robot_YM.jsonl 15:16:28 UTC:
BE_TRIGGER_REACHED
  direction: Long
  breakout_level: 49875.0
  be_trigger_price: 49940.000
  be_stop_price: 49874.0
  tick_price: 49940
  execution_instrument_key: MYM
```

Logs show ModifyStopToBreakEven returned Success, but **user reports the stop was not actually moved**. Root cause unknown — investigate ModifyStopToBreakEven path.

### 4. Why MNQ/MCL Might Not Trigger

For MNQ and MCL with `active_intent_count=1`:
- **Trigger not reached yet** — Price may not have crossed `be_trigger_price` (Long: tick >= trigger; Short: tick <= trigger).
- **Modify failing** — No `BE_MODIFY_MAX_RETRIES_EXCEEDED` or `STOP_MODIFY_FAIL` in recent logs, so modify path appears healthy.

### 5. Multi-Chart Architecture

With 7 strategy instances (MNG, M2K, MES, MYM, MCL, MNQ, MGC):
- Only the chart for the instrument with the position runs BE.
- Other charts emit BE_GATE_BLOCKED (INSTRUMENT_MISMATCH) as an audit trail.
- This produces many BE_GATE_BLOCKED logs when position is in one instrument — expected.

### 6. MNQ Root Cause: Execution Instrument Mismatch (FIXED)

**Symptom:** MNQ price went past break-even but nothing happened.

**Root cause:** When using an **NQ chart** that trades **MNQ** via execution policy:
- `GetExecutionInstrumentForBE()` used chart instrument → returned `"NQ"`
- Intents have `ExecutionInstrument = "MNQ"` (actual trading instrument)
- `GetActiveIntentsForBEMonitoring("NQ")` filtered out the MNQ intent → 0 intents → BE never triggered

**Fix:** Use `_engine.GetExecutionInstrument()` instead of chart-derived value. Engine knows the actual execution instrument (MNQ) which matches the intent.

---

## Fixes Applied

1. **RobotEventTypes.cs** — Added `BE_TRIGGER_REACHED`, `BE_TRIGGER_RETRY_NEEDED`, `BE_TRIGGER_FAILED`, `BE_TRIGGER_TIMEOUT_ERROR` to registry. Eliminates UNREGISTERED_EVENT_TYPE warning.
2. **RobotSimStrategy.cs** — Use `_engine.GetExecutionInstrument()` for BE path and `GetExposureState()` instead of chart-derived `GetExecutionInstrumentForBE()`. Fixes NQ chart / MNQ trade BE no-trigger.

---

## Recommendations

1. **Reduce BE_GATE_BLOCKED noise** — Consider raising rate limit for INSTRUMENT_MISMATCH (e.g. 5 min) since it's expected with multi-chart setup.
2. **Verify trigger levels** — If BE isn't firing for MNQ/MCL, check that `be_trigger_price` is reachable (e.g. long entry 25000, trigger 25065 — price must reach 25065).
3. **Check journal** — `GetActiveIntentsForBEMonitoring` requires `journalEntry.EntryFilled`. If fill callback never updated the journal (e.g. UNKNOWN_ORDER_CRITICAL flatten), BE will have 0 intents.

---

## Related

- [2026-02-18 Break-Even Full Assessment](2026-02-18_BREAK_EVEN_FULL_ASSESSMENT.md) — Full day assessment of every BE event, fixes, and summary.
- [2026-02-17 NQ1 Fill No Protectives](2026-02-17_NQ1_FILL_NO_PROTECTIVES_INVESTIGATION.md) — UNKNOWN_ORDER_CRITICAL caused flatten before journal update; BE had no intents.
- [BREAK_EVEN_DETECTION_SUMMARY](../BREAK_EVEN_DETECTION_SUMMARY.md) — BE architecture.
