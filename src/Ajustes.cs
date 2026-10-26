// Lo que guarda la app (no las carpetas): la lista de carpetas protegidas y los datos del
// correo de recuperación, en %APPDATA%\Candado. La contraseña del correo va cerrada con la
// cuenta de Windows.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Mail;
using System.Net.Mime;
using System.Runtime.Serialization;
using System.Text;

namespace Candado
{
    [DataContract]
    public class Config
    {
        [DataMember] public string Servidor = "smtp.gmail.com";
        [DataMember] public int Puerto = 587;
        [DataMember] public bool Cifrado = true;          // STARTTLS
        [DataMember] public string Usuario = "";
        [DataMember] public string ContrasenaCerrada = "";
        [DataMember] public string Para = "";
        [DataMember] public bool SobrescribirOriginales = false;
        [DataMember] public List<string> Carpetas = new List<string>();

        public bool CorreoListo
        {
            get { return !string.IsNullOrEmpty(Servidor) && !string.IsNullOrEmpty(Usuario) && !string.IsNullOrEmpty(ContrasenaCerrada); }
        }

        public string Destinatario { get { return string.IsNullOrEmpty(Para) ? Usuario : Para; } }
    }

    public static class Datos
    {
        // --datos <carpeta> lo cambia (para las pruebas).
        public static string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Candado");
        static string Ruta { get { return Path.Combine(Dir, "config.json"); } }

        public static Config Cargar()
        {
            Config c = null;
            if (File.Exists(Ruta))
            {
                try { c = Json.Leer<Config>(File.ReadAllBytes(Ruta)); } catch { c = null; }
            }
            if (c == null) c = new Config();
            if (c.Carpetas == null) c.Carpetas = new List<string>();
            return c;
        }

        public static void Guardar(Config c)
        {
            Directory.CreateDirectory(Dir);
            string tmp = Ruta + ".tmp";
            File.WriteAllBytes(tmp, Json.Escribir(c));
            if (File.Exists(Ruta)) File.Delete(Ruta);
            File.Move(tmp, Ruta);
        }

        public static void Agregar(Config c, string carpeta)
        {
            string n = Cofre.Normalizar(carpeta);
            if (!c.Carpetas.Any(x => string.Equals(x, n, StringComparison.OrdinalIgnoreCase))) c.Carpetas.Add(n);
            Guardar(c);
        }

        public static void Quitar(Config c, string carpeta)
        {
            string n = Cofre.Normalizar(carpeta);
            c.Carpetas.RemoveAll(x => string.Equals(x, n, StringComparison.OrdinalIgnoreCase));
            Guardar(c);
        }
    }

    public static class Correo
    {
        public static void Enviar(Config c, string asunto, string cuerpo)
        {
            if (!c.CorreoListo) throw new ErrorCandado("Falta configurar el correo de recuperación.");
            byte[] clave = Cripto.AbrirConWindows(c.ContrasenaCerrada);
            if (clave == null) throw new ErrorCandado("No se pudo leer la contraseña del correo: vuelve a ponerla en «Correo de recuperación».");
            using (SmtpClient cliente = new SmtpClient(c.Servidor, c.Puerto))
            using (MailMessage m = new MailMessage(new MailAddress(c.Usuario, "Candado"), new MailAddress(c.Destinatario)))
            {
                cliente.EnableSsl = c.Cifrado;
                cliente.UseDefaultCredentials = false;
                cliente.Credentials = new NetworkCredential(c.Usuario, Encoding.UTF8.GetString(clave));
                cliente.DeliveryMethod = SmtpDeliveryMethod.Network;
                cliente.Timeout = 30000;
                m.Subject = asunto;
                m.SubjectEncoding = Encoding.UTF8;
                m.Body = cuerpo;
                m.BodyEncoding = Encoding.UTF8;
                m.BodyTransferEncoding = TransferEncoding.QuotedPrintable;
                try { cliente.Send(m); }
                catch (SmtpException e)
                {
                    string detalle = e.InnerException != null ? e.InnerException.Message : e.Message;
                    if (e.StatusCode == SmtpStatusCode.MustIssueStartTlsFirst || detalle.Contains("5.7.0") || detalle.Contains("5.7.8") || detalle.Contains("535"))
                        throw new ErrorCandado("El servidor rechazó el usuario o la contraseña. En Gmail tiene que ser una contraseña de aplicación, no la normal.");
                    throw new ErrorCandado("No se pudo enviar el correo: " + detalle);
                }
            }
        }

        public static void EnviarClave(Config c, string carpeta, string clave)
        {
            string nombre = Path.GetFileName(carpeta);
            Enviar(c, "Clave de recuperación de «" + nombre + "»",
                "Protegiste con Candado la carpeta:\r\n" + carpeta + "\r\n\r\n" +
                "Tu clave de recuperación es:\r\n\r\n    " + clave + "\r\n\r\n" +
                "Si olvidas la contraseña: abre Candado, elige la carpeta, «Olvidé mi contraseña» y pega esta clave.\r\n\r\n" +
                "Guarda este correo y no lo reenvíes: quien tenga esta clave y acceso a tu PC puede abrir la carpeta.\r\n");
        }

        public static void EnviarPrueba(Config c)
        {
            Enviar(c, "Candado: correo de prueba",
                "Si te llegó este correo, Candado puede mandarte las claves de recuperación de tus carpetas.\r\n");
        }
    }
}
