$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$destination = Join-Path $PSScriptRoot '../packaging/Assets'
New-Item -ItemType Directory -Force $destination | Out-Null
function Draw-Logo([int]$size, [string]$path) {
 $bitmap = [Drawing.Bitmap]::new($size,$size)
 $graphics = [Drawing.Graphics]::FromImage($bitmap)
 $graphics.SmoothingMode = 'AntiAlias'
 $graphics.Clear([Drawing.ColorTranslator]::FromHtml('#172337'))
 $pen = [Drawing.Pen]::new([Drawing.ColorTranslator]::FromHtml('#B5F2DB'),$size*0.075)
 $pen.StartCap='Round'; $pen.EndCap='Round'
 $graphics.DrawEllipse($pen,[single]($size*.24),[single]($size*.20),[single]($size*.40),[single]($size*.40))
 $graphics.DrawLine($pen,[single]($size*.59),[single]($size*.56),[single]($size*.78),[single]($size*.77))
 $dot = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#B2A7FF'))
 $graphics.FillEllipse($dot,[single]($size*.36),[single]($size*.32),[single]($size*.16),[single]($size*.16))
 $bitmap.Save($path,[Drawing.Imaging.ImageFormat]::Png)
 $graphics.Dispose(); $pen.Dispose(); $dot.Dispose(); $bitmap.Dispose()
}
Draw-Logo 44 (Join-Path $destination 'Square44x44Logo.png')
Draw-Logo 150 (Join-Path $destination 'Square150x150Logo.png')
Draw-Logo 50 (Join-Path $destination 'StoreLogo.png')
Draw-Logo 300 (Join-Path $destination 'Square150x150Logo.scale-200.png')
Draw-Logo 88 (Join-Path $destination 'Square44x44Logo.scale-200.png')
Draw-Logo 256 (Join-Path $destination 'Icon256.png')
Draw-Logo 300 (Join-Path $destination 'StoreIcon300.png')
# PNG-compressed ICO, using the standard ICO directory header.
$png = [IO.File]::ReadAllBytes((Join-Path $destination 'Icon256.png'))
$stream = [IO.File]::Create((Join-Path $PSScriptRoot '../packaging/App.ico'))
$writer = [IO.BinaryWriter]::new($stream)
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]1)
$writer.Write([byte]0); $writer.Write([byte]0); $writer.Write([byte]0); $writer.Write([byte]0)
$writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]$png.Length); $writer.Write([uint32]22); $writer.Write($png)
$writer.Dispose(); $stream.Dispose()
Write-Output 'Created original Local Search Studio icons.'
