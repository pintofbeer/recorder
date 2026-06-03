param(
    [string] $OutputPath = "Resources\AppIcon.ico"
)

Add-Type -AssemblyName System.Drawing

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$output = Join-Path $root $OutputPath
$outputDirectory = Split-Path -Parent $output

if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
    New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
}

function New-IconDibBytes {
    param([int] $Size)

    $bitmap = New-Object System.Drawing.Bitmap $Size, $Size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

    $scale = $Size / 256.0

    try {
        $bounds = New-Object System.Drawing.Rectangle 0, 0, $Size, $Size
        $background = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
            $bounds,
            [System.Drawing.Color]::FromArgb(255, 25, 33, 54),
            [System.Drawing.Color]::FromArgb(255, 20, 99, 112),
            45
        )
        $graphics.FillRectangle($background, $bounds)
        $background.Dispose()

        $glowBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(70, 55, 231, 188))
        $graphics.FillEllipse($glowBrush, [single](20 * $scale), [single](20 * $scale), [single](216 * $scale), [single](216 * $scale))
        $glowBrush.Dispose()

        $trackPen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(225, 232, 247, 244)), ([single](16 * $scale))
        $trackPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $trackPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $trackPen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round

        $points = @(
            [System.Drawing.PointF]::new([single](42 * $scale), [single](134 * $scale)),
            [System.Drawing.PointF]::new([single](66 * $scale), [single](134 * $scale)),
            [System.Drawing.PointF]::new([single](88 * $scale), [single](96 * $scale)),
            [System.Drawing.PointF]::new([single](116 * $scale), [single](176 * $scale)),
            [System.Drawing.PointF]::new([single](144 * $scale), [single](82 * $scale)),
            [System.Drawing.PointF]::new([single](176 * $scale), [single](134 * $scale)),
            [System.Drawing.PointF]::new([single](214 * $scale), [single](134 * $scale))
        )
        $graphics.DrawLines($trackPen, $points)
        $trackPen.Dispose()

        $dotShadow = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(85, 0, 0, 0))
        $graphics.FillEllipse($dotShadow, [single](81 * $scale), [single](77 * $scale), [single](94 * $scale), [single](94 * $scale))
        $dotShadow.Dispose()

        $dotBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 236, 67, 80))
        $graphics.FillEllipse($dotBrush, [single](74 * $scale), [single](68 * $scale), [single](94 * $scale), [single](94 * $scale))
        $dotBrush.Dispose()

        $highlightBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(150, 255, 255, 255))
        $graphics.FillEllipse($highlightBrush, [single](98 * $scale), [single](86 * $scale), [single](24 * $scale), [single](24 * $scale))
        $highlightBrush.Dispose()

        $ringPen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(120, 255, 255, 255)), ([single](6 * $scale))
        $graphics.DrawEllipse($ringPen, [single](74 * $scale), [single](68 * $scale), [single](94 * $scale), [single](94 * $scale))
        $ringPen.Dispose()

        $memory = New-Object System.IO.MemoryStream
        $writer = New-Object System.IO.BinaryWriter($memory)

        $xorBytesPerRow = $Size * 4
        $andBytesPerRow = [int]([Math]::Ceiling($Size / 32.0) * 4)
        $xorSize = $xorBytesPerRow * $Size
        $andSize = $andBytesPerRow * $Size

        $writer.Write([UInt32]40)
        $writer.Write([Int32]$Size)
        $writer.Write([Int32]($Size * 2))
        $writer.Write([UInt16]1)
        $writer.Write([UInt16]32)
        $writer.Write([UInt32]0)
        $writer.Write([UInt32]$xorSize)
        $writer.Write([Int32]0)
        $writer.Write([Int32]0)
        $writer.Write([UInt32]0)
        $writer.Write([UInt32]0)

        for ($y = $Size - 1; $y -ge 0; $y--) {
            for ($x = 0; $x -lt $Size; $x++) {
                $pixel = $bitmap.GetPixel($x, $y)
                $writer.Write([byte]$pixel.B)
                $writer.Write([byte]$pixel.G)
                $writer.Write([byte]$pixel.R)
                $writer.Write([byte]$pixel.A)
            }
        }

        for ($i = 0; $i -lt $andSize; $i++) {
            $writer.Write([byte]0)
        }

        $writer.Dispose()
        return ,$memory.ToArray()
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$images = foreach ($size in $sizes) {
    [pscustomobject]@{
        Size = $size
        Bytes = New-IconDibBytes -Size $size
    }
}

$headerSize = 6
$directoryEntrySize = 16
$imageOffset = $headerSize + ($directoryEntrySize * $images.Count)

$stream = New-Object System.IO.FileStream($output, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write)
$writer = New-Object System.IO.BinaryWriter($stream)

try {
    $writer.Write([UInt16]0)
    $writer.Write([UInt16]1)
    $writer.Write([UInt16]$images.Count)

    $offset = $imageOffset
    foreach ($image in $images) {
        $encodedSize = if ($image.Size -eq 256) { 0 } else { $image.Size }
        $writer.Write([byte]$encodedSize)
        $writer.Write([byte]$encodedSize)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([UInt16]1)
        $writer.Write([UInt16]32)
        $writer.Write([UInt32]$image.Bytes.Length)
        $writer.Write([UInt32]$offset)
        $offset += $image.Bytes.Length
    }

    foreach ($image in $images) {
        $writer.Write($image.Bytes)
    }
}
finally {
    $writer.Dispose()
    $stream.Dispose()
}

Write-Host "Generated $output"
