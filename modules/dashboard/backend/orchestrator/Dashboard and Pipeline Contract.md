🔧 Cursor Prompt — Phase-1 Verification Tests (DO NOT REFACTOR)

Objective:
Verify that the Phase-1 always-on pipeline + dashboard system satisfies all architectural invariants.
This is a verification pass, not a redesign.

Global Rules (CRITICAL)

❌ Do NOT refactor production code

❌ Do NOT add new abstractions

❌ Do NOT change runtime behavior

❌ Do NOT add deduplication logic

❌ Do NOT introduce retries, buffering, or snapshots

✅ ONLY add tests, logging, or temporary instrumentation if needed

✅ Remove instrumentation after verification if appropriate

✅ What We Must Verify (Exact Checklist)
1️⃣ Single-Owner Event Emission

Write tests or instrumentation to confirm:

For each (stage, event) pair:

Exactly one code path emits it

Specifically verify:

translator/* → only translator service

analyzer/* → only analyzer service

merger/* → only merger service

pipeline/start|success|failed → only orchestrator

pipeline/state_change → only state manager

scheduler/* → only scheduler observer

system/* → only orchestrator background tasks

Deliverable:

A test or runtime log that shows no duplicate semantic events from multiple sources.

2️⃣ Runner Emits Control-Plane Only

Verify that runner.py does not emit:

translator/error

analyzer/error

merger/error

*/failure

Runner may emit:

orchestration warnings

retry exhaustion

pipeline aborts

system-level errors

Deliverable:

A test that runs a failing pipeline and asserts:

stage-level failure events come from services

runner emits no duplicate stage errors

3️⃣ EventBus.publish Is Non-Failing

Add a test that:

publishes events with:

zero subscribers

one subscriber

slow subscriber

subscriber throwing an exception

Verify:

publish() never raises

JSONL write still occurs

other subscribers are unaffected

4️⃣ Background Tasks Cannot Kill Backend

Add a test harness that:

injects exceptions into:

heartbeat loop

scheduler health loop

watchdog loop

archive task

websocket subscription loop

Verify:

exception is logged

error event is emitted

backend process stays alive

5️⃣ WebSocket Lifetime Guarantees

Add an integration test that:

opens a WebSocket

leaves it idle (no events) for several minutes

emits events after idle

confirms socket never closed by backend

Also test:

client disconnect → backend does not error

backend restart → client reconnects cleanly

6️⃣ Canonical Pipeline State Enforcement

Add tests asserting:

/api/pipeline/status only ever returns:

idle

running

stopped

error

Verify:

no starting_up

no inferred states

UI mirrors backend state exactly

7️⃣ JSONL Is Historical Only

Add a test to confirm:

live UI receives events only via EventBus/WebSocket

JSONL write failures do not break live events

UI does not depend on JSONL replay

🧪 How to Implement Tests

You may use:

pytest

async test helpers

FastAPI test client

WebSocket test client

temporary log instrumentation

Prefer:

black-box tests

observable behavior verification

logs + assertions over mocks where possible

📦 Final Output Required

Cursor must deliver:

A PHASE1_VERIFICATION_REPORT.md containing:

each checklist item

how it was tested

pass/fail

evidence (logs or assertions)

Any test files added

A clear statement:

“Phase-1 invariants verified”
or
“Violations found in X, Y, Z”

🚨 Important Constraint

If a violation is found:

❌ Do NOT auto-fix

❌ Do NOT refactor

✅ Report exactly where and why

✅ Propose the smallest possible fix