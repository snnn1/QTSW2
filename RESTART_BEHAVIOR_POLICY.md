# Restart-Within-Session Behavior Policy

## Problem Statement

When the strategy restarts mid-session, the BarsRequest API loads historical bars that may differ from what would have been available during uninterrupted operation. This creates ambiguity about whether restart should:

1. **Full Reconstruction**: Load all available historical bars and recompute range (current behavior)
2. **Stream Invalidation**: Mark stream as invalidated if restart occurs after slot time

## Current Policy: "Restart = Full Reconstruction"

### Decision

**The system implements "Restart = Full Reconstruction" policy.**

When a strategy restarts mid-session:
- Historical bars are loaded from `range_start` to `min(slot_time, now)`
- Range is recomputed from all available bars (historical + live)
- Stream continues to operate normally
- Result may differ from uninterrupted operation if restart occurs after slot time

### Rationale

1. **Recovery from Crashes**: Allows system to recover from unexpected shutdowns
2. **Deterministic Reconstruction**: Same restart time produces same result
3. **Operational Flexibility**: Operators can restart strategy without losing the trading day
4. **Trade-off Accepted**: Same day may produce different results depending on restart timing

### Implementation Details

#### BarsRequest Limitation

When restarting after slot time, BarsRequest is **limited to slot_time** (not current time):

```csharp
// If restart occurs after slot_time, only request up to slot_time
var endTimeChicago = nowChicago < slotTimeChicagoTime
    ? nowChicago.ToString("HH:mm")
    : slotTimeChicago; // Never request beyond slot_time
```

**Why**: Prevents loading bars beyond the range window, which would change the input set compared to uninterrupted operation.

#### Detection and Logging

The system detects mid-session restarts and logs them:

**Event**: `MID_SESSION_RESTART_DETECTED`
- Triggered when: Journal exists, stream not committed, restart occurs after `range_start`
- Logs: Previous state, restart time, policy applied, warning about potential differences

**Example Log Entry**:
```json
{
  "event": "MID_SESSION_RESTART_DETECTED",
  "trading_date": "2026-01-16",
  "previous_state": "RANGE_LOCKED",
  "restart_time_chicago": "2026-01-16T08:15:00-06:00",
  "range_start_chicago": "2026-01-16T02:00:00-06:00",
  "slot_time_chicago": "2026-01-16T07:30:00-06:00",
  "policy": "RESTART_FULL_RECONSTRUCTION",
  "note": "Mid-session restart detected - will reconstruct range from historical + live bars. Result may differ from uninterrupted operation."
}
```

### Scenarios

#### Scenario 1: Restart Before Slot Time

**Timeline**:
- Strategy stops at 07:10
- Strategy restarts at 07:15
- Slot time: 07:30

**Behavior**:
- BarsRequest loads: 02:00 → 07:15 (up to current time)
- Range computed at 07:30 from bars 02:00 → 07:30
- **Result**: Same as uninterrupted operation ✓

#### Scenario 2: Restart After Slot Time (Range Not Yet Computed)

**Timeline**:
- Strategy stops at 07:25 (before slot time)
- Strategy restarts at 08:10 (after slot time)
- Slot time: 07:30

**Behavior**:
- BarsRequest loads: 02:00 → 07:30 (limited to slot_time, not 08:10)
- Range computed immediately from bars 02:00 → 07:30
- **Result**: Same as uninterrupted operation ✓

#### Scenario 3: Restart After Slot Time (Range Already Computed)

**Timeline**:
- Strategy runs continuously: Range computed at 07:30 from bars 02:00 → 07:30
- Strategy stops at 08:10
- Strategy restarts at 08:15

**Behavior**:
- BarsRequest loads: 02:00 → 07:30 (limited to slot_time)
- Range recomputed from bars 02:00 → 07:30
- **Result**: Same as uninterrupted operation ✓

**Note**: If range was already computed and committed, stream would be in `DONE` state and not restart.

### Alternative Policy (Not Implemented): "Restart Invalidates Stream"

**If implemented**, this policy would:
- Detect restart after slot time
- If range already computed: Mark stream as invalidated
- If range not yet computed: Allow reconstruction (same as current policy)
- **Trade-off**: Safer (prevents discrepancies) but less flexible (loses trading day on restart)

**Why Not Implemented**:
- Too restrictive for operational needs
- Crashes would lose entire trading day
- Full reconstruction is deterministic and auditable

## Invariants

1. **BarsRequest Never Exceeds Slot Time**: Historical bars are never loaded beyond `slot_time`, even if restart occurs after slot time
2. **Deterministic Reconstruction**: Same restart time produces same result
3. **Auditable Differences**: All restarts are logged with full context
4. **No Silent Failures**: Restart behavior is explicit and documented

## Logging Requirements

The system logs:
- `MID_SESSION_RESTART_DETECTED`: When restart is detected mid-session
- `RESTART_POLICY`: When BarsRequest is limited to slot_time
- Previous state and restart time for auditability

## Future Considerations

If operational experience shows that restart discrepancies are problematic:
1. Consider implementing "Restart Invalidates Stream" policy
2. Add configuration option to choose policy
3. Add validation to detect and warn about discrepancies

## Summary

**Current Policy**: Restart = Full Reconstruction
- **Pros**: Recovery from crashes, operational flexibility, deterministic
- **Cons**: Same day may produce different results depending on restart timing
- **Mitigation**: BarsRequest limited to slot_time, full logging, explicit policy

**Ambiguity Resolved**: ✅ Policy is documented and enforced
