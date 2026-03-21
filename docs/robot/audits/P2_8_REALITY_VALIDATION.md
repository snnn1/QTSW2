# P2.8 — Reality validation (measurement playbook)

**Date:** 2026-03-17  
**Mode:** Observability & analysis only — **no control-logic changes** in this phase.  
**Premise (P2.7):** The destructive funnel is **stable enough to measure**; validation is now a **data** problem.

---

## 1. Questions this phase answers

| Question | Why it matters |
|----------|----------------|
| **How often does policy block actions?** | Detect over/under enforcement; correlate with user pain or risk. |
| **How often does drift occur?** | Precheck vs final read mismatch — timing, cancel phase, or data bugs. |
| **Are emergency paths over-triggering?** | Noise vs real incidents; thread/queue misuse after P2.6.7. |
| **Too conservative vs too aggressive?** | Blocks + stream containment without flatten vs instrument flattens / emergencies. |

---

## 2. Data sources

| Source | Use |
|--------|-----|
| **Robot / NT logs** (JSON or text, however `RobotLogger` persists) | Primary counts for event types below. |
| **`automation/logs/events/*.jsonl`** (if pipeline emits structured events) | Batch aggregates by `eventType` / day. |
| **Canonical execution events** (`ExecutionEventTypes`, `EmitCanonical`) | Cross-check protective / mismatch families. |

Normalize on **`eventType`** (or equivalent field in your log JSON). If logs are plain text, **`rg -c 'EVENT_NAME'`** on rotated files is enough for a first pass.

---

## 3. Event catalog → metrics

### 3.1 Policy blocks (deny without broker flatten on that path)

**Primary:** `DESTRUCTIVE_ACTION_BLOCKED`

**Suggested breakdowns** (from payload if present):

- `phase` — e.g. `pre_cancel_execute_flatten_instrument`, `pre_submit_request_flatten`, `flatten_intent_real_pre_submit`, `on_recovery_requested_stream_containment`, `emergency_flatten_direct`
- `reason_code` / `policy_path` — e.g. `recovery_attribution_blocks_instrument`, `denied`, `recovery_missing_attribution`
- `instrument` / `execution_instrument_key`

**Derived metric:**

```text
block_rate ≈ count(DESTRUCTIVE_ACTION_BLOCKED) / count(DESTRUCTIVE_ACTION_REQUESTED)
```

(Use the same time window; optionally filter by `phase` to separate **command funnel** vs **recovery** vs **FlattenIntentReal**.)

**Related (engine):** `INSTRUMENT_SCOPED_RECOVERY_BLOCKED_BY_ATTRIBUTION` — stream containment without instrument flatten (policy outcome surfaced at engine layer).

### 3.2 Policy allows (context for denominator / sanity)

**`DESTRUCTIVE_ACTION_DECISION`** with payload `allowed: true` (or absence of `DESTRUCTIVE_ACTION_BLOCKED` after a `REQUESTED` on the same correlation).

**`DESTRUCTIVE_ACTION_EXECUTED`** — broker-side destructive submit completed (flatten order submitted path).

**Useful ratio:**

```text
execute_rate ≈ count(DESTRUCTIVE_ACTION_EXECUTED) / count(DESTRUCTIVE_ACTION_REQUESTED)
```

Low execute with high REQUESTED may mean many no-ops (flat account) or blocks — slice by instrument/session.

### 3.3 Drift (P2.6.7)

**`DESTRUCTIVE_POLICY_PREPOST_DRIFT`**

Payload fields to aggregate:

| `drift_kind` | Meaning |
|--------------|---------|
| `broker_abs_qty` | \|qty\| changed between adapter precheck and `RequestFlatten` read |
| `position_side_flip` | Signed qty flipped (logged with elevated severity note) |
| `journal_open_qty_after_cancel_phase` | Journal sum changed after scoped cancel, before flatten |

**Metrics:**

```text
drift_rate ≈ count(DESTRUCTIVE_POLICY_PREPOST_DRIFT) / count(DESTRUCTIVE_ACTION_REQUESTED)
drift_by_kind = group_by(drift_kind)
```

High **broker** drift: fills/cancels racing the funnel — expected occasionally; sustained high → investigate timing or latch.

High **journal** drift: journal vs broker coupling during cancel — observability only today; trend for data quality.

### 3.4 Emergency paths (over-triggering)

| Event | Interpretation |
|-------|----------------|
| `FLATTEN_EMERGENCY_ON_BLOCK` | `FlattenEmergency` entered (SIM) — enqueue or path selection |
| `FLATTEN_EMERGENCY_ON_BLOCK_FAILED` | Engine reports flatten failure after emergency call |
| `EMERGENCY_FLATTEN_CONSTRAINT_VIOLATION` | **P2.6.7:** `EmergencyFlatten` called **off** strategy thread |
| `EMERGENCY_FLATTEN_EXECUTING` | Direct `EmergencyFlatten` passed policy and is submitting |
| `DESTRUCTIVE_ACTION_*` with `phase: emergency_flatten_direct` | Policy audit trail for direct emergency |
| `PROTECTIVE_EMERGENCY_FLATTEN_TRIGGERED` | Protective coverage coordinator — **different** subsystem; correlate if “emergency” feels high |

**Over-triggering heuristic:**

```text
emergency_intent_rate ≈ count(FLATTEN_EMERGENCY_ON_BLOCK) + count(EMERGENCY_FLATTEN_EXECUTING)
constraint_violation_rate ≈ count(EMERGENCY_FLATTEN_CONSTRAINT_VIOLATION)
```

High **constraint violations** → callers still invoking `EmergencyFlatten` from wrong thread; fix **call site** or routing, not policy.

### 3.5 Invariant violations (misuse / bugs)

**`DESTRUCTIVE_POLICY_INVARIANT_VIOLATION`**

- Token/instrument mismatch, wrong thread with preclear token, invalid precheck context.

**Target:** ~0 in production. Non-zero → bug or hostile call pattern.

---

## 4. Conservative vs aggressive — how to read the numbers

| Signal | Likely “too conservative” | Likely “too aggressive” / leaky |
|--------|---------------------------|----------------------------------|
| **Blocks** | High `block_rate` on **RECOVERY** phases + many `stream_containment` outcomes; flat account still “busy” | Very low blocks but frequent **FAIL_CLOSED** / **UNKNOWN_ORDER** flattens |
| **Drift** | N/A (drift is not a policy knob) | Sustained **side_flip** drifts — investigate race or bad position read |
| **Emergency** | Low emergency counts, high **constraint violations** (wrong thread) | High `EMERGENCY_FLATTEN_EXECUTING` + normal market conditions |
| **Executed flattens** | Low **EXECUTED** vs high manual flatten attempts | High **EXECUTED** with low fills (check `FLATTEN_ORDER_REJECTED`) |

**Ground truth:** Pair metrics with **PnL / outage / support tickets**. Policy tuning should follow **business risk**, not counts alone.

---

## 5. Minimal operational queries (no new infra)

**Counts per day (bash-style):**

```bash
# Blocks
rg -c "DESTRUCTIVE_ACTION_BLOCKED" logs/

# Drift
rg -c "DESTRUCTIVE_POLICY_PREPOST_DRIFT" logs/

# Emergency SIM path
rg -c "FLATTEN_EMERGENCY_ON_BLOCK" logs/
rg -c "EMERGENCY_FLATTEN_EXECUTING" logs/
rg -c "EMERGENCY_FLATTEN_CONSTRAINT_VIOLATION" logs/

# Decisions (grep JSON if structured)
rg "DESTRUCTIVE_ACTION_DECISION" logs/ | rg "allowed"
```

**If JSON lines include `eventType`:**

```text
jq -r 'select(.eventType|test("DESTRUCTIVE")) | .eventType' events.jsonl | sort | uniq -c
```

(Adjust field names to your serializer.)

---

## 6. Suggested next step (still not control logic)

1. **One dashboard** (Grafana, spreadsheet, or weekly script): blocks / drift / emergency / invariant violations per **instrument** and **trading week**.  
2. **Baseline window:** e.g. first 2 weeks post-deploy of P2.6.7 — store baseline rates.  
3. **Alert thresholds** (ops): e.g. `EMERGENCY_FLATTEN_CONSTRAINT_VIOLATION > 0` daily → page; drift_rate > p95 baseline → ticket.

---

## 7. References

- **Funnel & event map:** `P2_7_IMPOSSIBILITY_AUDIT.md` §5  
- **Historical gap list (pre-fix):** `P2_6_5_DESTRUCTIVE_COVERAGE_AUDIT.md` (footer links P2.7)

---

*End of P2.8 reality validation playbook.*
