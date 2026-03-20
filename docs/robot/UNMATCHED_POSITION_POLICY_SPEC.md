# RECOVERY_POSITION_UNMATCHED — Deterministic Policy Specification

## Behavior

```
IF ownership_proven AND adoption_supported → adopt
ELSE → flatten
```

- **ownership_proven**: Policy evaluator returns `OWNERSHIP_PROVEN` (all hard gates + continuity pass).
- **adoption_supported**: Execution path has adoption infrastructure (IEA). Legacy path: `adoption_supported = false`.

## 1. Unmatched Detection (Evidence-Based)

A broker position is **unmatched** when it cannot be proven to belong to robot-owned state through recovery evidence.

**A position is MATCHED only if ALL of the following hold:**
- Canonical instrument equivalence: broker instrument matches journal/registry instrument (MES↔ES, MNQ↔NQ, M2K↔RTY, etc.)
- Journal evidence exists: at least one adoption candidate (EntrySubmitted, !TradeCompleted) for that instrument
- Exactly one candidate: no ambiguity
- Exact quantity match: broker qty == journal open qty
- Session/trading-date validity: candidate belongs to current trading date
- At least one continuity evidence: (A) QTSW2 tag/intent linkage, (B) broker working order for candidate, or (C) strong journal continuity

**A position is UNMATCHED if any of the above fails.**

The policy evaluator runs on every non-flat position. No pre-filter by stream instrument string alone.

## 2. Decision Rules (Exact Order)

### Hard Gates — ALL required
1. **Instrument non-empty** — reject: INSTRUMENT_EMPTY
2. **Canonical instrument match** — enforced via candidate selection; no candidates → NO_CANDIDATE
3. **Journal evidence exists** — EntrySubmitted, !TradeCompleted; reject: NO_JOURNAL_ENTRY, JOURNAL_STATE_INVALID
4. **Exactly one candidate** — reject: NO_CANDIDATE (0), MULTIPLE_CANDIDATES (>1)
5. **Exact quantity match** — broker qty == journal open qty; reject: QTY_MISMATCH
6. **Session/trading-date validity** — candidate.TradingDate == current; reject: TRADING_DATE_MISMATCH

### Continuity Layer — at least ONE required
- **A.** QTSW2 tag / intent linkage: working order with QTSW2 tag decoding to candidate intentId
- **B.** Broker working order linkage: same as A (working order for candidate)
- **C.** Strong journal continuity: EntryFilled, journalOpenQty==brokerQty, !TradeCompleted

If any hard gate fails OR continuity layer fails → **FLATTEN**

## 3. ADOPT Path

**Legacy path (no IEA):** Adoption infrastructure does not exist. When policy would ADOPT, we **FLATTEN** with reason `ADOPTION_PATH_UNAVAILABLE`. No false adoption.

**IEA path:** Recovery uses IEA.BeginReconnectRecovery; this policy does not apply (IEA has its own bootstrap).

## 4. FLATTEN Path

- Method: `IExecutionAdapter.FlattenEmergency(instrument, utcNow)`
- SIM: NinjaTraderSimAdapter → FlattenIntentReal / IEA.RequestFlatten
- LIVE: NinjaTraderLiveAdapter → Account.Flatten (real NT API)
- DRYRUN: NullExecutionAdapter → log and return success

## 5. Events

All events include: instrument, broker_qty, candidate_count, candidate_intent_id (when exactly one), reason, run_id.
