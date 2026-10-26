# Prueba la ventana con datos de demo: dibuja cada pantalla y hace los flujos de verdad
# (desbloquear, error, bloquear, terminar, proteger), comparando los archivos al final.
param([string]$Salida = (Join-Path $env:TEMP 'candado-capturas'))
$ErrorActionPreference = 'Stop'
$dist = Join-Path $PSScriptRoot 'dist'
$datos = Join-Path $Salida 'datos'
$demo = 'D:\candado-pruebas\demo'
New-Item -ItemType Directory -Force $Salida | Out-Null
& "$dist\pruebas.exe" --demo $datos $demo | Out-Null

function Capturar([string]$Pantalla, [string]$Datos = $datos, [string]$Archivo = $Pantalla, [string]$Extra = '') {
    $png = Join-Path $Salida "$Archivo.png"
    Remove-Item $png -ErrorAction SilentlyContinue
    $p = Start-Process "$dist\Candado.exe" -ArgumentList "--datos `"$Datos`" --captura `"$png`" --pantalla $Pantalla $Extra" -PassThru
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
$extras = '.candado', 'Carpeta protegida con Candado.txt', 'Desbloquear con Candado.lnk', 'desktop.ini'
'Para proteger quedó sin archivos a la vista: ' + (@(Get-ChildItem "$demo\Para proteger" -Force | Where-Object { $_.Name -notin $extras }).Count -eq 0)
Capturar 'flujo-proteger-acceso'
$acc = "$demo\Para proteger (acceso)"
$negado = try { Get-ChildItem -LiteralPath "$acc\.candado\contenido" -ErrorAction Stop | Out-Null; $false } catch { $true }
'Para proteger (acceso): sin archivos a la vista: {0} · el contenido no se puede listar: {1}' -f (@(Get-ChildItem -LiteralPath $acc -Force | Where-Object { $_.Name -notin $extras }).Count -eq 0), $negado
Capturar 'proteger'
Capturar 'lista' $datos 'lista-final'

# La ventanita: lo que abre el acceso «Desbloquear con Candado» de una carpeta bloqueada.
function Acceso([string]$Dir) {
    $lnk = Join-Path $Dir 'Desbloquear con Candado.lnk'
    if (-not (Test-Path -LiteralPath $lnk)) { return 'no hay' }
    $s = (New-Object -ComObject WScript.Shell).CreateShortcut($lnk)
    '{0} {1}' -f (Split-Path $s.TargetPath -Leaf), $s.Arguments
}
$v = "$demo\Ventanita"
'Ventanita bloqueada, acceso: ' + (Acceso $v)
foreach ($p in 'mini', 'mini-error', 'mini-recuperar') { Capturar $p $datos $p "--carpeta `"$v`"" }
Capturar 'mini-listo' $datos 'mini-listo' "--carpeta `"$v`""
'Ventanita desbloqueada idéntica: {0} · acceso: {1}' -f ((Huella $v) -eq $referencia), (Acceso $v)
Capturar 'mini' $datos 'mini-bloquear' "--carpeta `"$v`""
'Ventanita bloqueada de nuevo, acceso: ' + (Acceso $v)
$w = "$demo\Ventanita (acceso)"
Capturar 'mini-listo' $datos 'mini-listo-acceso' "--carpeta `"$w`""
'Ventanita (acceso) desbloqueada idéntica: {0} · acceso: {1}' -f ((Huella $w) -eq $referencia), (Acceso $w)

# Clic derecho: lo que llega con --carpeta.
Capturar 'carpeta' $datos 'clic-bloqueada' "--carpeta `"$demo\Clientes 2026`""
New-Item -ItemType Directory -Force "$demo\Sin proteger" | Out-Null
Capturar 'carpeta' $datos 'clic-sin-proteger' "--carpeta `"$demo\Sin proteger`""
Capturar 'carpeta' $datos 'clic-sistema' "--carpeta `"$env:SystemRoot`""
# Con Candado ya abierta: la segunda copia le pasa la carpeta a la primera y se cierra.
$png = Join-Path $Salida 'clic-con-app-abierta.png'
Remove-Item $png -ErrorAction SilentlyContinue
$a = Start-Process "$dist\Candado.exe" -ArgumentList "--datos `"$datos`" --instancia prueba-tuberia --captura `"$png`" --pantalla esperar" -PassThru
Start-Sleep -Seconds 4
$b = Start-Process "$dist\Candado.exe" -ArgumentList "--datos `"$datos`" --instancia prueba-tuberia --carpeta `"$demo\Clientes 2026`"" -PassThru
$bSalio = $b.WaitForExit(10000)
$a.WaitForExit(60000) | Out-Null
'{0,-18} {1} (la segunda copia se cerró sola: {2})' -f 'clic-app-abierta', $(if (Test-Path $png) { 'ok' } else { 'SIN CAPTURA' }), $bSalio
if (-not $bSalio) { $b.Kill() }
$vacio = Join-Path $Salida 'vacio'
New-Item -ItemType Directory -Force $vacio | Out-Null
Remove-Item "$vacio\config.json" -ErrorAction SilentlyContinue
foreach ($p in 'lista', 'proteger', 'recuperar', 'correo') { Capturar $p $vacio "vacio-$p" }
"Capturas en $Salida"
