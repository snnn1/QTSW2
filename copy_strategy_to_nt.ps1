# Convenience entry — forwards to scripts\copy_strategy_to_nt.ps1
$script = Join-Path $PSScriptRoot "scripts\copy_strategy_to_nt.ps1"
if (-not (Test-Path $script)) {
    Write-Error "Missing $script"
    exit 1
}
& $script @args
exit $LASTEXITCODE
