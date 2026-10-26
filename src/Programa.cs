// Punto de entrada. --carpeta <ruta> es lo que mandan el clic derecho del Explorador y el
// acceso «Desbloquear con Candado»: si Candado ya está abierta, se la pasa a esa ventana por
// una tubería con nombre y termina; si no, una carpeta protegida abre la ventanita (Mini) y
// una sin proteger, la ventana completa para protegerla. --vigilar es el vigilante de las
// carpetas que piden la contraseña cada vez (arranca con Windows; ver Vigilante).
// Solo para probarla: --datos <carpeta> (en vez de %APPDATA%\Candado), --instancia <nombre>
// (otra ventana única, para probar el paso de carpetas) y --captura <png> --pantalla <nombre>
// (dibuja la ventana fuera de la pantalla, guarda la imagen y cierra; las pantallas «mini…»
// son de la ventanita y necesitan --carpeta).
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows;

namespace Candado
{
    static class Programa
    {
        [DllImport("user32.dll")]
        static extern bool AllowSetForegroundWindow(int proceso);

        static string instancia;

        [STAThread]
        static int Main(string[] args)
        {
            string captura = null, pantalla = "lista", carpeta = null;
            bool vigilar = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--vigilar") vigilar = true;
                else if (args[i] == "--datos" && i + 1 < args.Length) Datos.Dir = args[++i];
                else if (args[i] == "--captura" && i + 1 < args.Length) captura = args[++i];
                else if (args[i] == "--pantalla" && i + 1 < args.Length) pantalla = args[++i];
                else if (args[i] == "--carpeta" && i + 1 < args.Length)
                {
                    // En la raíz de un disco el Explorador manda «"D:\"», que llega como «D:"»:
                    // «D:» solo sería la carpeta actual de ese disco, así que se vuelve «D:\».
                    carpeta = args[++i].Trim().Trim('"').Trim();
                    if (carpeta.EndsWith(":")) carpeta += "\\";
                }
                else if (args[i] == "--instancia" && i + 1 < args.Length) instancia = args[++i];
            }
            if (instancia == null) instancia = captura == null ? "Candado" : "Candado-" + Guid.NewGuid();
            string tuberia = instancia + "-" + Environment.UserName;
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("CarpetaBloqueada.ico"))
            using (MemoryStream m = new MemoryStream()) { s.CopyTo(m); Cofre.IconoCarpeta = m.ToArray(); }
            Cofre.RutaApp = Assembly.GetExecutingAssembly().Location;

            if (vigilar)
            {
                bool primerVigilante;
                using (Mutex unico = new Mutex(true, NombreVigilante(), out primerVigilante))
                {
                    if (!primerVigilante) return 0;
                    Application app = Nueva(false);
                    app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                    new Vigilante(captura, pantalla).Empezar();
                    return app.Run();
                }
            }

            string protegida = null;
            if (carpeta != null && (captura == null || pantalla.StartsWith("mini")))
            {
                try { protegida = Cofre.ProtegidaDe(carpeta, Datos.Cargar().Carpetas); } catch (Exception) { }
            }
            if (protegida != null && (captura != null || !Existe(@"Local\" + instancia)))
            {
                // Una sola ventanita por carpeta, aunque le den doble clic dos veces.
                bool primeraMini;
                using (Mutex unica = new Mutex(true, NombreMini(protegida), out primeraMini))
                {
                    if (!primeraMini) return 0;
                    if (captura == null) Datos.Agregar(Datos.Cargar(), protegida);
                    return Nueva(true).Run(new Mini(protegida, captura, pantalla).Window);
                }
            }

            bool primera;
            using (Mutex unica = new Mutex(true, @"Local\" + instancia, out primera))
            {
                if (!primera)
                {
                    if (carpeta != null && Pasar(tuberia, carpeta)) return 0;
                    MessageBox.Show("Candado ya está abierto.", "Candado", MessageBoxButton.OK, MessageBoxImage.Information);
                    return 0;
                }
                Application app = Nueva(true);
                Ventana v = new Ventana(captura, pantalla, carpeta);
                Escuchar(tuberia, v);
                return app.Run(v.Window);
            }
        }

        // avisar: el vigilante no muestra los errores (corre en segundo plano y vuelve a intentar).
        static Application Nueva(bool avisar)
        {
            Application app = new Application();
            app.DispatcherUnhandledException += (s, e) =>
            {
                if (avisar) MessageBox.Show("Algo falló: " + e.Exception.Message, "Candado", MessageBoxButton.OK, MessageBoxImage.Error);
                e.Handled = true;
            };
            return app;
        }

        // Una sola ventanita por carpeta, la abra el acceso, el clic derecho o el vigilante.
        public static string NombreMini(string carpeta)
        {
            return @"Local\" + instancia + "-mini-" + Huella(carpeta);
        }

        // Un vigilante por cada lista de carpetas (las pruebas usan otra con --datos).
        static string NombreVigilante()
        {
            return @"Local\Candado-vigilante-" + Huella(Datos.Dir.TrimEnd('\\'));
        }

        // Lo arranca si no está corriendo: lo necesitan las carpetas que piden la contraseña cada vez.
        public static void AsegurarVigilante()
        {
            if (Existe(NombreVigilante())) return;
            try { Process.Start(Cofre.RutaApp, "--vigilar --datos \"" + Datos.Dir.TrimEnd('\\') + "\""); }
            catch (Exception) { }
        }

        static bool Existe(string mutex)
        {
            Mutex m;
            if (!Mutex.TryOpenExisting(mutex, out m)) return false;
            m.Dispose();
            return true;
        }

        static string Huella(string carpeta)
        {
            using (SHA1 sha = SHA1.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(carpeta.ToLowerInvariant()))).Replace("-", "").Substring(0, 16);
        }

        // La otra copia de Candado le pasa la carpeta a esta, que ya está abierta.
        static bool Pasar(string tuberia, string carpeta)
        {
            try
            {
                AllowSetForegroundWindow(-1);   // que la ventana abierta pueda ponerse al frente
                using (NamedPipeClientStream c = new NamedPipeClientStream(".", tuberia, PipeDirection.Out))
                {
                    c.Connect(3000);
                    byte[] b = Encoding.UTF8.GetBytes(carpeta);
                    c.Write(b, 0, b.Length);
                }
                return true;
            }
            catch (Exception) { return false; }
        }

        static void Escuchar(string tuberia, Ventana v)
        {
            Thread hilo = new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        using (NamedPipeServerStream s = new NamedPipeServerStream(tuberia, PipeDirection.In, 1))
                        {
                            s.WaitForConnection();
                            using (StreamReader r = new StreamReader(s, Encoding.UTF8))
                            {
                                string carpeta = r.ReadToEnd().Trim();
                                if (carpeta != "") v.Window.Dispatcher.BeginInvoke(new Action(() => { v.Traer(); v.AbrirCarpeta(carpeta); }));
                            }
                        }
                    }
                    catch (Exception) { Thread.Sleep(500); }
                }
            });
            hilo.IsBackground = true;
            hilo.Start();
        }
    }
}
