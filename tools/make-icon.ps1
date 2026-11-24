# Builds src/DropLink/app.ico from tools/icon-source.png.
# Pure ASCII, Windows PowerShell 5.1 safe. Multi-size ICO.
#
# Two things here are not cosmetic details:
#
# 1. Frames up to 128 px are written as uncompressed 32bpp DIB, not PNG. GDI+
#    (System.Drawing.Icon, which the tray uses) does not reliably pick a
#    PNG-compressed frame; the shell and GDI+ disagree about it, and the visible
#    result is a wrong-sized or blurry icon. PNG is kept only for 256 px, which
#    is the one size where every consumer expects it.
#
# 2. The artwork is cropped to its own bounding box before scaling. The design
#    file is a 1254 px canvas with the tile inset about 10% on every side, so a
#    naive full-canvas downscale produced a tile that filled only ~80% of the
#    icon frame - next to other taskbar icons it looked one size smaller.
#
# The design lives in tools/icon-source.png; this script only rasterises it.
# make-icon-legacy.ps1 holds the previous vector version for reference.
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

function Get-ArtworkBox([System.Drawing.Image]$image) {
    # Scan a 256 px probe instead of the full canvas: 65k pixels is instant,
    # 1.5M is not, and the answer only needs to be accurate to a few source
    # pixels.
    $probeSize = 256
    $probe = New-Object System.Drawing.Bitmap($probeSize, $probeSize, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($probe)
    try {
        $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.DrawImage($image, 0, 0, $probeSize, $probeSize)
    } finally { $graphics.Dispose() }

    $rect = New-Object System.Drawing.Rectangle(0, 0, $probeSize, $probeSize)
    $data = $probe.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $stride = $data.Stride
        $bytes = New-Object byte[] ($stride * $probeSize)
        [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $bytes, 0, $bytes.Length)
    } finally { $probe.UnlockBits($data); $probe.Dispose() }

    $minX = $probeSize; $minY = $probeSize; $maxX = -1; $maxY = -1
    for ($y = 0; $y -lt $probeSize; $y++) {
        $row = $y * $stride
        for ($x = 0; $x -lt $probeSize; $x++) {
            if ($bytes[$row + $x * 4 + 3] -gt 8) {
                if ($x -lt $minX) { $minX = $x }
                if ($x -gt $maxX) { $maxX = $x }
                if ($y -lt $minY) { $minY = $y }
                if ($y -gt $maxY) { $maxY = $y }
            }
        }
    }
    if ($maxX -lt 0) { return New-Object System.Drawing.RectangleF(0, 0, $image.Width, $image.Height) }

    $scaleX = $image.Width / $probeSize
    $scaleY = $image.Height / $probeSize
    $left = [single]($minX * $scaleX)
    $top = [single]($minY * $scaleY)
    $width = [single](($maxX - $minX + 1) * $scaleX)
    $height = [single](($maxY - $minY + 1) * $scaleY)

    # Icon frames are square. Take a square window centred on the artwork so the
    # scale stays uniform: the design tile is 1004x1029, and stretching that into
    # a square is a 2.5% squash - small, but it is still a distorted logo.
    $side = [math]::Min([math]::Max($width, $height), [math]::Min($image.Width, $image.Height))
    $centreX = $left + $width / 2
    $centreY = $top + $height / 2
    $x = [math]::Max(0, [math]::Min($centreX - $side / 2, $image.Width - $side))
    $y = [math]::Max(0, [math]::Min($centreY - $side / 2, $image.Height - $side))
    return New-Object System.Drawing.RectangleF([single]$x, [single]$y, [single]$side, [single]$side)
}

function Get-DibFrame([System.Drawing.Bitmap]$bitmap) {
    # ICO stores DIBs bottom-up, BGRA, with a 1bpp AND mask after the pixels.
    # The mask is all zero because the alpha channel decides transparency; it
    # still has to be present and correctly sized or the icon is rejected.
    $size = $bitmap.Width
    $rect = New-Object System.Drawing.Rectangle(0, 0, $size, $size)
    $data = $bitmap.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $stride = $data.Stride
        $pixels = New-Object byte[] ($stride * $size)
        [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $pixels, 0, $pixels.Length)
    } finally { $bitmap.UnlockBits($data) }

    $maskStride = [int]([math]::Ceiling($size / 8.0 / 4.0) * 4)
    $maskSize = $maskStride * $size

    $buffer = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter($buffer)
    try {
        $writer.Write([UInt32]40)             # biSize
        $writer.Write([Int32]$size)           # biWidth
        $writer.Write([Int32]($size * 2))     # biHeight: XOR image + AND mask
        $writer.Write([UInt16]1)              # biPlanes
        $writer.Write([UInt16]32)             # biBitCount
        $writer.Write([UInt32]0)              # BI_RGB
        $writer.Write([UInt32]($size * $size * 4))
        $writer.Write([Int32]0); $writer.Write([Int32]0)
        $writer.Write([UInt32]0); $writer.Write([UInt32]0)
        for ($y = $size - 1; $y -ge 0; $y--) {
            $writer.Write($pixels, $y * $stride, $size * 4)
        }
        $writer.Write((New-Object byte[] $maskSize), 0, $maskSize)
        $writer.Flush()
        return $buffer.ToArray()
    } finally {
        $writer.Dispose()
        $buffer.Dispose()
    }
}

$src = [System.Drawing.Image]::FromFile((Resolve-Path -LiteralPath $Source).Path)
$box = Get-ArtworkBox $src
Write-Output ('artwork box=' + [int]$box.X + ',' + [int]$box.Y + ' ' + [int]$box.Width + 'x' + [int]$box.Height +
              ' of ' + $src.Width + 'x' + $src.Height)
$frames = New-Object System.Collections.ArrayList
$previews = New-Object System.Collections.ArrayList
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
        # Crop the empty canvas margin, then fill the frame with the artwork.
        $dest = New-Object System.Drawing.Rectangle(0, 0, $size, $size)
        $g.DrawImage($src, $dest, $box.X, $box.Y, $box.Width, $box.Height, [System.Drawing.GraphicsUnit]::Pixel)
        $g.Dispose()

        if ($size -ge 256) {
            $buffer = New-Object System.IO.MemoryStream
            $bmp.Save($buffer, [System.Drawing.Imaging.ImageFormat]::Png)
            [void]$frames.Add([pscustomobject]@{ Size = $size; Data = $buffer.ToArray() })
            $buffer.Dispose()
        } else {
            [void]$frames.Add([pscustomobject]@{ Size = $size; Data = (Get-DibFrame $bmp) })
        }
        if ($Preview) {
            $buffer = New-Object System.IO.MemoryStream
            $bmp.Save($buffer, [System.Drawing.Imaging.ImageFormat]::Png)
            [void]$previews.Add([pscustomobject]@{ Size = $size; Data = $buffer.ToArray() })
            $buffer.Dispose()
        }
        $bmp.Dispose()
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
    foreach ($frame in $previews) {
        $path = Join-Path $dir ('icon-' + $frame.Size + '.png')
        [System.IO.File]::WriteAllBytes($path, $frame.Data)
    }
    Write-Output ('preview=' + $dir)
}
