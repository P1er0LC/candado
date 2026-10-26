# Candado

Contraseña para carpetas de Windows, sin moverlas de donde están.

Bloqueada, una carpeta se ve en el Explorador con un candado en el ícono, y adentro solo
tiene una nota y la carpeta oculta `.candado` con sus archivos cifrados. Desbloqueada,
vuelve a estar como siempre, con sus nombres, fechas y atributos. Si olvidas la
contraseña, la recuperas con una clave que te llega por correo.

![Lista de carpetas protegidas](docs/lista.png)

## Qué hace

- **Proteger una carpeta:** la eliges, le pones una contraseña y Candado te muestra una
  clave de recuperación (y te la manda por correo). Después la bloqueas.
- **Desbloquear:** pones la contraseña y los archivos vuelven tal cual.
- **Volver a bloquear:** no te pide la contraseña: mientras está abierta, tu cuenta de
  Windows guarda la llave.
- **Olvidé mi contraseña:** pegas la clave del correo y eliges una nueva.
- **Si se corta la luz o se cierra a la mitad:** la carpeta queda «A medias» y «Terminar»
  completa lo que faltaba, sin perder nada.
- **Cambiar la contraseña** y **quitar la protección** cuando quieras.

## Desde el Explorador

<img src="docs/carpeta-bloqueada.png" alt="Ícono de una carpeta bloqueada" width="64" align="right">

- **Clic derecho → «Candado: bloquear o desbloquear».** Hace lo que corresponde: si la
  carpeta no está protegida, abre «Proteger»; si está bloqueada, pide la contraseña; si
  está desbloqueada, la bloquea; si quedó a medias, la termina. También funciona con clic
  derecho en el fondo de una carpeta abierta, y sobre una subcarpeta de una protegida.
  Si Candado ya está abierta, le pasa la carpeta a esa misma ventana.
- **Ícono:** mientras está bloqueada, la carpeta se ve con el candado, y al pasar el mouse
  dice «Bloqueada con Candado». Si la carpeta ya tenía su propio ícono, vuelve al
  desbloquearla. A veces el Explorador tarda unos segundos en refrescarlo, o hay que pulsar
  F5.

En Windows 11 la opción aparece dentro de «Mostrar más opciones».

## Instalar

Necesita Windows 10 u 11. No instala nada más: compila con el `csc` que ya trae .NET
Framework 4.8.

```powershell
git clone https://github.com/P1er0LC/candado.git
cd candado
.\instalar.ps1
```

Queda en `%LOCALAPPDATA%\Programs\Candado`, con accesos en el menú Inicio y en el
escritorio, y con la opción en el clic derecho de las carpetas. Todo es solo para tu
usuario: no pide permisos de administrador. Para actualizarla, cierra Candado, haz
`git pull` y vuelve a correr `instalar.ps1`.

`desinstalar.ps1` quita el programa, los accesos y la opción del clic derecho. No toca tus
carpetas ni tus ajustes: una carpeta que quedó bloqueada sigue bloqueada, y para abrirla
hay que volver a instalar Candado.

## Antes de proteger algo importante: el correo

En **Correo de recuperación** van los datos con los que Candado te manda la clave de cada
carpeta. Con Gmail:

| Campo | Valor |
|---|---|
| Servidor SMTP | `smtp.gmail.com` |
| Puerto | `587` |
| Tu correo | tu dirección de Gmail |
| Contraseña de aplicación | una creada en [myaccount.google.com/apppasswords](https://myaccount.google.com/apppasswords), no tu contraseña normal |

Pulsa **Enviar correo de prueba** para comprobarlo. Si no configuras el correo, la clave
solo aparece en pantalla.

<p>
  <img src="docs/correo.png" alt="Ajustes del correo de recuperación" width="49%">
  <img src="docs/clave-recuperacion.png" alt="Clave de recuperación de una carpeta" width="49%">
</p>

## Cómo protege

- Cada carpeta tiene una llave de datos al azar. Cada archivo se cifra con AES-256-CBC y se
  autentica con HMAC-SHA256; los nombres y las rutas van en un índice cifrado, y los
  archivos cifrados tienen nombres al azar.
- Esa llave se guarda cerrada dos veces: con la **contraseña** (PBKDF2-SHA256, 310.000
  iteraciones) y con la **clave de recuperación** (160 bits al azar). La app no guarda la
  clave: solo te la muestra y te la manda.
- Mientras la carpeta está desbloqueada, la llave queda además cerrada con tu cuenta de
  Windows (DPAPI). En ese estado los archivos ya están a la vista, así que no se pierde nada.
- Nada se borra hasta que su versión cifrada está escrita y el índice confirmado, y cada
  paso queda anotado: un corte a la mitad nunca deja archivos perdidos.
- La contraseña del correo también queda cerrada con tu cuenta de Windows.

Si pierdes la contraseña **y** el correo con la clave, no hay forma de abrir la carpeta.
Es el precio de que el cifrado sea de verdad. Quien tenga tu correo y acceso a tu PC sí
puede abrirla: conviene tener activada la verificación en dos pasos en la cuenta.

## Límites

- **Rastros en el disco:** al bloquear, los originales borrados pueden recuperarse con
  herramientas especiales. La opción «sobrescribir los originales» (en Correo de
  recuperación) lo evita en discos mecánicos; en un SSD no hay garantía.
- **Mientras está desbloqueada,** cualquiera que use tu sesión la ve, y algunos programas
  (Photoshop, por ejemplo) dejan copias temporales o miniaturas fuera de la carpeta.
- **Velocidad:** cifra a unos 35–100 MB/s según el disco, y comprobar la contraseña tarda
  cerca de un segundo. Para carpetas de decenas de GB, cada bloqueo es una espera.
- **Carpetas que no deja proteger:** las del sistema y de programas, AppData, las
  personales enteras (Documentos, Escritorio, Imágenes…), las que están en OneDrive y las
  que tienen enlaces o uniones adentro.
- **Correo:** se conecta con STARTTLS (puerto 587); el 465 no lo admite.

## Desarrollo

```
src/Cripto.cs     cifrado: AES de .NET, PBKDF2 y HMAC de Windows (CNG)
src/Cofre.cs      una carpeta protegida: crear, bloquear, desbloquear, recuperar, reanudar
src/Ajustes.cs    configuración, lista de carpetas y envío del correo
src/Ventana.cs    la ventana (WPF); el diseño está en src/Ventana.xaml
pruebas/          pruebas del núcleo
```

```powershell
.\compilar.ps1                             # dist\Candado.exe y dist\pruebas.exe
.\dist\pruebas.exe D:\candado-pruebas      # 17 pruebas: ida y vuelta, cortes, alteraciones, correo falso
.\probar-ventana.ps1                       # capturas de cada pantalla y flujos reales con datos de demo
```

La carpeta de pruebas no puede estar dentro de AppData, porque Candado no deja proteger
nada ahí. Los `.ps1` tienen que quedar en UTF-8 **con BOM**: PowerShell 5.1 lee como ANSI
los que no lo tienen y rompe las tildes.
