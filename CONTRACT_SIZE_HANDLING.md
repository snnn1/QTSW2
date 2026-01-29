# How the Robot Handles Contract Sizes

## Overview

The robot uses a **two-tier instrument system**:
1. **Canonical Instrument**: Used for logic/analysis (ES, NQ, RTY, etc.)
2. **Execution Instrument**: Used for actual order placement (MES, MNQ, M2K, etc.)

This allows the robot to trade micro contracts (1/10th size) while maintaining logic parity with full-size contracts.

## Instrument Architecture

### Canonical vs Execution Instruments

| Canonical Instrument | Execution Instrument | Relationship |
|---------------------|---------------------|--------------|
| ES (E-mini S&P 500) | MES (Micro E-mini) | MES = 1/10th ES |
| NQ (E-mini Nasdaq) | MNQ (Micro Nasdaq) | MNQ = 1/10th NQ |
| RTY (Russell 2000) | M2K (Micro Russell) | M2K = 1/10th RTY |
| YM (E-mini Dow) | MYM (Micro Dow) | MYM = 1/10th YM |
| CL (Crude Oil) | MCL (Micro Crude) | MCL = 1/10th CL |
| GC (Gold) | MGC (Micro Gold) | MGC = 1/10th GC |
| NG (Natural Gas) | MNG (Micro Natural Gas) | MNG = 1/10th NG |

### Key Properties

**Micro Contracts:**
- `scaling_factor: 0.1` (1/10th size)
- `is_micro: true`
- `base_instrument`: Points to canonical instrument
- Same tick sizes as canonical instrument
- Same target ladders as canonical instrument
- **Profit calculated as 1/10th** of canonical equivalent

**Full-Size Contracts:**
- `scaling_factor: 1.0` (full size)
- `is_micro: false`
- `base_instrument`: Same as instrument name
- Used for logic/analysis, typically not traded

## Contract Size Configuration

### Execution Policy File (`configs/execution_policy.json`)

The execution policy file defines:
- Which execution instrument to use for each canonical market
- Order quantity (`base_size`)
- Maximum quantity (`max_size`)

**Example Configuration:**
```json
{
  "canonical_markets": {
    "RTY": {
      "execution_instruments": {
        "RTY": {
          "enabled": false,
          "base_size": 2,
          "max_size": 2
        },
        "M2K": {
          "enabled": true,
          "base_size": 2,
          "max_size": 2
        }
      }
    }
  }
}
```

**Current Policy Configuration:**
- **RTY → M2K**: `base_size: 2`, `max_size: 2` (configured for 2 micro contracts)
- **ES → MES**: `base_size: 2`, `max_size: 2` (configured for 2 micro contracts)
- **NQ → MNQ**: `base_size: 1`, `max_size: 1` (configured for 1 micro contract)
- **YM → MYM**: `base_size: 2`, `max_size: 2` (configured for 2 micro contracts)

**⚠️ IMPORTANT: Policy vs Actual Quantity Discrepancy**

**Current Situation:**
- **Policy file** (`configs/execution_policy.json`): Shows `base_size: 2` for M2K
- **Actual orders**: Show `quantity: 1` 
- **Logs confirm**: `policy_base_size: "1"` and `expected_qty: "1"` when streams were created

**Root Cause:**
The execution policy file was changed from `base_size: 1` to `base_size: 2` **after** the streams were already created. 

**Why This Happens:**
- Streams store quantity in `_orderQuantity` when created (from policy at startup)
- Policy is only read at robot startup when streams are initialized
- Changing the policy file doesn't affect existing streams until robot restart
- Streams must be recreated (robot restart) for new policy values to take effect

**To Fix:**
1. Restart the robot to recreate streams with new policy values
2. New streams will use `base_size: 2` from current policy
3. Existing streams will continue using `quantity: 1` until restart

**Verification:**
Check logs for `INTENT_POLICY_REGISTERED` events - they show what quantity was actually used:
- `expected_qty: "1"` = Stream created with old policy (quantity 1)
- `expected_qty: "2"` = Stream created with new policy (quantity 2)

### How Quantities Are Determined

1. **Timetable specifies canonical instrument** (e.g., `RTY`)
2. **Execution policy maps to execution instrument** (e.g., `RTY → M2K`)
3. **Policy provides quantity** (`base_size: 2` means 2 contracts)
4. **Robot places orders** using execution instrument with policy quantity

**Example Flow:**
```
Timetable: RTY1 stream, instrument: RTY
    ↓
Execution Policy: RTY → M2K (enabled: true, base_size: 2)
    ↓
Robot places orders: 2 contracts of M2K
    ↓
Logic calculations: Based on RTY (canonical)
    ↓
Profit calculation: M2K profit = RTY profit × 0.1 (scaling factor)
```

## Contract Multipliers

### Point Values (Dollars per Point)

| Instrument | Point Value | Micro Equivalent |
|------------|-------------|------------------|
| ES | $50.00 | MES: $5.00 |
| NQ | $10.00 | MNQ: $2.00 |
| RTY | $50.00 | M2K: $5.00 |
| YM | $5.00 | MYM: $0.50 |
| CL | $1,000.00 | MCL: $100.00 |
| GC | $100.00 | MGC: $10.00 |
| NG | $10,000.00 | MNG: $1,000.00 |

### How Contract Multiplier is Used

1. **Slippage Calculation**: `SlippageDollars = SlippagePoints × ContractMultiplier × Quantity`
2. **Profit Calculation**: Uses scaling factor (0.1 for micro) rather than contract multiplier
3. **Position Value**: NinjaTrader automatically handles contract multiplier

**Example:**
- RTY trade: 10 points profit
- M2K execution: 2 contracts
- Profit calculation: `10 points × 0.1 (scaling) × 2 contracts = 2.0 points equivalent`
- Dollar value: `2.0 points × $5.00/point (M2K) × 2 contracts = $20.00`

## Order Quantity Handling

### Fixed Quantity Per Stream

- **Quantity is fixed** per stream (from execution policy)
- **Not dynamic** - doesn't scale with account size or risk
- **Same quantity** for all orders (entry, stop, target)
- **Policy-controlled** - changes require policy file update

### Quantity Validation

The robot validates quantities before order submission:

1. **Pre-submission checks:**
   - Quantity > 0
   - Quantity ≤ `max_size` (from policy)
   - Cumulative filled ≤ `expected_quantity` (from policy)
   - Quantity ≤ remaining allowed (expected - filled)

2. **Hard blocks** (order rejected):
   - Invalid quantity (≤ 0)
   - Already overfilled
   - Quantity exceeds max_size
   - Quantity exceeds remaining allowed

3. **Warnings** (order allowed but logged):
   - Quantity mismatch (requested ≠ expected, but ≤ expected)

### Partial Fills

- Robot **completes the fill** (no cancel remainder)
- Protective orders are **sized to filled quantity**
- OCO integrity preserved for filled quantity
- Example: Order 2 contracts, fill 1 → protective orders for 1 contract

## Profit Calculation

### Scaling Factor Application

**Micro contracts use scaling factor 0.1:**

```python
# Analyzer calculates profit in canonical instrument points
canonical_profit = 10.0  # RTY points

# Robot execution uses micro contract
execution_instrument = "M2K"
scaling_factor = 0.1

# Profit calculation
micro_profit = canonical_profit * scaling_factor
# = 10.0 * 0.1 = 1.0 points (M2K equivalent)
```

**Dollar Value:**
```python
# M2K point value: $5.00 per point
# Quantity: 2 contracts
dollar_profit = micro_profit * point_value * quantity
# = 1.0 * $5.00 * 2 = $10.00
```

### Parity with Analyzer

- **Analyzer**: Calculates profit in canonical instrument points
- **Robot**: Executes in micro contracts, scales profit by 0.1
- **Result**: Same logic, smaller dollar exposure

## Code References

### Key Files

1. **Execution Policy**: `configs/execution_policy.json`
   - Defines execution instrument mapping
   - Sets base_size and max_size

2. **Instrument Config**: `modules/analyzer/logic/instrument_logic.py`
   - Defines scaling_factor, tick_size, target_ladder
   - Maps micro to canonical instruments

3. **Stream State Machine**: `modules/robot/core/StreamStateMachine.cs`
   - Sets ExecutionInstrument from policy
   - Uses policy base_size for order quantity

4. **Execution Adapter**: `modules/robot/core/Execution/NinjaTraderSimAdapter.cs`
   - Validates quantities against policy
   - Uses contract multiplier for slippage calculation

5. **Robot Engine**: `modules/robot/core/RobotEngine.cs`
   - Loads execution policy
   - Resolves execution instrument from canonical

### Key Methods

- `ExecutionPolicy.GetExecutionInstrumentPolicy()`: Gets policy for canonical→execution mapping
- `RobotEngine.ResolveExecutionInstrument()`: Maps canonical to execution instrument
- `StreamStateMachine.SetExecutionInstrument()`: Sets execution instrument on stream
- `NinjaTraderSimAdapter.SubmitStopEntryOrder()`: Validates quantity before submission

## Example: RTY1 Stream

**Configuration:**
- Canonical: RTY
- Execution: M2K (enabled: true)
- Base Size: 2 contracts
- Max Size: 2 contracts

**Order Placement:**
- Entry orders: 2 contracts M2K (Long @ 2675.9, Short @ 2660.9)
- If Long fills: Protective stop @ 2661.0 (2 contracts), Target @ 2686.0 (2 contracts)

**Profit Calculation:**
- Range: 2661.0 - 2675.8 = 14.8 points (RTY)
- Target: 10 points (RTY)
- If target hits: `10 points × 0.1 (scaling) × 2 contracts = 2.0 points (M2K)`
- Dollar value: `2.0 × $5.00 × 2 = $20.00`

## Summary

1. **Two-tier system**: Canonical (logic) vs Execution (trading)
2. **Micro contracts**: 1/10th size, same logic, smaller exposure
3. **Policy-controlled**: Quantities set in execution_policy.json
4. **Fixed quantities**: Same size for all orders per stream
5. **Scaling factor**: Profit calculated as canonical × 0.1 for micro
6. **Contract multiplier**: Used for slippage, not profit calculation
7. **Validation**: Hard blocks prevent overfilling or invalid quantities
