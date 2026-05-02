# Final system audit checklist — pre-live sign-off

This document is the **bounded** go-live gate for the trading robot. It does **not** claim that every folder in the repository is production-ready. It separates **architecture**, **enforcement**, **proof**, **deployment scope**, **forensics**, and **operations** so those questions are not blurred together.

---

## Bottom line

**NO-GO** for any ambiguous “whole repo / any tree” live claim.

**Conditional GO** only for an **explicitly bounded production path**:

- **`RobotCore_For_NinjaTrader`** (authoritative, buildable core for NinjaTrader)
- **`RobotSimStrategy.cs`** copied to NinjaTrader Strategies
- **`NT_ADDONS`** is a **legacy/manual mirror only**; it is not compiled or copied by the runtime deploy path
- **Sign-off evidence** produced from the **same linked source set** as that production build (see [Sign-off evidence commands](#sign-off-evidence-commands))

That is the correct professional stance.

---

## Why that conclusion is sound

Four questions that are often conflated:

| Question | Answer |
|----------|--------|
| Is the **authority model** coherent? | **Yes.** |
| Is **enforcement** in the **audited production-capable tree** strong enough? | **Yes.** |
| Are **scenario and contradiction proofs** meaningful? | **Yes**, for the **RobotCore / NT-linked runner path**. |
| Is the **whole repo** universally production-ready? | **No.** |

That distinction is intentional.

---

## The two real blockers (caveats)

### 1. Production-tree ambiguity

As long as **`modules/robot/core`** does not build and is not clearly **quarantined as non-production**, you cannot honestly say “the system” is live-ready without qualification.

**Correct live statement:**

> Only the **`RobotCore_For_NinjaTrader` DLL** plus copied **`RobotSimStrategy.cs`** path is in scope for live sign-off.

### 2. Best-effort decision-completion append

**Behavioral** authority can be correct while **forensic** artifacts still **silently fail to append** on disk I/O error (mismatch / flatten decision logs are designed not to block execution). That does not necessarily break trading correctness, but it weakens the standard **“durable proof always exists.”**

**Clean truth:**

- Execution/control behavior can still be correct even if some decision-log durability is imperfect.
- That is **acceptable only if the team explicitly accepts** that forensic limitation.

---

## Section 1 — Authority model

### 1.1 Truth ownership

- [ ] Current market exposure has **one primary authority** (broker-canonical / reconciliation-aligned model).
- [ ] Execution / fill history has **one primary authority** (execution journal and related structures — not raw narrative logs).
- [ ] Stream / slot terminal state has **one primary authority** (stream journal + state machine; terminal commit path).
- [ ] Execution permission has **one primary authority** (layered **RiskGate** + **ExecutionPermissionAuthority** / adapter preflight).
- [ ] Logs are **explicitly non-authoritative** for position, permission, or terminal truth.

**Go/No-Go:** all boxes in **1.1** must be checked for sign-off.

### 1.2 Tie-breaks

- [ ] Broker vs internal state has **one defined winner**.
- [ ] Journal vs logs has **one defined winner**.
- [ ] Persisted stream state vs in-memory state has **one defined winner**.
- [ ] Mismatch block vs generic frozen state has **one defined surfaced reason** (distinct deny codes / paths).
- [ ] Flatten complete has **one defined winner** (broker canonical reconciliation abs zero — not submit success alone).

**Go/No-Go:** all boxes in **1.2** must be checked.

### 1.3 Decision authority

- [ ] There is **one final authority** for risk-increasing execution permission (combined gates + EPA).
- [ ] **Detection** does not act as an independent decision engine (coordinator feeds engine-owned authority).
- [ ] **Decision** and **enforcement** are not split across competing sources for the same condition.
- [ ] **Recovery readiness** and **kill-switch** semantics are separated.
- [ ] **Risk-reducing** actions have explicit policy treatment (documented exceptions vs RI paths).

**Go/No-Go:** all boxes in **1.3** must be checked.

---

## Section 2 — Execution safety

### 2.1 Hot path gating

- [ ] **RI** actions cannot bypass the permission model.
- [ ] **RC** (coverage) actions follow the documented path.
- [ ] **RR** (risk-reducing) actions follow the documented exceptions/policy.
- [ ] **Kill switch** cannot be mistaken for **recovery disallow** (distinct reasons/gates).
- [ ] **Recovery disallow** cannot be mistaken for **kill switch**.

**Go/No-Go:** all boxes in **2.1** must be checked.

### 2.2 Mismatch safety

- [ ] Mismatch **detection** is separate from mismatch **decision**.
- [ ] Mismatch block has **one decision owner** (engine / EPA).
- [ ] Mismatch block has **one enforcement path** (hot path reads engine authority, not coordinator-only).
- [ ] Mismatch block has **one durable record** (append-only under persistence root — see forensic caveat above).
- [ ] **Contradiction tests** prove **one outcome** under mismatch conflict (audited runner).

**Go/No-Go:** all boxes in **2.2** must be checked.

### 2.3 Flatten safety

- [ ] **Flatten requested** is distinct from **flatten complete**.
- [ ] **Flatten submitted / in-progress** is distinct from **flatten complete**.
- [ ] Flatten completion has **one authoritative definition** (canonical broker exposure).
- [ ] Flatten completion is **not** inferred from logs alone.
- [ ] Flatten completion has **durable proof** (append-only — with best-effort caveat).

**Go/No-Go:** all boxes in **2.3** must be checked.

---

## Section 3 — Persistence and truth integrity

### 3.1 Stream commit integrity

- [ ] A stream cannot become **DONE** without **successful journal persistence** (primary `Commit()` path rolls back on save failure).
- [ ] Failed commit does **not** silently produce terminal DONE truth.
- [ ] Commit failure is **visible** (logged / observable).
- [ ] Call sites do **not** behave as though commit succeeded when it failed.

**Go/No-Go:** all boxes in **3.1** must be checked.

### 3.2 Run namespace

- [ ] Truth-bearing artifacts live under the **active run namespace** (`_persistenceBase` / deployment root).
- [ ] Mismatch decision records are **run-scoped** (under that root).
- [ ] Flatten completion records are **run-scoped**.
- [ ] Risk/control artifacts required for truth are **not** floating outside the run.
- [ ] A run can be **reconstructed** from run-scoped artifacts plus broker truth as designed.

**Go/No-Go:** all boxes in **3.2** must be checked.

### 3.3 Recovery truth

- [ ] Recovery completion has **one authoritative transition path** (engine-owned completion under lock).
- [ ] Recovery consumers read the **same engine-owned readiness** signal.
- [ ] No adapter/local logic **infers** recovery completion independently.

**Go/No-Go:** all boxes in **3.3** must be checked.

---

## Section 4 — Scenario proof

### 4.1 Required scenario runs

- [ ] Normal entry scenario passes  
- [ ] Execution-before-order scenario passes  
- [ ] Duplicate execution scenario passes  
- [ ] Stop-cancel recovery scenario passes  
- [ ] Stop-cancel flatten scenario passes  
- [ ] Persistent mismatch scenario passes  

**Evidence (production-linked path):** `SCENARIO_HARNESS_SIX` — see [Sign-off evidence commands](#sign-off-evidence-commands).

**Go/No-Go:** all boxes in **4.1** must be checked on the **audited runner path**.

### 4.2 Contradiction tests

- [ ] Mismatch vs flatten contradiction passes  
- [ ] Recovery vs RI execution contradiction passes  
- [ ] Broker vs journal contradiction passes  

**Evidence:** `AUTHORITY_CONTRADICTIONS` — see [Sign-off evidence commands](#sign-off-evidence-commands).

**Go/No-Go:** all boxes in **4.2** must be checked.

### 4.3 Outcome quality

- [ ] Each scenario produces **one coherent decision story** (replay/rebuild).
- [ ] Each scenario produces **one coherent persistence story**.
- [ ] Each scenario produces **one coherent operator story** (where validated).
- [ ] No scenario produces **dual truths**.

**Go/No-Go:** all boxes in **4.3** must be checked where applicable.

---

## Section 5 — Deployment parity

### 5.1 Runtime parity

- [ ] The **actual runtime build path** uses the corrected authority logic (**`RobotCore_For_NinjaTrader`**).
- [ ] **`NT_ADDONS`**, if kept, is explicitly treated as a manual mirror/non-runtime audit artifact.
- [ ] No **production-capable** tree still uses **outdated** authority wiring without an explicit exception.
- [ ] Any **non-aligned** tree is **explicitly non-production** (e.g. **`modules/robot/core`** until it builds and is re-audited).

**Go/No-Go:** all boxes in **5.1** must be checked.

### 5.2 Build discipline

- [ ] The **live deployment artifact** is clearly identified (**RobotCore DLL + RobotSimStrategy.cs**).
- [ ] The live deployment artifact **builds successfully**.
- [ ] The **test harness** used for sign-off is tied to the **same authority logic** as production (linked sources / same project).
- [ ] There is **no ambiguity** about which codebase is authoritative for live trading.

**Go/No-Go:** all boxes in **5.2** must be checked.

---

## Section 6 — Forensic auditability

### 6.1 Critical questions (each should be **YES** before live)

- [ ] Can I prove **what position was open**?
- [ ] Can I prove **why execution was blocked**?
- [ ] Can I prove **why execution was allowed**?
- [ ] Can I prove **why a stream committed terminal state**?
- [ ] Can I prove **when mismatch block entered and exited**?
- [ ] Can I prove **when flatten completed**?
- [ ] Can I prove **when recovery completed**?
- [ ] Can I prove **which component made the decision**?

**Go/No-Go:** at least all **decision-critical** questions must be answerable from **authoritative or durable** sources (not log prose alone), subject to the **best-effort append** caveat.

### 6.2 Artifact sufficiency

- [ ] Answers do **not** depend on logs alone where a durable artifact is required.
- [ ] Durable artifacts exist for the **highest-risk** decisions (subject to disk-failure acceptance).
- [ ] Operator-facing narrative is **not** treated as authority.
- [ ] Missing best-effort logs would **not** destroy **core** truth (journals + broker still ground truth).

**Go/No-Go:** **PARTIAL** acceptable only with **explicit** acceptance of residual forensic risk.

---

## Section 7 — Operational readiness

### 7.1 Failure behavior

- [ ] Commit failure fails **closed** enough to avoid false terminal truth.
- [ ] Mismatch block failure does **not** silently allow unsafe execution.
- [ ] Flatten completion ambiguity does **not** silently report flat when not flat.
- [ ] Recovery ambiguity does **not** silently re-enable execution early.
- [ ] Kill switch behavior is **absolute** where intended.

### 7.2 Alerting / observability

- [ ] Critical failures emit **visible** signals.
- [ ] Commit failure is **visible**.
- [ ] Mismatch block enter/exit is **visible**.
- [ ] Flatten completion is **visible**.
- [ ] Recovery completion is **visible**.

### 7.3 Human operability

- [ ] An operator can explain **blocked vs unblocked** state.
- [ ] An operator can explain the **most recent flatten** outcome.
- [ ] An operator can explain the **most recent recovery** outcome.
- [ ] An operator can distinguish **narrative logs** from **authoritative** truth.

**Go/No-Go:** Section **7** is **UNKNOWN from the repo alone** — it must be completed **outside the repo** before capital is at risk.

---

## Section 8 — Optional but strongly recommended

- [ ] **8.1 Determinism** — Same scenario produces the same authority and durable truth outcomes on repeat.
- [ ] **8.2 Stress** — Randomized / mixed-event stress run passes; no invalid state under repeated transitions; no cross-run contamination.
- [ ] **8.3 Dev/runtime hygiene** — Broken non-production trees are **fixed or quarantined**; mirror trees cannot be mistaken for live authority.

These are **not** absolute blockers unless you adopt a stricter standard.

---

## Hard stop conditions (automatic NO-GO)

Do **not** go live if any of these are true:

- More than one component can **independently decide** execution block for the **same** condition without a single reconciled authority story.
- A stream can be **DONE** without **durable** journal commit (primary path must not allow this).
- Flatten can be called **complete** without **authoritative** flat proof (broker-canonical model).
- Recovery can be called **complete** from **more than one** inconsistent path.
- **Live deployment tree** differs from the **audited authority tree** without documentation.
- **Contradiction tests** fail on the audited runner.
- **Scenario harness** (`SCENARIO_HARNESS_SIX`) fails on the audited runner.

---

## Sign-off evidence commands

Run against **`RobotCore_For_NinjaTrader`** (same linked sources as production sign-off):

```bash
dotnet run --project RobotCore_For_NinjaTrader/SiblingProtectiveCancelQueue.Test/SiblingProtectiveCancelQueue.Test.csproj -- SCENARIO_HARNESS_SIX
dotnet run --project RobotCore_For_NinjaTrader/SiblingProtectiveCancelQueue.Test/SiblingProtectiveCancelQueue.Test.csproj -- AUTHORITY_CONTRADICTIONS
```

When **`modules/robot/core`** builds again, the same tests are also wired for:

`dotnet run --project modules/robot/harness/Robot.Harness.csproj -- --test SCENARIO_HARNESS_SIX`  
`dotnet run --project modules/robot/harness/Robot.Harness.csproj -- --test AUTHORITY_CONTRADICTIONS`

Until then, treat **`modules/robot/core`** as **non-production**.

---

## Recommended sign-off summary

| Area | Typical read |
|------|----------------|
| **Architecture** | PASS |
| **Enforcement** | PASS |
| **Scenario proof** | PASS for the **audited runner path** |
| **Deployment parity** | **FAIL** unless production scope is **explicitly narrowed** to RobotCore DLL + RobotSimStrategy.cs |
| **Forensic auditability** | **PARTIAL** (best-effort decision append) |
| **Operational readiness** | **UNKNOWN** from repo alone |

---

## Conditional GO — statement to sign

**Conditional GO for live deployment** only if **all** of the following are true:

1. **Production** is explicitly limited to **`RobotCore_For_NinjaTrader`** DLL plus copied **`RobotSimStrategy.cs`**.
2. **`modules/robot/core`** is explicitly treated as **non-production** until fixed (and quarantined in process, not only in this doc).
3. **Sign-off evidence** is the passing **`SCENARIO_HARNESS_SIX`** and **`AUTHORITY_CONTRADICTIONS`** runs against **that** production-linked source path.
4. The team **explicitly accepts** the residual forensic risk that mismatch/flatten **decision append logs** are **best-effort** under disk failure.
5. **Section 7** operational checks are **completed outside the repo** before capital is put at risk.

**Without those conditions, the decision remains NO-GO.**

---

## The most important sentence

You are **not** blocked by architecture anymore. You are blocked by **production-scope discipline** and **operational sign-off discipline**. That is a much better place than being blocked by fundamental design flaws.

---

## Non-production quarantine note

| Path | Status |
|------|--------|
| `RobotCore_For_NinjaTrader/` | **In scope** for live sign-off (when checklist above is satisfied). |
| `NT_ADDONS/` | **Out of runtime scope**; legacy/manual mirror only while it exists. |
| `modules/robot/core/` | **Out of scope** for live until it **builds** and is **re-audited** against the same gates. |

---

*Document version: aligned with bounded production path and NT-linked sign-off commands. Update when `modules/robot/core` is restored or scope changes.*
