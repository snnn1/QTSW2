# Phase 3 gap pressure rank — P3-G1 … P3-G5

**Source:** [phase3_enforcement_audit_robotcore.md](phase3_enforcement_audit_robotcore.md) §7.  
**Purpose:** Implementation order from risk pressure, not audit breadth.

---

## 1. Category map (each gap ID)

| Gap ID | Keeps mismatch authority split? | Safety (S) | Truth / persistence (T) | Forensic (F) | Operator (O) |
|--------|----------------------------------|--------------|-----------------------------|--------------|--------------|
| **P3-G1** | **YES — this is the gap** | Secondary | **YES** (no durable decision record; who-decided is ambiguous) | **YES** (forensic “who decided?”) | — |
| **P3-G2** | No | Secondary | **YES — primary** (`JournalStore.Save` vs `State=DONE`) | Secondary | — |
| **P3-G3** | No | — | — | — | **YES** (multiple exit paths) |
| **P3-G4** | No | — | Secondary | **YES — primary** (flatten “officially complete”) | — |
| **P3-G5** | No | **YES — primary** (kill mixed into recovery guard) | Secondary | — | Secondary |

**Plain language**

- **Only P3-G1** is the gap that **preserves the split mismatch authority** (coordinator decides `Blocked`; gates enforce via OR’d callback — no single EPA decision artifact).
- **P3-G5** is the **safety**-critical one: wrong allow/deny coupling of kill switch and recovery.
- **P3-G2** is the **truth/persistence**-critical one: memory can say committed while disk did not persist.
- **P3-G4** is **forensic-only** in practice: clearer audit of flatten completion; does not fix wrong risk by itself.
- **P3-G3** is **operator/ops** weighting: confusing recovery exit, not a direct market-risk bug in the same way as G5.

---

## 2. Recommended implementation order (pressure-ranked)

| Order | Gap | Why this position |
|------:|-----|-------------------|
| **1** | **P3-G5** | **Safety first:** `IsExecutionAllowed` conflates recovery with global kill; affects every gated path using the adapter recovery callback and risks violating locked Phase 2A RR semantics. |
| **2** | **P3-G2** | **Truth second:** silent `JournalStore.Save` failure + `State=DONE` breaks §2 “official commit” and any downstream belief of terminal state. |
| **3** | **P3-G1** | **Unify mismatch decision authority:** removes the architectural split; depends on clear semantics from G5/G2 so you don’t bake confusion into a new façade. |
| **4** | **P3-G4** | **Forensic:** single “broker flat confirmed” (or equivalent) improves audits and post-mortems; lower urgency if G2 is fixed. |
| **5** | **P3-G3** | **Operator clarity:** consolidate `RECOVERY_COMPLETE` transitions; lowest acute trading risk in this list. |

---

## 3. One-line verdict

**Real implementation order:** **G5 → G2 → G1 → G4 → G3** — safety and durable truth before authority façade, forensics next, operator ergonomics last.
