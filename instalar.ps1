# Compila y copia Candado.exe a %LOCALAPPDATA%\Programs\Candado, con accesos en el menú
# Inicio y en el escritorio, y agrega «Candado: bloquear o desbloquear» al clic derecho de
# las carpetas (y del fondo de una carpeta abierta). Todo es solo para tu usuario: no pide
# permisos de administrador. Para actualizarla, ciérrala y vuelve a correrlo.
# También deja el vigilante (Candado.exe --vigilar) arrancando con Windows: es el que pide la
# contraseña al abrir una carpeta que la pide cada vez, y la vuelve a bloquear al cerrarla.
param([switch]$SinEscritorio)
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'compilar.ps1')
$destino = Join-Path $env:LOCALAPPDATA 'Programs\Candado'
$exe = Join-Path $destino 'Candado.exe'
New-Item -ItemType Directory -Force $destino | Out-Null
# El vigilante corre en segundo plano con el exe instalado: se detiene para reemplazarlo.
Get-CimInstance Win32_Process -Filter "Name = 'Candado.exe'" | Where-Object { $_.CommandLine -like '*--vigilar*' -and $_.ExecutablePath -eq $exe } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force; Wait-Process -Id $_.ProcessId -ErrorAction SilentlyContinue }
try { Copy-Item (Join-Path $PSScriptRoot 'dist\Candado.exe') $destino -Force }
catch { throw 'No se pudo copiar Candado.exe: ciérrala si está abierta y vuelve a intentarlo.' }

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

# Clic derecho: sobre una carpeta (%1) y sobre el fondo de una carpeta abierta (%V).
foreach ($par in @(@('HKCU:\Software\Classes\Directory\shell\Candado', '%1'), @('HKCU:\Software\Classes\Directory\Background\shell\Candado', '%V'))) {
    $clave = $par[0]
    New-Item -Path "$clave\command" -Force | Out-Null
    Set-Item -Path $clave -Value 'Candado: bloquear o desbloquear'
    New-ItemProperty -Path $clave -Name 'Icon' -Value "`"$exe`",0" -Force | Out-Null
    Set-Item -Path "$clave\command" -Value ("`"$exe`" --carpeta `"{0}`"" -f $par[1])
}

# El vigilante, al iniciar Windows. Si no hay carpetas que pidan la contraseña cada vez, se
# cierra solo a los 30 segundos.
New-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'Candado' -Value "`"$exe`" --vigilar" -Force | Out-Null
Start-Process $exe -ArgumentList '--vigilar'
"Instalada en $exe, con la opción en el clic derecho de las carpetas y el vigilante al iniciar Windows"
