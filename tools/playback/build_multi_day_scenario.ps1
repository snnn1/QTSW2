param(
    [Parameter(Mandatory = $true)]
    [string]$Start,

    [Parameter(Mandatory = $true)]
    [string]$End,

    [string]$Matrix,
    [string]$RunId,
    [string]$ScenarioId,
    [switch]$IncludeWeekends
)

$ErrorActionPreference = "Stop"
$script = Join-Path $PSScriptRoot "build_multi_day_scenario.py"
$argsList = @($script, "--start", $Start, "--end", $End)
if ($Matrix) { $argsList += @("--matrix", $Matrix) }
if ($RunId) { $argsList += @("--run-id", $RunId) }
if ($ScenarioId) { $argsList += @("--scenario-id", $ScenarioId) }
if ($IncludeWeekends) { $argsList += "--include-weekends" }

python @argsList
