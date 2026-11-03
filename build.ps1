$ErrorActionPreference = 'Stop'
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$src = Join-Path $PSScriptRoot 'QuietGuard.cs'
$exe = Join-Path $PSScriptRoot 'QuietGuard.exe'
$ico = Join-Path $PSScriptRoot 'QuietGuard.ico'
$opts = @('/nologo', '/codepage:65001', '/optimize+', '/target:winexe',
          '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll')

& $csc @opts "/out:$exe" $src
if ($LASTEXITCODE) { throw 'Build failed.' }
Start-Process -FilePath $exe -ArgumentList '--write-icon', "`"$ico`"" -Wait

& $csc @opts "/out:$exe" "/win32icon:$ico" $src
if ($LASTEXITCODE) { throw 'Build failed.' }
Write-Host "Done: $exe"
