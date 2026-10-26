// Punto de entrada. Argumentos solo para probarla: --datos <carpeta> (en vez de
// %APPDATA%\Candado), --captura <png> --pantalla <nombre> (dibuja la ventana fuera de la
// pantalla, guarda la imagen y cierra).
using System;
using System.Net;
using System.Threading;
using System.Windows;

namespace Candado
{
    static class Programa
    {
        [STAThread]
        static int Main(string[] args)
        {
            string captura = null, pantalla = "lista";
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--datos" && i + 1 < args.Length) Datos.Dir = args[++i];
                else if (args[i] == "--captura" && i + 1 < args.Length) captura = args[++i];
                else if (args[i] == "--pantalla" && i + 1 < args.Length) pantalla = args[++i];
            }
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

            bool primera;
            using (Mutex unica = new Mutex(true, captura == null ? @"Local\Candado" : @"Local\Candado-" + Guid.NewGuid(), out primera))
            {
                if (!primera)
                {
                    MessageBox.Show("Candado ya está abierto.", "Candado", MessageBoxButton.OK, MessageBoxImage.Information);
                    return 0;
                }
                Application app = new Application();
                app.DispatcherUnhandledException += (s, e) =>
                {
                    MessageBox.Show("Algo falló: " + e.Exception.Message, "Candado", MessageBoxButton.OK, MessageBoxImage.Error);
                    e.Handled = true;
                };
                Ventana v = new Ventana(captura, pantalla);
                return app.Run(v.Window);
            }
        }
    }
}
