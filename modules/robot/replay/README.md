# Robot.Replay — IEA Deterministic Replay Module

**Phase 1 deliverable.** Pure library component. No IEA wiring. No NinjaTrader dependency.

## Purpose

- Define canonical replay event schema (POCOs)
- Load and validate JSONL replay files
- Fail-fast on ordering violations, sequence gaps, precondition violations

## Reference

- [IEA_REPLAY_CONTRACT.md](../../../docs/robot/IEA_REPLAY_CONTRACT.md)
- [REPLAY_PHASE_0_TARGET.md](../../../docs/robot/REPLAY_PHASE_0_TARGET.md)

## Structure

| File | Purpose |
|------|---------|
| `ReplayEventEnvelope.cs` | Wrapper: source, sequence, executionInstrumentKey, type, payload |
| `ReplayIntent.cs` | Branch-relevant intent fields |
| `ReplayIntentRegistered.cs` | IntentRegistered payload |
| `ReplayIntentPolicyRegistered.cs` | IntentPolicyRegistered payload |
| `ReplayExecutionUpdate.cs` | ExecutionUpdate payload |
| `ReplayOrderUpdate.cs` | OrderUpdate payload |
| `ReplayTick.cs` | Tick/MarketData payload |
| `ReplayLoader.cs` | Load + validate JSONL; fail-fast |
| `ReplayLoadException.cs` | Validation failure exception |
| `sample_replay.jsonl` | Sample replay file |

## Usage

```bash
# Run loader self-test
dotnet run --project modules/robot/replay/Robot.Replay.csproj -- --test-loader [path]

# Print state checksum (binary determinism proof)
dotnet run --project modules/robot/replay/Robot.Replay.csproj -- --checksum --file path/to/events.jsonl

# Determinism test: run twice, compare hashes. Exit 1 if mismatch.
dotnet run --project modules/robot/replay/Robot.Replay.csproj -- --determinism-test --file path/to/events.jsonl
```

## Determinism Harness

**Per-step IEA test:** `--determinism-test` validates JSONL → writes canonical file → spawns net48 ReplayHost → runs IEA twice, compares per-step snapshot hashes. Exit 1 on divergence.

**Output (machine-parseable):**
```
CANONICAL:sha256=<input-fingerprint>
HOST:version=<commit-or-dev>
DETERMINISM:PASS
DETERMINISM:steps=N
```

**Golden files:** `sample_replay.jsonl` (short), `golden_medium.jsonl` (99 events), `golden_long.jsonl` (999 events). Generate more: `python scripts/generate_replay.py N out.jsonl`.

**CI:** `.github/workflows/replay-determinism.yml` runs determinism test on push/PR. Set `REPLAY_HOST_VERSION` (e.g. `$GITHUB_SHA`) for traceability.

## Incident Replay Packs

Incident packs are portable, deterministic slices for incident-driven debugging. Fixes must land in shared Core logic (IEA), not replay glue.

### Extract an incident pack

```bash
dotnet run --project modules/robot/replay/Robot.Replay.csproj -- \
  --extract-incident --from <day.jsonl> --out modules/robot/replay/incidents/<INCIDENT_ID>
```

**Selectors:** `--error-event-type <TYPE>`, `--message-contains <substr>`, `--instrument <key>`, `--account <name>`

**Window:** `--pre-events N` (default 200), `--post-events N` (default 200)

Output: `events.jsonl`, `canonical.json`, `metadata.json`, `expected.json` (stub).

### Add invariants to expected.json

Edit `incidents/<ID>/expected.json`:

```json
{
  "invariants": [
    { "id": "no-dup", "type": "NO_DUPLICATE_EXECUTION_PROCESSED", "params": {}, "expected": {} },
    { "id": "policy", "type": "INTENT_REQUIRES_POLICY_BEFORE_SUBMISSION", "params": {}, "expected": {} },
    { "id": "protected", "type": "NO_UNPROTECTED_POSITION", "params": { "max_events_unprotected": 50 }, "expected": {} },
    { "id": "be-crossed", "type": "BE_PRICE_CROSSED_BY_STEP", "params": { "intent_id": "intent_001", "latest_step_index": 3 }, "expected": {} },
    { "id": "be-triggered", "type": "BE_TRIGGERED_BY_STEP", "params": { "intent_id": "intent_001", "latest_step_index": 3 }, "expected": {} }
  ]
}
```

**Failure output** includes `reason=` for classification: `DUPLICATE_EXECUTION`, `INTENT_WITHOUT_POLICY`, `UNPROTECTED_TIMEOUT`, `BE_PRICE_NOT_CROSSED`, `BE_NOT_TRIGGERED`, `INVALID_PARAMS`.

### Run locally

```bash
# Determinism + invariants
dotnet run --project modules/robot/replay_host/Robot.ReplayHost.csproj -- \
  --determinism-test --file incidents/<ID>/canonical.json \
  --run-invariants --expected incidents/<ID>/expected.json
```

### CI

The `incident-packs` job discovers all packs under `modules/robot/replay/incidents/*/`, runs determinism + invariants for each, and fails on divergence or invariant failure.

## JSONL Format

Each line is a JSON object:

```json
{"source":"MNQ","sequence":0,"executionInstrumentKey":"MNQ","type":"IntentRegistered","payload":{...}}
```

- `source`, `sequence`, `executionInstrumentKey`, `type` — required envelope fields
- `payload` — event-specific object (see contract §3)

## Validation Rules

- Ordering: `(source, sequence)` strictly ascending
- Sequence: no gaps per source
- Precondition: IntentRegistered must precede ExecutionUpdate/OrderUpdate referencing that intent
- Required fields validated per event type

## Next Phases

- Phase 2: Clock injection (NowEvent/NowWall)
- Phase 3: Iteration stabilization
- Phase 4: Replay entry points (decouple from NT types)
- Phase 5: IEAReplayRunner
- Phase 6: Snapshot hashing
- Phase 7: CLI, gating
