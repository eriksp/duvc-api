$ErrorActionPreference = "Stop"

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$png  = Join-Path $root "dist\assets\cellari_logo.png"
$ico  = Join-Path $root "dist\assets\cellari_logo.ico"

if (-not (Test-Path $png)) { throw "Missing source PNG: $png" }

Add-Type -AssemblyName System.Drawing

$source = [System.Drawing.Image]::FromFile((Resolve-Path $png).Path)
try {
    $sizes = 16, 32, 48, 256
    $payloads = @()
    foreach ($size in $sizes) {
        $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        try {
            $g = [System.Drawing.Graphics]::FromImage($bmp)
            try {
                $g.InterpolationMode  = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $g.SmoothingMode      = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
                $g.PixelOffsetMode    = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
                $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
                $g.Clear([System.Drawing.Color]::Transparent)
                $g.DrawImage($source, 0, 0, $size, $size)
            } finally { $g.Dispose() }
            $ms = New-Object IO.MemoryStream
            try {
                $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
                $payloads += ,$ms.ToArray()
            } finally { $ms.Dispose() }
        } finally { $bmp.Dispose() }
    }
} finally { $source.Dispose() }

# Build ICO container: ICONDIR + ICONDIRENTRYs + PNG payloads.
$out = New-Object IO.MemoryStream
$bw  = New-Object IO.BinaryWriter($out)
try {
    $bw.Write([uint16]0)            # reserved
    $bw.Write([uint16]1)            # type = 1 (icon)
    $bw.Write([uint16]$payloads.Count)

    $headerSize = 6 + (16 * $payloads.Count)
    $offset = $headerSize
    for ($i = 0; $i -lt $payloads.Count; $i++) {
        $size    = $sizes[$i]
        $payload = $payloads[$i]
        $w = if ($size -ge 256) { 0 } else { $size }
        $h = if ($size -ge 256) { 0 } else { $size }
        $bw.Write([byte]$w)              # width  (0 means 256)
        $bw.Write([byte]$h)              # height (0 means 256)
        $bw.Write([byte]0)               # palette count
        $bw.Write([byte]0)               # reserved
        $bw.Write([uint16]1)             # planes
        $bw.Write([uint16]32)            # bits-per-pixel
        $bw.Write([uint32]$payload.Length)
        $bw.Write([uint32]$offset)
        $offset += $payload.Length
    }
    foreach ($payload in $payloads) { $bw.Write($payload) }
    $bw.Flush()
    [IO.File]::WriteAllBytes($ico, $out.ToArray())
} finally {
    $bw.Dispose()
    $out.Dispose()
}

Write-Host ("Wrote {0} ({1} bytes, sizes: {2})" -f $ico, (Get-Item $ico).Length, ($sizes -join ', '))
