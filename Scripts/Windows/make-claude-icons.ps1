# Builds the Claude SyncBar Windows icons from the Ubuntu app mark:
# AppIcon.ico (window, taskbar, Start) and the tray icons with a status dot.
# Run with Windows PowerShell (System.Drawing). Output: Windows/ClaudeSyncBar.Windows.App/Assets.
param(
    [string]$Source = (Join-Path $PSScriptRoot '..\..\Ubuntu\resources\claude-syncbar.png'),
    [string]$Output = (Join-Path $PSScriptRoot '..\..\Windows\ClaudeSyncBar.Windows.App\Assets')
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force $Output | Out-Null
$mark = [System.Drawing.Image]::FromFile((Resolve-Path $Source))

function Render([int]$size, [string]$dot) {
    $bitmap = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.DrawImage($mark, 0, 0, $size, $size)
    if ($dot) {
        # A status dot in the lower right, ringed so it reads on light and dark taskbars.
        $d = [Math]::Max(6, [Math]::Round($size * 0.46))
        $x = $size - $d; $y = $size - $d
        $ring = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 32, 32, 32))
        $fill = New-Object System.Drawing.SolidBrush ([System.Drawing.ColorTranslator]::FromHtml($dot))
        $g.FillEllipse($ring, $x, $y, $d, $d)
        $inset = [Math]::Max(1, [Math]::Round($d * 0.16))
        $g.FillEllipse($fill, $x + $inset, $y + $inset, $d - 2 * $inset, $d - 2 * $inset)
    }
    $g.Dispose()
    $stream = New-Object System.IO.MemoryStream
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    return ,$stream.ToArray()
}

function Write-Ico([string]$path, [int[]]$sizes, [string]$dot) {
    $images = foreach ($s in $sizes) { ,(Render $s $dot) }
    $file = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter $file
    $w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $s = $sizes[$i]; $bytes = $images[$i]
        $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s }))); $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
        $w.Write([byte]0); $w.Write([byte]0); $w.Write([uint16]1); $w.Write([uint16]32)
        $w.Write([uint32]$bytes.Length); $w.Write([uint32]$offset)
        $offset += $bytes.Length
    }
    foreach ($bytes in $images) { $w.Write($bytes) }
    $w.Flush()
    [System.IO.File]::WriteAllBytes($path, $file.ToArray())
}

Write-Ico (Join-Path $Output 'AppIcon.ico') @(16, 20, 24, 32, 40, 48, 64, 256) $null
Write-Ico (Join-Path $Output 'Tray.ico') @(16, 20, 24, 32) $null
Write-Ico (Join-Path $Output 'TrayWarning.ico') @(16, 20, 24, 32) '#FCE100'
Write-Ico (Join-Path $Output 'TrayCritical.ico') @(16, 20, 24, 32) '#FF5F5F'
[System.IO.File]::WriteAllBytes((Join-Path $Output 'AppIcon.png'), (Render 64 $null))
$mark.Dispose()
Get-ChildItem $Output | Select-Object Name, Length
