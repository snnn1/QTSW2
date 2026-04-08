using QTSW2.Robot.Core.Tests;

namespace QTSW2.Robot.Harness;

/// <summary>
/// Runs reconciliation contract / release-adoption remediation tests without building full Robot.Core
/// (see Robot.ReconciliationHarness.csproj linked sources).
/// </summary>
internal static class ReconciliationHarnessProgram
{
    private static int Main(string[] args)
    {
        var list = args.ToList();
        var testIndex = list.IndexOf("--test");
        var name = testIndex >= 0 && testIndex + 1 < list.Count ? list[testIndex + 1] : "";

        if (string.IsNullOrWhiteSpace(name) ||
            name.Equals("ALL", StringComparison.OrdinalIgnoreCase))
        {
            var (ok1, e1) = ReconciliationContractRefactorTests.RunAll();
            if (!ok1)
            {
                Console.WriteLine($"FAIL: RECONCILIATION_CONTRACT_REFACTOR — {e1}");
                return 1;
            }

            Console.WriteLine("PASS: RECONCILIATION_CONTRACT_REFACTOR");
            var (ok2, e2) = ReleaseAdoptionRemediationTests.RunReleaseAdoptionRemediationTests();
            if (!ok2)
            {
                Console.WriteLine($"FAIL: RELEASE_ADOPTION_REMEDIATION — {e2}");
                return 1;
            }

            Console.WriteLine("PASS: RELEASE_ADOPTION_REMEDIATION");
            return 0;
        }

        if (name.Equals("RECONCILIATION_CONTRACT_REFACTOR", StringComparison.OrdinalIgnoreCase))
        {
            var (pass, err) = ReconciliationContractRefactorTests.RunAll();
            Console.WriteLine(pass
                ? "PASS: Reconciliation contract refactor tests"
                : $"FAIL: {err}");
            return pass ? 0 : 1;
        }

        if (name.Equals("RELEASE_ADOPTION_REMEDIATION", StringComparison.OrdinalIgnoreCase))
        {
            var (pass, err) = ReleaseAdoptionRemediationTests.RunReleaseAdoptionRemediationTests();
            Console.WriteLine(pass
                ? "PASS: Release/adoption remediation contract tests"
                : $"FAIL: {err}");
            return pass ? 0 : 1;
        }

        Console.WriteLine("Usage: dotnet run --project modules/robot/harness/Robot.ReconciliationHarness.csproj -- --test RECONCILIATION_CONTRACT_REFACTOR|RELEASE_ADOPTION_REMEDIATION|ALL");
        return 2;
    }
}
