# M2K / RTY1 Breakout Timing Investigation

**Issue**: M2K had orders placed when the breakout had already happened for RTY1.

## Key Finding: Lock Delay

From `ranges_2026-03-10.jsonl`:

| Metric | Value |
|--------|-------|
| RTY1 slot (range end) | 09:00 CT |
| Range locked at | **13:22 CT** |
| **Lock delay** | **~4 hours 22 minutes after slot** |
| Breakout levels | Long: 2569.1, Short: 2531.0 |
| Freeze close | 2555.6 |

**Conclusion**: Orders were placed at 13:22 CT when the breakout would have occurred at or shortly after 09:00 CT. The breakout had already happened hours before we placed M2K stop orders.

---

## Root Cause Hypotheses

### 1. Late NinjaTrader / Robot Start (Most Likely)
- If NinjaTrader was started at ~13:22 CT, the robot had no bars until then.
- When bars finally arrived (via BarsRequest + live feed), the robot locked and placed orders.
- By then, the 09:00 breakout had already occurred.

### 2. M2K Bar Data Delay
- M2K (micro Russell 2000) has lower liquidity than RTY.
- Bar data might arrive later than RTY or other instruments.
- If M2K bars were delayed, lock would be delayed.

### 3. BarsRequest / Pre-Hydration Delay
- The robot requests historical bars before locking.
- If BarsRequest for M2K is slow or fails initially, lock is delayed until bars are available.

### 4. Multiple Strategy Instances
- RTY1 stream receives bars from both M2K and RTY (both canonicalize to RTY).
- If only RTY chart was running initially, M2K orders wouldn't be placed until M2K chart was added.

---

## Recommendations

### 1. Block Late Locks (Already Implemented)
The code already has `LATE_START_MISSED_BREAKOUT` - when starting after slot time, if breakout already occurred, the stream commits with NO_TRADE. This may not apply when:
- Bars arrive late (not "late start" per se)
- The robot was running but M2K bars were delayed

### 2. Add Lock-Delay Guard
Consider adding a guard: if `locked_at - slot_time > N minutes` (e.g. 15 min), either:
- **Option A**: Block order submission (NO_TRADE_LATE_LOCK)
- **Option B**: Log warning and allow (current behavior)

### 3. Ensure M2K Chart Running
- RTY streams use M2K as execution instrument.
- Ensure M2K chart is running and started before RTY1 slot (09:00 CT).
- If NinjaTrader starts late, expect late locks.

### 4. Investigate BarsRequest Timing
- Check if M2K BarsRequest completes before 09:00.
- Log `BarsRequest` completion time per instrument.
- If M2K is consistently slow, consider pre-warming or different data source.

---

## Investigation Script

Run:
```bash
python investigate_m2k_rty1_breakout_timing.py
```

This shows:
- RTY1 RANGE_LOCKED timing from ranges file
- Lock delay (minutes after slot)
- Timeline of key events (orders, fills, etc.)

---

## Related Docs

- `RTY_EXTRA_ORDER_FIX.md` - Duplicate order prevention
- `RTY_0930_EXTRA_ORDER_ANALYSIS.md` - 09:30 extra order analysis
- `ROBOT_BAR_INGESTION_ROUTING_SUMMARY.md` - Bar routing (M2K vs RTY)
- `STOP_ORDER_PLACEMENT_ASSESSMENT.md` - Stop order placement logic
