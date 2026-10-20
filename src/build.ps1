# Builds IntunePackageGrabber.exe with the .NET Framework 4.x C# compiler (included in Windows)
# and embeds IntuneWinAppUtil.exe as a resource.
param(
    [string]$IntuneWinAppUtil = (Join-Path $PSScriptRoot '..\..\IntuneWinAppUtil.exe'),
    [string]$Output = (Join-Path $PSScriptRoot '..\IntunePackageGrabber.exe'),
    # Do not embed IntuneWinAppUtil.exe (distribution build: the user downloads it with the button in the UI)
    [switch]$NoEmbed
)
$ErrorActionPreference = 'Stop'
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$icon = Join-Path $PSScriptRoot 'app.ico'

if (-not (Test-Path $icon)) {
    Add-Type -AssemblyName System.Drawing
    $bmp = New-Object Drawing.Bitmap 64, 64
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $path = New-Object Drawing.Drawing2D.GraphicsPath
    $r = 14; $path.AddArc(2, 2, $r, $r, 180, 90); $path.AddArc(48, 2, $r, $r, 270, 90); $path.AddArc(48, 48, $r, $r, 0, 90); $path.AddArc(2, 48, $r, $r, 90, 90); $path.CloseFigure()
    $g.FillPath((New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(15, 108, 189))), $path)
    $white = New-Object Drawing.Pen ([Drawing.Color]::White), 5
    $white.StartCap = 'Round'; $white.EndCap = 'Round'; $white.LineJoin = 'Round'
    $g.DrawLine($white, 32, 12, 32, 36)
    $g.DrawLines($white, [Drawing.Point[]]@((New-Object Drawing.Point 21, 26), (New-Object Drawing.Point 32, 37), (New-Object Drawing.Point 43, 26)))
    $g.DrawLines($white, [Drawing.Point[]]@((New-Object Drawing.Point 14, 40), (New-Object Drawing.Point 14, 50), (New-Object Drawing.Point 50, 50), (New-Object Drawing.Point 50, 40)))
    $g.Dispose()
    $ms = New-Object IO.MemoryStream
    $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png)
    $png = $ms.ToArray()
    $fs = [IO.File]::Create($icon); $bw = New-Object IO.BinaryWriter $fs
    $bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]1)
    $bw.Write([Byte]64); $bw.Write([Byte]64); $bw.Write([Byte]0); $bw.Write([Byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32); $bw.Write([UInt32]$png.Length); $bw.Write([UInt32]22)
    $bw.Write($png); $bw.Close()
}

$cscArgs = @(
    '/nologo', '/target:winexe', '/optimize+', '/platform:anycpu',
    "/out:$Output", "/win32icon:$icon", "/win32manifest:$(Join-Path $PSScriptRoot 'app.manifest')",
    '/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll',
    '/r:System.Web.Extensions.dll', '/r:System.IO.Compression.dll', '/r:System.IO.Compression.FileSystem.dll', '/r:System.Xml.dll'
)
if ($NoEmbed) { }
elseif (Test-Path $IntuneWinAppUtil) { $cscArgs += "/resource:$((Resolve-Path $IntuneWinAppUtil).Path),IntuneWinAppUtil.exe" }
else { Write-Warning "IntuneWinAppUtil.exe not found at $IntuneWinAppUtil : it will not be embedded in the exe." }
$cscArgs += (Join-Path $PSScriptRoot 'Program.cs')

& $csc @cscArgs
if ($LASTEXITCODE -ne 0) { throw "Build failed ($LASTEXITCODE)" }
Get-Item $Output | Select-Object FullName, Length, LastWriteTime
