# Convenience entry point (repo root) — forwards to scripts\rebuild_and_deploy.ps1
#
# Usage (from QTSW2 root):
#   .\rebuild_and_deploy.ps1
#   .\rebuild_and_deploy.ps1 -ForceCacheClear
#
# Builds Robot.Core (Release), copies DLLs + strategy via copy_dll_when_ready / copy_strategy_to_nt.
# Close NinjaTrader before deploy if DLLs are locked.

param([switch]$ForceCacheClear)

$script = Join-Path $PSScriptRoot "scripts\rebuild_and_deploy.ps1"
if (-not (Test-Path $script)) {
    Write-Error "Missing $script"
    exit 1
}

& $script @PSBoundParameters
exit $LASTEXITCODE
