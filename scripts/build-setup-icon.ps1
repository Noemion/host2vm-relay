[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$SourcePath,
    [Parameter(Mandatory = $true)][string]$OutputPath
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$inputBytes = [IO.File]::ReadAllBytes([IO.Path]::GetFullPath($SourcePath))
$inputStream = [IO.MemoryStream]::new($inputBytes, $false)
$reader = [IO.BinaryReader]::new($inputStream)
$frames = [Collections.Generic.List[object]]::new()
try {
    if ($reader.ReadUInt16() -ne 0 -or $reader.ReadUInt16() -ne 1) { throw 'Invalid source ICO.' }
    $count = $reader.ReadUInt16()
    if ($count -lt 1 -or $count -gt 256 -or (6 + 16 * $count) -gt $inputBytes.Length) { throw 'Invalid ICO directory.' }
    for ($index = 0; $index -lt $count; $index++) {
        $inputStream.Position = 6 + $index * 16
        $width = [int]$reader.ReadByte(); if (!$width) { $width = 256 }
        $height = [int]$reader.ReadByte(); if (!$height) { $height = 256 }
        $inputStream.Position += 6
        $length = $reader.ReadUInt32(); $offset = $reader.ReadUInt32()
        if ($width -ne $height -or $offset -lt (6 + 16 * $count) -or ([long]$offset + $length) -gt $inputBytes.Length) { throw 'Invalid ICO frame bounds.' }
        $inputStream.Position = $offset
        $png = $reader.ReadBytes([int]$length)
        if ($png.Length -lt 8 -or [BitConverter]::ToString($png, 0, 8) -ne '89-50-4E-47-0D-0A-1A-0A') { throw 'Source ICO must contain PNG frames.' }
        if ($width -eq 256) { $frames.Add([pscustomobject]@{ Size = $width; Data = $png }); continue }
        $imageStream = [IO.MemoryStream]::new($png, $false)
        $bitmap = [Drawing.Bitmap]::new($imageStream)
        $buffer = [IO.MemoryStream]::new(); $writer = [IO.BinaryWriter]::new($buffer)
        try {
            if ($bitmap.Width -ne $width -or $bitmap.Height -ne $height) { throw 'ICO dimensions do not match the image.' }
            $maskStride = [int]([Math]::Ceiling($width / 32.0) * 4)
            $writer.Write([uint32]40); $writer.Write([int]$width); $writer.Write([int]($height * 2))
            $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]0)
            $writer.Write([uint32]($width * $height * 4 + $maskStride * $height))
            for ($n = 0; $n -lt 4; $n++) { $writer.Write([uint32]0) }
            # Uncompressed 32-bit BGRA plus a DWORD-aligned AND transparency mask.
            for ($y = $height - 1; $y -ge 0; $y--) {
                for ($x = 0; $x -lt $width; $x++) {
                    $pixel = $bitmap.GetPixel($x, $y)
                    $writer.Write([byte]$pixel.B); $writer.Write([byte]$pixel.G)
                    $writer.Write([byte]$pixel.R); $writer.Write([byte]$pixel.A)
                }
            }
            for ($y = $height - 1; $y -ge 0; $y--) {
                $mask = New-Object byte[] $maskStride
                for ($x = 0; $x -lt $width; $x++) {
                    if ($bitmap.GetPixel($x, $y).A -eq 0) {
                        $byteIndex = [int][Math]::Floor($x / 8)
                        $mask[$byteIndex] = [byte]($mask[$byteIndex] -bor (1 -shl (7 - ($x % 8))))
                    }
                }
                $writer.Write([byte[]]$mask)
            }
            $writer.Flush(); $frames.Add([pscustomobject]@{ Size = $width; Data = $buffer.ToArray() })
        }
        finally { $writer.Dispose(); $buffer.Dispose(); $bitmap.Dispose(); $imageStream.Dispose() }
    }
}
finally { $reader.Dispose(); $inputStream.Dispose() }
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($OutputPath)) | Out-Null
$temp = $OutputPath + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
try {
    $file = [IO.File]::Create($temp); $writer = [IO.BinaryWriter]::new($file)
    try {
        $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
        $offset = 6 + $frames.Count * 16
        foreach ($frame in $frames) {
            $dimension = [byte]0; if ($frame.Size -lt 256) { $dimension = [byte]$frame.Size }
            $writer.Write($dimension); $writer.Write($dimension); $writer.Write([uint16]0)
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
finally { if ([IO.File]::Exists($temp)) { [IO.File]::Delete($temp) } }
Write-Host "Setup icon: $OutputPath"
