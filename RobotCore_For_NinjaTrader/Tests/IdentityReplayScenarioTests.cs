// Identity validation scenarios (replay + journal + adapter guards + AGG tags).
// Run: dotnet run --project RobotCore_For_NinjaTrader/SiblingProtectiveCancelQueue.Test/SiblingProtectiveCancelQueue.Test.csproj -- IDENTITY_REPLAY_SCENARIOS

using System;
using QTSW2.Robot.Core.Execution;

namespace QTSW2.Robot.Core.Tests;

public static class IdentityReplayScenarioTests
{
    /// <summary>All seven identity scenarios; suitable for --test IDENTITY_REPLAY_SCENARIOS.</summary>
    public static (bool AllPassed, string? Error) RunAll(Action<string>? log = null) =>
        IdentityReplayScenarioRunner.RunAllIdentityValidationScenarios(log);
}
