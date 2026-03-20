# Operations Rule: No Resequence During Trading Hours

**Date:** 2026-03-19  
**Status:** Immediate — enforce before any further code work  
**Rationale:** Disconnects cluster around matrix resequence; Episode 5 (10:36 AM) showed disconnect during 37s resequence

---

## Rule

**No resequence during the user's live trading session.**

Operationally defined:

1. **No `rolling_resequence`** during trading hours (e.g. 9:30–16:00 ET / 8:30–15:00 CT)
2. **No heavy matrix rebuilds** while NinjaTrader is actively trading
3. **If needed:** Allow only lightweight read-only operations (e.g. matrix_load for display) during market hours

---

## How to Enforce

| Mechanism | Action |
|-----------|--------|
| **Scheduler / cron** | If resequence is scheduled, move to off-hours (e.g. 4:00–8:00 CT or after 16:00 ET) |
| **Manual runs** | Do not run `run_matrix_and_timetable.py` with resequence during trading |
| **Dashboard / API** | If matrix API triggers resequence, add time-of-day guard: reject or defer during 8:30–15:00 CT |
| **Documentation** | Add to runbook: "Resequence only outside 8:30–15:00 CT" |

---

## Trading Hours (Reference)

- **Central:** 8:30–15:00 CT (regular session)
- **Eastern:** 9:30–16:00 ET
- **Conservative:** No resequence 7:00–17:00 CT (covers pre/post market)

---

## Exceptions

- **Emergency fix:** If timetable is corrupt and must be regenerated during market hours, accept the risk and document
- **Lightweight reads:** `matrix_load` (parquet read) is lower risk; resequence (parquet write + timetable write) is the high-risk operation
