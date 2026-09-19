<#
.SYNOPSIS
  Runs KitchenCore against a fixture scenario.

.DESCRIPTION
  Stops any server already running first. A stale process keeps a lock on the
  build output, and -- worse -- if it is still bound to the port the new run
  fails silently and the old one keeps serving an asset manifest that no longer
  matches the rebuilt files, which shows up in the browser as a 404 on
  _framework/dotnet.js rather than as anything resembling the real cause.

.EXAMPLE
  ./scripts/dev.ps1 basic
  ./scripts/dev.ps1 duplicates
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$Scenario = 'basic'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

Get-CimInstance Win32_Process -Filter "Name='KitchenCore.Server.exe'" |
    ForEach-Object {
        Write-Host "Stopping server $($_.ProcessId)" -ForegroundColor DarkYellow
        Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
    }

Start-Sleep -Milliseconds 500

Write-Host "Building..." -ForegroundColor Cyan
dotnet build "$root/KitchenCore.slnx" -v q --nologo
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

Write-Host "Running scenario '$Scenario'" -ForegroundColor Green
dotnet run --project "$root/src/KitchenCore.Server" --launch-profile $Scenario
