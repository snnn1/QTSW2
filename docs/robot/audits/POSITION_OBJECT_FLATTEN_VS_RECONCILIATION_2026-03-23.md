# Forensic audit — Exact broker position object vs flatten (2026-03-23 incident)

**Mode:** Read-only. **No fixes.**  
**Question:** Why can `NT_ACTION_SUCCESS` follow flatten while reconciliation still reports `broker_qty > 0`?

**Primary log window:** `2026-03-23T01:30Z`–`01:41Z` (evidence cited from prior root-cause audit + code).

---

## Section 1 — Position reader map

| path_name | file | input | matching method | quantity source | normalization / scope |
|-----------|------|-------|-----------------|-----------------|------------------------|
| **ReconciliationRunner — account qty** | `ReconciliationRunner.cs` | `PositionSnapshot.Instrument` + `Quantity` from `GetAccountSnapshot` | Keys by `p.Instrument.Trim()` (no extra root split in loop) | **Account-level:** iterates **all** `account.Positions` via adapter snapshot; **sums** `Math.Abs(qty)` into `accountQtyByInstrument[inst]` when same key repeats | Snapshot builds `Instrument` from **`position.Instrument.MasterInstrument.Name`** (see row below). Exec variant **only** for journal: `execVariant = inst.StartsWith("M") ? inst : "M"+inst`. |
| **GetAccountSnapshotReal — positions** | `NinjaTraderSimAdapter.NT.cs` ~5531–5541 | NT `Account.Positions` | Every `position` with `Quantity != 0` | `PositionSnapshot.Instrument = position.Instrument.MasterInstrument.Name`; `Quantity = position.Quantity` | **Per position row**, **master** name (not full `"MNQ 06-26"` string in snapshot). **Account-wide** enumeration. |
| **GetAccountSnapshotReal — orders** | same ~5545–5560 | `account.Orders` | Working / Accepted | `WorkingOrderSnapshot.Instrument = order.Instrument.MasterInstrument.Name` | Account-wide. |
| **IIEA GetAccountPositionForInstrument** | `NinjaTraderSimAdapter.NT.cs` ~234–265 | Parameter `instrument` (**string**) | **`account.GetPosition(ntInstrument)`** using adapter field **`_ntInstrument`** (chart-bound `Instrument`). Fallback: `GetPosition(ntInstrument.MasterInstrument.Name)` | **Single** position: `position.Quantity` / `MarketPosition` | **Critical:** Primary path **does not** use the `instrument` argument to select which NT `Instrument` to query. It uses **`_ntInstrument`** for the strategy instance that owns this adapter. |
| **RequestFlatten — position read** | `InstrumentExecutionAuthority.Flatten.cs` ~93 | `instrumentKey` = trimmed string from command | Calls `Executor.GetAccountPositionForInstrument(instrumentKey)` → **still ends on `_ntInstrument`** on the executor adapter | Same as row above | IEA `ExecutionInstrumentKey` used for **policy** (`ToPolicyInput(ExecutionInstrumentKey, absQty, …)`), not for NT `GetPosition` selection. |
| **SubmitFlattenOrder** | `NinjaTraderSimAdapter.NT.cs` ~271–295 | `instrument` string for logging / tags | **`account.CreateOrder(ntInstrument, …)`** | Submits market order for `absQty` chosen from **GetPosition** path | Order is for **chart `_ntInstrument`**, not a dynamically resolved `Instrument` from `instrument` string. |
| **Flatten verify (pending)** | `NinjaTraderSimAdapter.NT.cs` ~1159–1218 | `instrumentKey` string (dictionary key) | `GetPosition(instrumentRef)` where `instrumentRef` was **`_ntInstrument` at register time** | Re-check **same chart instrument ref** | Verification is **not** “re-sum account by master name”; it re-queries **one** `Instrument` reference. |
| **Bootstrap / recovery snapshot (IEA)** | `NinjaTraderSimAdapter.NT.cs` ~531 | `instrument` argument to callback | `GetAccountPositionForInstrument(instrument)` | Same **chart `_ntInstrument`** semantics | Used in bootstrap path; same caveat. |
| **ExecutionInstrumentResolver.ResolveExecutionInstrumentKey** | `ExecutionInstrumentResolver.cs` | `(account, instrument object/string, engineExecutionInstrument)` | Prefers `engineExecutionInstrument` if non-empty; else parses `ToString()` first token | N/A (key string only) | Used for IEA registry / `RequestRecoveryForInstrument` routing — **not** the same as snapshot position grouping. |
| **Journal open qty** | `ExecutionJournal.cs` ~1531–1549 | `executionInstrument` + optional canonical | String match on journal bucket keys | Sum of open entry residuals | Separate from NT position APIs. |

**Plain English:**

- **Reconciliation “broker side”** = **sum of every NT position row** on the account, grouped by **`MasterInstrument.Name`** (as stored in `PositionSnapshot.Instrument`).
- **Flatten / IEA position read** = **`Account.GetPosition(this chart’s _ntInstrument)`** (plus weak fallbacks). The **`instrument_key` string on `NtFlattenInstrumentCommand`** is **not** used to pick the NT `Instrument` for the primary `GetPosition` / `CreateOrder` path.

---

## Section 2 — Flatten target map (incident window)

Evidence from `logs/robot/robot_ENGINE.jsonl` (timestamps UTC). **NT object** = not logged as a CLR reference; **inferred** from code in §1.

| timestamp | logical instrument (command / log) | execution instrument key (IEA / policy logs) | NT_ACTION `instrument_key` | contract / NT object (proven vs inferred) | correlation id |
|-----------|--------------------------------------|-----------------------------------------------|----------------------------|---------------------------------------------|------------------|
| 01:35:08–01:35:11 | **MES** | IEA `iea_instance_id` **1** (MES bootstrap chain) | `MES` | **Inferred:** flatten uses **MES chart adapter’s `_ntInstrument`** when that strategy drains the queue — code: `CreateOrder(ntInstrument,…)` | `BOOTSTRAP:MES:20260323013508326`, `RECOVERY:MES:20260323013510654` |
| 01:35:22–01:35:26 | **MNQ** | MNQ IEA **2** | `MNQ` | **Inferred:** MNQ-bound adapter’s `_ntInstrument` when MNQ strategy executes drain | `RECOVERY:MNQ:20260323013522767` |
| 01:35:37–01:35:50 | **MYM** | MYM IEA **3** | `MYM` | **Inferred:** MYM chart `_ntInstrument` | `BOOTSTRAP:MYM:20260323013537115`, `RECOVERY:MYM:20260323013538336` |
| 01:36:30–01:36:52 | **MNG** | MNG IEA **7** | `MNG` | **Inferred:** MNG chart `_ntInstrument` | `RECOVERY:MNG:20260323013630930` |

**What the engine “thought”:** Log `instrument_key` matches the string on `NtFlattenInstrumentCommand`.  
**What NT used for position/order (code-proven):** **`_ntInstrument`** on the executing adapter, **not** a fresh resolve of `instrument_key` to an `Instrument` (see `GetAccountPositionForInstrument` / `SubmitFlattenOrder`).

---

## Section 3 — Reconciliation target vs flatten target

| instrument | reconciliation target definition | flatten target definition | same object guaranteed? | gap explanation (code + evidence) |
|--------------|-----------------------------------|---------------------------|-------------------------|-------------------------------------|
| **MNQ** | Sum of **all** account positions whose `MasterInstrument.Name` normalizes to snapshot key **`MNQ`** (dictionary sum in `ReconciliationRunner`). | **Single** `Position` for **`GetPosition(_ntInstrument)`** on the **MNQ chart adapter**; market exit on **`ntInstrument`**. | **No** | **Proven structural gap:** reconciliation is **account-wide aggregate by master name**; flatten is **chart contract `Instrument`**. If multiple position rows contribute to the same master key, or contract-level `GetPosition` differs from enumeration semantics, counts diverge. **Observed:** `NT_ACTION_SUCCESS` for MNQ ~01:35:26Z but `RECONCILIATION_CONTEXT` still `broker_qty: 1` at 01:40Z — **consistent with** “submit succeeded, fill lag / other row still open / read path mismatch” — **not** proven which sub-case without NT position dump. |
| **MYM** | Same aggregation pattern for key **`MYM`**. | Same **chart `_ntInstrument`** semantics on **MYM** executor. | **No** | **Log evidence (proven):** At `2026-03-23T01:36:01.3998569Z`, `RECONCILIATION_CONTEXT` shows **`broker_qty: 2`** while a **MYM** attribution row the same second shows **`broker_position_qty: 0`**. That is **direct proof** of **inconsistent broker quantity** between **account snapshot path** and **`GetAccountPositionForInstrument` path** (or timing), **not** merely “stale log”. |
| **MNG** | Same aggregation for **`MNG`**. | **MNG** chart `_ntInstrument` path. | **No** | **Observed:** `NT_ACTION_SUCCESS` ~`01:36:52Z` but **`RECONCILIATION_CONTEXT`** still **`broker_qty: 2`** through ~`01:41Z`. **Disk journal** `2026-03-20_NG2_cef6b767b7a307c1.json` shows trade **completed** at `01:02Z` — journal `0` open is **consistent**; mismatch is **broker snapshot still > 0** vs **journal**. |

**Answers to explicit questions:**

- **Guaranteed same object?** **No** (code).
- **Reconciliation broader than flatten?** **Yes, possible:** reconciliation sums **all** position rows sharing the same snapshot **instrument string** (master name); flatten closes **at most** the position tied to **one** `Instrument` reference (`_ntInstrument`).
- **Micro/mini / month conflation?** **Possible:** reconciliation keys by **`MasterInstrument.Name`**; `GetPosition` is **`Instrument`-specific**. **Not proven** which contracts existed on the account without NT UI / export — **inference** supported by architecture.

---

## Section 4 — Instrument normalization differences

| rule | where | behavior |
|------|--------|----------|
| Snapshot position identity | `GetAccountSnapshotReal` | `Instrument = MasterInstrument.Name` (e.g. `MNQ`, `MYM`, `MNG`). |
| Reconciliation grouping | `ReconciliationRunner` | Sums quantities keyed by that string; matches journal with `execVariant` (`M`+root) for **journal only**, not for changing snapshot keys. |
| Flatten position read | `GetAccountPositionForInstrument` | **Ignores** string key for primary lookup; uses **`_ntInstrument`**. |
| Flatten order | `SubmitFlattenOrder` | **`CreateOrder(ntInstrument, …)`**. |
| Resolver `IsSameInstrument` | `ExecutionInstrumentResolver` | Maps micro↔mini **for matching only**; comments state **must not** merge IEA registry keys — **separate** from snapshot summing. |
| Journal | `GetOpenJournalQuantitySumForInstrument` | Matches journal file bucket keys to execution or canonical variant strings. |

**Differences that can explain “flatten success, reconciliation still > 0”:**

1. **Account snapshot sum by master name** vs **GetPosition(chart contract)**.  
2. **Multiple strategies / times**: snapshot at **T1** vs `GetPosition` at **T2**.  
3. **`NT_ACTION_SUCCESS`** does **not** assert post-fill flat (§5).

---

## Section 5 — Meaning of `NT_ACTION_SUCCESS`

| event | actual semantic (code) | what it does **NOT** guarantee |
|-------|---------------------------|--------------------------------|
| **`NT_ACTION_SUCCESS`** | `StrategyThreadExecutor.DrainNtActions`: emitted when `action.Execute(executor)` **returns without throwing** (`StrategyThreadExecutor.cs` ~318–326). | Does **not** mean broker is flat. Does **not** mean flatten **fill** completed. Does **not** mean reconciliation will see `broker_qty == 0` on the **next** `GetAccountSnapshot`. |
| **`ExecuteFlattenInstrument`** | After `RequestFlatten`, logs `FLATTEN_SUBMITTED` with `result.Success`; **throws** if `!result.Success` (~1051–1052). | So `NT_ACTION_SUCCESS` for flatten implies **no throw** and **`RequestFlatten` returned `Success`**. |
| **`RequestFlatten` success** | **Two** important cases return **`FlattenResult.SuccessResult`** **without** submitting a closing order: (**a**) **`FLATTEN_SKIPPED_ACCOUNT_FLAT`** when `GetAccountPositionForInstrument` sees flat (~95–106); (**b**) **`FLATTEN_ALREADY_IN_PROGRESS`** duplicate (~85). | **(a)** means **chart `GetPosition` saw flat** — reconciliation could **still** see other rows / same master / timing difference. **(b)** means **no new order**. |
| **Order actually submitted** | `SubmitFlattenOrder` returns `SuccessResult(order.OrderId)` after `account.Submit` (~295–305). | **Proves submission path returned**, **not** that the market order **filled** or that **all** contracts with that **master** are flat. |

**Conclusion for incident:** `NT_ACTION_SUCCESS` is **weak** for “exposure cleared”: it is **command completion without exception**, and **`RequestFlatten` success** means **policy passed** and either **skip (flat on chart)** or **submit flatten order** — **never** “reconciliation qty match restored.”

---

## Section 6 — Most likely surviving position object (by instrument)

**Method:** Rank hypotheses **allowed by code** and **supported or not contradicted** by logs. Where logs cannot name the NT `Instrument` instance, marked **inferred**.

### MNQ

| rank | hypothesis |
|------|----------------|
| **Most likely** | **Flatten path confirmed only / acted on chart `_ntInstrument` position**; **reconciliation** still sums **account-wide** `MasterInstrument.Name` **MNQ** — **another row, timing, or fill lag** leaves non-zero in snapshot. **Correlated:** registry / stale-order work (`STALE_QTSW2_ORDER_DETECTED`, `REGISTRY_BROKER_DIVERGENCE`) on IEA **2**. |
| **Second** | **Submit succeeded** but **market fill** not yet reflected at snapshot time used by reconciliation (short lag) — **possible**, **not log-proven** vs persistent 01:40Z. |
| **Least likely** | Pure **stale snapshot cache** with no structural difference — **metrics** record snapshot calls but logs don’t prove cache bug. |

### MYM

| rank | hypothesis |
|------|----------------|
| **Most likely** | **`GetAccountPositionForInstrument` / chart path reports 0** while **`GetAccountSnapshot` path still shows 2** for **MYM** — **proven inconsistency** at `01:36:01Z` (§3). **Best structural explanation:** **different read APIs** (**account-wide position list keyed by master name** vs **`GetPosition(_ntInstrument)`**) and/or **contract / instance mismatch** under same master. |
| **Second** | **Race** between subsystems in the same second (less explanatory than structural mismatch). |
| **Least likely** | Journal wrong — **journal_qty 0** aligns with “no open journal rows” narrative; not the driver of **broker_qty 2**. |

### MNG

| rank | hypothesis |
|------|----------------|
| **Most likely** | Same **aggregate vs chart `GetPosition`** gap as MYM/MNQ; plus **on-disk journal** shows **trade already reconciled flat at 01:02Z**, so **`journal_qty 0` is expected** — surviving issue is **broker snapshot still showing 2** after **MNG** flatten **submit success** (`01:36:52Z`). **Implies** flatten did **not** clear the exposure reconciliation attributes to **MNG**, or **snapshot** still sees it. |
| **Second** | **Wrong contract flattened** relative to position rows that still carry **MNG** master name in snapshot — **inferred**, **not log-proven**. |
| **Least likely** | **`NT_ACTION_SUCCESS` with no order** — would normally pair with **`FLATTEN_SKIPPED_ACCOUNT_FLAT`** on that path; **not** searched exhaustively in this doc for MNG-specific skip lines. |

---

## Section 7 — Overall root cause

**Primary (code-proven + log-backed):**  
**`NT_ACTION_SUCCESS` means the NT action delegate finished without exception — for flatten, `RequestFlatten` returned success (including possible “skip flat on chart” or “order submitted”) — it does *not* mean reconciliation’s account-wide position sum is zero.**

**Secondary (code-proven structural):**  
**Reconciliation and flatten do not use the same position identity:**

- Reconciliation: **all** positions, **`MasterInstrument.Name`**, **summed per key**.
- Flatten: **`GetPosition(_ntInstrument)`** + **`CreateOrder(ntInstrument)`** on the **executing chart’s** instrument; **string `instrument_key` is not used to select the NT `Instrument`** in the primary path (`NinjaTraderSimAdapter.NT.cs` ~234–265, ~271–295).

**Contributing (log-proven):**  
**Cross-path numeric contradiction for MYM** (`broker_qty` vs `broker_position_qty` at same timestamp).

**Ranked one-liner choices from task list:**

1. **Primary:** **Flatten success = command completion (and/or order submit), not reconciliation clearing — plus structural mismatch between account snapshot aggregation and chart `GetPosition` / `CreateOrder`.**  
2. **Secondary:** **Reconciliation and flatten target different position identities** (master-level sum vs single `Instrument` reference).  
3. **Contributing:** **Broker state readers are inconsistent and non-authoritative across subsystems** (MYM evidence).

---

## Provenance — key code citations

```234:265:RobotCore_For_NinjaTrader/Execution/NinjaTraderSimAdapter.NT.cs
    (int quantity, string direction) IIEAOrderExecutor.GetAccountPositionForInstrument(string instrument)
    {
        var account = _ntAccount as Account;
        var ntInstrument = _ntInstrument as Instrument;
        // ...
            try
            {
                position = dynAccount.GetPosition(ntInstrument);
            }
            // ...
    }
```

```5531:5541:RobotCore_For_NinjaTrader/Execution/NinjaTraderSimAdapter.NT.cs
            foreach (var position in account.Positions)
            {
                if (position.Quantity != 0)
                {
                    positions.Add(new PositionSnapshot
                    {
                        Instrument = position.Instrument.MasterInstrument.Name,
                        Quantity = position.Quantity,
```

```107:118:RobotCore_For_NinjaTrader/Execution/ReconciliationRunner.cs
        foreach (var p in positions)
        {
            if (p.Quantity != 0 && !string.IsNullOrWhiteSpace(p.Instrument))
            {
                var inst = p.Instrument.Trim();
                // ...
                accountQtyByInstrument[inst] = existing + qty;
```

```298:326:RobotCore_For_NinjaTrader/Execution/StrategyThreadExecutor.cs
                action.Execute(executor);
                _log.Write(RobotEvents.EngineBase(_utcNow(), tradingDate: "", eventType: "NT_ACTION_SUCCESS", state: "ENGINE",
```

```93:106:RobotCore_For_NinjaTrader/Execution/InstrumentExecutionAuthority.Flatten.cs
            var (quantity, direction) = Executor.GetAccountPositionForInstrument(instrumentKey);

            if (quantity == 0 || string.IsNullOrEmpty(direction))
            {
                // ...
                Log.Write(... "FLATTEN_SKIPPED_ACCOUNT_FLAT" ...);
                return FlattenResult.SuccessResult(utcNow);
            }
```

---

## What remains **unproven** without NT exports

- Exact **contract month(s)** of every open position row on the account at each timestamp.  
- Whether **two** `Position` objects shared **`MasterInstrument.Name` MYM/MNG/MNQ** simultaneously.  
- NinjaTrader’s internal behavior when **`GetPosition(chartInstrument)`** vs **enumerating `account.Positions`**.

Those gaps do **not** weaken the **code-level** conclusion that **reconciliation and flatten are not guaranteed to reference the same position object** and that **`NT_ACTION_SUCCESS` is not flat confirmation.**

---

*Supersedes nothing; complements `ROOT_CAUSE_BROKER_QTY_PERSISTENT_2026-03-23_0130-0141.md`.*
