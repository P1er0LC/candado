// Colores claros u oscuros según el tema de Windows, y la carga de las ventanas: cada XAML
// embebido recibe los estilos compartidos (Estilos.xaml) y sus colores (@Fondo, @Acento…).
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Markup;
using Microsoft.Win32;

namespace Candado
{
    public static class Tema
    {
        public static readonly Dictionary<string, string> Colores;
        public static readonly Dictionary<string, string[]> Chips;   // fondo y texto de cada tono

        static Tema()
        {
            object valor = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 0);
            if (valor is int && (int)valor == 1)
            {
                Colores = new Dictionary<string, string> {
                    { "Fondo", "#F3F4F6" }, { "Panel", "#FFFFFF" }, { "Superficie", "#FFFFFF" }, { "Superficie2", "#E7EAEE" }, { "Borde", "#DADDE3" },
                    { "Texto", "#1F2328" }, { "Tenue", "#5F6773" }, { "Acento", "#5A48D8" }, { "TextoAcento", "#FFFFFF" }, { "Pista", "#E1E4E8" },
                    { "Aviso", "#9A6700" }, { "Peligro", "#CF222E" }, { "Ok", "#1A7F37" }, { "Velo", "#80000000" }, { "FondoAviso", "#FBEFCF" }, { "TextoAviso", "#7D5600" } };
                Chips = new Dictionary<string, string[]> {
                    { "acento", new[] { "#E6E2FB", "#4636B8" } }, { "aviso", new[] { "#FBEFCF", "#7D5600" } },
                    { "peligro", new[] { "#FBE0E0", "#B3261E" } }, { "neutro", new[] { "#E7EAEE", "#4B5563" } } };
            }
            else
            {
                Colores = new Dictionary<string, string> {
                    { "Fondo", "#1B1D21" }, { "Panel", "#202328" }, { "Superficie", "#2A2E34" }, { "Superficie2", "#353A42" }, { "Borde", "#3A3F47" },
                    { "Texto", "#E8EAED" }, { "Tenue", "#9AA1AC" }, { "Acento", "#7B6AF0" }, { "TextoAcento", "#FFFFFF" }, { "Pista", "#3A3F47" },
                    { "Aviso", "#D9A93A" }, { "Peligro", "#E5534B" }, { "Ok", "#3FB950" }, { "Velo", "#B3000000" }, { "FondoAviso", "#3D3320" }, { "TextoAviso", "#E8C26B" } };
                Chips = new Dictionary<string, string[]> {
                    { "acento", new[] { "#2C2750", "#B9AEFF" } }, { "aviso", new[] { "#3D3320", "#E8C26B" } },
                    { "peligro", new[] { "#45282A", "#F5A3A0" } }, { "neutro", new[] { "#353A42", "#B9C0CA" } } };
            }
        }

        public static Window Cargar(string recurso)
        {
            string estilos = Leer("Estilos.xaml");
            int desde = estilos.IndexOf('>', estilos.IndexOf("<ResourceDictionary")) + 1;
            estilos = estilos.Substring(desde, estilos.LastIndexOf("</ResourceDictionary>") - desde);
            string xaml = Leer(recurso).Replace("@@ESTILOS@@", estilos);
            foreach (string k in Colores.Keys.OrderByDescending(x => x.Length)) xaml = xaml.Replace("@" + k, Colores[k]);
            return (Window)XamlReader.Parse(xaml);
        }

        static string Leer(string recurso)
        {
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(recurso))
            using (StreamReader r = new StreamReader(s, Encoding.UTF8)) return r.ReadToEnd();
        }
    }
}
