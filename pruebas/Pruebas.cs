// Pruebas del núcleo de Candado. Uso: pruebas.exe <carpeta de trabajo>
// Crea datos de prueba, los bloquea y desbloquea de todas las formas (contraseña,
// recuperación, cortes a la mitad matando el proceso, archivos alterados o abiertos) y
// compara con SHA-256 que todo vuelva idéntico. El correo se prueba contra un servidor
// SMTP falso local: no sale nada a internet.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Candado;

static class Pruebas
{
    const string Clave1 = "contraseña-de-prueba-1";
    static int fallas = 0, total = 0;
    static string trabajo;

    static int Main(string[] args)
    {
        using (Stream s = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("CarpetaBloqueada.ico"))
        using (MemoryStream m = new MemoryStream()) { s.CopyTo(m); Cofre.IconoCarpeta = m.ToArray(); }
        if (args.Length >= 3 && args[0] == "--icono") return GuardarIcono(args[1], args[2]);
        if (args.Length >= 1 && args[0] == "--eco") { foreach (string a in args) Console.WriteLine("[" + a + "]"); return 0; }
        if (args.Length >= 1 && args[0] == "--hijo") return Hijo(args);
        if (args.Length >= 1 && args[0] == "--medir") return Medir(args.Length > 1 ? args[1] : Path.GetTempPath());
        if (args.Length >= 3 && args[0] == "--demo") return Demo(args[1], args[2]);
        if (args.Length >= 1 && args[0] == "--correo") { Directory.CreateDirectory(Path.GetTempPath()); trabajo = Path.GetTempPath(); ProbarCorreo(); return 0; }
        trabajo = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "candado-pruebas");
        if (Directory.Exists(trabajo)) BorrarTodo(trabajo);
        Directory.CreateDirectory(trabajo);
        Datos.Dir = Path.Combine(trabajo, "datos-app");
        Stopwatch reloj = Stopwatch.StartNew();

        Probar("PBKDF2 y HMAC de Windows dan lo mismo que los de .NET", ProbarCng);
        Probar("Clave de recuperación: formato y normalización", ProbarClave);
        Probar("Bloquear y desbloquear: todo vuelve idéntico", ProbarIdaVuelta);
        Probar("Carpeta con su propio ícono (desktop.ini)", ProbarDesktopIniPropio);
        Probar("Solo bloquear el acceso: nadie entra y los archivos no se tocan", ProbarAcceso);
        Probar("Contraseña equivocada no cambia nada", ProbarContrasenaMala);
        Probar("Bloquear de nuevo sin contraseña (cuenta de Windows)", ProbarSinContrasena);
        Probar("Recuperar con la clave y contraseña nueva", ProbarRecuperacion);
        Probar("Cambiar la contraseña", ProbarCambio);
        Probar("Un archivo cifrado alterado se detecta y no rompe nada", ProbarAlterado);
        Probar("Archivo abierto en otro programa: se deshace el bloqueo", ProbarAbierto);
        Probar("Fallo a mitad del bloqueo: se deshace solo", ProbarFalloBloqueo);
        Probar("Corte (proceso matado) mientras se bloquea", () => ProbarCorte("bloquear"));
        Probar("Corte (proceso matado) mientras se desbloquea", () => ProbarCorte("desbloquear"));
        Probar("Sobrescribir originales al bloquear", ProbarSobrescribir);
        Probar("Quitar la protección", ProbarQuitar);
        Probar("Carpetas que no se dejan proteger", ProbarValidar);
        Probar("Correo de recuperación (servidor SMTP falso)", ProbarCorreo);
        Probar("Velocidad con un archivo de 300 MB", ProbarVelocidad);

        Console.WriteLine();
        Console.WriteLine("{0} de {1} pruebas bien, {2} fallaron ({3:N0} s)", total - fallas, total, fallas, reloj.Elapsed.TotalSeconds);
        BorrarTodo(trabajo);
        return fallas == 0 ? 0 : 1;
    }

    static void Probar(string nombre, Action prueba)
    {
        total++;
        Stopwatch r = Stopwatch.StartNew();
        try { prueba(); Console.WriteLine("OK    {0} ({1:N1} s)", nombre, r.Elapsed.TotalSeconds); }
        catch (Exception e) { fallas++; Console.WriteLine("FALLA {0}: {1}", nombre, e.Message); Console.WriteLine(e.StackTrace); }
    }

    static void Afirmar(bool c, string que) { if (!c) throw new Exception(que); }

    static void Espera<T>(Action a, string contiene) where T : Exception
    {
        try { a(); }
        catch (T e) { Afirmar(contiene == null || e.Message.Contains(contiene), "mensaje inesperado: " + e.Message); return; }
        throw new Exception("se esperaba " + typeof(T).Name + " con «" + contiene + "»");
    }

    // ---------------------------------------------------------------- datos de prueba

    static string NuevaCarpeta(string nombre)
    {
        string c = Path.Combine(trabajo, nombre);
        Directory.CreateDirectory(c);
        Random azar = new Random(42);
        int[] tamanos = { 0, 1, 15, 16, 17, 1048575, 1048576, 1048577, 5 * 1048576 + 3 };
        for (int i = 0; i < tamanos.Length; i++) Escribir(Path.Combine(c, "archivo " + i + ".bin"), tamanos[i], azar);
        Directory.CreateDirectory(Path.Combine(c, "Fotos ñandú", "2026", "octubre"));
        Escribir(Path.Combine(c, "Fotos ñandú", "2026", "octubre", "tatuaje café ☕.jpg"), 300000, azar);
        File.WriteAllText(Path.Combine(c, "Fotos ñandú", "notas.txt"), "MARCADOR-SECRETO texto con tildes áéíóú", Encoding.UTF8);
        Directory.CreateDirectory(Path.Combine(c, "vacía"));
        Directory.CreateDirectory(Path.Combine(c, "Fotos ñandú", "vacía también"));
        string oculto = Path.Combine(c, "oculto.dat"); Escribir(oculto, 1000, azar); File.SetAttributes(oculto, FileAttributes.Hidden);
        string soloLectura = Path.Combine(c, "solo lectura.txt"); File.WriteAllText(soloLectura, "no tocar"); File.SetAttributes(soloLectura, FileAttributes.ReadOnly);
        File.SetCreationTimeUtc(Path.Combine(c, "archivo 3.bin"), new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(Path.Combine(c, "archivo 3.bin"), new DateTime(2021, 6, 7, 8, 9, 10, DateTimeKind.Utc));
        return c;
    }

    static void Escribir(string ruta, int tamano, Random azar)
    {
        byte[] b = new byte[tamano];
        azar.NextBytes(b);
        File.WriteAllBytes(ruta, b);
    }

    // Huella de todo: ruta, SHA-256, fechas y atributos, más las carpetas.
    static string Huella(string carpeta) { return Huella(carpeta, false); }

    // sinArchivo: en el modo acceso, NTFS le pone el atributo «Archivo» a lo que no lo tenía
    // al cambiarle los permisos (es la marca de «cambió» que usan los respaldos).
    static string Huella(string carpeta, bool sinArchivo)
    {
        StringBuilder s = new StringBuilder();
        using (SHA256 sha = SHA256.Create())
        {
            foreach (string f in Directory.GetFiles(carpeta, "*", SearchOption.AllDirectories).Where(x => !x.Contains("\\" + Cofre.NombreDir + "\\")).OrderBy(x => x, StringComparer.Ordinal))
            {
                FileInfo fi = new FileInfo(f);
                s.AppendLine(string.Format("{0}|{1}|{2}|{3}|{4}", f.Substring(carpeta.Length), BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(f))), fi.CreationTimeUtc.Ticks, fi.LastWriteTimeUtc.Ticks,
                    sinArchivo ? fi.Attributes & ~FileAttributes.Archive : fi.Attributes));
            }
        }
        foreach (string d in Directory.GetDirectories(carpeta, "*", SearchOption.AllDirectories).Where(x => !x.Contains("\\" + Cofre.NombreDir)).OrderBy(x => x, StringComparer.Ordinal))
            s.AppendLine("dir " + d.Substring(carpeta.Length));
        return s.ToString();
    }

    static string Diferencia(string a, string b)
    {
        string[] x = a.Split('\n'), y = b.Split('\n');
        return "faltan [" + string.Join(" ; ", x.Except(y).Take(3)) + "] sobran [" + string.Join(" ; ", y.Except(x).Take(3)) + "]";
    }

    static List<string> FueraDelCofre(string carpeta)
    {
        return Directory.GetFileSystemEntries(carpeta).Where(r => !(Path.GetFileName(r) == Cofre.NombreIni && Cofre.EsNuestroIni(r)))
            .Select(Path.GetFileName).Where(n => n != Cofre.NombreDir && n != Cofre.NombreNota).ToList();
    }

    static void ComprobarBloqueada(string carpeta)
    {
        Cofre c = new Cofre(carpeta);
        Afirmar(c.Estado == Estados.Bloqueada, "estado " + c.Estado);
        Afirmar(c.Cabecera.LlaveConWindows == null, "quedó la llave de Windows");
        List<string> fuera = FueraDelCofre(carpeta);
        Afirmar(fuera.Count == 0, "quedaron afuera: " + string.Join(", ", fuera));
        Afirmar(File.Exists(Path.Combine(carpeta, Cofre.NombreNota)), "falta la nota");
        string ini = Path.Combine(carpeta, Cofre.NombreIni);
        Afirmar(File.Exists(ini) && Cofre.EsNuestroIni(ini), "falta el desktop.ini del ícono");
        Afirmar((new DirectoryInfo(carpeta).Attributes & FileAttributes.ReadOnly) != 0, "la carpeta no quedó de solo lectura (sin eso no se ve el ícono)");
        Afirmar(File.Exists(Path.Combine(carpeta, Cofre.NombreDir, "carpeta.ico")), "falta el ícono");
        foreach (string f in Directory.GetFiles(Path.Combine(carpeta, Cofre.NombreDir), "*", SearchOption.AllDirectories))
        {
            string texto = Encoding.UTF8.GetString(File.ReadAllBytes(f));
            Afirmar(!texto.Contains("MARCADOR-SECRETO") && !texto.Contains("ñandú") && !texto.Contains("tatuaje"), "texto sin cifrar en " + f);
        }
    }

    static CancellationToken Nada { get { return CancellationToken.None; } }

    // ---------------------------------------------------------------- pruebas

    static void ProbarCng()
    {
        byte[] sal = Cripto.Azar(16);
        foreach (string clave in new[] { "contraseña", "x", "una frase larga con espacios y ñ" })
            using (Rfc2898DeriveBytes d = new Rfc2898DeriveBytes(Encoding.UTF8.GetBytes(clave), sal, 1000, HashAlgorithmName.SHA256))
                Afirmar(d.GetBytes(64).SequenceEqual(Cripto.Derivar(clave, sal, 1000)), "PBKDF2 distinto para «" + clave + "»");
        byte[] llave = Cripto.Azar(32), datos = Cripto.Azar(100003);
        foreach (int[] tramo in new[] { new[] { 0, 0 }, new[] { 0, 1 }, new[] { 7, 5000 }, new[] { 3, 100000 } })
            using (HMACSHA256 h = new HMACSHA256(llave))
                Afirmar(h.ComputeHash(datos, tramo[0], tramo[1]).SequenceEqual(Cripto.Hmac(llave, datos, tramo[0], tramo[1])), "HMAC distinto en " + tramo[0] + "+" + tramo[1]);
        Stopwatch r = Stopwatch.StartNew();
        Cripto.Derivar(Clave1, sal, Cripto.IteracionesContrasena);
        Console.WriteLine("      comprobar una contraseña ({0:N0} iteraciones): {1:N2} s", Cripto.IteracionesContrasena, r.Elapsed.TotalSeconds);
    }

    static void ProbarClave()
    {
        string k = Cripto.NuevaClaveRecuperacion();
        Afirmar(k.Length == 39 && k.Split('-').Length == 8, "formato " + k);
        Afirmar(Cripto.NormalizarClave(k) == Cripto.NormalizarClave(" " + k.ToLowerInvariant().Replace("-", " ") + " "), "normalización");
        Afirmar(Cripto.NormalizarClave("O0I1B8") == "OOIIBB", "caracteres parecidos");
        Afirmar(Cripto.NuevaClaveRecuperacion() != k, "repetida");
    }

    static void ProbarIdaVuelta()
    {
        string c = NuevaCarpeta("ida y vuelta");
        string antes = Huella(c);
        FileAttributes atributos = new DirectoryInfo(c).Attributes;
        string clave = Cofre.Crear(c, Clave1, null);
        Afirmar(clave.Length == 39, "clave");
        new Cofre(c).Bloquear(null, Nada, false);
        ComprobarBloqueada(c);
        new Cofre(c).Desbloquear(Clave1, null, Nada);
        Afirmar(new Cofre(c).Estado == Estados.Desbloqueada, "no quedó desbloqueada");
        Afirmar(Huella(c) == antes, "los archivos no volvieron idénticos");
        Afirmar(!File.Exists(Path.Combine(c, Cofre.NombreNota)), "quedó la nota");
        Afirmar(!Directory.Exists(Path.Combine(c, Cofre.NombreDir, "datos")), "quedó la carpeta de datos");
        Afirmar(!File.Exists(Path.Combine(c, Cofre.NombreIni)), "quedó el desktop.ini");
        Afirmar(new DirectoryInfo(c).Attributes == atributos, "los atributos de la carpeta no volvieron: " + new DirectoryInfo(c).Attributes);
        Afirmar(new Cofre(c).Cabecera.AtributosCarpeta == null, "quedaron anotados los atributos");
    }

    static void ProbarAcceso()
    {
        string c = NuevaCarpeta("solo acceso");
        string antes = Huella(c, true);
        FileAttributes atributos = new DirectoryInfo(c).Attributes;
        string clave = Cofre.Crear(c, Clave1, null, true);
        Afirmar(new Cofre(c).SoloAcceso, "no quedó en modo acceso");
        Stopwatch r = Stopwatch.StartNew();
        new Cofre(c).Bloquear(null, Nada, false);   // no pide la contraseña
        double segundos = r.Elapsed.TotalSeconds;
        Cofre k = new Cofre(c);   // la cabecera se sigue leyendo con la carpeta bloqueada
        Afirmar(k.Estado == Estados.Bloqueada, "estado " + k.Estado);
        Afirmar(!Directory.Exists(Path.Combine(c, Cofre.NombreDir, "datos")), "cifró algo");
        Espera<UnauthorizedAccessException>(() => Directory.GetFiles(c), null);
        Espera<UnauthorizedAccessException>(() => File.ReadAllBytes(Path.Combine(c, "archivo 5.bin")), null);
        Espera<UnauthorizedAccessException>(() => File.ReadAllText(Path.Combine(c, "Fotos ñandú", "notas.txt")), null);
        Espera<UnauthorizedAccessException>(() => File.Delete(Path.Combine(c, "archivo 5.bin")), null);
        Espera<UnauthorizedAccessException>(() => File.WriteAllText(Path.Combine(c, "nuevo.txt"), "x"), null);
        Afirmar(Cofre.EsNuestroIni(Path.Combine(c, Cofre.NombreIni)), "el Explorador no podría leer el desktop.ini del ícono");
        Afirmar(k.Tamano() > 5000000, "no recordó el tamaño: " + k.Tamano());
        Espera<ErrorCandado>(() => new Cofre(c).Desbloquear("otra-contraseña", null, Nada), "no es correcta");
        new Cofre(c).Desbloquear(Clave1, null, Nada);
        Afirmar(Huella(c, true) == antes, "los archivos cambiaron: " + Diferencia(antes, Huella(c, true)));
        Afirmar(new DirectoryInfo(c).Attributes == atributos, "los atributos de la carpeta no volvieron");
        Afirmar(!new FileInfo(Path.Combine(c, "archivo 5.bin")).GetAccessControl().GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier))
            .Cast<System.Security.AccessControl.FileSystemAccessRule>().Any(x => x.AccessControlType == System.Security.AccessControl.AccessControlType.Deny), "quedó una negación en un archivo");
        new Cofre(c).Bloquear(null, Nada, false);
        new Cofre(c).Recuperar(clave, "nueva-contraseña", null, Nada);
        Afirmar(Huella(c, true) == antes, "los archivos cambiaron al recuperar");
        new Cofre(c).QuitarProteccion();
        Afirmar(!Cofre.EstaProtegida(c), "sigue protegida");
        Console.WriteLine("      bloquear en modo acceso: {0:N2} s", segundos);
    }

    // Una carpeta con su propio ícono: el suyo se cifra con lo demás y vuelve al desbloquear.
    static void ProbarDesktopIniPropio()
    {
        string c = NuevaCarpeta("con su desktop.ini");
        string ini = Path.Combine(c, Cofre.NombreIni);
        File.WriteAllText(ini, "[.ShellClassInfo]\r\nIconResource=C:\\Windows\\System32\\shell32.dll,4\r\n", Encoding.Unicode);
        File.SetAttributes(ini, FileAttributes.Hidden | FileAttributes.System);
        new DirectoryInfo(c).Attributes |= FileAttributes.ReadOnly;
        FileAttributes atributos = new DirectoryInfo(c).Attributes;
        string antes = Huella(c);
        Cofre.Crear(c, Clave1, null);
        new Cofre(c).Bloquear(null, Nada, false);
        ComprobarBloqueada(c);
        Afirmar(!File.ReadAllText(ini).Contains("shell32"), "quedó el desktop.ini viejo a la vista");
        new Cofre(c).Desbloquear(Clave1, null, Nada);
        Afirmar(Huella(c) == antes, "no volvió idéntica (su desktop.ini incluido)");
        Afirmar(File.ReadAllText(ini).Contains("shell32"), "no volvió su desktop.ini");
        Afirmar(new DirectoryInfo(c).Attributes == atributos, "los atributos de la carpeta no volvieron");
    }

    // Guarda el ícono que Windows muestra para la carpeta, para mirarlo.
    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern IntPtr SHGetFileInfo(string ruta, uint atributos, ref InfoArchivo info, uint tamano, uint banderas);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    struct InfoArchivo
    {
        public IntPtr Icono; public int Indice; public uint Atributos;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 260)] public string Nombre;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 80)] public string Tipo;
    }

    static int GuardarIcono(string carpeta, string png)
    {
        InfoArchivo info = new InfoArchivo();
        SHGetFileInfo(carpeta, 0, ref info, (uint)System.Runtime.InteropServices.Marshal.SizeOf(info), 0x100);   // SHGFI_ICON, grande
        if (info.Icono == IntPtr.Zero) { Console.WriteLine("sin ícono"); return 1; }
        using (System.Drawing.Icon i = System.Drawing.Icon.FromHandle(info.Icono)) i.ToBitmap().Save(png);
        Console.WriteLine("ícono de {0} en {1}", carpeta, png);
        return 0;
    }

    static void ProbarContrasenaMala()
    {
        string c = NuevaCarpeta("contraseña mala");
        Cofre.Crear(c, Clave1, null);
        new Cofre(c).Bloquear(null, Nada, false);
        Espera<ErrorCandado>(() => new Cofre(c).Desbloquear("otra-contraseña", null, Nada), "no es correcta");
        ComprobarBloqueada(c);
    }

    static void ProbarSinContrasena()
    {
        string c = NuevaCarpeta("sin contraseña");
        string antes = Huella(c);
        Cofre.Crear(c, Clave1, null);
        Afirmar(new Cofre(c).PuedeSinContrasena, "debería poder sin contraseña");
        new Cofre(c).Bloquear(null, Nada, false);
        Afirmar(!new Cofre(c).PuedeSinContrasena, "bloqueada no debería");
        Espera<ErrorCandado>(() => new Cofre(c).Bloquear(null, Nada, false), "Hace falta la contraseña");   // bloqueada ya no guarda la llave de Windows
        new Cofre(c).Desbloquear(Clave1, null, Nada);
        new Cofre(c).Bloquear(null, Nada, false);   // de nuevo sin contraseña
        ComprobarBloqueada(c);
        new Cofre(c).Desbloquear(Clave1, null, Nada);
        Afirmar(Huella(c) == antes, "no volvió idéntica");
    }

    static void ProbarRecuperacion()
    {
        string c = NuevaCarpeta("recuperación");
        string antes = Huella(c);
        string clave = Cofre.Crear(c, Clave1, null);
        new Cofre(c).Bloquear(null, Nada, false);
        Espera<ErrorCandado>(() => new Cofre(c).Recuperar(Cripto.NuevaClaveRecuperacion(), "nueva-contraseña", null, Nada), "no corresponde");
        Espera<ErrorCandado>(() => new Cofre(c).Recuperar(clave, "corta", null, Nada), "al menos");
        ComprobarBloqueada(c);
        new Cofre(c).Recuperar(clave.ToLowerInvariant().Replace("-", " "), "nueva-contraseña", null, Nada);
        Afirmar(Huella(c) == antes, "no volvió idéntica");
        new Cofre(c).Bloquear(null, Nada, false);
        Espera<ErrorCandado>(() => new Cofre(c).Desbloquear(Clave1, null, Nada), "no es correcta");
        new Cofre(c).Desbloquear("nueva-contraseña", null, Nada);
        Afirmar(Huella(c) == antes, "no volvió idéntica con la nueva");
    }

    static void ProbarCambio()
    {
        string c = NuevaCarpeta("cambio");
        Cofre.Crear(c, Clave1, null);
        Espera<ErrorCandado>(() => new Cofre(c).CambiarContrasena("mala-mala", "otra-nueva-1"), "no es correcta");
        new Cofre(c).CambiarContrasena(Clave1, "otra-nueva-1");
        new Cofre(c).Bloquear(null, Nada, false);
        Espera<ErrorCandado>(() => new Cofre(c).Desbloquear(Clave1, null, Nada), "no es correcta");
        new Cofre(c).Desbloquear("otra-nueva-1", null, Nada);
    }

    static void ProbarAlterado()
    {
        string c = NuevaCarpeta("alterado");
        string antes = Huella(c);
        Cofre.Crear(c, Clave1, null);
        new Cofre(c).Bloquear(null, Nada, false);
        string[] cifrados = Directory.GetFiles(Path.Combine(c, Cofre.NombreDir, "datos")).OrderByDescending(f => new FileInfo(f).Length).ToArray();
        string victima = cifrados[0];
        byte[] b = File.ReadAllBytes(victima);
        b[b.Length / 2] ^= 0x40;
        File.WriteAllBytes(victima, b);
        Espera<ErrorCandado>(() => new Cofre(c).Desbloquear(Clave1, null, Nada), "dañado");
        Afirmar(new Cofre(c).Estado == Estados.Desbloqueando, "estado tras el daño: " + new Cofre(c).Estado);
        Afirmar(!Directory.GetFiles(c, "*.candado-tmp", SearchOption.AllDirectories).Any(), "quedó un temporal");
        b[b.Length / 2] ^= 0x40;   // se repara y se reanuda
        File.WriteAllBytes(victima, b);
        new Cofre(c).Reanudar((string)null, null, Nada, false);
        Afirmar(new Cofre(c).Estado == Estados.Desbloqueada, "no terminó");
        Afirmar(Huella(c) == antes, "no volvió idéntica");
    }

    static void ProbarAbierto()
    {
        string c = NuevaCarpeta("abierto");
        string antes = Huella(c);
        Cofre.Crear(c, Clave1, null);
        using (new FileStream(Path.Combine(c, "Fotos ñandú", "notas.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Espera<ErrorCandado>(() => new Cofre(c).Bloquear(null, Nada, false), "abierto en otro programa");
        Cofre k = new Cofre(c);
        Afirmar(k.Estado == Estados.Desbloqueada, "estado " + k.Estado);
        Afirmar(!Directory.Exists(Path.Combine(c, Cofre.NombreDir, "datos")) || !Directory.GetFiles(Path.Combine(c, Cofre.NombreDir, "datos")).Any(), "quedaron cifrados");
        Afirmar(Huella(c) == antes, "se tocaron los originales");
        k.Bloquear(null, Nada, false);   // ya cerrado, ahora sí
        ComprobarBloqueada(c);
    }

    static void ProbarFalloBloqueo()
    {
        string c = NuevaCarpeta("fallo");
        string antes = Huella(c);
        Cofre.Crear(c, Clave1, null);
        Cofre.PruebaFallarTras = 5;
        try { Espera<ErrorCandado>(() => new Cofre(c).Bloquear(null, Nada, false), "fallo de prueba"); }
        finally { Cofre.PruebaFallarTras = -1; }
        Afirmar(new Cofre(c).Estado == Estados.Desbloqueada, "no se deshizo");
        Afirmar(Huella(c) == antes, "se tocaron los originales");
    }

    // Lanza este mismo exe para bloquear o desbloquear despacio y lo mata dos veces a la
    // mitad (la segunda, mientras reanuda); después reanuda aquí y compara.
    static void ProbarCorte(string operacion)
    {
        string c = NuevaCarpeta("corte " + operacion);
        string antes = Huella(c);
        Cofre.Crear(c, Clave1, null);
        if (operacion == "desbloquear") new Cofre(c).Bloquear(null, Nada, false);
        for (int corte = 1; corte <= 2; corte++)
        {
            Process p = Process.Start(new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName, "--hijo " + operacion + " \"" + c + "\"") { UseShellExecute = false, CreateNoWindow = true });
            Stopwatch r = Stopwatch.StartNew();
            while (!Interrumpida(c) && !p.HasExited && r.Elapsed.TotalSeconds < 30) Thread.Sleep(30);
            Thread.Sleep(250);
            Afirmar(!p.HasExited, "terminó antes del corte " + corte);
            p.Kill(); p.WaitForExit();
            Afirmar(Interrumpida(c), "tras el corte " + corte + " el estado es " + new Cofre(c).Estado);
        }
        new Cofre(c).Reanudar(Clave1, null, Nada, false);
        if (operacion == "bloquear")
        {
            ComprobarBloqueada(c);
            new Cofre(c).Desbloquear(Clave1, null, Nada);
        }
        Afirmar(new Cofre(c).Estado == Estados.Desbloqueada, "no terminó: " + new Cofre(c).Estado);
        Afirmar(Huella(c) == antes, "no volvió idéntica tras los cortes");
        Afirmar(!Directory.GetFiles(c, "*.candado-tmp", SearchOption.AllDirectories).Any(), "quedaron temporales");
    }

    // El hijo reescribe la cabecera mientras se lee: se reintenta.
    static bool Interrumpida(string c)
    {
        for (int i = 0; i < 20; i++)
        {
            try { return new Cofre(c).Interrumpida; }
            catch (IOException) { Thread.Sleep(20); }
            catch (System.Runtime.Serialization.SerializationException) { Thread.Sleep(20); }
        }
        return false;
    }

    static int Hijo(string[] args)
    {
        Cofre.PruebaPausaMs = 60;
        Cofre k = new Cofre(args[2]);
        if (args[1] == "bloquear") { if (k.Interrumpida) k.Reanudar(Clave1, null, CancellationToken.None, false); else k.Bloquear(null, CancellationToken.None, false); }
        else { if (k.Interrumpida) k.Reanudar(Clave1, null, CancellationToken.None, false); else k.Desbloquear(Clave1, null, CancellationToken.None); }
        return 0;
    }

    static void ProbarSobrescribir()
    {
        string c = NuevaCarpeta("sobrescribir");
        string antes = Huella(c);
        Cofre.Crear(c, Clave1, null);
        new Cofre(c).Bloquear(null, Nada, true);
        ComprobarBloqueada(c);
        new Cofre(c).Desbloquear(Clave1, null, Nada);
        Afirmar(Huella(c) == antes, "no volvió idéntica");
    }

    static void ProbarQuitar()
    {
        string c = NuevaCarpeta("quitar");
        string antes = Huella(c);
        Cofre.Crear(c, Clave1, null);
        new Cofre(c).Bloquear(null, Nada, false);
        Espera<ErrorCandado>(() => new Cofre(c).QuitarProteccion(), "desbloquéala");
        new Cofre(c).Desbloquear(Clave1, null, Nada);
        new Cofre(c).QuitarProteccion();
        Afirmar(!Cofre.EstaProtegida(c), "sigue protegida");
        Afirmar(!Directory.Exists(Path.Combine(c, Cofre.NombreDir)), "quedó .candado");
        Afirmar(Huella(c) == antes, "se tocaron los archivos");
    }

    static void ProbarValidar()
    {
        Espera<ErrorCandado>(() => Cofre.Validar("C:\\", null), "disco entero");
        Espera<ErrorCandado>(() => Cofre.Validar("D:\\", null), "disco entero");
        Afirmar(Cofre.Normalizar("D:\\") == "D:\\" && Cofre.Normalizar("D:\\x\\") == "D:\\x", "Normalizar con raíces");
        Espera<ErrorCandado>(() => Cofre.Validar(Environment.GetFolderPath(Environment.SpecialFolder.Windows), null), "sistema");
        Espera<ErrorCandado>(() => Cofre.Validar(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp"), null), "sistema");
        Espera<ErrorCandado>(() => Cofre.Validar(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), null), "de adentro");
        Espera<ErrorCandado>(() => Cofre.Validar(Path.Combine(trabajo, "no existe"), null), "no existe");
        string a = Path.Combine(trabajo, "anidada"); Directory.CreateDirectory(Path.Combine(a, "hija"));
        Espera<ErrorCandado>(() => Cofre.Validar(Path.Combine(a, "hija"), new[] { a }), "otra carpeta protegida");
        Espera<ErrorCandado>(() => Cofre.Validar(a, new[] { Path.Combine(a, "hija") }), "otra carpeta protegida");
        Espera<ErrorCandado>(() => Cofre.Crear(a, "corta", null), "al menos");
        Afirmar(!Cofre.EstaProtegida(a), "se protegió con contraseña corta");
    }

    static void ProbarCorreo()
    {
        TcpListener l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int puerto = ((IPEndPoint)l.LocalEndpoint).Port;
        string recibido = null;
        Thread servidor = new Thread(() => { recibido = AtenderSmtp(l); });
        servidor.Start();
        Config cfg = new Config { Servidor = "127.0.0.1", Puerto = puerto, Cifrado = false, Usuario = "piero@prueba.local", ContrasenaCerrada = Cripto.CerrarConWindows(Encoding.UTF8.GetBytes("secreto")) };
        string clave = Cripto.NuevaClaveRecuperacion();
        Correo.EnviarClave(cfg, "D:\\Clientes ñandú", clave);
        servidor.Join(10000);
        l.Stop();
        Afirmar(recibido != null, "el servidor no recibió nada");
        string cuerpo = DecodificarQP(recibido.Substring(recibido.IndexOf("\r\n\r\n") + 4));
        if (!cuerpo.Contains(clave)) File.WriteAllText(Path.Combine(trabajo, "correo-recibido.txt"), recibido);
        Afirmar(cuerpo.Contains(clave), "la clave no está en el correo (ver correo-recibido.txt)");
        Afirmar(cuerpo.Contains("D:\\Clientes ñandú"), "falta la carpeta en el correo");
        Afirmar(recibido.Contains("AUTH") || recibido.Contains("cGllcm9AcHJ1ZWJhLmxvY2Fs"), "no se autenticó");
        Espera<ErrorCandado>(() => Correo.Enviar(new Config(), "x", "y"), "configurar");
    }

    // quoted-printable: «=» al final de línea es un corte suave y «=XX» es un byte.
    static string DecodificarQP(string qp)
    {
        qp = qp.Replace("=\r\n", "");
        List<byte> b = new List<byte>();
        for (int i = 0; i < qp.Length; i++)
        {
            if (qp[i] == '=' && i + 2 < qp.Length) { b.Add(Convert.ToByte(qp.Substring(i + 1, 2), 16)); i += 2; }
            else b.AddRange(Encoding.UTF8.GetBytes(qp[i].ToString()));
        }
        return Encoding.UTF8.GetString(b.ToArray());
    }

    static string AtenderSmtp(TcpListener l)
    {
        using (TcpClient c = l.AcceptTcpClient())
        using (NetworkStream s = c.GetStream())
        using (StreamReader r = new StreamReader(s, Encoding.ASCII))
        using (StreamWriter w = new StreamWriter(s, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true })
        {
            StringBuilder todo = new StringBuilder();
            w.WriteLine("220 prueba");
            string linea;
            while ((linea = r.ReadLine()) != null)
            {
                todo.AppendLine(linea);
                string u = linea.ToUpperInvariant();
                if (u.StartsWith("EHLO")) { w.WriteLine("250-prueba"); w.WriteLine("250 AUTH LOGIN"); }
                else if (u.Trim() == "AUTH LOGIN") { w.WriteLine("334 VXNlcm5hbWU6"); todo.AppendLine(r.ReadLine()); w.WriteLine("334 UGFzc3dvcmQ6"); todo.AppendLine(r.ReadLine()); w.WriteLine("235 ok"); }
                else if (u.StartsWith("AUTH LOGIN ")) { w.WriteLine("334 UGFzc3dvcmQ6"); todo.AppendLine(r.ReadLine()); w.WriteLine("235 ok"); }
                else if (u.StartsWith("DATA"))
                {
                    w.WriteLine("354 adelante");
                    while ((linea = r.ReadLine()) != null && linea != ".") todo.AppendLine(linea);
                    w.WriteLine("250 ok");
                }
                else if (u.StartsWith("QUIT")) { w.WriteLine("221 chao"); break; }
                else w.WriteLine("250 ok");
            }
            return todo.ToString();
        }
    }

    static void ProbarVelocidad()
    {
        string c = Path.Combine(trabajo, "velocidad");
        Directory.CreateDirectory(c);
        string grande = Path.Combine(c, "grande.bin");
        using (FileStream f = new FileStream(grande, FileMode.Create))
        {
            byte[] b = new byte[1 << 20]; new Random(1).NextBytes(b);
            for (int i = 0; i < 300; i++) f.Write(b, 0, b.Length);
        }
        string antes = Huella(c);
        Cofre.Crear(c, Clave1, null);
        Stopwatch r = Stopwatch.StartNew();
        new Cofre(c).Bloquear(null, Nada, false);
        double tb = r.Elapsed.TotalSeconds;
        r.Restart();
        new Cofre(c).Desbloquear(Clave1, null, Nada);
        double td = r.Elapsed.TotalSeconds;
        Afirmar(Huella(c) == antes, "no volvió idéntica");
        Console.WriteLine("      bloquear 300 MB: {0:N1} s ({1:N0} MB/s) · desbloquear: {2:N1} s ({3:N0} MB/s, incluye ~1 s de la contraseña)", tb, 300 / tb, td, 300 / td);
    }

    // Datos de muestra para las capturas de la ventana: una carpeta bloqueada, una
    // desbloqueada, una a medias y una que ya no está.
    static int Demo(string datos, string dir)
    {
        trabajo = dir;
        if (Directory.Exists(dir)) BorrarTodo(dir);
        Directory.CreateDirectory(dir);
        Datos.Dir = datos;
        Config cfg = new Config { Usuario = "piero@ejemplo.com", ContrasenaCerrada = Cripto.CerrarConWindows(Encoding.UTF8.GetBytes("x")) };
        string a = NuevaCarpeta("Clientes 2026"), b = NuevaCarpeta("Diseños de tatuajes"), c = NuevaCarpeta("Fotos de sesiones");
        foreach (string x in new[] { a, b, c }) { Cofre.Crear(x, Clave1, null); cfg.Carpetas.Add(x); }
        new Cofre(a).Bloquear(null, Nada, false);
        new Cofre(c).Bloquear(null, Nada, false);
        Cofre.PruebaFallarTras = 4;
        try { new Cofre(c).Desbloquear(Clave1, null, Nada); } catch (ErrorCandado) { }
        Cofre.PruebaFallarTras = -1;
        string d = NuevaCarpeta("Contratos (solo acceso)");
        Cofre.Crear(d, Clave1, null, true);
        new Cofre(d).Bloquear(null, Nada, false);
        cfg.Carpetas.Add(d);
        cfg.Carpetas.Add(Path.Combine(dir, "Contratos viejos"));
        NuevaCarpeta("Para proteger");            // la usa la captura «flujo-proteger»
        NuevaCarpeta("Para proteger (acceso)");   // y esta «flujo-proteger-acceso»
        Datos.Guardar(cfg);
        Console.WriteLine("demo lista en " + dir);
        return 0;
    }

    // Cuánto tarda cada pieza, para saber dónde se va el tiempo.
    static int Medir(string dir)
    {
        Stopwatch r = Stopwatch.StartNew();
        Cripto.Derivar("contraseña", new byte[16], Cripto.IteracionesContrasena);
        Console.WriteLine("PBKDF2 {0:N0} iteraciones: {1:N2} s", Cripto.IteracionesContrasena, r.Elapsed.TotalSeconds);
        byte[] datos = new byte[256 << 20]; new Random(1).NextBytes(datos);
        byte[] llave = Cripto.Azar(64);
        r.Restart();
        using (var aes = System.Security.Cryptography.Aes.Create())
        using (var t = aes.CreateEncryptor(new byte[32], new byte[16])) t.TransformFinalBlock(datos, 0, datos.Length);
        Console.WriteLine("AES-256 en memoria: {0:N0} MB/s ({1})", 256 / r.Elapsed.TotalSeconds, System.Security.Cryptography.Aes.Create().GetType().Name);
        r.Restart();
        Cripto.Hmac(new byte[32], datos, 0, datos.Length);
        Console.WriteLine("HMAC-SHA256 (CNG) en memoria: {0:N0} MB/s", 256 / r.Elapsed.TotalSeconds);
        string a = Path.Combine(dir, "medir-claro.bin"), b = Path.Combine(dir, "medir-cifrado.bin"), c = Path.Combine(dir, "medir-vuelta.bin");
        File.WriteAllBytes(a, datos);
        foreach (string f in new[] { b, c }) if (File.Exists(f)) File.Delete(f);
        r.Restart();
        Cripto.CifrarArchivo(llave, a, b, null, CancellationToken.None);
        Console.WriteLine("Cifrar archivo de 256 MB en {0}: {1:N0} MB/s", dir, 256 / r.Elapsed.TotalSeconds);
        r.Restart();
        Cripto.DescifrarArchivo(llave, b, c, null, CancellationToken.None);
        Console.WriteLine("Descifrar: {0:N0} MB/s", 256 / r.Elapsed.TotalSeconds);
        foreach (string f in new[] { a, b, c }) File.Delete(f);
        return 0;
    }

    // Una prueba que falla a la mitad puede dejar una carpeta en modo acceso: se le saca la
    // negación antes de borrar.
    static void QuitarBloqueos(string d)
    {
        try { Cofre.QuitarNegaciones(d); } catch (Exception) { }
        string[] hijas;
        try { hijas = Directory.GetDirectories(d); } catch (Exception) { return; }
        foreach (string h in hijas) QuitarBloqueos(h);
    }

    static void BorrarTodo(string d)
    {
        QuitarBloqueos(d);
        foreach (string f in Directory.GetFiles(d, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
        foreach (string x in Directory.GetDirectories(d, "*", SearchOption.AllDirectories)) new DirectoryInfo(x).Attributes = FileAttributes.Directory;
        Directory.Delete(d, true);
    }
}
