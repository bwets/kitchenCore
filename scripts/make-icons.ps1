<#
.SYNOPSIS
  Generates the app icon files from the same fork-and-knife mark the UI uses.

.DESCRIPTION
  Drawn in code rather than committed as a binary blob nobody can diff, and kept
  in step with Components/BrandMark.razor by hand -- there is one shape, and it
  is simple enough that a rasteriser dependency would cost more than it saves.

  Produces resources/icon_512.png (the source the MSIX tile logos are resized
  from) and resources/icon.ico (the embedded exe icon).

.EXAMPLE
  ./scripts/make-icons.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'resources'
New-Item -ItemType Directory -Force -Path $out | Out-Null

# The app's primary green, from Styles/themes/_light.scss.
$bg = [System.Drawing.ColorTranslator]::FromHtml('#3c6b4f')
$fg = [System.Drawing.Color]::White

function New-Icon([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)

    # Rounded square, matching the header mark's shape.
    $r = [int]($size * 0.22)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, 2*$r, 2*$r, 180, 90)
    $path.AddArc($size-2*$r, 0, 2*$r, 2*$r, 270, 90)
    $path.AddArc($size-2*$r, $size-2*$r, 2*$r, 2*$r, 0, 90)
    $path.AddArc(0, $size-2*$r, 2*$r, 2*$r, 90, 90)
    $path.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush $bg), $path)

    $pen = New-Object System.Drawing.Pen $fg, ($size * 0.052)
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'; $pen.LineJoin = 'Round'

    $u = $size / 24.0   # the SVG's 24-unit viewBox, scaled

    # Fork: three tines joined into a stem.
    $g.DrawLine($pen, 7*$u, 5*$u, 7*$u, 10*$u)
    $g.DrawLine($pen, 11*$u, 5*$u, 11*$u, 10*$u)
    $g.DrawLine($pen, 7*$u, 10*$u, 11*$u, 10*$u)
    $g.DrawLine($pen, 9*$u, 10*$u, 9*$u, 19*$u)

    # Knife.
    $g.DrawLine($pen, 16*$u, 5*$u, 16*$u, 19*$u)
    $g.DrawArc($pen, 14*$u, 4.5*$u, 4*$u, 7*$u, -90, 180)

    $g.Dispose()
    $pen.Dispose()
    return $bmp
}

$png = New-Icon 512
$pngPath = Join-Path $out 'icon_512.png'
$png.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png)
Write-Host "wrote $pngPath" -ForegroundColor Green

# A multi-size .ico for the executable: Explorer and the taskbar pick the size
# they need, and a single large frame scales badly in the small ones.
$sizes = 16, 32, 48, 64, 128, 256
$frames = $sizes | ForEach-Object {
    $b = New-Icon $_
    $ms = New-Object System.IO.MemoryStream
    $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $b.Dispose()
    [pscustomobject]@{ Size = $_; Bytes = $ms.ToArray() }
}

$icoPath = Join-Path $out 'icon.ico'
$fs = [System.IO.File]::Create($icoPath)
$bw = New-Object System.IO.BinaryWriter $fs
try {
    $bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$frames.Count)
    $offset = 6 + (16 * $frames.Count)

    foreach ($f in $frames) {
        $dim = if ($f.Size -ge 256) { 0 } else { $f.Size }
        $bw.Write([byte]$dim); $bw.Write([byte]$dim)
        $bw.Write([byte]0); $bw.Write([byte]0)
        $bw.Write([uint16]1); $bw.Write([uint16]32)
        $bw.Write([uint32]$f.Bytes.Length); $bw.Write([uint32]$offset)
        $offset += $f.Bytes.Length
    }

    foreach ($f in $frames) { $bw.Write($f.Bytes) }
}
finally {
    $bw.Dispose(); $fs.Dispose()
}

$png.Dispose()
Write-Host "wrote $icoPath" -ForegroundColor Green
