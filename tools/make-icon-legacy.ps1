# Builds src/DropLink/app.ico from vector drawing primitives.
# Pure ASCII, Windows PowerShell 5.1 safe. Multi-size PNG-compressed ICO.
param(
    [string]$Out = '',
    [switch]$Preview
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

if ([string]::IsNullOrWhiteSpace($Out)) {
    $Out = Join-Path (Split-Path -Parent $PSScriptRoot) 'src\DropLink\app.ico'
}

function New-TileBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    # Rounded square background with a blue-to-cyan diagonal gradient.
    $pad = [float][math]::Max(0.5, $size * 0.03)
    $rect = New-Object System.Drawing.RectangleF($pad, $pad, [float]($size - 2 * $pad), [float]($size - 2 * $pad))
    $d = [float]($size * 0.46)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
    $path.AddArc([float]($rect.Right - $d), $rect.Y, $d, $d, 270, 90)
    $path.AddArc([float]($rect.Right - $d), [float]($rect.Bottom - $d), $d, $d, 0, 90)
    $path.AddArc($rect.X, [float]($rect.Bottom - $d), $d, $d, 90, 90)
    $path.CloseFigure()

    $c1 = [System.Drawing.Color]::FromArgb(255, 43, 92, 255)
    $c2 = [System.Drawing.Color]::FromArgb(255, 22, 198, 255)
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, $c1, $c2, 50.0)
    $g.FillPath($grad, $path)

    # White incoming-transfer arrow: shaft, head, and a landing line.
    $white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
    $cx = [float]($size / 2.0)
    $shaftW = [float]($size * 0.155)
    $headW = [float]($size * 0.46)
    $top = [float]($size * 0.20)
    $tipY = [float]($size * 0.585)

    $pts = New-Object 'System.Drawing.PointF[]' 7
    $pts[0] = New-Object System.Drawing.PointF([float]($cx - $shaftW / 2), $top)
    $pts[1] = New-Object System.Drawing.PointF([float]($cx + $shaftW / 2), $top)
    $pts[2] = New-Object System.Drawing.PointF([float]($cx + $shaftW / 2), [float]($tipY - $headW / 2))
    $pts[3] = New-Object System.Drawing.PointF([float]($cx + $headW / 2), [float]($tipY - $headW / 2))
    $pts[4] = New-Object System.Drawing.PointF($cx, $tipY)
    $pts[5] = New-Object System.Drawing.PointF([float]($cx - $headW / 2), [float]($tipY - $headW / 2))
    $pts[6] = New-Object System.Drawing.PointF([float]($cx - $shaftW / 2), [float]($tipY - $headW / 2))
    $arrow = New-Object System.Drawing.Drawing2D.GraphicsPath
    $arrow.AddPolygon($pts)
    $g.FillPath($white, $arrow)

    # Landing line.
    $lineH = [float][math]::Max(1.4, $size * 0.072)
    $lineY = [float]($size * 0.745)
    $lineW = [float]($headW * 0.92)
    $lineRect = New-Object System.Drawing.RectangleF([float]($cx - $lineW / 2), $lineY, $lineW, $lineH)
    $g.FillRectangle($white, $lineRect)

    $g.Dispose()
    return $bmp
}

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$payloads = New-Object 'System.Collections.Generic.List[byte[]]'

foreach ($s in $sizes) {
    $bmp = New-TileBitmap $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $payloads.Add($ms.ToArray())
    if ($Preview -and $s -eq 256) {
        $previewPath = [System.IO.Path]::ChangeExtension($Out, '.png')
        $bmp.Save($previewPath, [System.Drawing.Imaging.ImageFormat]::Png)
        Write-Output ('preview=' + $previewPath)
    }
    $ms.Dispose()
    $bmp.Dispose()
}

$dir = Split-Path -Parent $Out
if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }

$fs = [System.IO.File]::Create($Out)
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([uint16]0)               # reserved
$bw.Write([uint16]1)               # type: icon
$bw.Write([uint16]$sizes.Count)

$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $dim = if ($s -ge 256) { [byte]0 } else { [byte]$s }
    $bw.Write($dim)                # width
    $bw.Write($dim)                # height
    $bw.Write([byte]0)             # palette
    $bw.Write([byte]0)             # reserved
    $bw.Write([uint16]1)           # planes
    $bw.Write([uint16]32)          # bits per pixel
    $bw.Write([uint32]$payloads[$i].Length)
    $bw.Write([uint32]$offset)
    $offset += $payloads[$i].Length
}
foreach ($p in $payloads) { $bw.Write($p) }
$bw.Flush()
$bw.Dispose()
$fs.Dispose()

$info = Get-Item -LiteralPath $Out
Write-Output ('OK ' + $Out + ' ' + $info.Length + ' bytes ' + $sizes.Count + ' sizes')
