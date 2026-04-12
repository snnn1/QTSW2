# Phase 2A / 2B addendum — RR policy freeze + path audit

**Status:** Normative (frozen policy in §1; §2 is a code snapshot — update when implementation changes)  
**Related:** [02_decision_authority.md](02_decision_authority.md)

---

## Phase 2A — RR policy (binary)

| # | Question | Answer |
|---|----------|--------|
| 1 | Can the global kill switch block cancel? | **NO** |
| 2 | Can it block BE tighten? | **NO** |
| 3 | Can it block flatten? | **NO** |
| 4 | Are all RR actions always allowed, or only some? | **ONLY SOME** |
| 5 | Is there one shared RR gate, or action-specific RR rules? | **ACTION-SPECIFIC** |

**Scope note:** “Global kill switch” means `KillSwitch.IsEnabled()` / `configs/robot/kill_switch.json` as wired into `ExecutionPermissionAuthority.TryAdapterOrderSubmitPreflight` (adapter order-submit preflight). It does **not** include instrument unsafe-lock from unmapped fills (separate mechanism).

---

## Phase 2B — Path audit (class model)

**Legend — expected gate path (normative for this addendum):**

- **RI:** stream `RiskGate.CheckGates` when stream-driven; always adapter `TryAdapterOrderSubmitPreflight` + structural + overlay on SIM submits.
- **RC:** adapter `TryAdapterOrderSubmitPreflight` + structural + overlay on submits; no stream `RiskGate` unless the call site is inside a stream method that already enforced it.
- **RR:** **no** global kill preflight; **no** stream `RiskGate`; action-specific checks (below).

| Path | Class | Current gate path | Expected gate path | Compliant |
|------|-------|-------------------|--------------------|-----------|
| Lock bracket submit (`SubmitStopEntryBracketsAtLock` → `SubmitEntryOrder` / `SubmitStopEntryOrder`) | RI | Stream `RiskGate` → adapter EPA preflight → structural + overlay | Same | **YES** |
| Market reentry submit (`ExecuteSubmitMarketReentry` → `SubmitEntryOrder`) | RI | Engine re-entry predicates + IEA queue; **no** stream `RiskGate`; adapter EPA preflight → structural + overlay | Same (no stream `RiskGate` on this path) | **YES** |
| Protective submit after fill (`HandleReentryFill` → `SubmitProtectiveStop` / `SubmitTargetOrder`, etc.) | RC | **No** stream `RiskGate`; adapter EPA preflight → structural + overlay | Same | **YES** |
| BE move (`ModifyStopToBreakEven` → `ModifyStopToBreakEvenReal`) | RR | SIM verify + NT context + journal duplicate guard; **no** EPA preflight; **no** structural/overlay layer | RR: **no** global kill; action-specific guards only (context + journal) | **YES** (matches Phase 2A: kill does not block BE) |
| Cancel protective (`CancelIntentOrders` — stops/targets) | RR | Same as cancel entry row | Same | **YES** |
| Cancel entry (`CancelIntentOrders`) | RR | SIM verify + NT context → `CancelIntentOrdersReal`; **no** EPA preflight | RR: **no** global kill; context checks only | **YES** |
| Flatten (`FlattenIntent` / `Flatten` → `NtFlattenInstrumentCommand` enqueue) | RR | `TryExecutionSafetyFlattenGuard` (structural flatten + overlay); **no** EPA preflight | Same | **YES** |
| Emergency flatten (`EnqueueEmergencyFlattenProtective` / same NT flatten command path) | RR | Same flatten guard chain as flatten | Same | **YES** |
| Orphan-fill containment (unmapped fill → `ApplyUnmappedExecutionKillSwitch` + `blockInstrument` / instrument lock) | Safety (blocks RR) | Unsafe-lock overlay + engine freeze / stand-down; **not** global kill file | Fail-closed until operator unlock; RR **blocked** on purpose for that instrument | **YES** |
| Recovery-driven repair (`TryRepairTaggedBrokerWithoutJournal` / tagged-broker journal upsert) | Repair / journal | Internal eligibility + cooldown; **no** `RiskGate`; **no** EPA preflight | Integrity + bounded retry; not RI/RC submit path | **YES** |

**Notes (non-binary):**

- **Cancel protective** and **cancel entry** share one implementation: `CancelIntentOrders` — no distinction at the adapter gate layer.
- **Orphan-fill:** the log text states trading disabled including flatten until unlock; that is **not** the global kill switch — it is **unsafe-lock + supervisory block** (Phase 2A answers refer only to global kill).

---

## Index

| Phase | Document |
|------:|----------|
| 2A | This file § “Phase 2A” |
| 2B | This file § “Phase 2B” |
