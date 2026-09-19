<#
.SYNOPSIS
  Builds the 'git' fixture: a data folder that is a real repository, plus a bare
  remote to push to.

.DESCRIPTION
  Generated rather than committed. The scenario needs a real .git directory at
  the data root -- which is exactly what detection keys on -- and a nested
  repository inside this one is more trouble than it is worth. The seed content
  is committed under fixtures/git/seed; everything this creates is gitignored.

  Also plants a conflicting commit on the remote, so the rebase-conflict path can
  be exercised offline instead of only being discovered in production.

.EXAMPLE
  ./scripts/init-git-fixture.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$fixture = Join-Path $root 'fixtures/git'
$data = Join-Path $fixture 'data'
$remote = Join-Path $fixture 'remote.git'
$seed = Join-Path $fixture 'seed'

foreach ($path in @($data, $remote)) {
    if (Test-Path $path) {
        Remove-Item $path -Recurse -Force
    }
}

Write-Host "Creating bare remote at $remote" -ForegroundColor Cyan
git init --bare --initial-branch=main $remote | Out-Null

Write-Host "Creating data repository at $data" -ForegroundColor Cyan
New-Item -ItemType Directory -Path (Join-Path $data 'menu') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $data 'shopping') -Force | Out-Null

if (Test-Path $seed) {
    Copy-Item (Join-Path $seed '*') $data -Recurse -Force -ErrorAction SilentlyContinue
}

Push-Location $data
try {
    git init --initial-branch=main | Out-Null
    git config user.name 'KitchenCore Fixture'
    git config user.email 'fixture@local'
    git add -A
    git commit -m 'Seed the menu' --quiet
    git remote add origin $remote
    git push -u origin main --quiet

    # A commit that exists only on the remote, so the next push has to rebase --
    # and a conflicting edit to the same day makes that rebase fail, which is the
    # path the admin page has to report rather than silently resolve.
    $clone = Join-Path ([System.IO.Path]::GetTempPath()) ("kc-conflict-" + [guid]::NewGuid().ToString('N'))
    git clone $remote $clone --quiet

    $menu = Join-Path $clone 'menu/2026.yaml'
    Set-Content -Path $menu -Value @'
year: 2026
days:
  2026-09-21:
    dinner:
      title: Something else entirely
'@

    Push-Location $clone
    try {
        git config user.name 'Someone Else'
        git config user.email 'other@local'
        git add -A
        git commit -m 'Conflicting change made elsewhere' --quiet
        git push --quiet
    }
    finally {
        Pop-Location
    }

    Remove-Item $clone -Recurse -Force -ErrorAction SilentlyContinue
}
finally {
    Pop-Location
}

Write-Host "Done. Run with: ./scripts/dev.ps1 git" -ForegroundColor Green
