using QTSW2.Robot.Core.Tests;

namespace QTSW2.Robot.Core.KeyEventsNarrativeTool;

internal static class Program
{
    private static int Main()
    {
        var (pass, err) = KeyEventsNarrativeReconstructionTests.RunAll();
        Console.WriteLine(pass ? "PASS: KEY_EVENTS narrative reconstruction" : $"FAIL: {err}");
        return pass ? 0 : 1;
    }
}
