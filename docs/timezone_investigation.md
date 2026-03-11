# Timezone Investigation — NQ Feb 4, 2025

## Summary

**No timezone bug found.** Data, range logic, and entry logic all use America/Chicago consistently. The "NoTrade" vs expected breakout is due to the **range window definition** (02:00–09:00 vs 08:00–09:00), not timezone handling.

---

## 1. Translator

| Step | Implementation |
|------|-----------------|
| Input | Raw CSV `timestamp_utc` (UTC, e.g. `2026-03-09T00:00:00Z`) |
| Conversion | `pd.to_datetime(..., utc=True)` → `ts_utc.dt.tz_convert("America/Chicago")` |
| Output | `timestamp` column tz-aware America/Chicago |

**Verified:** 00:00 UTC → 19:00 CT previous day (correct for DST).

---

## 2. Translated Data (NQ Feb 4)

```
First: 2025-02-03 18:00:00-06:00 (America/Chicago)
Last:  2025-02-04 17:59:00-06:00 (America/Chicago)
```

CME session 18:00 Feb 3 – 17:59 Feb 4 CT is correctly represented.

---

## 3. Range Logic

| Component | Timezone |
|-----------|----------|
| `chicago_tz` | `pytz.timezone("America/Chicago")` |
| `start_ts` / `end_ts` | `pd.Timestamp("2025-02-04 02:00:00", tz=chicago_tz)` |
| Filter | `timestamp >= start_ts` and `timestamp < end_ts` |

Data and filter use the same timezone; no conversion mismatch.

---

## 4. Date Extraction (build_slot_ranges)

The analyzer uses `date_ct` for iteration:

- `hour >= 23` → assign to next calendar day  
- Otherwise → current calendar date  

This differs from CME trading date (18:00 boundary):

| Bar time | date_ct | CME trading date |
|----------|---------|-------------------|
| 18:00 Feb 3 | 2025-02-03 | 2025-02-04 |
| 20:48 Feb 3 | 2025-02-03 | 2025-02-04 |
| 02:00 Feb 4 | 2025-02-04 | 2025-02-04 |

For Feb 4 09:00, the range is built with `date_str = "2025-02-04"`, so the range window 02:00–09:00 Feb 4 is correct. The `date_ct` vs CME mismatch affects which dates are iterated, not the range calculation for Feb 4.

---

## 5. Entry Logic

- `end_ts` and `market_close` use the same timezone as the data.
- `post = df[df_timestamps >= end_ts]` includes the 09:00 bar and later.

---

## 6. Root Cause of NoTrade

| Range window | High | Low | Breakout at 09:00? |
|--------------|------|-----|--------------------|
| **02:00–09:00** (analyzer) | 22,606 | 22,317 | No |
| **08:00–09:00** (last hour) | 22,597 | 22,537 | Yes |

S1 uses a single `slot_start = 02:00` for all slots, so the 09:00 slot uses a 7‑hour range instead of a shorter one. That’s a range design choice, not a timezone issue.

---

## 7. Recommendations

1. **Timezone:** No changes needed; handling is consistent.
2. **Range design:** If 09:00 should use a shorter range (e.g. 08:00–09:00), the range logic would need to support per-slot or per-session ranges.
3. **Date iteration:** Optional: use `get_trading_date_cme_series()` instead of `date_ct` for CME-aligned date iteration.
