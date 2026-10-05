# Fail the build when NuGet reports any direct or transitive vulnerability.
# JSON output keeps the check independent of the machine UI language.
param(
    [string]$Solution = "",
    [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($Solution)) {
    $Solution = Join-Path $root 'Deduplicator.slnx'
}

if (-not $NoRestore) {
    dotnet restore $Solution --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'dotnet restore failed before vulnerability audit.' }
}

$output = & dotnet list $Solution package --vulnerable --include-transitive --format json --no-restore
if ($LASTEXITCODE -ne 0) { throw 'dotnet package vulnerability audit failed to run.' }

try { $report = ($output -join [Environment]::NewLine) | ConvertFrom-Json }
catch {
    $output | ForEach-Object { Write-Host $_ }
    throw 'Could not parse NuGet vulnerability audit JSON.'
}

$findings = @()
foreach ($project in @($report.projects)) {
    foreach ($framework in @($project.frameworks)) {
        foreach ($package in @($framework.topLevelPackages) + @($framework.transitivePackages)) {
            foreach ($vulnerability in @($package.vulnerabilities)) {
                if ($null -eq $vulnerability) { continue }
                $findings += [pscustomobject]@{
                    Project  = $project.path
                    Package  = $package.id
                    Version  = $package.resolvedVersion
                    Severity = $vulnerability.severity
                    Advisory = $vulnerability.advisoryurl
                }
            }
        }
    }
}

if ($findings.Count -gt 0) {
    Write-Host 'NuGet vulnerability audit FAILED:' -ForegroundColor Red
    $findings | Format-Table -AutoSize | Out-String | Write-Host
    exit 1
}

Write-Host 'NuGet vulnerability audit: 0 findings.' -ForegroundColor Green
