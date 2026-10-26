# Compila dist\Candado.exe y dist\pruebas.exe con el csc que trae .NET Framework 4.8:
# no instala nada.
param([switch]$SoloPruebas)
$ErrorActionPreference = 'Stop'
$raiz = $PSScriptRoot
$dist = Join-Path $raiz 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null
$csc = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$wpf = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\WPF'
$comunes = @('/nologo', '/codepage:65001', '/optimize+', '/unsafe', '/reference:System.Security.dll', '/reference:System.Runtime.Serialization.dll', '/reference:System.Core.dll')
$nucleo = "$raiz\src\Cripto.cs", "$raiz\src\Cofre.cs", "$raiz\src\Ajustes.cs", "$raiz\src\Vigia.cs"
if (-not (Test-Path "$raiz\icono.ico") -or -not (Test-Path "$raiz\carpeta-bloqueada.ico")) { & "$raiz\icono.ps1" | Out-Null }
$comunes += "/resource:$raiz\carpeta-bloqueada.ico,CarpetaBloqueada.ico"

& $csc @comunes /target:exe "/out:$dist\pruebas.exe" /reference:System.Drawing.dll @nucleo "$raiz\pruebas\Pruebas.cs"
if ($LASTEXITCODE) { throw "csc (pruebas) terminó con código $LASTEXITCODE" }
if ($SoloPruebas) { return }

& $csc @comunes /target:winexe "/out:$dist\Candado.exe" "/win32icon:$raiz\icono.ico" `
    "/reference:$wpf\PresentationFramework.dll" "/reference:$wpf\PresentationCore.dll" "/reference:$wpf\WindowsBase.dll" `
    /reference:System.Xaml.dll /reference:System.Windows.Forms.dll /reference:System.Drawing.dll `
    "/resource:$raiz\src\Estilos.xaml,Estilos.xaml" "/resource:$raiz\src\Ventana.xaml,Ventana.xaml" "/resource:$raiz\src\Mini.xaml,Mini.xaml" `
    @nucleo "$raiz\src\Tema.cs" "$raiz\src\Ventana.cs" "$raiz\src\Mini.cs" "$raiz\src\Vigilante.cs" "$raiz\src\Programa.cs"
if ($LASTEXITCODE) { throw "csc (Candado) terminó con código $LASTEXITCODE" }
Get-Item "$dist\Candado.exe" | ForEach-Object { 'Compilado: {0} ({1:N0} KB)' -f $_.FullName, ($_.Length / 1KB) }
