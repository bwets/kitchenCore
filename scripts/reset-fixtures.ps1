<#
.SYNOPSIS
  Puts fixtures/ back to what is committed.

.DESCRIPTION
  Dev runs write into the scenario they are pointed at -- that is deliberate, and
  'git diff fixtures/' is how you see what the app wrote. But 'git checkout' only
  restores TRACKED files, and the app also creates files that were never
  committed: config/devices.yaml the moment a device asks for access, and
  requests.yaml the moment somebody asks for a meal. Those survive a checkout and
  then leak into the next test run, where they look like inexplicable failures.

  So: checkout for modified files, clean for new ones. Ignored paths are left
  alone, which is what keeps the generated git fixture from being wiped.

.EXAMPLE
  ./scripts/reset-fixtures.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

Push-Location $root
try {
    git checkout -- fixtures/
    git clean -fd fixtures/ | ForEach-Object { Write-Host $_ -ForegroundColor DarkYellow }
    Write-Host "fixtures/ reset." -ForegroundColor Green
}
finally {
    Pop-Location
}
