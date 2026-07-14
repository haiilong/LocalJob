#!/usr/bin/env powershell
# Launches 3 instances of the LocalJob.Sample worker. No infrastructure needed — LocalJob is in-memory.
# Each instance opens in its own PowerShell window so you can see ALL THREE ticking (unlike SingletonJob,
# where only the leader runs). With the sample's 2s Jitter, each window's schedule is offset differently.

$ErrorActionPreference = 'Stop'
$proj = Join-Path $PSScriptRoot 'LocalJob.Sample\LocalJob.Sample.csproj'

if (-not (Test-Path $proj)) {
    Write-Error "Cannot find $proj"
    exit 1
}

# Build ONCE before spawning workers. Three parallel `dotnet run` invocations would otherwise race on the
# Roslyn analyzer DLL (LocalJob.SourceGenerator.dll) and fail with CS2012 file-in-use.
Write-Host "Building $proj (Release) ..."
dotnet build $proj -c Release | Out-Host
if ($LASTEXITCODE -ne 0) {
    Write-Error "Build failed."
    exit 1
}

# Prefer PowerShell 7+ (pwsh) if installed; fall back to Windows PowerShell 5.1 (powershell).
$shell = if (Get-Command pwsh -ErrorAction SilentlyContinue) { 'pwsh' } else { 'powershell' }

for ($i = 1; $i -le 3; $i++) {
    Start-Process $shell -ArgumentList @(
        '-NoExit',
        '-Command',
        "`$Host.UI.RawUI.WindowTitle='LocalJob worker #$i'; dotnet run --project `"$proj`" -c Release --no-build"
    )
    Start-Sleep -Milliseconds 400
}

Write-Host "Three workers launched ($shell). Every window ticks — that's the point of LocalJob."
Write-Host "Note how the jitter offsets each window's schedule so they don't fire in lockstep."
