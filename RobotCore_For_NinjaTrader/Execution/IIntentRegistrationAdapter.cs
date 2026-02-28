namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Adapter capability for registering intents and policy (test inject, StreamStateMachine).
/// Implemented by NinjaTraderSimAdapter. Not all adapters support this.
/// </summary>
public interface IIntentRegistrationAdapter
{
    void RegisterIntent(Intent intent);
    void RegisterIntentPolicy(string intentId, int expectedQty, int maxQty, string canonical, string execution, string policySource);
}
