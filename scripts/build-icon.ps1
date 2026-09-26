[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$SourcePath,
    [Parameter(Mandatory = $true)][string]$OutputPath
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
[xml]$svg = Get-Content -LiteralPath $SourcePath -Raw
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
New-Item -ItemType Directory -Force ([IO.Path]::GetDirectoryName($OutputPath)) | Out-Null
# The bundled SVG intentionally uses only rounded rectangles and M/L/Z paths.
function New-ShapePath {
    param($Element)
    $shape = [Drawing.Drawing2D.GraphicsPath]::new()
    if ($Element.LocalName -eq 'rect') {
        $x = [single]$Element.x; $y = [single]$Element.y
        $w = [single]$Element.width; $h = [single]$Element.height; $d = 2 * [single]$Element.rx
        $shape.AddArc($x, $y, $d, $d, 180, 90)
        $shape.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
        $shape.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
        $shape.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
        $shape.CloseFigure()
    }
    elseif ($Element.LocalName -eq 'path') {
        $tokens = @([regex]::Matches([string]$Element.d, '[MLZ]|-?\d+(?:\.\d+)?') | ForEach-Object Value)
        $i = 0; $px = [single]0; $py = [single]0
        while ($i -lt $tokens.Count) {
            $command = $tokens[$i++]
            if ($command -eq 'Z') { $shape.CloseFigure(); continue }
            if ($command -ne 'M' -and $command -ne 'L') { throw 'Unsupported icon SVG path command.' }
            $x = [single]::Parse($tokens[$i++], [Globalization.CultureInfo]::InvariantCulture)
            $y = [single]::Parse($tokens[$i++], [Globalization.CultureInfo]::InvariantCulture)
            if ($command -eq 'M') { $shape.StartFigure() }
            else { $shape.AddLine($px, $py, $x, $y) }
            $px = $x; $py = $y
        }
    }
    else { throw "Unsupported icon SVG element: $($Element.LocalName)" }
    return ,$shape
}
$frames = [System.Collections.Generic.List[object]]::new()
foreach ($size in @(16,20,24,32,40,48,64,128,256)) {
    $large = [Drawing.Bitmap]::new(($size * 4), ($size * 4), [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [Drawing.Graphics]::FromImage($large)
    $bitmap = [Drawing.Bitmap]::new($size, $size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $small = [Drawing.Graphics]::FromImage($bitmap)
    $buffer = [IO.MemoryStream]::new()
    try {
        $graphics.Clear([Drawing.Color]::Transparent)
        $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.ScaleTransform([single]($size * 4 / 256), [single]($size * 4 / 256))
        foreach ($element in $svg.DocumentElement.ChildNodes) {
            if ($element.NodeType -ne [Xml.XmlNodeType]::Element) { continue }
            $shape = New-ShapePath $element
            try {
                if ($element.fill -and $element.fill -ne 'none') {
                    $brush = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml([string]$element.fill))
                    try { $graphics.FillPath($brush, $shape) } finally { $brush.Dispose() }
                }
                if ($element.stroke -and $element.stroke -ne 'none') {
                    $pen = [Drawing.Pen]::new([Drawing.ColorTranslator]::FromHtml([string]$element.stroke), [single]$element.GetAttribute('stroke-width'))
                    $pen.LineJoin = [Drawing.Drawing2D.LineJoin]::Round
                    $pen.StartCap = [Drawing.Drawing2D.LineCap]::Round
                    $pen.EndCap = [Drawing.Drawing2D.LineCap]::Round
                    try { $graphics.DrawPath($pen, $shape) } finally { $pen.Dispose() }
                }
            }
            finally { $shape.Dispose() }
        }
        $small.CompositingMode = [Drawing.Drawing2D.CompositingMode]::SourceCopy
        $small.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $small.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $small.DrawImage($large, ([Drawing.Rectangle]::new(0, 0, $size, $size)))
        $bitmap.Save($buffer, [Drawing.Imaging.ImageFormat]::Png)
        $frames.Add([pscustomobject]@{ Size = $size; Data = $buffer.ToArray() })
    }
    finally { $buffer.Dispose(); $graphics.Dispose(); $small.Dispose(); $bitmap.Dispose(); $large.Dispose() }
}
$temp = $OutputPath + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
try {
    $file = [IO.File]::Create($temp)
    $writer = [IO.BinaryWriter]::new($file)
    try {
        $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
        $offset = 6 + 16 * $frames.Count
        foreach ($frame in $frames) {
            $dimension = [byte]0
            if ($frame.Size -lt 256) { $dimension = [byte]$frame.Size }
            $writer.Write($dimension); $writer.Write($dimension); $writer.Write([byte]0); $writer.Write([byte]0)
            $writer.Write([uint16]1); $writer.Write([uint16]32)
            $writer.Write([uint32]$frame.Data.Length); $writer.Write([uint32]$offset)
            $offset += $frame.Data.Length
        }
        foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Data) }
    }
    finally { $writer.Dispose(); $file.Dispose() }
    if ([IO.File]::Exists($OutputPath)) { [IO.File]::Replace($temp, $OutputPath, $null) }
    else { [IO.File]::Move($temp, $OutputPath) }
}
finally { if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Force } }
Write-Host "Application icon: $OutputPath"
