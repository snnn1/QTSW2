# MGC and M2K Issue Diagnosis

## Problems Identified

### 1. M2K - Instrument Resolution Failure

**Issue**: `Instrument.GetInstrument("M2K")` returns `null` - NinjaTrader cannot resolve the instrument name.

**Evidence from logs**:
- Multiple `INSTRUMENT_RESOLUTION_FAILED` events
- Error: "Could not resolve Instrument from string, using strategy instrument as fallback"
- Requested: "M2K"
- Had whitespace: False (so not a whitespace issue)

**Root Cause**: NinjaTrader's `Instrument.GetInstrument()` method cannot find an instrument named "M2K". This could mean:
- The instrument name format is wrong (might need contract month, e.g., "M2K 03-26")
- The instrument is not available in the SIM account
- NinjaTrader uses a different symbol name for Micro Russell 2000

**Current Behavior**: Code falls back to using strategy's instrument, but if strategy isn't running on M2K, orders will fail.

### 2. MGC - Order Rejection Without Detailed Error

**Issue**: Orders are being rejected but we're only seeing "Order rejected" without the actual NinjaTrader error message.

**Evidence from logs**:
- Multiple `ORDER_SUBMIT_FAIL` events
- Error: "Order rejected" (generic, no details)
- Order type: ENTRY_STOP
- Account: SIM

**Root Cause**: The error extraction code may not be capturing the full error message from NinjaTrader's OrderEventArgs. The error message extraction tries multiple properties but may be missing the actual rejection reason.

**Possible Reasons for Rejection**:
- Instrument not supported by account
- Invalid order parameters (price, quantity)
- Account permissions
- Market closed or instrument not trading
- Insufficient buying power (though SIM shouldn't have this)

## Next Steps to Investigate

1. **Check NinjaTrader instrument names**: Verify what NinjaTrader actually calls M2K (might be "M2K 03-26" or similar)
2. **Improve error extraction**: Add more logging to capture the full OrderEventArgs properties
3. **Check account permissions**: Verify SIM account supports MGC and M2K
4. **Add instrument validation**: Check if instrument exists before attempting order submission
