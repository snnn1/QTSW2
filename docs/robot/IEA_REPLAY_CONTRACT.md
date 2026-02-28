IEA Deterministic Replay Contract (Institutional Grade)

Component: Instrument Execution Authority (IEA)
Purpose: Define the minimum invariants, event contract, and code constraints required to make IEA behavior deterministic under replay without modifying live trading semantics.
Reference: IEA_RUNTIME_DECONSTRUCTION.md

1. Replay Scope and Modes
1.1 Audit Replay (Required — v1)

Purpose: Deterministic reconstruction of IEA internal state transitions from recorded event streams.

Includes:

IntentRegistered

IntentPolicyRegistered

ExecutionUpdate

OrderUpdate

Tick / MarketData

Excludes:

EntrySubmissionRequest

FlattenRequest

Broker submission

OCO identity

EnqueueAndWait timeout behavior

Wall-clock enforcement

Goal:
Identical input event stream → identical internal IEA state snapshot.

This is the required and supported replay mode.

1.2 Full-System Replay (Optional Future)

Adds:

EntrySubmissionRequest

FlattenRequest

Broker simulation

Deterministic OCO identity

Simulated wall clock

Not required for deterministic IEA state reconstruction.

2. Replay Ordering Contract

All replay events MUST include:

sequence (long)

source (string)

executionInstrumentKey (string)

Replay ordering is strictly:

(source, sequence) ascending


The replay harness MUST:

Fail on sequence gaps

Fail on out-of-order events

Fail on cross-source misordering

Reject events referencing unknown intentId

Replay is single-lane deterministic. No concurrent replay execution is permitted.

3. Minimum Event Types (Audit Replay)
3.1 IntentRegistered

Required fields:

sequence

intentId

Full Intent (branch-relevant fields only)

Must precede:

Any ExecutionUpdate

Any BE evaluation

Any fill handling

Drives:

RegisterIntent()

Replay MUST fail if violated.

3.2 IntentPolicyRegistered

Required fields:

sequence

intentId

expectedQty

maxQty

canonical

execution

policySource (stable enum/string only)

Must precede:

EntrySubmissionRequest (Full-System only)

Drives:

RegisterIntentPolicy()

Replay MUST fail if violated.

3.3 ExecutionUpdate

Minimal deterministic replay shape:

sequence

executionId (nullable string)

orderId (string)

fillPrice (decimal)

fillQuantity (int)

marketPosition (string)

executionTime (DateTimeOffset)

tag OR intentId (string)

executionInstrumentKey

Deterministic Authority Rules

executionTime is the ONLY authoritative time input.

Dedup composite key MUST be derived exclusively from replay payload fields.

No raw NinjaTrader execution/order object properties may influence branching.

Replay MUST NOT depend on NT runtime types.

Drives:

EnqueueExecutionUpdate()

ProcessExecutionUpdate()

HandleEntryFill()

Dedup logic

3.4 OrderUpdate

Required fields:

sequence

orderId

orderState

filled (nullable int)

quantity (nullable int)

tag OR intentId (nullable string)

updateTime (nullable DateTimeOffset)

Deterministic rule:

Raw NT objects must not influence branching.

Replay must not require NT platform runtime.

Drives:

EnqueueOrderUpdate()

ProcessOrderUpdate()

Bracket state transitions

3.5 Tick / MarketData

Required fields:

sequence

tickPrice

tickTimeFromEvent (nullable DateTimeOffset)

executionInstrument

If tickTimeFromEvent is null:

Replay MUST synthesize deterministic time:

baseTime + sequence * 1ms


Where baseTime is either:

Earliest timestamp in replay stream for that instrument, OR

Fixed constant from harness configuration.

Authoritative Time for BE

tickTimeFromEvent (or synthesized time)

No other timestamps may influence BE logic.

4. Time Determinism Contract

IEA MUST support two strictly separated clocks.

4.1 Event Clock (NowEvent())

Used for:

BE throttles

BE dedupe fallback

BE modify attempt interval

Dedup insertion timestamps

Dedup eviction cutoff

Any branch-relevant timing logic

Live:

NowEvent() = DateTimeOffset.UtcNow


Replay:

NowEvent() = event timestamp


ALL branch-relevant uses of DateTimeOffset.UtcNow MUST be replaced with NowEvent().

No exceptions.

4.2 Wall Clock (NowWall())

Used only for:

EnqueueAndWait timeouts

Worker timeout detection

Operational blocking logic

Live:

NowWall() = DateTimeOffset.UtcNow


Audit Replay:

Not exercised

Full-System Replay:

Simulated or bypassed deterministically

Wall clock logic MUST remain wall-clock based in live mode.

5. Dedup Determinism

Current non-deterministic behavior:

_processedExecutionIds.TryAdd(key, UtcNow)
EvictDedupEntries uses UtcNow cutoff


Required changes:

Replace with:

NowEvent()


Stabilize eviction:

.OrderBy(kvp => kvp.Key)


Dedup composite key MUST derive only from replay payload fields.

Dedup insertion and eviction MUST use Event Clock exclusively.

6. Unordered Iteration Stabilization

ConcurrentDictionary iteration order is undefined.

The following MUST be ordered:

TryAggregateWithExistingOrders

FindOppositeEntryIntentId

EvictDedupEntries

Required pattern:

foreach (var kvp in map.OrderBy(k => k.Key))


GetPositionState:

No ordering required (commutative sums)

Bracket state:

Must be stabilized if downstream logic depends on order

7. Off-Worker Mutation Contract

RegisterIntent and RegisterIntentPolicy occur off worker thread.

Live system guarantees ordering.

Replay harness MUST enforce:

IntentRegistered precedes ExecutionUpdate for same intent.

IntentPolicyRegistered precedes EntrySubmissionRequest.

No ExecutionUpdate references unknown intent.

Replay MUST hard-fail on violation.

No runtime concurrency changes required if harness enforces ordering.

8. Adapter Independence Requirement

Replay MUST NOT require:

NinjaTrader execution objects

NinjaTrader order objects

NinjaTrader runtime

IEA branching must depend exclusively on replay payload fields.

Adapter must provide:

Minimal replay payload path OR

Mock objects that contain only replay fields

No hidden NT behavior may influence replay.

9. OCO Determinism (Full-System Only)

Current:

Guid.NewGuid()


Audit Replay:

Not required

Full-System Replay:
Add:

EncodeEntryOco(..., string? deterministicSeed = null)


Replay MUST supply deterministicSeed.

Live default MUST remain Guid.NewGuid().

10. Determinism Definition

Replay is deterministic if:

Identical event stream produces identical:

OrderMap

IntentMap

Dedup state

BE state

Instrument block state

Any branch-relevant internal field

across 3 independent process executions.

Logs do NOT need to be identical.

Determinism is defined strictly as identical internal state snapshot hash.

11. Minimal Required Code Changes

Inject NowEvent() and NowWall() into IEA.

Replace branch-relevant UtcNow with NowEvent().

Stabilize unordered iteration with .OrderBy(kvp => kvp.Key).

Ensure dedup key derives only from replay payload.

Provide replay adapter path for execution/order payload.

(Optional Full-System) Add deterministic OCO seed.

Live trading semantics MUST remain unchanged.

All determinism MUST be injectable and default to current behavior when replay disabled.

12. Replay Acceptance Criteria

Replay is valid only if:

Identical input → identical final IEA snapshot hash across 3 independent runs.

No DateTimeOffset.UtcNow in branch-relevant paths.

No Guid.NewGuid() in branch-relevant paths.

BE throttles use Event Clock.

Dedup eviction uses Event Clock.

All unordered iteration stabilized.

Harness MUST reject:

Missing IntentRegistered

Missing IntentPolicyRegistered (when required)

Sequence gaps

Out-of-order events

Unknown intent references

13. Non-Negotiable Invariant

Replay determinism MUST NOT alter live trading semantics.

Event Clock injection MUST default to UtcNow in live mode.

Wall Clock semantics MUST remain unchanged in live mode.

No branch behavior may differ between live and replay given identical event timing inputs.

Final Verdict

Deterministic replay is achievable with localized clock injection and iteration stabilization.

Primary risk: incomplete clock replacement in BE/dedup paths.

Primary structural requirement: adapter decoupling from NT object dependency.

No architectural blocker exists.

End of Contract.