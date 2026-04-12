# Live validation helper: per-second rates for engine events (post-deploy window optional).
param(
    [string] $LogRoot = (Join-Path (Split-Path -Parent $PSScriptRoot) "logs\robot"),
    [string] $SinceUtc = "2026-03-30T18:35:00+00:00",
    [switch] $AllArchives
)
$ErrorActionPreference = "Stop"

$deployUtc = [datetimeoffset]::Parse($SinceUtc)
$dayStamp = $deployUtc.ToString("yyyyMMdd")
if ($AllArchives) {
    $archive = @(Get-ChildItem -LiteralPath (Join-Path $LogRoot "archive") -Filter "robot_ENGINE*.jsonl" -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName })
} else {
    $archive = @(Get-ChildItem -LiteralPath (Join-Path $LogRoot "archive") -Filter ("robot_ENGINE_{0}_*.jsonl" -f $dayStamp) -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName })
}
$files = @((Join-Path $LogRoot "robot_ENGINE.jsonl")) + $archive | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -Unique

$events = @(
    "TAGGED_BROKER_WITHOUT_JOURNAL_DETECTED",
    "TAGGED_BROKER_EXPOSURE_RECOVERY_JOURNAL_UPSERT",
    "TAGGED_BROKER_WITHOUT_JOURNAL_REPAIR_COMPLETE",
    "RECONCILIATION_MISMATCH_DETECTED",
    "POSITION_DRIFT_DETECTED"
)
$perEvSec = @{}
$totals = @{}
$peak = @{}
$peakSec = @{}
foreach ($ev in $events) {
    $perEvSec[$ev] = @{}
    $totals[$ev] = 0
    $peak[$ev] = 0
    $peakSec[$ev] = ""
}

foreach ($f in $files) {
    $path = [System.IO.Path]::GetFullPath($f)
    $share = [System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete
    $fs = [System.IO.File]::Open($path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, $share)
    $reader = [System.IO.StreamReader]::new($fs)
    try {
        while ($null -ne ($line = $reader.ReadLine())) {
            if ($line -notmatch '"ts_utc":"([^"]+)"') { continue }
            $tsRaw = $Matches[1] -replace '\\u002B', '+'
            try {
                $ts = [datetimeoffset]::Parse($tsRaw, [cultureinfo]::InvariantCulture)
            } catch {
                continue
            }
            if ($ts -lt $deployUtc) { continue }
            if ($line -notmatch '"event":"([^"]+)"') { continue }
            $e = $Matches[1]
            if (-not $totals.ContainsKey($e)) { continue }
            $sec = $ts.ToString("yyyy-MM-ddTHH:mm:ss")
            $h = $perEvSec[$e]
            if (-not $h.ContainsKey($sec)) { $h[$sec] = 0 }
            $h[$sec]++
            $nv = $h[$sec]
            if ($nv -gt $peak[$e]) {
                $peak[$e] = $nv
                $peakSec[$e] = $sec
            }
            $totals[$e]++
        }
    } finally {
        $reader.Dispose()
        $fs.Dispose()
    }
}

Write-Host "Since $SinceUtc - files: $($files.Count)"
Write-Host "--- Totals ---"
foreach ($ev in $events) { Write-Host ($ev + " : " + $totals[$ev]) }
Write-Host ""
Write-Host "--- Typical events/sec (mean over seconds where that event fired) ---"
foreach ($ev in $events) {
    $h = $perEvSec[$ev]
    $sum = 0
    $n = $h.Count
    foreach ($v in $h.Values) { $sum += $v }
    $mean = if ($n -gt 0) { [math]::Round($sum / $n, 4) } else { 0 }
    Write-Host ($ev + " mean=" + $mean + " active-sec=" + $n)
}
Write-Host ""
Write-Host "--- Peak events/sec by type ---"
foreach ($ev in $events) {
    Write-Host ($ev + " peak=" + $peak[$ev] + " sec=" + $peakSec[$ev])
}
$d = $totals["TAGGED_BROKER_WITHOUT_JOURNAL_DETECTED"]
$u = $totals["TAGGED_BROKER_EXPOSURE_RECOVERY_JOURNAL_UPSERT"]
$c = $totals["TAGGED_BROKER_WITHOUT_JOURNAL_REPAIR_COMPLETE"]
Write-Host ""
Write-Host ("DETECTED:UPSERT:COMPLETE = " + $d + ":" + $u + ":" + $c)
if ($u -gt 0) {
    Write-Host ("DETECTED:UPSERT ratio = " + [math]::Round($d / $u, 4))
}
if ($c -gt 0) {
    Write-Host ("DETECTED:COMPLETE ratio = " + [math]::Round($d / $c, 4))
}
