// Punto de entrada. --carpeta <ruta> es lo que manda el clic derecho del Explorador: si
// Candado ya está abierta, se la pasa a esa ventana por una tubería con nombre y termina.
// Solo para probarla: --datos <carpeta> (en vez de %APPDATA%\Candado), --instancia <nombre>
// (otra ventana única, para probar el paso de carpetas) y --captura <png> --pantalla <nombre>
// (dibuja la ventana fuera de la pantalla, guarda la imagen y cierra).
using System;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;

namespace Candado
{
    static class Programa
    {
        [DllImport("user32.dll")]
        static extern bool AllowSetForegroundWindow(int proceso);

        [STAThread]
        static int Main(string[] args)
        {
            string captura = null, pantalla = "lista", carpeta = null, instancia = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--datos" && i + 1 < args.Length) Datos.Dir = args[++i];
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

            bool primera;
            using (Mutex unica = new Mutex(true, @"Local\" + instancia, out primera))
            {
                if (!primera)
                {
                    if (carpeta != null && Pasar(tuberia, carpeta)) return 0;
                    MessageBox.Show("Candado ya está abierto.", "Candado", MessageBoxButton.OK, MessageBoxImage.Information);
                    return 0;
                }
                Application app = new Application();
                app.DispatcherUnhandledException += (s, e) =>
                {
                    MessageBox.Show("Algo falló: " + e.Exception.Message, "Candado", MessageBoxButton.OK, MessageBoxImage.Error);
                    e.Handled = true;
                };
                Ventana v = new Ventana(captura, pantalla, carpeta);
                Escuchar(tuberia, v);
                return app.Run(v.Window);
            }
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
