# Intent origin trace: `cef6b767b7a307c1`

**Objective:** Where the id is created, how it flows into tags, whether recovery/static state can smear it across instruments, and why MES/MYM/MNQ stops carry an id whose journal is **MNG / NG2**.

**Constraint:** Read-only code + on-disk journal + logs. No fixes.

---

## Section 1 — Intent creation

### 1.1 Algorithm (authoritative)

`intent_id` is **not** a GUID. It is the **first 16 hex characters** of **SHA256** over a **canonical string**:

```104:124:RobotCore_For_NinjaTrader/Execution/ExecutionJournal.cs
    public static string ComputeIntentId(
        string tradingDate,
        string stream,
        string instrument,
        string session,
        string slotTimeChicago,
        string? direction,
        decimal? entryPrice,
        decimal? stopPrice,
        decimal? targetPrice,
        decimal? beTrigger)
    {
        var canonical = $"{tradingDate}|{stream}|{instrument}|{session}|{slotTimeChicago}|{direction ?? "NULL"}|{entryPrice?.ToString("F2") ?? "NULL"}|{stopPrice?.ToString("F2") ?? "NULL"}|{targetPrice?.ToString("F2") ?? "NULL"}|{beTrigger?.ToString("F2") ?? "NULL"}";
        // ... SHA256 ...
        return hexString.Substring(0, 16); // Use first 16 chars as ID
    }
```

`Intent.ComputeIntentId()` forwards these fields from the `Intent` object (`Intent.cs`).

**Not included in the hash:** `EntryTimeUtc`, `TriggerReason` (they exist on `Intent` but do **not** change the id).

### 1.2 First materialization for this id (disk evidence)

| Field | Value |
|-------|--------|
| **Journal file** | `data/execution_journals/2026-03-20_NG2_cef6b767b7a307c1.json` |
| **Stream** | **NG2** |
| **Instrument (journal)** | **MNG** |
| **EntrySubmittedAt** | `2026-03-20T15:30:06.9955725+00:00` |
| **EntryFilledAt** | `2026-03-20T18:41:05.7902060+00:00` |

**Interpretation:** The id **`cef6b767b7a307c1`** is the hash for **`TradingDate|NG2|MNG|…|prices…`** at bracket/intent construction — i.e. **natural gas micro, session/stream NG2**, not ES/YM/NQ micro.

### 1.3 Code path that *creates* the `Intent` and registers it

1. **`StreamStateMachine`** builds `Intent` with `TradingDate`, `Stream`, `Instrument`, `ExecutionInstrument`, `Session`, `SlotTimeChicago`, direction, prices, `SlotTimeUtc`, trigger reason (e.g. `ENTRY_STOP_BRACKET_LONG` / similar paths — see `ComputeIntentId` helpers around `StreamStateMachine.cs` ~2634–2650, 2590+).
2. **`NinjaTraderSimAdapter.RegisterIntent(intent)`** stores `IntentMap[intent.ComputeIntentId()] = intent` (~1659–1660 in `NinjaTraderSimAdapter.cs`).
3. **Journal** written on entry path with the same `intentId` in the filename pattern `{date}_{stream}_{intentId}.json` (`ExecutionJournal.GetJournalPath`).

**Creation context:** **Normal entry / bracket submission** on the **NG2 / MNG** stream (not recovery-generated id).

---

## Section 2 — Order assignment paths (tagging + submit)

### 2.1 Tag encoding (single source)

| Method | Produces |
|--------|----------|
| `RobotOrderIds.EncodeTag(intentId)` | `QTSW2:{intentId}` |
| `RobotOrderIds.EncodeStopTag(intentId)` | `QTSW2:{intentId}:STOP` |
| `RobotOrderIds.EncodeTargetTag(intentId)` | `QTSW2:{intentId}:TARGET` |

`DecodeIntentId` strips `QTSW2:` and takes the segment before the next `:` (AGG tags special-cased) — `RobotOrderIds.cs`.

**Intent id into tag:** Always the **string parameter** passed into `EncodeStopTag` / `EncodeTargetTag` / `EncodeTag` — no separate hidden store.

### 2.2 Protective stop submission (entry fill path)

After entry fill, protectives use the **`intentId`** from the filled position’s intent and **`intent.Instrument`** as the **logical** instrument parameter:

```1359:1366:RobotCore_For_NinjaTrader/Execution/NinjaTraderSimAdapter.cs
            stopResult = SubmitProtectiveStop(
                intentId,
                intent.Instrument,
                intent.Direction,
                intent.StopPrice.Value,
                totalFilledQuantity,
                protectiveOcoGroup,
                utcNow);
```

IEA path enqueues the same pattern (`intent.Instrument`) via `NtSubmitProtectivesCommand` — `InstrumentExecutionAuthority.NT.cs` ~509–518.

### 2.3 Physical NT instrument for the stop (critical)

`SubmitProtectiveStopReal` **always** uses the adapter’s **`_ntInstrument`** (strategy chart instance), **not** the `instrument` string, for `CreateOrder`:

```4359:4363:RobotCore_For_NinjaTrader/Execution/NinjaTraderSimAdapter.NT.cs
                order = account.CreateOrder(
                    ntInstrument,                           // Instrument
                    ...
```

So **a given adapter instance can only attach the order to its chart’s contract**. The **`instrument` parameter** is used for logging / validation elsewhere; the **tag** still uses the passed **`intentId`**:

```4137:4138:RobotCore_For_NinjaTrader/Execution/NinjaTraderSimAdapter.NT.cs
            var stopTag = RobotOrderIds.EncodeStopTag(intentId);
```

**Implication:** A **correct** fill-driven protective for the **MNG** journal intent runs on the **MNG** chart’s adapter → stop is an **MNG** order with `QTSW2:cef6b767b7a307c1:STOP`. It **cannot** become an **MES** order through this path without that adapter’s `_ntInstrument` being MES.

### 2.4 Corrective stop (ProtectiveCoverageCoordinator → engine)

`RobotEngine.TrySubmitCorrectiveStop` picks **`intentId` from the journal entry matched under `req.Instrument`**, then submits:

```5324:5355:RobotCore_For_NinjaTrader/RobotEngine.cs
        var intentId = match.IntentId;
        ...
        var result = _executionAdapter.SubmitProtectiveStop(intentId, req.Instrument, direction, stopPrice.Value, qty, null, utcNow);
```

Journal grouping uses **`entry.Instrument`** as key (`GetOpenJournalEntriesByInstrument` — `ExecutionJournal.cs` ~1486–1492). **MNG** open trade indexes under **`MNG`**, not **MES/MYM/MNQ**. So this path **does not** explain submitting **MNG’s** `intentId` with **`req.Instrument = MES`** unless `ExecutionInstrumentResolver.IsSameInstrument` incorrectly aliases **MNG** to **MES** in the fallback key search (it should not).

### 2.5 Per-instrument broker rows (logs vs code)

| broker_order_id (log) | Log instrument | If robot-submitted from this codebase | Intent source for tag |
|----------------------|----------------|--------------------------------------|------------------------|
| `408453625015` | MES | Would require **MES** strategy adapter calling `SubmitProtectiveStop` with `intentId=cef…` | Parameter only; **not** from static global — must be fill/intent pipeline or corrective match for **that** chart |
| `408453625012` | MYM | Same, **MYM** adapter | Same |
| `408453625017` | MNQ | Same, **MNQ** adapter | Same |
| **MNG** (journal entry order) | MNG | **MNG** adapter, fill for registered intent | `Intent` / `IntentMap` for NG2 trade |

**MNG** protective for this intent is the **only** path that aligns journal + chart instrument.

---

## Section 3 — Tagging mechanism (answers to prompt)

| Question | Answer |
|----------|--------|
| Where does `intent_id` come from when tagging? | The **caller-supplied string** to `EncodeStopTag` / `EncodeTargetTag` / `EncodeTag`. |
| Stream state? | **Indirectly:** `StreamStateMachine` builds `Intent` from `TradingDate`, `Stream`, `Instrument`, …; `ComputeIntentId()` derives the id **deterministically** from those fields. |
| Shared object? | **`IntentMap[intentId]`** and **`OrderMap`** (per adapter/IEA), not a process-wide static intent id. |
| Cached variable? | **No `lastIntentId` / `currentIntent` static** found in grep. `currentIntent` appears only as **local** `TryGetValue` results (`IntentMap.TryGetValue`). |

---

## Section 4 — Leakage analysis (cross-stream / static)

Searched for patterns like static `Intent`, `lastIntent`, `currentIntent`, `sharedExecution` — **no cross-strategy static intent id** storage found.

**Per design:**

- **IEA `IntentMap` / `OrderMap`:** Shared **per (account, execution instrument)** registry, keyed by **`intentId`** and **`broker_order_id`** — not global across unrelated instruments.
- **`ExecutionUpdateRouter`:** Registers by **`(accountName, executionInstrumentKey)`** — isolates callbacks per execution key.

**Real “leakage” observed in *behavior* (prior audit):** `ScanAndAdoptExistingOrders` iterates **`account.Orders`** without first filtering **`Order.Instrument` to `ExecutionInstrumentKey`**, so **every** IEA on the account **sees** every QTSW2-tagged order and may **log** UNOWNED for **foreign** instruments. That is **observability / policy** coupling, not proof that **MES** submitted **MNG’s** stop.

---

## Section 5 — Recovery (`RECOVERY_PHASE3`)

- **`RECOVERY_PHASE3`** in logs is the **`action` field** on `UNOWNED_LIVE_ORDER_DETECTED` — set when unowned is detected on restart scan (`InstrumentExecutionAuthority.NT.cs` ~765–769).
- **`RequestRecovery` / `RequestSupervisoryAction`** follow; they **react** to unowned state.
- **No code path reviewed** that **writes a new broker tag** or **reassigns** `intent_id` onto arbitrary instruments as part of “RECOVERY_PHASE3” **by itself**. Recovery may drive **flatten / cancel / reconstruct** elsewhere; it does **not** replace the **tag encoding** functions.

**Conclusion:** Recovery is **downstream signaling**, not the **origin** of `QTSW2:cef…:STOP` on MES/MYM/MNQ.

---

## Section 6 — Final classification (exactly ONE)

### **D. Other (must explain)**

**Primary statement:**  
Within the **documented** robot paths, **fill-driven protectives** tag **`intentId` from the `Intent` on that chart** and place orders on **`_ntInstrument`**. A **single** MNG/NG2 journal intent **does not** flow through that pipeline to create **three** stops on **MES, MYM, and MNQ** with the same tag **without** one of the following:

1. **Separate adapter instances** each calling `SubmitProtectiveStop("cef6b767b7a307c1", …)` on **their** chart (requires **that string** to enter each chart’s fill/protective or corrective pipeline — **inconsistent** with journal keyed to **MNG only** unless…),
2. **Truncated 16-hex id collision** (same prefix for a **different** canonical tuple on another stream) — **statistically unlikely** but **not disproven** here without full hash audit,
3. **Non-robot or manual / template / NT duplication** of tags onto multiple instruments,
4. **Bug not located in this pass** that passes a **foreign** `intentId` into `SubmitProtectiveStop` on non-MNG charts (no static leak found; would be **call-site** error).

**Therefore this is not cleanly A/B/C:**

- **Not A** (“created correctly but reused incorrectly on submit”) **only** — creation is correct for MNG; **reuse** on three other micros is **not** explained by the normal fill path alone.
- **Not B** (“incorrectly shared across streams” via **static** store) — **no** such static `intent_id` found.
- **Not C** (recovery **reassigns** ids) — recovery flags **RECOVERY_PHASE3** as **response**, not tag manufacture.

**D** captures: **cross-instrument tagged stops + MNG-only journal** = **identity/causality gap** between **deterministic id creation (NG2/MNG)** and **observed broker orders (MES/MYM/MNQ)** under **known** submission invariants (`CreateOrder(ntInstrument, …)`).

---

## Section 7 — Recommended next steps (investigation only)

1. **Hash audit:** Enumerate all journal filenames / canonical tuples; test for **another** open trade whose `ComputeIntentId` **collides** on first 16 hex with `cef6b767b7a307c1`.
2. **NT order audit:** In NinjaTrader, inspect **order creation time / strategy name / fromEntrySignal** for `408453625015`, `012`, `017` vs MNG chart orders.
3. **Log grep:** `ORDER_SUBMIT_ATTEMPT` / `ORDER_CREATED_STOPMARKET` with `intent_id=cef6b767b7a307c1` **per instrument** to see **which chart** actually submitted.

---

*End of trace.*
