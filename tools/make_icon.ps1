# Draws Abyss Remote's icon and writes every copy the app uses:
#   src/AbyssRemote.Host/Assets/AbyssRemote.ico      the Windows app, window and tray icon
#   src/AbyssRemote.Host/wwwroot/icon-192.png        browser tab and web-app icon
#   src/AbyssRemote.Host/wwwroot/icon-512.png
#   src/AbyssRemote.Host/wwwroot/icon-maskable-512.png   Android crops this one to its own shape
#   src/AbyssRemote.Host/wwwroot/apple-touch-icon.png    iPhone and iPad Home Screen
# The design: a monitor with a mouse pointer and a phone in front of it, drawn in the
# project's blue-to-cyan accent on the dark rounded tile the other Abyss apps use.
# Usage: powershell -ExecutionPolicy Bypass -File tools/make_icon.ps1 [-Preview <folder>]
param([string]$Preview)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function Get-Color([string]$hex, [int]$alpha = 255) {
    [System.Drawing.Color]::FromArgb($alpha, [System.Drawing.ColorTranslator]::FromHtml($hex))
}

function New-RoundRect([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    [single]$d = [Math]::Min(2 * $r, [Math]::Min($w, $h))
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($x, $y, $d, $d, [single]180, [single]90)
    $path.AddArc([single]($x + $w - $d), $y, $d, $d, [single]270, [single]90)
    $path.AddArc([single]($x + $w - $d), [single]($y + $h - $d), $d, $d, [single]0, [single]90)
    $path.AddArc($x, [single]($y + $h - $d), $d, $d, [single]90, [single]90)
    $path.CloseFigure()
    return ,$path
}

# $tile: draw the rounded tile with transparent corners (Windows, browser tab). Otherwise the
# background fills the whole square, for systems that cut the icon to their own shape.
# $motif: how much of the square the drawing uses (1 = the tile layout).
function New-IconBitmap([int]$size, [bool]$tile = $true, [single]$motif = 1) {
    [single]$s = $size
    [single]$c = $s / 2
    $bitmap = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    # Background: the Abyss tile, darker toward the bottom.
    $fill = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF([single]0, [single]-1)), (New-Object System.Drawing.PointF([single]0, [single]($s + 1))),
        (Get-Color '#2b3452'), (Get-Color '#090c13'))
    $tilePath = $null
    if ($tile) {
        [single]$inset = [Math]::Max(0.5, $s * 0.04)
        $tilePath = New-RoundRect $inset $inset ([single]($s - 2 * $inset)) ([single]($s - 2 * $inset)) ([single]($s * 0.22))
        $g.FillPath($fill, $tilePath)
    } else {
        $g.FillRectangle($fill, [single]0, [single]0, $s, $s)
    }

    # The drawing is laid out in a 0..1 square and placed around the centre.
    $X = { param($u) [single]($c + ($u - 0.5) * $s * $motif) }
    $Y = { param($u) [single]($c + ($u - 0.52) * $s * $motif) }
    $L = { param($u) [single]($u * $s * $motif) }

    $glowPath = New-Object System.Drawing.Drawing2D.GraphicsPath
    [single]$glowRadius = & $L 0.4
    $glowPath.AddEllipse([single]($c - $glowRadius), [single]($c - $glowRadius), [single](2 * $glowRadius), [single](2 * $glowRadius))
    $glow = New-Object System.Drawing.Drawing2D.PathGradientBrush($glowPath)
    $glow.CenterColor = Get-Color '#3b82f6' 95
    $glow.SurroundColors = [System.Drawing.Color[]]@(Get-Color '#3b82f6' 0)
    $g.FillPath($glow, $glowPath)

    # Small sizes drop the pointer and the phone's details and use heavier lines.
    $small = $size -le 24
    if ($small) { [single]$stroke = & $L 0.09 } else { [single]$stroke = & $L 0.05 }
    $accent = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF((& $X 0.14), (& $Y 0.2))), (New-Object System.Drawing.PointF((& $X 0.86), (& $Y 0.84))),
        (Get-Color '#3b82f6'), (Get-Color '#22d3ee'))
    $pen = New-Object System.Drawing.Pen($accent, $stroke)
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round

    # Monitor, stand and base.
    $monitor = New-RoundRect (& $X 0.165) (& $Y 0.235) (& $L 0.54) (& $L 0.34) (& $L 0.06)
    $g.DrawPath($pen, $monitor)
    $g.DrawLine($pen, (& $X 0.435), (& $Y 0.6), (& $X 0.435), (& $Y 0.69))
    $g.DrawLine($pen, (& $X 0.33), (& $Y 0.69), (& $X 0.54), (& $Y 0.69))

    # Mouse pointer on the monitor.
    if (-not $small) {
        [single]$k = & $L 0.2
        [single]$px = & $X 0.285
        [single]$py = & $Y 0.305
        $points = foreach ($p in @(@(0, 0), @(0, 0.8), @(0.19, 0.62), @(0.33, 0.93), @(0.45, 0.875), @(0.31, 0.57), @(0.57, 0.57))) {
            New-Object System.Drawing.PointF([single]($px + $p[0] * $k), [single]($py + $p[1] * $k))
        }
        $white = New-Object System.Drawing.SolidBrush((Get-Color '#f5faff'))
        $g.FillPolygon($white, [System.Drawing.PointF[]]$points)
        $white.Dispose()
    }

    # Phone in front: first repaint the background around it so the monitor's lines stop short.
    [single]$gap = if ($small) { 0.075 } else { 0.06 }
    $mask = New-RoundRect (& $X (0.575 - $gap)) (& $Y (0.385 - $gap)) (& $L (0.26 + 2 * $gap)) (& $L (0.425 + 2 * $gap)) (& $L (0.065 + $gap))
    $state = $g.Save()
    if ($tilePath) { $g.SetClip($tilePath) }
    $g.FillPath($fill, $mask)
    $g.Restore($state)
    $phone = New-RoundRect (& $X 0.575) (& $Y 0.385) (& $L 0.26) (& $L 0.425) (& $L 0.065)
    if ($small) {
        $g.FillPath($accent, $phone)
    } else {
        $g.DrawPath($pen, $phone)
        $screen = New-RoundRect (& $X 0.627) (& $Y 0.443) (& $L 0.156) (& $L 0.125) (& $L 0.014)
        $screenBrush = New-Object System.Drawing.SolidBrush((Get-Color '#2563eb' 150))
        $g.FillPath($screenBrush, $screen)
        [single]$dot = & $L 0.021
        $dotBrush = New-Object System.Drawing.SolidBrush((Get-Color '#22d3ee'))
        $g.FillEllipse($dotBrush, [single]((& $X 0.705) - $dot), [single]((& $Y 0.75) - $dot), [single](2 * $dot), [single](2 * $dot))
        $screen.Dispose(); $screenBrush.Dispose(); $dotBrush.Dispose()
    }

    if ($tilePath -and $size -ge 32) { # a faint rim keeps the tile visible on dark taskbars
        $rim = New-Object System.Drawing.Pen((Get-Color '#56618a' 140), [single][Math]::Max(1, $s / 128))
        $g.DrawPath($rim, $tilePath)
        $rim.Dispose()
    }
    $g.Dispose(); $fill.Dispose(); $glow.Dispose(); $glowPath.Dispose(); $accent.Dispose(); $pen.Dispose()
    $monitor.Dispose(); $mask.Dispose(); $phone.Dispose()
    if ($tilePath) { $tilePath.Dispose() }
    return $bitmap
}

function Save-Png($bitmap, [string]$path) {
    New-Item -ItemType Directory -Force (Split-Path -Parent $path) | Out-Null
    $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    Write-Output "wrote $path"
}

$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$web = Join-Path $root 'src\AbyssRemote.Host\wwwroot'

$images = foreach ($size in 16, 20, 24, 32, 40, 48, 64, 96, 128, 256) {
    $bitmap = New-IconBitmap $size
    $png = New-Object System.IO.MemoryStream
    $bitmap.Save($png, [System.Drawing.Imaging.ImageFormat]::Png)
    if ($Preview) { Save-Png $bitmap (Join-Path $Preview "icon-$size.png") | Out-Null } else { $bitmap.Dispose() }
    [pscustomobject]@{ Size = $size; Data = $png.ToArray() }
}

# ICO: a 6-byte header, a 16-byte entry per image, then the PNG data.
$file = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter($file)
$writer.Write([UInt16]0); $writer.Write([UInt16]1); $writer.Write([UInt16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($image in $images) {
    $dimension = if ($image.Size -ge 256) { 0 } else { $image.Size }
    $writer.Write([Byte]$dimension); $writer.Write([Byte]$dimension); $writer.Write([Byte]0); $writer.Write([Byte]0)
    $writer.Write([UInt16]1); $writer.Write([UInt16]32); $writer.Write([UInt32]$image.Data.Length); $writer.Write([UInt32]$offset)
    $offset += $image.Data.Length
}
foreach ($image in $images) { $writer.Write([byte[]]$image.Data) }
$writer.Flush()
$ico = Join-Path $root 'src\AbyssRemote.Host\Assets\AbyssRemote.ico'
New-Item -ItemType Directory -Force (Split-Path -Parent $ico) | Out-Null
[System.IO.File]::WriteAllBytes($ico, $file.ToArray())
Write-Output "wrote $ico ($($file.Length) bytes, $($images.Count) sizes)"

Save-Png (New-IconBitmap 192) (Join-Path $web 'icon-192.png')
Save-Png (New-IconBitmap 512) (Join-Path $web 'icon-512.png')
# Android keeps only the middle of a maskable icon, iOS rounds the corners itself.
Save-Png (New-IconBitmap 512 $false 0.72) (Join-Path $web 'icon-maskable-512.png')
Save-Png (New-IconBitmap 180 $false 0.9) (Join-Path $web 'apple-touch-icon.png')
