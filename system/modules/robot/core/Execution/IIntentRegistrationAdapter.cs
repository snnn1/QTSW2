namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Adapter capability for registering intents and execution policy expectations.
/// Implemented by adapters that can own live intent/order maps.
/// </summary>
public interface IIntentRegistrationAdapter
{
    void RegisterIntent(Intent intent);
    void RegisterIntentPolicy(string intentId, int expectedQty, int maxQty, string canonical, string execution, string policySource);
}
