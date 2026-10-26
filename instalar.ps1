# Compila y copia Candado.exe a %LOCALAPPDATA%\Programs\Candado, con accesos en el menú
# Inicio y en el escritorio. Para actualizarla, ciérrala y vuelve a correrlo.
param([switch]$SinEscritorio)
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'compilar.ps1')
$destino = Join-Path $env:LOCALAPPDATA 'Programs\Candado'
New-Item -ItemType Directory -Force $destino | Out-Null
try { Copy-Item (Join-Path $PSScriptRoot 'dist\Candado.exe') $destino -Force }
catch { throw 'No se pudo copiar Candado.exe: ciérrala si está abierta y vuelve a intentarlo.' }
$exe = Join-Path $destino 'Candado.exe'
$shell = New-Object -ComObject WScript.Shell
$carpetas = @([Environment]::GetFolderPath('Programs'))
if (-not $SinEscritorio) { $carpetas += [Environment]::GetFolderPath('Desktop') }
foreach ($c in $carpetas) {
    $acceso = $shell.CreateShortcut((Join-Path $c 'Candado.lnk'))
    $acceso.TargetPath = $exe
    $acceso.WorkingDirectory = $destino
    $acceso.Description = 'Contraseña para tus carpetas'
    $acceso.Save()
}
"Instalada en $exe"
