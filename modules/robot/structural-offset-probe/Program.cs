using System;
using QTSW2.Robot.Core.Tests;

namespace QTSW2.Robot.StructuralOffsetProbe;

internal static class Program
{
    private static int Main()
    {
        var (pass, err) = StructuralMultiIntentAutoOffsetTests.RunAll();
        if (!pass)
        {
            Console.Error.WriteLine(err ?? "unknown error");
            return 1;
        }

        Console.WriteLine("STRUCTURAL_AUTO_OFFSET probe: PASS");
        return 0;
    }
}
