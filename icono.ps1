# Dibuja icono.ico: un candado blanco sobre un cuadrado redondeado violeta.
# Cada tamaño va como PNG dentro del .ico (Windows lo acepta desde Vista).
param([string]$Salida = (Join-Path $PSScriptRoot 'icono.ico'))
Add-Type -AssemblyName System.Drawing

function New-Redondeado([float]$x, [float]$y, [float]$an, [float]$al, [float]$r) {
    $c = New-Object Drawing.Drawing2D.GraphicsPath
    $c.AddArc($x, $y, 2 * $r, 2 * $r, 180, 90)
    $c.AddArc($x + $an - 2 * $r, $y, 2 * $r, 2 * $r, 270, 90)
    $c.AddArc($x + $an - 2 * $r, $y + $al - 2 * $r, 2 * $r, 2 * $r, 0, 90)
    $c.AddArc($x, $y + $al - 2 * $r, 2 * $r, 2 * $r, 90, 90)
    $c.CloseFigure()
    $c
}

function New-Png([int]$s) {
    $bmp = New-Object Drawing.Bitmap $s, $s, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([Drawing.Color]::Transparent)
    $m = [math]::Max(0.5, $s * 0.04); $lado = $s - 2 * $m
    $rect = New-Object Drawing.RectangleF $m, $m, $lado, $lado
    $fondo = New-Object Drawing.Drawing2D.LinearGradientBrush $rect, ([Drawing.Color]::FromArgb(255, 139, 124, 246)), ([Drawing.Color]::FromArgb(255, 74, 58, 196)), 45.0
    $g.FillPath($fondo, (New-Redondeado $m $m $lado $lado ($lado * 0.24)))
    $blanco = [Drawing.Brushes]::White
    # arco, en un solo trazo para que no se note la unión
    $grosor = [math]::Max(1.5, $s * 0.085)
    $lapiz = New-Object Drawing.Pen ([Drawing.Color]::White), $grosor
    $lapiz.LineJoin = 'Round'
    $ax = $s * 0.335; $ay = $s * 0.20; $aa = $s * 0.33
    $arco = New-Object Drawing.Drawing2D.GraphicsPath
    $arco.AddLine($ax, ($s * 0.47), $ax, ($ay + $aa / 2))
    $arco.AddArc($ax, $ay, $aa, $aa, 180, 180)
    $arco.AddLine(($ax + $aa), ($ay + $aa / 2), ($ax + $aa), ($s * 0.47))
    $g.DrawPath($lapiz, $arco)
    # cuerpo
    $g.FillPath($blanco, (New-Redondeado ($s * 0.25) ($s * 0.44) ($s * 0.50) ($s * 0.36) ($s * 0.07)))
    # bocallave
    if ($s -ge 24) {
        $hueco = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(255, 92, 76, 214))
        $r = $s * 0.055
        $g.FillEllipse($hueco, ($s * 0.5 - $r), ($s * 0.565 - $r), 2 * $r, 2 * $r)
        $g.FillRectangle($hueco, ($s * 0.5 - $r * 0.45), ($s * 0.57), ($r * 0.9), ($s * 0.11))
    }
    $g.Dispose()
    $ms = New-Object IO.MemoryStream
    $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $ms.ToArray()
}

$imagenes = @(16, 24, 32, 48, 64, 256 | ForEach-Object { [pscustomobject]@{ Lado = $_; Datos = (New-Png $_) } })
$ms = New-Object IO.MemoryStream
$w = New-Object IO.BinaryWriter $ms
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$imagenes.Count)
$offset = 6 + 16 * $imagenes.Count
foreach ($i in $imagenes) {
    $lado = if ($i.Lado -ge 256) { 0 } else { $i.Lado }
    $w.Write([byte]$lado); $w.Write([byte]$lado); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$i.Datos.Length); $w.Write([uint32]$offset)
    $offset += $i.Datos.Length
}
foreach ($i in $imagenes) { $w.Write($i.Datos) }
$w.Flush()
[IO.File]::WriteAllBytes($Salida, $ms.ToArray())
[IO.File]::WriteAllBytes((Join-Path (Split-Path $Salida) 'icono-256.png'), $imagenes[-1].Datos)
"Ícono en $Salida"
