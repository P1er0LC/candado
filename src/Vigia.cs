// Lo que decide el vigilante de las carpetas que piden la contraseña cada vez: mira qué
// carpeta muestra cada ventana del Explorador y avisa cuándo pedir la contraseña (alguien
// entró a una bloqueada) y cuándo volver a bloquear (ninguna ventana muestra una
// desbloqueada desde hace unos segundos). No depende de WPF, para poder probarlo; la
// ventanita y los bloqueos los pone Vigilante.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Candado
{
    public struct VentanaExplorador
    {
        public IntPtr Hwnd;
        public string Ruta;
    }

    public class Vigia
    {
        // Sin ventanas que la muestren durante Espera, se bloquea; si no se pudo (un archivo
        // abierto en otro programa), se reintenta pasado Reintento.
        public static readonly TimeSpan Espera = TimeSpan.FromSeconds(5), Reintento = TimeSpan.FromSeconds(15);

        List<VentanaExplorador> antes = new List<VentanaExplorador>();
        readonly Dictionary<string, DateTime> sola = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, DateTime> noAntes = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        // carpetas: las protegidas que piden la contraseña cada vez. ocupadas: las que tienen la
        // ventanita abierta o se están bloqueando. Deja en «pedir» las carpetas en las que alguien
        // acaba de entrar, con su ventana, y en «bloquear» las que hay que volver a bloquear.
        public void Pasada(IEnumerable<string> carpetas, IList<VentanaExplorador> ventanas, ICollection<string> ocupadas, DateTime ahora,
                           List<VentanaExplorador> pedir, List<string> bloquear)
        {
            foreach (string c in carpetas)
            {
                string estado;
                try { estado = new Cofre(c).Estado; }
                catch (Exception) { continue; }
                List<VentanaExplorador> la = ventanas.Where(v => Dentro(v.Ruta, c)).ToList();
                if (ocupadas.Contains(c) || (estado != Estados.Bloqueada && estado != Estados.Desbloqueada)) { sola.Remove(c); continue; }
                if (estado == Estados.Bloqueada)
                {
                    sola.Remove(c);
                    foreach (VentanaExplorador v in la)
                        if (!antes.Any(a => a.Hwnd == v.Hwnd && Dentro(a.Ruta, c)))
                        {
                            pedir.Add(new VentanaExplorador { Hwnd = v.Hwnd, Ruta = c });
                            break;
                        }
                }
                else if (la.Count > 0) sola.Remove(c);
                else
                {
                    DateTime desde, espera;
                    if (!sola.TryGetValue(c, out desde)) sola[c] = ahora;
                    else if (ahora - desde >= Espera && (!noAntes.TryGetValue(c, out espera) || ahora >= espera)) bloquear.Add(c);
                }
            }
            antes = ventanas.ToList();
        }

        public void SePudo(string carpeta) { sola.Remove(carpeta); noAntes.Remove(carpeta); }

        public void NoSePudo(string carpeta, DateTime ahora) { noAntes[carpeta] = ahora + Reintento; }

        public static bool Dentro(string ruta, string carpeta)
        {
            string c = carpeta.TrimEnd('\\');
            return string.Equals(ruta.TrimEnd('\\'), c, StringComparison.OrdinalIgnoreCase) || ruta.StartsWith(c + "\\", StringComparison.OrdinalIgnoreCase);
        }

        // ------------------------------------------------------------ ventanas del Explorador

        // Las ventanas del Explorador abiertas y la carpeta del disco que muestra cada una
        // («Este equipo» y las demás que no son carpetas no cuentan).
        public static List<VentanaExplorador> Ventanas()
        {
            List<VentanaExplorador> lista = new List<VentanaExplorador>();
            CadaVentana((w, ruta) => lista.Add(new VentanaExplorador { Hwnd = new IntPtr(Convert.ToInt64(Leer(w, "HWND"))), Ruta = ruta }));
            return lista;
        }

        // Que las que muestran la carpeta la vuelvan a leer, sin esperar a que el Explorador se dé cuenta.
        public static void Refrescar(string carpeta)
        {
            CadaVentana((w, ruta) => { if (Dentro(ruta, carpeta)) Llamar(w, "Refresh"); });
        }

        // Para las pruebas: cierra las que la muestran.
        public static void Cerrar(string carpeta)
        {
            CadaVentana((w, ruta) => { if (Dentro(ruta, carpeta)) Llamar(w, "Quit"); });
        }

        static void CadaVentana(Action<object, string> hacer)
        {
            object shell = null, ventanas = null;
            try
            {
                shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application"));
                ventanas = Llamar(shell, "Windows");
                int n = Convert.ToInt32(Leer(ventanas, "Count"));
                for (int i = 0; i < n; i++)
                {
                    object w = null;
                    try
                    {
                        w = Llamar(ventanas, "Item", i);
                        if (w == null) continue;
                        string exe = Leer(w, "FullName") as string;
                        if (exe == null || !string.Equals(Path.GetFileName(exe), "explorer.exe", StringComparison.OrdinalIgnoreCase)) continue;
                        string ruta = RutaDe(w);
                        if (ruta != null) hacer(w, ruta);
                    }
                    catch (Exception) { }
                    finally { Soltar(w); }
                }
            }
            catch (Exception) { }
            finally { Soltar(ventanas); Soltar(shell); }
        }

        static string RutaDe(object ventana)
        {
            object doc = null, carpeta = null, item = null;
            try
            {
                doc = Leer(ventana, "Document");
                carpeta = Leer(doc, "Folder");
                item = Leer(carpeta, "Self");
                string ruta = Leer(item, "Path") as string;
                return ruta != null && (ruta.Length > 2 && ruta[1] == ':' || ruta.StartsWith(@"\\")) ? ruta : null;
            }
            catch (Exception) { return null; }
            finally { Soltar(item); Soltar(carpeta); Soltar(doc); }
        }

        static object Leer(object o, string propiedad) { return o.GetType().InvokeMember(propiedad, BindingFlags.GetProperty, null, o, null); }

        static object Llamar(object o, string metodo, params object[] args) { return o.GetType().InvokeMember(metodo, BindingFlags.InvokeMethod, null, o, args); }

        static void Soltar(object o) { if (o != null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o); }
    }
}
