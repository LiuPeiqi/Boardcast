# Rebuild committed icon assets from the SVG using Windows' built-in WPF renderer.
# No external image tools, packages, or generated code are needed at app runtime.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, WindowsBase
$projectRoot = Split-Path -Parent $PSScriptRoot
$assetRoot = Join-Path $projectRoot 'assets'
[xml]$svg = [IO.File]::ReadAllText((Join-Path $assetRoot 'boardcast.svg'))
$brushConverter = New-Object Windows.Media.BrushConverter
function Render-Icon([int]$size) {
    $visual = New-Object Windows.Media.DrawingVisual
    $drawing = $visual.RenderOpen()
    try {
        $drawing.PushTransform([Windows.Media.ScaleTransform]::new($size / 64.0, $size / 64.0))
        foreach ($element in $svg.DocumentElement.ChildNodes) {
            if ($element.LocalName -notin @('rect','circle','path')) { continue }
            $brush = $brushConverter.ConvertFromInvariantString($element.GetAttribute('fill'))
            switch ($element.LocalName) {
                'rect' {
                    $bounds = [Windows.Rect]::new([double]$element.x, [double]$element.y, [double]$element.width, [double]$element.height)
                    $radius = [double]$element.rx
                    $drawing.DrawRoundedRectangle($brush, $null, $bounds, $radius, $radius)
                }
                'circle' {
                    $drawing.DrawEllipse($brush, $null, [Windows.Point]::new([double]$element.cx, [double]$element.cy), [double]$element.r, [double]$element.r)
                }
                'path' { $drawing.DrawGeometry($brush, $null, [Windows.Media.Geometry]::Parse($element.d)) }
            }
        }
        $drawing.Pop()
    } finally { $drawing.Close() }
    $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new($size, $size, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    return $bitmap
}
function Encode-Png($bitmap) {
    $stream = [IO.MemoryStream]::new()
    try {
        $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
        $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
        $encoder.Save($stream)
        return ,$stream.ToArray()
    } finally { $stream.Dispose() }
}
function Encode-Dib($bitmap, [int]$size) {
    $converted = [Windows.Media.Imaging.FormatConvertedBitmap]::new($bitmap, [Windows.Media.PixelFormats]::Bgra32, $null, 0)
    $pixels = New-Object byte[] ($size * $size * 4)
    $converted.CopyPixels($pixels, $size * 4, 0)
    $maskStride = [int]([Math]::Ceiling($size / 32.0) * 4)
    $mask = New-Object byte[] ($maskStride * $size)
    $stream = [IO.MemoryStream]::new()
    $writer = [IO.BinaryWriter]::new($stream)
    try {
        $writer.Write([uint32]40)
        $writer.Write([int32]$size)
        $writer.Write([int32]($size * 2))
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]0)
        $writer.Write([uint32]($pixels.Length + $mask.Length))
        $writer.Write([int32]0); $writer.Write([int32]0)
        $writer.Write([uint32]0); $writer.Write([uint32]0)
        for ($row = $size - 1; $row -ge 0; $row--) {
            $writer.Write($pixels, $row * $size * 4, $size * 4)
            for ($column = 0; $column -lt $size; $column++) {
                if ($pixels[($row * $size + $column) * 4 + 3] -eq 0) {
                    $offset = ($size - 1 - $row) * $maskStride + [int][Math]::Floor($column / 8.0)
                    $mask[$offset] = $mask[$offset] -bor (128 -shr ($column % 8))
                }
            }
        }
        $writer.Write($mask)
        return ,$stream.ToArray()
    } finally { $writer.Dispose(); $stream.Dispose() }
}
$sizes = @(16,20,24,32,40,48,64,96,128,256)
$frames = @()
foreach ($size in $sizes) {
    $bitmap = Render-Icon $size
    $bytes = if ($size -eq 256) { Encode-Png $bitmap } else { Encode-Dib $bitmap $size }
    $frames += ,([byte[]]$bytes)
}
$iconStream = [IO.MemoryStream]::new()
$iconWriter = [IO.BinaryWriter]::new($iconStream)
try {
    $iconWriter.Write([uint16]0); $iconWriter.Write([uint16]1)
    $iconWriter.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
        $iconWriter.Write([byte]$dimension); $iconWriter.Write([byte]$dimension)
        $iconWriter.Write([byte]0); $iconWriter.Write([byte]0)
        $iconWriter.Write([uint16]1); $iconWriter.Write([uint16]32)
        $iconWriter.Write([uint32]$frames[$i].Length); $iconWriter.Write([uint32]$offset)
        $offset += $frames[$i].Length
    }
    foreach ($frame in $frames) { $iconWriter.Write([byte[]]$frame) }
    [IO.File]::WriteAllBytes((Join-Path $assetRoot 'boardcast.ico'), $iconStream.ToArray())
} finally { $iconWriter.Dispose(); $iconStream.Dispose() }
[IO.File]::WriteAllBytes((Join-Path $assetRoot 'boardcast.png'), (Encode-Png (Render-Icon 512)))
Write-Host ('Generated boardcast.ico: ' + ($sizes -join ', ') + ' px; PNG preview: 512 px.')
