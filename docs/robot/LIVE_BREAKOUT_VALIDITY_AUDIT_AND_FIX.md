# Live Breakout Validity Enforcement — Audit and Fix

## 1. Why NinjaTraderLiveAdapter.GetCurrentMarketPrice Returns (null, null)

### Root cause

`NinjaTraderLiveAdapter` is a **stub implementation** for Phase C (live brokerage integration). All methods are placeholders that log and return failure or empty results. `GetCurrentMarketPrice` is a one-liner:

```csharp
public (decimal? Bid, decimal? Ask) GetCurrentMarketPrice(string instrument, DateTimeOffset utcNow)
    => (null, null);  // LIVE: gate skips (fail open) - implement when market data available
```

**Exact locations:**
- `modules/robot/core/Execution/NinjaTraderLiveAdapter.cs` line 220–221
- `RobotCore_For_NinjaTrader/Execution/NinjaTraderLiveAdapter.cs` line 237–240
- `NT_ADDONS/Execution/NinjaTraderLiveAdapter.cs` line 217–220

### Why it was never implemented

1. LIVE mode is not enabled: `ExecutionAdapterFactory.Create` throws for `ExecutionMode.LIVE`.
2. The adapter has no NinjaTrader context: it never receives `Account` or `Instrument` from the Strategy.
3. The Sim adapter uses `SetNTContext(account, instrument)` to get `_ntInstrument`; the Live adapter has no equivalent.

### Effect on breakout validity gate

`IsBreakoutValidForResubmit` uses `GetCurrentMarketPrice`. When it returns `(null, null)`:

- `longInvalid = ask.HasValue && ...` → false (ask is null)
- `shortInvalid = bid.HasValue && ...` → false (bid is null)
- Gate returns **true** (fail-open) → delayed resubmit/restart retry proceeds without price validation.

---

## 2. Correct Live Source for Current Market Price in NinjaTrader

### NinjaTrader API

For a NinjaTrader strategy, the instrument is `Instrument` (or `Bars.Instrument`). Market data is exposed via:

- `Instrument.MarketData` — level‑1 market data
- `MarketData.GetBid(int barsAgo)` / `MarketData.GetAsk(int barsAgo)` — best bid/ask
- For current quote: `GetBid(0)`, `GetAsk(0)`
- Fallback: `MarketData.Bid`, `MarketData.Ask` (properties)

### Architecture alignment

The **NinjaTraderSimAdapter** already uses this pattern in `GetCurrentMarketPriceReal`:

```csharp
// NinjaTraderSimAdapter.NT.cs
var marketData = dynInstrument.MarketData;
bid = (double?)marketData.GetBid(0);
ask = (double?)marketData.GetAsk(0);
// Fallback: marketData.Bid, marketData.Ask
```

The Live adapter runs in the same NinjaTrader process and has access to the same `Instrument` when the Strategy wires context. The correct source is therefore **`Instrument.MarketData.GetBid(0)` / `GetAsk(0)`**, identical to the Sim adapter.

---

## 3. Submission Path Classification

| Path | Description | Gate | Time bound |
|------|-------------|------|------------|
| **Normal immediate slot-time** | First submission at slot time, within freshness window | None (marketable stop allowed) | `utcNow - SlotTimeUtc` ≤ freshness window |
| **Delayed initial submission** | First submission after slot time, beyond freshness window | Block (NO_TRADE_MATERIALLY_DELAYED) | `utcNow - SlotTimeUtc` > freshness window |
| **Delayed resubmit** | Recovery resubmit (missing/broken orders) | `IsBreakoutValidForResubmit` | MarketCloseUtc |
| **Restart retry** | Restart after failed placement | `IsBreakoutValidForResubmit` | MarketCloseUtc |

---

## 4. Initial Submission Freshness Window (Recommendation)

**Recommendation:** `INITIAL_SUBMISSION_FRESHNESS_WINDOW_MINUTES = 3`

- **Rationale:** Slot-time submissions within ~3 minutes are treated as “at signal time”; marketable fills are acceptable.
- **Beyond 3 minutes:** Treat as materially delayed; block initial submission and commit `NO_TRADE_MATERIALLY_DELAYED_INITIAL_SUBMISSION`.
- **Configurable:** Add to spec (e.g. `breakout.initial_submission_freshness_minutes`) with default 3.

---

## 5. Implementation Summary (Completed)

1. **NinjaTraderLiveAdapter:** Added `_ntInstrument`, `SetNTContext(account, instrument)`, and `GetCurrentMarketPrice` using `Instrument.MarketData.GetBid(0)`/`GetAsk(0)` via dynamic. Uses `ToDoubleOrNull` for robust conversion from dynamic.
2. **Strategy:** `WireNTContextToAdapter` now supports both `NinjaTraderSimAdapter` and `NinjaTraderLiveAdapter`; calls `SetNTContext` on Live adapter when that is the adapter type.
3. **StreamStateMachine:** Added freshness window check in `TryLockRange` before initial `SubmitStopEntryBracketsAtLock`. Configurable via `breakout.initial_submission_freshness_minutes` (default 3).
4. **Tests:** `BreakoutValidityGateTests` includes `TestLiveAdapter_NoContext_ReturnsNull` and `TestLiveAdapter_WithMockInstrument_ReturnsBidAsk` (anonymous-type mock with Func delegates).
