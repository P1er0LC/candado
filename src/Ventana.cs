// La ventana de Candado. El XAML va embebido (Ventana.xaml) y se carga al abrir: el csc de
// .NET Framework no compila XAML. Toda la lógica de las carpetas está en Cofre; aquí solo
// se muestran y se lanzan sus operaciones en segundo plano.
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Candado
{
    public class Fila : INotifyPropertyChanged
    {
        string detalle, estado, fondoEstado, colorEstado, icono, colorIcono, textoPrincipal;
        public event PropertyChangedEventHandler PropertyChanged;
        void Avisar(string p) { PropertyChangedEventHandler h = PropertyChanged; if (h != null) h(this, new PropertyChangedEventArgs(p)); }

        public string Ruta { get; set; }
        public string Nombre { get; set; }
        public string Tipo { get; set; }   // bloqueada | desbloqueada | interrumpida | perdida | sinproteccion
        public bool SoloAcceso { get; set; }
        public bool CadaVez { get; set; }
        public string Detalle { get { return detalle; } set { detalle = value; Avisar("Detalle"); } }
        public string Estado { get { return estado; } set { estado = value; Avisar("Estado"); } }
        public string FondoEstado { get { return fondoEstado; } set { fondoEstado = value; Avisar("FondoEstado"); } }
        public string ColorEstado { get { return colorEstado; } set { colorEstado = value; Avisar("ColorEstado"); } }
        public string Icono { get { return icono; } set { icono = value; Avisar("Icono"); } }
        public string ColorIcono { get { return colorIcono; } set { colorIcono = value; Avisar("ColorIcono"); } }
        public string TextoPrincipal { get { return textoPrincipal; } set { textoPrincipal = value; Avisar("TextoPrincipal"); } }
    }

    public class Ventana
    {
        public readonly Window Window;
        readonly Dictionary<string, string> tema = Tema.Colores;
        readonly Dictionary<string, string[]> chips = Tema.Chips;
        readonly ObservableCollection<Fila> filas = new ObservableCollection<Fila>();
        Config cfg;
        CancellationTokenSource cancelacion;
        bool ocupado;
        Func<Task> alAceptar;
        Action alCancelar, alEnlace;
        long ultimoAvance;
        string nombreEnCurso;
        DispatcherTimer ocultarProgreso;
        readonly string carpetaInicial;
        bool recibioCarpeta, enCaptura;

        const string IconoCandado = "\uE72E", IconoAbierto = "\uE785", IconoAviso = "\uE7BA", IconoCarpeta = "\uE8B7";
        const string TextoCadaVez = "Pedir la contrase\u00F1a cada vez que la abro";

        // ------------------------------------------------------------ armado

        public Ventana(string captura, string pantalla, string carpetaInicial)
        {
            this.carpetaInicial = carpetaInicial;
            Window = Tema.Cargar("Ventana.xaml");

            E<ItemsControl>("Lista").ItemsSource = filas;
            E<ItemsControl>("Lista").AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(ClicEnFila));
            E<Button>("BtnProteger").Click += delegate { Proteger(); };
            E<Button>("BtnCorreo").Click += delegate { AbrirCorreo(); };
            E<Button>("BtnAvisoCorreo").Click += delegate { AbrirCorreo(); };
            E<Button>("BtnCancelar").Click += delegate { Cancelar(); };
            E<Button>("CapaPrincipal").Click += async delegate { await AceptarCapa(); };
            E<Button>("CapaSecundario").Click += delegate { if (alCancelar != null) alCancelar(); else CerrarCapa(); };
            E<Button>("CapaEnlace").Click += delegate { if (alEnlace != null) alEnlace(); };
            Window.PreviewKeyDown += TeclaPulsada;
            Window.Closing += Cerrando;

            cfg = Datos.Cargar();
            Refrescar();
            if (captura == null && filas.Any(f => f.CadaVez)) Programa.AsegurarVigilante();

            if (captura == null && carpetaInicial != null)
                Window.ContentRendered += delegate { AbrirCarpeta(carpetaInicial); };
            if (captura != null)
            {
                // Fuera de la pantalla y sin activarse: si no, se queda con el teclado de quien
                // esté usando la PC mientras corren las pruebas.
                enCaptura = true;
                Window.WindowStartupLocation = WindowStartupLocation.Manual;
                Window.Left = -20000; Window.Top = 0; Window.ShowInTaskbar = false; Window.ShowActivated = false;
                Window.ContentRendered += async delegate
                {
                    try { await Pantalla(pantalla); }
                    catch (Exception ex) { Aviso("La prueba falló", ex.Message); }
                    await Task.Delay(400);   // que terminen de medirse los tamaños
                    await Window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    Capturar(captura);
                    Window.Close();
                };
            }
        }

        T E<T>(string nombre) where T : class { return (T)Window.FindName(nombre); }

        Brush Pincel(string hex) { return (Brush)new BrushConverter().ConvertFromString(hex); }

        static string Tamano(long b)
        {
            if (b < 0) return "?";
            if (b >= 1L << 30) return string.Format("{0:N1} GB", b / (double)(1L << 30));
            if (b >= 1L << 20) return string.Format("{0:N0} MB", b / (double)(1L << 20));
            if (b > 0) return string.Format("{0:N0} KB", Math.Max(1, b / 1024.0));
            return "vacía";
        }

        // ------------------------------------------------------------ lista

        void Refrescar()
        {
            filas.Clear();
            foreach (string ruta in cfg.Carpetas) filas.Add(NuevaFila(ruta));
            E<Border>("Vacio").Visibility = filas.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            E<Border>("AvisoCorreo").Visibility = cfg.CorreoListo ? Visibility.Collapsed : Visibility.Visible;
            foreach (Fila f in filas.ToList()) MedirTamano(f);
        }

        Fila NuevaFila(string ruta)
        {
            Fila f = new Fila { Ruta = ruta, Nombre = Path.GetFileName(ruta), Detalle = ruta };
            try
            {
                if (Cofre.EstaProtegida(ruta)) { Cofre c = new Cofre(ruta); f.SoloAcceso = c.SoloAcceso; f.CadaVez = c.CadaVez; }
            }
            catch (Exception) { }
            PonerTipo(f, TipoDe(ruta));
            return f;
        }

        static string TipoDe(string ruta)
        {
            if (!Directory.Exists(ruta)) return "perdida";
            if (!Cofre.EstaProtegida(ruta)) return "sinproteccion";
            try
            {
                Cofre c = new Cofre(ruta);
                if (c.Interrumpida) return "interrumpida";
                return c.Estado == Estados.Bloqueada ? "bloqueada" : "desbloqueada";
            }
            catch (Exception) { return "perdida"; }
        }

        void PonerTipo(Fila f, string tipo)
        {
            f.Tipo = tipo;
            string[] c;
            switch (tipo)
            {
                case "bloqueada": f.Estado = "Bloqueada"; c = chips["acento"]; f.Icono = IconoCandado; f.ColorIcono = tema["Acento"]; f.TextoPrincipal = "Desbloquear"; break;
                case "desbloqueada": f.Estado = "Desbloqueada"; c = chips["aviso"]; f.Icono = IconoAbierto; f.ColorIcono = tema["Aviso"]; f.TextoPrincipal = "Bloquear"; break;
                case "interrumpida": f.Estado = "A medias"; c = chips["peligro"]; f.Icono = IconoAviso; f.ColorIcono = tema["Peligro"]; f.TextoPrincipal = "Terminar"; break;
                case "sinproteccion": f.Estado = "Sin protección"; c = chips["neutro"]; f.Icono = IconoCarpeta; f.ColorIcono = tema["Tenue"]; f.TextoPrincipal = "Quitar"; break;
                default: f.Estado = "No la encuentro"; c = chips["neutro"]; f.Icono = IconoCarpeta; f.ColorIcono = tema["Tenue"]; f.TextoPrincipal = "Quitar"; break;
            }
            f.FondoEstado = c[0]; f.ColorEstado = c[1];
        }

        void MedirTamano(Fila f)
        {
            if (f.Tipo == "perdida" || f.Tipo == "sinproteccion") return;
            string ruta = f.Ruta;
            Task.Run(() => { try { return new Cofre(ruta).Tamano(); } catch { return -1L; } })
                .ContinueWith(t => Window.Dispatcher.BeginInvoke(new Action(() =>
                {
                    f.Detalle = ruta + "  ·  " + Tamano(t.Result) + (f.SoloAcceso ? "  ·  solo acceso" : "") + (f.CadaVez ? "  ·  pide contraseña cada vez" : "");
                })));
        }

        void ActualizarFila(Fila f)
        {
            PonerTipo(f, TipoDe(f.Ruta));
            MedirTamano(f);
        }

        Fila FilaDe(string ruta)
        {
            return filas.FirstOrDefault(x => string.Equals(x.Ruta, ruta, StringComparison.OrdinalIgnoreCase));
        }

        void ClicEnFila(object sender, RoutedEventArgs e)
        {
            Button b = e.OriginalSource as Button;
            if (b == null || ocupado) return;
            Fila f = b.Tag as Fila;
            if (f == null) return;
            if (b.Uid == "principal") Principal(f);
            else if (b.Uid == "mas") Opciones(f);
        }

        void Principal(Fila f)
        {
            switch (f.Tipo)
            {
                case "bloqueada": PedirDesbloquear(f); break;
                case "desbloqueada": var t = Bloquear(f); break;
                case "interrumpida": Terminar(f); break;
                default: Datos.Quitar(cfg, f.Ruta); Refrescar(); break;
            }
        }

        // ------------------------------------------------------------ clic derecho del Explorador

        // Hace lo que corresponde con la carpeta: protegerla, desbloquearla, bloquearla o
        // terminar lo que quedó a medias. Si es una carpeta de adentro de una protegida, actúa
        // sobre la protegida.
        public void AbrirCarpeta(string ruta)
        {
            recibioCarpeta = true;
            string c;
            try { c = Cofre.Normalizar(ruta); }
            catch (Exception) { Aviso("No entendí qué carpeta es", "Llegó «" + ruta + "». Ábrela desde «Proteger una carpeta»."); return; }
            if (ocupado) { Aviso("Espera un momento", "Candado está terminando con «" + nombreEnCurso + "»."); return; }
            string protegida = Cofre.ProtegidaDe(c, cfg.Carpetas);
            if (protegida != null)
            {
                c = protegida;
                if (FilaDe(c) == null) { Datos.Agregar(cfg, c); Refrescar(); }
                Fila f = FilaDe(c);
                if (f != null) Principal(f);
                return;
            }
            try { Cofre.Validar(c, cfg.Carpetas); }
            catch (ErrorCandado ex) { Aviso("No se puede proteger esa carpeta", ex.Message); return; }
            DialogoProteger(c);
        }

        public void Traer()
        {
            if (enCaptura) return;
            if (Window.WindowState == WindowState.Minimized) Window.WindowState = WindowState.Normal;
            Window.Activate();
            Window.Topmost = true;
            Window.Topmost = false;
            Window.Focus();
        }

        // ------------------------------------------------------------ capa de diálogo

        void Capa(string titulo, string texto, string principal, Func<Task> aceptar, string secundario = "Cancelar", string estilo = "Boton")
        {
            E<TextBlock>("CapaTitulo").Text = titulo;
            E<TextBlock>("CapaTexto").Text = texto ?? "";
            E<TextBlock>("CapaTexto").Visibility = string.IsNullOrEmpty(texto) ? Visibility.Collapsed : Visibility.Visible;
            E<StackPanel>("CapaCampos").Children.Clear();
            E<TextBlock>("CapaMensaje").Visibility = Visibility.Collapsed;
            Button p = E<Button>("CapaPrincipal"), s = E<Button>("CapaSecundario"), l = E<Button>("CapaEnlace");
            p.Content = principal; p.Style = (Style)Window.FindResource(estilo);
            p.Visibility = principal == null ? Visibility.Collapsed : Visibility.Visible;
            s.Content = secundario; s.Visibility = secundario == null ? Visibility.Collapsed : Visibility.Visible;
            l.Visibility = Visibility.Collapsed;
            alAceptar = aceptar; alCancelar = null; alEnlace = null;
            E<Grid>("Capa").Visibility = Visibility.Visible;
            if (!enCaptura) Window.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                Control primero = E<StackPanel>("CapaCampos").Children.OfType<Control>().FirstOrDefault(x => (x is PasswordBox || x is TextBox) && !(x is TextBox && ((TextBox)x).IsReadOnly));
                if (primero != null) primero.Focus();
            }));
        }

        void Enlace(string texto, Action accion)
        {
            Button l = E<Button>("CapaEnlace");
            l.Content = texto; l.Visibility = Visibility.Visible;
            alEnlace = accion;
        }

        void Etiqueta(string texto)
        {
            if (texto == null) return;
            E<StackPanel>("CapaCampos").Children.Add(new TextBlock { Text = texto, Style = (Style)Window.FindResource("Etiqueta"), TextWrapping = TextWrapping.Wrap });
        }

        PasswordBox CampoClave(string etiqueta)
        {
            Etiqueta(etiqueta);
            PasswordBox p = new PasswordBox();
            E<StackPanel>("CapaCampos").Children.Add(p);
            return p;
        }

        TextBox CampoTexto(string etiqueta, string valor)
        {
            Etiqueta(etiqueta);
            TextBox t = new TextBox { Text = valor ?? "" };
            E<StackPanel>("CapaCampos").Children.Add(t);
            return t;
        }

        CheckBox CampoCasilla(string texto, bool valor)
        {
            CheckBox c = new CheckBox { IsChecked = valor, Margin = new Thickness(0, 14, 0, 0), Content = new TextBlock { Text = texto, TextWrapping = TextWrapping.Wrap } };
            E<StackPanel>("CapaCampos").Children.Add(c);
            return c;
        }

        RadioButton CampoOpcion(string titulo, string detalle, bool marcada)
        {
            StackPanel contenido = new StackPanel();
            contenido.Children.Add(new TextBlock { Text = titulo, FontWeight = FontWeights.SemiBold });
            contenido.Children.Add(new TextBlock { Text = detalle, Style = (Style)Window.FindResource("Tenue"), FontSize = 12, Margin = new Thickness(0, 3, 0, 0) });
            RadioButton r = new RadioButton { Content = contenido, IsChecked = marcada, GroupName = "modo", Style = (Style)Window.FindResource("Opcion") };
            E<StackPanel>("CapaCampos").Children.Add(r);
            return r;
        }

        Button CampoBoton(string texto, string estilo, Action accion)
        {
            Button b = new Button { Content = texto, Style = (Style)Window.FindResource(estilo), HorizontalContentAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
            b.Click += delegate { accion(); };
            E<StackPanel>("CapaCampos").Children.Add(b);
            return b;
        }

        void Mensaje(string texto, bool error)
        {
            TextBlock m = E<TextBlock>("CapaMensaje");
            m.Text = texto;
            m.Foreground = Pincel(error ? tema["Peligro"] : tema["Tenue"]);
            m.Visibility = Visibility.Visible;
        }

        void CerrarCapa()
        {
            E<Grid>("Capa").Visibility = Visibility.Collapsed;
            alAceptar = null; alCancelar = null; alEnlace = null;
        }

        void Aviso(string titulo, string texto)
        {
            Capa(titulo, texto, "Entendido", null, null);
        }

        async Task AceptarCapa()
        {
            if (alAceptar == null) { CerrarCapa(); return; }
            Button p = E<Button>("CapaPrincipal"), s = E<Button>("CapaSecundario"), l = E<Button>("CapaEnlace");
            p.IsEnabled = s.IsEnabled = l.IsEnabled = false;
            try { await alAceptar(); }
            catch (ErrorCandado ex) { Mensaje(ex.Message, true); }
            catch (Exception ex) { Mensaje("Algo falló: " + ex.Message, true); }
            finally { p.IsEnabled = s.IsEnabled = l.IsEnabled = true; }
        }

        void TeclaPulsada(object sender, KeyEventArgs e)
        {
            if (E<Grid>("Capa").Visibility != Visibility.Visible) return;
            if (e.Key == Key.Escape && E<Button>("CapaSecundario").Visibility == Visibility.Visible) { e.Handled = true; if (alCancelar != null) alCancelar(); else CerrarCapa(); }
            else if (e.Key == Key.Enter && E<Button>("CapaPrincipal").IsEnabled && E<Button>("CapaPrincipal").Visibility == Visibility.Visible) { e.Handled = true; var t = AceptarCapa(); }
        }

        static void ValidarNueva(string a, string b)
        {
            Cofre.ValidarContrasena(a);
            if (a != b) throw new ErrorCandado("Las dos contraseñas no coinciden.");
        }

        // ------------------------------------------------------------ operaciones largas

        async Task<bool> Operar(Fila f, string accion, Action<Action<Progreso>, CancellationToken> trabajo, string listo)
        {
            ocupado = true;
            nombreEnCurso = f.Nombre;
            E<Button>("BtnProteger").IsEnabled = false;
            if (ocultarProgreso != null) ocultarProgreso.Stop();
            Border panel = E<Border>("PanelProgreso");
            panel.Visibility = Visibility.Visible;
            Button cancelar = E<Button>("BtnCancelar");
            cancelar.Visibility = Visibility.Visible; cancelar.IsEnabled = true;
            E<TextBlock>("ProgresoTexto").Text = accion + " «" + f.Nombre + "»";
            E<TextBlock>("ProgresoDetalle").Text = "";
            E<ProgressBar>("Barra").Value = 0;
            cancelacion = new CancellationTokenSource();
            CancellationToken token = cancelacion.Token;
            Stopwatch reloj = Stopwatch.StartNew();
            ultimoAvance = -1000;
            Action<Progreso> avance = p =>
            {
                long ahora = reloj.ElapsedMilliseconds;
                if (ahora - Interlocked.Read(ref ultimoAvance) < 80 && p.Hechos < p.Total) return;
                Interlocked.Exchange(ref ultimoAvance, ahora);
                Progreso copia = new Progreso { Accion = p.Accion, Hechos = p.Hechos, Total = p.Total, Bytes = p.Bytes, TotalBytes = p.TotalBytes, Actual = p.Actual };
                Window.Dispatcher.BeginInvoke(new Action(() => MostrarProgreso(copia)));
            };
            bool bien = false;
            try
            {
                await Task.Run(() => trabajo(avance, token));
                bien = true;
                Terminado(listo != null ? "Listo: «" + f.Nombre + "» " + listo + "." : null);
            }
            catch (OperationCanceledException)
            {
                string tipo = TipoDe(f.Ruta);
                Terminado(tipo == "interrumpida" ? "Se canceló a medias: «Terminar» la completa cuando quieras." : "Cancelado: la carpeta quedó como estaba.");
            }
            catch (Exception ex)
            {
                Terminado(null);
                Aviso("No se pudo terminar", ex is ErrorCandado ? ex.Message : "Algo falló: " + ex.Message);
            }
            finally
            {
                ocupado = false;
                E<Button>("BtnProteger").IsEnabled = true;
                ActualizarFila(f);
            }
            return bien;
        }

        void MostrarProgreso(Progreso p)
        {
            double fraccion = p.TotalBytes > 0 ? p.Bytes / (double)p.TotalBytes : p.Total > 0 ? p.Hechos / (double)p.Total : 0;
            E<ProgressBar>("Barra").Value = 1000 * Math.Min(1, fraccion);
            E<TextBlock>("ProgresoTexto").Text = p.Accion + " «" + nombreEnCurso + "»";
            string detalle = p.Hechos + " de " + p.Total + " archivos";
            if (p.TotalBytes > 0) detalle += "  ·  " + Tamano(p.Bytes) + " de " + Tamano(p.TotalBytes);
            if (!string.IsNullOrEmpty(p.Actual)) detalle += "  ·  " + p.Actual;
            E<TextBlock>("ProgresoDetalle").Text = detalle;
        }

        void Terminado(string mensaje)
        {
            Border panel = E<Border>("PanelProgreso");
            if (mensaje == null) { panel.Visibility = Visibility.Collapsed; return; }
            E<Button>("BtnCancelar").Visibility = Visibility.Collapsed;
            E<TextBlock>("ProgresoTexto").Text = mensaje;
            E<TextBlock>("ProgresoDetalle").Text = "";
            E<ProgressBar>("Barra").Value = 1000;
            ocultarProgreso = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            ocultarProgreso.Tick += delegate { ocultarProgreso.Stop(); if (!ocupado) panel.Visibility = Visibility.Collapsed; };
            ocultarProgreso.Start();
        }

        void Cancelar()
        {
            if (cancelacion == null) return;
            cancelacion.Cancel();
            E<Button>("BtnCancelar").IsEnabled = false;
            E<TextBlock>("ProgresoTexto").Text = "Cancelando…";
        }

        void Cerrando(object sender, CancelEventArgs e)
        {
            if (!ocupado) return;
            MessageBoxResult r = MessageBox.Show(Window, "Hay una carpeta bloqueándose o desbloqueándose. Si cierras ahora, queda a medias (después se puede terminar).", "Candado", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (r != MessageBoxResult.OK) e.Cancel = true;
            else if (cancelacion != null) cancelacion.Cancel();
        }

        // ------------------------------------------------------------ acciones

        void Proteger()
        {
            string ruta = ElegirCarpeta();
            if (ruta == null) return;
            try { Cofre.Validar(ruta, cfg.Carpetas); }
            catch (ErrorCandado ex) { Aviso("No se puede proteger esa carpeta", ex.Message); return; }
            DialogoProteger(Cofre.Normalizar(ruta));
        }

        void DialogoProteger(string ruta)
        {
            string correo = cfg.CorreoListo
                ? "Si la olvidas, la recuperas con la clave que te llega a " + cfg.Destinatario + "."
                : "Todavía no configuraste el correo: la clave de recuperación solo la vas a ver en pantalla.";
            Capa("Proteger «" + Path.GetFileName(ruta) + "»", ruta + "\n\nSe queda donde está. " + correo, "Proteger", null);
            Etiqueta("Cómo protegerla");
            RadioButton cifrar = CampoOpcion("Cifrar los archivos",
                "Sin la contraseña nadie puede verlos, ni llevándose el disco. Bloquear y desbloquear tarda según cuánto pese.", true);
            RadioButton acceso = CampoOpcion("Solo bloquear el acceso",
                "Al instante y sin tocar los archivos, pero alguien con permisos de administrador o que sepa cambiar permisos puede entrar igual.", false);
            CheckBox cadaVez = CampoCasilla(TextoCadaVez, true);
            PasswordBox p1 = CampoClave("Contraseña (al menos " + Cofre.LargoMinimo + " caracteres)");
            PasswordBox p2 = CampoClave("Repítela");
            alAceptar = async () =>
            {
                ValidarNueva(p1.Password, p2.Password);
                string clave1 = p1.Password;
                bool soloAcceso = acceso.IsChecked == true, siempre = cadaVez.IsChecked == true;
                Mensaje("Preparando…", false);
                List<string> otras = cfg.Carpetas.ToList();
                string clave = await Task.Run(() => Cofre.Crear(ruta, clave1, otras, soloAcceso, siempre));
                Datos.Agregar(cfg, ruta);
                Refrescar();
                if (siempre && !enCaptura) Programa.AsegurarVigilante();
                string envio = null;
                if (cfg.CorreoListo)
                {
                    Mensaje("Mandándote la clave por correo…", false);
                    Config c = cfg;
                    try { await Task.Run(() => Correo.EnviarClave(c, ruta, clave)); envio = "ok"; }
                    catch (Exception ex) { envio = ex.Message; }
                }
                MostrarClave(FilaDe(ruta), clave, envio);
            };
        }

        void MostrarClave(Fila f, string clave, string envio)
        {
            string guardala = "Si olvidas la contraseña, es la única forma de abrir la carpeta.";
            string texto = envio == "ok" ? "Te la mandé a " + cfg.Destinatario + ". Guarda ese correo. " + guardala
                : envio == null ? "No configuraste el correo, así que solo la ves aquí: anótala o guárdala ahora. " + guardala
                : "No se pudo mandar por correo (" + envio.TrimEnd('.') + "). Anótala o guárdala ahora. " + guardala;
            // La que pide la contraseña cada vez se bloquea sola en cuanto no está abierta.
            Capa("Clave de recuperación de «" + f.Nombre + "»", texto, "Bloquear ahora", null, f.CadaVez ? "Cerrar" : "Más tarde");
            TextBox t = CampoTexto(null, clave);
            t.IsReadOnly = true; t.FontFamily = new FontFamily("Cascadia Mono, Consolas"); t.FontSize = 15;
            t.TextWrapping = TextWrapping.Wrap; t.TextAlignment = TextAlignment.Center;
            t.Margin = new Thickness(0, 16, 0, 0); t.Padding = new Thickness(8, 10, 8, 10);
            Enlace("Copiar la clave", () =>
            {
                try { Clipboard.SetText(clave); Mensaje("Copiada.", false); }
                catch (Exception) { Mensaje("No se pudo copiar: selecciónala y copia con Ctrl+C.", true); }
            });
            alAceptar = async () => { CerrarCapa(); await Bloquear(f); };
        }

        async Task Bloquear(Fila f)
        {
            Cofre c = new Cofre(f.Ruta);
            bool sobrescribir = cfg.SobrescribirOriginales;
            if (c.PuedeSinContrasena)
            {
                await Operar(f, "Bloqueando", (av, ct) => c.Bloquear(av, ct, sobrescribir), "quedó bloqueada");
                return;
            }
            Capa("Bloquear «" + f.Nombre + "»", "Esta cuenta de Windows no tiene guardada su llave: pon la contraseña.", "Bloquear", null);
            PasswordBox p = CampoClave("Contraseña");
            alAceptar = async () =>
            {
                string clave = p.Password;
                Mensaje("Comprobando…", false);
                byte[] llave = await Task.Run(() => c.LlaveConContrasena(clave));
                CerrarCapa();
                await Operar(f, "Bloqueando", (av, ct) => c.Bloquear(llave, av, ct, sobrescribir), "quedó bloqueada");
            };
        }

        void PedirDesbloquear(Fila f)
        {
            Capa("Desbloquear «" + f.Nombre + "»", f.Ruta, "Desbloquear", null);
            PasswordBox p = CampoClave("Contraseña");
            Enlace("Olvidé mi contraseña", () => Recuperar(f));
            alAceptar = async () =>
            {
                Cofre c = new Cofre(f.Ruta);
                string clave = p.Password;
                Mensaje("Comprobando…", false);
                byte[] llave = await Task.Run(() => c.LlaveConContrasena(clave));
                CerrarCapa();
                if (await Operar(f, "Desbloqueando", (av, ct) => c.Desbloquear(llave, av, ct), "quedó desbloqueada")) AbrirSiCadaVez(c);
            };
        }

        // Si pide la contraseña cada vez, al desbloquearla se abre: cerrada, el vigilante la
        // vuelve a bloquear en unos segundos.
        void AbrirSiCadaVez(Cofre c)
        {
            if (!c.CadaVez || enCaptura) return;
            Process.Start("explorer.exe", "\"" + c.Carpeta + "\"");
            Programa.AsegurarVigilante();
        }

        void Terminar(Fila f)
        {
            Cofre c = new Cofre(f.Ruta);
            bool sobrescribir = cfg.SobrescribirOriginales;
            string que = c.Estado == Estados.Bloqueando ? "Terminando de bloquear" : "Terminando de desbloquear";
            string listo = c.Estado == Estados.Bloqueando ? "quedó bloqueada" : "quedó desbloqueada";
            if (c.PuedeSinContrasena)
            {
                var t = Operar(f, que, (av, ct) => c.Reanudar((string)null, av, ct, sobrescribir), listo);
                return;
            }
            Capa("Terminar «" + f.Nombre + "»", "Quedó a medias. Pon la contraseña para terminar.", "Terminar", null);
            PasswordBox p = CampoClave("Contraseña");
            Enlace("Olvidé mi contraseña", () => Recuperar(f));
            alAceptar = async () =>
            {
                string clave = p.Password;
                Mensaje("Comprobando…", false);
                byte[] llave = await Task.Run(() => c.LlaveConContrasena(clave));
                CerrarCapa();
                await Operar(f, que, (av, ct) => c.ReanudarConLlave(llave, av, ct, sobrescribir), listo);
            };
        }

        void Recuperar(Fila f)
        {
            Capa("Olvidé la contraseña de «" + f.Nombre + "»",
                "Pega la clave de recuperación del correo «Clave de recuperación de «" + f.Nombre + "»» y elige una contraseña nueva.",
                "Recuperar", null);
            TextBox clave = CampoTexto("Clave de recuperación", "");
            clave.FontFamily = new FontFamily("Cascadia Mono, Consolas");
            PasswordBox p1 = CampoClave("Contraseña nueva (al menos " + Cofre.LargoMinimo + " caracteres)");
            PasswordBox p2 = CampoClave("Repítela");
            alAceptar = async () =>
            {
                ValidarNueva(p1.Password, p2.Password);
                Cofre c = new Cofre(f.Ruta);
                string k = clave.Text, nueva = p1.Password;
                Mensaje("Comprobando la clave…", false);
                byte[] llave = await Task.Run(() => c.LlaveConRecuperacion(k));
                await Task.Run(() => c.PonerContrasena(llave, nueva));
                CerrarCapa();
                if (c.Estado == Estados.Desbloqueada) Aviso("Listo", "«" + f.Nombre + "» ya tiene tu contraseña nueva.");
                else if (await Operar(f, "Desbloqueando", (av, ct) => c.Desbloquear(llave, av, ct), "quedó desbloqueada con tu contraseña nueva")) AbrirSiCadaVez(c);
            };
        }

        void CambiarContrasena(Fila f)
        {
            Capa("Cambiar la contraseña de «" + f.Nombre + "»", "La clave de recuperación sigue siendo la misma.", "Cambiar", null);
            PasswordBox actual = CampoClave("Contraseña actual");
            PasswordBox p1 = CampoClave("Contraseña nueva (al menos " + Cofre.LargoMinimo + " caracteres)");
            PasswordBox p2 = CampoClave("Repítela");
            Enlace("Olvidé mi contraseña", () => Recuperar(f));
            alAceptar = async () =>
            {
                ValidarNueva(p1.Password, p2.Password);
                Cofre c = new Cofre(f.Ruta);
                string a = actual.Password, nueva = p1.Password;
                Mensaje("Comprobando…", false);
                await Task.Run(() => c.CambiarContrasena(a, nueva));
                Aviso("Listo", "Cambió la contraseña de «" + f.Nombre + "».");
            };
        }

        void Opciones(Fila f)
        {
            string modo = f.Tipo == "perdida" || f.Tipo == "sinproteccion" ? ""
                : f.SoloAcceso ? "\nSolo bloquea el acceso: sus archivos no están cifrados." : "\nSus archivos se cifran al bloquearla.";
            Capa(f.Nombre, f.Ruta + modo, null, null, "Cerrar");
            if (Directory.Exists(f.Ruta))
                CampoBoton("Abrir la carpeta", "BotonSecundario", () => { CerrarCapa(); Process.Start("explorer.exe", "\"" + f.Ruta + "\""); });
            if (f.Tipo == "bloqueada" || f.Tipo == "desbloqueada" || f.Tipo == "interrumpida")
            {
                CheckBox cadaVez = CampoCasilla(TextoCadaVez, f.CadaVez);
                cadaVez.Margin = new Thickness(0, 14, 0, 6);
                cadaVez.Click += delegate
                {
                    bool v = cadaVez.IsChecked == true;
                    try
                    {
                        new Cofre(f.Ruta).PonerCadaVez(v);
                        f.CadaVez = v;
                        MedirTamano(f);
                        if (v && !enCaptura) Programa.AsegurarVigilante();
                    }
                    catch (Exception ex) { cadaVez.IsChecked = !v; Mensaje(ex is ErrorCandado ? ex.Message : "Algo falló: " + ex.Message, true); }
                };
                CampoBoton("Cambiar la contraseña", "BotonSecundario", () => CambiarContrasena(f));
                CampoBoton("Olvidé la contraseña", "BotonSecundario", () => Recuperar(f));
                CampoBoton("Quitar la protección", "BotonSecundario", () => QuitarProteccion(f)).Foreground = Pincel(tema["Peligro"]);
            }
            else CampoBoton("Quitar de la lista", "BotonSecundario", () => { CerrarCapa(); Datos.Quitar(cfg, f.Ruta); Refrescar(); });
        }

        void QuitarProteccion(Fila f)
        {
            Cofre c = new Cofre(f.Ruta);
            if (c.Estado == Estados.Desbloqueada)
            {
                Capa("Quitar la protección de «" + f.Nombre + "»",
                    "Sus archivos quedan como están ahora, sin contraseña, y la clave de recuperación deja de servir.",
                    "Quitar la protección", null, "Cancelar", "BotonPeligro");
                alAceptar = async () =>
                {
                    await Task.Run(() => c.QuitarProteccion());
                    Datos.Quitar(cfg, f.Ruta);
                    Refrescar();
                    Aviso("Listo", "«" + f.Nombre + "» ya no tiene contraseña.");
                };
                return;
            }
            Capa("Quitar la protección de «" + f.Nombre + "»",
                "Primero hay que desbloquearla: sus archivos quedan como antes, sin contraseña, y la clave de recuperación deja de servir.",
                "Desbloquear y quitar", null, "Cancelar", "BotonPeligro");
            PasswordBox p = CampoClave("Contraseña");
            Enlace("Olvidé mi contraseña", () => Recuperar(f));
            alAceptar = async () =>
            {
                string clave = p.Password;
                Mensaje("Comprobando…", false);
                byte[] llave = await Task.Run(() => c.LlaveConContrasena(clave));
                CerrarCapa();
                if (await Operar(f, "Desbloqueando", (av, ct) => c.Desbloquear(llave, av, ct), null))
                {
                    c.QuitarProteccion();
                    Datos.Quitar(cfg, f.Ruta);
                    Refrescar();
                    Aviso("Listo", "«" + f.Nombre + "» ya no tiene contraseña.");
                }
            };
        }

        void AbrirCorreo()
        {
            Capa("Correo de recuperación",
                "Por aquí te llega la clave de recuperación de cada carpeta que protejas. Con Gmail: smtp.gmail.com, puerto 587 y una contraseña de aplicación (myaccount.google.com/apppasswords), no tu contraseña normal.",
                "Guardar", null);
            TextBox servidor = CampoTexto("Servidor SMTP", cfg.Servidor);
            TextBox puerto = CampoTexto("Puerto", cfg.Puerto.ToString());
            TextBox usuario = CampoTexto("Tu correo", cfg.Usuario);
            PasswordBox clave = CampoClave(string.IsNullOrEmpty(cfg.ContrasenaCerrada) ? "Contraseña de aplicación" : "Contraseña de aplicación (déjala vacía para no cambiarla)");
            TextBox para = CampoTexto("Mandar las claves a otro correo (opcional)", cfg.Para);
            CheckBox sobrescribir = CampoCasilla("Al bloquear, sobrescribir los originales antes de borrarlos. Es más lento; en un disco mecánico impide recuperarlos.", cfg.SobrescribirOriginales);
            Func<Config> leer = () =>
            {
                int p;
                if (!int.TryParse(puerto.Text.Trim(), out p) || p <= 0 || p > 65535) throw new ErrorCandado("El puerto tiene que ser un número (587 en Gmail).");
                if (p == 465) throw new ErrorCandado("Usa el puerto 587: Candado se conecta con STARTTLS, y el 465 no lo admite.");
                if (usuario.Text.Trim() != "" && !usuario.Text.Contains("@")) throw new ErrorCandado("Revisa tu correo: le falta la @.");
                return new Config
                {
                    Servidor = servidor.Text.Trim(), Puerto = p, Cifrado = true, Usuario = usuario.Text.Trim(), Para = para.Text.Trim(),
                    ContrasenaCerrada = clave.Password != "" ? Cripto.CerrarConWindows(Encoding.UTF8.GetBytes(clave.Password)) : cfg.ContrasenaCerrada,
                    SobrescribirOriginales = sobrescribir.IsChecked == true, Carpetas = cfg.Carpetas
                };
            };
            Enlace("Enviar correo de prueba", async () =>
            {
                Button l = E<Button>("CapaEnlace");
                try
                {
                    Config c = leer();
                    if (!c.CorreoListo) throw new ErrorCandado("Completa el servidor, tu correo y la contraseña de aplicación.");
                    l.IsEnabled = false;
                    Mensaje("Enviando…", false);
                    await Task.Run(() => Correo.EnviarPrueba(c));
                    Mensaje("Listo: revisa la bandeja de " + c.Destinatario + ".", false);
                }
                catch (Exception ex) { Mensaje(ex.Message, true); }
                finally { l.IsEnabled = true; }
            });
            alAceptar = () =>
            {
                cfg = leer();
                Datos.Guardar(cfg);
                CerrarCapa();
                Refrescar();
                return Task.FromResult(0);
            };
        }

        // ------------------------------------------------------------ elegir carpeta (diálogo moderno de Windows)

        [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
        class DialogoAbrir { }

        [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IFileOpenDialog
        {
            [PreserveSig] int Show(IntPtr padre);
            void SetFileTypes(uint n, IntPtr filtros);
            void SetFileTypeIndex(uint i);
            void GetFileTypeIndex(out uint i);
            void Advise(IntPtr eventos, out uint cookie);
            void Unadvise(uint cookie);
            void SetOptions(uint opciones);
            void GetOptions(out uint opciones);
            void SetDefaultFolder(IShellItem item);
            void SetFolder(IShellItem item);
            void GetFolder(out IShellItem item);
            void GetCurrentSelection(out IShellItem item);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string nombre);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string nombre);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string titulo);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string texto);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string texto);
            void GetResult(out IShellItem item);
        }

        [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItem
        {
            void BindToHandler(IntPtr ctx, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            void GetParent(out IShellItem item);
            void GetDisplayName(uint sigdn, [MarshalAs(UnmanagedType.LPWStr)] out string nombre);
        }

        string ElegirCarpeta()
        {
            IFileOpenDialog d = (IFileOpenDialog)new DialogoAbrir();
            d.SetOptions(0x20 | 0x40 | 0x8);   // elegir carpetas, solo del disco, no cambiar el directorio actual
            d.SetTitle("Elige la carpeta a proteger");
            d.SetOkButtonLabel("Elegir esta carpeta");
            if (d.Show(new WindowInteropHelper(Window).Handle) != 0) return null;
            IShellItem item;
            d.GetResult(out item);
            string ruta;
            item.GetDisplayName(0x80058000, out ruta);   // SIGDN_FILESYSPATH
            return ruta;
        }

        // ------------------------------------------------------------ capturas de prueba

        // Pantallas para revisar el diseño y «flujo-…», que hacen la operación de verdad
        // escribiendo en los campos y pulsando los botones como lo haría una persona.
        const string ClaveDemo = "contraseña-de-prueba-1";

        async Task Pantalla(string nombre)
        {
            Fila primera = filas.FirstOrDefault(x => x.Tipo == "bloqueada") ?? filas.FirstOrDefault();
            Fila abierta = filas.FirstOrDefault(x => x.Tipo == "desbloqueada");
            Fila media = filas.FirstOrDefault(x => x.Tipo == "interrumpida");
            switch (nombre)
            {
                case "flujo-desbloquear":
                    PedirDesbloquear(primera);
                    Campos<PasswordBox>()[0].Password = ClaveDemo;
                    await AceptarCapa(); await Esperar();
                    return;
                case "flujo-error":
                    PedirDesbloquear(primera);
                    Campos<PasswordBox>()[0].Password = "no-es-esta";
                    await AceptarCapa();
                    return;
                case "flujo-bloquear": await Bloquear(abierta); await Esperar(); return;
                case "flujo-terminar": Terminar(media); await Esperar(); return;
                case "flujo-proteger":
                    cfg.ContrasenaCerrada = "";   // que no intente mandar el correo de verdad
                    DialogoProteger(Cofre.Normalizar(Path.Combine(Path.GetDirectoryName(primera.Ruta), "Para proteger")));
                    Campos<PasswordBox>()[0].Password = ClaveDemo; Campos<PasswordBox>()[1].Password = ClaveDemo;
                    await AceptarCapa();
                    await AceptarCapa();   // «Bloquear ahora» en el diálogo de la clave
                    await Esperar();
                    return;
                case "flujo-proteger-acceso":
                    cfg.ContrasenaCerrada = "";
                    DialogoProteger(Cofre.Normalizar(Path.Combine(Path.GetDirectoryName(primera.Ruta), "Para proteger (acceso)")));
                    Campos<RadioButton>()[1].IsChecked = true;
                    Campos<PasswordBox>()[0].Password = ClaveDemo; Campos<PasswordBox>()[1].Password = ClaveDemo;
                    await AceptarCapa();
                    await AceptarCapa();
                    await Esperar();
                    return;
                case "flujo-recuperar":
                    Recuperar(primera);
                    return;
                case "carpeta": AbrirCarpeta(carpetaInicial); return;
                case "esperar":
                    for (int i = 0; i < 200 && !recibioCarpeta; i++) await Task.Delay(100);
                    await Task.Delay(300);
                    return;
                case "proteger": DialogoProteger(@"D:\Clientes 2026"); break;
                case "clave": MostrarClave(primera ?? new Fila { Nombre = "Clientes 2026" }, Cripto.NuevaClaveRecuperacion(), cfg.CorreoListo ? "ok" : null); break;
                case "desbloquear": if (primera != null) PedirDesbloquear(primera); break;
                case "error": if (primera != null) { PedirDesbloquear(primera); Mensaje("La contraseña no es correcta.", true); } break;
                case "recuperar": if (primera != null) Recuperar(primera); break;
                case "opciones": if (primera != null) Opciones(primera); break;
                case "correo": AbrirCorreo(); break;
                case "progreso":
                    nombreEnCurso = primera != null ? primera.Nombre : "Clientes 2026";
                    E<Border>("PanelProgreso").Visibility = Visibility.Visible;
                    MostrarProgreso(new Progreso { Accion = "Bloqueando", Hechos = 37, Total = 120, Bytes = 1300L << 20, TotalBytes = 3400L << 20, Actual = @"Sesiones\2026\octubre\tatuaje-brazo.psd" });
                    break;
            }
        }

        List<T> Campos<T>() where T : class
        {
            return E<StackPanel>("CapaCampos").Children.OfType<T>().ToList();
        }

        async Task Esperar()
        {
            while (ocupado) await Task.Delay(100);
        }

        void Capturar(string ruta)
        {
            FrameworkElement raiz = (FrameworkElement)Window.Content;
            raiz.UpdateLayout();
            RenderTargetBitmap bmp = new RenderTargetBitmap((int)raiz.ActualWidth, (int)raiz.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(raiz);
            PngBitmapEncoder png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(bmp));
            using (FileStream f = File.Create(ruta)) png.Save(f);
        }
    }
}
