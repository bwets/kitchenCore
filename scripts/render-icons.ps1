<#
.SYNOPSIS
  Renders every raster icon from the SVG sources.

.DESCRIPTION
  The icon is the header's brand mark (Components/BrandMark.razor). Three SVGs
  carry it:

    resources/icon.svg                       the tile, rounded like the header's
    resources/icon-maskable.svg              full-bleed, for platforms that cut their own shape
    src/KitchenCore.Client/wwwroot/favicon.svg   heavier stroke, for a 16px tab

  and this script draws the PNGs and the .ico from them with headless Chrome, so
  the rasters cannot drift from the mark. The outputs are committed: building
  the app never needs a browser. Android's adaptive icon is a vector drawable
  of the same paths and is not rendered here.

  Run it after changing any of the SVGs.
#>
[CmdletBinding()]
param(
    [string] $Chrome
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

if (-not $Chrome) {
    $Chrome = @(
        "$env:ProgramFiles\Google\Chrome\Application\chrome.exe",
        "${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe",
        "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe",
        "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe"
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
}

if (-not $Chrome) {
    throw 'Chrome or Edge is needed to render the icons; pass -Chrome <path>.'
}

$work = Join-Path ([IO.Path]::GetTempPath()) "kitchencore-icons-$PID"
New-Item -ItemType Directory -Force $work | Out-Null

$icon = Join-Path $root 'resources/icon.svg'
$maskable = Join-Path $root 'resources/icon-maskable.svg'
$favicon = Join-Path $root 'src/KitchenCore.Client/wwwroot/favicon.svg'

# Draws one SVG at one size. The SVG is inlined rather than loaded through an
# <img>: Chrome will not load a file:// image into a file:// page.
function Render([string] $svgPath, [int] $size, [string] $output) {
    $svg = (Get-Content $svgPath -Raw) -replace '(?s)<!--.*?-->', ''
    $svg = $svg.Trim() -replace '<svg ', "<svg width=""$size"" height=""$size"" style=""display:block"" "

    $page = Join-Path $work "page-$size.html"
    "<!doctype html><html><head><style>html,body{margin:0;background:transparent}</style></head><body>$svg</body></html>" |
        Set-Content $page -Encoding utf8

    $shot = Join-Path $work "shot-$size.png"
    Remove-Item $shot -ErrorAction SilentlyContinue

    & $Chrome --headless=new --disable-gpu --hide-scrollbars --default-background-color=00000000 `
        --force-device-scale-factor=1 --blink-settings=preferredColorScheme=1 "--window-size=$size,$size" "--screenshot=$shot" `
        ('file:///' + ($page -replace '\\', '/')) 2>&1 | Out-Null

    if (-not (Test-Path $shot)) {
        throw "Rendering $svgPath at $size failed."
    }

    New-Item -ItemType Directory -Force (Split-Path $output -Parent) | Out-Null
    Move-Item $shot $output -Force

    if ($output.StartsWith($root)) {
        Write-Host "  $($output.Substring($root.Length + 1)) ($size)"
    }
}

# An .ico is a directory of PNGs; every Windows version since Vista reads them.
function Write-Ico([string[]] $pngs, [int[]] $sizes, [string] $output) {
    $images = $pngs | ForEach-Object { , [IO.File]::ReadAllBytes($_) }
    $stream = [IO.File]::Create($output)
    $writer = [IO.BinaryWriter]::new($stream)

    try {
        $writer.Write([uint16]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]$images.Count)

        $offset = 6 + 16 * $images.Count

        for ($i = 0; $i -lt $images.Count; $i++) {
            $side = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
            $writer.Write([byte]$side)
            $writer.Write([byte]$side)
            $writer.Write([byte]0)
            $writer.Write([byte]0)
            $writer.Write([uint16]1)
            $writer.Write([uint16]32)
            $writer.Write([uint32]$images[$i].Length)
            $writer.Write([uint32]$offset)
            $offset += $images[$i].Length
        }

        foreach ($image in $images) {
            $writer.Write($image)
        }
    }
    finally {
        $writer.Dispose()
    }

    Write-Host "  $($output.Substring($root.Length + 1)) ($($sizes -join ', '))"
}

try {
    Write-Host 'Web'
    $web = Join-Path $root 'src/KitchenCore.Client/wwwroot'
    Render $favicon 32 "$web/favicon.png"
    Render $icon 192 "$web/icon-192.png"
    Render $icon 512 "$web/icon-512.png"
    Render $maskable 512 "$web/icon-maskable-512.png"
    Render $maskable 180 "$web/apple-touch-icon.png"

    Write-Host 'Windows'
    Render $icon 512 "$root/resources/icon_512.png"

    # The tab-tuned glyph for the sizes Explorer shows in lists, the tile above.
    $sizes = 16, 24, 32, 48, 64, 128, 256
    $pngs = foreach ($size in $sizes) {
        $png = Join-Path $work "ico-$size.png"
        Render ($size -le 32 ? $favicon : $icon) $size $png
        $png
    }
    Write-Ico $pngs $sizes "$root/resources/icon.ico"

    # Android before 8.0 has no adaptive icons and shows these as they are.
    Write-Host 'Android'
    $densities = @{ mdpi = 48; hdpi = 72; xhdpi = 96; xxhdpi = 144; xxxhdpi = 192 }
    foreach ($density in $densities.GetEnumerator()) {
        Render $icon $density.Value "$root/src/KitchenCore.Android/Resources/mipmap-$($density.Key)/ic_launcher.png"
    }
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
