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
//
// En el modo «solo acceso» no se cifra nada: bloquear mueve lo de la carpeta a
// .candado\contenido y ahí pone un permiso de Windows que le niega a todos listar, abrir,
// escribir y borrar. La llave y la clave de recuperación siguen igual, como prueba de la
// contraseña.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.AccessControl;
using System.Security.Principal;
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
        [DataMember(EmitDefaultValue = false)] public int? AtributosCarpeta;   // los de antes de ponerle el ícono
        [DataMember(EmitDefaultValue = false)] public string Modo;             // null o «cifrado», o «acceso»
        [DataMember(EmitDefaultValue = false)] public long? TamanoBloqueada;   // modo acceso: lo que pesaba al bloquearla
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
        public const string NombreAcceso = "Desbloquear con Candado.lnk";
        public const string NombreIni = "desktop.ini";

        // La app que pone el acceso «Desbloquear con Candado» (sin ella, no se pone).
        public static string RutaApp;
        const string MarcaIni = "; Candado: ícono de carpeta bloqueada";
        public const int LargoMinimo = 8;

        // El ícono de carpeta con candado que se pone mientras está bloqueada. Lo carga la app
        // desde sus recursos; sin él, la carpeta se bloquea igual pero con su ícono de siempre.
        public static byte[] IconoCarpeta;

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
        string DirContenido { get { return Path.Combine(DirCandado, "contenido"); } }

        public Cofre(string carpeta)
        {
            Carpeta = Normalizar(carpeta);
            if (!File.Exists(RutaCabecera)) throw new ErrorCandado("Esta carpeta no está protegida con Candado.");
            Cabecera = Json.Leer<Cabecera>(File.ReadAllBytes(RutaCabecera));
        }

        // Sin la barra final, salvo en la raíz de un disco: «D:» a secas es la carpeta actual de D:.
        public static string Normalizar(string carpeta)
        {
            string c = Path.GetFullPath(carpeta);
            string raiz = Path.GetPathRoot(c);
            return c.Length > raiz.Length ? c.TrimEnd('\\') : raiz;
        }

        public static bool EstaProtegida(string carpeta)
        {
            return File.Exists(Path.Combine(Normalizar(carpeta), NombreDir, "cabecera.json"));
        }

        // La carpeta protegida que corresponde a «carpeta»: ella misma, o la de la lista que la
        // contiene. Null si ninguna.
        public static string ProtegidaDe(string carpeta, IEnumerable<string> lista)
        {
            string c = Normalizar(carpeta);
            if (EstaProtegida(c)) return c;
            string madre = lista.FirstOrDefault(p => (c.TrimEnd('\\') + "\\").StartsWith(Normalizar(p).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) && EstaProtegida(p));
            return madre == null ? null : Normalizar(madre);
        }

        public const string ModoCifrado = "cifrado", ModoAcceso = "acceso";
        public bool SoloAcceso { get { return Cabecera.Modo == ModoAcceso; } }

        public string Estado { get { return Cabecera.Estado; } }
        public bool Interrumpida { get { return Estado == Estados.Bloqueando || Estado == Estados.Desbloqueando; } }

        // ------------------------------------------------------------ crear y validar

        // Revisa que tenga sentido proteger esa carpeta. «otras» son las que ya protege la app.
        public static void Validar(string carpeta, IEnumerable<string> otras)
        {
            string c = Normalizar(carpeta);
            if (!Directory.Exists(c)) throw new ErrorCandado("La carpeta no existe.");
            if (string.Equals(Path.GetPathRoot(c), c, StringComparison.OrdinalIgnoreCase))
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
            string x = Normalizar(a).TrimEnd('\\') + "\\", y = Normalizar(b).TrimEnd('\\') + "\\";
            return x.StartsWith(y, StringComparison.OrdinalIgnoreCase);
        }

        // Protege la carpeta (queda desbloqueada) y devuelve la clave de recuperación.
        public static string Crear(string carpeta, string contrasena, IEnumerable<string> otras, bool soloAcceso = false)
        {
            Validar(carpeta, otras);
            ValidarContrasena(contrasena);
            string c = Normalizar(carpeta);
            if (soloAcceso) ComprobarDueno(c);
            else Recorrer(c);   // falla antes de tocar nada si hay enlaces o uniones adentro

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
                Creada = DateTime.Now.ToString("o"),
                Modo = soloAcceso ? ModoAcceso : ModoCifrado
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

        public bool PuedeSinContrasena { get { return (SoloAcceso && Estado != Estados.Bloqueada) || LlaveDeWindows() != null; } }

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
            if (SoloAcceso) { if (Estado != Estados.Bloqueada) BloquearAcceso(avance); return; }   // no necesita la llave
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
            if (SoloAcceso)
            {
                if (Estado == Estados.Bloqueando) BloquearAcceso(avance);
                else if (Estado == Estados.Desbloqueando) DesbloquearAcceso(llave, avance);
                return;
            }
            if (Estado == Estados.Bloqueando) Bloquear(llave, avance, cancelar, sobrescribir);
            else if (Estado == Estados.Desbloqueando) Desbloquear(llave, avance, cancelar);
        }

        // Lo que ocupa: los cifrados si está bloqueada, los archivos si no.
        public long Tamano()
        {
            long total = 0;
            if (SoloAcceso && Estado != Estados.Desbloqueada) return Cabecera.TamanoBloqueada ?? -1;   // bloqueada no se puede recorrer
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
            if (SoloAcceso) QuitarNegaciones(Carpeta);
            QuitarExtras();
            new DirectoryInfo(DirCandado).Attributes = FileAttributes.Directory;
            Directory.Delete(DirCandado, true);
        }

        // ------------------------------------------------------------ bloquear

        public void Bloquear(byte[] llave, Action<Progreso> avance, CancellationToken cancelar, bool sobrescribir)
        {
            if (Estado == Estados.Bloqueada) return;
            if (Estado == Estados.Desbloqueando) throw new ErrorCandado("Hay un desbloqueo a medias: termínalo primero.");
            if (SoloAcceso) { BloquearAcceso(avance); return; }
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

            PonerExtras();
            Cabecera.LlaveConWindows = null;
            Cabecera.Estado = Estados.Bloqueada;
            GuardarCabecera();
            AvisarExplorador(Carpeta);
        }

        // ------------------------------------------------------------ lo que se ve de una carpeta bloqueada

        // Al abrir una carpeta bloqueada se ve una nota y el acceso «Desbloquear con Candado»,
        // que abre la ventanita de la contraseña; por fuera, el ícono con el candado.
        void PonerExtras()
        {
            PonerIcono();
            PonerAcceso();
            File.WriteAllText(RutaNota, "Esta carpeta está bloqueada con Candado.\r\n\r\n" +
                "Para ver tus archivos, abre «Desbloquear con Candado» (aquí mismo) y pon la contraseña.\r\n" +
                "No borres la carpeta oculta «.candado»: ahí están tus archivos.\r\n", Encoding.UTF8);
        }

        void QuitarExtras()
        {
            Borrar(RutaNota);
            Borrar(Path.Combine(Carpeta, NombreAcceso));
            QuitarIcono();
        }

        // El acceso apunta a la app que bloqueó la carpeta (Programa la anota en RutaApp).
        void PonerAcceso()
        {
            if (RutaApp == null) return;
            try
            {
                Type tipo = Type.GetTypeFromProgID("WScript.Shell");
                object shell = Activator.CreateInstance(tipo);
                object lnk = tipo.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { Path.Combine(Carpeta, NombreAcceso) });
                Type t = lnk.GetType();
                t.InvokeMember("TargetPath", BindingFlags.SetProperty, null, lnk, new object[] { RutaApp });
                t.InvokeMember("Arguments", BindingFlags.SetProperty, null, lnk, new object[] { "--carpeta \"" + Carpeta + "\"" });
                t.InvokeMember("IconLocation", BindingFlags.SetProperty, null, lnk, new object[] { RutaApp + ",0" });
                t.InvokeMember("Description", BindingFlags.SetProperty, null, lnk, new object[] { "Desbloquear esta carpeta con Candado" });
                t.InvokeMember("Save", BindingFlags.InvokeMethod, null, lnk, null);
            }
            catch (Exception) { }   // como el ícono: sin él, queda el clic derecho
        }

        // ------------------------------------------------------------ ícono de carpeta bloqueada

        // El Explorador muestra el ícono de un desktop.ini si la carpeta tiene el atributo de
        // solo lectura. Si la carpeta ya tenía su propio desktop.ini, ese se cifró con todo lo
        // demás y vuelve al desbloquearla.
        void PonerIcono()
        {
            if (IconoCarpeta == null) return;
            try
            {
                string ini = Path.Combine(Carpeta, NombreIni), icono = Path.Combine(DirCandado, "carpeta.ico");
                if (File.Exists(ini) && !EsNuestroIni(ini)) return;
                Borrar(ini);
                File.WriteAllBytes(icono, IconoCarpeta);
                File.WriteAllText(ini, MarcaIni + "\r\n[.ShellClassInfo]\r\nIconResource=" + icono + ",0\r\nInfoTip=Bloqueada con Candado\r\n", Encoding.Unicode);
                File.SetAttributes(ini, FileAttributes.Hidden | FileAttributes.System);
                DirectoryInfo d = new DirectoryInfo(Carpeta);
                if (Cabecera.AtributosCarpeta == null) { Cabecera.AtributosCarpeta = (int)d.Attributes; GuardarCabecera(); }
                d.Attributes |= FileAttributes.ReadOnly;
                AvisarExplorador(Carpeta);
            }
            catch (Exception) { }   // el ícono es un extra: si falla, la carpeta queda bloqueada igual
        }

        void QuitarIcono()
        {
            string ini = Path.Combine(Carpeta, NombreIni);
            if (File.Exists(ini) && EsNuestroIni(ini)) Borrar(ini);
            Borrar(Path.Combine(DirCandado, "carpeta.ico"));
            if (Cabecera.AtributosCarpeta != null)
            {
                new DirectoryInfo(Carpeta).Attributes = (FileAttributes)Cabecera.AtributosCarpeta.Value;
                Cabecera.AtributosCarpeta = null;
            }
            AvisarExplorador(Carpeta);
        }

        public static bool EsNuestroIni(string ruta)
        {
            try { return File.ReadAllText(ruta).StartsWith(MarcaIni); }
            catch (Exception) { return false; }
        }

        [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern void SHChangeNotify(int evento, int banderas, string a, IntPtr b);

        // ------------------------------------------------------------ modo «solo acceso»

        static readonly SecurityIdentifier Todos = new SecurityIdentifier(WellKnownSidType.WorldSid, null);

        // Lo que se le niega a todos (la cuenta del usuario incluida) en la carpeta y en todo lo
        // de adentro. No se niega leer los permisos ni cambiarlos: así Candado puede sacar la
        // regla al desbloquear, y también se puede sacar a mano desde Propiedades → Seguridad.
        const FileSystemRights Negados = FileSystemRights.ListDirectory | FileSystemRights.Traverse | FileSystemRights.ReadExtendedAttributes
            | FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories | FileSystemRights.WriteExtendedAttributes
            | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.Delete;

        // Para poder sacar la regla después hay que ser dueño de la carpeta: si no, se intenta serlo.
        static void ComprobarDueno(string carpeta)
        {
            SecurityIdentifier yo = WindowsIdentity.GetCurrent().User;
            DirectorySecurity s = Directory.GetAccessControl(carpeta, AccessControlSections.Owner);
            if (yo.Equals(s.GetOwner(typeof(SecurityIdentifier)))) return;
            try { s.SetOwner(yo); Directory.SetAccessControl(carpeta, s); }
            catch (Exception)
            {
                throw new ErrorCandado("Tu cuenta de Windows no es la dueña de esta carpeta, así que Candado no podría volver a abrirla en el modo «solo acceso». Usa el cifrado.");
            }
        }

        // Lo de la carpeta se mueve a .candado\contenido (en el mismo disco es solo cambiarle el
        // nombre: instantáneo y sin tocar los archivos) y ahí se niega el acceso. La carpeta en
        // sí queda abierta, para que al entrar se vea el acceso «Desbloquear con Candado».
        void BloquearAcceso(Action<Progreso> avance)
        {
            Progreso p = new Progreso { Accion = "Bloqueando", Total = 1, Actual = "" };
            if (avance != null) avance(p);
            ComprobarDueno(Carpeta);
            Cabecera.Estado = Estados.Bloqueando;
            if (Cabecera.TamanoBloqueada == null)
            {
                long total = 0;
                try { foreach (string f in Directory.EnumerateFiles(Carpeta, "*", SearchOption.AllDirectories)) if (!f.StartsWith(DirCandado + "\\")) total += new FileInfo(f).Length; }
                catch (Exception) { total = -1; }
                Cabecera.TamanoBloqueada = total;
            }
            GuardarCabecera();
            if (Directory.Exists(DirContenido)) QuitarNegaciones(DirContenido);   // un bloqueo cortado a medias
            else Directory.CreateDirectory(DirContenido);
            List<string> movidas = new List<string>();
            foreach (string e in EntradasDeLaCarpeta())
            {
                string destino = Path.Combine(DirContenido, Path.GetFileName(e));
                if (Existe(destino)) destino = NombreLibre(destino);
                try { Mover(e, destino); }
                catch (Exception ex)
                {
                    if (!(ex is IOException) && !(ex is UnauthorizedAccessException)) throw;
                    foreach (string m in movidas) Mover(m, Path.Combine(Carpeta, Path.GetFileName(m)));
                    if (!Directory.EnumerateFileSystemEntries(DirContenido).Any()) Directory.Delete(DirContenido);
                    Cabecera.TamanoBloqueada = null;
                    Cabecera.Estado = Estados.Desbloqueada;
                    GuardarCabecera();
                    throw new ErrorCandado("No se pudo bloquear: «" + Path.GetFileName(e) + "» está abierto en otro programa. Ciérralo y vuelve a intentarlo.");
                }
                movidas.Add(destino);
            }
            DirectorySecurity s = Directory.GetAccessControl(DirContenido);
            s.AddAccessRule(new FileSystemAccessRule(Todos, Negados, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Deny));
            // Windows la propaga a todo lo de adentro. No toca contenidos ni fechas, pero NTFS le
            // pone el atributo «Archivo» a lo que no lo tenía (marca de cambio para respaldos).
            Directory.SetAccessControl(DirContenido, s);
            PonerExtras();
            Cabecera.LlaveConWindows = null;
            Cabecera.Estado = Estados.Bloqueada;
            GuardarCabecera();
            p.Hechos = 1;
            if (avance != null) avance(p);
            AvisarExplorador(Carpeta);
        }

        void DesbloquearAcceso(byte[] llave, Action<Progreso> avance)
        {
            Progreso p = new Progreso { Accion = "Desbloqueando", Total = 1, Actual = "" };
            if (avance != null) avance(p);
            Cabecera.LlaveConWindows = Cripto.CerrarConWindows(llave);
            Cabecera.Estado = Estados.Desbloqueando;
            GuardarCabecera();
            QuitarNegaciones(Carpeta);   // las bloqueadas con la primera versión negaban la carpeta entera
            QuitarExtras();              // antes de devolver todo: puede traer su propio desktop.ini
            if (Directory.Exists(DirContenido))
            {
                QuitarNegaciones(DirContenido);
                foreach (string e in Directory.GetFileSystemEntries(DirContenido))
                {
                    string destino = Path.Combine(Carpeta, Path.GetFileName(e));
                    if (Existe(destino)) destino = NombreLibre(destino);
                    Mover(e, destino);
                }
                Directory.Delete(DirContenido);
            }
            Cabecera.TamanoBloqueada = null;
            Cabecera.Estado = Estados.Desbloqueada;
            GuardarCabecera();
            p.Hechos = 1;
            if (avance != null) avance(p);
            AvisarExplorador(Carpeta);
        }

        // Lo de la raíz de la carpeta que no es de Candado.
        List<string> EntradasDeLaCarpeta()
        {
            return Directory.GetFileSystemEntries(Carpeta).Where(e =>
            {
                string n = Path.GetFileName(e);
                return n != NombreDir && n != NombreNota && n != NombreAcceso && !(string.Equals(n, NombreIni, StringComparison.OrdinalIgnoreCase) && EsNuestroIni(e));
            }).ToList();
        }

        static bool Existe(string ruta) { return File.Exists(ruta) || Directory.Exists(ruta); }

        static void Mover(string origen, string destino)
        {
            if (Directory.Exists(origen)) Directory.Move(origen, destino);
            else File.Move(origen, destino);
        }

        // Saca las reglas que niegan a «Todos» puestas directamente en la carpeta; las copias
        // heredadas de adentro se van solas.
        public static void QuitarNegaciones(string carpeta)
        {
            DirectorySecurity s = Directory.GetAccessControl(carpeta);
            foreach (FileSystemAccessRule r in s.GetAccessRules(true, false, typeof(SecurityIdentifier)))
                if (r.AccessControlType == AccessControlType.Deny && Todos.Equals(r.IdentityReference)) s.RemoveAccessRuleSpecific(r);
            Directory.SetAccessControl(carpeta, s);
        }

        // Que el Explorador vuelva a leer el ícono sin esperar a que el usuario pulse F5.
        static void AvisarExplorador(string carpeta)
        {
            try
            {
                SHChangeNotify(0x00002000, 0x0005, carpeta, IntPtr.Zero);   // SHCNE_UPDATEITEM, SHCNF_PATHW
                SHChangeNotify(0x00001000, 0x0005, carpeta, IntPtr.Zero);   // SHCNE_UPDATEDIR: si está abierta, que se vea lo nuevo
                string padre = Path.GetDirectoryName(carpeta);
                if (padre != null) SHChangeNotify(0x00001000, 0x0005, padre, IntPtr.Zero);   // SHCNE_UPDATEDIR
            }
            catch (Exception) { }
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
            if (SoloAcceso) { DesbloquearAcceso(llave, avance); return; }
            if (Estado == Estados.Bloqueando && !File.Exists(RutaIndice))
            {
                // Bloqueo cortado antes de confirmar el índice: los originales siguen todos ahí.
                VaciarDatos();
                QuitarExtras();
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
            QuitarExtras();   // antes de restaurar: la carpeta puede traer su propio desktop.ini
            GuardarCabecera();
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
            AvisarExplorador(Carpeta);
        }

        static string NombreLibre(string ruta)
        {
            string dir = Path.GetDirectoryName(ruta), nombre = Path.GetFileNameWithoutExtension(ruta), ext = Path.GetExtension(ruta);
            for (int i = 1; ; i++)
            {
                string r = Path.Combine(dir, nombre + " (desbloqueado" + (i > 1 ? " " + i : "") + ")" + ext);
                if (!Existe(r)) return r;
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
                if (dir == raiz && (Path.GetFileName(f) == NombreNota || Path.GetFileName(f) == NombreAcceso)) continue;
                if (dir == raiz && string.Equals(Path.GetFileName(f), NombreIni, StringComparison.OrdinalIgnoreCase) && EsNuestroIni(f)) continue;
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
