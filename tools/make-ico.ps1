<#  Builds a multi-size .ico (16/32/48 as BMP, 64/128/256 as PNG) from assets\icon.png.
    Usage (from repo root):  .\tools\make-ico.ps1  #>
Add-Type -AssemblyName System.Drawing
$root = Split-Path $PSScriptRoot -Parent
$src  = [System.Drawing.Image]::FromFile((Join-Path $root 'assets\icon.png'))
$sizes = 16,32,48,64,128,256
$frames = @()
foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = 'HighQualityBicubic'; $g.SmoothingMode = 'HighQuality'; $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.DrawImage($src, 0, 0, $s, $s); $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    if ($s -ge 64) {
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    } else {
        $bw = New-Object System.IO.BinaryWriter($ms)
        $bw.Write([uint32]40); $bw.Write([int32]$s); $bw.Write([int32]($s*2)); $bw.Write([uint16]1); $bw.Write([uint16]32)
        $bw.Write([uint32]0); $bw.Write([uint32]($s*$s*4)); $bw.Write([int32]0); $bw.Write([int32]0); $bw.Write([uint32]0); $bw.Write([uint32]0)
        for ($y = $s-1; $y -ge 0; $y--) { for ($x = 0; $x -lt $s; $x++) { $c = $bmp.GetPixel($x,$y); $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A) } }
        $maskRow = [int]([math]::Ceiling($s/32.0)*4)
        $bw.Write((New-Object byte[] ($maskRow*$s)))
        $bw.Flush()
    }
    $frames += ,@{ Size=$s; Data=$ms.ToArray() }
    $bmp.Dispose()
}
$src.Dispose()
$out = [System.IO.BinaryWriter]::new([System.IO.File]::Create((Join-Path $root 'assets\icon.ico')))
$out.Write([uint16]0); $out.Write([uint16]1); $out.Write([uint16]$frames.Count)
$off = 6 + 16*$frames.Count
foreach ($f in $frames) {
    $dim = if ($f.Size -ge 256) { 0 } else { $f.Size }
    $out.Write([byte]$dim); $out.Write([byte]$dim); $out.Write([byte]0); $out.Write([byte]0)
    $out.Write([uint16]1); $out.Write([uint16]32); $out.Write([uint32]$f.Data.Length); $out.Write([uint32]$off)
    $off += $f.Data.Length
}
foreach ($f in $frames) { $out.Write($f.Data) }
$out.Close()
Write-Host ("icon.ico written: {0:N0} bytes, {1} sizes" -f (Get-Item (Join-Path $root 'assets\icon.ico')).Length, $frames.Count)
