# Builds src/DropLink/app.ico from tools/icon-source.png.
# Pure ASCII, Windows PowerShell 5.1 safe. Multi-size, PNG-compressed ICO.
#
# The icon is an asset, not something drawn at build time: the design lives in
# tools/icon-source.png and this script only rasterises it to the sizes Windows
# actually asks for. make-icon-legacy.ps1 holds the previous vector version for
# reference; nothing calls it.
param(
    [string]$Source = '',
    [string]$Out = '',
    [int[]]$Sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256),
    [switch]$Preview
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

if ([string]::IsNullOrWhiteSpace($Source)) {
    $Source = Join-Path $PSScriptRoot 'icon-source.png'
}
if (-not (Test-Path -LiteralPath $Source)) { throw ('icon source is missing: ' + $Source) }
if ([string]::IsNullOrWhiteSpace($Out)) {
    $Out = Join-Path (Split-Path -Parent $PSScriptRoot) 'src\DropLink\app.ico'
}

$src = [System.Drawing.Image]::FromFile((Resolve-Path -LiteralPath $Source).Path)
$frames = New-Object System.Collections.ArrayList
try {
    foreach ($size in $Sizes) {
        $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        # SourceCopy keeps the alpha channel instead of blending it onto the
        # default black canvas, which is what made earlier conversions come out
        # with dark fringes around the arcs.
        $g.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
        $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $g.Clear([System.Drawing.Color]::Transparent)
        $g.DrawImage($src, 0, 0, $size, $size)
        $g.Dispose()
        $buffer = New-Object System.IO.MemoryStream
        $bmp.Save($buffer, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
        [void]$frames.Add([pscustomobject]@{ Size = $size; Data = $buffer.ToArray() })
        $buffer.Dispose()
    }
} finally {
    $src.Dispose()
}

if ($frames.Count -eq 0) { throw 'no icon frames were produced' }

$imageOffset = 6 + 16 * $frames.Count
$stream = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter($stream)
try {
    # ICONDIR
    $writer.Write([UInt16]0)
    $writer.Write([UInt16]1)
    $writer.Write([UInt16]$frames.Count)
    # ICONDIRENTRY per frame; 256 is encoded as 0.
    foreach ($frame in $frames) {
        $dim = if ($frame.Size -ge 256) { 0 } else { $frame.Size }
        $writer.Write([byte]$dim)
        $writer.Write([byte]$dim)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([UInt16]1)
        $writer.Write([UInt16]32)
        $writer.Write([UInt32]$frame.Data.Length)
        $writer.Write([UInt32]$imageOffset)
        $imageOffset += $frame.Data.Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Data) }
    $writer.Flush()
    [System.IO.File]::WriteAllBytes($Out, $stream.ToArray())
} finally {
    $writer.Dispose()
    $stream.Dispose()
}

$info = Get-Item -LiteralPath $Out
Write-Output ('OK ' + $info.FullName)
Write-Output ('bytes=' + $info.Length)
Write-Output ('frames=' + (($frames | ForEach-Object { $_.Size }) -join ','))

if ($Preview) {
    $dir = Join-Path $env:TEMP 'droplink-icon-preview'
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    foreach ($frame in $frames) {
        $path = Join-Path $dir ('icon-' + $frame.Size + '.png')
        [System.IO.File]::WriteAllBytes($path, $frame.Data)
    }
    Write-Output ('preview=' + $dir)
}
