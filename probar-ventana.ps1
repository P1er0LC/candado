# Prueba la ventana con datos de demo: dibuja cada pantalla y hace los flujos de verdad
# (desbloquear, error, bloquear, terminar, proteger), comparando los archivos al final.
param([string]$Salida = (Join-Path $env:TEMP 'candado-capturas'))
$ErrorActionPreference = 'Stop'
$dist = Join-Path $PSScriptRoot 'dist'
$datos = Join-Path $Salida 'datos'
$demo = 'D:\candado-pruebas\demo'
New-Item -ItemType Directory -Force $Salida | Out-Null
& "$dist\pruebas.exe" --demo $datos $demo | Out-Null

function Capturar([string]$Pantalla, [string]$Datos = $datos, [string]$Archivo = $Pantalla) {
    $png = Join-Path $Salida "$Archivo.png"
    Remove-Item $png -ErrorAction SilentlyContinue
    $p = Start-Process "$dist\Candado.exe" -ArgumentList "--datos `"$Datos`" --captura `"$png`" --pantalla $Pantalla" -PassThru
    if (-not $p.WaitForExit(120000)) { $p.Kill(); "${Pantalla}: no terminó" ; return }
    '{0,-18} {1}' -f $Archivo, $(if (Test-Path $png) { 'ok' } else { 'SIN CAPTURA' })
}

function Huella([string]$Dir) {
    (Get-ChildItem $Dir -Recurse -File -Force | Where-Object { $_.FullName -notmatch '\\\.candado\\' -and $_.Name -ne 'Carpeta protegida con Candado.txt' } |
        Sort-Object { $_.FullName.Substring($Dir.Length) } | ForEach-Object { $_.FullName.Substring($Dir.Length) + '|' + (Get-FileHash $_.FullName).Hash }) -join "`n"
}

$referencia = Huella "$demo\Para proteger"   # misma semilla que las demás carpetas de la demo
foreach ($p in 'lista', 'opciones', 'desbloquear', 'progreso', 'clave') { Capturar $p }
Capturar 'flujo-error'
Capturar 'flujo-desbloquear'
'Clientes 2026 desbloqueada idéntica: ' + ((Huella "$demo\Clientes 2026") -eq $referencia)
Capturar 'flujo-bloquear'
Capturar 'flujo-terminar'
'Fotos de sesiones terminada idéntica: ' + ((Huella "$demo\Fotos de sesiones") -eq $referencia)
Capturar 'flujo-proteger'
'Para proteger quedó sin archivos a la vista: ' + (@(Get-ChildItem "$demo\Para proteger" -Force | Where-Object { $_.Name -notin '.candado', 'Carpeta protegida con Candado.txt' }).Count -eq 0)
Capturar 'lista' $datos 'lista-final'
$vacio = Join-Path $Salida 'vacio'
New-Item -ItemType Directory -Force $vacio | Out-Null
Remove-Item "$vacio\config.json" -ErrorAction SilentlyContinue
foreach ($p in 'lista', 'proteger', 'recuperar', 'correo') { Capturar $p $vacio "vacio-$p" }
"Capturas en $Salida"
