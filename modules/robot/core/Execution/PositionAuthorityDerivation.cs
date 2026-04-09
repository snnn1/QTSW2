namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Primary execution gate vocabulary (Phase 1): derived from broker vs journal split (real vs recovery), no side effects.
/// </summary>
public enum DerivedPositionAuthority
{
    REAL,
    RECOVERY,
    UNKNOWN
}

/// <summary>
/// Derives position authority for execution gating. Uses absolute broker quantity and journal splits from
/// <see cref="ExecutionJournal.GetPositionAuthorityOpenQuantitiesForInstrument"/>.
/// </summary>
public static class PositionAuthorityDerivation
{
    /// <summary>
    /// REAL: real book alone matches broker; no open recovery quantity.
    /// RECOVERY: real short vs broker, recovery bridges the gap, journal total matches broker.
    /// UNKNOWN: everything else.
    /// </summary>
    public static DerivedPositionAuthority DerivePositionAuthority(int brokerQtyAbs, int realOpenQty, int recoveryOpenQty)
    {
        var b = System.Math.Abs(brokerQtyAbs);
        if (realOpenQty == b && recoveryOpenQty == 0)
            return DerivedPositionAuthority.REAL;
        if (realOpenQty < b && recoveryOpenQty > 0 && realOpenQty + recoveryOpenQty == b)
            return DerivedPositionAuthority.RECOVERY;
        return DerivedPositionAuthority.UNKNOWN;
    }

    /// <summary>REAL → baseline normal execution (subject to overlays and downstream checks).</summary>
    public static bool IsNormalExecutionAllowed(DerivedPositionAuthority authority) =>
        authority == DerivedPositionAuthority.REAL;

    /// <summary>RECOVERY → repair / flatten / non-directional manage orders per policy.</summary>
    public static bool IsRepairOnlyAllowed(DerivedPositionAuthority authority) =>
        authority == DerivedPositionAuthority.RECOVERY;

    /// <summary>UNKNOWN → block normal execution paths until authority is resolved.</summary>
    public static bool IsExecutionBlocked(DerivedPositionAuthority authority) =>
        authority == DerivedPositionAuthority.UNKNOWN;
}
