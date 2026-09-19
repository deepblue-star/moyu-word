# Reproducible code-drawn application icon; no external image assets.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$iconPath = Join-Path ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))) 'assets\moyu.ico'
$image = New-Object Drawing.Bitmap(64, 64)
$graphics = [Drawing.Graphics]::FromImage($image)
$graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
$green = New-Object Drawing.SolidBrush([Drawing.ColorTranslator]::FromHtml('#527666'))
$cream = New-Object Drawing.SolidBrush([Drawing.ColorTranslator]::FromHtml('#F7F8F1'))
$pen = New-Object Drawing.Pen([Drawing.ColorTranslator]::FromHtml('#F7F8F1'), 3)
try {
    $graphics.Clear([Drawing.Color]::Transparent)
    $graphics.FillEllipse($green, 3, 3, 58, 58)
    $graphics.FillEllipse($cream, 21, 14, 22, 31)
    $graphics.DrawLine($pen, 30, 34, 25, 51)
    $handle = $image.GetHicon()
    $icon = [Drawing.Icon]::FromHandle($handle)
    $stream = [IO.File]::Create($iconPath)
    try { $icon.Save($stream) } finally { $stream.Dispose(); $icon.Dispose() }
} finally { $pen.Dispose(); $cream.Dispose(); $green.Dispose(); $graphics.Dispose(); $image.Dispose() }
