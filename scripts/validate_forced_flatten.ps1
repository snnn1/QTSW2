# Run forced flatten validation: simulate session through close, verify FORCED_FLATTEN_TRIGGERED,
# _forced_flatten_markers.json, and ForcedFlattenTimestamp in slot journals.
# Usage: .\scripts\validate_forced_flatten.ps1

$ErrorActionPreference = "Stop"
$qtsw2 = if ($env:QTSW2_ROOT) { $env:QTSW2_ROOT } else { (Resolve-Path "$PSScriptRoot\..").Path }
$testDate = "2026-03-04"

Push-Location $qtsw2
dotnet run --project modules/robot/harness/Robot.Harness.csproj -- --validate-forced-flatten
$exitCode = $LASTEXITCODE
if ($exitCode -eq 0) {
    python scripts/check_forced_flatten_today.py $testDate
}
Pop-Location
exit $exitCode
