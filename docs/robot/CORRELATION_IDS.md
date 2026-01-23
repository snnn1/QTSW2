# Correlation IDs and Traceability

## Overview

Correlation IDs (`run_id`) enable tracing events across components and correlating related operations (e.g., order submission → acknowledgment → fill).

## Current State

**Status**: ⚠️ Partial Implementation

- `run_id` field exists in `RobotLogEvent` schema
- `run_id` is set in some events (engine-level)
- `run_id` is **not consistently propagated** through call chains
- Cannot trace order lifecycle end-to-end

## Recommendations

### 1. Set `run_id` at Engine Start

```csharp
// RobotEngine.cs - OnStart()
var runId = Guid.NewGuid().ToString("N").Substring(0, 16);
// Store in _runId field
// Include in all LogEvent() calls
```

### 2. Propagate `run_id` Through Call Chains

- Pass `run_id` as parameter to stream state machine methods
- Include `run_id` in execution adapter calls
- Store `run_id` in execution journal entries

### 3. Add Request ID for Broker API Calls

- Generate `request_id` for each broker API call
- Include in request/response logging
- Correlate with order events

## Implementation Priority

**Medium Priority** - Improves observability but not critical for basic operation.

## See Also

- [LOGGING_ASSESSMENT_REPORT.md](LOGGING_ASSESSMENT_REPORT.md) - Observability section
