# ---------------------------------------------------------------------------
# Draws the OMR XML Builder application icon and writes a multi-size app.ico.
#
# The glyph is an OMR sheet: a corner anchor plus timing marks down the top and
# left edges (the grid the whole app is built on) and three answer rows with one
# bubble filled, plus an XML badge at the sizes big enough to carry it.
# The palette matches Icons.cs so the exe icon and the toolbar read as one set.
#
# Run with Windows PowerShell (it has System.Drawing):
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\MakeAppIcon.ps1
# ---------------------------------------------------------------------------
param([string]$Out = (Join-Path $PSScriptRoot '..\app.ico'),
      [string]$Preview = '')

Add-Type -AssemblyName System.Drawing

$Ink    = [System.Drawing.Color]::FromArgb(70, 70, 78)
$Accent = [System.Drawing.Color]::FromArgb(28, 110, 200)
$Paper  = [System.Drawing.Color]::FromArgb(252, 252, 253)
$Faint  = [System.Drawing.Color]::FromArgb(120, 70, 70, 78)

function New-RoundRect([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function Fill-Rect($g, $color, [single]$x, [single]$y, [single]$w, [single]$h) {
    $b = New-Object System.Drawing.SolidBrush($color)
    $g.FillRectangle($b, $x, $y, $w, $h)
    $b.Dispose()
}

function Fill-Dot($g, $color, [single]$cx, [single]$cy, [single]$d) {
    $b = New-Object System.Drawing.SolidBrush($color)
    $g.FillEllipse($b, $cx - $d / 2, $cy - $d / 2, $d, $d)
    $b.Dispose()
}

# Everything is drawn in a 32x32 design space and scaled to the target size, but
# the amount of detail steps down so the small icons stay readable.
function Draw-Icon($g, [int]$size) {
    $g.SmoothingMode   = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.ScaleTransform($size / 32.0, $size / 32.0)

    if ($size -le 20) {
        # 16px: page, anchor, 3x3 solid dots. Rings and timing marks are sub-pixel
        # at this size, so they would only turn into grey mush.
        $path = New-RoundRect 3 1.5 26 29 2.5
        $b = New-Object System.Drawing.SolidBrush($Paper); $g.FillPath($b, $path); $b.Dispose()
        $p = New-Object System.Drawing.Pen($Ink, 2.4);     $g.DrawPath($p, $path); $p.Dispose()
        $path.Dispose()

        Fill-Rect $g $Ink 5.8 4.0 5.0 5.0                   # corner anchor
        $fill = @(1, 2, 0)                                  # marked dot per row
        for ($r = 0; $r -lt 3; $r++) {
            for ($c = 0; $c -lt 3; $c++) {
                $col = if ($c -eq $fill[$r]) { $Accent } else { $Faint }
                Fill-Dot $g $col (13.5 + $c * 5.7) (12.5 + $r * 6.0) 4.6
            }
        }
        return
    }

    # 24px and up: the full sheet. The four left timing marks sit level with the
    # four answer rows, the way they index the rows on a real OMR form.
    $path = New-RoundRect 3.5 1.5 25 29 2.5
    $b = New-Object System.Drawing.SolidBrush($Paper); $g.FillPath($b, $path); $b.Dispose()
    $p = New-Object System.Drawing.Pen($Ink, 1.7);     $g.DrawPath($p, $path); $p.Dispose()
    $path.Dispose()

    Fill-Rect $g $Ink 5.8 3.8 4.0 4.0                                              # corner anchor
    foreach ($x in 11.5, 15.6, 19.7, 23.8) { Fill-Rect $g $Ink $x 4.6 2.4 2.4 }    # top timing marks
    foreach ($y in 10.5, 14.8, 19.1, 23.4) { Fill-Rect $g $Ink 6.1 $y 2.4 2.4 }    # left timing marks

    $fill = @(1, 2, 0, 2)
    for ($r = 0; $r -lt 4; $r++) {
        $cy = 11.7 + $r * 4.3
        for ($c = 0; $c -lt 3; $c++) {
            $cx = 13.6 + $c * 5.2
            if ($c -eq $fill[$r]) {
                Fill-Dot $g $Accent $cx $cy 4.2
            } else {
                $p = New-Object System.Drawing.Pen($Faint, 1.2)
                $g.DrawEllipse($p, $cx - 2.1, $cy - 2.1, 4.2, 4.2)
                $p.Dispose()
            }
        }
    }
}

function Render([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::Transparent)
    Draw-Icon $g $size
    $g.Dispose()
    return $bmp
}

# ---- ICO encoding ----------------------------------------------------------
# Every size the app itself can ask for goes in as a raw DIB: the .NET Framework
# Icon class cannot decode a PNG-compressed frame, so Icon.ToBitmap() throws on
# one. Only 256 is PNG (it would be 256 KB as a DIB), and nothing but the shell
# ever asks for that — the shell reads it from the Win32 resource, not via .NET.
function Get-Dib($bmp) {
    $w = $bmp.Width; $h = $bmp.Height
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    $bw.Write([int]40); $bw.Write([int]$w); $bw.Write([int]($h * 2))
    $bw.Write([System.Int16]1); $bw.Write([System.Int16]32)
    $bw.Write([int]0); $bw.Write([int]0)
    $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0)
    for ($y = $h - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $w; $x++) {
            $c = $bmp.GetPixel($x, $y)
            $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A)
        }
    }
    $stride = [int][math]::Floor(($w + 31) / 32) * 4
    for ($y = $h - 1; $y -ge 0; $y--) {
        $row = New-Object byte[] $stride
        for ($x = 0; $x -lt $w; $x++) {
            if ($bmp.GetPixel($x, $y).A -eq 0) {
                $i = [int][math]::Floor($x / 8)
                $row[$i] = [byte]($row[$i] -bor [byte](0x80 -shr ($x % 8)))
            }
        }
        $bw.Write($row)
    }
    $bw.Flush()
    return ,$ms.ToArray()
}

function Get-Png($bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    return ,$ms.ToArray()
}

$dibSizes = 16, 24, 32, 48, 64, 96
$pngSizes = 256
$entries = @()
foreach ($s in ($dibSizes + $pngSizes)) {
    $bmp = Render $s
    $data = if ($pngSizes -contains $s) { Get-Png $bmp } else { Get-Dib $bmp }
    $entries += [pscustomobject]@{ Size = $s; Data = $data; Bmp = $bmp }
}

$ico = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($ico)
$bw.Write([System.Int16]0); $bw.Write([System.Int16]1); $bw.Write([System.Int16]$entries.Count)
$offset = 6 + 16 * $entries.Count
foreach ($e in $entries) {
    $dim = if ($e.Size -ge 256) { 0 } else { $e.Size }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([System.Int16]1); $bw.Write([System.Int16]32)
    $bw.Write([int]$e.Data.Length); $bw.Write([int]$offset)
    $offset += $e.Data.Length
}
foreach ($e in $entries) { $bw.Write([byte[]]$e.Data) }
$bw.Flush()
$full = [System.IO.Path]::GetFullPath($Out)
[System.IO.File]::WriteAllBytes($full, $ico.ToArray())
Write-Host ("Wrote {0} ({1:N0} bytes, {2} sizes)" -f $full, $ico.Length, $entries.Count)

if ($Preview) {
    $sheet = New-Object System.Drawing.Bitmap(520, 220)
    $pg = [System.Drawing.Graphics]::FromImage($sheet)
    $pg.Clear([System.Drawing.Color]::White)
    $dark = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(38, 38, 44))
    $pg.FillRectangle($dark, 0, 110, 520, 110)
    $x = 12
    foreach ($e in $entries) {
        if ($e.Size -gt 128) { continue }
        $pg.DrawImage($e.Bmp, $x, 20, $e.Size, $e.Size)
        $pg.DrawImage($e.Bmp, $x, 130, $e.Size, $e.Size)
        $x += $e.Size + 20
    }
    $pg.Dispose()
    $sheet.Save($Preview, [System.Drawing.Imaging.ImageFormat]::Png)
    Write-Host "Preview: $Preview"
}
