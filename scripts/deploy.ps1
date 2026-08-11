<#
.SYNOPSIS
    Publishes BrowserSync and installs it somewhere permanent, then restarts it.

.DESCRIPTION
    Builds a self-contained single-file exe and copies it to a stable install directory,
    OUTSIDE the repo on purpose: the "Start with Windows" tray option stores whatever path the
    running exe came from, so pointing it at bin\Release\... would break the moment the repo is
    cleaned, rebuilt or moved.

    Any running instance is stopped first. It holds its own DLLs open, so publishing over the
    top of a live process fails with a file lock rather than anything self-explanatory.

.PARAMETER InstallDir
    Where to install. Defaults to %LOCALAPPDATA%\BrowserSync\app, alongside the database and
    logs the host already keeps in %LOCALAPPDATA%\BrowserSync.

.PARAMETER StartWithWindows
    Also register the installed exe to run at login (HKCU Run key, no admin needed). Left off by
    default so deploying doesn't quietly change startup behaviour; the tray has the same toggle.

.PARAMETER NoStart
    Install without launching it afterwards.

.EXAMPLE
    .\scripts\deploy.ps1

.EXAMPLE
    .\scripts\deploy.ps1 -StartWithWindows
#>
[CmdletBinding()]
param(
    [string]$InstallDir = "$env:LOCALAPPDATA\BrowserSync\app",
    [switch]$StartWithWindows,
    [switch]$NoStart
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$hostProject = Join-Path $repoRoot 'src\BrowserSync.Host\BrowserSync.Host.csproj'
$publishDir = Join-Path $repoRoot 'artifacts\publish'
$exeName = 'BrowserSync.Host.exe'

if (-not (Test-Path $hostProject)) {
    throw "Could not find $hostProject - run this from the BrowserSync repo."
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'dotnet SDK not found on PATH.'
}

Write-Host '==> Stopping any running BrowserSync host' -ForegroundColor Cyan
$running = Get-Process -Name 'BrowserSync.Host' -ErrorAction SilentlyContinue
if ($running) {
    $running | Stop-Process -Force
    # Wait for the handles to actually drop; Stop-Process returns before the OS has released
    # the file locks, and publishing into them fails.
    $deadline = (Get-Date).AddSeconds(10)
    while ((Get-Process -Name 'BrowserSync.Host' -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 200
    }
    Write-Host "    stopped $($running.Count) instance(s)"
} else {
    Write-Host '    nothing running'
}

Write-Host '==> Publishing (self-contained, single file)' -ForegroundColor Cyan
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

# IncludeNativeLibrariesForSelfExtract matters: SQLite ships a native e_sqlite3 library, and
# without this the single-file exe builds fine but fails at runtime when it first opens the DB.
dotnet publish $hostProject `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    --output $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

$publishedExe = Join-Path $publishDir $exeName
if (-not (Test-Path $publishedExe)) { throw "Publish succeeded but $exeName is missing from $publishDir." }

Write-Host "==> Installing to $InstallDir" -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Copy-Item (Join-Path $publishDir '*') -Destination $InstallDir -Recurse -Force
$installedExe = Join-Path $InstallDir $exeName

if ($StartWithWindows) {
    Write-Host '==> Registering to start at login' -ForegroundColor Cyan
    $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    Set-ItemProperty -Path $runKey -Name 'BrowserSync' -Value "`"$installedExe`""
    Write-Host "    HKCU\...\Run\BrowserSync -> $installedExe"
}

if (-not $NoStart) {
    Write-Host '==> Starting' -ForegroundColor Cyan
    Start-Process -FilePath $installedExe -WorkingDirectory $InstallDir
    Start-Sleep -Seconds 3
    if (Get-Process -Name 'BrowserSync.Host' -ErrorAction SilentlyContinue) {
        Write-Host '    running (look for the B icon in the system tray)'
    } else {
        Write-Warning "    it exited immediately - check $env:LOCALAPPDATA\BrowserSync\logs"
    }
}

Write-Host ''
Write-Host 'Done.' -ForegroundColor Green
Write-Host "  Installed:  $installedExe"
Write-Host "  Data/logs:  $env:LOCALAPPDATA\BrowserSync"
Write-Host ''
Write-Host 'The extension still has to be loaded by hand in each browser:' -ForegroundColor Yellow
Write-Host '  chrome://extensions and edge://extensions -> Developer mode -> Load unpacked'
Write-Host "  $(Join-Path $repoRoot 'extension')"
Write-Host '  (use the reload icon for updates - removing and re-adding is not needed)'
