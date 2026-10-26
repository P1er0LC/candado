# Quita Candado: el programa, sus accesos directos y la opción del clic derecho.
# No toca tus carpetas ni tus ajustes (%APPDATA%\Candado): una carpeta que quedó bloqueada
# sigue bloqueada, y para abrirla hay que volver a instalar Candado.
$ErrorActionPreference = 'Stop'
Get-CimInstance Win32_Process -Filter "Name = 'Candado.exe'" | Where-Object { $_.CommandLine -like '*--vigilar*' } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force; Wait-Process -Id $_.ProcessId -ErrorAction SilentlyContinue }
if (Get-Process -Name Candado -ErrorAction SilentlyContinue) { throw 'Cierra Candado antes de desinstalarla.' }
foreach ($clave in 'HKCU:\Software\Classes\Directory\shell\Candado', 'HKCU:\Software\Classes\Directory\Background\shell\Candado') {
    if (Test-Path $clave) { Remove-Item -Path $clave -Recurse }
}
Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'Candado' -ErrorAction SilentlyContinue
foreach ($c in [Environment]::GetFolderPath('Programs'), [Environment]::GetFolderPath('Desktop')) {
    $l = Join-Path $c 'Candado.lnk'
    if (Test-Path $l) { Remove-Item $l }
}
$destino = Join-Path $env:LOCALAPPDATA 'Programs\Candado'
if (Test-Path $destino) { Remove-Item $destino -Recurse }
'Candado quedó desinstalada. Tus carpetas y ajustes siguen como estaban.'
