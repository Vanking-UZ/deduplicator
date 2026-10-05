# Clean Windows publish + verify the NEW engine actually made it into the output.
# Guards against a stale installer built from older sources. Run from the repo root.
# ASCII-only on purpose: Windows PowerShell 5.1 reads .ps1 as the system ANSI codepage,
# so non-ASCII comments can corrupt parsing depending on file encoding.
#
#   powershell -ExecutionPolicy Bypass -File scripts/publish-win-x64.ps1
#
param([string]$Version = "")
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out  = Join-Path $root 'dist/Deduplicator-win-x64'
$propsPath = Join-Path $root 'Directory.Build.props'

if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$props = Get-Content -LiteralPath $propsPath
    $Version = [string]$props.Project.PropertyGroup.VersionPrefix
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Version must be numeric SemVer (for example 1.2.3), got '$Version'."
}
$versionProperty = "/p:VersionPrefix=$Version"
Write-Host ("== version {0} ==" -f $Version) -ForegroundColor Cyan

Write-Host '== clean ==' -ForegroundColor Cyan
dotnet clean "$root/Deduplicator.slnx" -c Release | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'dotnet clean failed.' }
if (Test-Path $out) { Remove-Item $out -Recurse -Force }

Write-Host '== locked restore + build + test + audit ==' -ForegroundColor Cyan
dotnet restore "$root/Deduplicator.slnx" --locked-mode $versionProperty
if ($LASTEXITCODE -ne 0) { throw 'dotnet restore failed.' }
dotnet build "$root/Deduplicator.slnx" -c Release --no-restore $versionProperty
if ($LASTEXITCODE -ne 0) { throw 'dotnet build failed.' }
dotnet test "$root/Deduplicator.slnx" -c Release --no-build --no-restore $versionProperty
if ($LASTEXITCODE -ne 0) { throw 'dotnet test failed.' }
& (Join-Path $root 'scripts/audit-vulnerabilities.ps1') -Solution "$root/Deduplicator.slnx" -NoRestore
if ($LASTEXITCODE -ne 0) { throw 'NuGet vulnerability audit failed.' }

Write-Host '== publish (self-contained win-x64) ==' -ForegroundColor Cyan
dotnet restore "$root/src/Deduplicator.App/Deduplicator.App.csproj" `
    -r win-x64 --locked-mode $versionProperty
if ($LASTEXITCODE -ne 0) { throw 'win-x64 restore failed.' }
dotnet publish "$root/src/Deduplicator.App/Deduplicator.App.csproj" `
    -c Release -r win-x64 --self-contained true --no-restore -o $out $versionProperty
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

# Publish the narrow privileged helper into the same self-contained runtime folder. It is a
# separate executable and has no references to App/Core/SQLite/UI. Runtime trust gating still
# keeps it disabled unless both EXEs are signed and installed in protected Program Files.
dotnet restore "$root/src/Deduplicator.MftHelper/Deduplicator.MftHelper.csproj" `
    -r win-x64 --locked-mode $versionProperty
if ($LASTEXITCODE -ne 0) { throw 'MFT helper win-x64 restore failed.' }
dotnet publish "$root/src/Deduplicator.MftHelper/Deduplicator.MftHelper.csproj" `
    -c Release -r win-x64 --self-contained true --no-restore -o $out $versionProperty
if ($LASTEXITCODE -ne 0) { throw 'MFT helper publish failed.' }

# Exercise the files from the publish folder, not bin/. The app visits every main page in
# both themes and closes itself. This catches deferred XAML/runtime assets that unit tests
# can miss because their templates are not realized with live data.
Write-Host '== published UI smoke ==' -ForegroundColor Cyan
$smokeRoot = Join-Path ([IO.Path]::GetTempPath()) ("DeduplicatorSmoke-" + [Guid]::NewGuid().ToString('N'))
$smokeOut = Join-Path $smokeRoot 'stdout.txt'
$smokeErr = Join-Path $smokeRoot 'stderr.txt'
$oldSmoke = $env:DEDUP_SMOKE
$oldDataRoot = $env:DEDUP_DATA_ROOT
$smokePassed = $false
try {
    New-Item -ItemType Directory -Force $smokeRoot | Out-Null
    $env:DEDUP_SMOKE = '1'
    $env:DEDUP_DATA_ROOT = Join-Path $smokeRoot 'Data'
    $smoke = Start-Process -FilePath (Join-Path $out 'Deduplicator.exe') -PassThru `
        -WindowStyle Hidden -RedirectStandardOutput $smokeOut -RedirectStandardError $smokeErr
    if (-not $smoke.WaitForExit(60000)) {
        Stop-Process -Id $smoke.Id -Force -ErrorAction SilentlyContinue
        throw 'Published UI smoke timed out after 60 seconds.'
    }
    $smoke.WaitForExit()
    $smokeExitCode = $smoke.ExitCode
    if ($null -ne $smokeExitCode -and $smokeExitCode -ne 0) {
        $captured = ((Get-Content $smokeErr -Raw -ErrorAction SilentlyContinue) + ' ' +
                     (Get-Content $smokeOut -Raw -ErrorAction SilentlyContinue)).Trim()
        throw "Published UI smoke failed with exit code $smokeExitCode. $captured"
    }
    $smokeLog = Join-Path $env:DEDUP_DATA_ROOT 'logs/deduplicator.log'
    if (-not (Test-Path $smokeLog) -or
        -not (Select-String -Path $smokeLog -SimpleMatch 'INFO Application run finished cleanly' -Quiet) -or
        (Select-String -Path $smokeLog -Pattern 'AppDomain\.UnhandledException|FATAL' -Quiet)) {
        throw 'Published UI smoke did not finish cleanly. Inspect its application log.'
    }
    if ((Test-Path $smokeOut) -and
        (Select-String -Path $smokeOut -SimpleMatch '[Binding]An error occurred' -Quiet)) {
        throw 'Published UI smoke reported Avalonia binding errors. Inspect stdout.txt.'
    }
    Write-Host 'OK   published UI navigation smoke completed cleanly' -ForegroundColor Green
    $smokePassed = $true
}
finally {
    $env:DEDUP_SMOKE = $oldSmoke
    $env:DEDUP_DATA_ROOT = $oldDataRoot
    if ($smokePassed -and (Test-Path $smokeRoot)) {
        Remove-Item $smokeRoot -Recurse -Force -ErrorAction SilentlyContinue
    } elseif (-not $smokePassed) {
        Write-Host "Published UI smoke diagnostics: $smokeRoot" -ForegroundColor Yellow
    }
}

# --- Verify the publish folder contains the NEW engine, not a stale one ---
$fail = $false
function Check([bool]$ok, [string]$msg) {
    if ($ok) { Write-Host "OK   $msg" -ForegroundColor Green }
    else     { Write-Host "FAIL $msg" -ForegroundColor Red; $script:fail = $true }
}

Check (Test-Path (Join-Path $out 'FastDedup.Engine.dll'))  'FastDedup.Engine.dll present'
Check (Test-Path (Join-Path $out 'Deduplicator.Core.dll')) 'Deduplicator.Core.dll present'
Check (Test-Path (Join-Path $out 'Avalonia.Controls.ColorPicker.dll')) `
    'Avalonia.Controls.ColorPicker.dll present for runtime XAML bindings'
Check (Test-Path (Join-Path $out 'Deduplicator.MftHelper.exe')) 'Deduplicator.MftHelper.exe present'
Check ((Get-Item (Join-Path $out 'Deduplicator.exe')).VersionInfo.FileVersion -eq "$Version.0") `
    "Deduplicator.exe file version is $Version.0"
Check ((Get-Item (Join-Path $out 'Deduplicator.MftHelper.exe')).VersionInfo.FileVersion -eq "$Version.0") `
    "Deduplicator.MftHelper.exe file version is $Version.0"

$deps = Join-Path $out 'Deduplicator.deps.json'
Check ((Test-Path $deps) -and (Select-String -Path $deps -Pattern 'FastDedup.Engine' -Quiet)) `
    'deps.json references FastDedup.Engine'
$helperDeps = Join-Path $out 'Deduplicator.MftHelper.deps.json'
Check ((Test-Path $helperDeps) -and (Select-String -Path $helperDeps -Pattern 'FastDedup.Engine' -Quiet) -and
       -not (Select-String -Path $helperDeps -Pattern 'Deduplicator.Core|Avalonia|Microsoft.Data.Sqlite' -Quiet)) `
    'MFT helper dependency graph is narrow (Engine only; no Core/UI/SQLite)'
Check ((Select-String -Path $deps -SimpleMatch 'SQLitePCLRaw.bundle_e_sqlite3/3.0.3' -Quiet) -and
       -not (Select-String -Path $deps -Pattern 'SQLitePCLRaw\.lib\.e_sqlite3/2\.' -Quiet)) `
    'publish uses the hardened SQLitePCLRaw 3.x bundle'

# New engine symbols must be in FastDedup.Engine.dll (rough proof fresh code was built).
$engine = Join-Path $out 'FastDedup.Engine.dll'
foreach ($sym in 'Win32FindMetadataEnumerator','BackendSelector','UsnEnumerator') {
    Check (Select-String -Path $engine -Pattern $sym -Quiet) "FastDedup.Engine.dll contains '$sym'"
}

# New Core symbols too.
$core = Join-Path $out 'Deduplicator.Core.dll'
foreach ($sym in 'ExactDuplicateEngineV2','ProgressCoalescer','VolumeParallelExecutor') {
    Check (Select-String -Path $core -Pattern $sym -Quiet) "Deduplicator.Core.dll contains '$sym'"
}

if ($fail) { Write-Host "`nPUBLISH FAILED VERIFICATION - do not distribute this dist." -ForegroundColor Red; exit 1 }
Write-Host "`nDone: $out" -ForegroundColor Green
