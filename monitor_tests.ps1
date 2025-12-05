while ($true) {
    if (Test-Path "logs\continuous_test.log") {
        $newLines = Get-Content "logs\continuous_test.log" -Tail 20
        Clear-Host
        Write-Host "=== CONTINUOUS TEST MONITOR ===" -ForegroundColor Cyan
        Write-Host ""
        $newLines | ForEach-Object {
            if ($_ -match "ERROR|Failed|failed|error") {
                Write-Host $_ -ForegroundColor Red
            } elseif ($_ -match "SUCCESS|success|") {
                Write-Host $_ -ForegroundColor Green
            } else {
                Write-Host $_
            }
        }
    }
    Start-Sleep -Seconds 5
}
