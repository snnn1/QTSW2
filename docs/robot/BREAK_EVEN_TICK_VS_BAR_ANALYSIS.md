# Break-Even Detection: Tick-Based vs Bar-Based — Analysis & Recommendation

**Date:** 2026-02-13  
**Scope:** RobotCore_For_NinjaTrader

---

## Executive Summary

| Aspect | Current (Bar-Based) | Proposed (Tick-Based) |
|--------|---------------------|------------------------|
| **Price source** | Bar close (Closes[1][0] or Closes[0][0]) | Live Last tick via OnMarketData |
| **Check frequency** | Every 5 seconds (throttled) | Every Last tick (throttled internally) |
| **Worst-case delay** | 5 s + bar period (1 s–5 min) | ~200 ms (modify throttle) |
| **Missed triggers** | Possible if price crosses & reverses within bar | Minimal; tick-level visibility |
| **Recommendation** | — | **Yes, move to tick-based** — benefits outweigh risks |

---

## 1. Current State: Bar-Based Detection

### How It Works Today

```
OnBarUpdate (BIP 1 or BIP 0) — every 5 seconds when in position
    ↓
RunBreakEvenCheck() → currentPrice = Closes[priceBarsIndex][0]
    ↓
CheckBreakEvenTriggersTickBased(currentPrice, utcNow)
```

**Price source:** The last **close** of the current bar:
- **BIP 1 (1-second bars):** Last close of current 1-second bar
- **BIP 0 fallback:** Last close of primary bar (e.g. 5-min)

**Throttle:** BE check runs at most every **5 seconds** to reduce chart lag across multiple strategies.

### Limitations

| Limitation | Impact |
|------------|--------|
| **Bar close only** | If price crosses BE trigger and reverses within the bar, the trigger can be missed until the next bar close. |
| **5-second throttle** | Even with 1-second bars, we only *check* every 5 seconds. Price could cross and reverse in that window. |
| **BIP 1 not always available** | MCL, M2K, etc. may not have 1-second bars → fallback to 5-min bars → up to 5+ minutes delay. |
| **Misleading name** | `CheckBreakEvenTriggersTickBased` suggests tick-based logic but uses bar close. |

### When Missed Triggers Matter

**Scenario:** Long at 100, BE trigger 102.60, target 104.
- Price goes 102.70 → 102.40 within 1 second (fast reversal).
- Bar-based: If the 1-second bar closes at 102.35, we never see 102.70. BE not triggered.
- Result: Stop stays at original level; a later drop could lock in a loss that BE would have avoided.

**Frequency:** Depends on volatility. In fast markets (e.g. NQ, ES around news), this can happen multiple times per day. In slower instruments (e.g. GC), less often.

---

## 2. Proposed: Tick-Based Detection via OnMarketData

### How It Would Work

```csharp
protected override void OnMarketData(MarketDataEventArgs e)
{
    if (e.MarketDataType != MarketDataType.Last) return;
    if (State != State.Realtime || Position.MarketPosition == MarketPosition.Flat) return;
    if (_adapter == null || !_engineReady) return;

    var tickPrice = (decimal)e.Price;
    CheckBreakEvenTriggersTickBased(tickPrice, DateTimeOffset.UtcNow);
}
```

**Price source:** `e.Price` from `MarketDataType.Last` — actual last-trade price for each tick.

**Frequency:** NinjaTrader calls `OnMarketData` for every Level 1 change. For Last, that’s every trade. ES/MES can see hundreds of ticks per second in active periods.

### Throttling Still Required

Even with tick-based input, we should keep:

1. **BE_SCAN_THROTTLE_MS (200 ms):** Min interval between full intent scans (avoids hitting journal + locks on every tick).
2. **BE_MODIFY_ATTEMPT_INTERVAL_MS (200 ms):** Min interval between modify attempts per intent.
3. **Intent cache (200 ms):** Avoid refreshing `GetActiveIntentsForBEMonitoring` on every tick.

So the flow becomes:
- **Every tick:** Update `_lastTickPriceForBE` (O(1)).
- **Every 200 ms:** Run full scan using latched price; compare to BE triggers; call `ModifyStopToBreakEven` if needed.

This keeps tick-level price visibility while limiting expensive work.

---

## 3. Pros and Cons

### Tick-Based — Pros

| Benefit | Detail |
|---------|--------|
| **Faster detection** | Price is seen as soon as it trades; no wait for bar close. |
| **Fewer missed triggers** | Cross-and-reverse within a bar is visible. |
| **No BIP 1 dependency** | Works even when 1-second bars are missing or stale. |
| **Aligns with naming** | `CheckBreakEvenTriggersTickBased` actually uses tick data. |
| **Better for fast markets** | NQ, ES, etc. benefit most from immediate reaction. |

### Tick-Based — Cons

| Risk | Mitigation |
|------|------------|
| **Higher call frequency** | Throttle scan to 200 ms; keep existing modify throttle. |
| **UI thread load** | OnMarketData runs on UI thread; keep logic minimal (latch price + throttled scan). |
| **Multiple strategies** | Each strategy gets ticks for its instrument only; no change from current design. |
| **Historical/backtest** | OnMarketData may behave differently in historical; keep bar-based path for historical if needed. |

### Bar-Based — Why Keep as Fallback?

- **Historical mode:** Some setups may not have tick replay; bar-based can remain the fallback.
- **Defense in depth:** If OnMarketData is not called (e.g. data feed quirk), BIP 1/BIP 0 path still runs.

---

## 4. Recommendation

### Move to Tick-Based Detection

**Reasons:**
1. BE is a risk-management feature; missed triggers can turn winners into losers.
2. Throttling (200 ms) keeps cost low while improving responsiveness.
3. Removes dependency on 1-second bars for instruments that don’t have them.
4. Implementation is small: add `OnMarketData` override and route Last price into existing logic.

### Implementation Outline

1. **Add `OnMarketData` override** in `RobotSimStrategy`:
   - Filter `MarketDataType.Last` only.
   - Gate on Realtime + in position.
   - Call `CheckBreakEvenTriggersTickBased((decimal)e.Price, DateTimeOffset.UtcNow)`.

2. **Keep bar-based path** for:
   - Historical mode (if OnMarketData is not reliable there), or
   - Fallback when `OnMarketData` has not fired for N seconds (e.g. 10 s).

3. **No changes** to:
   - `CheckBreakEvenTriggersTickBased` (already accepts price + time).
   - Throttling constants (200 ms scan, 200 ms modify).
   - `GetActiveIntentsForBEMonitoring` or adapter logic.

4. **Optional:** Add `BE_DETECTION_SOURCE` to logs: `"TICK"` vs `"BAR_FALLBACK"` for observability.

### Effort Estimate

- **Low:** ~30–60 minutes for OnMarketData + fallback logic.
- **Testing:** Verify BE triggers in SIM with 1-second bars enabled and disabled.

---

## 5. Alternative: Improve Bar-Based Without OnMarketData

If you prefer not to add OnMarketData:

| Change | Effect |
|--------|--------|
| Reduce BIP 1 throttle from 5 s to 1–2 s | More frequent checks; more UI load. |
| Add tick-based series | e.g. `AddDataSeries(BarsPeriodType.Tick, 1)` — gives tick bars; still bar close, but much finer. |
| Log when BIP 1 is stale | Improves diagnosis; no functional change. |

These help but do not match the reliability of true tick-based detection.

---

## 6. Conclusion

| Question | Answer |
|----------|--------|
| **Should we move to tick-based detection?** | **Yes.** |
| **Why?** | Fewer missed BE triggers, no 1-second bar dependency, small code change, throttling keeps cost low. |
| **Risk level?** | Low, with throttling and optional bar-based fallback. |
| **Effort?** | Low (~1 hour implementation + testing). |

The current bar-based design works but can miss BE triggers in volatile conditions. Tick-based detection via `OnMarketData(MarketDataType.Last)` with existing throttling is the recommended path.
