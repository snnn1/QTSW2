# Fix execution journal Instrument field for today (canonical -> execution instrument)
# Run from project root. Only modifies 2026-02-26 journals with wrong Instrument.
# Mappings: RTY -> M2K, YM -> MYM

$ErrorActionPreference = "Stop"
$journalDir = Join-Path $PSScriptRoot "..\data\execution_journals"
$today = Get-Date -Format "yyyy-MM-dd"

$mappings = @{
    "RTY" = "M2K"
    "YM"  = "MYM"
}

$fixed = 0
$files = Get-ChildItem $journalDir -Filter "${today}_*.json" -ErrorAction SilentlyContinue
foreach ($f in $files) {
    $json = Get-Content $f.FullName -Raw | ConvertFrom-Json
    $current = if ($json.Instrument) { $json.Instrument.Trim() } else { "" }
    if ([string]::IsNullOrEmpty($current)) { continue }
    
    $correct = $mappings[$current]
    if ($correct) {
        Write-Host "Fixing $($f.Name): Instrument '$current' -> '$correct'"
        $json.Instrument = $correct
        $json | ConvertTo-Json -Depth 10 -Compress | Set-Content $f.FullName -NoNewline
        $fixed++
    }
}

Write-Host "Done. Fixed $fixed journal(s) for $today"
