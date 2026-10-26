// La ventanita que abre el acceso «Desbloquear con Candado» de una carpeta bloqueada (y el
// clic derecho, si la app no está abierta): solo la contraseña, el avance y un mensaje de
// listo o de error. Sobre una carpeta desbloqueada, la bloquea; si quedó a medias, la termina.
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Candado
{
    public class Mini
    {
        public readonly Window Window;
        readonly string carpeta, nombre;
        readonly bool enCaptura;
        string modo;                     // desbloquear | recuperar | bloquear | terminar | listo
        CancellationTokenSource cancelacion;
        bool trabajando;
        long ultimoAvance;

        const string IconoCandado = "", IconoAbierto = "", IconoAviso = "", IconoListo = "";

        public Mini(string carpeta, string captura, string pantalla)
        {
            this.carpeta = carpeta;
            nombre = Path.GetFileName(carpeta);
            Window = Tema.Cargar("Mini.xaml");
            E<TextBlock>("Ruta").Text = carpeta;
            E<Button>("Principal").Click += async delegate { await Aceptar(); };
            E<Button>("Secundario").Click += delegate { if (trabajando && cancelacion != null) Cancelar(); else Window.Close(); };
            E<Button>("Enlace").Click += delegate { Recuperar(); };
            Window.Closing += (s, e) =>
            {
                if (!trabajando || enCaptura) return;
                MessageBoxResult r = MessageBox.Show(Window, "Si cierras ahora, la carpeta queda a medias (después se puede terminar).", "Candado", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
                if (r != MessageBoxResult.OK) e.Cancel = true; else if (cancelacion != null) cancelacion.Cancel();
            };
            Window.ContentRendered += async delegate
            {
                await Empezar();
                if (captura != null) await Capturar(captura, pantalla);
            };
            if (captura != null)
            {
                enCaptura = true;
                Window.WindowStartupLocation = WindowStartupLocation.Manual;
                Window.Left = -20000; Window.Top = 0; Window.ShowInTaskbar = false; Window.ShowActivated = false;
            }
        }

        T E<T>(string n) where T : class { return (T)Window.FindName(n); }

        void Ver(string panel, bool visible) { E<FrameworkElement>(panel).Visibility = visible ? Visibility.Visible : Visibility.Collapsed; }

        void Mensaje(string texto, string color)
        {
            TextBlock m = E<TextBlock>("Mensaje");
            m.Text = texto;
            m.Foreground = (Brush)new BrushConverter().ConvertFromString(Tema.Colores[color]);
            m.Visibility = string.IsNullOrEmpty(texto) ? Visibility.Collapsed : Visibility.Visible;
        }

        void Cabeza(string titulo, string icono, string color)
        {
            E<TextBlock>("Titulo").Text = titulo;
            Window.Title = titulo;
            E<TextBlock>("Icono").Text = icono;
            E<TextBlock>("Icono").Foreground = (Brush)new BrushConverter().ConvertFromString(Tema.Colores[color]);
        }

        void Botones(string principal, string secundario, bool enlace)
        {
            Button p = E<Button>("Principal");
            p.Content = principal; p.Visibility = principal == null ? Visibility.Collapsed : Visibility.Visible; p.IsEnabled = true;
            E<Button>("Secundario").Content = secundario; E<Button>("Secundario").IsEnabled = true;
            Ver("Enlace", enlace);
        }

        void Enfocar(Control c)
        {
            if (!enCaptura) Window.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => c.Focus()));
        }

        // ------------------------------------------------------------ qué hacer según el estado

        async Task Empezar()
        {
            Cofre c;
            try { c = new Cofre(carpeta); }
            catch (Exception ex) { Final("No se pudo abrir la carpeta", ex.Message); return; }
            if (c.Estado == Estados.Bloqueada)
            {
                modo = "desbloquear";
                Cabeza("Desbloquear «" + nombre + "»", IconoCandado, "Acento");
                Ver("PanelClave", true);
                Botones("Desbloquear", "Cancelar", true);
                Enfocar(E<PasswordBox>("Clave"));
                return;
            }
            bool bloquear = c.Estado == Estados.Desbloqueada;
            modo = bloquear ? "bloquear" : "terminar";
            Cabeza((bloquear ? "Bloquear «" : "Terminar «") + nombre + "»", bloquear ? IconoAbierto : IconoAviso, bloquear ? "Aviso" : "Peligro");
            if (c.PuedeSinContrasena) { Ver("PanelClave", false); await Trabajar(null); return; }
            Ver("PanelClave", true);
            Mensaje(bloquear ? "Esta cuenta de Windows no tiene guardada su llave: pon la contraseña." : "Quedó a medias: pon la contraseña para terminar.", "Tenue");
            Botones(bloquear ? "Bloquear" : "Terminar", "Cancelar", !bloquear);
            Enfocar(E<PasswordBox>("Clave"));
        }

        async Task Aceptar()
        {
            if (modo == "listo") { Process.Start("explorer.exe", "\"" + carpeta + "\""); Window.Close(); return; }
            if (trabajando) return;
            Button p = E<Button>("Principal");
            p.IsEnabled = false;
            try
            {
                Cofre c = new Cofre(carpeta);
                byte[] llave;
                if (modo == "recuperar")
                {
                    string k = E<TextBox>("ClaveRecuperacion").Text, a = E<PasswordBox>("Nueva1").Password, b = E<PasswordBox>("Nueva2").Password;
                    Cofre.ValidarContrasena(a);
                    if (a != b) throw new ErrorCandado("Las dos contraseñas no coinciden.");
                    Mensaje("Comprobando la clave…", "Tenue");
                    llave = await Task.Run(() => c.LlaveConRecuperacion(k));
                    await Task.Run(() => c.PonerContrasena(llave, a));
                    if (c.Estado == Estados.Desbloqueada) { Listo("Contraseña cambiada", "«" + nombre + "» ya tiene tu contraseña nueva.", true); return; }
                }
                else
                {
                    string clave = E<PasswordBox>("Clave").Password;
                    if (clave == "") throw new ErrorCandado("Escribe la contraseña.");
                    Mensaje("Comprobando…", "Tenue");
                    llave = await Task.Run(() => c.LlaveConContrasena(clave));
                }
                await Trabajar(llave);
            }
            catch (ErrorCandado ex)
            {
                Mensaje(ex.Message, "Peligro");
                if (modo != "recuperar") { E<PasswordBox>("Clave").Clear(); Enfocar(E<PasswordBox>("Clave")); }
                p.IsEnabled = true;
            }
            catch (Exception ex) { Mensaje("Algo falló: " + ex.Message, "Peligro"); p.IsEnabled = true; }
        }

        void Recuperar()
        {
            modo = "recuperar";
            Cabeza("Olvidé la contraseña de «" + nombre + "»", IconoCandado, "Acento");
            Ver("PanelClave", false); Ver("PanelRecuperar", true);
            Mensaje("", "Tenue");
            Botones("Recuperar", "Cancelar", false);
            Enfocar(E<TextBox>("ClaveRecuperacion"));
        }

        // ------------------------------------------------------------ bloquear, desbloquear, terminar

        // llave null: con la cuenta de Windows (bloquear, o terminar si la guarda).
        async Task Trabajar(byte[] llave)
        {
            Cofre c = new Cofre(carpeta);
            string que = modo == "bloquear" ? "Bloqueando" : modo == "terminar" ? "Terminando" : "Desbloqueando";
            bool sobrescribir = Datos.Cargar().SobrescribirOriginales;
            Ver("PanelClave", false); Ver("PanelRecuperar", false); Ver("Enlace", false); Ver("PanelProgreso", true);
            Mensaje("", "Tenue");
            E<TextBlock>("Detalle").Text = que + "…";
            E<Button>("Principal").Visibility = Visibility.Collapsed;
            trabajando = true;
            cancelacion = new CancellationTokenSource();
            CancellationToken token = cancelacion.Token;
            Stopwatch reloj = Stopwatch.StartNew();
            ultimoAvance = -1000;
            Action<Progreso> avance = p =>
            {
                long ahora = reloj.ElapsedMilliseconds;
                if (ahora - Interlocked.Read(ref ultimoAvance) < 80 && p.Hechos < p.Total) return;
                Interlocked.Exchange(ref ultimoAvance, ahora);
                double f = p.TotalBytes > 0 ? p.Bytes / (double)p.TotalBytes : p.Total > 0 ? p.Hechos / (double)p.Total : 0;
                string d = p.Accion + (p.Total > 1 ? "  ·  " + p.Hechos + " de " + p.Total + " archivos" : "") + (string.IsNullOrEmpty(p.Actual) ? "" : "  ·  " + p.Actual);
                Window.Dispatcher.BeginInvoke(new Action(() => { E<ProgressBar>("Barra").Value = 1000 * Math.Min(1, f); E<TextBlock>("Detalle").Text = d; }));
            };
            string modoAntes = modo;
            try
            {
                await Task.Run(() =>
                {
                    if (modoAntes == "bloquear") { if (llave == null) c.Bloquear(avance, token, sobrescribir); else c.Bloquear(llave, avance, token, sobrescribir); }
                    else if (modoAntes == "terminar") { if (llave == null) c.Reanudar((string)null, avance, token, sobrescribir); else c.ReanudarConLlave(llave, avance, token, sobrescribir); }
                    else c.Desbloquear(llave, avance, token);
                });
                trabajando = false;
                if (new Cofre(carpeta).Estado == Estados.Desbloqueada)
                    Listo("«" + nombre + "» desbloqueada", "Listo: tus archivos ya están a la vista. Para bloquearla otra vez: clic derecho sobre la carpeta → Candado.", true);
                else
                    Listo("«" + nombre + "» bloqueada", "Listo. Para volver a entrar, abre «Desbloquear con Candado» dentro de la carpeta.", false);
            }
            catch (OperationCanceledException)
            {
                trabajando = false;
                Final("Se canceló", new Cofre(carpeta).Interrumpida ? "Quedó a medias: vuelve a abrir «Desbloquear con Candado» para terminar." : "La carpeta quedó como estaba.");
            }
            catch (Exception ex)
            {
                trabajando = false;
                Final("No se pudo terminar", ex is ErrorCandado ? ex.Message : "Algo falló: " + ex.Message);
            }
        }

        void Cancelar()
        {
            cancelacion.Cancel();
            E<Button>("Secundario").IsEnabled = false;
            E<TextBlock>("Detalle").Text = "Cancelando…";
        }

        void Listo(string titulo, string texto, bool abierta)
        {
            modo = "listo";
            Ver("PanelProgreso", false); Ver("PanelClave", false); Ver("PanelRecuperar", false);
            Cabeza(titulo, IconoListo, "Ok");
            Mensaje(texto, "Ok");
            Botones(abierta ? "Abrir la carpeta" : null, "Cerrar", false);
        }

        void Final(string titulo, string texto)
        {
            modo = "final";
            Ver("PanelProgreso", false); Ver("PanelClave", false); Ver("PanelRecuperar", false);
            Cabeza(titulo, IconoAviso, "Peligro");
            Mensaje(texto, "Peligro");
            Botones(null, "Cerrar", false);
        }

        // ------------------------------------------------------------ capturas de prueba

        // mini: como abre; mini-error y mini-listo escriben una contraseña mala o la buena;
        // mini-recuperar abre «Olvidé mi contraseña». Sobre una carpeta desbloqueada, mini la bloquea.
        async Task Capturar(string ruta, string pantalla)
        {
            if (pantalla == "mini-error" || pantalla == "mini-listo")
            {
                E<PasswordBox>("Clave").Password = pantalla == "mini-error" ? "no-es-esta" : "contraseña-de-prueba-1";
                await Aceptar();
            }
            else if (pantalla == "mini-recuperar") Recuperar();
            while (trabajando) await Task.Delay(100);
            await Task.Delay(300);
            await Window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            FrameworkElement raiz = E<FrameworkElement>("Raiz");
            raiz.UpdateLayout();
            RenderTargetBitmap bmp = new RenderTargetBitmap((int)raiz.ActualWidth, (int)raiz.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(raiz);
            PngBitmapEncoder png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(bmp));
            using (FileStream f = File.Create(ruta)) png.Save(f);
            Window.Close();
        }
    }
}
