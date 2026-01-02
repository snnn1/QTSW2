# NinjaTrader 8 SIM Testing Guide

## Step 1: Package and Install Strategy

### Copy Strategy File
1. Copy `modules/robot/ninjatrader/RobotSimStrategy.cs` to:
   ```
   Documents\NinjaTrader 8\bin\Custom\Strategies\RobotSimStrategy.cs
   ```

2. Ensure namespace matches NT expectations:
   ```csharp
   namespace NinjaTrader.Custom.Strategies
   ```

### Reference Robot.Core.dll
1. Build Robot.Core project:
   ```powershell
   dotnet build modules/robot/core/Robot.Core.csproj
   ```

2. Copy `Robot.Core.dll` to NT8 custom bin folder:
   ```
   Documents\NinjaTrader 8\bin\Custom\Robot.Core.dll
   ```

3. In NinjaTrader, add reference:
   - Tools → References → Add → Browse to `Robot.Core.dll`

### Verify Compilation
1. Open NinjaTrader 8
2. Tools → Strategies → New Strategy
3. Search for "RobotSimStrategy"
4. Strategy should appear and compile without errors

**Deliverable**: Strategy appears in NT UI and compiles.

## Step 2: Configure Minimal SIM Test

### Strategy Parameters
- **Instrument**: ES (or MES for safety)
- **Account**: Sim101
- **Timeframe**: 1 minute
- **Date**: 2025-12-01 (or date with known intents)
- **Quantity**: 1
- **Mode**: SIM (enforced by strategy)

### Kill Switch Configuration
Ensure `configs/robot/kill_switch.json` is disabled:
```json
{
  "message": null,
  "enabled": false
}
```

### Expected Startup Logs
```
EXECUTION_MODE_SET { mode: "SIM", adapter: "NinjaTraderSimAdapter" }
SIM_ACCOUNT_VERIFIED { account_name: "Sim101" }
```

## Step 3: Real Execution Evidence

### Expected Log Flow (Chronological)

```
{"ts_utc":"2025-12-01T14:30:00Z","ts_chicago":"2025-12-01T08:30:00-06:00","event_type":"EXECUTION_MODE_SET","state":"ENGINE","data":{"mode":"SIM","adapter":"NinjaTraderSimAdapter"}}

{"ts_utc":"2025-12-01T14:30:01Z","ts_chicago":"2025-12-01T08:30:01-06:00","event_type":"SIM_ACCOUNT_VERIFIED","state":"ENGINE","data":{"account_name":"Sim101","note":"SIM account verification passed"}}

{"ts_utc":"2025-12-01T14:35:00Z","ts_chicago":"2025-12-01T08:35:00-06:00","event_type":"ORDER_SUBMIT_ATTEMPT","intent_id":"abc123def456","instrument":"ES","data":{"order_type":"ENTRY","direction":"Long","entry_price":5000.25,"quantity":1,"account":"SIM"}}

{"ts_utc":"2025-12-01T14:35:00.050Z","ts_chicago":"2025-12-01T08:35:00.050-06:00","event_type":"ORDER_SUBMIT_SUCCESS","intent_id":"abc123def456","instrument":"ES","data":{"broker_order_id":"NT_ORDER_12345","order_type":"ENTRY","direction":"Long","entry_price":5000.25,"quantity":1,"account":"SIM","order_action":"Buy","order_type_nt":"Market","order_state":"Accepted"}}

{"ts_utc":"2025-12-01T14:35:00.100Z","ts_chicago":"2025-12-01T08:35:00.100-06:00","event_type":"ORDER_ACKNOWLEDGED","intent_id":"abc123def456","instrument":"ES","data":{"broker_order_id":"NT_ORDER_12345","order_type":"ENTRY"}}

{"ts_utc":"2025-12-01T14:35:01Z","ts_chicago":"2025-12-01T08:35:01-06:00","event_type":"EXECUTION_FILLED","intent_id":"abc123def456","instrument":"ES","data":{"fill_price":5000.25,"fill_quantity":1,"broker_order_id":"NT_ORDER_12345","order_type":"ENTRY"}}

{"ts_utc":"2025-12-01T14:35:01.050Z","ts_chicago":"2025-12-01T08:35:01.050-06:00","event_type":"ORDER_SUBMIT_ATTEMPT","intent_id":"abc123def456","instrument":"ES","data":{"order_type":"PROTECTIVE_STOP","direction":"Long","stop_price":4990.00,"quantity":1,"account":"SIM"}}

{"ts_utc":"2025-12-01T14:35:01.100Z","ts_chicago":"2025-12-01T08:35:01.100-06:00","event_type":"ORDER_SUBMIT_SUCCESS","intent_id":"abc123def456","instrument":"ES","data":{"broker_order_id":"NT_ORDER_12346","order_type":"PROTECTIVE_STOP","direction":"Long","stop_price":4990.00,"quantity":1,"account":"SIM"}}

{"ts_utc":"2025-12-01T14:35:01.150Z","ts_chicago":"2025-12-01T08:35:01.150-06:00","event_type":"ORDER_SUBMIT_ATTEMPT","intent_id":"abc123def456","instrument":"ES","data":{"order_type":"TARGET","direction":"Long","target_price":5010.00,"quantity":1,"account":"SIM"}}

{"ts_utc":"2025-12-01T14:35:01.200Z","ts_chicago":"2025-12-01T08:35:01.200-06:00","event_type":"ORDER_SUBMIT_SUCCESS","intent_id":"abc123def456","instrument":"ES","data":{"broker_order_id":"NT_ORDER_12347","order_type":"TARGET","direction":"Long","target_price":5010.00,"quantity":1,"account":"SIM"}}

{"ts_utc":"2025-12-01T14:35:01.250Z","ts_chicago":"2025-12-01T08:35:01.250-06:00","event_type":"PROTECTIVE_ORDERS_SUBMITTED","intent_id":"abc123def456","instrument":"ES","data":{"stop_order_id":"NT_ORDER_12346","target_order_id":"NT_ORDER_12347","stop_price":4990.00,"target_price":5010.00,"quantity":1}}
```

**Key Evidence Points**:
- Real NT order IDs: `NT_ORDER_12345`, `NT_ORDER_12346`, `NT_ORDER_12347`
- Entry fill: `EXECUTION_FILLED` with fill_price and broker_order_id
- Protective orders submitted after fill: STOP and TARGET orders follow immediately after fill

## Step 4: ExecutionJournal Output

### Expected Journal Entry
**File**: `data/execution_journals/2025-12-01_ES1_abc123def456.json`

```json
{
  "intent_id": "abc123def456",
  "trading_date": "2025-12-01",
  "stream": "ES1",
  "instrument": "ES",
  "entry_submitted": true,
  "entry_submitted_at": "2025-12-01T14:35:00.050Z",
  "entry_filled": true,
  "entry_filled_at": "2025-12-01T14:35:01Z",
  "broker_order_id": "NT_ORDER_12345",
  "entry_order_type": "ENTRY",
  "fill_price": 5000.25,
  "fill_quantity": 1,
  "rejected": false
}
```

**Key Fields**:
- `intent_id`: Hash of canonical intent fields
- `broker_order_id`: Real NT order ID
- `entry_submitted`: true
- `entry_filled`: true
- State transitions: SUBMITTED → FILLED

## Step 5: Restart Idempotency Test

### Procedure
1. Run strategy until entry order submitted
2. Stop strategy (or restart NT)
3. Start strategy again same session/day

### Expected Logs (Second Run)
```
{"ts_utc":"2025-12-01T14:40:00Z","ts_chicago":"2025-12-01T08:40:00-06:00","event_type":"EXECUTION_SKIPPED_DUPLICATE","intent_id":"abc123def456","instrument":"ES","data":{"reason":"INTENT_ALREADY_SUBMITTED","trading_date":"2025-12-01","stream":"ES1","direction":"Long","entry_price":5000.25}}
```

**Key Evidence**:
- `EXECUTION_SKIPPED_DUPLICATE` logged
- No new entry order submitted
- Journal check prevents duplicate submission

## Step 6: Kill Switch Test

### Procedure
1. Enable kill switch:
   ```json
   {
     "message": "SIM smoke test - kill switch enabled",
     "enabled": true
   }
   ```

2. Start strategy

### Expected Logs
```
{"ts_utc":"2025-12-01T14:45:00Z","ts_chicago":"2025-12-01T08:45:00-06:00","event_type":"EXECUTION_BLOCKED","intent_id":"abc123def456","instrument":"ES","data":{"reason":"KILL_SWITCH_ACTIVE","note":"Order submission blocked by risk gate"}}
```

**Key Evidence**:
- `EXECUTION_BLOCKED` with reason `KILL_SWITCH_ACTIVE`
- Zero orders submitted
- No `ORDER_SUBMIT_ATTEMPT` logs after kill switch enabled

### Reset
Disable kill switch after test:
```json
{
  "message": null,
  "enabled": false
}
```

## Step 7: Execution Summary

### Expected Summary JSON
**File**: `data/execution_summaries/summary_20251201143500.json`

```json
{
  "intents_seen": 1,
  "intents_executed": 1,
  "orders_submitted": 3,
  "orders_rejected": 0,
  "orders_filled": 1,
  "orders_blocked": 0,
  "blocked_by_reason": {},
  "duplicates_skipped": 0,
  "intent_details": [
    {
      "intent_id": "abc123def456",
      "trading_date": "2025-12-01",
      "stream": "ES1",
      "instrument": "ES",
      "executed": true,
      "orders_submitted": 3,
      "orders_rejected": 0,
      "orders_filled": 1,
      "order_types": ["ENTRY", "STOP", "TARGET"],
      "rejection_reasons": [],
      "blocked": false,
      "duplicate_skipped": false
    }
  ]
}
```

**Summary Counts**:
- `intents_seen`: 1
- `intents_executed`: 1
- `orders_submitted`: 3 (entry + stop + target)
- `orders_rejected`: 0
- `orders_filled`: 1 (entry fill)
- `orders_blocked`: 0
- `duplicates_skipped`: 0 (or 1 if restart test included)

## Testing Checklist

- [ ] Strategy compiles in NT8
- [ ] Strategy appears in NT UI
- [ ] SIM account verified
- [ ] Entry order submitted with real NT order ID
- [ ] Entry order filled
- [ ] Stop order submitted after fill
- [ ] Target order submitted after fill
- [ ] ExecutionJournal records all order IDs
- [ ] Restart prevents duplicate submission
- [ ] Kill switch blocks all execution
- [ ] Execution summary generated

---

**Note**: This guide provides expected outputs. Actual NT8 testing requires running the strategy inside NinjaTrader 8.
