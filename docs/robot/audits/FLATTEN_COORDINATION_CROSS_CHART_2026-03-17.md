# Cross-chart flatten coordination + verify debounce (operator notes)

## Purpose

- **One active flatten owner** per `(account, canonical_broker_key)` in-process (`FlattenCoordinationTracker.Shared`).
- **Debounced verify retries**: first non-zero post-flatten window does **not** enqueue another flatten; **second consecutive** non-zero window does (unless exposure **worsens**, which bypasses debounce).
- **Fail-closed** preserved: persistent verify failure still blocks instrument / stands down; secondaries do not storm the same canonical bucket.

## Configuration (defaults)

| Item | Default | Location |
|------|---------|----------|
| Stale owner TTL (takeover) | 45s | `FlattenCoordinationTracker.DefaultStaleOwnerTtl` |
| Consecutive non-zero threshold | 2 | `FlattenCoordinationTracker.DefaultVerifyNonzeroThreshold` |
| Max verify-driven retries / episode | 4 | `FlattenCoordinationTracker.DefaultMaxVerifyRetries` (= `NinjaTraderSimAdapter` `FLATTEN_VERIFY_MAX_RETRIES`) |
| Verify window | 4s | `FLATTEN_VERIFY_WINDOW_SEC` |
| `FLATTEN_FAILED_PERSISTENT_STILL_OPEN` min spacing | 60s | `DefaultPersistentStillOpenLogCooldown` |

**Instance id:** `RobotEngine` sets `NinjaTraderSimAdapter.SetFlattenCoordinationInstanceId(_reconciliationWriterInstanceId)` so flatten ownership aligns with the engine’s reconciliation writer id. If unset, adapter falls back to `nta:<adapter#>`.

## Coordination model

1. **Enqueue gate** (`TryCoordinationGateFlattenEnqueue` → `TryRequestFlattenEnqueue`): runs for every `NtFlattenInstrumentCommand` (including verify retries via `IsVerifyRetryFlatten`).
2. **Execute guard:** `ExecuteFlattenInstrument` returns early if this instance is no longer the coordination owner (stale queued command after takeover).
3. **Episode:** `episode_id` is minted when this instance becomes owner for a new flatten episode; verify logs carry `episode_id`, `owner_instance_id`, `account`, `canonical_broker_key`, `host_chart_instrument` where available.
4. **States:** `IDLE` → `FLATTENING` (enqueue) → `VERIFYING` (after successful submit) → `IDLE` (flat confirmed) or `FAILED_PERSISTENT` (max verify retries).

## Events (interpretation)

| Event | Meaning |
|-------|---------|
| `FLATTEN_COORDINATION_OWNER_ASSIGNED` | This instance became / reaffirmed owner for the canonical key (new episode id on fresh assign; also logged after takeover as “after_stale_owner_takeover”). |
| `FLATTEN_SECONDARY_INSTANCE_SKIPPED` | Another chart/instance tried flatten/verify for the same key while an active owner exists (or verify retry with no owner episode). |
| `FLATTEN_OWNER_TAKEOVER` | Prior owner stale past TTL (or stale after `FAILED_PERSISTENT`); new instance took ownership. Payload includes `elapsed_since_last_update_sec`. |
| `FLATTEN_VERIFY_STILL_OPEN_DEBOUNCED` | First (or intermediate) non-zero verify window; **no** retry flatten enqueued yet. |
| `FLATTEN_VERIFY_EXPOSURE_WORSENED` | Canonical abs exposure increased vs prior verify snapshot; **debounce bypass**, retry flatten enqueued immediately if under max retries. |
| `FLATTEN_FAILED_PERSISTENT` | Verify retries exhausted for the episode; same fail-closed callbacks as before. |
| `FLATTEN_FAILED_PERSISTENT_STILL_OPEN` | Rate-limited reminder: owner still blocked on a persistent episode; **no** new flatten storm. |
| `FLATTEN_BROKER_FLAT_CONFIRMED` | Broker flat confirmed after verify; coordination row cleared to `IDLE`. |

**Metrics** (monotonic, in log payload and on `FlattenCoordinationTracker.Shared.Metrics`):  
`flatten_owner_assigned_total`, `flatten_secondary_skipped_total`, `flatten_owner_takeover_total`, `flatten_verify_debounced_total`, `flatten_verify_worsened_total`, `flatten_failed_persistent_total`, `flatten_failed_persistent_still_open_total`.

## Tests

```bash
dotnet run --project modules/robot/harness/Robot.Harness.csproj -- --test FLATTEN_COORDINATION_TRACKER
```

Covers: single owner + secondary skip, stale takeover, debounce, second non-zero retry, worsening bypass, persistent block + secondary skip + still-open flag.
