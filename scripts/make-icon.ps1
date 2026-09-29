# Genera el icono de DOCS-DR: src/DocsDR.App/Assets/icon.ico (16-256 px) y icon-256.png.
# Cada tamaño se dibuja por separado (con menos detalle en los pequeños) para que se vea nítido.
# Uso: powershell -File scripts/make-icon.ps1
Add-Type -AssemblyName System.Drawing

$out = Join-Path $PSScriptRoot '..\src\DocsDR.App\Assets'
New-Item -ItemType Directory -Force $out | Out-Null

function C($hex) { [System.Drawing.ColorTranslator]::FromHtml($hex) }

function RoundedRect($x, $y, $w, $h, $r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    $p
}

# Dibuja el icono en un lienzo de $size px. Coordenadas de diseño: 256 x 256.
function Draw-Icon([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $k = $size / 256.0
    $g.ScaleTransform($k, $k)
    $small = $size -le 32

    # Fondo: cuadrado redondeado con degradado azul
    $bgPath = RoundedRect 8 8 240 240 54
    $p1 = New-Object System.Drawing.PointF 0, 8
    $p2 = New-Object System.Drawing.PointF 0, 248
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush $p1, $p2, (C '#3B82F6'), (C '#1E3A8A')
    $g.FillPath($bg, $bgPath)

    # Hoja con la esquina superior derecha doblada
    $page = New-Object System.Drawing.Drawing2D.GraphicsPath
    $page.AddPolygon(@(
        (New-Object System.Drawing.PointF 64, 40),
        (New-Object System.Drawing.PointF 148, 40),
        (New-Object System.Drawing.PointF 198, 90),
        (New-Object System.Drawing.PointF 198, 216),
        (New-Object System.Drawing.PointF 64, 216)))
    # sombra suave
    if (-not $small) {
        $shadow = New-Object System.Drawing.Drawing2D.Matrix
        $shadow.Translate(0, 6)
        $sp = $page.Clone(); $sp.Transform($shadow)
        $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(70, 8, 20, 70))), $sp)
    }
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)), $page)

    # Doblez
    $fold = New-Object System.Drawing.Drawing2D.GraphicsPath
    $fold.AddPolygon(@(
        (New-Object System.Drawing.PointF 148, 40),
        (New-Object System.Drawing.PointF 148, 90),
        (New-Object System.Drawing.PointF 198, 90)))
    $g.FillPath((New-Object System.Drawing.SolidBrush (C '#BFD3F7')), $fold)

    $green = New-Object System.Drawing.SolidBrush (C '#16A34A')
    if ($small) {
        # Versión simple: un bloque verde que sugiere la tabla
        $g.FillRectangle($green, 84, 128, 98, 26)
        $g.FillRectangle((New-Object System.Drawing.SolidBrush (C '#86C9A1')), 84, 164, 98, 18)
    }
    else {
        # Renglones de texto
        $gray = New-Object System.Drawing.SolidBrush (C '#9CB3D9')
        $g.FillPath($gray, (RoundedRect 84 62 52 9 4.5))
        $g.FillPath($gray, (RoundedRect 84 80 46 9 4.5))
        $g.FillPath($gray, (RoundedRect 84 104 100 9 4.5))

        # Tabla verde: encabezado relleno y rejilla
        $g.FillPath($green, (RoundedRect 84 128 100 24 5))
        $pen = New-Object System.Drawing.Pen (C '#16A34A'), 5
        $g.DrawRectangle($pen, 86, 130, 96, 70)
        $g.DrawLine($pen, 86, 176, 182, 176)
        $g.DrawLine($pen, 134, 130, 134, 200)
        $g.DrawLine((New-Object System.Drawing.Pen ([System.Drawing.Color]::White), 4), 134, 132, 134, 150)
    }
    $g.Dispose()
    $bmp
}

function Png-Bytes($bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    , $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 96, 128, 256
$images = foreach ($s in $sizes) {
    $b = Draw-Icon $s
    [pscustomobject]@{ Size = $s; Png = (Png-Bytes $b); Bitmap = $b }
}

# ICO: cabecera + directorio + imágenes PNG
$ms = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $ms
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($i in $images) {
    $dim = if ($i.Size -ge 256) { 0 } else { $i.Size }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$i.Png.Length); $w.Write([uint32]$offset)
    $offset += $i.Png.Length
}
foreach ($i in $images) { $w.Write($i.Png) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $out 'icon.ico'), $ms.ToArray())

($images | Where-Object Size -eq 256).Bitmap.Save((Join-Path $out 'icon-256.png'), [System.Drawing.Imaging.ImageFormat]::Png)

# Hoja de prueba (fondo claro y oscuro) para revisar todos los tamaños
$sheetW = 700; $sheet = New-Object System.Drawing.Bitmap $sheetW, 340
$sg = [System.Drawing.Graphics]::FromImage($sheet)
$sg.FillRectangle((New-Object System.Drawing.SolidBrush (C '#F3F3F3')), 0, 0, $sheetW, 170)
$sg.FillRectangle((New-Object System.Drawing.SolidBrush (C '#202020')), 0, 170, $sheetW, 170)
foreach ($row in 0, 1) {
    $x = 10
    foreach ($i in ($images | Where-Object Size -le 128)) {
        $sg.DrawImageUnscaled($i.Bitmap, $x, $row * 170 + 10)
        $x += $i.Size + 12
    }
}
$sheet.Save((Join-Path $out 'icon-preview.png'))
$sg.Dispose()
"Icono generado en $out"
