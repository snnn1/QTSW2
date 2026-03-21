# Investigation: ES1 “forgot” orders + ES2 did not submit (broker / restart) — root causes & fixes

**Related:** [2026-03-19 S1 orders cancelled](2026-03-19_S1_ORDERS_CANCELLED_INVESTIGATION.md), [2026-03-20 timetable NO_TRADE](2026-03-20_S1_TIMETABLE_SLOT_CORRUPTION_NO_TRADE_INVESTIGATION.md)  
**Summaries hub:** [docs/robot/summaries/README.md](../summaries/README.md) · [Master roll-up Mar 17–20](../summaries/MASTER_RECENT_ISSUES_AND_FIXES_2026-03-17_through_2026-03-20.md)

---

## 1. Executive summary

| Issue | Primary explanation | Code / ops fix |
|-------|---------------------|----------------|
| **ES1 did not “remember” orders** after reconnect/restart | In-memory **IEA registry** is empty on new `run_id`. “Memory” is **restart adoption**: match broker `QTSW2:` orders to **execution journals** (`EntrySubmitted && !TradeCompleted`). Adoption was skipped or **failed closed too early**, so orders became **UNOWNED_ENTRY_RESTART** → flatten. | **Time-only adoption deferral** (no scan-count cap), longer grace (**60s**), **instrument normalization** (`MES 03-26` → `MES`) for journal matching. |
| **ES2 did not place orders when broker started** | Usually **not** “broker connected ⇒ ES2 submits.” ES2 submits at its **own** range lock / slot. **Secondary:** shared **MES** IEA — ES1 **flatten/recovery** or **NO_TRADE** / **stand-down** on MES blocks **all** streams on that symbol until cleared. | Same adoption fixes reduce wrongful flatten; **verify logs** for `NO_TRADE`, `STAND_DOWN`, `UNOWNED_*`, `FLATTEN_*`; **timetable guard** (separate doc); ensure **`QTSW2_PROJECT_ROOT`** stable so journals and adoption see the same directory. |

---

## 2. ES1 — why the registry looked “empty”

### 2.1 Intended design

- **Broker** holds working orders across strategy restarts.
- **IEA `OrderMap`** is in-process only → **lost on restart**.
- **Recovery:** `ScanAndAdoptExistingOrders` (`InstrumentExecutionAuthority.NT.cs`) reads broker orders, decodes `intentId` from tags, and checks `Executor.GetAdoptionCandidateIntentIds(ExecutionInstrumentKey)` which scans `data/execution_journals/*.json`.

### 2.2 Documented failure (2026-03-19)

From [S1 orders cancelled investigation](2026-03-19_S1_ORDERS_CANCELLED_INVESTIGATION.md):

1. ES1 submitted brackets; broker had 2 working orders.
2. New run (`run_id` changed) → **local_working = 0**, broker still had orders.
3. Restart path classified orders as **unowned** → **RequestRecovery** → **flatten** → orders cancelled.

The journal file for the intent **existed** with plausible adoption fields; adoption **should** have matched. Hypotheses in that doc: journal path/cwd, scan timing, logging gaps.

### 2.3 New finding: deferral logic + burst execution updates

`AdoptionDeferralDecision` previously required **both**:

- wall time within grace (**20s**), and  
- **deferred scan count ≤ 10**.

`_adoptionDeferredCount` increments on **every** `ScanAndAdoptExistingOrders` pass in the “candidates empty, broker has QTSW2 orders” branch. NinjaTrader can emit **many execution updates per second** (Accepted → Working → …). That can push **scan count > 10 in &lt;1s** while **elapsed wall time ≈ 0**, tripping **GraceExpiredUnowned** and proceeding to **UNOWNED** — **not** a real “grace expired” condition.

**Fix implemented:** deferral is **wall-clock only** (default **60s** grace in IEA). Scan count is no longer part of the decision. Unit test: `TestManyRapidScansDoNotForceUnowned` in `DelayedJournalVisibilityTests.cs`.

### 2.4 New finding: instrument string mismatch (NT full name)

Journals store whatever `RecordSubmission` received for `instrument`. If the journal holds **`MES 03-26`** (or similar) and adoption compares to execution key **`MES`**, **strict string equality fails** → **no adoption candidates** → same UNOWNED path.

**Fix implemented:** `JournalInstrumentMatchesExecutionKey` in `ExecutionJournal.cs` normalizes by **truncating at first space** before comparing to execution instrument and canonical.

### 2.5 Operational hardening (still recommended)

- Set **`QTSW2_PROJECT_ROOT`** to the repo root on the NT machine so **journal path never depends on `cwd`** (`ProjectRootResolver.cs`).
- At startup, confirm logs show **`ADOPTION_SCAN_START`** with non-zero `adoption_candidate_count` when journals exist, and **`journal_dir`** pointing at the expected `data/execution_journals` folder.

---

## 3. ES2 — why orders might not appear “when broker started”

### 3.1 Expected behavior

- **ES2** is a **separate stream** (later session / slot). Entry submission follows **stream state machine** (hydration → range build → lock → submit), not the moment the **broker connection** goes green.
- “Broker started” often coincides with **strategy realtime**; ES2 may still be **PRE_HYDRATION** or **RANGE_BUILDING** for hours before its slot.

### 3.2 Coupling through shared MES

ES1 and ES2 both **execute on MES** through the **same IEA** for that account+symbol. If ES1’s orders trigger **UNOWNED / flatten / supervisory stand-down** on **MES**, the engine may **block or tear down** working risk on that instrument — **ES2 cannot place** its brackets until the instrument is healthy again.

**Fix:** Same as ES1 — **correct adoption** and **avoid false UNOWNED** so ES1 brackets are not cancelled spuriously.

### 3.3 Other common blockers (check logs)

- **`NO_TRADE_*`** (e.g. timetable slot corruption — [2026-03-20 investigation](2026-03-20_S1_TIMETABLE_SLOT_CORRUPTION_NO_TRADE_INVESTIGATION.md)).
- **`RECONCILIATION_*`** / **`ORDER_REGISTRY_MISSING`** leading to recovery paths.
- Stream **STAND_DOWN** after journal corruption or aggregation failure.

---

## 4. Code changes (this repo)

| Area | Change |
|------|--------|
| `modules/robot/core/Execution/AdoptionDeferralDecision.cs` | **Time-only** deferral; default grace **60s** in API; `SimulateSequence` takes optional `graceSeconds`. |
| `RobotCore_For_NinjaTrader/Execution/InstrumentExecutionAuthority.NT.cs` | `RestartAdoptionGraceSeconds = 60`; call new `Evaluate(...)` signature; removed scan-count cap constant. |
| `ExecutionJournal.cs` (RobotCore + `modules/robot/core`) | `NormalizeJournalInstrumentSymbol` + `JournalInstrumentMatchesExecutionKey` for adoption candidate discovery. |
| `modules/robot/core/Tests/DelayedJournalVisibilityTests.cs` | Existing scenarios pass **`graceSeconds: 20`**; added **rapid-scan regression** test. |

---

## 5. Verification

- Run harness delayed-journal tests (project-specific), e.g.  
  `dotnet run --project modules/robot/harness/Robot.Harness.csproj -- --test DELAYED_JOURNAL_VISIBILITY`  
  (or the command your harness documents).
- Rebuild **Robot.Core** (NinjaTrader): `RobotCore_For_NinjaTrader/Robot.Core.csproj`.
- In a **sim replay** or **controlled restart**: confirm **`ADOPTION_DEFERRED_CANDIDATES_EMPTY`** may appear, then **`ADOPTION_SUCCESS`** / **`RECONCILIATION_RECOVERY_ADOPTION_SUCCESS`** before any **`UNOWNED_ENTRY_RESTART`** when journals are present.

---

## 6. Residual risks / follow-ups

1. **60s grace** — If journals are on a **slow/unavailable** volume for &gt;60s with real broker orders, the robot may still UNOWNED (fail-closed). Consider a **configurable** grace via `configs/robot/*.json` if needed.
2. **TryRecoveryAdoption** already runs from reconciliation (`RobotEngine`); ensure adoption **succeeds** with normalized instruments so mismatch clears without flatten.
3. **Aggregated entry tags** — If multiple intents share one broker order, adoption paths must continue to respect **aggregated tag** decoding (separate review if issues persist).
