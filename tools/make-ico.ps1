# tools/make-ico.ps1
#
# Generates a Windows multi-resolution .ico from a transparent source PNG.
#
# Why this exists: ImageMagick is not on PATH on every Windows dev box. This
# script uses pure .NET (System.Drawing) to rasterise the source at each
# target size, then emits the ICO container by hand. Frames at >=128 px are
# stored as PNG (the Vista+ icon format); smaller frames are stored as BMP
# (Windows still complains if 16/32/48 are PNG-encoded on legacy code paths).
#
# Usage:
#   pwsh -NoProfile -ExecutionPolicy Bypass -File tools/make-ico.ps1 `
#        -Input  prism-logo.png `
#        -Output agent/src/PRISM.Agent/Assets/PRISM.Agent.ico

[CmdletBinding()]
param(
  [Parameter(Mandatory=$true)] [string]$SourcePath,
  [Parameter(Mandatory=$true)] [string]$OutputPath,
  [int[]]$Sizes = @(16, 32, 48, 64, 128, 256)
)

Add-Type -AssemblyName System.Drawing

$inPath  = (Resolve-Path -LiteralPath $SourcePath).ProviderPath
$outPath = [System.IO.Path]::GetFullPath(
  (Join-Path -Path (Get-Location) -ChildPath $OutputPath))

$outDir = [System.IO.Path]::GetDirectoryName($outPath)
if (-not (Test-Path -LiteralPath $outDir)) {
  New-Item -ItemType Directory -Path $outDir -Force | Out-Null
}

$src = [System.Drawing.Image]::FromFile($inPath)
try {
  $frames = @()

  foreach ($size in $Sizes) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size,
      ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g   = [System.Drawing.Graphics]::FromImage($bmp)
    try {
      $g.CompositingMode    = [System.Drawing.Drawing2D.CompositingMode]::SourceOver
      $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
      $g.InterpolationMode  = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
      $g.SmoothingMode      = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
      $g.PixelOffsetMode    = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
      $g.Clear([System.Drawing.Color]::Transparent)
      $g.DrawImage($src, 0, 0, $size, $size)
    } finally {
      $g.Dispose()
    }

    # All frames go in as PNG. Windows has handled PNG-compressed ICO
    # frames at every size since Vista; the only consumer that ever
    # complained was the original XP shell, which we don't target.
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bytes = $ms.ToArray()
    $ms.Dispose()
    $bmp.Dispose()

    $frames += [pscustomobject]@{
      Size  = $size
      Bytes = $bytes
    }
    Write-Host ("  frame {0,3}x{0,-3}  {1,7:N0} bytes" -f $size, $bytes.Length)
  }
} finally {
  $src.Dispose()
}

# ---- Write the ICO container --------------------------------------------
# Header: ICONDIR (6 bytes) + N x ICONDIRENTRY (16 bytes) + payload.
# All multi-byte fields are little-endian.

$fs = [System.IO.File]::Open($outPath,
  [System.IO.FileMode]::Create,
  [System.IO.FileAccess]::Write)
try {
  $bw = New-Object System.IO.BinaryWriter $fs
  try {
    $bw.Write([uint16]0)              # Reserved
    $bw.Write([uint16]1)              # Type = 1 (icon)
    $bw.Write([uint16]$frames.Count)  # Count

    # ICONDIRENTRY table sits between the header and the payloads.
    $headerSize = 6 + (16 * $frames.Count)
    $offset = $headerSize

    foreach ($f in $frames) {
      # Width/Height: 0 means 256.
      $w = if ($f.Size -ge 256) { 0 } else { [byte]$f.Size }
      $h = $w
      $bw.Write([byte]$w)              # bWidth
      $bw.Write([byte]$h)              # bHeight
      $bw.Write([byte]0)               # bColorCount (0 = >=256 colours)
      $bw.Write([byte]0)               # bReserved
      $bw.Write([uint16]1)             # wPlanes
      $bw.Write([uint16]32)            # wBitCount
      $bw.Write([uint32]$f.Bytes.Length)  # dwBytesInRes
      $bw.Write([uint32]$offset)       # dwImageOffset
      $offset += $f.Bytes.Length
    }

    foreach ($f in $frames) {
      $bw.Write($f.Bytes)
    }
  } finally {
    $bw.Flush()
    $bw.Dispose()
  }
} finally {
  $fs.Dispose()
}

$len = (Get-Item -LiteralPath $outPath).Length
Write-Host ("Wrote {0} ({1:N0} bytes)" -f $outPath, $len)
