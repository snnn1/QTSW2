# Check Current Stream Status
# Shows what's happening with each stream

$ErrorActionPreference = "Continue"

Write-Host "================================================" -ForegroundColor Cyan
Write-Host "  Current Stream Status Report" -ForegroundColor Cyan
Write-Host "================================================" -ForegroundColor Cyan
Write-Host ""

# Check today's streams
$today = Get-Date -Format "yyyy-MM-dd"
Write-Host "Today's Date: $today" -ForegroundColor Yellow
Write-Host ""

# Check journal files for today
$todayStreams = Get-ChildItem -Path "logs\robot\journal" -Filter "$today_*.json" -ErrorAction SilentlyContinue
if ($todayStreams) {
    Write-Host "Streams for Today ($today):" -ForegroundColor Green
    Write-Host ""
    $todayStreams | ForEach-Object {
        $content = Get-Content $_.FullName | ConvertFrom-Json
        $stateColor = switch ($content.LastState) {
            "DONE" { "Green" }
            "RANGE_LOCKED" { "Cyan" }
            "RANGE_BUILDING" { "Yellow" }
            "ARMED" { "Yellow" }
            "PRE_HYDRATION" { "Gray" }
            default { "White" }
        }
        Write-Host "  $($content.Stream): " -NoNewline
        Write-Host "$($content.LastState)" -ForegroundColor $stateColor -NoNewline
        if ($content.Committed) {
            Write-Host " [COMMITTED]" -ForegroundColor Green -NoNewline
        }
        if ($content.EntryDetected) {
            Write-Host " [ENTRY DETECTED]" -ForegroundColor Cyan -NoNewline
        }
        Write-Host ""
    }
    Write-Host ""
} else {
    Write-Host "No streams found for today ($today)" -ForegroundColor Yellow
    Write-Host ""
}

# Check recent timetable parsing
Write-Host "Recent Timetable Parsing:" -ForegroundColor Yellow
$recentTimetable = Get-Content "logs\robot\robot_ENGINE.jsonl" -Tail 500 | Select-String -Pattern "TIMETABLE_PARSING_COMPLETE|STREAMS_CREATED" | Select-Object -Last 5
if ($recentTimetable) {
    foreach ($line in $recentTimetable) {
        if ($line -match '"event":"TIMETABLE_PARSING_COMPLETE"') {
            Write-Host "  Timetable parsed - checking details..." -ForegroundColor Gray
        }
        if ($line -match '"event":"STREAMS_CREATED"') {
            if ($line -match '"stream_count"\s*:\s*(\d+)') {
                $count = $matches[1]
                if ([int]$count -eq 0) {
                    Write-Host "  WARNING: No streams created (stream_count = 0)" -ForegroundColor Yellow
                } else {
                    Write-Host "  Streams created: $count" -ForegroundColor Green
                }
            }
        }
    }
} else {
    Write-Host "  No recent timetable parsing events found" -ForegroundColor Gray
}
Write-Host ""

# Show most recent stream states
Write-Host "Most Recent Stream Activity (All Dates):" -ForegroundColor Yellow
Write-Host ""
Get-ChildItem -Path "logs\robot\journal" -Filter "*.json" | 
    Sort-Object LastWriteTime -Descending | 
    Select-Object -First 15 | 
    ForEach-Object {
        $content = Get-Content $_.FullName | ConvertFrom-Json
        $age = (Get-Date) - ([DateTimeOffset]::Parse($content.LastUpdateUtc)).LocalDateTime
        $ageStr = if ($age.TotalHours -lt 1) {
            "$([math]::Round($age.TotalMinutes, 0)) minutes ago"
        } elseif ($age.TotalDays -lt 1) {
            "$([math]::Round($age.TotalHours, 1)) hours ago"
        } else {
            "$([math]::Round($age.TotalDays, 1)) days ago"
        }
        
        $stateColor = switch ($content.LastState) {
            "DONE" { "Green" }
            "RANGE_LOCKED" { "Cyan" }
            "RANGE_BUILDING" { "Yellow" }
            "ARMED" { "Yellow" }
            "PRE_HYDRATION" { "Gray" }
            default { "White" }
        }
        
        Write-Host "  [$($content.TradingDate)] $($content.Stream): " -NoNewline
        Write-Host "$($content.LastState)" -ForegroundColor $stateColor -NoNewline
        Write-Host " ($ageStr)" -ForegroundColor DarkGray
    }

Write-Host ""
Write-Host "================================================" -ForegroundColor Cyan
