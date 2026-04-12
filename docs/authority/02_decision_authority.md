# Phase 2 — Decision authority (“can trade?”)

**Version:** 1.1  
**Status:** Normative  
**Related:** [01_authority_model.md](01_authority_model.md)

---

## 1. Named sole decision authority

**Concept name:** **ExecutionPermissionAuthority** (EPA) — the single logical evaluator that answers:

- For a given **risk action class** (see §2), may this **instrument** / **stream** perform the **broker-affecting operation** right now?

All **execution-affecting actions** MUST obtain an **allow** from EPA (or a **registered delegate** per [03_transition_contract.md](03_transition_contract.md)), **scoped to that action’s class**. EPA is one logical authority; **different gate stacks apply** to risk-increasing vs risk-reducing operations (§5).

---

## 2. Risk action classes (normative)

Every broker-facing or journal-mutating execution step MUST be classified into exactly one of:

| Class | Code | Meaning | Examples |
|------:|------|---------|----------|
| **Risk-increasing** | `RI` | Opens, adds, or restores **delta exposure** or working orders that can **fill into** new or larger position | Market/limit **entry**; **stop-entry** brackets; **session re-entry MARKET** that opens a position |
| **Coverage / structural** | `RC` | Mutates **protection or position definition** without being a deliberate “add size” intent—still broker submits, must be fail-closed | Initial **protective stop** / **target** after entry or re-entry fill; **corrective** protective per policy |
| **Risk-reducing** | `RR` | Reduces or removes exposure, cancels risk, or **tightens** loss (not expanding size) | **Flatten** family; **cancel** working orders; **break-even stop move** (tighter stop) |

**Rules**

1. **RI** is the strictest class: full **stream** `RiskGate.CheckGates` **when the submit is driven from the stream tick path**, plus adapter **structural + overlay** and (for SIM) **EPA preflight** (`KillSwitch`, instrument supervisory freeze) on `NinjaTraderSimAdapter` order-submit APIs.
2. **RC** uses the **adapter order-submit** path (`TryExecutionSafetyGateForOrderSubmit` → structural + overlay + EPA preflight where wired). It does **not** inherently require the **timetable/armed** stream gates unless the call is issued from the same **stream-scheduled** bracket path (implementation: initial brackets at lock use **both** stream `RiskGate` and adapter gates; post-fill protectives from `HandleReentryFill` use **adapter only**—see §9).
3. **RR** must still pass **EPA-consistent safety** (structural reality, overlay locks, snapshot freshness) on paths that touch the broker, but **must not** be blocked solely by **stream** gates meant for RI (timetable not armed, slot window)—otherwise recovery cannot flatten. **RR** skips the **stream** `RiskGate` in the engine; the adapter applies **`TryExecutionSafetyFlattenGuard`** for flatten and **does not** apply **`ExecutionPermissionAuthority.TryAdapterOrderSubmitPreflight`** on flatten (by design). **Cancel** and **BE modify** currently rely on NT context / execution-safety rules without the **same** EPA preflight used for RI submits—see §9 gaps.

**Forbidden:** Treating **RR** as **RI** (blocking flatten with “stream not armed”). **Forbidden:** Treating **RI** as **RR** (bypassing `RiskGate` on stream-driven entries).

---

## 3. Hard constraint — sole gatekeeper

**No component** may:

- Submit, modify, or cancel orders  
- Flatten (including emergency flatten)  
- Re-enter after stand-down  

**without** passing through **EPA** for that **action class** (or an explicitly registered delegate with **bounded** scope in the delegation register).

**Forbidden:** “Emergency” paths that mutate broker state **without** an EPA allow **for the correct risk class** documented as a **controlled escape hatch** in Phase 3 (cause / decision / effect).

**Reconciliation** may **detect** mismatch and **propose** repair; **mutations** to journal or broker must still be **authorized** under EPA unless a **single** registered repair delegate is listed with exact scope (default: EPA authorizes all mutations).

---

## 4. Execution-affecting actions (classified)

| Action | Class | Must pass |
|--------|-------|-----------|
| Submit entry / stop-entry (stream lock path) | RI | Stream `RiskGate` + adapter order-submit pipeline |
| Submit entry via **market re-entry** (`ExecuteSubmitMarketReentry` → `SubmitEntryOrder`) | RI | Engine re-entry gates (`IsInstrumentBlockedForReentry`, …) + adapter pipeline (**no** stream `CheckGates` on that code path) |
| Submit protective / target (post-fill, recovery paths) | RC | Adapter order-submit pipeline; stream `RiskGate` only if invoked from stream method that already enforced it |
| Modify stop to break-even | RR | Adapter `ModifyStopToBreakEven` (no `TryAdapterOrderSubmitPreflight` in current code—RR policy) |
| Cancel working orders (`CancelIntentOrders`) | RR | Adapter cancel path (no stream `RiskGate`; SIM/NT context guards) |
| Flatten / emergency flatten / `NtFlattenInstrumentCommand` | RR | `TryExecutionSafetyFlattenGuard` (structural + overlay); **no** `TryAdapterOrderSubmitPreflight` |

Read-only: snapshots, journal reads, logging — **no** EPA required.

---

## 5. Gate stacks by risk class (conceptual)

### 5.1 Stream `RiskGate` (`RiskGate.CheckGates`)

**Primary use:** **RI** submits initiated from **`StreamStateMachine`** where **`SubmitStopEntryBracketsAtLock`** runs (stop-entry brackets at lock).

Order: instrument frozen → recovery guard → kill switch → timetable validated → stream armed → session/slot (see [`RiskGate.cs`](../../RobotCore_For_NinjaTrader/Execution/RiskGate.cs)).

**Not used for:** flatten; **not** used for **`ExecuteSubmitMarketReentry`** (re-entry uses engine + adapter only).

### 5.2 Adapter EPA preflight (`ExecutionPermissionAuthority.TryAdapterOrderSubmitPreflight`)

**Primary use:** **RI** and **RC** **order submits** on `NinjaTraderSimAdapter` (`SubmitEntryOrder`, `SubmitStopEntryOrder`, `SubmitProtectiveStop`, `SubmitTargetOrder`, …): global kill switch + instrument frozen / supervisory block (aligned with `RiskGate` gates −1 and 1).

**Not used for:** flatten (`TryExecutionSafetyFlattenGuard`). **Not** used for: `ModifyStopToBreakEven`, `CancelIntentOrders` (current implementation).

### 5.3 Adapter structural + overlay

**Use:** All **RI** and **RC** order submits; **RR** flatten after enqueue uses **`TryExecutionSafetyFlattenGuard`** (parity, recovery latch, exposure rules, overlay unsafe lock / snapshot age).

---

## 6. Fixed evaluation order (blocker stack) — stream RI path

Evaluate in **this order** for **stream-attributed RI**; **first failing gate** determines the **effective block reason**.

| Order | Gate | Source (conceptual) |
|------:|------|---------------------|
| G-1 | Instrument frozen / risk latch / engine `_frozenInstruments` | Control + Phase 1 |
| G0 | Recovery guard — `IExecutionRecoveryGuard.IsExecutionAllowed()` | Connection recovery |
| G1 | Global kill switch | `KillSwitch.IsEnabled()` |
| G2 | Timetable validated for stream | Engine / timetable apply |
| G3 | Stream armed | `StreamStateMachine` |
| G4 | Session / slot window | `ParitySpec` + time |

Mismatch / protective coordinator blocks are folded into **instrument frozen / supervisory** callbacks where implemented (`IsInstrumentFrozenOrSupervisorilyBlocked`).

**Effective reason:** First failure wins.

---

## 7. Blocker precedence (multiple simultaneous)

- **Strongest policy:** Fail-closed global (kill switch, recovery disallow) overrides stream-local “ok.”  
- **Instrument freeze** overrides stream armed for **RI** paths that respect freeze.  
- **Single surfaced reason:** `EFFECTIVE_BLOCK = first_failed_gate` with stable reason code enum (below).

---

## 8. Reason model (normative codes)

| Code | Meaning | Unblock when |
|------|---------|--------------|
| `INSTRUMENT_FROZEN` | Frozen set or latch | Explicit unfreeze + conditions per engine |
| `RECOVERY_STATE` | Not CONNECTED_OK / RECOVERY_COMPLETE | Recovery completes |
| `KILL_SWITCH_ACTIVE` | Kill switch enabled | File disabled |
| `TIMETABLE_NOT_VALIDATED` | Stream not in eligible set / not applied | Timetable apply |
| `STREAM_NOT_ARMED` | Stream not armed | Arm |
| `SESSION_OR_SLOT` | Outside slot/session | Time enters window |
| `MISMATCH_BLOCK` | Mismatch coordinator blocked | Per coordinator release |
| `PROTECTIVE_BLOCK` | Protective coordinator | Per coordinator |
| `GLOBAL_KILL_SWITCH_ACTIVE` / `INSTRUMENT_FROZEN_OR_EPA_BLOCKED` | Adapter EPA preflight (`EXECUTION_BLOCKED_EPA`) | Same as G1 / G-1 respectively |

Each code maps to **one** authority path (file, journal, or named component).

---

## 9. Implementation audit matrix (`RobotCore_For_NinjaTrader`)

| Path | Class | Stream `RiskGate`? | `TryAdapterOrderSubmitPreflight`? | Structural / overlay |
|------|-------|-------------------|-----------------------------------|------------------------|
| `StreamStateMachine.SubmitStopEntryBracketsAtLock` → `SubmitEntryOrder` / `SubmitStopEntryOrder` | RI | **Yes** | **Yes** (inside adapter) | Yes |
| `NinjaTraderSimAdapter.ExecuteSubmitMarketReentry` → `SubmitEntryOrder` | RI | **No** | **Yes** | Yes |
| `StreamStateMachine.HandleReentryFill` → `SubmitProtectiveStop` / `SubmitTargetOrder` | RC | **No** | **Yes** | Yes |
| `ModifyStopToBreakEven` → `ModifyStopToBreakEvenReal` | RR | **No** | **No** | No (context checks only) |
| `CancelIntentOrders` → `CancelIntentOrdersReal` | RR | **No** | **No** | N/A |
| `FlattenIntent` / `Flatten` / `NtFlattenInstrumentCommand` enqueue | RR | **No** | **No** | **`TryExecutionSafetyFlattenGuard`** |
| Engine `FlattenIntent` / `CancelIntentOrders` helpers | RR | **No** | Delegates to adapter | As above |

**Residual risks (truthful gaps):**

- **RC** and **RI** share the same adapter preflight; **kill switch** does not block **RR** flatten in the current adapter design (intentional: must be able to reduce risk when frozen).  
- **Cancel** and **BE modify** do not yet share **`TryAdapterOrderSubmitPreflight`**; normatively they are **RR** and should either stay exempt from stream gates or adopt a **dedicated RR EPA hook** if product requires kill-switch to block cancels (unusual).

---

## 10. Detection vs decision

| Role | Components |
|------|------------|
| **Detect only** | `ReconciliationRunner` (compares snapshot to journal), mismatch **detectors**, health monitors |
| **Decide** | **EPA only** — emits allow/deny + reason **per risk class** |

Mismatch **detection** MUST NOT set freeze/block without EPA recording the decision (implementation may delegate **effect** application to a registered worker; see Phase 3).

---

## 11. Pass criteria (Phase 2)

- Every block reason traces to **one** code and **one** authority path.  
- All execution-affecting actions go through EPA **with the correct risk class**.  
- **RI** cannot bypass stream `RiskGate` when the submit is stream-scheduled bracket placement.  
- **RR** cannot be blocked solely by stream timetable/armed gates.  
- Mismatch detection is not a second decision engine — **EPA** owns deny/allow.  
- **§9 matrix** stays consistent with code or is updated when code changes.
