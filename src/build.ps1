# 生成图标并编译 Claude账号管家.exe（只用系统自带的 .NET Framework 4 编译器）
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$src = $PSScriptRoot
$ico = Join-Path $src 'app.ico'

$pngs = foreach ($s in 256, 48, 32, 16) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'
    $r = [int]($s * 0.24); $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $r * 2, $r * 2, 180, 90); $path.AddArc($s - $r * 2 - 1, 0, $r * 2, $r * 2, 270, 90)
    $path.AddArc($s - $r * 2 - 1, $s - $r * 2 - 1, $r * 2, $r * 2, 0, 90); $path.AddArc(0, $s - $r * 2 - 1, $r * 2, $r * 2, 90, 90)
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(217, 119, 87))), $path)
    $font = New-Object System.Drawing.Font 'Segoe UI', ($s * 0.52), ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
    $fmt = New-Object System.Drawing.StringFormat; $fmt.Alignment = 'Center'; $fmt.LineAlignment = 'Center'
    $g.DrawString('C', $font, [System.Drawing.Brushes]::White, (New-Object System.Drawing.RectangleF 0, ($s * 0.02), $s, $s), $fmt)
    $ms = New-Object IO.MemoryStream; $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    , @($s, $ms.ToArray())
}
$fs = [IO.File]::Create($ico); $w = New-Object IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$pngs.Count)
$off = 6 + 16 * $pngs.Count
foreach ($p in $pngs) {
    $d = if ($p[0] -ge 256) { 0 } else { $p[0] }
    $w.Write([byte]$d); $w.Write([byte]$d); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$p[1].Length); $w.Write([uint32]$off); $off += $p[1].Length
}
foreach ($p in $pngs) { $w.Write($p[1]) }
$w.Close()

$fw = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
$out = Join-Path (Split-Path $src) 'Claude账号管家.exe'
& "$fw\csc.exe" /nologo /target:winexe /optimize+ /codepage:65001 "/out:$out" "/win32icon:$ico" "/lib:$fw\WPF" `
    /r:PresentationFramework.dll /r:PresentationCore.dll /r:WindowsBase.dll /r:System.Xaml.dll `
    /r:System.Web.Extensions.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Management.dll `
    "/resource:$src\main.xaml,main.xaml" "/resource:$src\dialog.xaml,dialog.xaml" "/resource:$src\theme.xaml,theme.xaml" `
    "$src\Program.cs"
if ($LASTEXITCODE) { exit 1 }
Get-Item $out | Select-Object Name, Length
