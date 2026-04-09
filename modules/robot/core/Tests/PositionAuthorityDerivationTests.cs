using QTSW2.Robot.Core.Execution;

namespace QTSW2.Robot.Core.Tests;

public static class PositionAuthorityDerivationTests
{
    public static (bool Pass, string? Error) RunAll()
    {
        if (PositionAuthorityDerivation.DerivePositionAuthority(3, 3, 0) != DerivedPositionAuthority.REAL)
            return (false, "expected REAL when real==broker and no recovery");
        if (PositionAuthorityDerivation.DerivePositionAuthority(5, 2, 3) != DerivedPositionAuthority.RECOVERY)
            return (false, "expected RECOVERY when journal matches broker");
        if (PositionAuthorityDerivation.DerivePositionAuthority(5, 2, 2) != DerivedPositionAuthority.UNKNOWN)
            return (false, "expected UNKNOWN when journal does not match broker");
        if (PositionAuthorityDerivation.DerivePositionAuthority(4, 5, 0) != DerivedPositionAuthority.UNKNOWN)
            return (false, "expected UNKNOWN when real > broker");
        return (true, null);
    }
}
