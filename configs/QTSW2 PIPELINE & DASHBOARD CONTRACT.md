QTSW2 PIPELINE & DASHBOARD CONTRACT

Version: Unversioned (canonical)
Scope: Orchestrator, Pipeline Stages, Event System, Dashboard
Audience: You (current), You (future), Cursor, any contributor

1. System Purpose (Non-Negotiable)

QTSW2 is a deterministic, batch-oriented research pipeline that:

Transforms raw market data into standardized data

Analyzes that data using rule-based logic

Consolidates results into auditable outputs

Exposes execution state and progress via a dashboard

It is not a live trading system.
It is not a strategy decision engine.

2. Authority & Ownership Model
2.1 Single Source of Truth
Concern	Authoritative Owner
Pipeline lifecycle	Orchestrator
Execution ordering	Orchestrator
State machine	StateManager
Concurrency control	LockManager
Data correctness	Translator / Analyzer / Merger
Event truth	JSONL logs
UI state	Derived only

No other component may infer, override, or repair these facts.

3. Orchestrator Contract
3.1 Responsibilities (Must Do)

The Orchestrator must:

Enforce a single active pipeline run

Manage the pipeline finite state machine

Coordinate stage execution order

Acquire and release locks deterministically

Emit lifecycle and stage events

Recover safely after crashes

Expose degraded mode explicitly if unavailable

3.2 Prohibitions (Must Never Do)

The Orchestrator must never:

Inspect or modify market data

Infer missing data or stage outputs

Bypass state transitions

Run stages out of order

Continue execution if lock acquisition fails

Hide degraded or failed states

4. Pipeline Stage Contract (Translator, Analyzer, Merger)
4.1 Shared Rules (All Stages)

All pipeline stages must:

Be deterministic

Be idempotent

Accept explicit inputs

Produce explicit outputs

Fail loudly on invalid input

Emit events, not control flow

Stages must never:

Coordinate with other stages directly

Modify upstream data

Skip validation to “make it work”

Decide pipeline success/failure (runner + orchestrator decide)

4.2 Translator Contract

Owns:

Raw CSV → standardized Parquet

Timestamp normalization

Schema enforcement

Must fail if:

Schema is invalid

Timestamp conversion fails

Instrument cannot be identified

Must never:

Analyze data

Infer missing fields

Modify already-translated files

4.3 Analyzer Contract

Owns:

Pattern detection

Indicator calculation

Signal generation

Must:

Treat translated data as immutable

Associate all outputs with run_id

Write outputs atomically

Must never:

Modify translated data

Decide which instruments “matter”

Infer session/stream metadata

4.4 Merger Contract

Owns:

Aggregation

Alignment

Consolidation

Must:

Require explicit instrument + session identity

Fail loudly on missing fields

Preserve upstream semantics exactly

Must never:

Infer strategy logic

Filter trades

Apply slot switching

“Fix” analyzer results

5. State Manager Contract
5.1 State Authority

The StateManager is the only authority on pipeline state.

States are:

IDLE
RUNNING_TRANSLATOR
RUNNING_ANALYZER
RUNNING_MERGER
SUCCESS
FAILED

5.2 Rules

All transitions must be validated

State writes must be atomic

Corrupted state must recover to IDLE

State may not be skipped

No component may assume state — it must be read.

6. Lock Manager Contract
6.1 Rules

Lock acquisition must be atomic

Only one pipeline may hold a lock

Locks must be released on success or failure

Stale locks may be reclaimed deterministically

6.2 Prohibitions

No speculative locking

No silent lock overrides

No parallel pipeline runs

7. Event System Contract
7.1 Event Semantics

There are two tiers of events:

Tier	run_id
System-level	"__system__"
Run-scoped	real run_id
7.2 Rules

Events must never crash execution

Missing run_id must degrade to system event

JSONL logs are the audit truth

WebSocket is best-effort visibility only

Observability must be lossy before execution becomes brittle.

8. Dashboard Contract
8.1 Responsibilities

The Dashboard may:

Start / stop / reset pipeline

Display state and progress

Stream events

Surface errors clearly

8.2 Prohibitions (Critical)

The Dashboard must never:

Decide pipeline logic

Retry stages

Infer state

Modify data

Mask degraded mode

Act as a source of truth

The dashboard is observability + control, not intelligence.

9. Failure & Degraded Mode Contract
9.1 Degraded Mode

If the orchestrator is unavailable:

Pipeline routes are disabled

Health endpoint reports degraded

WebSocket closes with explicit reason

UI must surface degraded state

Partial availability is worse than failure.

10. Invariants (System-Wide)

These must always hold:

One pipeline at a time

No inferred data

No silent failure

No hidden state

No UI-driven logic

No observability-driven execution failure

11. Why This Contract Matters

This contract:

Prevents architectural drift

Protects against future shortcuts

Makes refactoring safe

Makes Cursor safer to use

Preserves quant-grade discipline

It is not documentation fluff — it is a system governor.