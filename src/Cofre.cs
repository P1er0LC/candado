// Una carpeta protegida con Candado.
//
// La carpeta se queda donde está. Adentro, en .candado (oculta), viven cabecera.json, el
// índice cifrado y los archivos cifrados con nombres al azar. Cada carpeta tiene una
// llave de datos de 64 bytes que se guarda cerrada dos veces: con la contraseña y con la
// clave de recuperación que se manda por correo. Mientras está desbloqueada se guarda
// además cerrada con la cuenta de Windows, para volver a bloquearla sin pedir la
// contraseña: en ese estado los archivos ya están a la vista, así que no se pierde nada.
//
// Nada se borra hasta que su versión cifrada está escrita y el índice confirmado, y cada
// paso deja el estado en la cabecera: si se corta la luz, Reanudar termina lo empezado.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;

namespace Candado
{
    [DataContract]
    public class Cabecera
    {
        [DataMember] public int Version = 1;
        [DataMember] public string Estado;               // desbloqueada | bloqueando | bloqueada | desbloqueando
        [DataMember] public string Sal;
        [DataMember] public int Iteraciones;
        [DataMember] public string LlaveConContrasena;
        [DataMember] public string SalRecuperacion;
        [DataMember] public string LlaveConRecuperacion;
        [DataMember] public string LlaveConWindows;       // solo mientras no está bloqueada
        [DataMember] public string Creada;
    }

    [DataContract]
    public class Entrada
    {
        [DataMember] public string Ruta;
        [DataMember] public string Archivo;
        [DataMember] public long Tamano;
        [DataMember] public long Creado;
        [DataMember] public long Modificado;
        [DataMember] public int Atributos;
    }

    [DataContract]
    public class Indice
    {
        [DataMember] public List<Entrada> Archivos = new List<Entrada>();
        [DataMember] public List<string> Carpetas = new List<string>();
    }

    public class Progreso
    {
        public string Accion;
        public int Hechos, Total;
        public long Bytes, TotalBytes;
        public string Actual;
    }

    public static class Estados
    {
        public const string Desbloqueada = "desbloqueada", Bloqueando = "bloqueando", Bloqueada = "bloqueada", Desbloqueando = "desbloqueando";
    }

    public class Cofre
    {
        public const string NombreDir = ".candado";
        public const string NombreNota = "Carpeta protegida con Candado.txt";
        public const int LargoMinimo = 8;

        // Para las pruebas de cortes: pausa por archivo y fallo a propósito tras N archivos.
        public static int PruebaPausaMs = 0;
        public static int PruebaFallarTras = -1;

        public readonly string Carpeta;
        public Cabecera Cabecera { get; private set; }

        string DirCandado { get { return Path.Combine(Carpeta, NombreDir); } }
        string DirDatos { get { return Path.Combine(DirCandado, "datos"); } }
        string RutaCabecera { get { return Path.Combine(DirCandado, "cabecera.json"); } }
        string RutaIndice { get { return Path.Combine(DirCandado, "indice.bin"); } }
        string RutaNota { get { return Path.Combine(Carpeta, NombreNota); } }

        public Cofre(string carpeta)
        {
            Carpeta = Normalizar(carpeta);
            if (!File.Exists(RutaCabecera)) throw new ErrorCandado("Esta carpeta no está protegida con Candado.");
            Cabecera = Json.Leer<Cabecera>(File.ReadAllBytes(RutaCabecera));
        }

        public static string Normalizar(string carpeta)
        {
            return Path.GetFullPath(carpeta).TrimEnd('\\');
        }

        public static bool EstaProtegida(string carpeta)
        {
            return File.Exists(Path.Combine(Normalizar(carpeta), NombreDir, "cabecera.json"));
        }

        public string Estado { get { return Cabecera.Estado; } }
        public bool Interrumpida { get { return Estado == Estados.Bloqueando || Estado == Estados.Desbloqueando; } }

        // ------------------------------------------------------------ crear y validar

        // Revisa que tenga sentido proteger esa carpeta. «otras» son las que ya protege la app.
        public static void Validar(string carpeta, IEnumerable<string> otras)
        {
            string c = Normalizar(carpeta);
            if (!Directory.Exists(c)) throw new ErrorCandado("La carpeta no existe.");
            if (string.Equals(Path.GetPathRoot(c).TrimEnd('\\'), c, StringComparison.OrdinalIgnoreCase))
                throw new ErrorCandado("No se puede proteger un disco entero: elige una carpeta.");
            if (EstaProtegida(c)) throw new ErrorCandado("Esta carpeta ya está protegida.");

            Environment.SpecialFolder[] sistema = {
                Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
                Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData };
            foreach (Environment.SpecialFolder s in sistema)
            {
                string r = Environment.GetFolderPath(s);
                if (r != "" && Dentro(c, r)) throw new ErrorCandado("Es una carpeta del sistema o de programas: protegerla podría romper Windows o una app.");
            }
            string perfil = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string[] personales = {
                perfil, Path.Combine(perfil, "Downloads"),
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop), Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
                Environment.GetFolderPath(Environment.SpecialFolder.MyVideos) };
            foreach (string p in personales)
                if (p != "" && string.Equals(Normalizar(p), c, StringComparison.OrdinalIgnoreCase))
                    throw new ErrorCandado("Protege una carpeta de adentro, no «" + Path.GetFileName(c) + "» entera: hay programas que la usan.");
            foreach (string v in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
            {
                string r = Environment.GetEnvironmentVariable(v);
                if (!string.IsNullOrEmpty(r) && Dentro(c, r)) throw new ErrorCandado("Está dentro de OneDrive: se subiría cada cambio. Usa una carpeta que no se sincronice.");
            }
            if (Dentro(Normalizar(AppDomain.CurrentDomain.BaseDirectory), c)) throw new ErrorCandado("Ahí está la propia app Candado.");
            foreach (string o in otras ?? new string[0])
            {
                string n = Normalizar(o);
                if (Dentro(c, n) || Dentro(n, c)) throw new ErrorCandado("Está dentro de otra carpeta protegida, o tiene una adentro.");
            }
        }

        // ¿a está dentro de b (o es b)?
        static bool Dentro(string a, string b)
        {
            string x = Normalizar(a) + "\\", y = Normalizar(b) + "\\";
            return x.StartsWith(y, StringComparison.OrdinalIgnoreCase);
        }

        // Protege la carpeta (queda desbloqueada) y devuelve la clave de recuperación.
        public static string Crear(string carpeta, string contrasena, IEnumerable<string> otras)
        {
            Validar(carpeta, otras);
            ValidarContrasena(contrasena);
            string c = Normalizar(carpeta);
            Recorrer(c);   // falla antes de tocar nada si hay enlaces o uniones adentro

            byte[] llave = Cripto.Azar(64);
            byte[] sal = Cripto.Azar(16), salR = Cripto.Azar(16);
            string clave = Cripto.NuevaClaveRecuperacion();
            Cabecera cab = new Cabecera
            {
                Estado = Estados.Desbloqueada,
                Sal = Convert.ToBase64String(sal),
                Iteraciones = Cripto.IteracionesContrasena,
                LlaveConContrasena = Convert.ToBase64String(Cripto.Cerrar(Cripto.Derivar(contrasena, sal, Cripto.IteracionesContrasena), llave)),
                SalRecuperacion = Convert.ToBase64String(salR),
                LlaveConRecuperacion = Convert.ToBase64String(Cripto.Cerrar(Cripto.Derivar(Cripto.NormalizarClave(clave), salR, Cripto.IteracionesRecuperacion), llave)),
                LlaveConWindows = Cripto.CerrarConWindows(llave),
                Creada = DateTime.Now.ToString("o")
            };
            DirectoryInfo d = Directory.CreateDirectory(Path.Combine(c, NombreDir));
            d.Attributes |= FileAttributes.Hidden | FileAttributes.System;
            EscribirAtomico(Path.Combine(d.FullName, "cabecera.json"), Json.Escribir(cab));
            return clave;
        }

        public static void ValidarContrasena(string contrasena)
        {
            if (contrasena == null || contrasena.Length < LargoMinimo)
                throw new ErrorCandado("La contraseña tiene que tener al menos " + LargoMinimo + " caracteres.");
        }

        // ------------------------------------------------------------ llaves

        // La interfaz comprueba la contraseña primero (para mostrar el error en el mismo
        // diálogo) y después bloquea o desbloquea con la llave, sin derivarla dos veces.
        public byte[] LlaveConContrasena(string contrasena)
        {
            byte[] kek = Cripto.Derivar(contrasena ?? "", Convert.FromBase64String(Cabecera.Sal), Cabecera.Iteraciones);
            byte[] llave = Cripto.Abrir(kek, Convert.FromBase64String(Cabecera.LlaveConContrasena));
            if (llave == null) throw new ErrorCandado("La contraseña no es correcta.");
            return llave;
        }

        public byte[] LlaveConRecuperacion(string clave)
        {
            byte[] kek = Cripto.Derivar(Cripto.NormalizarClave(clave), Convert.FromBase64String(Cabecera.SalRecuperacion), Cripto.IteracionesRecuperacion);
            byte[] llave = Cripto.Abrir(kek, Convert.FromBase64String(Cabecera.LlaveConRecuperacion));
            if (llave == null) throw new ErrorCandado("La clave de recuperación no corresponde a esta carpeta.");
            return llave;
        }

        // null si no hay (está bloqueada) o la cuenta de Windows no puede abrirla.
        public byte[] LlaveDeWindows()
        {
            return Cripto.AbrirConWindows(Cabecera.LlaveConWindows);
        }

        public bool PuedeSinContrasena { get { return LlaveDeWindows() != null; } }

        public void PonerContrasena(byte[] llave, string nueva)
        {
            ValidarContrasena(nueva);
            byte[] sal = Cripto.Azar(16);
            Cabecera.Sal = Convert.ToBase64String(sal);
            Cabecera.Iteraciones = Cripto.IteracionesContrasena;
            Cabecera.LlaveConContrasena = Convert.ToBase64String(Cripto.Cerrar(Cripto.Derivar(nueva, sal, Cripto.IteracionesContrasena), llave));
            GuardarCabecera();
        }

        public void CambiarContrasena(string actual, string nueva)
        {
            PonerContrasena(LlaveConContrasena(actual), nueva);
        }

        // ------------------------------------------------------------ operaciones públicas

        public void Bloquear(Action<Progreso> avance, CancellationToken cancelar, bool sobrescribir)
        {
            byte[] llave = LlaveDeWindows();
            if (llave == null) throw new ErrorCandado("Hace falta la contraseña para bloquearla.");
            Bloquear(llave, avance, cancelar, sobrescribir);
        }

        public void Bloquear(string contrasena, Action<Progreso> avance, CancellationToken cancelar, bool sobrescribir)
        {
            Bloquear(LlaveConContrasena(contrasena), avance, cancelar, sobrescribir);
        }

        public void Desbloquear(string contrasena, Action<Progreso> avance, CancellationToken cancelar)
        {
            Desbloquear(LlaveConContrasena(contrasena), avance, cancelar);
        }

        // Pone la contraseña nueva y desbloquea.
        public void Recuperar(string clave, string nueva, Action<Progreso> avance, CancellationToken cancelar)
        {
            byte[] llave = LlaveConRecuperacion(clave);
            PonerContrasena(llave, nueva);
            Desbloquear(llave, avance, cancelar);
        }

        // Termina un bloqueo o desbloqueo cortado. Usa la cuenta de Windows o, si no puede, la contraseña.
        public void Reanudar(string contrasena, Action<Progreso> avance, CancellationToken cancelar, bool sobrescribir)
        {
            ReanudarConLlave(LlaveDeWindows() ?? LlaveConContrasena(contrasena), avance, cancelar, sobrescribir);
        }

        public void ReanudarConLlave(byte[] llave, Action<Progreso> avance, CancellationToken cancelar, bool sobrescribir)
        {
            if (Estado == Estados.Bloqueando) Bloquear(llave, avance, cancelar, sobrescribir);
            else if (Estado == Estados.Desbloqueando) Desbloquear(llave, avance, cancelar);
        }

        // Lo que ocupa: los cifrados si está bloqueada, los archivos si no.
        public long Tamano()
        {
            long total = 0;
            if (Estado == Estados.Bloqueada)
            {
                if (Directory.Exists(DirDatos)) foreach (string f in Directory.GetFiles(DirDatos)) total += new FileInfo(f).Length;
                return total;
            }
            List<string> archivos, carpetas;
            try { Recorrer(Carpeta, out archivos, out carpetas); }
            catch (ErrorCandado) { return -1; }
            foreach (string r in archivos) total += new FileInfo(Path.Combine(Carpeta, r)).Length;
            return total;
        }

        // Deja la carpeta como una carpeta normal. Solo estando desbloqueada.
        public void QuitarProteccion()
        {
            if (Estado != Estados.Desbloqueada) throw new ErrorCandado("Primero desbloquéala.");
            new DirectoryInfo(DirCandado).Attributes = FileAttributes.Directory;
            Directory.Delete(DirCandado, true);
        }

        // ------------------------------------------------------------ bloquear

        public void Bloquear(byte[] llave, Action<Progreso> avance, CancellationToken cancelar, bool sobrescribir)
        {
            if (Estado == Estados.Bloqueada) return;
            if (Estado == Estados.Desbloqueando) throw new ErrorCandado("Hay un desbloqueo a medias: termínalo primero.");
            Indice indice;
            if (Estado == Estados.Bloqueando && File.Exists(RutaIndice))
            {
                indice = LeerIndice(llave);
            }
            else
            {
                // Un bloqueo cortado antes de confirmar el índice no sirve: se descarta.
                VaciarDatos();
                Directory.CreateDirectory(DirDatos);
                Cabecera.Estado = Estados.Bloqueando;
                GuardarCabecera();
                try
                {
                    indice = new Indice();
                    List<string> archivos, carpetas;
                    Recorrer(Carpeta, out archivos, out carpetas);
                    indice.Carpetas = carpetas;
                    CifrarArchivos(llave, archivos, indice, avance, cancelar);
                    EscribirIndice(llave, indice);
                }
                catch
                {
                    VaciarDatos();
                    Cabecera.Estado = Estados.Desbloqueada;
                    GuardarCabecera();
                    throw;
                }
            }

            // Desde acá el bloqueo está confirmado: se borran los originales.
            bool cambiado = false;
            Progreso p = new Progreso { Accion = "Borrando los originales", Total = indice.Archivos.Count };
            foreach (Entrada e in indice.Archivos.ToList())
            {
                string ruta = Path.Combine(Carpeta, e.Ruta);
                p.Hechos++; p.Actual = e.Ruta;
                if (avance != null) avance(p);
                if (!File.Exists(ruta)) continue;
                FileInfo fi = new FileInfo(ruta);
                if (fi.Length != e.Tamano || fi.LastWriteTimeUtc.ToFileTimeUtc() != e.Modificado)
                {
                    // Cambió mientras se bloqueaba: se cifra de nuevo para no perder el cambio.
                    string viejo = e.Archivo;
                    Entrada nueva = CifrarUno(llave, e.Ruta, null, cancelar);
                    indice.Archivos[indice.Archivos.IndexOf(e)] = nueva;
                    File.Delete(Path.Combine(DirDatos, viejo));
                    cambiado = true;
                }
                BorrarOriginal(ruta, sobrescribir);
            }
            if (cambiado) EscribirIndice(llave, indice);

            // Lo que apareció mientras tanto también se cifra.
            List<string> nuevos, carpetasNuevas;
            Recorrer(Carpeta, out nuevos, out carpetasNuevas);
            if (nuevos.Count > 0)
            {
                CifrarArchivos(llave, nuevos, indice, avance, cancelar);
                foreach (string c in carpetasNuevas) if (!indice.Carpetas.Contains(c)) indice.Carpetas.Add(c);
                EscribirIndice(llave, indice);
                foreach (string r in nuevos) BorrarOriginal(Path.Combine(Carpeta, r), sobrescribir);
            }
            foreach (string c in indice.Carpetas.OrderByDescending(x => x.Length))
            {
                string d = Path.Combine(Carpeta, c);
                try { if (Directory.Exists(d) && !Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d); }
                catch (IOException) { }
            }

            Cabecera.LlaveConWindows = null;
            Cabecera.Estado = Estados.Bloqueada;
            GuardarCabecera();
            File.WriteAllText(RutaNota, "Esta carpeta está bloqueada con Candado.\r\n\r\nPara ver tus archivos, abre Candado, elige esta carpeta y pon la contraseña.\r\nNo borres la carpeta oculta «.candado»: ahí están tus archivos cifrados.\r\n", Encoding.UTF8);
        }

        void CifrarArchivos(byte[] llave, List<string> archivos, Indice indice, Action<Progreso> avance, CancellationToken cancelar)
        {
            Progreso p = new Progreso { Accion = "Bloqueando", Total = archivos.Count };
            foreach (string r in archivos) p.TotalBytes += new FileInfo(Path.Combine(Carpeta, r)).Length;
            foreach (string r in archivos)
            {
                cancelar.ThrowIfCancellationRequested();
                if (PruebaFallarTras >= 0 && p.Hechos >= PruebaFallarTras) throw new ErrorCandado("fallo de prueba");
                p.Actual = r;
                if (avance != null) avance(p);
                Progreso local = p;
                indice.Archivos.Add(CifrarUno(llave, r, n => { local.Bytes += n; if (avance != null) avance(local); }, cancelar));
                p.Hechos++;
                if (PruebaPausaMs > 0) Thread.Sleep(PruebaPausaMs);
            }
            if (avance != null) avance(p);
        }

        Entrada CifrarUno(byte[] llave, string relativa, Action<long> avance, CancellationToken cancelar)
        {
            string origen = Path.Combine(Carpeta, relativa);
            FileInfo fi = new FileInfo(origen);
            string nombre = Cripto.Hex(Cripto.Azar(16));
            string tmp = Path.Combine(DirDatos, nombre + ".tmp");
            try { Cripto.CifrarArchivo(llave, origen, tmp, avance, cancelar); }
            catch (IOException)
            {
                Borrar(tmp);
                throw new ErrorCandado("No se pudo leer «" + relativa + "»: está abierto en otro programa. Ciérralo y vuelve a intentarlo.");
            }
            catch { Borrar(tmp); throw; }
            File.Move(tmp, Path.Combine(DirDatos, nombre));
            return new Entrada
            {
                Ruta = relativa,
                Archivo = nombre,
                Tamano = fi.Length,
                Creado = fi.CreationTimeUtc.ToFileTimeUtc(),
                Modificado = fi.LastWriteTimeUtc.ToFileTimeUtc(),
                Atributos = (int)fi.Attributes
            };
        }

        static void BorrarOriginal(string ruta, bool sobrescribir)
        {
            File.SetAttributes(ruta, FileAttributes.Normal);
            if (sobrescribir)
            {
                // En un disco mecánico deja el contenido viejo irrecuperable; en un SSD no es garantía.
                using (FileStream f = new FileStream(ruta, FileMode.Open, FileAccess.Write, FileShare.None))
                {
                    byte[] ceros = new byte[1 << 20];
                    long resto = f.Length;
                    while (resto > 0) { int n = (int)Math.Min(ceros.Length, resto); f.Write(ceros, 0, n); resto -= n; }
                    f.Flush(true);
                }
            }
            File.Delete(ruta);
        }

        // ------------------------------------------------------------ desbloquear

        public void Desbloquear(byte[] llave, Action<Progreso> avance, CancellationToken cancelar)
        {
            if (Estado == Estados.Desbloqueada) return;
            if (Estado == Estados.Bloqueando && !File.Exists(RutaIndice))
            {
                // Bloqueo cortado antes de confirmar el índice: los originales siguen todos ahí.
                VaciarDatos();
                Cabecera.Estado = Estados.Desbloqueada;
                GuardarCabecera();
                return;
            }
            // Si el corte fue mientras se borraban los originales, el bucle de abajo
            // reconoce los que siguen intactos y restaura el resto.
            Indice indice = LeerIndice(llave);
            Cabecera.LlaveConWindows = Cripto.CerrarConWindows(llave);
            Cabecera.Estado = Estados.Desbloqueando;
            GuardarCabecera();
            Borrar(RutaNota);
            foreach (string c in indice.Carpetas) Directory.CreateDirectory(Path.Combine(Carpeta, c));

            Progreso p = new Progreso { Accion = "Desbloqueando", Total = indice.Archivos.Count };
            foreach (Entrada e in indice.Archivos) p.TotalBytes += e.Tamano;
            foreach (Entrada e in indice.Archivos)
            {
                cancelar.ThrowIfCancellationRequested();
                if (PruebaFallarTras >= 0 && p.Hechos >= PruebaFallarTras) throw new ErrorCandado("fallo de prueba");
                p.Actual = e.Ruta;
                if (avance != null) avance(p);
                string cifrado = Path.Combine(DirDatos, e.Archivo);
                string destino = Path.Combine(Carpeta, e.Ruta);
                if (!File.Exists(cifrado)) { p.Hechos++; p.Bytes += e.Tamano; continue; }   // ya se restauró antes del corte
                if (File.Exists(destino))
                {
                    FileInfo ya = new FileInfo(destino);
                    if (ya.Length == e.Tamano && ya.LastWriteTimeUtc.ToFileTimeUtc() == e.Modificado)
                    {
                        File.Delete(cifrado); p.Hechos++; p.Bytes += e.Tamano; continue;   // se cortó justo después de restaurarlo
                    }
                    destino = NombreLibre(destino);   // algo nuevo con el mismo nombre: no se pisa
                }
                Directory.CreateDirectory(Path.GetDirectoryName(destino));
                string tmp = destino + ".candado-tmp";
                Borrar(tmp);
                Progreso local = p;
                try { Cripto.DescifrarArchivo(llave, cifrado, tmp, n => { local.Bytes += n; if (avance != null) avance(local); }, cancelar); }
                catch (ErrorCandado)
                {
                    Borrar(tmp);
                    throw new ErrorCandado("«" + e.Ruta + "» está dañado: no se pudo descifrar. Lo demás no se tocó.");
                }
                catch { Borrar(tmp); throw; }
                File.SetCreationTimeUtc(tmp, DateTime.FromFileTimeUtc(e.Creado));
                File.SetLastWriteTimeUtc(tmp, DateTime.FromFileTimeUtc(e.Modificado));
                File.Move(tmp, destino);
                File.SetAttributes(destino, (FileAttributes)e.Atributos);
                File.Delete(cifrado);
                p.Hechos++;
                if (PruebaPausaMs > 0) Thread.Sleep(PruebaPausaMs);
            }
            if (avance != null) avance(p);
            Borrar(RutaIndice);
            VaciarDatos();
            if (Directory.Exists(DirDatos) && !Directory.EnumerateFileSystemEntries(DirDatos).Any()) Directory.Delete(DirDatos);
            Cabecera.Estado = Estados.Desbloqueada;
            GuardarCabecera();
        }

        static string NombreLibre(string ruta)
        {
            string dir = Path.GetDirectoryName(ruta), nombre = Path.GetFileNameWithoutExtension(ruta), ext = Path.GetExtension(ruta);
            for (int i = 1; ; i++)
            {
                string r = Path.Combine(dir, nombre + " (desbloqueado" + (i > 1 ? " " + i : "") + ")" + ext);
                if (!File.Exists(r)) return r;
            }
        }

        // ------------------------------------------------------------ recorrido, índice y cabecera

        static void Recorrer(string carpeta)
        {
            List<string> a, c;
            Recorrer(carpeta, out a, out c);
        }

        static void Recorrer(string carpeta, out List<string> archivos, out List<string> carpetas)
        {
            archivos = new List<string>();
            carpetas = new List<string>();
            Recorrer(carpeta, carpeta, archivos, carpetas);
        }

        static void Recorrer(string raiz, string dir, List<string> archivos, List<string> carpetas)
        {
            foreach (string d in Directory.GetDirectories(dir))
            {
                DirectoryInfo di = new DirectoryInfo(d);
                if (dir == raiz && di.Name == NombreDir) continue;
                if ((di.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new ErrorCandado("«" + Relativa(raiz, d) + "» es un enlace a otra carpeta: Candado no puede protegerlo. Sácalo de la carpeta.");
                carpetas.Add(Relativa(raiz, d));
                Recorrer(raiz, d, archivos, carpetas);
            }
            foreach (string f in Directory.GetFiles(dir))
            {
                if (dir == raiz && Path.GetFileName(f) == NombreNota) continue;
                if ((File.GetAttributes(f) & FileAttributes.ReparsePoint) != 0)
                    throw new ErrorCandado("«" + Relativa(raiz, f) + "» es un enlace a otro archivo: Candado no puede protegerlo. Sácalo de la carpeta.");
                archivos.Add(Relativa(raiz, f));
            }
        }

        static string Relativa(string raiz, string ruta)
        {
            return ruta.Substring(raiz.Length).TrimStart('\\');
        }

        Indice LeerIndice(byte[] llave)
        {
            byte[] datos = Cripto.Abrir(llave, File.ReadAllBytes(RutaIndice));
            if (datos == null) throw new ErrorCandado("El índice de la carpeta está dañado.");
            return Json.Leer<Indice>(datos);
        }

        void EscribirIndice(byte[] llave, Indice indice)
        {
            EscribirAtomico(RutaIndice, Cripto.Cerrar(llave, Json.Escribir(indice)));
        }

        void GuardarCabecera()
        {
            EscribirAtomico(RutaCabecera, Json.Escribir(Cabecera));
        }

        void VaciarDatos()
        {
            if (!Directory.Exists(DirDatos)) return;
            foreach (string f in Directory.GetFiles(DirDatos)) File.Delete(f);
        }

        static void Borrar(string ruta)
        {
            if (File.Exists(ruta)) { File.SetAttributes(ruta, FileAttributes.Normal); File.Delete(ruta); }
        }

        // Escribe a un .tmp, lo baja al disco y lo pone en su lugar de una sola vez.
        static void EscribirAtomico(string ruta, byte[] datos)
        {
            string tmp = ruta + ".tmp";
            using (FileStream f = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                f.Write(datos, 0, datos.Length);
                f.Flush(true);
            }
            if (!File.Exists(ruta)) { File.Move(tmp, ruta); return; }
            try { File.Replace(tmp, ruta, null); }
            catch (IOException) { File.Delete(ruta); File.Move(tmp, ruta); }   // discos que no son NTFS
        }
    }

    public static class Json
    {
        public static T Leer<T>(byte[] datos)
        {
            using (MemoryStream m = new MemoryStream(datos)) return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(m);
        }

        public static byte[] Escribir<T>(T valor)
        {
            using (MemoryStream m = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(T)).WriteObject(m, valor);
                return m.ToArray();
            }
        }
    }
}
