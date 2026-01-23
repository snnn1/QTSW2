# RANGE_COMPUTE_FAILED Reason Codes

**Last Updated**: 2026-01-22

## Overview

The `RANGE_COMPUTE_FAILED` event now includes standardized reason codes that categorize failures as either **benign** (expected, not actionable) or **actionable** (require investigation).

## Reason Code Categories

### Benign Failures (Expected, Not Actionable)

These failures are normal during range computation and indicate the system is waiting for data or alignment:

| Reason Code | Description | When It Occurs |
|-------------|-------------|----------------|
| `NO_BARS_YET` | No bars in buffer at all | Early in session, before any bars arrive |
| `NO_BARS_IN_WINDOW` | Bars exist but none in range window | Bars present but outside expected time window |
| `OUTSIDE_RANGE_WINDOW` | Bars from correct date but outside time window | Bars exist but don't fall within `[range_start, slot_time)` |
| `BARS_FROM_WRONG_DATE` | Bars exist but from different trading date | Bars present but from wrong trading date (date mismatch) |
| `INSUFFICIENT_BARS` | Less than 3 bars available | Not enough bars for reliable range computation (need at least 3) |

**Action**: No action needed. The system will retry on the next tick or bar arrival.

### Actionable Failures (Require Investigation)

These failures indicate potential data corruption or logic issues:

| Reason Code | Description | When It Occurs |
|-------------|-------------|----------------|
| `INVALID_RANGE_HIGH_LOW` | Range high < range low (data corruption) | Invalid price data (high < low) |
| `NO_FREEZE_CLOSE` | Cannot determine freeze close price | Logic error - no bar close found before slot time |

**Action**: Investigate data quality or logic issues. These should be rare.

## Event Structure

```json
{
  "ts_utc": "2026-01-22T23:04:18.0000000+00:00",
  "level": "ERROR",
  "source": "StreamStateMachine",
  "event": "RANGE_COMPUTE_FAILED",
  "data": {
    "range_start_utc": "2026-01-22T18:00:00.0000000+00:00",
    "range_end_utc": "2026-01-22T22:00:00.0000000+00:00",
    "reason": "NO_BARS_YET",
    "reason_category": "BENIGN",
    "bar_count": 0,
    "message": "Range computation failed - will retry on next tick or use partial data",
    "note": "Benign failure - waiting for bars or data alignment (rate-limited to once per minute)"
  }
}
```

## Filtering in Summaries

### Filter Benign Failures

```python
# Only show actionable failures
actionable_failures = [
    e for e in events 
    if e.get('event') == 'RANGE_COMPUTE_FAILED' 
    and e.get('data', {}).get('reason_category') == 'ACTIONABLE'
]
```

### Filter by Specific Reason

```python
# Show only "waiting for bars" scenarios
waiting_scenarios = [
    e for e in events 
    if e.get('event') == 'RANGE_COMPUTE_FAILED'
    and e.get('data', {}).get('reason') in ['NO_BARS_YET', 'NO_BARS_IN_WINDOW', 'INSUFFICIENT_BARS']
]
```

## Usage in Log Analysis

### Example: Count by Category

```python
from collections import Counter

failures = [e for e in events if e.get('event') == 'RANGE_COMPUTE_FAILED']
categories = Counter([e.get('data', {}).get('reason_category', 'UNKNOWN') for e in failures])
reasons = Counter([e.get('data', {}).get('reason', 'UNKNOWN') for e in failures])

print("By Category:")
for cat, count in categories.items():
    print(f"  {cat}: {count}")

print("\nBy Reason:")
for reason, count in reasons.most_common():
    print(f"  {reason}: {count}")
```

### Example: Alert on Actionable Failures

```python
actionable = [
    e for e in events 
    if e.get('event') == 'RANGE_COMPUTE_FAILED'
    and e.get('data', {}).get('reason_category') == 'ACTIONABLE'
]

if actionable:
    print(f"⚠️  Found {len(actionable)} actionable RANGE_COMPUTE_FAILED events:")
    for e in actionable:
        reason = e.get('data', {}).get('reason')
        instrument = e.get('instrument', '')
        print(f"  {instrument}: {reason}")
```

## Defensive Guardrails

### Invariant: ACTIONABLE = ERROR Level

**Hard Contract**: All ACTIONABLE failures MUST be logged at ERROR level. This is enforced with:

1. **Debug Assertion**: `System.Diagnostics.Debug.Assert()` verifies the level at compile-time
2. **Runtime Check**: Production code validates the level and logs `LOGGING_INVARIANT_VIOLATION` if violated

**Rationale**: Prevents accidental downgrades in future refactors. Makes "ACTIONABLE = ERROR" a hard contract that cannot be accidentally broken.

**Implementation**: The guardrail checks `RobotEventTypes.GetLevel("RANGE_COMPUTE_FAILED")` and ensures it returns `"ERROR"` when logging ACTIONABLE failures.

## Migration Notes

- **Existing logs**: Older logs may have `reason: null` or generic reasons. The `reason_category` field helps distinguish.
- **Backward compatibility**: Code handles `reason: null` gracefully (categorized as "UNKNOWN").
- **Rate limiting**: All `RANGE_COMPUTE_FAILED` events are rate-limited to once per minute per stream.
- **Invariant protection**: ACTIONABLE failures are protected by debug assertions and runtime checks to ensure ERROR level logging.

## Related Events

- `RANGE_COMPUTE_START` - Range computation attempt started
- `RANGE_COMPUTE_NO_BARS_DIAGNOSTIC` - Detailed diagnostic when no bars found
- `RANGE_COMPUTE_BAR_FILTERING` - Bar filtering details (diagnostic)
- `RANGE_LOCKED` - Range successfully computed and locked
