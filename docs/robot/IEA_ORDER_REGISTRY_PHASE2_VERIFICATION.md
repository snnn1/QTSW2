# Phase 2 Pre-Close Verification

## 1. Flatten Loops from Integrity Failures

**Status**: Verified with one fix required.

| Event | Triggers Flatten? | Loop Risk |
|-------|------------------|-----------|
| `REGISTRY_BROKER_DIVERGENCE` | **No** — only logs | None. Integrity check does not request flatten. |
| `UNOWNED_LIVE_ORDER_DETECTED` | Yes (once per scan) | **Low**. ScanAndAdopt runs once per restart. Per-instrument flatten latch prevents concurrent duplicate flatten. |
| `MANUAL_OR_EXTERNAL_ORDER_DETECTED` | Yes (once per order) | **None**. After first detection we register as UNOWNED; subsequent order updates resolve via registry and skip the flatten path. |

**Flatten latch**: Per-instrument `TryAdd` rejects duplicate flatten when one is in progress. Account-flat check skips order submission. No repeated flatten storm from heartbeat.

**Fix applied**: UNOWNED_LIVE_ORDER was calling `RequestFlatten` directly from IEA worker (wrong thread). Changed to `Executor.EnqueueNtAction(NtFlattenInstrumentCommand)` so flatten runs on strategy thread.

---

## 2. Registry Cleanup Cannot Erase Useful Forensic State

**Current**: 10-minute terminal retention.

**Considerations**:
- **Delayed broker events**: 10 min may be tight for very late fills.
- **Post-incident review**: 10 min in-memory only; cleanup removes entries before longer review.
- **Restart edge cases**: Terminal orders from before restart are cleaned; journal remains source of truth.

**Recommendation**: Document that forensic retention is separate from registry cleanup:
- **Short in-memory retention** (10 min): Registry cleanup for operational hygiene.
- **Longer persisted event trail**: `ORDER_REGISTRY_*` events already go to log; no cleanup of those.
- **Phase 3**: Consider configurable retention or optional persistence of terminal entries for audit.

**Conclusion**: 10 min is acceptable for Phase 2. The design doc now notes forensic considerations.

---

## 3. EXECUTION_UNOWNED Must Remain High-Severity

**Status**: Verified.

| Event | Severity | Location |
|-------|----------|----------|
| `EXECUTION_UNOWNED` | **CRITICAL** | RobotEventTypes.cs |
| `UNOWNED_LIVE_ORDER_DETECTED` | **CRITICAL** | RobotEventTypes.cs |
| `MANUAL_OR_EXTERNAL_ORDER_DETECTED` | **CRITICAL** | RobotEventTypes.cs |
| `REGISTRY_BROKER_DIVERGENCE` | **CRITICAL** | RobotEventTypes.cs |

All ownership-related anomaly events are CRITICAL. No degradation to WARN/INFO.

---

## 4. Adoption Matching Logic Must Be Narrowly Defined

**Status**: Verified and documented.

**Current adoption criteria** (all required):

1. **Tag**: `tag.StartsWith("QTSW2:", StringComparison.OrdinalIgnoreCase)`
2. **Leg**: `tag.EndsWith(":STOP")` or `tag.EndsWith(":TARGET")` — no entry orders, no generic QTSW2
3. **IntentId**: `RobotOrderIds.DecodeIntentId(tag)` non-empty
4. **Active intent**: `activeIntentIds.Contains(intentId)` — intent must be in `GetActiveIntentsForBEMonitoring` (journal says entry filled, intent not completed)
5. **Order state**: `OrderState.Working` or `OrderState.Accepted`
6. **Not already in OrderMap**: `!OrderMap.TryGetValue(mapKey, out _)`

**Exclusions**:
- Orders without QTSW2 tag → ignored
- QTSW2 entry orders (no :STOP/:TARGET) → not adopted
- Intent not in active set → UNOWNED
- Empty intentId from tag → UNOWNED

**Conclusion**: Tight, documented criteria. No loose similarity matching.
