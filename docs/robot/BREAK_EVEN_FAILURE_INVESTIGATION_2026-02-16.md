# Break-Even Failure Investigation — 2026-02-16

**Scope:** Why break-even did not trigger today despite multiple streams hitting BE levels  
**Root cause:** BE check path never executed (no BE_PATH_ACTIVE or BE_EVALUATION_TICK in logs)

---

## 1. Today's Filled Streams (Execution Journals)

| Stream | IntentId | Direction | Entry | Target | BE Trigger | BEModified | TradeCompleted |
|--------|----------|-----------|-------|--------|------------|------------|----------------|
| NQ2 | 6abbfe253d2bd9bf | Short | 24692 | 24642 | **24659.5** | false | false |
| CL2 | 07a29a53ba4967fe | Long | 63.59 | 64.09 | **63.915** | false | false |
| YM1 | 46bcf96ec5f41ff7 | Short | 49622 | 49522 | **49557** | false | false |
| RTY2 | 5d525a2ad080624a | Short | 2647.4 | 2637.4 | **2640.9** | false | false |
| NG2 | 21ca26873f3df645 | Short | 3.023 | 2.973 | 2.9905 | false | true (stop hit) |

All journals show `BEModified: false` and `BETriggerPrice: null` (journal doesn't persist BeTrigger until BE is modified).

---

## 2. Intent Registration — BeTrigger Set Correctly

INTENT_REGISTERED events in robot logs show `has_be_trigger: True` for all streams:

- **NQ2 Short:** be_trigger 24659.5
- **CL2 Long:** be_trigger 63.915
- **YM1 Short:** be_trigger 49557
- **RTY2 Short:** be_trigger 2640.9
- **NG2 Short:** be_trigger 2.9905

Intent registration and BeTrigger values are correct.

---

## 3. Root Cause: BE Check Path Never Ran

**Finding:** No `BE_PATH_ACTIVE` or `BE_EVALUATION_TICK` events in any robot logs for 2026-02-16.

These events are emitted only when:
1. `Position.MarketPosition != MarketPosition.Flat`
2. `State == State.Realtime`
3. Either **BIP 1 (1-second bars)** fires, OR **BIP 0 fallback** runs

### 3.1 BIP 1 (1-Second Series) Path

BE runs when `BarsInProgress == 1` (1-second bar update). If 1-second bars do not fire for an instrument, this path never executes.

### 3.2 BIP 0 Fallback Path — Bug

The fallback is intended for when 1-second bars are stale. The condition is:

```csharp
var runBeFromBip0 = inPositionRealtime && (
    BarsArray.Length < 2 ||
    (_lastBip1WorkUtc != DateTimeOffset.MinValue && (now - _lastBip1WorkUtc).TotalSeconds >= 10));
```

**Bug:** When 1-second bars **never** fire, `_lastBip1WorkUtc` stays `DateTimeOffset.MinValue`. The second clause is then false. With `AddDataSeries(Second, 1)`, `BarsArray.Length >= 2`, so the first clause is also false. **Result: the fallback never runs when BIP 1 has never fired.**

Instruments with unreliable 1-second bars (e.g. MCL, M2K, MNG) or data gaps never reach the BE check.

---

## 4. Why This Affects Multiple Instruments

- **MNQ (NQ2):** Liquid; 1-second bars should fire, but ENGINE_TICK_STALL shows 120s+ gaps. If 1-second bars share that cadence, BE would run only when bars arrive.
- **MCL (CL2), M2K (RTY2), MNG (NG2), MYM (YM1):** Lower volume; 1-second bars may not update reliably. If BIP 1 never fires, the fallback does not run.

---

## 5. Recommended Fix

Extend the BIP 0 fallback so it runs when BIP 1 has never fired:

```csharp
var runBeFromBip0 = inPositionRealtime && (
    BarsArray.Length < 2 ||
    _lastBip1WorkUtc == DateTimeOffset.MinValue ||  // BIP 1 never fired — use primary bar
    (_lastBip1WorkUtc != DateTimeOffset.MinValue && (DateTimeOffset.UtcNow - _lastBip1WorkUtc).TotalSeconds >= bip1StaleSeconds));
```

This ensures:
- When 1-second bars never update, BE runs from primary bar close (BIP 0).
- When 1-second bars are stale (>10s), BE still runs from BIP 0.
- When 1-second bars fire, BIP 1 path runs and returns before BIP 0.

---

## 6. Additional Hardening (From Audit)

1. **"Only tighten" guard** — Do not move stop to BE if the current stop is already tighter.
2. **BE_PATH_ACTIVE in LIVE_CRITICAL** — Add to watchdog feed for visibility when BE path is active.
3. **BETriggerPrice in journal** — Persist BeTrigger when intent is registered so journals are self-contained.

---

## 7. Verification After Fix

After deploying the fix:
1. Confirm `BE_PATH_ACTIVE` appears when in position (rate-limited to 1/min).
2. Confirm `BE_EVALUATION_TICK` appears when in position (rate-limited to 1/s).
3. Confirm `BE_TRIGGER_REACHED` when price crosses the BE level.
4. Confirm journal `BEModified: true` after a successful BE modification.
