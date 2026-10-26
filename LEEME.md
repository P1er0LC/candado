# Candado

Contraseña para carpetas de Windows, sin moverlas de donde están. Bloqueada, una carpeta
solo tiene adentro una nota y la carpeta oculta `.candado` con sus archivos cifrados;
desbloqueada, vuelve a estar como siempre.

## Cómo protege

- Cada carpeta tiene una llave de datos al azar (AES-256-CBC + HMAC-SHA256 por archivo,
  nombres incluidos en el índice cifrado).
- Esa llave se guarda cerrada con la **contraseña** (PBKDF2-SHA256, 310.000 iteraciones)
  y con una **clave de recuperación** de 160 bits que se manda por SMTP al protegerla y se
  muestra en pantalla. La app no la guarda.
- Mientras está desbloqueada, la llave queda además cerrada con la cuenta de Windows
  (DPAPI), para volver a bloquearla sin pedir la contraseña.
- Nada se borra hasta que su versión cifrada está escrita y el índice confirmado. Si se
  corta la luz a la mitad, la carpeta queda «A medias» y «Terminar» completa lo empezado.

Si se pierden la contraseña **y** el correo con la clave, no hay forma de abrirla.

## Límites

- Al bloquear, los originales borrados pueden quedar recuperables en el disco. La opción
  «sobrescribir los originales» lo evita en discos mecánicos; en un SSD no hay garantía.
- Mientras está desbloqueada, cualquiera que use la sesión la ve, y algunos programas
  dejan copias temporales o miniaturas fuera de la carpeta.
- No deja proteger carpetas del sistema, de programas, de AppData, las personales enteras
  (Documentos, Escritorio…), las de OneDrive ni las que tienen enlaces o uniones adentro.
- El SMTP va con STARTTLS (puerto 587). Con Gmail hace falta una contraseña de aplicación.

## Compilar y probar

Con el `csc` de .NET Framework 4.8, sin instalar nada:

```powershell
.\compilar.ps1                              # dist\Candado.exe y dist\pruebas.exe
.\dist\pruebas.exe D:\candado-pruebas       # núcleo: ida y vuelta, cortes, alteraciones, correo falso
.\probar-ventana.ps1                        # capturas de cada pantalla y flujos reales con datos de demo
.\instalar.ps1                              # %LOCALAPPDATA%\Programs\Candado + accesos directos
```

La carpeta de pruebas no puede estar dentro de AppData: Candado no deja proteger nada ahí.
