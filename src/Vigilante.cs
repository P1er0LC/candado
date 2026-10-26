// El proceso de segundo plano (Candado.exe --vigilar, que arranca con Windows): cada medio
// segundo le pasa a Vigia las ventanas del Explorador, abre la ventanita cuando alguien
// entra a una carpeta bloqueada que pide la contraseña cada vez y la vuelve a bloquear
// cuando ya nadie la mira. Sin carpetas así, se cierra solo.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace Candado
{
    public class Vigilante
    {
        readonly Vigia vigia = new Vigia();
        readonly HashSet<string> bloqueando = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, Mini> abiertas = new Dictionary<string, Mini>(StringComparer.OrdinalIgnoreCase);
        readonly string captura, pantalla;
        DateTime ultimaConCarpetas = DateTime.Now;

        public Vigilante(string captura, string pantalla)
        {
            this.captura = captura;
            this.pantalla = pantalla;
        }

        public void Empezar()
        {
            DispatcherTimer reloj = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            reloj.Tick += delegate { Pasada(); };
            reloj.Start();
        }

        void Pasada()
        {
            List<string> carpetas = Carpetas();
            if (carpetas.Count == 0)
            {
                if (abiertas.Count == 0 && bloqueando.Count == 0 && DateTime.Now - ultimaConCarpetas > TimeSpan.FromSeconds(30))
                    Application.Current.Shutdown();
                return;
            }
            ultimaConCarpetas = DateTime.Now;
            List<VentanaExplorador> pedir = new List<VentanaExplorador>();
            List<string> bloquear = new List<string>();
            vigia.Pasada(carpetas, Vigia.Ventanas(), bloqueando.Concat(abiertas.Keys).ToList(), DateTime.Now, pedir, bloquear);
            foreach (VentanaExplorador p in pedir) Pedir(p.Ruta, p.Hwnd);
            foreach (string c in bloquear) Bloquear(c);
        }

        static List<string> Carpetas()
        {
            return Datos.Cargar().Carpetas.Where(c =>
            {
                try { return Cofre.EstaProtegida(c) && new Cofre(c).CadaVez; }
                catch (Exception) { return false; }
            }).ToList();
        }

        void Pedir(string carpeta, IntPtr ventana)
        {
            Mini m;
            if (abiertas.TryGetValue(carpeta, out m)) { m.Traer(); return; }
            bool mia;
            Mutex unica = new Mutex(true, Programa.NombreMini(carpeta), out mia);
            if (!mia) { unica.Dispose(); return; }   // ya la abrió el acceso «Desbloquear con Candado»
            m = new Mini(carpeta, captura, pantalla, ventana);
            abiertas[carpeta] = m;
            m.Window.Closed += delegate { abiertas.Remove(carpeta); unica.ReleaseMutex(); unica.Dispose(); };
            m.Window.Show();
        }

        void Bloquear(string carpeta)
        {
            bloqueando.Add(carpeta);
            bool sobrescribir = Datos.Cargar().SobrescribirOriginales;
            Task.Run(() => new Cofre(carpeta).Bloquear(null, CancellationToken.None, sobrescribir)).ContinueWith(t =>
                Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    bloqueando.Remove(carpeta);
                    if (t.Exception == null) vigia.SePudo(carpeta);
                    else vigia.NoSePudo(carpeta, DateTime.Now);
                })));
        }
    }
}
