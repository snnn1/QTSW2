# IEA Flatten Authority: Thread and Serialization Assumptions

## Overview

The IEA Flatten Authority derives flatten side and quantity from **account position at decision time**. Position query, flatten decision, and order submission must behave as one critical section.

## Thread Assumptions

- **Strategy thread**: All NinjaTrader Account/Order APIs (GetPosition, CreateOrder, Submit) must run on the strategy thread (OnBarUpdate/OnMarketData context).
- **Position reads**: `GetAccountPositionForInstrument` MUST be called from the strategy thread. NinjaTrader `Account.Positions` and `GetPosition` are not guaranteed thread-safe; reads from worker or other threads may be stale or inconsistent.
- **Order submission**: `SubmitFlattenOrder` MUST run on the strategy thread.

## Serialization (Critical Section)

Position query + flatten decision + order submission run as one atomic sequence:

1. **Option A (IEA path)**: `RequestFlatten` is called from `ExecuteFlattenInstrument`, which runs on strategy thread when the NT action queue is drained. The full sequence (get position, validate, submit) runs in one call on the strategy thread.
2. **Option B (adapter path)**: `FlattenIntentReal` uses `EnsureStrategyThreadOrEnqueue`; when on worker, it enqueues a lambda. The lambda runs on strategy thread and performs the full sequence.

**Implementation**: Both paths ensure the sequence runs on strategy thread. No intervening fills can change position between read and submit because the sequence is synchronous on that thread.

## Freshness

- NinjaTrader `Account.GetPosition(instrument)` reflects the account's current position. Fills that arrived in the same tick/bar should be reflected; there is no known explicit lag.
- **Verification**: If position appears stale in production, add diagnostic logging (position at read vs. fill timestamp) and re-evaluate.

## Known Limitations

- When `Flatten()` is called from the IEA worker and IEA is enabled, it enqueues `NtFlattenInstrumentCommand` and returns "Enqueued for strategy thread". The actual flatten runs when the strategy thread drains the queue. The caller does not wait for the result.
