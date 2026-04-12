using System;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Reusable exposure-reduction invariant for flatten orders.
/// Rule: long (qty > 0) requires SELL; short (qty < 0) requires BUY; proposedQty <= abs(accountQty).
/// Caller contract: IEA must check account position first. If qty == 0, skip flatten and emit
/// FLATTEN_SKIPPED_ACCOUNT_FLAT without calling this validator. This validator is only invoked
/// when qty != 0; it cannot influence order submission when flat.
/// </summary>
public static class FlattenInvariantValidator
{
    /// <summary>Validate that proposed flatten order would reduce or eliminate exposure.</summary>
    /// <returns>(valid, errorMessage)</returns>
    public static (bool valid, string? error) ValidateFlattenReducesExposure(int accountQty, string proposedSide, int proposedQty)
    {
        if (accountQty > 0) // Long
        {
            if (!proposedSide.Equals("SELL", StringComparison.OrdinalIgnoreCase))
                return (false, $"Long position requires SELL, got {proposedSide}");
            if (proposedQty > accountQty)
                return (false, $"Proposed qty {proposedQty} exceeds account long {accountQty}");
        }
        else if (accountQty < 0) // Short
        {
            if (!proposedSide.Equals("BUY", StringComparison.OrdinalIgnoreCase))
                return (false, $"Short position requires BUY, got {proposedSide}");
            if (proposedQty > Math.Abs(accountQty))
                return (false, $"Proposed qty {proposedQty} exceeds account short {Math.Abs(accountQty)}");
        }
        return (true, null);
    }
}
