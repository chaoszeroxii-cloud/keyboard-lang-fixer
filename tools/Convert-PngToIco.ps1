<#
  Builds a multi-size Windows .ico from a PNG.

  Kept in the repo so the icon can be regenerated from a new source image
  instead of being an opaque binary nobody can reproduce.

      powershell -ExecutionPolicy Bypass -File tools\Convert-PngToIco.ps1 `
          -Source "C:\path\to\logo.png" -Destination icon.ico

  The source is cropped to its opaque content, padded back to a square and
  written as 32-bit BGRA entries (no PNG-compressed entries: .NET's own Icon
  parser is unreliable with those, and the tray icon is loaded through it).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Source,
    [Parameter(Mandatory)][string]$Destination,
    [int[]]$Sizes = @(16, 20, 24, 32, 48, 64, 128),
    # Share of the square canvas left empty around the artwork.
    [double]$Padding = 0.02
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

if (-not (Test-Path -LiteralPath $Source)) { throw "Source not found: $Source" }

$src = New-Object System.Drawing.Bitmap (Resolve-Path -LiteralPath $Source).Path
Write-Host ("source: {0} x {1} ({2})" -f $src.Width, $src.Height, $src.PixelFormat)

# ---- crop to the visible artwork -------------------------------------------
# LockBits rather than GetPixel: a 1536x1024 image is 1.5M pixels and GetPixel
# would take minutes.
$rect = New-Object System.Drawing.Rectangle 0, 0, $src.Width, $src.Height
$data = $src.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
                      [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$stride = $data.Stride
$bytes = New-Object byte[] ($stride * $src.Height)
[System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $bytes, 0, $bytes.Length)
$src.UnlockBits($data)

$minX = $src.Width; $minY = $src.Height; $maxX = -1; $maxY = -1
for ($y = 0; $y -lt $src.Height; $y++) {
    $row = $y * $stride
    for ($x = 0; $x -lt $src.Width; $x++) {
        if ($bytes[$row + $x * 4 + 3] -gt 8) {          # alpha channel
            if ($x -lt $minX) { $minX = $x }
            if ($x -gt $maxX) { $maxX = $x }
            if ($y -lt $minY) { $minY = $y }
            if ($y -gt $maxY) { $maxY = $y }
        }
    }
}
if ($maxX -lt 0) { throw "Source image is fully transparent." }
$cropW = $maxX - $minX + 1
$cropH = $maxY - $minY + 1
Write-Host ("artwork: {0},{1} {2} x {3}" -f $minX, $minY, $cropW, $cropH)

# ---- square canvas ----------------------------------------------------------
$side = [int]([Math]::Max($cropW, $cropH) * (1 + 2 * $Padding))
$square = New-Object System.Drawing.Bitmap $side, $side, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($square)
$g.Clear([System.Drawing.Color]::Transparent)
$g.InterpolationMode = 'HighQualityBicubic'
$g.PixelOffsetMode = 'HighQuality'
$g.DrawImage($src,
    (New-Object System.Drawing.Rectangle ([int](($side - $cropW) / 2)), ([int](($side - $cropH) / 2)), $cropW, $cropH),
    (New-Object System.Drawing.Rectangle $minX, $minY, $cropW, $cropH),
    [System.Drawing.GraphicsUnit]::Pixel)
$g.Dispose()
$src.Dispose()

# ---- one 32bpp BGRA DIB per size -------------------------------------------
function Get-IconImageBytes([System.Drawing.Bitmap]$canvas, [int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $gg = [System.Drawing.Graphics]::FromImage($bmp)
    $gg.Clear([System.Drawing.Color]::Transparent)
    $gg.InterpolationMode = 'HighQualityBicubic'
    $gg.PixelOffsetMode = 'HighQuality'
    $gg.SmoothingMode = 'AntiAlias'
    $gg.DrawImage($canvas, (New-Object System.Drawing.Rectangle 0, 0, $size, $size))
    $gg.Dispose()

    $r = New-Object System.Drawing.Rectangle 0, 0, $size, $size
    $d = $bmp.LockBits($r, [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
                       [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $st = $d.Stride
    $px = New-Object byte[] ($st * $size)
    [System.Runtime.InteropServices.Marshal]::Copy($d.Scan0, $px, 0, $px.Length)
    $bmp.UnlockBits($d)
    $bmp.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter $ms

    # BITMAPINFOHEADER. Height is doubled: the DIB holds the colour image and
    # the AND mask stacked on top of each other.
    $andStride = [int]([Math]::Floor(($size + 31) / 32)) * 4
    $bw.Write([uint32]40)
    $bw.Write([int32]$size)
    $bw.Write([int32]($size * 2))
    $bw.Write([uint16]1)
    $bw.Write([uint16]32)
    $bw.Write([uint32]0)                                  # BI_RGB
    $bw.Write([uint32]($size * $size * 4 + $andStride * $size))
    $bw.Write([int32]0); $bw.Write([int32]0)
    $bw.Write([uint32]0); $bw.Write([uint32]0)

    # Colour data, bottom-up.
    for ($y = $size - 1; $y -ge 0; $y--) {
        $bw.Write($px, $y * $st, $size * 4)
    }
    # AND mask, bottom-up. 32-bit icons ignore it, but it must be present and
    # sized correctly or some shell surfaces render garbage.
    $blank = New-Object byte[] $andStride
    for ($y = 0; $y -lt $size; $y++) { $bw.Write($blank, 0, $andStride) }

    $bw.Flush()
    $out = $ms.ToArray()
    $bw.Dispose(); $ms.Dispose()
    return $out
}

$images = @()
foreach ($s in ($Sizes | Sort-Object -Unique)) {
    $images += , @{ Size = $s; Bytes = (Get-IconImageBytes $square $s) }
}
$square.Dispose()

# ---- ICONDIR + ICONDIRENTRY + payloads -------------------------------------
$fs = New-Object System.IO.FileStream ((Join-Path (Get-Location) $Destination)), ([System.IO.FileMode]::Create)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([uint16]0)                       # reserved
$w.Write([uint16]1)                       # 1 = icon
$w.Write([uint16]$images.Count)

$offset = 6 + 16 * $images.Count
foreach ($img in $images) {
    $s = $img.Size
    $w.Write([byte]$(if ($s -ge 256) { 0 } else { $s }))
    $w.Write([byte]$(if ($s -ge 256) { 0 } else { $s }))
    $w.Write([byte]0)                     # palette entries
    $w.Write([byte]0)                     # reserved
    $w.Write([uint16]1)                   # colour planes
    $w.Write([uint16]32)                  # bits per pixel
    $w.Write([uint32]$img.Bytes.Length)
    $w.Write([uint32]$offset)
    $offset += $img.Bytes.Length
}
foreach ($img in $images) { $w.Write($img.Bytes, 0, $img.Bytes.Length) }
$w.Flush(); $w.Dispose(); $fs.Dispose()

$final = Resolve-Path $Destination
Write-Host ("wrote {0} ({1:N0} bytes, sizes: {2})" -f `
    $final, (Get-Item $final).Length, (($images | ForEach-Object { $_.Size }) -join ', ')) -ForegroundColor Green
