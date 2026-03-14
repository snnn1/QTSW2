// Unit tests for IEA Flatten Authority Hardening (MES incident class fix).
// Run: dotnet run --project modules/robot/harness/Robot.Harness.csproj -- --test IEA_FLATTEN
//
// Verifies: FlattenInvariantValidator, exposure-reduction invariant, side/qty derivation.

using System;
using QTSW2.Robot.Core.Execution;

namespace QTSW2.Robot.Core.Tests;

public static class IeaFlattenAuthorityTests
{
    public static (bool Pass, string? Error) RunIeaFlattenTests()
    {
        // 1. Flatten direction invariant: long requires SELL
        var (valid, err) = FlattenInvariantValidator.ValidateFlattenReducesExposure(5, "SELL", 5);
        if (!valid || err != null)
            return (false, $"Long+SELL+5 should be valid: valid={valid}, err={err}");

        // 2. Flatten direction invariant: long with BUY is invalid (exposure-increasing)
        (valid, err) = FlattenInvariantValidator.ValidateFlattenReducesExposure(5, "BUY", 5);
        if (valid || string.IsNullOrEmpty(err))
            return (false, $"Long+BUY should be invalid: valid={valid}, err={err}");

        // 3. Flatten direction invariant: short requires BUY
        (valid, err) = FlattenInvariantValidator.ValidateFlattenReducesExposure(-3, "BUY", 3);
        if (!valid || err != null)
            return (false, $"Short+BUY+3 should be valid: valid={valid}, err={err}");

        // 4. Flatten direction invariant: short with SELL is invalid (exposure-increasing)
        (valid, err) = FlattenInvariantValidator.ValidateFlattenReducesExposure(-3, "SELL", 3);
        if (valid || string.IsNullOrEmpty(err))
            return (false, $"Short+SELL should be invalid: valid={valid}, err={err}");

        // 5. Proposed qty exceeds account long
        (valid, err) = FlattenInvariantValidator.ValidateFlattenReducesExposure(5, "SELL", 10);
        if (valid || string.IsNullOrEmpty(err))
            return (false, $"Proposed qty 10 > long 5 should be invalid: valid={valid}, err={err}");

        // 6. Proposed qty exceeds account short
        (valid, err) = FlattenInvariantValidator.ValidateFlattenReducesExposure(-3, "BUY", 5);
        if (valid || string.IsNullOrEmpty(err))
            return (false, $"Proposed qty 5 > short 3 should be invalid: valid={valid}, err={err}");

        // 7. Partial flatten: long 5, SELL 3 is valid
        (valid, err) = FlattenInvariantValidator.ValidateFlattenReducesExposure(5, "SELL", 3);
        if (!valid || err != null)
            return (false, $"Long+SELL+3 (partial) should be valid: valid={valid}, err={err}");

        // 8. Partial flatten: short 4, BUY 2 is valid
        (valid, err) = FlattenInvariantValidator.ValidateFlattenReducesExposure(-4, "BUY", 2);
        if (!valid || err != null)
            return (false, $"Short+BUY+2 (partial) should be valid: valid={valid}, err={err}");

        // 9. Case-insensitive side: "sell" and "buy" accepted
        (valid, err) = FlattenInvariantValidator.ValidateFlattenReducesExposure(5, "sell", 5);
        if (!valid || err != null)
            return (false, $"Long+sell (lowercase) should be valid: valid={valid}, err={err}");
        (valid, err) = FlattenInvariantValidator.ValidateFlattenReducesExposure(-3, "buy", 3);
        if (!valid || err != null)
            return (false, $"Short+buy (lowercase) should be valid: valid={valid}, err={err}");

        // 10. Account flat (qty 0): no direction, validator returns valid (no order would be sent)
        (valid, err) = FlattenInvariantValidator.ValidateFlattenReducesExposure(0, "SELL", 0);
        if (!valid || err != null)
            return (false, $"Flat account (0) with SELL 0 should be valid: valid={valid}, err={err}");

        // 11. MES incident: account short 4, intent state says long, request flatten → must use BUY 4
        // Intent state must NOT influence flatten direction; account position is sole source of truth.
        (valid, err) = FlattenInvariantValidator.ValidateFlattenReducesExposure(-4, "BUY", 4);
        if (!valid || err != null)
            return (false, $"Account short 4 + BUY 4 (ignoring stale intent) should be valid: valid={valid}, err={err}");
        (valid, err) = FlattenInvariantValidator.ValidateFlattenReducesExposure(-4, "SELL", 4);
        if (valid || string.IsNullOrEmpty(err))
            return (false, $"Account short 4 + SELL 4 (intent-corrupted direction) must be invalid: valid={valid}, err={err}");

        return (true, null);
    }
}
