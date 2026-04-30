# Legacy/manual mirror tool for system/NT_ADDONS.
#
# NinjaTrader runtime deploy is DLL-only and does not compile or copy AddOns source.
# Do not call this from the normal deploy path; use it only for an explicit
# mirror-audit workflow while system/NT_ADDONS still exists.

param(
    [switch]$CheckOnly,
    [switch]$FailOnDrift
)

$ErrorActionPreference = "Stop"
$projectRoot = if ($PSScriptRoot) { $PSScriptRoot } else { $PWD }
for ($i = 0; $i -lt 6; $i++) {
    if (Test-Path (Join-Path $projectRoot "system\RobotCore_For_NinjaTrader")) { break }
    $parent = Split-Path $projectRoot -Parent
    if ([string]::IsNullOrEmpty($parent) -or $parent -eq $projectRoot) { break }
    $projectRoot = $parent
}

$sourceDir = Join-Path $projectRoot "system\RobotCore_For_NinjaTrader"
$destDir = Join-Path $projectRoot "system\NT_ADDONS"

if (-not (Test-Path $destDir)) {
    Write-Host "[WARN] NT_ADDONS not found, skipping sync" -ForegroundColor Yellow
    exit 0
}

# These files have intentional NT_ADDONS divergence.
# Keep this list small and audited; every other common .cs file is treated as a mirror.
$knownDivergentFiles = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
@(
    # None currently. Add named exceptions only with audit evidence.
) | ForEach-Object { [void]$knownDivergentFiles.Add($_) }

function Get-RelativePath([string]$root, [string]$path) {
    return $path.Substring($root.Length + 1).Replace("/", "\")
}

function Get-FileSha256([string]$path) {
    return (Get-FileHash -Path $path -Algorithm SHA256).Hash
}

function Test-IsExcludedRelativePath([string]$rel) {
    if ([string]::IsNullOrWhiteSpace($rel)) {
        return $true
    }

    $normalized = $rel.Replace("/", "\")
    return $normalized -match '(^|\\)(bin|obj|Tests|SiblingProtectiveCancelQueue\.Test|Strategies|Properties)\\'
}

$sourceRoot = (Resolve-Path $sourceDir).Path
$destRoot = (Resolve-Path $destDir).Path
$managedFiles = New-Object System.Collections.Generic.List[string]
$sourceOnlyFiles = New-Object System.Collections.Generic.List[string]
$destOnlyFiles = New-Object System.Collections.Generic.List[string]
$managedFileSet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
$sourceOnlyFileSet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
$linkedSourceByRelativePath = @{}

function Add-ManagedFile([string]$rel) {
    if ($managedFileSet.Add($rel)) {
        $managedFiles.Add($rel)
    }
}

function Add-SourceOnlyFile([string]$rel) {
    if ($sourceOnlyFileSet.Add($rel)) {
        $sourceOnlyFiles.Add($rel)
    }
}

function Add-LinkedSourcesFromProject([string]$projectPath) {
    if (-not (Test-Path $projectPath)) {
        return
    }

    $projectDir = Split-Path $projectPath -Parent
    try {
        [xml]$projectXml = Get-Content $projectPath -Raw
        foreach ($itemGroup in $projectXml.Project.ItemGroup) {
            foreach ($compile in $itemGroup.Compile) {
                if (-not $compile.Include -or -not $compile.Link) {
                    continue
                }

                $rel = $compile.Link.ToString().Replace("/", "\")
                if (Test-IsExcludedRelativePath $rel) {
                    continue
                }

                if ($knownDivergentFiles.Contains($rel)) {
                    continue
                }

                $includePath = Join-Path $projectDir $compile.Include.ToString()
                if (Test-Path $includePath) {
                    $linkedSourceByRelativePath[$rel] = (Resolve-Path $includePath).Path
                }
            }
        }
    }
    catch {
        Write-Host "[WARN] Could not parse linked sources from ${projectPath}: $($_.Exception.Message)" -ForegroundColor Yellow
    }
}

function Get-SourcePathForRelativePath([string]$rel) {
    if ($linkedSourceByRelativePath.ContainsKey($rel)) {
        return $linkedSourceByRelativePath[$rel]
    }

    return Join-Path $sourceRoot $rel
}

Add-LinkedSourcesFromProject (Join-Path $sourceRoot "Robot.Core.csproj")

foreach ($srcFile in Get-ChildItem -Path $sourceRoot -Recurse -File -Filter "*.cs") {
    if ($srcFile.FullName -match "\\(bin|obj|Tests|SiblingProtectiveCancelQueue\.Test|Strategies|Properties)\\") {
        continue
    }

    $rel = Get-RelativePath $sourceRoot $srcFile.FullName
    if ($knownDivergentFiles.Contains($rel)) {
        continue
    }

    $dst = Join-Path $destRoot $rel
    if (Test-Path $dst) {
        Add-ManagedFile $rel
    }
    else {
        Add-SourceOnlyFile $rel
    }
}

foreach ($rel in $linkedSourceByRelativePath.Keys) {
    $dst = Join-Path $destRoot $rel
    if (Test-Path $dst) {
        Add-ManagedFile $rel
    }
    else {
        Add-SourceOnlyFile $rel
    }
}

foreach ($dstFile in Get-ChildItem -Path $destRoot -Recurse -File -Filter "*.cs") {
    if ($dstFile.FullName -match "\\(bin|obj|Tests|SiblingProtectiveCancelQueue\.Test|Strategies|Properties)\\") {
        continue
    }

    $rel = Get-RelativePath $destRoot $dstFile.FullName
    if ($knownDivergentFiles.Contains($rel)) {
        continue
    }

    $src = Get-SourcePathForRelativePath $rel
    if (-not (Test-Path $src)) {
        $destOnlyFiles.Add($rel)
    }
}

$drifted = New-Object System.Collections.Generic.List[string]
foreach ($rel in $managedFiles) {
    $src = Get-SourcePathForRelativePath $rel
    $dst = Join-Path $destRoot $rel
    if ((Get-FileSha256 $src) -ne (Get-FileSha256 $dst)) {
        $drifted.Add($rel)
    }
}

if ($CheckOnly) {
    Write-Host "[INFO] Managed mirror files: $($managedFiles.Count)" -ForegroundColor Gray
    Write-Host "[INFO] Known divergent files: $($knownDivergentFiles.Count)" -ForegroundColor Gray
    if ($sourceOnlyFiles.Count -gt 0) {
        Write-Host "[WARN] RobotCore source file(s) missing from NT_ADDONS mirror: $($sourceOnlyFiles.Count)" -ForegroundColor Yellow
        foreach ($rel in $sourceOnlyFiles) {
            Write-Host "  $rel" -ForegroundColor Yellow
        }
    }

    if ($destOnlyFiles.Count -gt 0) {
        Write-Host "[WARN] NT_ADDONS destination-only file(s) with no RobotCore source: $($destOnlyFiles.Count)" -ForegroundColor Yellow
        foreach ($rel in $destOnlyFiles) {
            Write-Host "  $rel" -ForegroundColor Yellow
        }
    }

    if ($drifted.Count -eq 0 -and $sourceOnlyFiles.Count -eq 0 -and $destOnlyFiles.Count -eq 0) {
        Write-Host "[OK] NT_ADDONS mirror is complete and in sync" -ForegroundColor Green
        exit 0
    }

    if ($drifted.Count -gt 0) {
        Write-Host "[WARN] NT_ADDONS mirror drift detected in $($drifted.Count) managed file(s):" -ForegroundColor Yellow
        foreach ($rel in $drifted) {
            Write-Host "  $rel" -ForegroundColor Yellow
        }
    }

    if ($FailOnDrift) {
        exit 2
    }

    exit 0
}

$synced = 0
foreach ($rel in $managedFiles) {
    $src = Get-SourcePathForRelativePath $rel
    $dst = Join-Path $destRoot $rel
    if ((Get-FileSha256 $src) -ne (Get-FileSha256 $dst)) {
        Copy-Item -Path $src -Destination $dst -Force
        $synced++
        Write-Host "  Synced: $rel" -ForegroundColor Gray
    }
}

$created = 0
foreach ($rel in $sourceOnlyFiles) {
    $src = Get-SourcePathForRelativePath $rel
    $dst = Join-Path $destRoot $rel
    $dstParent = Split-Path $dst -Parent
    if (-not (Test-Path $dstParent)) {
        New-Item -ItemType Directory -Path $dstParent -Force | Out-Null
    }

    Copy-Item -Path $src -Destination $dst -Force
    $created++
    Write-Host "  Created: $rel" -ForegroundColor Gray
}

if ($destOnlyFiles.Count -gt 0) {
    Write-Host "[WARN] NT_ADDONS destination-only file(s) were left unchanged: $($destOnlyFiles.Count)" -ForegroundColor Yellow
}

Write-Host "[OK] NT_ADDONS mirror sync complete: $synced changed, $created created, $($managedFiles.Count) managed, $($knownDivergentFiles.Count) known divergent, $($destOnlyFiles.Count) destination-only" -ForegroundColor Green
