# Dibuja los dos íconos: icono.ico (la app: un candado blanco sobre un cuadrado violeta) y
# carpeta-bloqueada.ico (una carpeta amarilla con ese mismo candado en la esquina, el que
# muestra el Explorador mientras la carpeta está bloqueada). Cada tamaño va como PNG dentro
# del .ico, que Windows acepta desde Vista.
param([string]$Dir = $PSScriptRoot)
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

# El candado dentro de un cuadrado de lado $l con la esquina en ($x, $y).
function Dibujar-Candado($g, [float]$x, [float]$y, [float]$l, [bool]$bocallave) {
    $rect = New-Object Drawing.RectangleF $x, $y, $l, $l
    $fondo = New-Object Drawing.Drawing2D.LinearGradientBrush $rect, ([Drawing.Color]::FromArgb(255, 139, 124, 246)), ([Drawing.Color]::FromArgb(255, 74, 58, 196)), 45.0
    $g.FillPath($fondo, (New-Redondeado $x $y $l $l ($l * 0.24)))
    $lapiz = New-Object Drawing.Pen ([Drawing.Color]::White), ([math]::Max(1.2, $l * 0.09))
    $lapiz.LineJoin = 'Round'
    $ax = $x + $l * 0.335; $ay = $y + $l * 0.19; $aa = $l * 0.33
    $arco = New-Object Drawing.Drawing2D.GraphicsPath
    $arco.AddLine($ax, ($y + $l * 0.47), $ax, ($ay + $aa / 2))
    $arco.AddArc($ax, $ay, $aa, $aa, 180, 180)
    $arco.AddLine(($ax + $aa), ($ay + $aa / 2), ($ax + $aa), ($y + $l * 0.47))
    $g.DrawPath($lapiz, $arco)
    $g.FillPath([Drawing.Brushes]::White, (New-Redondeado ($x + $l * 0.25) ($y + $l * 0.44) ($l * 0.50) ($l * 0.36) ($l * 0.07)))
    if ($bocallave) {
        $hueco = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(255, 92, 76, 214))
        $r = $l * 0.055
        $g.FillEllipse($hueco, ($x + $l * 0.5 - $r), ($y + $l * 0.565 - $r), 2 * $r, 2 * $r)
        $g.FillRectangle($hueco, ($x + $l * 0.5 - $r * 0.45), ($y + $l * 0.57), ($r * 0.9), ($l * 0.11))
    }
}

function New-Lienzo([int]$s) {
    $bmp = New-Object Drawing.Bitmap $s, $s, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([Drawing.Color]::Transparent)
    $bmp, $g
}

function ConvertTo-Png($bmp) {
    $ms = New-Object IO.MemoryStream
    $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $ms.ToArray()
}

function New-Icono([int]$s) {
    $bmp, $g = New-Lienzo $s
    $m = [math]::Max(0.5, $s * 0.04)
    Dibujar-Candado $g $m $m ($s - 2 * $m) ($s -ge 24)
    $g.Dispose()
    ConvertTo-Png $bmp
}

function New-Carpeta([int]$s) {
    $bmp, $g = New-Lienzo $s
    $trasera = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(255, 226, 172, 58))
    $frente = New-Object Drawing.Drawing2D.LinearGradientBrush (New-Object Drawing.RectangleF 0, ($s * 0.3), $s, ($s * 0.6)), ([Drawing.Color]::FromArgb(255, 252, 218, 120)), ([Drawing.Color]::FromArgb(255, 240, 192, 76)), 90.0
    $r = [math]::Max(1, $s * 0.05)
    $g.FillPath($trasera, (New-Redondeado ($s * 0.04) ($s * 0.14) ($s * 0.40) ($s * 0.20) $r))      # pestaña
    $g.FillPath($trasera, (New-Redondeado ($s * 0.04) ($s * 0.22) ($s * 0.92) ($s * 0.64) $r))      # fondo
    $g.FillPath($frente, (New-Redondeado ($s * 0.04) ($s * 0.32) ($s * 0.92) ($s * 0.56) $r))       # frente
    $l = $s * 0.56
    Dibujar-Candado $g ($s - $l) ($s - $l) $l ($s -ge 48)
    $g.Dispose()
    ConvertTo-Png $bmp
}

function Write-Ico([string]$Ruta, [scriptblock]$Dibujar) {
    $imagenes = @(16, 24, 32, 48, 64, 256 | ForEach-Object { [pscustomobject]@{ Lado = $_; Datos = (& $Dibujar $_) } })
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
    [IO.File]::WriteAllBytes($Ruta, $ms.ToArray())
    [IO.File]::WriteAllBytes(($Ruta -replace '\.ico$', '-256.png'), $imagenes[-1].Datos)   # vista previa
    "Ícono en $Ruta"
}

Write-Ico (Join-Path $Dir 'icono.ico') { param($s) New-Icono $s }
Write-Ico (Join-Path $Dir 'carpeta-bloqueada.ico') { param($s) New-Carpeta $s }
