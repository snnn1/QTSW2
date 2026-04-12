# Run SessionCloseFallbackTimeZoneTests.RunDstBoundaryTests
# Uses Robot.Harness --test DST (modules/robot/core/Tests/SessionCloseFallbackTimeZoneTests.cs)
# Requires: dotnet

$qtsw2 = if ($env:QTSW2_ROOT) { $env:QTSW2_ROOT } else { (Resolve-Path "$PSScriptRoot\..").Path }
Push-Location $qtsw2
dotnet run --project modules/robot/harness/Robot.Harness.csproj -- --test DST
$exitCode = $LASTEXITCODE
Pop-Location
exit $exitCode
