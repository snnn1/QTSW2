# Phase 4 — Event model (conceptual)

**Version:** 1.0  
**Status:** Normative (logical layer; file paths in Phase 5)

---

## 1. Classification

| Class | Meaning |
|-------|---------|
| **Authoritative** | Emitted when durable state changes or permission decided; may drive replay semantics |
| **Descriptive** | Diagnostics, metrics, human narrative; never sole proof of permission |

---

## 2. Canonical event taxonomy (first-class)

| Event type | Class | Notes |
|-------------|-------|-------|
| `INTENT_RECORDED` | Authoritative | Intent identity created or updated in ledger |
| `ORDER_SUBMITTED` | Authoritative | Order sent toward broker |
| `EXECUTION_FILLED` | Authoritative | Fill applied |
| `STREAM_STATE_TRANSITION` | Authoritative | Stream FSM transition |
| `CONTROL_DECISION` | Authoritative | EPA allow/deny with reason code |
| `JOURNAL_COMMITTED` | Authoritative | Stream terminal persisted |
| `RECONCILIATION_PASS` | Descriptive | Unless policy elevates |
| `LOG_DIAGNOSTIC` | Descriptive | Rate-limited, droppable |

Registry may extend; new **authoritative** types require Phase 1/2 review.

---

## 3. Required fields (all authoritative events)

| Field | Required |
|-------|----------|
| `event_id` | Yes (UUID or monotonic per run) |
| `ts_utc` | Yes |
| `run_id` | Yes |
| `event_type` | Yes |
| `instrument` | If applicable |
| `stream` | If applicable |
| `intent_id` | If applicable |
| `causal_parent_event_id` | If emitted as consequence of another |

---

## 4. Ordering rules

- **Per run:** Events are totally ordered by `(ts_utc, event_id)` for forensic export.  
- **Per stream:** `STREAM_STATE_TRANSITION` and `JOURNAL_COMMITTED` must respect FSM order.  
- **Causal:** `causal_parent_event_id` MUST point to an earlier event in the same run when set.

---

## 5. Minimum viable event chain (MVEC)

The **smallest** ordered set that can reconstruct **what happened and why** for a single intent lifecycle:

```
INTENT_RECORDED → ORDER_SUBMITTED → EXECUTION_FILLED → STREAM_STATE_TRANSITION → CONTROL_DECISION → JOURNAL_COMMITTED
```

**Rule:** Forensic replay must be able to start from **MVEC** + authority artifacts ([01_authority_model.md](01_authority_model.md)); verbose diagnostics are optional.

---

## 6. Authoritative vs optional streams

| Stream | Role |
|--------|------|
| **MVEC-aligned authoritative emission** | Required for proof |
| Primary robot JSONL | Narrative + ops; **optional** for strict replay if journals present |
| Canonical execution JSONL | **Supporting** replay audit; not permission authority |
| Health sink | **Optional** |

---

## 7. Pass criteria (Phase 4)

- Taxonomy + MVEC + field rules defined.  
- “What happened and why” recoverable from **MVEC + journals + EPA decisions** without requiring every diagnostic line.
