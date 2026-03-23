# Audit: Broker orders 408453625015 / 408453625012 / 408453625017 + `ComputeIntentId` collision check

**Scope:** On-disk logs under `logs/` (recursive `*.jsonl`) and execution journals under `data/execution_journals/*.json`.  
**Intent under study:** `cef6b767b7a307c1` (journal: `2026-03-20_NG2_cef6b767b7a307c1.json`, execution instrument **MNG**).

---

## Part A — Origin of broker order IDs

### Method

For each broker order ID: locate the **earliest** JSONL line in the repo; list the **event sequence** around creation; record any of: strategy/chart/adapter identity, instrument, tag, `fromEntrySignal`, OCO, `run_id`, `iea_instance_id` when present.

### Findings summary

| Broker ID | Earliest log (UTC) | Log file | First event | `run_id` | `iea_instance_id` in first line |
|-----------|-------------------|----------|-------------|----------|-----------------------------------|
| **408453625015** (MES) | `2026-03-23T00:41:10.4408798Z` | `logs/robot/robot_MES.jsonl` | `ORDER_UPDATED` (`order_state`: Submitted) | `2bc31b200b3b4bba8e8c1f2f95beaed2` | *(not in payload)* |
| **408453625012** (MYM) | `2026-03-23T00:41:10.0819341Z` | `logs/robot/robot_MYM.jsonl` | `ORDER_UPDATED` (Submitted) | `362ce7f966f540698ed3051f1980a4bc` | *(not in payload)* |
| **408453625017** (MNQ) | `2026-03-23T00:41:10.4558243Z` | `logs/robot/robot_MNQ.jsonl` | `ORDER_UPDATED` (Submitted) | `593e7d307944415d93caa612b2c94d6b` | *(not in payload)* |

**Not found anywhere in workspace `*.jsonl`:** lines containing these IDs together with `ORDER_SUBMIT_ATTEMPT`, `ORDER_SUBMITTED`, `ORDER_SUBMIT_SUCCESS`, `ORDER_CREATED_*`, or `ORDER_ACCEPTED` as a dedicated event name. The broker lifecycle appears in logs primarily as **`ORDER_UPDATED`** with `order_state` transitions (`Submitted`, `Working`, `ChangePending`, `Accepted` on MNQ, etc.).

**Fields requested but absent on first lines:** `stream`, `session`, `slot_time_chicago`, `trading_date`, `fromEntrySignal`, explicit `tag`, `oco` — first hits only carry `instrument`, `intent_id`, `order_type`, `stop_order_id` / `broker_order_id`, `stop_price`, `order_state`, and later `iea_instance_id` on CRITICAL paths.

### Per-ID detail

#### 408453625015 (MES)

1. **Earliest mention:** `ORDER_UPDATED` at `2026-03-23T00:41:10.4408798Z` — `stop_order_id` **408453625015**, `intent_id` **cef6b767b7a307c1**, `order_type` STOP, `stop_price` `"3"`, `order_state` **Submitted** (`robot_MES.jsonl`).
2. **Immediate sequence (same file, same `run_id` `2bc31b20…`):** Submitted → Working (dup) → ChangePending → Working (dup lines).
3. **~4s later:** `MANUAL_OR_EXTERNAL_ORDER_DETECTED` / `UNOWNED_LIVE_ORDER_DETECTED` (`run_id` **362ce7f966f540698ed3051f1980a4bc**, `iea_instance_id` **2**) — text: *"Restart scan: QTSW2 protective with no matching active intent"*.
4. **Parallel IEA:** same UNOWNED pair with `run_id` **593e7d307944415d93caa612b2c94d6b**, `iea_instance_id` **3**.
5. **Later adoption:** `ORDER_REGISTRY_ADOPTED` / `ADOPTION_SUCCESS` at `2026-03-23T00:59:26.7067166Z` (`run_id` **27e637c0771c4c049bad79a7b849c888**, `iea_instance_id` **7**, `source_context` **RESTART_ADOPTION**).

**Context immediately before first 015 line (same `robot_MES.jsonl`):** at `00:41:09.165Z` a **different** order id (`3fc2ee3d93214157b20d25c9cb767eb4`) triggers `MANUAL_OR_EXTERNAL_ORDER_DETECTED` with the **same** `intent_id` **cef6b767b7a307c1** — then the numeric **408453625015** appears on `ORDER_UPDATED`. That pattern is consistent with **broker / NT order id churn** (e.g. change/replace) while the **intent tag** stays stable.

#### 408453625012 (MYM)

1. **Earliest mention:** `ORDER_UPDATED` at `2026-03-23T00:41:10.0819341Z` — **earlier** than MES first line; `run_id` **362ce7f966f540698ed3051f1980a4bc**, `intent_id` **cef6b767b7a307c1**, Submitted.
2. **Sequence:** Submitted → Accepted → Working → ChangePending → Working (dup).
3. **UNOWNED:** `00:41:14.860Z`, `iea_instance_id` **2**; again `00:41:17.453Z`, `iea_instance_id` **3**.

**First health log:** `logs/health/_MNG_.jsonl` at `2026-03-23T00:59:28.2006035Z` — `REGISTRY_BROKER_DIVERGENCE`, `broker_order_id` **408453625012**, `iea_instance_id` **7** (broker has order, registry missing — adopt path). No MYM-specific health file line for this ID in the grep sweep.

#### 408453625017 (MNQ)

1. **Earliest mention:** `ORDER_UPDATED` at `2026-03-23T00:41:10.4558243Z`, `run_id` **593e7d307944415d93caa612b2c94d6b**, Submitted.
2. **Sequence:** includes `order_state` **Accepted** inside `ORDER_UPDATED` at `00:41:11.4096370Z`.
3. **UNOWNED:** `00:41:15.105Z` (`iea_instance_id` **2**), `00:41:17.466Z` (`iea_instance_id` **3**).

**First health log:** same `_MNG_.jsonl` timestamp as MYM — `REGISTRY_BROKER_DIVERGENCE` for **408453625017**, `iea_instance_id` **7**.

---

### Answers (Part A)

1. **Were these orders created by robot code in this session, or merely observed on restart?**  
   - **Logs do not contain a submit/create audit trail tied to these numeric broker IDs** (no `ORDER_SUBMIT_ATTEMPT` / `ORDER_CREATED_*` with these ids). The **earliest** evidence is **`ORDER_UPDATED`** on `2026-03-23` ~00:41:10Z, i.e. **execution callbacks / reconciliation**, not a proven in-log submit record.  
   - **`UNOWNED_LIVE_ORDER_DETECTED`** (restart scan, QTSW2 protective) proves that **at least some IEA instances treated them as live broker orders not owned in registry** at ~00:41:14–17Z — **observation / recovery semantics** on that run slice.  
   - **Journal `2026-03-20_NG2_cef6b767b7a307c1.json`** shows the **intent** was **submitted and filled on MNG** on **2026-03-20**; the micro-suite stops carrying the same `intent_id` are **consistent with robot protective tagging**, but **this log corpus cannot prove the exact clock-time submit of 015/012/017** on the micro charts.

2. **If created by robot code, which adapter/chart?**  
   - **Per-instrument log sinks:** **MES** chart/strategy instance logged **015** (`robot_MES.jsonl`), **MYM** logged **012**, **MNQ** logged **017**.  
   - **Distinct `run_id`s** at first `ORDER_UPDATED` (**2bc31b20…**, **362ce7f9…**, **593e7d30…**) support **three separate RobotEngine/IEA instances** (one per execution instrument), not a single adapter.

3. **If not created in-session (from logs), what is the earliest evidence they already existed?**  
   - **Earliest log line per ID is already `ORDER_UPDATED` in Submitted/Working-class states**, not “discovered flat” later — so the **first log evidence** is **broker-reported state**, not a later restart-only adoption line.  
   - **Explicit “already on broker / not in registry”** language appears at **`REGISTRY_BROKER_DIVERGENCE`** for **012/017** on the **MNG** health log at **`2026-03-23T00:59:28Z`** (`iea_instance_id` **7**).

---

## Part B — `ComputeIntentId` collision check (prefix `cef6b767b7a307c1`)

### Algorithm (matches `ExecutionJournal.ComputeIntentId`)

```csharp
// ExecutionJournal.cs — canonical string, UTF-8 SHA256, lowercase hex, first 16 chars
var canonical = $"{tradingDate}|{stream}|{instrument}|{session}|{slotTimeChicago}|{direction ?? "NULL"}|{entryPrice:F2}|{stopPrice:F2}|{targetPrice:F2}|{beTrigger:F2}";
```

**Important:** `Intent.ComputeIntentId()` uses `Intent.Instrument` — for `StreamStateMachine` brackets that is the **canonical** instrument derived from **stream** (e.g. **NG** for **NG2**), **not** the journal JSON’s `Instrument` (**MNG**).

**Slot time:** C# `ParseSlotFromOcoGroup` splits on `:` and fails for `10:30` in `QTSW2:OCO_ENTRY:…:NG2:10:30:…` (splits into `10` and `30`). For **collision scanning**, slot was recovered as **`HH:MM`** when `parts[1]==OCO_ENTRY`, `parts[3]==stream`, and `parts[4]/parts[5]` look like time components — matching runtime slot **10:30** for the NG2 journal and reproducing prefix **cef6b767b7a307c1**.

**BE trigger:** Same formula as `NinjaTraderSimAdapter.ComputeBeTrigger` (65% of entry–target distance, direction-sensitive).

### MNG journal row (authoritative for this intent)

| Field | Value |
|--------|--------|
| **Canonical tuple** | `2026-03-20\|NG2\|NG\|S2\|10:30\|Long\|3.11\|3.04\|3.16\|3.14` |
| **Full SHA256** | `cef6b767b7a307c1f4e6f8f797acad5263d4ed17b5c63496d0de00eb408e5a5a` |
| **16-char prefix** | `cef6b767b7a307c1` |
| **Execution instrument (journal JSON)** | `MNG` |
| **Collision (non-MNG tuple → same prefix)** | **NO** |

### Full-corpus scan

- **816** `data/execution_journals/*.json` files processed.  
- **Output:** `docs/robot/audits/journal_intent_id_recompute_20260317.csv` — columns: `file`, `exec_instrument`, `canonical_tuple`, `full_sha256`, `prefix_16`, `matches_filename`, `collision_non_mng`.  
- **Rows with `collision_non_mng == yes`:** **0** (no journal whose **execution** instrument ≠ **MNG** recomputes to prefix **cef6b767b7a307c1** under the rules above).  
- **Note:** **340** rows have `matches_filename == false` — recomputation from journal-only fields does not always equal the filename intent id (slot/price/BE drift vs true `StreamStateMachine` tuple). That does **not** affect the **target-prefix collision** result (still zero non-MNG hits).

---

## References

- `RobotCore_For_NinjaTrader/Execution/ExecutionJournal.cs` — `ComputeIntentId`
- `RobotCore_For_NinjaTrader/Execution/Intent.cs` — `ComputeIntentId()` uses `Instrument` (canonical), not `ExecutionInstrument`
- `RobotCore_For_NinjaTrader/Execution/NinjaTraderSimAdapter.cs` — `CreateIntentFromJournalEntry`, `ParseSlotFromOcoGroup`, `ComputeBeTrigger`
- Journal: `data/execution_journals/2026-03-20_NG2_cef6b767b7a307c1.json`
