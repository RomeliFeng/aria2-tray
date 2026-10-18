# 生成 src\Aria2Tray\Assets\Aria2Tray.ico：程序图标（窗口、exe、通知）。托盘图标不在这里，由程序现画（Ui\TrayArt.cs）。
# 图形和空闲时的托盘图标一样：向下的箭头落到一条托盘上。macOS 那种圆角方块，绿底白图形。
# 只在改图标时跑一次，产物提交进仓库：
#   powershell -ExecutionPolicy Bypass -File tools\make-icons.ps1
Add-Type -AssemblyName System.Drawing

$assets = Join-Path $PSScriptRoot '..\src\Aria2Tray\Assets'
New-Item -ItemType Directory -Force $assets | Out-Null

function Add-RoundRect([System.Drawing.Drawing2D.GraphicsPath]$path, [single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $d = 2 * $r
    $path.StartFigure()
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
}

# 在 24 × 24 的格子里画，和 TrayArt.Idle 同一套坐标。$box 是图形占的边长，($ox, $oy) 是左上角。
function Add-Glyph([System.Drawing.Drawing2D.GraphicsPath]$path, [single]$ox, [single]$oy, [single]$box) {
    $k = $box / 24
    $pt = { param($x, $y) New-Object System.Drawing.PointF ([single]($ox + $x * $k)), ([single]($oy + $y * $k)) }
    $path.AddRectangle((New-Object System.Drawing.RectangleF ([single]($ox + 10.5 * $k)), ([single]($oy + 2.4 * $k)), ([single](3 * $k)), ([single](8.6 * $k))))
    $path.AddPolygon([System.Drawing.PointF[]]@((& $pt 5.2 10.2), (& $pt 18.8 10.2), (& $pt 12 17.2)))
    Add-RoundRect $path ($ox + 2.8 * $k) ($oy + 19.3 * $k) (18.4 * $k) (2.8 * $k) (1.4 * $k)
}

function New-Png([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $bg = New-Object System.Drawing.Drawing2D.GraphicsPath
    Add-RoundRect $bg 0 0 ($size - 1) ($size - 1) ($size * 0.225)
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 48, 176, 80))), $bg)
    $box = $size * 0.6
    $off = ($size - $box) / 2
    # 图形整体（箭头 + 托盘）在格子里偏下，往上挪一点看起来居中。
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath ([System.Drawing.Drawing2D.FillMode]::Winding)
    Add-Glyph $path $off ($off - $box * 0.02) $box
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)), $path)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $ms.ToArray()
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$images = foreach ($s in $sizes) { , (New-Png $s) }
$fs = [System.IO.File]::Create((Join-Path $assets 'Aria2Tray.ico'))
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $images[$i].Length
    $dim = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$len); $w.Write([uint32]$offset)
    $offset += $len
}
foreach ($img in $images) { $w.Write($img) }
$w.Dispose()
Get-ChildItem $assets -Filter *.ico | Select-Object Name, Length
