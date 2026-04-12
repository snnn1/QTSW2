namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Predicate for deferring ORDER_REGISTRY_MISSING_FAIL_CLOSED when broker working orders exist but IEA count is still zero.
/// </summary>
public static class ReconciliationDeferPolicy
{
    public static bool ShouldDeferOrderRegistryMissingFailClosed(bool ieaOwnershipAmbiguous, int brokerWorking, int robotTaggedWorking) =>
        ieaOwnershipAmbiguous && brokerWorking > 0
        || robotTaggedWorking < brokerWorking && brokerWorking > 0;
}
